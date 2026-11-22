using SteamKit2;
using SteamKitSDK.ContentDelivery;
using SteamKitSDK.ContentDelivery.Models.Downloading;
using SdkSteamClient = SteamKitSDK.Core.SteamClient;
using SdkManifest = SteamKitSDK.ContentDelivery.Models.Manifest;

namespace SteamWorkshopManager;

internal sealed class WorkshopManifestLoader(SdkSteamClient steam, SteamContentClient content, WorkshopCdnTransport cdn) : IDisposable
{
    private const uint AppId = 977950;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private uint _depotId;
    private byte[]? _key;

    internal async Task<(DefaultDownloadHandler Handler, byte[] Key)> LoadAsync(ulong manifestId,
        Action<string>? report, CancellationToken token)
    {
        if (manifestId == 0) throw new InvalidDataException("Steam 返回了空的 manifest ID。");
        uint depotId;
        byte[] key;
        await _metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var apps = steam.InternalClient.GetHandler<SteamApps>()
                ?? throw new InvalidOperationException("Steam 应用服务不可用。");
            if (_depotId == 0)
            {
                report?.Invoke("正在读取 ADOFAI 工坊 depot 信息…");
                _depotId = await DownloadNetworkRetry.RunAsync(() => DownloadNetworkRetry.ObserveAsync(async () =>
                {
                    var access = await apps.PICSGetAccessTokens([AppId], []).ToTask().WaitAsync(Timeout, token).ConfigureAwait(false);
                    if (access.AppTokensDenied.Contains(AppId)) throw new UnauthorizedAccessException("Steam 未授予 ADOFAI 应用信息权限。");
                    var accessToken = access.AppTokens.TryGetValue(AppId, out var value) ? value : 0;
                    var response = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(AppId, accessToken), null)
                        .ToTask().WaitAsync(Timeout, token).ConfigureAwait(false);
                    var info = (response.Results ?? []).SelectMany(result => result.Apps.Values).FirstOrDefault(app => app.ID == AppId);
                    var id = info?.KeyValues["depots"]["workshopdepot"].AsUnsignedInteger() ?? 0;
                    if (id == 0) throw new InvalidDataException("Steam 未返回 ADOFAI 工坊 depot 信息。");
                    return id;
                }, "ADOFAI 工坊 depot 信息", token), "ADOFAI 工坊 depot 信息", report, token).ConfigureAwait(false);
            }
            if (_key == null)
            {
                report?.Invoke("正在请求 Steam 工坊内容授权…");
                _key = await DownloadNetworkRetry.RunAsync(() => DownloadNetworkRetry.ObserveAsync(async () =>
                {
                    var result = await apps.GetDepotDecryptionKey(_depotId, AppId).ToTask().WaitAsync(Timeout, token).ConfigureAwait(false);
                    if (result.Result == EResult.AccessDenied) throw new UnauthorizedAccessException("Steam 未授予此工坊内容的下载权限。");
                    if (result.Result is EResult.Timeout or EResult.ServiceUnavailable or EResult.Busy or EResult.NoConnection or EResult.TryAnotherCM)
                        throw new IOException($"Steam 内容授权服务暂时不可用（{result.Result}）。");
                    if (result.Result != EResult.OK || result.DepotKey.Length != 32)
                        throw new InvalidDataException($"Steam 工坊内容授权无效（{result.Result}）。");
                    return result.DepotKey;
                }, "Steam depot key 授权", token), "Steam depot key 授权", report, token).ConfigureAwait(false);
            }
            depotId = _depotId;
            key = _key;
        }
        finally { _metadataGate.Release(); }
        // Different projects may download their manifests concurrently after metadata is ready.
        var manifest = await cdn.DownloadManifestAsync(depotId, manifestId, key, report, token).ConfigureAwait(false);
        SdkManifest converted = manifest;
        return (new DefaultDownloadHandler(content, converted, AppId, depotId), key);
    }

    public void Dispose() => _metadataGate.Dispose();
}
