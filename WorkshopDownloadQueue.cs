namespace SteamWorkshopManager;

internal enum DownloadState { Queued, Running, Completed, Skipped, Failed, Canceled }

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
    public bool IsTerminal => State is not (DownloadState.Queued or DownloadState.Running);
    public string StatusText => State switch
    {
        DownloadState.Queued => "排队中", DownloadState.Running => "下载中",
        DownloadState.Completed => "已完成", DownloadState.Skipped => "已跳过",
        DownloadState.Failed => "失败", DownloadState.Canceled => "已取消", _ => string.Empty,
    };
}

internal sealed class WorkshopDownloadQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Job> _pending = new();
    private readonly Dictionary<long, Job> _jobs = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<WorkshopDownloadRequest, Action<double, string>, CancellationToken, Task<InstallResult>> _execute;
    private readonly Task _worker;
    private long _nextId;
    private bool _paused;
    private bool _disposed;

    public WorkshopDownloadQueue(Func<WorkshopDownloadRequest, Action<double, string>, CancellationToken, Task<InstallResult>> execute)
    {
        _execute = execute;
        _worker = Task.Run(RunAsync);
    }

    public bool IsPaused { get { lock (_gate) return _paused; } }

    public long Enqueue(WorkshopDownloadRequest request)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            request.SessionToken.ThrowIfCancellationRequested();
            var matching = _jobs.Values.Where(job => job.Request.PublishedFileId == request.PublishedFileId
                && ReferenceEquals(job.Request.Connection, request.Connection)
                && job.State is DownloadState.Queued or DownloadState.Running).ToArray();
            var existing = matching.FirstOrDefault(job => !request.KeepWithoutSubscription
                || job.Request.KeepWithoutSubscription || job.State == DownloadState.Queued);
            if (existing != null)
            {
                if (request.KeepWithoutSubscription)
                    existing.Request.KeepWithoutSubscription = true;
                return existing.Id;
            }

            var job = new Job(++_nextId, request,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, request.SessionToken));
            _jobs.Add(job.Id, job);
            _pending.Enqueue(job);
            // Keep every unfinished task; limit only old completed history.
            foreach (var old in _jobs.Values.Where(value => value.IsTerminal).OrderBy(value => value.UpdatedAt)
                .Take(Math.Max(0, _jobs.Count - 500)).ToArray())
                _jobs.Remove(old.Id);
            _signal.Release();
            return job.Id;
        }
    }

    public DownloadJobSnapshot[] GetSnapshots()
    {
        lock (_gate)
            return _jobs.Values.Select(job => job.Snapshot()).OrderByDescending(job => job.Id).ToArray();
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _paused = paused;
            if (!paused) _signal.Release();
        }
    }

    public void Cancel(long id)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.IsTerminal) return;
            cancellation = job.Cancellation;
            if (job.State == DownloadState.Queued)
                SetState(job, DownloadState.Canceled, 0, "已取消排队。");
            else
                job.Message = "正在取消…";
        }
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void CancelAll()
    {
        long[] ids;
        lock (_gate) ids = _jobs.Values.Where(job => !job.IsTerminal).Select(job => job.Id).ToArray();
        foreach (var id in ids) Cancel(id);
    }

    public void ClearFinished()
    {
        lock (_gate)
            foreach (var job in _jobs.Values.Where(job => job.IsTerminal).ToArray())
                _jobs.Remove(job.Id);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CancelAll();
        _lifetime.Cancel();
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
                    lock (_gate)
                    {
                        if (_paused || _disposed) break;
                        while (_pending.TryDequeue(out var candidate))
                        {
                            if (candidate.IsTerminal) { candidate.Cancellation.Dispose(); continue; }
                            job = candidate;
                            SetState(job, DownloadState.Running, 0, "准备下载…");
                            break;
                        }
                    }
                    if (job == null) break;
                    try
                    {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        var result = await _execute(job.Request, (progress, message) =>
                        {
                            lock (_gate)
                                if (job.State == DownloadState.Running)
                                    SetState(job, DownloadState.Running, Math.Clamp(progress, 0, 1), message);
                        }, job.Cancellation.Token).ConfigureAwait(false);
                        lock (_gate)
                            SetState(job, result.Skipped ? DownloadState.Skipped : DownloadState.Completed, 1, result.Message);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_gate) SetState(job, DownloadState.Canceled, 0, "下载已取消。");
                    }
                    catch (Exception exception)
                    {
                        lock (_gate)
                            SetState(job, job.Cancellation.IsCancellationRequested ? DownloadState.Canceled : DownloadState.Failed,
                                0, job.Cancellation.IsCancellationRequested ? "下载已取消。" : exception.GetBaseException().Message);
                    }
                    finally { job.Cancellation.Dispose(); }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
                while (_pending.TryDequeue(out var pending)) pending.Cancellation.Dispose();
            _signal.Dispose();
            _lifetime.Dispose();
        }
    }

    private static void SetState(Job job, DownloadState state, double progress, string message)
    {
        job.State = state;
        job.Progress = progress;
        job.Message = message;
        job.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private sealed class Job(long id, WorkshopDownloadRequest request, CancellationTokenSource cancellation)
    {
        public long Id { get; } = id;
        public WorkshopDownloadRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DownloadState State { get; set; } = DownloadState.Queued;
        public double Progress { get; set; }
        public string Message { get; set; } = "等待下载。";
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool IsTerminal => State is not (DownloadState.Queued or DownloadState.Running);
        public DownloadJobSnapshot Snapshot() => new(Id, Request.PublishedFileId,
            string.IsNullOrWhiteSpace(Request.Item?.Title) ? Request.PublishedFileId : Request.Item.Title,
            State, Progress, Message, UpdatedAt, Request.KeepWithoutSubscription);
    }
}
