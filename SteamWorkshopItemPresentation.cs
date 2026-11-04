namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    private readonly Dictionary<string, (WorkshopItem Item, DateTimeOffset LoadedAt)> _detailCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _detailCancellation;
    private long _detailGeneration;
    private string _detailRequestedId = string.Empty;
    private WorkshopItem? _detailItem;
    private bool _detailBusy;
    private string _detailError = string.Empty;
    private WorkshopPreview? _workshopPreview;
    private string _previewItemId = string.Empty;
    private int _previewIndex;

    private WorkshopItem EnsureWorkshopPresentation(WorkshopItem seed)
    {
        SteamKitConnection connection;
        CancellationTokenSource cancellation;
        long generation;
        lock (_stateLock)
        {
            if (_detailRequestedId == seed.PublishedFileId) return _detailItem ?? seed;
            _detailCancellation?.Cancel();
            _detailCancellation = null;
            generation = ++_detailGeneration;
            _detailRequestedId = seed.PublishedFileId;
            _detailItem = seed;
            _detailError = string.Empty;
            _detailBusy = false;
            if (_detailCache.TryGetValue(seed.PublishedFileId, out var cached)
                && DateTimeOffset.UtcNow - cached.LoadedAt < TimeSpan.FromMinutes(10))
                return _detailItem = cached.Item;
            if (_connection is not { IsConnected: true } || _sessionCancellation == null)
            {
                _detailError = "登录 Steam 后可补全工坊详情。";
                return seed;
            }
            connection = _connection;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, _unloadCancellation.Token);
            _detailCancellation = cancellation;
            _detailBusy = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var item = await connection.GetWorkshopPresentationAsync(seed.PublishedFileId, cancellation.Token).ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (generation != _detailGeneration || cancellation.IsCancellationRequested) return;
                    _detailItem = item;
                    _detailCache[item.PublishedFileId] = (item, DateTimeOffset.UtcNow);
                    foreach (var key in _detailCache.OrderBy(pair => pair.Value.LoadedAt).Take(Math.Max(0, _detailCache.Count - 128))
                        .Select(pair => pair.Key).ToArray()) _detailCache.Remove(key);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                lock (_stateLock)
                    if (generation == _detailGeneration) _detailError = $"详情补充失败：{exception.GetBaseException().Message}";
            }
            finally
            {
                lock (_stateLock)
                    if (generation == _detailGeneration) { _detailBusy = false; _detailCancellation = null; }
                cancellation.Dispose();
            }
        });
        return seed;
    }

    private void CancelWorkshopPresentation()
    {
        lock (_stateLock)
        {
            ++_detailGeneration;
            _detailCancellation?.Cancel();
            _detailCancellation = null;
            _detailRequestedId = string.Empty;
            _detailItem = null;
            _detailBusy = false;
            _detailError = string.Empty;
        }
        _workshopPreview?.Reset();
        _previewItemId = string.Empty;
        _previewIndex = 0;
    }

    private void CloseWorkshopDetails()
    {
        _workshopDetails = null;
        CancelWorkshopPresentation();
    }

    private void RetryWorkshopPresentation()
    {
        lock (_stateLock)
        {
            if (_workshopDetails != null) _detailCache.Remove(_workshopDetails.PublishedFileId);
        }
        CancelWorkshopPresentation();
    }
}
