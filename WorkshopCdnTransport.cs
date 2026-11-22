using System.Net;
using SteamKit2;
using SteamKit2.CDN;
using SdkSteamClient = SteamKitSDK.Core.SteamClient;

namespace SteamWorkshopManager;

// One transport per content connection. Steam supplies every endpoint and authorization token.
internal sealed class WorkshopCdnTransport : IDisposable
{
    private const uint AppId = 977950;
    private readonly SdkSteamClient _steam;
    private readonly SteamContent _content;
    private readonly Client _cdn;
    private readonly SteamCdnHttpCompatibility _httpCompatibility;
    private readonly SemaphoreSlim _directoryGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _blockedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SteamContent.CDNAuthToken> _auth = new(StringComparer.OrdinalIgnoreCase);
    private bool _localLoaded;
    private bool _globalLoaded;
    private int _failures;
    private DateTimeOffset _loadedAt;
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(45);

    internal WorkshopCdnTransport(SdkSteamClient steam, SteamCdnHttpCompatibility httpCompatibility)
    {
        _steam = steam;
        _httpCompatibility = httpCompatibility;
        _content = steam.InternalClient.GetHandler<SteamContent>()
            ?? throw new InvalidOperationException("Steam 内容服务不可用。");
        _cdn = new Client(steam.InternalClient);
    }

    internal async Task<DepotManifest> DownloadManifestAsync(uint depotId, ulong manifestId, byte[] key,
        Action<string>? report, CancellationToken token)
    {
        var manifest = await DownloadNetworkRetry.RunAsync(async () =>
        {
            var code = await DownloadNetworkRetry.ObserveAsync(
                () => _content.GetManifestRequestCode(depotId, AppId, manifestId).WaitAsync(ControlTimeout, token),
                "manifest 授权请求", token).ConfigureAwait(false);
            return await TransferAsync(depotId, (server, auth) => _cdn.DownloadManifestAsync(
                depotId, manifestId, code, server, key, cdnAuthToken: auth), "manifest CDN 下载", report, token).ConfigureAwait(false);
        }, "manifest CDN 下载", report, token, attempts: 6).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (manifest.DepotID != depotId || manifest.ManifestGID != manifestId || manifest.FilenamesEncrypted)
            throw new InvalidDataException("Steam manifest 身份不匹配或文件名未正确解密。");
        return manifest;
    }

    internal Task<byte[]> DownloadChunkAsync(uint depotId, DepotManifest.ChunkData chunk, byte[] key,
        Action<string>? report, CancellationToken token)
        => DownloadNetworkRetry.RunAsync(() => TransferAsync(depotId, async (server, auth) =>
        {
            var bytes = new byte[checked((int)chunk.UncompressedLength)];
            var count = await _cdn.DownloadDepotChunkAsync(depotId, chunk, server, bytes, key, cdnAuthToken: auth).ConfigureAwait(false);
            if (count != bytes.Length) throw new InvalidDataException("Steam CDN 分块长度不匹配。");
            return bytes;
        }, "Steam CDN 分块传输", report, token), "Steam CDN 分块传输", report, token, attempts: 5);

    private async Task<T> TransferAsync<T>(uint depotId, Func<Server, string?, Task<T>> transfer,
        string phase, Action<string>? report, CancellationToken token)
    {
        Endpoint? endpoint = null;
        for (var selection = 0; selection < 2 && endpoint == null; selection++)
        {
            await EnsureDirectoryAsync(report, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                endpoint = _endpoints.Values.Where(candidate => IsAvailable(candidate, now))
                    .OrderBy(candidate => candidate.Active).ThenBy(candidate => candidate.Failures)
                    .ThenBy(candidate => candidate.Server.WeightedLoad).FirstOrDefault();
                if (endpoint != null) endpoint.Active++;
            }
            // Other transfers can quarantine the last candidate after directory lookup.
        }
        if (endpoint == null) throw new DownloadCdnUnavailableException();
        var nodePhase = $"{phase} · {endpoint.Server.Protocol}://{endpoint.Server.VHost}:{endpoint.Server.Port}";
        try
        {
            report?.Invoke($"{nodePhase}，正在连接…");
            token.ThrowIfCancellationRequested();
            var auth = CachedAuth(depotId, endpoint.Server);
            try
            {
                return await DownloadNetworkRetry.ObserveAsync(() => transfer(endpoint.Server, auth), nodePhase, token).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (!token.IsCancellationRequested
                && exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var authorization = await DownloadNetworkRetry.ObserveAsync(
                    () => _content.GetCDNAuthToken(AppId, depotId, endpoint.Server.VHost!).WaitAsync(ControlTimeout, token),
                    "CDN 节点授权", token).ConfigureAwait(false);
                if (authorization.Result != EResult.OK || string.IsNullOrEmpty(authorization.Token))
                    throw new UnauthorizedAccessException("Steam 未授予此内容节点的下载权限。");
                lock (_gate) _auth[AuthKey(depotId, endpoint.Server)] = authorization;
                token.ThrowIfCancellationRequested();
                return await DownloadNetworkRetry.ObserveAsync(() => transfer(endpoint.Server, authorization.Token), nodePhase, token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (DownloadNetworkRetry.IsTransient(exception, token))
        {
            lock (_gate)
            {
                endpoint.Failures++;
                _blockedHosts[endpoint.Server.VHost!] = DateTimeOffset.UtcNow.AddMinutes(10);
                _failures++;
            }
            var address = new UriBuilder(endpoint.Server.Protocol == Server.ConnectionProtocol.HTTPS ? "https" : "http",
                endpoint.Server.VHost!, endpoint.Server.Port).Uri;
            if (_httpCompatibility.WasCompatibilityAttempted(address))
                throw new DownloadTransportException($"{nodePhase} · 已尝试 TLS 1.2 / HTTP 1.1", exception);
            throw;
        }
        finally { lock (_gate) endpoint.Active--; }
    }

    // Called with _gate held. Quarantine the host across advertised protocols as well.
    private bool IsAvailable(Endpoint endpoint, DateTimeOffset now)
        => !_blockedHosts.TryGetValue(endpoint.Server.VHost!, out var blockedUntil) || blockedUntil <= now;

    private string? CachedAuth(uint depotId, Server server)
    {
        lock (_gate)
            return _auth.TryGetValue(AuthKey(depotId, server), out var token) && token.Expiration > DateTime.UtcNow.AddMinutes(1)
                ? token.Token : null;
    }

    private static string AuthKey(uint depotId, Server server) => $"{depotId}/{server.VHost}";
    private static string EndpointKey(Server server) => $"{server.Protocol}/{server.VHost}:{server.Port}";

    private async Task EnsureDirectoryAsync(Action<string>? report, CancellationToken token)
    {
        await _directoryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A directory without usable local endpoints immediately gets one global query.
            for (var pass = 0; pass < 2; pass++)
            {
                bool global;
                lock (_gate)
                {
                    var available = _endpoints.Values.Any(candidate => IsAvailable(candidate, DateTimeOffset.UtcNow));
                    if (_localLoaded && available && (_globalLoaded || _failures < 3)
                        && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromMinutes(10)) return;
                    global = _localLoaded;
                }
                report?.Invoke(global ? "正在通过 Steam CM 查询备用 CDN 节点…" : "正在通过 Steam CM 查询 CDN 节点…");
                var servers = await DownloadNetworkRetry.RunAsync(() => DownloadNetworkRetry.ObserveAsync(
                    () => _content.GetServersForSteamPipe(global ? 0u : _steam.ActiveCellId, 100).WaitAsync(ControlTimeout, token),
                    "Steam CM 内容节点列表", token), "Steam CM 内容节点列表", report, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    foreach (var server in servers)
                    {
                        if (server.Type is not ("CDN" or "SteamCache") || server.UseAsProxy || server.SteamChinaOnly
                            || server.AllowedAppIds.Length > 0 && !server.AllowedAppIds.Contains(AppId)
                            || string.IsNullOrWhiteSpace(server.VHost) || Uri.CheckHostName(server.VHost) != UriHostNameType.Dns
                            || server.Port is not (80 or 443)) continue;
                        // Keep quarantines and active counts when the same endpoint is returned again.
                        _endpoints.TryAdd(EndpointKey(server), new Endpoint(server));
                    }
                    _localLoaded = true;
                    if (global) _globalLoaded = true;
                    _loadedAt = DateTimeOffset.UtcNow;
                    if (_endpoints.Values.Any(candidate => IsAvailable(candidate, DateTimeOffset.UtcNow))) return;
                    if (global) throw new DownloadCdnUnavailableException();
                }
            }
            throw new DownloadCdnUnavailableException();
        }
        finally { _directoryGate.Release(); }
    }

    public void Dispose()
    {
        _cdn.Dispose();
        _directoryGate.Dispose();
    }

    private sealed class Endpoint(Server server)
    {
        internal Server Server { get; } = server;
        internal int Active { get; set; }
        internal int Failures { get; set; }
    }
}

internal sealed class DownloadCdnUnavailableException()
    : IOException("Steam 返回的可用 CDN 节点已全部失败或不可用，已保留断点；失败节点冷却后可重试。");
