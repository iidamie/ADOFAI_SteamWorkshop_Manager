using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamWorkshopManager;

internal static class ModUpdatePackage
{
    internal const string ModId = "SteamWorkshopManager";
    internal const string PackageRoot = ModId + "/";
    internal const long MaxPackageBytes = 64L * 1024 * 1024;
    private const long MaxFileBytes = 32L * 1024 * 1024;
    private const long MaxExpandedBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] RequiredFiles =
    [
        "SteamWorkshopManager.dll", "SteamWorkshopManager.deps.json", "SteamKit2.dll", "SteamKitSDK.dll", "StbImageSharp.dll",
    ];

    internal static bool TryVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var normalized = text.Trim().TrimStart('v', 'V');
        if (!Regex.IsMatch(normalized, @"^\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)) || !Version.TryParse(normalized, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    internal static async Task<IReadOnlyList<string>> ExtractAsync(string archivePath, string stagingDirectory,
        string expectedVersion, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 2 or > 128) throw new InvalidDataException("更新包文件数量无效。");
        var manifestEntry = archive.GetEntry(PackageRoot + "update-manifest.json")
            ?? throw new InvalidDataException("更新包缺少清单。");
        if (manifestEntry.Length > 65536) throw new InvalidDataException("更新清单过大。");
        byte[] manifestBytes;
        await using (var input = manifestEntry.Open())
        using (var output = new MemoryStream())
        {
            await CopyLimitedAsync(input, output, 65536, token).ConfigureAwait(false);
            manifestBytes = output.ToArray();
        }
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("更新清单无效。");
        if (manifest.Format != 1 || manifest.Id != ModId || !TryVersion(manifest.Version, out var packageVersion)
            || !TryVersion(expectedVersion, out var expected) || packageVersion != expected)
            throw new InvalidDataException("更新包的 Mod 名称或版本不匹配。");
        if (manifest.Files.Count is < 5 or > 120) throw new InvalidDataException("更新文件清单无效。");
        var files = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (!AllowedPath(file.Path) || !files.TryAdd(file.Path, file) || file.Size is < 0 or > MaxFileBytes
                || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("更新清单含有无效文件。");
            total = checked(total + file.Size);
            if (total > MaxExpandedBytes) throw new InvalidDataException("更新包展开后过大。");
        }
        if (RequiredFiles.Any(file => !files.ContainsKey(file))) throw new InvalidDataException("更新包缺少运行依赖。");
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(PackageRoot, StringComparison.Ordinal) || entry.FullName.Contains('\\')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("更新包路径或文件类型无效。");
            if (entry.FullName.EndsWith('/'))
            {
                if (entry.FullName != PackageRoot && entry.FullName != PackageRoot + "licenses/")
                    throw new InvalidDataException("更新包含有额外目录。");
                continue;
            }
            if (!entryNames.Add(entry.FullName)) throw new InvalidDataException("更新包包含重复路径。");
            var relative = entry.FullName[PackageRoot.Length..];
            if (relative != "update-manifest.json" && !files.ContainsKey(relative))
                throw new InvalidDataException("更新包含有清单之外的文件。");
        }
        Directory.CreateDirectory(stagingDirectory);
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(PackageRoot + file.Path)
                ?? throw new InvalidDataException($"更新文件缺失：{file.Path}");
            if (entry.Length != file.Size) throw new InvalidDataException($"更新文件大小不匹配：{file.Path}");
            var destination = Path.Combine(stagingDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
                await CopyLimitedAsync(input, output, file.Size, token).ConfigureAwait(false);
            if (new FileInfo(destination).Length != file.Size) throw new InvalidDataException("更新文件长度不匹配。");
            await using var verification = File.OpenRead(destination);
            var digest = await SHA256.HashDataAsync(verification, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException($"更新文件校验失败：{file.Path}");
            if (file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                _ = AssemblyName.GetAssemblyName(destination);
        }
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(stagingDirectory, ModId + ".dll"));
        if (assembly.Name != ModId || assembly.Version != packageVersion)
            throw new InvalidDataException("更新程序集名称或版本不匹配。");
        return manifest.Files.Select(file => file.Path).ToArray();
    }

    private static bool AllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 180 || path.Contains('\\') || path.Contains(':')
            || path.Split('/').Any(part => part is "" or "." or "..")) return false;
        if (path.StartsWith("licenses/", StringComparison.Ordinal))
            return path.Count(character => character == '/') == 1
                && Regex.IsMatch(path[9..], @"^[A-Za-z0-9_.-]+\.(txt|md)$", RegexOptions.CultureInvariant);
        if (path.Contains('/')) return false;
        if (path is "README.md" or "THIRD-PARTY-NOTICES.md" or "SteamWorkshopManager.deps.json") return true;
        return Regex.IsMatch(path, @"^[A-Za-z0-9_.-]+\.dll$", RegexOptions.CultureInvariant)
            && !path.StartsWith("StArray.", StringComparison.OrdinalIgnoreCase)
            && !path.Equals("ImGui.NET.dll", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Install(string stagingDirectory, string modDirectory, IReadOnlyList<string> files)
    {
        var backup = Path.Combine(modDirectory, ".update-backup-" + Guid.NewGuid().ToString("N"));
        var committed = new List<(string Target, string Backup, bool Existed)>();
        var preserveBackup = false;
        Directory.CreateDirectory(backup);
        try
        {
            var plan = files.OrderBy(file => file == ModId + ".dll" ? 1 : 0).Select(relative =>
            {
                if (!AllowedPath(relative)) throw new InvalidDataException("更新目标路径无效。");
                var target = Path.Combine(modDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                var saved = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
                var staged = Path.Combine(stagingDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                var existed = File.Exists(target);
                if (existed && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("更新目标不能是符号链接。");
                var parent = Path.GetDirectoryName(target)!;
                if (Directory.Exists(parent) && new DirectoryInfo(parent).LinkTarget != null)
                    throw new IOException("更新目录不能是符号链接。");
                return (Target: target, Backup: saved, Staged: staged, Existed: existed);
            }).ToArray();
            foreach (var file in plan)
            {
                if (!file.Existed) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file.Backup)!);
                File.Copy(file.Target, file.Backup, overwrite: false);
            }
            foreach (var file in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                File.Move(file.Staged, file.Target, overwrite: true);
                committed.Add((file.Target, file.Backup, file.Existed));
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var file in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (file.Existed) File.Move(file.Backup, file.Target, overwrite: true);
                    else File.Delete(file.Target);
                }
                catch (Exception exception) { errors.Add(exception); }
            }
            if (errors.Count > 0)
            {
                preserveBackup = true;
                throw new AggregateException("更新恢复未完成，原文件备份已保留。", new[] { failure }.Concat(errors));
            }
            throw;
        }
        finally
        {
            if (!preserveBackup) TryDeleteDirectory(backup);
        }
    }

    internal static async Task<long> CopyLimitedAsync(Stream input, Stream output, long maximum, CancellationToken token,
        Action<long>? report = null)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total > maximum) throw new InvalidDataException("下载或解压文件超过大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            report?.Invoke(total);
        }
        return total;
    }

    internal static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }

    private sealed class UpdateManifest
    {
        public int Format { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public List<UpdateFile> Files { get; set; } = [];
    }
    private sealed class UpdateFile
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
