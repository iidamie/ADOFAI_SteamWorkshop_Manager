using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamWorkshopManager;

internal sealed class WorkshopDownloadWorkspace
{
    private readonly string _root;
    public string ContentDirectory { get; }
    public string StateDirectory { get; }

    private WorkshopDownloadWorkspace(string root)
    {
        _root = root;
        ContentDirectory = Path.Combine(root, "content");
        StateDirectory = Path.Combine(root, "transfer");
    }

    internal static string AccountScope(ulong steamId)
    {
        if (steamId == 0) throw new InvalidOperationException("请先登录 Steam。");
        return Hash(steamId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static WorkshopDownloadWorkspace Open(string pluginDirectory, string accountScope, WorkshopItem item)
    {
        if (accountScope.Length != 64 || !accountScope.All(Uri.IsHexDigit)
            || !ulong.TryParse(item.PublishedFileId, out var id) || id == 0)
            throw new InvalidDataException("下载工作区身份无效。");
        var root = Path.Combine(pluginDirectory, "workshop-cache");
        DownloadCheckpoint.Directory(root);
        foreach (var segment in new[] { "downloads", accountScope, item.PublishedFileId })
        {
            root = Path.Combine(root, segment);
            DownloadCheckpoint.Directory(root);
        }
        var workspace = new WorkshopDownloadWorkspace(root);
        var versionPath = Path.Combine(root, "version.json");
        var identity = new CacheVersion(1, item.PublishedFileId, item.TimeUpdated, item.FileSize, item.ContentHandle);
        CacheVersion? existing = null;
        try { existing = DownloadCheckpoint.Read<CacheVersion>(versionPath, 8192); } catch { }
        if (existing != identity)
        {
            // Only this version-pinned download cache is invalidated; Levels is untouched.
            System.IO.Directory.Delete(root, recursive: true);
            DownloadCheckpoint.Directory(root);
            DownloadCheckpoint.Write(versionPath, identity);
        }
        DownloadCheckpoint.Directory(workspace.ContentDirectory);
        DownloadCheckpoint.Directory(workspace.StateDirectory);
        return workspace;
    }

    internal string PayloadPath(string relative) => Resolve(ContentDirectory, relative);
    internal string StatePath(string relative) => Resolve(StateDirectory, relative);

    private static string Resolve(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':') || parts.Any(part => string.IsNullOrWhiteSpace(part)
            || part is "." or ".." || part.Any(char.IsControl)))
            throw new InvalidDataException("Steam 下载文件路径无效。");
        var current = root;
        foreach (var part in parts[..^1])
        {
            current = Path.Combine(current, part);
            DownloadCheckpoint.Directory(current);
        }
        var path = Path.Combine(current, parts[^1]);
        DownloadCheckpoint.CheckFile(path);
        return path;
    }

    internal void Complete()
    {
        try { System.IO.Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record CacheVersion(int Format, string PublishedFileId, long TimeUpdated, long FileSize, ulong ContentHandle);
}

internal static class DownloadCheckpoint
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static void Directory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget != null)
            throw new IOException("下载缓存目录不能是符号链接。");
        System.IO.Directory.CreateDirectory(path);
    }

    internal static void CheckFile(string path)
    {
        if (new FileInfo(path).LinkTarget != null || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("下载缓存文件不能是符号链接。");
    }

    internal static T? Read<T>(string path, long maximum)
    {
        CheckFile(path);
        if (!File.Exists(path)) return default;
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException("下载记录超过大小限制。");
        return JsonSerializer.Deserialize<T>(stream, Json);
    }

    internal static void Write<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    internal static void WriteBytes(string path, byte[] bytes)
    {
        Directory(Path.GetDirectoryName(path)!);
        CheckFile(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
