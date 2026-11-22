namespace SteamWorkshopManager;

internal enum DownloadState { Queued, Running, Completed, Skipped, Failed, Canceled, Interrupted }

internal sealed class WorkshopDownloadRequest(
    string publishedFileId, SteamKitConnection connection, CancellationToken sessionToken,
    bool keepWithoutSubscription, WorkshopItem? item = null)
{
    private int _keepWithoutSubscription = keepWithoutSubscription ? 1 : 0;
    public string PublishedFileId { get; } = publishedFileId;
    public SteamKitConnection Connection { get; } = connection;
    public CancellationToken SessionToken { get; } = sessionToken;
    public WorkshopItem? Item { get; set; } = item;
    public bool KeepWithoutSubscription
    {
        get => Volatile.Read(ref _keepWithoutSubscription) != 0;
        set => Volatile.Write(ref _keepWithoutSubscription, value ? 1 : 0);
    }
}

internal sealed record DownloadJobSnapshot(long Id, string PublishedFileId, string Title,
    DownloadState State, double Progress, string Message, DateTimeOffset UpdatedAt, bool KeepWithoutSubscription)
{
    public bool IsTerminal => State is not (DownloadState.Queued or DownloadState.Running or DownloadState.Interrupted);
    public string StatusText => State switch
    {
        DownloadState.Queued => "排队中", DownloadState.Running => "下载中",
        DownloadState.Completed => "已完成", DownloadState.Skipped => "已跳过",
        DownloadState.Failed => "失败", DownloadState.Canceled => "已取消", DownloadState.Interrupted => "等待恢复", _ => string.Empty,
    };
}

internal sealed class WorkshopDownloadQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<(Job Job, long Revision)> _pending = new();
    private readonly Dictionary<long, Job> _jobs = [];
    private readonly SemaphoreSlim _signal = new(0, MaximumConcurrentTasks);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<WorkshopDownloadRequest, Action<double, string>, CancellationToken, Task<InstallResult>> _execute;
    private readonly string _journalPath;
    public const int MaximumConcurrentTasks = 6;
    private readonly Task _workers;
    private readonly HashSet<string> _activeProjects = new(StringComparer.Ordinal);
    private int _concurrentTasks;
    private long _nextId;
    private bool _paused;
    private bool _disposed;
    private string _persistenceError = string.Empty;
    private DateTimeOffset _lastPersisted;

    public WorkshopDownloadQueue(string modDirectory,
        Func<WorkshopDownloadRequest, Action<double, string>, CancellationToken, Task<InstallResult>> execute, int concurrentTasks = 3)
    {
        _execute = execute;
        _journalPath = Path.Combine(modDirectory, "data", "download-tasks.json");
        Load();
        _concurrentTasks = Math.Clamp(concurrentTasks, 1, MaximumConcurrentTasks);
        _workers = Task.WhenAll(Enumerable.Range(0, MaximumConcurrentTasks).Select(_ => Task.Run(RunAsync)));
        _ = _workers.ContinueWith(_ =>
        {
            lock (_gate) { _signal.Dispose(); _lifetime.Dispose(); }
        },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public bool HasActiveDownloads { get { lock (_gate) return _activeProjects.Count > 0; } }
    public int ConcurrentTasks { get { lock (_gate) return _concurrentTasks; } }

    public void SetConcurrency(int concurrentTasks)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _concurrentTasks = Math.Clamp(concurrentTasks, 1, MaximumConcurrentTasks);
            WakeWorkers();
        }
    }

    // Called with _gate held. Every idle worker must reconsider the shared limits.
    private void WakeWorkers()
    {
        var missing = MaximumConcurrentTasks - _signal.CurrentCount;
        if (missing > 0) _signal.Release(missing);
    }

    public bool IsPaused { get { lock (_gate) return _paused; } }
    public string PersistenceError { get { lock (_gate) return _persistenceError; } }

    private void Load()
    {
        try
        {
            var journal = DownloadCheckpoint.Read<Journal>(_journalPath, 16 * 1024 * 1024);
            if (journal == null) return;
            if (journal.Format != 1 || journal.Jobs.Count > 10500) throw new InvalidDataException("下载记录格式无效。");
            var restored = new Dictionary<long, Job>();
            foreach (var entry in journal.Jobs)
            {
                if (entry.Id <= 0 || !ulong.TryParse(entry.PublishedFileId, out var id) || id == 0
                    || entry.Owner.Length != 64 || !entry.Owner.All(Uri.IsHexDigit) || !Enum.IsDefined(entry.State))
                    throw new InvalidDataException("下载记录身份或状态无效。");
                var state = entry.State is DownloadState.Queued or DownloadState.Running or DownloadState.Failed ? DownloadState.Interrupted : entry.State;
                var job = new Job(entry.Id, entry.PublishedFileId, entry.Owner, entry.Title, entry.KeepWithoutSubscription)
                {
                    State = state, Progress = double.IsFinite(entry.Progress) ? Math.Clamp(entry.Progress, 0, 1) : 0,
                    UpdatedAt = entry.UpdatedAt,
                    Message = state == DownloadState.Interrupted ? "等待原 Steam 账号登录后继续下载。" : "上次运行保存的任务记录。",
                };
                if (!restored.TryAdd(job.Id, job)) throw new InvalidDataException("下载记录包含重复任务。");
            }
            foreach (var job in restored.Values) _jobs.Add(job.Id, job);
            _nextId = _jobs.Count == 0 ? 0 : _jobs.Keys.Max();
            _paused = journal.Paused;
        }
        catch (Exception exception) { _persistenceError = $"无法读取下载任务：{exception.Message}"; }
    }

    public long Enqueue(WorkshopDownloadRequest request)
    {
        var owner = WorkshopDownloadWorkspace.AccountScope(request.Connection.SteamId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            request.SessionToken.ThrowIfCancellationRequested();
            var existing = _jobs.Values.FirstOrDefault(job => job.Owner == owner && job.PublishedFileId == request.PublishedFileId
                && (!job.IsTerminal || job.State == DownloadState.Failed) && (!request.KeepWithoutSubscription || job.KeepWithoutSubscription || job.State != DownloadState.Running));
            if (existing != null)
            {
                if (request.KeepWithoutSubscription)
                {
                    existing.KeepWithoutSubscription = true;
                    if (existing.Request != null) existing.Request.KeepWithoutSubscription = true;
                }
                if (existing.State is DownloadState.Interrupted or DownloadState.Failed) Bind(existing, request);
                Persist(force: true);
                return existing.Id;
            }
            if (_jobs.Values.Count(job => !job.IsTerminal || job.State == DownloadState.Failed) >= 10000) throw new InvalidOperationException("未完成下载任务过多，请先取消不需要的任务。");
            var job = new Job(++_nextId, request.PublishedFileId, owner, request.Item?.Title ?? request.PublishedFileId, request.KeepWithoutSubscription);
            _jobs.Add(job.Id, job);
            Bind(job, request);
            TrimHistory();
            Persist(force: true);
            return job.Id;
        }
    }

    public void ResumeForSession(SteamKitConnection connection, CancellationToken sessionToken, IReadOnlySet<string>? subscribedIds)
    {
        var owner = WorkshopDownloadWorkspace.AccountScope(connection.SteamId);
        lock (_gate)
        {
            if (_disposed || sessionToken.IsCancellationRequested) return;
            var changed = false;
            var groups = _jobs.Values.Where(job => job.Owner == owner && job.State == DownloadState.Interrupted)
                .GroupBy(job => job.PublishedFileId).Select(group => group.OrderBy(job => job.Id).ToArray()).ToArray();
            foreach (var group in groups)
            {
                var manual = group.Any(job => job.KeepWithoutSubscription);
                var current = _jobs.Values.FirstOrDefault(job => job.Owner == owner && job.PublishedFileId == group[0].PublishedFileId
                    && job.State is DownloadState.Queued or DownloadState.Running
                    && (!manual || job.KeepWithoutSubscription || job.State == DownloadState.Queued));
                if (current == null && !manual)
                {
                    if (subscribedIds == null)
                    {
                        foreach (var waiting in group) waiting.Message = "等待完整订阅列表校验后继续。";
                        continue;
                    }
                    if (!subscribedIds.Contains(group[0].PublishedFileId))
                    {
                        foreach (var removed in group) SetState(removed, DownloadState.Canceled, removed.Progress, "项目已取消订阅，不再自动恢复。");
                        changed = true;
                        continue;
                    }
                }
                var primary = current ?? group[0];
                if (manual)
                {
                    primary.KeepWithoutSubscription = true;
                    if (primary.Request != null) primary.Request.KeepWithoutSubscription = true;
                }
                if (current == null)
                    Bind(primary, new WorkshopDownloadRequest(primary.PublishedFileId, connection, sessionToken, primary.KeepWithoutSubscription));
                foreach (var duplicate in group.Where(job => job != primary))
                    SetState(duplicate, DownloadState.Skipped, duplicate.Progress, "同项目已有恢复任务，已合并下载意图。");
                changed = true;
            }
            if (changed) Persist(force: true);
        }
    }

    private void Bind(Job job, WorkshopDownloadRequest request)
    {
        job.Request = request;
        job.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, request.SessionToken);
        job.Revision++;
        SetState(job, DownloadState.Queued, job.Progress, "已加入队列，将校验并复用已有下载数据。");
        _pending.Enqueue((job, job.Revision));
        WakeWorkers();
    }

    public DownloadJobSnapshot[] GetSnapshots()
    {
        lock (_gate) return _jobs.Values.Select(job => job.Snapshot()).OrderByDescending(job => job.Id).ToArray();
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _paused = paused;
            Persist(force: true);
            if (!paused) WakeWorkers();
        }
    }

    public void Cancel(long id)
    {
        CancellationTokenSource? cancellation;
        bool running;
        lock (_gate)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) || job.IsTerminal && job.State != DownloadState.Failed) return;
            cancellation = job.Cancellation;
            running = job.State == DownloadState.Running;
            job.Cancellation = null;
            job.Revision++;
            SetState(job, DownloadState.Canceled, job.Progress, "已取消此任务；断点保留，订阅同步仍按设置执行。");
            Persist(force: true);
        }
        CancelToken(cancellation, dispose: !running);
    }

    public void CancelAll()
    {
        var tokens = new List<(CancellationTokenSource? Token, bool Running)>();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var job in _jobs.Values.Where(job => !job.IsTerminal || job.State == DownloadState.Failed))
            {
                tokens.Add((job.Cancellation, job.State == DownloadState.Running));
                job.Cancellation = null;
                job.Revision++;
                SetState(job, DownloadState.Canceled, job.Progress, "已取消此任务；断点保留，订阅同步仍按设置执行。");
            }
            _pending.Clear();
            Persist(force: true);
        }
        foreach (var (token, running) in tokens) CancelToken(token, dispose: !running);
    }

    public void CancelProjects(IReadOnlySet<string> publishedFileIds)
        => CancelWhere(job => publishedFileIds.Contains(job.PublishedFileId), "本地项目已删除，已取消对应下载。");

    public void CancelUnsubscribed(SteamKitConnection connection, IReadOnlySet<string> subscribedIds)
    {
        var owner = WorkshopDownloadWorkspace.AccountScope(connection.SteamId);
        CancelWhere(job => job.Owner == owner && !job.KeepWithoutSubscription && !subscribedIds.Contains(job.PublishedFileId),
            "项目已取消订阅，已停止自动下载。");
    }

    private void CancelWhere(Func<Job, bool> predicate, string message)
    {
        var tokens = new List<(CancellationTokenSource? Token, bool Running)>();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var job in _jobs.Values.Where(job => (!job.IsTerminal || job.State == DownloadState.Failed) && predicate(job)))
            {
                tokens.Add((job.Cancellation, job.State == DownloadState.Running));
                job.Cancellation = null;
                job.Revision++;
                SetState(job, DownloadState.Canceled, job.Progress, message);
            }
            if (tokens.Count > 0) Persist(force: true);
        }
        foreach (var (token, running) in tokens) CancelToken(token, dispose: !running);
    }

    public void SuspendSession()
    {
        List<(CancellationTokenSource? Token, bool Running)> tokens;
        lock (_gate)
        {
            if (_disposed) return;
            tokens = Suspend();
            Persist(force: true);
        }
        foreach (var (token, running) in tokens) CancelToken(token, dispose: !running);
    }

    private List<(CancellationTokenSource? Token, bool Running)> Suspend()
    {
        var tokens = new List<(CancellationTokenSource?, bool)>();
        _pending.Clear();
        foreach (var job in _jobs.Values.Where(job => !job.IsTerminal))
        {
            tokens.Add((job.Cancellation, job.State == DownloadState.Running));
            job.Cancellation = null;
            job.Revision++;
            SetState(job, DownloadState.Interrupted, job.Progress, "已保留断点，等待原 Steam 账号登录后继续。");
        }
        return tokens;
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Values.Where(job => job.IsTerminal).ToArray()) _jobs.Remove(job.Id);
            Persist(force: true);
        }
    }

    public void Dispose()
    {
        List<(CancellationTokenSource? Token, bool Running)> tokens;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            tokens = Suspend();
            Persist(force: true);
        }
        foreach (var (token, running) in tokens) CancelToken(token, dispose: !running);
        lock (_gate) _lifetime.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    Job? job = null;
                    WorkshopDownloadRequest? request = null;
                    CancellationTokenSource? cancellation = null;
                    long revision = 0;
                    lock (_gate)
                    {
                        if (_paused || _disposed || _activeProjects.Count >= _concurrentTasks) break;
                        var remaining = _pending.Count;
                        while (remaining-- > 0 && _pending.TryDequeue(out var candidate))
                        {
                            if (candidate.Job.Revision != candidate.Revision || candidate.Job.State != DownloadState.Queued) continue;
                            // A canceled execution still owns its project until its I/O has drained.
                            if (_activeProjects.Contains(candidate.Job.PublishedFileId))
                            {
                                _pending.Enqueue(candidate);
                                continue;
                            }
                            job = candidate.Job;
                            _activeProjects.Add(job.PublishedFileId);
                            revision = job.Revision;
                            request = job.Request!;
                            cancellation = job.Cancellation!;
                            SetState(job, DownloadState.Running, job.Progress, "正在检查下载断点…");
                            Persist(force: true);
                            break;
                        }
                    }
                    if (job == null) break;
                    try
                    {
                        cancellation!.Token.ThrowIfCancellationRequested();
                        var result = await _execute(request!, (progress, message) =>
                        {
                            lock (_gate)
                                if (job.Revision == revision && job.State == DownloadState.Running)
                                {
                                    if (!string.IsNullOrWhiteSpace(request!.Item?.Title)) job.Title = request.Item.Title;
                                    SetState(job, DownloadState.Running, Math.Clamp(progress, 0, 1), message);
                                    Persist();
                                }
                        }, cancellation.Token).ConfigureAwait(false);
                        lock (_gate)
                            if (job.Revision == revision)
                            {
                                SetState(job, result.Skipped ? DownloadState.Skipped : DownloadState.Completed, 1, result.Message);
                                Persist(force: true);
                            }
                    }
                    catch (Exception exception)
                    {
                        lock (_gate)
                            if (job.Revision == revision)
                            {
                                var interrupted = request!.SessionToken.IsCancellationRequested || _lifetime.IsCancellationRequested
                                    || !request.Connection.IsConnected;
                                SetState(job, interrupted ? DownloadState.Interrupted : DownloadState.Failed, job.Progress,
                                    interrupted ? "已保留断点，登录后可继续。" : DownloadNetworkRetry.FailureMessage(exception));
                                Persist(force: true);
                            }
                    }
                    finally
                    {
                        cancellation!.Dispose();
                        lock (_gate)
                        {
                            if (ReferenceEquals(job.Cancellation, cancellation)) job.Cancellation = null;
                            _activeProjects.Remove(job.PublishedFileId);
                            if (!_disposed) WakeWorkers();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void TrimHistory()
    {
        foreach (var old in _jobs.Values.Where(job => job.IsTerminal && job.State != DownloadState.Failed).OrderByDescending(job => job.UpdatedAt).Skip(500).ToArray())
            _jobs.Remove(old.Id);
    }

    private void Persist(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastPersisted < TimeSpan.FromSeconds(1)) return;
        try
        {
            TrimHistory();
            var entries = _jobs.Values.Select(job => new Entry(job.Id, job.PublishedFileId, job.Owner,
                job.Title[..Math.Min(job.Title.Length, 128)], job.KeepWithoutSubscription, job.State, job.Progress, job.UpdatedAt)).ToList();
            DownloadCheckpoint.Write(_journalPath, new Journal(1, _paused, entries));
            _lastPersisted = DateTimeOffset.UtcNow;
            _persistenceError = string.Empty;
        }
        catch (Exception exception) { _persistenceError = $"下载任务记录无法保存：{exception.Message}"; }
    }

    private static void CancelToken(CancellationTokenSource? token, bool dispose)
    {
        try { token?.Cancel(); } catch (ObjectDisposedException) { }
        if (dispose) token?.Dispose();
    }

    private static void SetState(Job job, DownloadState state, double progress, string message)
    {
        job.State = state; job.Progress = progress; job.Message = message; job.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private sealed class Job(long id, string publishedFileId, string owner, string title, bool keep)
    {
        public long Id { get; } = id;
        public string PublishedFileId { get; } = publishedFileId;
        public string Owner { get; } = owner;
        public string Title { get; set; } = title;
        public bool KeepWithoutSubscription { get; set; } = keep;
        public WorkshopDownloadRequest? Request { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public long Revision { get; set; }
        public DownloadState State { get; set; } = DownloadState.Queued;
        public double Progress { get; set; }
        public string Message { get; set; } = "等待下载。";
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool IsTerminal => State is not (DownloadState.Queued or DownloadState.Running or DownloadState.Interrupted);
        public DownloadJobSnapshot Snapshot() => new(Id, PublishedFileId, string.IsNullOrWhiteSpace(Title) ? PublishedFileId : Title, State, Progress, Message, UpdatedAt, KeepWithoutSubscription);
    }

    private sealed record Journal(int Format, bool Paused, List<Entry> Jobs);
    private sealed record Entry(long Id, string PublishedFileId, string Owner, string Title, bool KeepWithoutSubscription,
        DownloadState State, double Progress, DateTimeOffset UpdatedAt);
}
