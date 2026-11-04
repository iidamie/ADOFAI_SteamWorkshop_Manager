using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using StArray.ModManager.Runtime;

[assembly: ModEntryPoint(typeof(SteamWorkshopManager.SteamWorkshopManagerPlugin))]

namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin : IModPlugin, IModSettings
{
    private static readonly TimeSpan AutoSyncInterval = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private CancellationTokenSource _unloadCancellation = new();
    private CancellationTokenSource? _sessionCancellation;
    private SteamKitConnection? _connection;
    private SteamAuthStore? _authStore;
    private SteamSavedLogin? _savedLogin;
    private string? _authStoreError;
    private Task? _autoSyncTask;
    private GitHubUpdateService? _updateService;
    private PluginSettings _settings = new();
    private List<WorkshopItem> _subscriptions = [];
    private List<WorkshopTaskSnapshot> _tasks = [];
    private readonly HashSet<string> _selectedDeleteFolders = new(StringComparer.Ordinal);
    private AuthPromptState? _authPrompt;
    private long _nextTaskId;
    private bool _loginBusy;
    private string _pluginDirectory = AppContext.BaseDirectory;
    private string _status = "请输入 Steam 账号并登录。";
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _guardCode = string.Empty;

    public string Id => "SteamWorkshopManager";
    public string Name => "Steam 工坊管理器";
    public string Version => "1.0.1";
    public string Author => "ADOFAI Mod Community";
    public string Description => "搜索、预览、下载和管理 ADOFAI Steam 工坊谱面，支持订阅同步与在线更新。";
    public IReadOnlyList<string> Dependencies => Array.Empty<string>();

    public void OnLoad()
    {
        var assemblyPath = typeof(SteamWorkshopManagerPlugin).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyPath))
            _pluginDirectory = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;

        _updateService = new GitHubUpdateService(_pluginDirectory, Version);
        _updateService.StartAutomaticCheck();
        _settings = LoadSettings();
        _levelsDirectoryEdit = _settings.LevelsDirectory;
        _settings.SelectedPublishedFileIds ??= [];
        _downloads = new WorkshopDownloadQueue(ExecuteDownloadAsync);
        try
        {
            _authStore = new SteamAuthStore(_pluginDirectory);
            _savedLogin = _authStore.Load();
            if (_savedLogin != null)
            {
                var savedLogin = _savedLogin;
                _username = savedLogin.AccountName;
                SetStatus("正在使用已保存的 Steam 会话自动登录…");
                BeginConnection(
                    token => SteamKitConnection.LoginWithRefreshTokenAsync(savedLogin, token),
                    automaticLogin: true);
                return;
            }
        }
        catch (Exception exception)
        {
            _authStoreError = exception.Message;
        }

        SetStatus("请输入 Steam 账号并登录。登录成功后会保存 Steam 会话并立即同步订阅。");
    }

    public void OnUnload()
    {
        CloseManagerWindow();
        _updateService?.Dispose();
        _updateService = null;
        _unloadCancellation.Cancel();
        CancelSession("Mod 已卸载。", clearSavedLogin: false);
        _downloads?.Dispose();
        _workshopPreview?.Dispose();
        _unloadCancellation.Dispose();
    }

    public void OnGui() => DrawSettingsUi();

    public void OnForegroundGUI(ImDrawListPtr drawList)
    {
        try
        {
            _workshopPreview?.ProcessDeferredRelease();
            DrawIndependentManagerWindow();
            _updateService?.DrawForegroundNotification();
        }
        catch (Exception exception)
        {
            CloseManagerWindow();
            SetStatus($"管理器界面绘制失败：{exception.GetBaseException().Message}");
        }
    }

    private void DrawLoginSection()
    {
        SteamKitConnection? connection;
        AuthPromptState? authPrompt;
        SteamSavedLogin? savedLogin;
        bool loginBusy;
        string status;
        lock (_stateLock)
        {
            connection = _connection;
            authPrompt = _authPrompt;
            savedLogin = _savedLogin;
            loginBusy = _loginBusy;
            status = _status;
        }

        ManagerWrapped(status);
        if (connection != null)
        {
            ManagerWrapped(connection.IsConnected
                ? $"已登录 Steam · SteamID {connection.SteamId}"
                : $"Steam 连接已断开 · SteamID {connection.SteamId}");
            if (!string.IsNullOrWhiteSpace(_authStoreError))
                ManagerWrapped($"本次登录会话未能安全保存，仅保留在内存：{_authStoreError}");
            else if (savedLogin != null)
                ManagerMuted("自动登录会话已使用本设备密钥加密保存。");
            ImGui.BeginDisabled(!connection.IsConnected);
            if (ImGui.Button("立即同步订阅"))
                StartManualSync();
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("退出 Steam 登录"))
                CancelSession("已退出 Steam 登录并清除保存的会话。", clearSavedLogin: true);
            return;
        }

        if (savedLogin != null || _authStore is { HasSavedSession: true } || _authStore != null && !string.IsNullOrWhiteSpace(_authStoreError))
        {
            if (savedLogin != null) ManagerMuted($"已加密保存 Steam 会话：{savedLogin.AccountName}");
            ImGui.BeginDisabled(loginBusy);
            if (savedLogin != null)
            {
                if (ImGui.Button("重试自动登录"))
                    BeginConnection(token => SteamKitConnection.LoginWithRefreshTokenAsync(savedLogin, token), automaticLogin: true);
                ImGui.SameLine();
            }
            if (ImGui.Button("清除保存的 Steam 会话")) ForgetSavedLogin();
            ImGui.EndDisabled();
        }
        if (!string.IsNullOrWhiteSpace(_authStoreError))
            ManagerWrapped($"Steam 会话安全存储：{_authStoreError}");

        ImGui.BeginDisabled(loginBusy);
        ImGui.InputText("Steam 账号", ref _username, 128);
        ImGui.InputText("Steam 密码", ref _password, 256, ImGuiInputTextFlags.Password);
        if (ImGui.Button("登录 Steam"))
            StartLogin();
        ImGui.EndDisabled();

        if (loginBusy && authPrompt == null)
        {
            ImGui.SameLine();
            if (ImGui.Button("取消登录"))
                CancelSession("已取消 Steam 登录。", clearSavedLogin: false);
        }

        if (authPrompt != null)
        {
            ImGui.Spacing();
            ManagerWrapped(authPrompt.Message);
            if (authPrompt.PreviousCodeWasIncorrect)
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "验证码不正确，请重新输入。");
            ImGui.InputText("Steam Guard 验证码", ref _guardCode, 64);
            if (ImGui.Button("提交验证码"))
            {
                var code = _guardCode.Trim();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    _guardCode = string.Empty;
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_authPrompt, authPrompt))
                            _authPrompt = null;
                    }
                    authPrompt.Completion.TrySetResult(code);
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("取消登录##guard"))
                CancelSession("已取消 Steam 登录。", clearSavedLogin: false);
        }
    }

    private void DrawSyncSettings()
    {
        bool autoSync;
        bool downloadOnlySelected;
        bool autoDeleteUnsubscribed;
        lock (_stateLock)
        {
            autoSync = _settings.AutoSync;
            downloadOnlySelected = _settings.DownloadOnlySelected;
            autoDeleteUnsubscribed = _settings.AutoDeleteUnsubscribed;
        }

        if (ImGui.Checkbox("自动同步订阅（每 15 分钟）", ref autoSync))
        {
            lock (_stateLock)
                _settings.AutoSync = autoSync;
            SaveSettings();
        }

        if (ImGui.Checkbox("仅下载已勾选的订阅", ref downloadOnlySelected))
        {
            lock (_stateLock)
                _settings.DownloadOnlySelected = downloadOnlySelected;
            SaveSettings();
        }

        if (ImGui.Checkbox("自动删除已取消订阅的谱面", ref autoDeleteUnsubscribed))
        {
            lock (_stateLock)
                _settings.AutoDeleteUnsubscribed = autoDeleteUnsubscribed;
            SaveSettings();
        }

        ImGui.InputText("Levels 目录", ref _levelsDirectoryEdit, 512);
        if (ImGui.Button("保存目录设置")) ApplyLevelsDirectory();
        ManagerWrapped("关闭筛选时下载全部订阅；开启后只下载订阅页勾选的项目。每个工坊项目使用 steam_<项目ID> 子目录，项目内谱面和资源保存在一起。");
    }

    private void SetDownloadSelection(string publishedFileId, bool selected)
    {
        lock (_stateLock)
        {
            _settings.SelectedPublishedFileIds ??= [];
            if (selected)
            {
                if (!_settings.SelectedPublishedFileIds.Contains(publishedFileId, StringComparer.Ordinal))
                    _settings.SelectedPublishedFileIds.Add(publishedFileId);
            }
            else
            {
                _settings.SelectedPublishedFileIds.RemoveAll(id => id == publishedFileId);
            }
        }
        SaveSettings();
    }

    private void SetAllDownloadSelection(bool select)
    {
        lock (_stateLock)
        {
            _settings.SelectedPublishedFileIds = select
                ? _subscriptions.Select(item => item.PublishedFileId).Distinct(StringComparer.Ordinal).ToList()
                : [];
        }
        SaveSettings();
    }

    private void StartDeleteSelectedLevels()
    {
        HashSet<string> folders;
        lock (_stateLock)
            folders = _selectedDeleteFolders.ToHashSet(StringComparer.Ordinal);
        if (folders.Count > 0)
            _ = Task.Run(() => DeleteSelectedLevelsAsync(folders), CancellationToken.None);
    }

    private async Task DeleteSelectedLevelsAsync(IReadOnlySet<string> folders)
    {
        if (!await _syncGate.WaitAsync(0).ConfigureAwait(false))
        {
            SetStatus("下载或同步正在运行，请暂停后续任务并等当前任务结束后再删除。");
            return;
        }

        try
        {
            string levelsDirectory;
            lock (_stateLock)
                levelsDirectory = _settings.LevelsDirectory;

            var removed = new WorkshopLevelInstaller(_pluginDirectory, levelsDirectory).RemoveInstalledLevels(folders);
            lock (_stateLock)
                foreach (var level in removed)
                    _selectedDeleteFolders.Remove(level.Folder);

            foreach (var level in removed)
                AddTask(level.PublishedFileId, string.IsNullOrWhiteSpace(level.Title) ? level.Folder : level.Title,
                    "已删除", 1d, $"已删除本地谱面目录 {level.Folder}。");

            SetStatus(removed.Count == 0
                ? "所选目录中没有可删除的 Mod 管理谱面。"
                : $"已删除 {removed.Count} 个本地谱面目录。仍订阅的谱面可能在后续同步中重新下载。");
        }
        catch (Exception exception)
        {
            SetStatus($"删除所选谱面失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            RequestLocalLibraryRefresh();
            _syncGate.Release();
        }
    }

    private void StartLogin()
    {
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrEmpty(_password))
        {
            SetStatus("请输入 Steam 账号和密码。");
            return;
        }

        string username = _username.Trim();
        string password = _password;
        string? guardData;
        lock (_stateLock)
            guardData = _savedLogin?.GuardData;

        BeginConnection(token =>
        {
            var authenticator = new SteamGuardPrompt(RequestGuardCodeAsync, SetStatus, token);
            return SteamKitConnection.LoginAsync(username, password, guardData, authenticator, token);
        }, automaticLogin: false);
    }

    private void BeginConnection(Func<CancellationToken, Task<SteamKitConnection>> createConnection, bool automaticLogin)
    {
        CancellationTokenSource sessionCancellation;
        lock (_stateLock)
        {
            if (_loginBusy || _connection != null)
                return;
            sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_unloadCancellation.Token);
            _sessionCancellation = sessionCancellation;
            _loginBusy = true;
            _authPrompt = null;
            _status = automaticLogin ? "正在使用已保存的 Steam 会话自动登录…" : "正在连接 Steam…";
        }

        var token = sessionCancellation.Token;
        _ = Task.Run(async () =>
        {
            SteamKitConnection? connection = null;
            string? persistenceError = null;
            try
            {
                connection = await createConnection(token).ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (_sessionCancellation != sessionCancellation || token.IsCancellationRequested)
                        throw new OperationCanceledException(token);
                    persistenceError = PersistSavedLogin(connection.SavedLogin);
                    _connection = connection;
                    connection = null;
                    _status = "Steam 登录成功，正在读取订阅…";
                }

                lock (_stateLock)
                    _password = string.Empty;
                await SynchronizeAsync(token).ConfigureAwait(false);
                if (persistenceError != null)
                    SetStatus($"Steam 已登录，但无法保存自动登录会话：{persistenceError}");
                _autoSyncTask = Task.Run(() => AutoSyncLoopAsync(token), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                SetStatus(automaticLogin ? "已取消 Steam 自动登录。" : "Steam 登录已取消。");
            }
            catch (Exception exception)
            {
                SetStatus(automaticLogin
                    ? $"Steam 自动登录失败：{exception.GetBaseException().Message}。可以重新登录或清除保存的会话。"
                    : $"Steam 登录失败：{exception.GetBaseException().Message}");
            }
            finally
            {
                connection?.Dispose();
                lock (_stateLock)
                {
                    _loginBusy = false;
                    _password = string.Empty;
                    _authPrompt = null;
                    if (_connection == null && ReferenceEquals(_sessionCancellation, sessionCancellation))
                    {
                        _sessionCancellation = null;
                        sessionCancellation.Dispose();
                    }
                }
            }
        });
    }

    private string? PersistSavedLogin(SteamSavedLogin savedLogin)
    {
        try
        {
            if (_authStore == null)
                return _authStoreError ?? "Android 私有存储不可用";
            _authStore.Save(savedLogin);
            _savedLogin = savedLogin;
            _authStoreError = null;
            return null;
        }
        catch (Exception exception)
        {
            _authStoreError = exception.Message;
            return _authStoreError;
        }
    }

    private void ForgetSavedLogin()
    {
        lock (_stateLock) _savedLogin = null;
        try
        {
            _authStore?.Clear();
            _authStoreError = null;
            SetStatus("已清除保存的 Steam 会话和设备密钥。");
        }
        catch (Exception exception)
        {
            _authStoreError = exception.Message;
            SetStatus($"清除保存的 Steam 会话失败：{exception.Message}");
        }
    }

    private async Task<string> RequestGuardCodeAsync(string message, bool previousCodeWasIncorrect, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new AuthPromptState(message, previousCodeWasIncorrect, completion);
        lock (_stateLock)
        {
            _authPrompt = prompt;
            _status = message;
        }

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_authPrompt, prompt))
                    _authPrompt = null;
            }
        }
    }

    private void StartManualSync(bool queueDownloads = true)
    {
        CancellationToken token;
        lock (_stateLock)
        {
            if (_connection == null || _sessionCancellation == null)
            {
                _status = "请先登录 Steam。";
                return;
            }
            token = _sessionCancellation.Token;
        }
        _ = Task.Run(() => SynchronizeAsync(token, queueDownloads), CancellationToken.None);
    }

    private async Task AutoSyncLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(AutoSyncInterval, cancellationToken).ConfigureAwait(false);
                bool enabled;
                lock (_stateLock)
                    enabled = _settings.AutoSync;
                if (enabled)
                    await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            SetStatus($"自动同步停止：{exception.GetBaseException().Message}");
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken, bool queueDownloads = true)
    {
        bool entered = false;
        try
        {
            entered = queueDownloads && await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (queueDownloads && !entered)
            {
                SetStatus("下载或同步正在运行，可在订阅页刷新列表并手动加入下载。");
                return;
            }

            SteamKitConnection connection;
            string levelsDirectory;
            lock (_stateLock)
            {
                connection = _connection ?? throw new InvalidOperationException("请先登录 Steam。");
                levelsDirectory = _settings.LevelsDirectory;
            }

            SetStatus("正在读取 AppID 977950 的完整订阅列表…");
            var subscriptions = await connection.GetSubscribedItemsAsync(cancellationToken).ConfigureAwait(false);
            var subscribedIds = subscriptions.Select(item => item.PublishedFileId).ToHashSet(StringComparer.Ordinal);
            bool downloadOnlySelected;
            bool autoDeleteUnsubscribed;
            HashSet<string> selectedDownloadIds;
            lock (_stateLock)
            {
                _subscriptions = subscriptions.ToList();
                downloadOnlySelected = _settings.DownloadOnlySelected;
                autoDeleteUnsubscribed = _settings.AutoDeleteUnsubscribed;
                selectedDownloadIds = _settings.SelectedPublishedFileIds.ToHashSet(StringComparer.Ordinal);
            }

            if (!queueDownloads)
            {
                SetStatus($"已读取 {subscriptions.Count} 个订阅项目。");
                return;
            }

            var installer = new WorkshopLevelInstaller(_pluginDirectory, levelsDirectory);
            IReadOnlyList<(string PublishedFileId, string Title, string Folder)> removedItems = autoDeleteUnsubscribed
                ? installer.RemoveUnsubscribed(subscribedIds)
                : [];
            foreach (var removed in removedItems)
                AddTask(removed.PublishedFileId, removed.Title, "已删除", 1d, $"取消订阅，已清理 {removed.Folder}");

            int completed = 0;
            int skipped = 0;
            int queued = 0;
            foreach (var item in subscriptions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (downloadOnlySelected && !selectedDownloadIds.Contains(item.PublishedFileId))
                {
                    skipped++;
                    continue;
                }
                if (installer.IsInstalledVersionCurrent(item))
                {
                    completed++;
                    continue;
                }
                EnqueueDownload(item.PublishedFileId, keepWithoutSubscription: false, item);
                queued++;
            }
            RequestLocalLibraryRefresh();
            SetStatus($"订阅已刷新：共 {subscriptions.Count} 项，最新 {completed} 项，跳过 {skipped} 项，加入下载 {queued} 项，清理 {removedItems.Count} 项。");

        }
        catch (OperationCanceledException)
        {
            SetStatus("同步已取消。");
        }
        catch (Exception exception)
        {
            SetStatus($"同步失败：{exception.GetBaseException().Message} 未确认完整订阅列表前不会删除本地谱面。");
        }
        finally
        {
            if (entered)
                _syncGate.Release();
        }
    }

    private void CancelSession(string status, bool clearSavedLogin)
    {
        CancellationTokenSource? cancellation;
        SteamKitConnection? connection;
        lock (_stateLock)
        {
            cancellation = _sessionCancellation;
            _sessionCancellation = null;
            connection = _connection;
            _connection = null;
            _authPrompt?.Completion.TrySetCanceled();
            _authPrompt = null;
            _loginBusy = false;
            _password = string.Empty;
            _guardCode = string.Empty;
            _subscriptions.Clear();
            _status = status;
        }
        _downloads?.CancelAll();
        CancelWorkshopPresentation();
        CancelWorkshopSearch();
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        connection?.Dispose();
        if (clearSavedLogin)
        {
            lock (_stateLock) _savedLogin = null;
            try
            {
                _authStore?.Clear();
                _authStoreError = null;
            }
            catch (Exception exception)
            {
                _authStoreError = exception.Message;
                status += $" 清除保存会话失败：{exception.Message}";
            }
        }
        SetStatus(status);
        if (cancellation != null)
        {
            try { cancellation.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    private long AddTask(string publishedFileId, string title, string status, double progress, string message)
    {
        lock (_stateLock)
        {
            var id = ++_nextTaskId;
            _tasks.Add(new WorkshopTaskSnapshot(id, publishedFileId, title, status, progress, message, DateTimeOffset.UtcNow));
            if (_tasks.Count > 100)
                _tasks.RemoveAt(0);
            return id;
        }
    }

    private void UpdateTask(long taskId, string status, double progress, string message)
    {
        lock (_stateLock)
        {
            var index = _tasks.FindIndex(task => task.TaskId == taskId);
            if (index < 0)
                return;
            _tasks[index] = _tasks[index] with
            {
                Status = status,
                Progress = Math.Clamp(progress, 0d, 1d),
                Message = message,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private void SetStatus(string status)
    {
        lock (_stateLock)
            _status = status;
    }

    private PluginSettings LoadSettings()
    {
        var path = SettingsPath;
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(path));
                if (settings != null)
                {
                    if (string.IsNullOrWhiteSpace(settings.LevelsDirectory))
                        settings.LevelsDirectory = WorkshopLevelInstaller.DefaultLevelsDirectory;
                    settings.SelectedPublishedFileIds = (settings.SelectedPublishedFileIds ?? [])
                        .Where(id => ulong.TryParse(id, out _))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    return settings;
                }
            }
        }
        catch { }
        return new PluginSettings();
    }

    private void SaveSettings()
    {
        try
        {
            PluginSettings settings;
            lock (_stateLock)
                settings = new PluginSettings
                {
                    AutoSync = _settings.AutoSync,
                    DownloadOnlySelected = _settings.DownloadOnlySelected,
                    AutoDeleteUnsubscribed = _settings.AutoDeleteUnsubscribed,
                    SelectedPublishedFileIds = (_settings.SelectedPublishedFileIds ?? []).ToList(),
                    LevelsDirectory = _settings.LevelsDirectory,
                };
            var path = SettingsPath;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception)
        {
            SetStatus($"保存设置失败：{exception.GetBaseException().Message}");
        }
    }

    private string SettingsPath => Path.Combine(_pluginDirectory, "SteamWorkshopManager.settings.json");

    private sealed record AuthPromptState(
        string Message,
        bool PreviousCodeWasIncorrect,
        TaskCompletionSource<string> Completion);
}
