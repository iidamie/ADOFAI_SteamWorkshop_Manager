namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    private WorkshopDownloadQueue? _downloads;
    private CancellationTokenSource? _workshopSearchCancellation;
    private long _workshopSearchGeneration;
    private WorkshopSearchPage? _workshopResults;
    private bool _workshopSearching;
    private string _workshopError = string.Empty;
    private string _activeWorkshopQuery = string.Empty;
    private IReadOnlyList<InstalledWorkshopLevel> _localLibrary = [];
    private bool _libraryRefreshRequested = true;
    private bool _libraryBusy;
    private string _libraryError = string.Empty;

    private bool EnqueueDownload(string publishedFileId, bool keepWithoutSubscription = true, WorkshopItem? item = null)
    {
        SteamKitConnection connection;
        CancellationToken token;
        lock (_stateLock)
        {
            if (_connection is not { IsConnected: true } || _sessionCancellation == null)
            {
                _status = "请在账号与设置页登录 Steam 后下载。";
                return false;
            }
            connection = _connection;
            token = _sessionCancellation.Token;
        }
        try
        {
            if (_downloads == null) return false;
            var id = _downloads.Enqueue(new WorkshopDownloadRequest(publishedFileId, connection, token, keepWithoutSubscription, item));
            SetStatus($"项目 {publishedFileId} 已加入下载任务 #{id}。");
            return true;
        }
        catch (Exception exception)
        {
            SetStatus($"无法加入下载任务：{exception.GetBaseException().Message}");
            return false;
        }
    }

    private async Task<InstallResult> ExecuteDownloadAsync(WorkshopDownloadRequest request, Action<double, string> progress,
        CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress(0, "正在读取工坊详情…");
            var item = await request.Connection.GetWorkshopItemAsync(request.PublishedFileId, cancellationToken).ConfigureAwait(false);
            request.Item = item;
            progress(0, "正在准备安装…");
            string levelsDirectory;
            lock (_stateLock) levelsDirectory = _settings.LevelsDirectory;
            var installer = new WorkshopLevelInstaller(_pluginDirectory, levelsDirectory);
            if (request.KeepWithoutSubscription)
                installer.KeepItemWithoutSubscription(item.PublishedFileId);
            if (installer.IsInstalledVersionCurrent(item))
                return new InstallResult(false, "本地谱面已是最新版本。", []);

            var result = await installer.InstallAsync(item,
                (directory, report, token) => request.Connection.DownloadPublishedFileAsync(item, directory, report, token),
                progress, cancellationToken, request.KeepWithoutSubscription).ConfigureAwait(false);
            if (request.KeepWithoutSubscription)
                installer.KeepItemWithoutSubscription(item.PublishedFileId);
            return result;
        }
        finally
        {
            _syncGate.Release();
            RequestLocalLibraryRefresh();
        }
    }

    private void StartWorkshopSearch(string query, int page = 1)
    {
        SteamKitConnection connection;
        CancellationTokenSource cancellation;
        long generation;
        lock (_stateLock)
        {
            if (_connection is not { IsConnected: true } || _sessionCancellation == null)
            {
                _workshopError = "请先在账号与设置页登录 Steam。";
                return;
            }
            _workshopSearchCancellation?.Cancel();
            connection = _connection;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, _unloadCancellation.Token);
            _workshopSearchCancellation = cancellation;
            generation = ++_workshopSearchGeneration;
            _workshopSearching = true;
            _workshopResults = null;
            _workshopError = string.Empty;
            _activeWorkshopQuery = query.Trim();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await connection.SearchWorkshopAsync(query, page, ManagerPaging.PageSize, cancellation.Token).ConfigureAwait(false);
                lock (_stateLock)
                    if (generation == _workshopSearchGeneration && !cancellation.IsCancellationRequested)
                        _workshopResults = result;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                lock (_stateLock)
                    if (generation == _workshopSearchGeneration)
                        _workshopError = exception.GetBaseException().Message;
            }
            finally
            {
                lock (_stateLock)
                    if (generation == _workshopSearchGeneration)
                    {
                        _workshopSearching = false;
                        _workshopSearchCancellation = null;
                    }
                cancellation.Dispose();
            }
        });
    }

    private void CancelWorkshopSearch()
    {
        lock (_stateLock)
        {
            ++_workshopSearchGeneration;
            _workshopSearchCancellation?.Cancel();
            _workshopSearchCancellation = null;
            _workshopSearching = false;
            _workshopResults = null;
        }
    }

    private void RequestLocalLibraryRefresh()
    {
        lock (_stateLock) _libraryRefreshRequested = true;
    }

    private void ApplyLevelsDirectory()
    {
        try
        {
            if (!Path.IsPathFullyQualified(_levelsDirectoryEdit))
                throw new ArgumentException("请输入完整的 Levels 绝对路径。");
            var directory = Path.GetFullPath(_levelsDirectoryEdit);
            _ = new WorkshopLevelInstaller(_pluginDirectory, directory);
            lock (_stateLock)
            {
                _settings.LevelsDirectory = directory;
                _localLibrary = [];
                _selectedDeleteFolders.Clear();
                _libraryError = string.Empty;
            }
            SaveSettings();
            RequestLocalLibraryRefresh();
            SetStatus("Levels 目录设置已保存。");
        }
        catch (Exception exception) { SetStatus($"目录设置未保存：{exception.GetBaseException().Message}"); }
    }

    private void EnsureLocalLibraryRefresh()
    {
        string levelsDirectory;
        lock (_stateLock)
        {
            if (!_libraryRefreshRequested || _libraryBusy) return;
            levelsDirectory = _settings.LevelsDirectory;
            _libraryRefreshRequested = false;
            _libraryBusy = true;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var levels = new WorkshopLevelInstaller(_pluginDirectory, levelsDirectory).GetInstalledLevels();
                lock (_stateLock)
                {
                    if (levelsDirectory != _settings.LevelsDirectory) { _libraryRefreshRequested = true; return; }
                    _localLibrary = levels;
                    _selectedDeleteFolders.RemoveWhere(folder => levels.All(level => level.Folder != folder));
                    _libraryError = string.Empty;
                }
            }
            catch (Exception exception)
            {
                lock (_stateLock) _libraryError = exception.GetBaseException().Message;
            }
            finally { lock (_stateLock) _libraryBusy = false; }
        });
    }
}
