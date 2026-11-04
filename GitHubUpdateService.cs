using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ImGuiNET;

namespace SteamWorkshopManager;

internal sealed class GitHubUpdateService : IDisposable
{
    internal const string Repository = "iidamie/ADOFAI_SteamWorkshop_Manager";
    internal const string RepositoryUrl = "https://github.com/" + Repository;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _currentVersion;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateState _state;
    private string _status = "尚未检查更新。";
    private ReleaseInfo? _release;
    private double _progress;
    private string _notification = string.Empty;
    private DateTimeOffset _notificationUntil;
    private bool _disposed;

    internal GitHubUpdateService(string modDirectory, string currentVersion)
    {
        _directory = Path.GetFullPath(modDirectory);
        _currentVersion = currentVersion;
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SteamWorkshopManager", currentVersion));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    internal void StartAutomaticCheck() => CheckNow();

    internal void CheckNow()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _state is UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToRestart) return;
            _state = UpdateState.Checking;
            _status = "正在检查 GitHub 正式版本…";
            _release = null;
            _progress = 0;
            token = _lifetime.Token;
        }
        _ = CheckAsync(token);
    }

    private async Task CheckAsync(CancellationToken lifetime)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var response = await _client.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=50",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new IOException("GitHub 暂时限制请求，请稍后重试。");
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await ModUpdatePackage.CopyLimitedAsync(input, buffer, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(buffer.ToArray());
            var release = ParseLatestRelease(document.RootElement);
            if (!ModUpdatePackage.TryVersion(_currentVersion, out var current)) throw new InvalidDataException("当前版本格式无效。");
            var available = release != null && ModUpdatePackage.TryVersion(release.Version, out var newest) && newest > current;
            lock (_gate)
            {
                if (_disposed) return;
                _release = available ? release : null;
                _state = available ? UpdateState.Available : UpdateState.UpToDate;
                _status = available ? $"发现新版本 {release!.Version}（当前 {_currentVersion}）。"
                    : release == null ? "仓库尚未发布可用的正式安装包。" : $"已是最新版本（{_currentVersion}）。";
                if (available) Notify($"Steam 工坊管理器 {release!.Version} 可用，请在设置中更新。", 5);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { Fail("检查更新超时，请重试。"); }
        catch (Exception exception) { Fail($"检查更新失败：{exception.GetBaseException().Message}"); }
    }

    private static ReleaseInfo? ParseLatestRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub 发布数据无效。");
        ReleaseInfo? latest = null;
        Version? latestVersion = null;
        foreach (var release in root.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
                || release.TryGetProperty("prerelease", out var preview) && preview.GetBoolean()) continue;
            var tag = release.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? string.Empty : string.Empty;
            if (!ModUpdatePackage.TryVersion(tag, out var version) || latestVersion != null && version <= latestVersion) continue;
            var textVersion = tag.Trim().TrimStart('v', 'V');
            var expectedAsset = $"SteamWorkshopManager-{textVersion}.zip";
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name) || name.GetString() != expectedAsset) continue;
                var url = asset.TryGetProperty("browser_download_url", out var address) ? address.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                    || !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal)) continue;
                var size = asset.TryGetProperty("size", out var length) ? length.GetInt64() : 0;
                if (size is <= 0 or > ModUpdatePackage.MaxPackageBytes) continue;
                var digest = asset.TryGetProperty("digest", out var hashValue) ? hashValue.GetString() : null;
                string? sha256 = null;
                if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                {
                    sha256 = digest[7..];
                    if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) continue;
                }
                var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
                latest = new ReleaseInfo(textVersion, uri, size, sha256, notes[..Math.Min(notes.Length, 12000)]);
                latestVersion = version;
                break;
            }
        }
        return latest;
    }

    internal void DownloadUpdate()
    {
        ReleaseInfo release;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _state != UpdateState.Available || _release == null) return;
            release = _release;
            _state = UpdateState.Downloading;
            _status = $"正在下载 {release.Version}…";
            _progress = 0;
            token = _lifetime.Token;
        }
        _ = DownloadAndInstallAsync(release, token);
    }

    private async Task DownloadAndInstallAsync(ReleaseInfo release, CancellationToken lifetime)
    {
        var id = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(_directory, ".update-" + id + ".zip");
        var staging = Path.Combine(_directory, ".update-" + id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            Directory.CreateDirectory(_directory);
            using var response = await _client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > ModUpdatePackage.MaxPackageBytes)
                throw new InvalidDataException("更新包超过 64 MiB 限制。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
                await ModUpdatePackage.CopyLimitedAsync(input, output, ModUpdatePackage.MaxPackageBytes, timeout.Token, count =>
                {
                    lock (_gate)
                    {
                        _progress = Math.Clamp(count / (double)release.Size, 0, 1);
                        _status = $"正在下载 {release.Version}：{count / 1048576d:F1} / {release.Size / 1048576d:F1} MiB";
                    }
                }).ConfigureAwait(false);
            if (new FileInfo(archivePath).Length != release.Size) throw new InvalidDataException("更新包下载不完整。");
            if (release.Sha256 != null)
            {
                await using var input = File.OpenRead(archivePath);
                var actual = await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("更新包校验失败。");
            }
            lock (_gate) _status = "正在校验并安装更新…";
            var files = await ModUpdatePackage.ExtractAsync(archivePath, staging, release.Version, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            // Finish a commit or its rollback once replacements begin, even if unload is requested.
            ModUpdatePackage.Install(staging, _directory, files);
            lock (_gate)
            {
                if (_disposed) return;
                _state = UpdateState.ReadyToRestart;
                _progress = 1;
                _status = $"已安装 {release.Version}，请重启游戏生效。";
                Notify(_status, 6);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { Fail("更新下载超时，请重新检查更新后重试。"); }
        catch (Exception exception) { Fail($"更新失败：{exception.Message}"); }
        finally
        {
            try { File.Delete(archivePath); } catch { }
            ModUpdatePackage.TryDeleteDirectory(staging);
        }
    }

    internal void DrawGui()
    {
        UpdateState state;
        string status;
        ReleaseInfo? release;
        double progress;
        lock (_gate) { state = _state; status = _status; release = _release; progress = _progress; }
        ImGui.Separator();
        ImGui.TextUnformatted("Mod 在线更新");
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted($"当前版本：{_currentVersion}\n{status}");
        ImGui.PopTextWrapPos();
        if (state == UpdateState.Downloading) ImGui.ProgressBar((float)progress, new Vector2(-1, 0));
        if (state is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToRestart))
        {
            var height = Math.Max(48, ImGui.GetFrameHeight());
            if (state == UpdateState.Available && ImGui.Button("下载并安装更新##workshop-update", new Vector2(-1, height))) DownloadUpdate();
            if (ImGui.Button("检查更新##workshop-check-update", new Vector2(-1, height))) CheckNow();
        }
        if (!string.IsNullOrWhiteSpace(release?.Notes) && ImGui.CollapsingHeader("版本说明##workshop-release-notes"))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(release.Notes);
            ImGui.PopTextWrapPos();
        }
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(RepositoryUrl);
        ImGui.PopTextWrapPos();
    }

    internal void DrawForegroundNotification()
    {
        string notification;
        lock (_gate)
        {
            if (_disposed || DateTimeOffset.UtcNow >= _notificationUntil || string.IsNullOrEmpty(_notification)) return;
            notification = _notification;
        }
        var display = ImGui.GetIO().DisplaySize;
        if (display.X < 200 || display.Y < 100) return;
        var margin = Math.Max(24, ImGui.GetFontSize());
        var width = Math.Max(1, Math.Min(display.X - margin * 2, ImGui.GetFontSize() * 30));
        ImGui.SetNextWindowPos(new Vector2((display.X - width) / 2, margin), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(.96f);
        var visible = ImGui.Begin("##SteamWorkshopManagerUpdateNotification",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize);
        try
        {
            if (visible)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(notification);
                ImGui.PopTextWrapPos();
            }
        }
        finally { ImGui.End(); }
    }

    private void Notify(string message, int seconds)
    {
        _notification = message;
        _notificationUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _state = UpdateState.Failed;
            _status = message;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
        _client.Dispose();
        _lifetime.Dispose();
    }

    private sealed record ReleaseInfo(string Version, Uri DownloadUrl, long Size, string? Sha256, string Notes);
    private enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, ReadyToRestart, Failed }
}
