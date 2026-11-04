using SteamKitSDK.ContentDelivery;
using SteamKitSDK.ContentDelivery.Models.Downloading;
using SteamKitSDK.Core;
using SteamKitSDK.Core.Structs;
using SdkSteamClient = SteamKitSDK.Core.SteamClient;

namespace SteamWorkshopManager;

internal sealed class SteamWorkshopContentClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    private const ulong MaxWorkshopDownloadBytes = 512UL * 1024 * 1024;
    private const int MaxWorkshopFiles = 4096;

    private readonly SteamSavedLogin _savedLogin;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private SdkSteamClient? _steam;
    private SteamContentClient? _content;
    private readonly object _lifetimeLock = new();
    private int _activeDownloads;
    private bool _cleanupStarted;
    private bool _disposed;

    public SteamWorkshopContentClient(SteamSavedLogin savedLogin)
    {
        if (string.IsNullOrWhiteSpace(savedLogin.AccountName) || string.IsNullOrWhiteSpace(savedLogin.RefreshToken))
            throw new ArgumentException("Steam 会话缺少账号名或 refresh token。", nameof(savedLogin));
        _savedLogin = savedLogin;
    }

    public async Task DownloadPublishedFileAsync(
        WorkshopItem item,
        string destinationDirectory,
        Action<double, string>? progress,
        CancellationToken cancellationToken)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeDownloads++;
        }
        try
        {
            await DownloadCoreAsync(item, destinationDirectory, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bool cleanup;
            lock (_lifetimeLock)
            {
                _activeDownloads--;
                cleanup = TakeCleanup();
            }
            if (cleanup) DisposeResources();
        }
    }

    private async Task DownloadCoreAsync(
        WorkshopItem item,
        string destinationDirectory,
        Action<double, string>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ulong.TryParse(item.PublishedFileId, out var publishedFileId) || publishedFileId == 0)
            throw new InvalidDataException("Steam 返回了无效的创意工坊项目 ID。");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;
        var content = await GetContentClientAsync(token).ConfigureAwait(false);
        var publishedFile = new PublishedFileId(publishedFileId);
        var details = await content.GetPublishedFileDetailsAsync(publishedFile, token).ConfigureAwait(false);
        if (details.consumer_appid != 977950)
            throw new InvalidDataException($"工坊项目属于 AppID {details.consumer_appid}，不是 ADOFAI。");
        var expectedSize = Convert.ToInt64(details.file_size);
        if (expectedSize > (long)MaxWorkshopDownloadBytes)
            throw new InvalidDataException("Steam 工坊项目超过 512 MiB 下载限制。");
        if (!string.IsNullOrWhiteSpace(details.file_url))
            ValidateLegacyDownloadUrl(details.file_url);

        await using IDownloadHandler handler = await content.GetPublishedFileDataAsync(
            publishedFile,
            cancellationToken: token).ConfigureAwait(false);

        await handler.SetupAsync(destinationDirectory, static _ => true, token).ConfigureAwait(false);
        if (handler.TotalFileCount > MaxWorkshopFiles)
            throw new InvalidDataException("Steam 工坊项目中的文件数量超过限制。");
        if (handler.TotalFileSize > MaxWorkshopDownloadBytes)
            throw new InvalidDataException("Steam 工坊项目超过 512 MiB 下载限制。");

        await handler.VerifyAsync(token).ConfigureAwait(false);
        handler.FileDownloaded += (_, file) =>
            progress?.Invoke(Math.Clamp(handler.TotalProgress * 0.7d, 0d, 0.7d), $"已下载 {file.FileName}");
        await handler.DownloadAsync(token).ConfigureAwait(false);
        var downloadedFiles = Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories).ToArray();
        if (downloadedFiles.Length == 0)
            throw new IOException("Steam 没有下载出工坊文件，请检查 Steam 会话和网络后重试。");
        var downloadedBytes = downloadedFiles.Sum(path => new FileInfo(path).Length);
        if (downloadedBytes > (long)MaxWorkshopDownloadBytes)
            throw new InvalidDataException("Steam 工坊项目超过 512 MiB 下载限制。");
        if (expectedSize > 0 && downloadedBytes != expectedSize)
            throw new InvalidDataException($"Steam 工坊文件不完整（预期 {expectedSize} 字节，实际 {downloadedBytes} 字节）。");
        progress?.Invoke(0.7d, $"Steam CDN 下载完成，共 {handler.TotalFileCount} 个文件");
    }

    public void Dispose()
    {
        bool cleanup;
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetimeCancellation.Cancel();
            cleanup = TakeCleanup();
        }
        if (cleanup) DisposeResources();
    }

    private bool TakeCleanup()
    {
        if (!_disposed || _activeDownloads != 0 || _cleanupStarted) return false;
        _cleanupStarted = true;
        return true;
    }

    private void DisposeResources()
    {
        try
        {
            _content?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { }
        try { _steam?.Shutdown(); }
        catch { }
        _steam?.Dispose();
        _connectGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task<SteamContentClient> GetContentClientAsync(CancellationToken cancellationToken)
    {
        if (_content != null)
            return _content;

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_content != null)
                return _content;

            var authenticator = new SavedTokenAuthenticator(_savedLogin.RefreshToken, _savedLogin.GuardData);
            var steam = new SdkSteamClient(
                new SteamCredentials(_savedLogin.AccountName, "refresh-token-session"),
                authenticator);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            timeout.CancelAfter(ConnectTimeout);

            try
            {
                await steam.ConnectAsync(timeout.Token).ConfigureAwait(false);
                _steam = steam;
                _content = new SteamContentClient(steam, maxConcurrentDownloadsPerTask: 2);
                return _content;
            }
            catch
            {
                steam.Shutdown();
                steam.Dispose();
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static void ValidateLegacyDownloadUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Steam 返回了无效的工坊下载地址。");

        var host = uri.IdnHost;
        if (!host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("akamaihd.net", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("steamusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("工坊文件地址不属于 Steam 内容分发网络。");
    }

    private sealed class SavedTokenAuthenticator(string accessToken, string? guardData) : SteamAuthenticator
    {
        private string _accessToken = accessToken;
        private string _guardData = guardData ?? string.Empty;

        public override Task<string> GetTwoFactorAuthenticationCodeAsync(
            bool previousCodeWasIncorrect,
            CancellationToken cancellationToken = default)
            => Task.FromException<string>(new InvalidOperationException("已保存的 Steam 会话需要重新验证，请退出后重新登录。"));

        public override Task<string> GetEmailAuthenticationCodeAsync(
            string accountEmail,
            bool previousCodeWasIncorrect,
            CancellationToken cancellationToken = default)
            => Task.FromException<string>(new InvalidOperationException("已保存的 Steam 会话需要重新验证，请退出后重新登录。"));

        public override Task<bool> NotifyMobileNotificationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public override Task PersistAccessTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            _accessToken = token;
            return Task.CompletedTask;
        }

        public override Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_accessToken);

        public override Task PersistGuardDataAsync(string data, CancellationToken cancellationToken = default)
        {
            _guardData = data;
            return Task.CompletedTask;
        }

        public override Task<string> GetGuardDataAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_guardData);
    }
}
