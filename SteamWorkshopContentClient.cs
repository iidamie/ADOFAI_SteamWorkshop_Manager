using SteamKitSDK.ContentDelivery;
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
    private ContentConnection? _currentConnection;
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
        WorkshopDownloadWorkspace workspace,
        Action<double, string>? progress,
        CancellationToken cancellationToken, int parallelChunks)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeDownloads++;
        }
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            var token = linkedCancellation.Token;
            double lastProgress = 0;
            await DownloadNetworkRetry.RunAsync(() => DownloadCoreAsync(item, workspace,
                (value, message) => { Volatile.Write(ref lastProgress, value); progress?.Invoke(value, message); }, token, parallelChunks),
                "Steam 内容下载", message => progress?.Invoke(Volatile.Read(ref lastProgress), message), token).ConfigureAwait(false);
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
        WorkshopDownloadWorkspace workspace,
        Action<double, string>? progress,
        CancellationToken cancellationToken, int parallelChunks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ulong.TryParse(item.PublishedFileId, out var publishedFileId) || publishedFileId == 0)
            throw new InvalidDataException("Steam 返回了无效的创意工坊项目 ID。");

        var token = cancellationToken;
        await using var lease = await AcquireConnectionAsync(progress, token).ConfigureAwait(false);
        var content = lease.Connection.Content;
        var publishedFile = new PublishedFileId(publishedFileId);
        var details = await DownloadNetworkRetry.ObserveAsync(
            () => content.GetPublishedFileDetailsAsync(publishedFile, token), "内容详情查询", token).ConfigureAwait(false);
        if (details.consumer_appid != 977950)
            throw new InvalidDataException($"工坊项目属于 AppID {details.consumer_appid}，不是 ADOFAI。");
        var expectedSize = Convert.ToInt64(details.file_size);
        if (expectedSize > (long)MaxWorkshopDownloadBytes)
            throw new InvalidDataException("Steam 工坊项目超过 512 MiB 下载限制。");
        if (details.file_type != (uint)SteamKit2.EWorkshopFileType.Community)
            throw new InvalidDataException("工坊项目不是可下载的谱面文件。");
        if (details.time_updated != item.TimeUpdated || details.hcontent_file != item.ContentHandle || expectedSize != item.FileSize)
            throw new IOException("工坊项目内容已更新，请重试以使用最新版本，旧版数据不会拼接到新版。");

        if (!string.IsNullOrWhiteSpace(details.file_url))
        {
            await ResumableHttpDownload.DownloadAsync(details.file_url, details.filename, expectedSize, workspace, progress, token)
                .ConfigureAwait(false);
        }
        else
        {
            var prepared = await lease.Connection.ManifestLoader.LoadAsync(details.hcontent_file,
                message => progress?.Invoke(0, message), token).ConfigureAwait(false);
            await using var manifest = prepared.Handler;
            await manifest.SetupAsync(workspace.ContentDirectory, static _ => true, token).ConfigureAwait(false);
            if (manifest.TotalFileCount > MaxWorkshopFiles || manifest.TotalFileSize > MaxWorkshopDownloadBytes)
                throw new InvalidDataException("Steam 工坊项目超过文件数或大小限制。");
            await ResumableManifestDownload.DownloadAsync(lease.Connection.Cdn, manifest, prepared.Key, workspace, progress, token, parallelChunks).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        var pending = new Stack<string>();
        pending.Push(workspace.ContentDirectory);
        var count = 0;
        long downloadedBytes = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("下载结果包含符号链接。");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                downloadedBytes = checked(downloadedBytes + new FileInfo(path).Length);
                if (++count > MaxWorkshopFiles || downloadedBytes > (long)MaxWorkshopDownloadBytes)
                    throw new InvalidDataException("Steam 工坊项目超过文件数或大小限制。");
            }
        }
        if (count == 0) throw new IOException("Steam 没有下载出工坊文件，请检查 Steam 会话和网络后重试。");
        if (expectedSize > 0 && downloadedBytes != expectedSize)
            throw new InvalidDataException($"Steam 工坊文件不完整（预期 {expectedSize} 字节，实际 {downloadedBytes} 字节）。");
        progress?.Invoke(.7, $"Steam CDN 下载和校验完成，共 {count} 个文件。");
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
        // No download or connection lease remains when the owner reaches this point.
        _currentConnection?.Dispose();
        _currentConnection = null;
        _connectGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task<ContentLease> AcquireConnectionAsync(Action<double, string>? progress, CancellationToken token)
    {
        await _connectGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ContentConnection? unused = null;
            lock (_lifetimeLock)
            {
                if (_currentConnection != null && !_currentConnection.IsHealthy)
                {
                    var retired = _currentConnection;
                    _currentConnection = null;
                    retired.Retired = true;
                    // A different download can still be using the retired connection.
                    if (retired.Users == 0) unused = retired;
                }
            }
            unused?.Dispose();
            if (_currentConnection == null)
                _currentConnection = await DownloadNetworkRetry.RunAsync(() => CreateConnectionAsync(token),
                    "Steam 内容连接握手", message => progress?.Invoke(0, message), token).ConfigureAwait(false);
            lock (_lifetimeLock)
            {
                _currentConnection.Users++;
                return new ContentLease(this, _currentConnection);
            }
        }
        finally { _connectGate.Release(); }
    }

    private async Task<ContentConnection> CreateConnectionAsync(CancellationToken token)
    {
        var authenticator = new SavedTokenAuthenticator(_savedLogin.RefreshToken, _savedLogin.GuardData);
        var httpCompatibility = new SteamCdnHttpCompatibility();
        var steam = new SdkSteamClient(new SteamCredentials(_savedLogin.AccountName, "refresh-token-session"), authenticator,
            builder => builder.WithHttpClientFactory(httpCompatibility.CreateClient));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await DownloadNetworkRetry.ObserveAsync(async () =>
            {
                await steam.ConnectAsync(timeout.Token).ConfigureAwait(false);
                // SDK ConnectAsync can select a canceled readiness task; verify readiness explicitly.
                timeout.Token.ThrowIfCancellationRequested();
                if (!steam.IsConnected) throw new IOException("Steam 内容连接尚未就绪。");
                return true;
            }, "Steam 内容连接握手", token).ConfigureAwait(false);
            return new ContentConnection(steam, new SteamContentClient(steam, maxConcurrentDownloadsPerTask: 2), httpCompatibility);
        }
        catch (Exception exception) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            try { steam.Dispose(); } catch { }
            throw new TimeoutException("Steam 内容连接握手超时。", exception);
        }
        catch
        {
            try { steam.Dispose(); } catch { }
            throw;
        }
    }

    private ValueTask ReleaseConnectionAsync(ContentConnection connection)
    {
        bool dispose;
        lock (_lifetimeLock)
        {
            connection.Users--;
            dispose = connection.Retired && connection.Users == 0;
        }
        if (dispose) connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class ContentConnection : IDisposable
    {
        private readonly SdkSteamClient _steam;
        internal SteamContentClient Content { get; }
        internal WorkshopCdnTransport Cdn { get; }
        internal WorkshopManifestLoader ManifestLoader { get; }
        internal int Users { get; set; }
        internal bool Retired { get; set; }
        internal bool IsHealthy => !_steam.IsFaulted && _steam.IsConnected && _steam.InternalClient.IsConnected
            && !_steam.CancellationToken.IsCancellationRequested;

        internal ContentConnection(SdkSteamClient steam, SteamContentClient content, SteamCdnHttpCompatibility httpCompatibility)
        {
            _steam = steam;
            Content = content;
            Cdn = new WorkshopCdnTransport(steam, httpCompatibility);
            ManifestLoader = new WorkshopManifestLoader(steam, content, Cdn);
        }

        public void Dispose()
        {
            ManifestLoader.Dispose();
            Cdn.Dispose();
            try { Content.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _steam.Dispose(); } catch { }
        }
    }

    private sealed class ContentLease(SteamWorkshopContentClient owner, ContentConnection connection) : IAsyncDisposable
    {
        internal ContentConnection Connection { get; } = connection;
        public ValueTask DisposeAsync() => owner.ReleaseConnectionAsync(Connection);
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
