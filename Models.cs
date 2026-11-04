namespace SteamWorkshopManager;

internal sealed record WorkshopItem(
    string PublishedFileId,
    string Title,
    string FileName,
    string FileUrl,
    long FileSize,
    long TimeUpdated,
    ulong ContentHandle,
    string Description = "")
{
    public WorkshopMetadata? Metadata { get; init; }
}

internal sealed record WorkshopSearchPage(IReadOnlyList<WorkshopItem> Items, uint Total, int Page, int PageSize)
{
    public bool HasNext => (long)Page * PageSize < Total;
}

internal sealed record SteamSavedLogin(
    string AccountName,
    ulong SteamId,
    string RefreshToken,
    string? GuardData);

internal sealed record InstallResult(bool Skipped, string Message, IReadOnlyList<string> InstalledFolders);

internal sealed record WorkshopTaskSnapshot(
    long TaskId,
    string PublishedFileId,
    string Title,
    string Status,
    double Progress,
    string Message,
    DateTimeOffset UpdatedAt);

internal sealed record InstalledWorkshopLevel(
    string PublishedFileId,
    string Title,
    string Folder,
    string ChartFileName,
    IReadOnlyList<string>? ChartFiles = null,
    bool KeepWithoutSubscription = false);

internal sealed class WorkshopInstallMarker
{
    public int SchemaVersion { get; set; } = 1;
    public string PublishedFileId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int ChartCount { get; set; } = 1;
    public bool KeepWithoutSubscription { get; set; }
    public long TimeUpdated { get; set; }
    public long FileSize { get; set; }
    public ulong ContentHandle { get; set; }
    public List<string> Files { get; set; } = [];
}

internal sealed class PluginSettings
{
    public bool AutoSync { get; set; } = true;
    public bool DownloadOnlySelected { get; set; }
    public bool AutoDeleteUnsubscribed { get; set; } = true;
    public List<string> SelectedPublishedFileIds { get; set; } = [];
    public string LevelsDirectory { get; set; } = WorkshopLevelInstaller.DefaultLevelsDirectory;
}
