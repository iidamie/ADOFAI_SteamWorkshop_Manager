namespace SteamWorkshopManager;

internal static class WorkshopDownloadConcurrency
{
    // Library mutations are short; network, validation and extraction never hold this gate.
    internal static readonly SemaphoreSlim LibraryGate = new(1, 1);

    // Bounded, process-local locks also cover draining jobs from a previous queue/session.
    // Key by item rather than account: accounts share the same Levels/steam_<ID> target.
    private static readonly SemaphoreSlim[] ProjectGates = Enumerable.Range(0, 257)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal static SemaphoreSlim ForProject(string publishedFileId)
    {
        if (!ulong.TryParse(publishedFileId, out var id) || id == 0)
            throw new InvalidDataException("无效的创意工坊项目 ID。");
        return ProjectGates[(int)(id % (ulong)ProjectGates.Length)];
    }
}
