using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamWorkshopManager;

internal sealed class WorkshopLevelInstaller
{
    public const string DefaultLevelsDirectory = "/storage/emulated/0/ADOFAI/Levels";

    private const string MarkerFileName = ".steam-workshop-manager.json";
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const int MaxArchiveEntries = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _levelsDirectory;
    private readonly string _scratchDirectory;

    public WorkshopLevelInstaller(string pluginDirectory, string levelsDirectory)
    {
        _levelsDirectory = Path.GetFullPath(levelsDirectory);
        if (string.IsNullOrWhiteSpace(levelsDirectory) || Path.GetPathRoot(_levelsDirectory) == _levelsDirectory)
            throw new ArgumentException("谱面目录必须是一个非根目录的绝对路径。", nameof(levelsDirectory));

        _scratchDirectory = Path.Combine(pluginDirectory, "workshop-cache");
    }

    public bool IsInstalledVersionCurrent(WorkshopItem item)
    {
        var markers = FindMarkersForItemWithDirectories(item.PublishedFileId);
        if (markers.Count != 1)
            return false;

        var (directory, marker) = markers[0];
        if (marker.TimeUpdated != item.TimeUpdated
            || marker.FileSize != item.FileSize
            || marker.ContentHandle != item.ContentHandle)
            return false;

        var chartFiles = marker.Files.Where(file => IsSafeDirectFileName(file)
            && file.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (chartFiles.Length == 0 || chartFiles.Length != marker.ChartCount)
            return false;

        return marker.Files.All(file => IsSafeDirectFileName(file) && File.Exists(Path.Combine(directory, file)));
    }

    public async Task<InstallResult> InstallAsync(
        WorkshopItem item,
        Func<string, Action<double, string>?, CancellationToken, Task> downloadContent,
        Action<double, string>? progress,
        CancellationToken cancellationToken,
        bool keepWithoutSubscription = false)
    {
        if (!ulong.TryParse(item.PublishedFileId, out _))
            throw new InvalidDataException("Steam 返回了无效的创意工坊项目 ID。");
        ArgumentNullException.ThrowIfNull(downloadContent);

        Directory.CreateDirectory(_scratchDirectory);
        Directory.CreateDirectory(_levelsDirectory);

        string runDirectory = Path.Combine(_scratchDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        string contentDirectory = Path.Combine(runDirectory, "content");
        string flattenedDirectory = Path.Combine(runDirectory, "flattened");
        try
        {
            Directory.CreateDirectory(contentDirectory);
            await downloadContent(contentDirectory, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(flattenedDirectory);
            progress?.Invoke(0.72d, "正在整理 Steam 工坊文件…");
            await FlattenContentDirectoryAsync(contentDirectory, flattenedDirectory, cancellationToken).ConfigureAwait(false);

            var chartFiles = Directory.EnumerateFiles(flattenedDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (chartFiles.Length == 0)
                return new InstallResult(true, "工坊项目中没有 .adofai 谱面文件。", []);

            var projectFiles = Directory.EnumerateFiles(flattenedDirectory, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var stageDirectory = Path.Combine(runDirectory, "project");
            Directory.CreateDirectory(stageDirectory);
            foreach (var projectFile in projectFiles)
                File.Copy(projectFile, Path.Combine(stageDirectory, Path.GetFileName(projectFile)!), overwrite: false);

            var folderName = ChooseFolderName(item.PublishedFileId);
            var targetDirectory = Path.Combine(_levelsDirectory, folderName);
            var stageFiles = Directory.EnumerateFiles(stageDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
            var marker = new WorkshopInstallMarker
            {
                PublishedFileId = item.PublishedFileId,
                Title = item.Title,
                ChartCount = chartFiles.Length,
                KeepWithoutSubscription = keepWithoutSubscription || FindMarkersForItemWithDirectories(item.PublishedFileId)
                    .Any(entry => entry.Marker.KeepWithoutSubscription),
                TimeUpdated = item.TimeUpdated,
                FileSize = item.FileSize,
                ContentHandle = item.ContentHandle,
                Files = stageFiles.Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            };

            CommitFiles(targetDirectory, item.PublishedFileId, stageFiles, marker);
            progress?.Invoke(1d, $"已将 {chartFiles.Length} 张谱面及其资源安装到 {folderName}");
            return new InstallResult(false, $"已将 {chartFiles.Length} 张谱面及其资源安装到同一目录。", [folderName]);
        }
        finally
        {
            TryDeleteDirectory(runDirectory);
        }
    }

    public IReadOnlyList<(string PublishedFileId, string Title, string Folder)> RemoveUnsubscribed(IReadOnlySet<string> subscribedIds)
    {
        if (!Directory.Exists(_levelsDirectory))
            return [];

        var removed = new List<(string PublishedFileId, string Title, string Folder)>();
        foreach (var directory in Directory.EnumerateDirectories(_levelsDirectory, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            if (IsDirectoryLink(directory))
                continue;
            var marker = ReadMarker(directory);
            if (marker == null || marker.KeepWithoutSubscription || subscribedIds.Contains(marker.PublishedFileId))
                continue;

            RemoveMarkedFiles(directory, marker);
            removed.Add((marker.PublishedFileId, marker.Title, Path.GetFileName(directory)));
        }

        return removed;
    }

    public IReadOnlyList<InstalledWorkshopLevel> GetInstalledLevels()
    {
        if (!Directory.Exists(_levelsDirectory))
            return [];

        var installed = new List<InstalledWorkshopLevel>();
        foreach (var directory in Directory.EnumerateDirectories(_levelsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsDirectoryLink(directory))
                continue;
            var marker = ReadMarker(directory);
            if (marker == null)
                continue;

            var chart = marker.Files
                .Where(file => IsSafeDirectFileName(file) && file.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(file => File.Exists(Path.Combine(directory, file)))
                ?? marker.Files.FirstOrDefault(file => IsSafeDirectFileName(file) && file.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;
            installed.Add(new InstalledWorkshopLevel(
                marker.PublishedFileId,
                marker.Title,
                Path.GetFileName(directory),
                chart,
                marker.Files.Where(file => IsSafeDirectFileName(file) && file.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase)).ToArray(),
                marker.KeepWithoutSubscription));
        }

        return installed.OrderBy(level => level.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(level => level.Folder, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<InstalledWorkshopLevel> RemoveInstalledLevels(IReadOnlySet<string> selectedFolders)
    {
        if (!Directory.Exists(_levelsDirectory) || selectedFolders.Count == 0)
            return [];

        var removed = new List<InstalledWorkshopLevel>();
        foreach (var directory in Directory.EnumerateDirectories(_levelsDirectory, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            var folder = Path.GetFileName(directory);
            if (!selectedFolders.Contains(folder) || !IsSafeDirectFileName(folder) || IsDirectoryLink(directory))
                continue;
            var marker = ReadMarker(directory);
            if (marker == null)
                continue;

            var chart = marker.Files.FirstOrDefault(file => IsSafeDirectFileName(file)
                && file.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            RemoveMarkedFiles(directory, marker);
            removed.Add(new InstalledWorkshopLevel(marker.PublishedFileId, marker.Title, folder, chart));
        }

        return removed;
    }

    public void KeepItemWithoutSubscription(string publishedFileId)
    {
        foreach (var (directory, marker) in FindMarkersForItemWithDirectories(publishedFileId))
        {
            if (marker.KeepWithoutSubscription)
                continue;
            marker.KeepWithoutSubscription = true;
            WriteMarkerAtomically(directory, marker);
        }
    }

    private static string GetSteamFolderBase(string publishedFileId)
        => SanitizeFolderName($"steam_{publishedFileId}");

    private static void RemoveMarkedFiles(string directory, WorkshopInstallMarker marker)
    {
        foreach (var file in marker.Files.Distinct(StringComparer.Ordinal))
        {
            if (!IsSafeDirectFileName(file))
                continue;
            var ownedPath = Path.Combine(directory, file);
            if (File.Exists(ownedPath))
                File.Delete(ownedPath);
        }

        var markerPath = Path.Combine(directory, MarkerFileName);
        if (File.Exists(markerPath))
            File.Delete(markerPath);
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static async Task FlattenContentDirectoryAsync(string sourceDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var sourceRootPrefix = sourceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? sourceRoot
            : sourceRoot + Path.DirectorySeparatorChar;
        var pendingDirectories = new Stack<string>();
        var sourceFiles = new List<(string Path, string RelativePath)>();
        pendingDirectories.Push(sourceRoot);
        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(currentDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Steam CDN 下载目录包含符号链接，已拒绝安装。");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(path);
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(sourceRootPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Steam CDN 返回了下载目录之外的文件路径。");
                var relative = Path.GetRelativePath(sourceRoot, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                ValidateArchiveEntryPath(relative);
                sourceFiles.Add((fullPath, relative));
                if (sourceFiles.Count > MaxArchiveEntries)
                    throw new InvalidDataException("Steam 工坊项目中的文件数量超过限制。");
            }
        }

        long expandedBytes = 0;
        var outputCount = 0;
        var expandArchives = !sourceFiles.Any(file => file.Path.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase));
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourcePath, relativePath) in sourceFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expandArchives && IsZipFile(sourcePath))
            {
                using var archive = ZipFile.OpenRead(sourcePath);
                if (archive.Entries.Count > MaxArchiveEntries)
                    throw new InvalidDataException("工坊压缩包文件数超过限制。");
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var normalized = entry.FullName.Replace('\\', '/');
                    ValidateArchiveEntryPath(normalized);
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("工坊压缩包包含符号链接。");
                    if (normalized.EndsWith('/')) continue;
                    if (++outputCount > MaxArchiveEntries || entry.Length > MaxExpandedBytes - expandedBytes)
                        throw new InvalidDataException("工坊压缩包展开后超过文件数或 1 GiB 大小限制。");
                    var name = MakeUniqueFileName(SafeFileName(Path.GetFileName(normalized)), relativePath + "!/" + normalized, outputNames);
                    await using var input = entry.Open();
                    await using var output = new FileStream(Path.Combine(outputDirectory, name), FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 65536, useAsync: true);
                    var buffer = new byte[65536];
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        expandedBytes = checked(expandedBytes + count);
                        if (expandedBytes > MaxExpandedBytes)
                            throw new InvalidDataException("工坊压缩包展开后超过 1 GiB 限制。");
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                }
                continue;
            }
            var fileInfo = new FileInfo(sourcePath);
            expandedBytes = checked(expandedBytes + fileInfo.Length);
            if (++outputCount > MaxArchiveEntries || expandedBytes > MaxExpandedBytes)
                throw new InvalidDataException("Steam 工坊内容超过 1 GiB 展开限制。");

            var baseName = SafeFileName(Path.GetFileName(relativePath));
            var outputName = MakeUniqueFileName(baseName, relativePath, outputNames);
            var destination = Path.Combine(outputDirectory, outputName);
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(sourcePath, destination, overwrite: false);
        }

    }

    private static bool IsZipFile(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 4 && header[0] == 'P' && header[1] == 'K'
            && ((header[2] == 3 && header[3] == 4) || (header[2] == 5 && header[3] == 6));
    }

    private static string MakeUniqueFileName(string baseName, string archivePath, HashSet<string> usedNames)
    {
        if (usedNames.Add(baseName))
            return baseName;

        var extension = Path.GetExtension(baseName);
        var stem = Path.GetFileNameWithoutExtension(baseName);
        var pathToken = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(archivePath)))[..8];
        var candidate = SafeFileName($"{stem}_{pathToken}{extension}");
        var suffix = 2;
        while (!usedNames.Add(candidate))
            candidate = SafeFileName($"{stem}_{pathToken}_{suffix++}{extension}");
        return candidate;
    }

    private static void ValidateArchiveEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(path) || path.Contains(':'))
            throw new InvalidDataException("工坊文件路径是绝对路径。");
        if (path.Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException("工坊文件路径包含路径穿越条目。");
    }

    private string ChooseFolderName(string publishedFileId)
    {
        var steamFolderBase = GetSteamFolderBase(publishedFileId);
        var candidate = steamFolderBase;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(_levelsDirectory, candidate)))
        {
            var marker = ReadMarker(Path.Combine(_levelsDirectory, candidate));
            if (marker?.PublishedFileId == publishedFileId)
                return candidate;

            candidate = SanitizeFolderName($"{steamFolderBase}_{suffix++}");
        }
        return candidate;
    }

    private void CommitFiles(string targetDirectory, string publishedFileId, IReadOnlyList<string> stagedFiles, WorkshopInstallMarker marker)
    {
        if (Directory.Exists(targetDirectory) && IsDirectoryLink(targetDirectory))
            throw new IOException("目标谱面目录是符号链接，拒绝写入。");
        var existedBefore = Directory.Exists(targetDirectory);
        Directory.CreateDirectory(targetDirectory);
        var oldMarker = ReadMarker(targetDirectory);
        if (oldMarker != null && oldMarker.PublishedFileId != publishedFileId)
            throw new IOException("目标目录属于另一个工坊项目，拒绝覆盖。");
        if (existedBefore && oldMarker == null && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
            throw new IOException("目标目录已存在且不属于本 Mod，拒绝覆盖。");

        var previousOwned = oldMarker?.Files.Where(IsSafeDirectFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newNames = marker.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rollbackDirectory = Path.Combine(_scratchDirectory, $"rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rollbackDirectory);
        var priorFiles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? markerBackup = null;
        var markerPath = Path.Combine(targetDirectory, MarkerFileName);
        var markerWasPresent = File.Exists(markerPath);
        var staleOwned = previousOwned.Where(name => !newNames.Contains(name)).ToArray();

        try
        {
            foreach (var stagedPath in stagedFiles)
            {
                var name = Path.GetFileName(stagedPath)!;
                if (!IsSafeDirectFileName(name))
            throw new InvalidDataException("工坊项目包含无效文件名。");

                var destination = Path.Combine(targetDirectory, name);
                if (File.Exists(destination) && !previousOwned.Contains(name))
                    throw new IOException($"目标目录中已存在未由本 Mod 管理的文件：{name}");

                if (File.Exists(destination) && !priorFiles.ContainsKey(name))
                {
                    var backup = Path.Combine(rollbackDirectory, name);
                    File.Copy(destination, backup, overwrite: false);
                    priorFiles[name] = backup;
                }
                else if (!File.Exists(destination))
                {
                    priorFiles.TryAdd(name, null);
                }

                var replacement = Path.Combine(targetDirectory, $".steam-workshop-{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(stagedPath, replacement, overwrite: false);
                    File.Move(replacement, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(replacement))
                        File.Delete(replacement);
                }
            }

            if (markerWasPresent)
            {
                markerBackup = Path.Combine(rollbackDirectory, MarkerFileName);
                File.Copy(markerPath, markerBackup, overwrite: false);
            }
            WriteMarkerAtomically(targetDirectory, marker);
        }
        catch
        {
            foreach (var (name, backupPath) in priorFiles)
            {
                var destination = Path.Combine(targetDirectory, name);
                if (backupPath == null)
                {
                    if (File.Exists(destination))
                        File.Delete(destination);
                }
                else if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, destination, overwrite: true);
                }
            }

            if (markerBackup != null && File.Exists(markerBackup))
                File.Copy(markerBackup, markerPath, overwrite: true);
            else if (!markerWasPresent && File.Exists(markerPath))
                File.Delete(markerPath);

            if (!existedBefore && Directory.Exists(targetDirectory) && !Directory.EnumerateFileSystemEntries(targetDirectory).Any())
                Directory.Delete(targetDirectory);
            throw;
        }
        finally
        {
            TryDeleteDirectory(rollbackDirectory);
        }

        foreach (var staleName in staleOwned)
        {
            try
            {
                var stalePath = Path.Combine(targetDirectory, staleName);
                if (File.Exists(stalePath))
                    File.Delete(stalePath);
            }
            catch { }
        }
    }

    private static void WriteMarkerAtomically(string directory, WorkshopInstallMarker marker)
    {
        var markerPath = Path.Combine(directory, MarkerFileName);
        var temporaryPath = Path.Combine(directory, $"{MarkerFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(marker, JsonOptions));
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private List<(string Directory, WorkshopInstallMarker Marker)> FindMarkersForItemWithDirectories(string publishedFileId)
    {
        if (!Directory.Exists(_levelsDirectory))
            return [];

        var markers = new List<(string Directory, WorkshopInstallMarker Marker)>();
        foreach (var directory in Directory.EnumerateDirectories(_levelsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsDirectoryLink(directory))
                continue;
            var marker = ReadMarker(directory);
            if (marker?.PublishedFileId == publishedFileId)
                markers.Add((directory, marker));
        }
        return markers;
    }

    private static bool IsDirectoryLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget != null; }
        catch { return true; }
    }

    private static WorkshopInstallMarker? ReadMarker(string directory)
    {
        var path = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            var marker = JsonSerializer.Deserialize<WorkshopInstallMarker>(File.ReadAllText(path));
            if (marker?.SchemaVersion != 1 || !ulong.TryParse(marker.PublishedFileId, out _))
                return null;
            marker.Files ??= [];
            return marker;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeDirectFileName(string name)
        => !string.IsNullOrWhiteSpace(name)
            && name != MarkerFileName
            && Path.GetFileName(name) == name
            && !name.Contains('/')
            && !name.Contains('\\')
            && name is not "." and not "..";

    private static string SafeFileName(string name)
    {
        var baseName = Path.GetFileName(name.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(baseName.Where(character => !invalid.Contains(character) && !char.IsControl(character)).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            throw new InvalidDataException("工坊压缩包包含无效文件名。");
        if (safe.Length > 180)
            safe = safe[..150] + "_" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(safe)))[..16] + Path.GetExtension(safe);
        return safe;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name.Where(character => !invalid.Contains(character) && !char.IsControl(character)).ToArray())
            .Replace('/', '_')
            .Replace('\\', '_')
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            safe = "WorkshopLevel";
        return safe.Length <= 80 ? safe : safe[..80].TrimEnd('.');
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
