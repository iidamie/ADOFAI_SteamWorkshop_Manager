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

    private bool EnqueueDownload(string publishedFileId, bool keepWithoutSubscription = true, WorkshopItem? item = null,
        CancellationToken? expectedSession = null)
    {
        try
        {
            lock (_stateLock)
            {
                if (expectedSession.HasValue && (_sessionCancellation == null || _sessionCancellation.Token != expectedSession.Value)) return false;
                if (_connection is not { IsConnected: true } || _sessionCancellation == null)
                {
                    _status = _reconnecting ? "Steam 正在自动重连，请连接恢复后加入下载。" : "请在账号与设置页登录 Steam 后下载。";
                    return false;
                }
                if (_downloads == null) return false;
                var id = _downloads.Enqueue(new WorkshopDownloadRequest(publishedFileId, _connection, _sessionCancellation.Token, keepWithoutSubscription, item));
                _status = $"项目 {publishedFileId} 已加入下载任务 #{id}。";
                return true;
            }
        }
        catch (Exception exception)
        {
            if (!expectedSession.HasValue) SetStatus($"无法加入下载任务：{exception.GetBaseException().Message}");
            return false;
        }
    }

    private async Task<InstallResult> ExecuteDownloadAsync(WorkshopDownloadRequest request, Action<double, string> progress,
        CancellationToken cancellationToken)
    {
        var projectGate = WorkshopDownloadConcurrency.ForProject(request.PublishedFileId);
        await projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress(0, "正在读取工坊详情…");
            var item = await DownloadNetworkRetry.RunAsync(
                () => request.Connection.GetWorkshopItemAsync(request.PublishedFileId, cancellationToken),
                "工坊详情查询", message => progress(0, message), cancellationToken).ConfigureAwait(false);
            request.Item = item;
            string levelsDirectory;
            int parallelChunks;
            WorkshopLevelInstaller installer;
            var libraryGate = WorkshopDownloadConcurrency.LibraryGate;
            await libraryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_stateLock)
                {
                    levelsDirectory = _settings.LevelsDirectory;
                    parallelChunks = _settings.ParallelChunksPerDownload;
                }
                if (!CanInstallRequest(request))
                    return new InstallResult(true, "项目已取消订阅，不再安装。", []);
                installer = new WorkshopLevelInstaller(_pluginDirectory, levelsDirectory);
                if (request.KeepWithoutSubscription)
                    installer.KeepItemWithoutSubscription(item.PublishedFileId);
                if (installer.IsInstalledVersionCurrent(item))
                {
                    WorkshopDownloadWorkspace.Open(_pluginDirectory, WorkshopDownloadWorkspace.AccountScope(request.Connection.SteamId), item).Complete();
                    return new InstallResult(false, "本地谱面已是最新版本。", []);
                }
            }
            finally { libraryGate.Release(); }

            progress(0, $"正在准备下载 · 每任务 {parallelChunks} 个并行分块…");
            return await installer.InstallAsync(item,
                (workspace, report, token) => request.Connection.DownloadPublishedFileAsync(item, workspace, report, token, parallelChunks),
                progress, cancellationToken, request.KeepWithoutSubscription,
                WorkshopDownloadWorkspace.AccountScope(request.Connection.SteamId), libraryGate,
                () => CanInstallRequest(request)).ConfigureAwait(false);
        }
        finally
        {
            projectGate.Release();
            RequestLocalLibraryRefresh();
        }
    }

    // Called while holding LibraryGate, after a complete subscription snapshot has been published.
    private bool CanInstallRequest(WorkshopDownloadRequest request)
    {
        lock (_stateLock)
        {
            EnsureSessionToken(request.SessionToken);
            return request.KeepWithoutSubscription || _subscriptions.Any(item => item.PublishedFileId == request.PublishedFileId);
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
        var libraryGate = WorkshopDownloadConcurrency.LibraryGate;
        if (!libraryGate.Wait(0))
        {
            SetStatus("本地谱面操作正在进行，请稍后保存目录设置。");
            return;
        }
        try
        {
            if (_downloads?.HasActiveDownloads == true)
                throw new InvalidOperationException("请暂停队列并等待当前下载结束后再修改目录。");
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
        finally { libraryGate.Release(); }
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
        _ = Task.Run(async () =>
        {
            var libraryGate = WorkshopDownloadConcurrency.LibraryGate;
            await libraryGate.WaitAsync().ConfigureAwait(false);
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
            finally { libraryGate.Release(); lock (_stateLock) _libraryBusy = false; }
        });
    }
}
