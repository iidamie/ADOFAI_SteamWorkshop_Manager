using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamWorkshopManager;

internal sealed class SteamKitConnection : IDisposable
{
    private const uint AppId = 977950;
    private const uint PageSize = 100;

    private readonly SteamClient _client = new();
    private readonly CallbackManager _callbacks;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamUser.LoggedOnCallback> _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pumpTask;
    private SteamWorkshopContentClient? _workshopContentClient;
    private bool _disposed;

    private SteamKitConnection()
    {
        _callbacks = new CallbackManager(_client);
        _subscriptions.Add(_callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult()));
        _subscriptions.Add(_callbacks.Subscribe<SteamClient.DisconnectedCallback>(callback =>
        {
            var reason = callback.UserInitiated ? "Steam 连接已断开。" : "Steam 连接中断。";
            _connected.TrySetException(new IOException(reason));
            _loggedOn.TrySetException(new IOException(reason));
        }));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback => _loggedOn.TrySetResult(callback)));
    }

    public ulong SteamId { get; private set; }
    public bool IsConnected => !_disposed && _client.IsConnected;
    public SteamSavedLogin SavedLogin { get; private set; } = new(string.Empty, 0, string.Empty, null);

    public static async Task<SteamKitConnection> LoginAsync(
        string username,
        string password,
        string? guardData,
        IAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var connection = new SteamKitConnection();
        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var authSession = await connection._client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                DeviceFriendlyName = "ADOFAI Steam Workshop Manager",
                IsPersistentSession = true,
                GuardData = guardData,
                Authenticator = authenticator,
            }).ConfigureAwait(false);

            var authResult = await authSession.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
            connection.SteamId = authSession.SteamID.ConvertToUInt64();
            connection.SavedLogin = new SteamSavedLogin(
                authResult.AccountName,
                connection.SteamId,
                authResult.RefreshToken,
                authResult.NewGuardData ?? guardData);

            if (string.IsNullOrWhiteSpace(authResult.RefreshToken))
                throw new InvalidOperationException("Steam 未返回可持久登录的 refresh token。");

            await connection.LogOnAsync(authResult.AccountName, authResult.RefreshToken, shouldRememberPassword: true, cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static async Task<SteamKitConnection> LoginWithRefreshTokenAsync(
        SteamSavedLogin savedLogin,
        CancellationToken cancellationToken)
    {
        var connection = new SteamKitConnection();
        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

            connection.SteamId = savedLogin.SteamId;
            connection.SavedLogin = savedLogin;
            await connection.LogOnAsync(savedLogin.AccountName, savedLogin.RefreshToken, shouldRememberPassword: true, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<WorkshopItem>> GetSubscribedItemsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected || SteamId == 0)
            throw new InvalidOperationException("Steam 尚未登录。");

        var messages = _client.GetHandler<SteamUnifiedMessages>()
            ?? throw new InvalidOperationException("SteamKit Unified Messages handler 不可用。");
        var publishedFiles = messages.CreateService<PublishedFile>();
        var items = new Dictionary<string, WorkshopItem>(StringComparer.Ordinal);
        uint? expectedTotal = null;

        for (uint page = 1; page <= 10000; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new CPublishedFile_GetUserFiles_Request
            {
                steamid = SteamId,
                appid = AppId,
                page = page,
                numperpage = PageSize,
                type = "mysubscriptions",
                sortmethod = "lastupdated",
            };

            var response = await publishedFiles.GetUserFiles(request).ToTask()
                .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (response.Result != EResult.OK)
                throw new InvalidOperationException($"读取 Steam 创意工坊订阅失败：{response.Result}。");

            var body = response.Body;
            expectedTotal ??= body.total;
            if (body.total != expectedTotal.Value)
                throw new InvalidOperationException("读取订阅期间列表发生变化，请稍后重试；本次不会清理本地谱面。");

            foreach (var details in body.publishedfiledetails)
            {
                var id = details.publishedfileid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(id) || id == "0")
                    continue;

                items[id] = ToWorkshopItem(details);
            }

            if (items.Count >= expectedTotal.Value)
                return items.Values.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToArray();

            if (body.publishedfiledetails.Count == 0)
                throw new InvalidOperationException("Steam 返回的订阅列表不完整；本次不会清理本地谱面。");
        }

        throw new InvalidOperationException("Steam 创意工坊订阅数量超过分页安全上限；本次不会清理本地谱面。");
    }

    public Task DownloadPublishedFileAsync(
        WorkshopItem item,
        string destinationDirectory,
        Action<double, string>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected || SteamId == 0)
            throw new InvalidOperationException("Steam 尚未登录。");

        _workshopContentClient ??= new SteamWorkshopContentClient(SavedLogin);
        return _workshopContentClient.DownloadPublishedFileAsync(item, destinationDirectory, progress, cancellationToken);
    }

    public async Task<WorkshopSearchPage> SearchWorkshopAsync(string query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var service = GetPublishedFileService();
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        var response = await service.QueryFiles(new CPublishedFile_QueryFiles_Request
        {
            appid = AppId,
            creator_appid = AppId,
            query_type = (uint)(string.IsNullOrWhiteSpace(query)
                ? SteamKit2.EPublishedFileQueryType.RankedByPublicationDate
                : SteamKit2.EPublishedFileQueryType.RankedByTextSearch),
            page = (uint)page,
            numperpage = (uint)pageSize,
            search_text = query.Trim(),
            filetype = (uint)EWorkshopFileType.Community,
            return_details = true,
            return_short_description = true,
            return_tags = true,
            return_kv_tags = true,
            return_previews = true,
            return_metadata = true,
            strip_description_bbcode = true,
        }).ToTask().WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        if (response.Result != EResult.OK)
            throw new IOException($"工坊搜索失败：{response.Result}。");
        var items = response.Body.publishedfiledetails
            .Where(details => details.result == (uint)EResult.OK && details.consumer_appid == AppId
                && details.file_type == (uint)EWorkshopFileType.Community)
            .Select(ToWorkshopItem).ToArray();
        return new WorkshopSearchPage(items, response.Body.total, page, pageSize);
    }

    public async Task<WorkshopItem> GetWorkshopItemAsync(string id, CancellationToken cancellationToken)
    {
        if (!WorkshopLinkParser.TryParse(id, out var publishedFileId))
            throw new ArgumentException("无效的创意工坊项目 ID。", nameof(id));
        var request = new CPublishedFile_GetDetails_Request
        {
            includemetadata = true, includetags = true, includekvtags = true,
            includeadditionalpreviews = true, includevotes = true,
            strip_description_bbcode = true,
        };
        request.publishedfileids.Add(publishedFileId);
        var response = await GetPublishedFileService().GetDetails(request).ToTask()
            .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        if (response.Result != EResult.OK)
            throw new IOException($"获取工坊项目失败：{response.Result}。");
        var details = response.Body.publishedfiledetails.FirstOrDefault();
        if (details == null || details.result != (uint)EResult.OK)
            throw new IOException($"无法访问工坊项目 {publishedFileId}：{(EResult)(details?.result ?? (uint)EResult.FileNotFound)}。");
        if (details.consumer_appid != AppId)
            throw new InvalidDataException($"该链接不是 ADOFAI 工坊项目（AppID {details.consumer_appid}）。");
        if (details.file_type != (uint)EWorkshopFileType.Community)
            throw new InvalidDataException("此项目不是可下载的谱面文件，暂不支持合集。");
        return ToWorkshopItem(details);
    }

    private PublishedFile GetPublishedFileService()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected || SteamId == 0)
            throw new InvalidOperationException("请先登录 Steam。");
        return (_client.GetHandler<SteamUnifiedMessages>()
            ?? throw new InvalidOperationException("Steam 工坊接口不可用。")).CreateService<PublishedFile>();
    }

    private static WorkshopItem ToWorkshopItem(PublishedFileDetails details)
        => new(details.publishedfileid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details.title ?? string.Empty, details.filename ?? string.Empty, details.file_url ?? string.Empty,
            checked((long)details.file_size), details.time_updated, details.hcontent_file,
            string.IsNullOrWhiteSpace(details.file_description) ? details.short_description ?? string.Empty : details.file_description)
        {
            Metadata = new WorkshopMetadata
            {
                CreatorSteamId = details.creator,
                TimeCreated = details.time_created,
                PreviewImages = new[] { details.preview_url }.Concat(details.previews
                    .Where(preview => preview.preview_type == 0).OrderBy(preview => preview.sortorder).Select(preview => preview.url))
                    .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal).Take(16).ToArray(),
                Tags = details.tags.Select(tag => string.IsNullOrWhiteSpace(tag.display_name) ? tag.tag : tag.display_name)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray(),
                Attributes = WorkshopMetadata.ReadAttributes(details.kvtags.Select(tag => new KeyValuePair<string, string>(tag.key, tag.value)), details.metadata),
                Subscriptions = details.subscriptions, Favorites = details.favorited, Views = details.views,
                VotesUp = details.vote_data?.votes_up, VotesDown = details.vote_data?.votes_down,
            },
        };

    public async Task<WorkshopItem> GetWorkshopPresentationAsync(string id, CancellationToken cancellationToken)
    {
        var item = await GetWorkshopItemAsync(id, cancellationToken).ConfigureAwait(false);
        var metadata = item.Metadata! with { FullDetails = true };
        if (metadata.CreatorSteamId != 0)
        {
            try
            {
                var request = new CPlayer_GetPlayerLinkDetails_Request();
                request.steamids.Add(metadata.CreatorSteamId);
                var player = _client.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
                var response = await player.GetPlayerLinkDetails(request).ToTask()
                    .WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
                if (response.Result == EResult.OK)
                {
                    var account = response.Body.accounts.FirstOrDefault(account => account.public_data?.steamid == metadata.CreatorSteamId);
                    metadata = metadata with { CreatorName = account?.public_data?.persona_name ?? string.Empty };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* A missing public profile must not hide the workshop details. */ }
        }
        return item with { Metadata = metadata };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pumpCancellation.Cancel();
        _workshopContentClient?.Dispose();
        try { _client.Disconnect(); }
        catch { }

        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        _pumpCancellation.Dispose();
    }

    private void PumpCallbacks()
    {
        try
        {
            while (!_pumpCancellation.IsCancellationRequested)
                _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
        }
        catch (Exception exception)
        {
            _connected.TrySetException(exception);
            _loggedOn.TrySetException(exception);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _pumpTask = Task.Run(PumpCallbacks, CancellationToken.None);
        _client.Connect();
        await _connected.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
    }

    private async Task LogOnAsync(string accountName, string clientToken, bool shouldRememberPassword, CancellationToken cancellationToken)
    {
        _client.GetHandler<SteamUser>()!.LogOn(new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = clientToken,
            ShouldRememberPassword = shouldRememberPassword,
        });

        var loginResult = await _loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        if (loginResult.Result != EResult.OK)
            throw new InvalidOperationException($"Steam 登录失败：{loginResult.Result} / {loginResult.ExtendedResult}。");
    }
}
