using System.Security.Cryptography;
using SteamKit2;
using SteamKitSDK.ContentDelivery.Models;
using SteamKitSDK.ContentDelivery.Models.Downloading;
using ManifestFile = SteamKitSDK.ContentDelivery.Models.ManifestFile;

namespace SteamWorkshopManager;

internal static class ResumableManifestDownload
{
    public const int MaximumParallelChunks = 8;
    public const int MaximumGlobalChunks = 12;
    private static readonly SemaphoreSlim ChunkSlots = new(MaximumGlobalChunks, MaximumGlobalChunks);
    internal static async Task DownloadAsync(WorkshopCdnTransport cdn, DefaultDownloadHandler handler, byte[] depotKey,
        WorkshopDownloadWorkspace workspace, Action<double, string>? progress, CancellationToken token, int parallelChunks)
    {
        var files = handler.Files.Where(file => ((int)file.Flags & 64) == 0).ToArray();
        if (files.Length > 4096 || files.Sum(file => file.ChunkHeaders.Count) > 65536
            || files.Sum(file => checked((long)file.TotalSize)) > 512L * 1024 * 1024)
            throw new InvalidDataException("Steam 工坊内容超过下载限制。");
        var total = files.Sum(file => checked((long)file.TotalSize));
        long completed = 0;
        var jobs = new List<ChunkJob>();
        var gates = new List<SemaphoreSlim>();
        var targets = new List<FileTarget>();
        var progressGate = new object();
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (((int)file.Flags & 512) != 0 || file.FileHash.Length != 20)
                    throw new InvalidDataException("Steam manifest 包含不支持的文件类型或校验值。");
                var path = workspace.PayloadPath(file.FileName);
                var receiptDirectory = Path.Combine("chunks", WorkshopDownloadWorkspace.Hash(file.FileName));
                var gate = new SemaphoreSlim(1, 1);
                gates.Add(gate);
                var target = new FileTarget(file, path, receiptDirectory, gate);
                if (await IsCompleteAsync(path, file, token).ConfigureAwait(false))
                {
                    completed += checked((long)file.TotalSize);
                    progress?.Invoke(Fraction(completed, total), $"已复用完整文件：{file.FileName}");
                    continue;
                }
                targets.Add(target);
                long expectedOffset = 0;
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    stream.SetLength(checked((long)file.TotalSize));
                    foreach (var chunk in file.ChunkHeaders.OrderBy(chunk => chunk.Offset))
                    {
                        token.ThrowIfCancellationRequested();
                        if (chunk.Id.Length != 20 || chunk.UncompressedLength is 0 or > 8 * 1024 * 1024
                            || chunk.Offset != (ulong)expectedOffset || chunk.UncompressedLength > file.TotalSize - (ulong)expectedOffset)
                            throw new InvalidDataException("Steam manifest 分块范围无效。");
                        expectedOffset = checked(expectedOffset + chunk.UncompressedLength);
                        var receipt = workspace.StatePath($"{receiptDirectory}/{chunk.Offset}-{Convert.ToHexString(chunk.Id)}.sha256");
                        var valid = false;
                        try
                        {
                            if (File.Exists(receipt) && new FileInfo(receipt).Length == 32)
                            {
                                var expected = await File.ReadAllBytesAsync(receipt, token).ConfigureAwait(false);
                                var buffer = new byte[checked((int)chunk.UncompressedLength)];
                                stream.Position = checked((long)chunk.Offset);
                                await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
                                valid = CryptographicOperations.FixedTimeEquals(SHA256.HashData(buffer), expected);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (IOException) { }
                        if (valid) completed += chunk.UncompressedLength;
                        else jobs.Add(new ChunkJob(target, chunk, receipt));
                    }
                    if ((ulong)expectedOffset != file.TotalSize) throw new InvalidDataException("Steam manifest 分块未覆盖完整文件。");
                    stream.Flush(flushToDisk: true);
                }
                progress?.Invoke(Fraction(completed, total), $"已校验下载断点：{file.FileName}");
            }
            if (jobs.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                // ForEachAsync waits for every in-flight chunk before transport/file teardown.
                await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallelChunks, 1, MaximumParallelChunks), CancellationToken = token },
                    async (job, cancellation) =>
                    {
                        await ChunkSlots.WaitAsync(cancellation).ConfigureAwait(false);
                        try
                        {
                            var bytes = await DownloadChunkAsync(cdn, handler, job.Chunk, depotKey, message =>
                            {
                                lock (progressGate) progress?.Invoke(Fraction(completed, total), message);
                            }, cancellation).ConfigureAwait(false);
                            cancellation.ThrowIfCancellationRequested();
                            await job.Target.Gate.WaitAsync(cancellation).ConfigureAwait(false);
                            try
                            {
                                using var output = new FileStream(job.Target.Path, FileMode.Open, FileAccess.Write, FileShare.Read);
                                output.Position = checked((long)job.Chunk.Offset);
                                await output.WriteAsync(bytes, cancellation).ConfigureAwait(false);
                                output.Flush(flushToDisk: true);
                                // The receipt is committed only after the actual chunk reaches durable storage.
                                DownloadCheckpoint.WriteBytes(job.Receipt, SHA256.HashData(bytes));
                            }
                            finally { job.Target.Gate.Release(); }
                            lock (progressGate)
                            {
                                completed += bytes.Length;
                                progress?.Invoke(Fraction(completed, total), $"下载分块：{job.Target.File.FileName} · {completed / 1048576d:F1}/{total / 1048576d:F1} MiB");
                            }
                        }
                        finally { ChunkSlots.Release(); }
                    }).ConfigureAwait(false);
            }
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                if (await IsCompleteAsync(target.Path, target.File, token).ConfigureAwait(false)) continue;
                // A bad whole-file hash must not leave receipts that repeatedly reuse the same bad data.
                var receipts = Path.GetDirectoryName(workspace.StatePath(target.ReceiptDirectory + "/placeholder"))!;
                try { Directory.Delete(receipts, recursive: true); } catch { }
                try { File.Delete(target.Path); } catch { }
                throw new InvalidDataException($"文件校验失败，将在重试时重新下载：{target.File.FileName}");
            }
            progress?.Invoke(.7, $"Steam CDN 下载和校验完成，共 {files.Length} 个文件。");
        }
        finally { foreach (var gate in gates) gate.Dispose(); }
    }

    private static double Fraction(long completed, long total) => total <= 0 ? .7 : Math.Clamp(completed / (double)total * .7, 0, .7);

    private static async Task<bool> IsCompleteAsync(string path, ManifestFile file, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != checked((long)file.TotalSize)) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var hash = await SHA1.HashDataAsync(input, token).ConfigureAwait(false);
        return hash.AsSpan().SequenceEqual(file.FileHash);
    }

    private static Task<byte[]> DownloadChunkAsync(WorkshopCdnTransport cdn, DefaultDownloadHandler handler,
        ManifestFileChunkHeader chunk, byte[] depotKey, Action<string>? retryReport, CancellationToken token)
        => cdn.DownloadChunkAsync(handler.DepotId, new SteamKit2.DepotManifest.ChunkData
        {
            ChunkID = chunk.Id, Checksum = chunk.Checksum, Offset = chunk.Offset,
            CompressedLength = chunk.CompressedLength, UncompressedLength = chunk.UncompressedLength,
        }, depotKey, retryReport, token);

    private sealed record FileTarget(ManifestFile File, string Path, string ReceiptDirectory, SemaphoreSlim Gate);
    private sealed record ChunkJob(FileTarget Target, ManifestFileChunkHeader Chunk, string Receipt);
}
