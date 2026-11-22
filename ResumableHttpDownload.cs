using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SteamWorkshopManager;

internal static class ResumableHttpDownload
{
    private const int BlockSize = 1024 * 1024;
    private const long MaximumBytes = 512L * BlockSize;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(45) };

    internal static async Task DownloadAsync(string address, string fileName, long expectedSize,
        WorkshopDownloadWorkspace workspace, Action<double, string>? progress, CancellationToken token)
    {
        double lastProgress = 0;
        await DownloadNetworkRetry.RunAsync(() => DownloadAttemptAsync(address, fileName, expectedSize, workspace,
            (value, message) => { lastProgress = value; progress?.Invoke(value, message); }, token),
            "HTTP 工坊文件下载", message => progress?.Invoke(lastProgress, message), token).ConfigureAwait(false);
    }

    private static async Task DownloadAttemptAsync(string address, string fileName, long expectedSize,
        WorkshopDownloadWorkspace workspace, Action<double, string>? progress, CancellationToken token)
    {
        if (expectedSize < 0 || expectedSize > MaximumBytes) throw new InvalidDataException("Steam 工坊文件大小无效。");
        var uri = ValidateUri(address);
        var resource = WorkshopDownloadWorkspace.Hash(uri.GetLeftPart(UriPartial.Path));
        var path = workspace.PayloadPath(fileName);
        var statePath = workspace.StatePath("http.json");
        HttpState? saved = null;
        try { saved = DownloadCheckpoint.Read<HttpState>(statePath, 131072); } catch { }
        if (saved is not { Format: 1 } || saved.Resource != resource || saved.ExpectedSize != expectedSize
            || saved.Blocks == null || saved.Blocks.Count > 512 || saved.TailLength is < 0 or >= BlockSize
            || saved.TotalLength is < 0 or > MaximumBytes)
            saved = new HttpState { Resource = resource, ExpectedSize = expectedSize };
        var state = saved;
        if (!ValidEtag(state.ETag)) state.ETag = null;
        await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            65536, FileOptions.Asynchronous);
        var verified = await VerifyPrefixAsync(output, state, token).ConfigureAwait(false);
        output.SetLength(verified);
        output.Position = verified;
        if ((state.Complete || state.TotalLength > 0 && verified == state.TotalLength) && verified == state.TotalLength)
        {
            output.Position = 0;
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(output, token).ConfigureAwait(false));
            if (!state.Complete || string.Equals(state.FileHash, digest, StringComparison.OrdinalIgnoreCase))
            {
                state.Complete = true;
                state.FileHash = digest;
                DownloadCheckpoint.Write(statePath, state);
                progress?.Invoke(.7, "已复用完整的工坊文件。");
                return;
            }
            Reset(output, state);
            verified = 0;
        }
        if (verified > 0 && !CanResume(state)) { Reset(output, state); verified = 0; }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var response = await GetAsync(uri, verified, state, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && verified > 0)
            {
                Reset(output, state); verified = 0; DownloadCheckpoint.Write(statePath, state); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != verified || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                    || range.To == null || range.To < verified || range.Length == null || range.To >= range.Length
                    || response.Content.Headers.ContentLength is { } responseLength && responseLength != range.To - range.From + 1
                    || range.Length > MaximumBytes || expectedSize > 0 && range.Length != expectedSize
                    || verified > 0 && (state.TotalLength > 0 && state.TotalLength != range.Length || !ValidatorsMatch(state, response)))
                {
                    Reset(output, state); verified = 0; DownloadCheckpoint.Write(statePath, state); continue;
                }
                state.TotalLength = range.Length.Value;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                // A full response to Range means the server cannot reuse this prefix.
                Reset(output, state);
                verified = 0;
                state.TotalLength = response.Content.Headers.ContentLength ?? Math.Max(0, expectedSize);
            }
            else throw new IOException("Steam 文件服务器返回了不支持的下载响应。");
            if (state.TotalLength > MaximumBytes || expectedSize > 0 && state.TotalLength > 0 && state.TotalLength != expectedSize)
                throw new InvalidDataException("Steam 工坊文件大小不匹配或超过 512 MiB。");
            if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Steam 文件服务器返回了不支持的内容编码。");
            state.ETag = response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;
            state.LastModified = response.Content.Headers.LastModified;
            state.Complete = false;
            state.FileHash = null;
            DownloadCheckpoint.Write(statePath, state);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await CopyAsync(input, output, state, statePath, verified, progress, token).ConfigureAwait(false);
            return;
        }
        throw new IOException("服务器无法校验已有下载数据；已重置该 HTTP 文件，重试后重新下载。");
    }

    private static async Task<long> VerifyPrefixAsync(FileStream file, HttpState state, CancellationToken token)
    {
        var buffer = new byte[BlockSize];
        long verified = 0;
        var validBlocks = 0;
        foreach (var expected in state.Blocks)
        {
            token.ThrowIfCancellationRequested();
            if (file.Length - verified < BlockSize || !ValidHash(expected)) break;
            file.Position = verified;
            await file.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            if (!SHA256.HashData(buffer).AsSpan().SequenceEqual(Convert.FromHexString(expected))) break;
            verified += BlockSize;
            validBlocks++;
        }
        if (validBlocks != state.Blocks.Count)
        {
            state.Blocks = state.Blocks.Take(validBlocks).ToList();
            state.TailLength = 0; state.TailHash = null; state.Complete = false;
        }
        if (state.TailLength > 0)
        {
            var tail = buffer.AsMemory(0, state.TailLength);
            var valid = file.Length - verified >= state.TailLength && ValidHash(state.TailHash);
            if (valid)
            {
                file.Position = verified;
                await file.ReadExactlyAsync(tail, token).ConfigureAwait(false);
                valid = SHA256.HashData(tail.Span).AsSpan().SequenceEqual(Convert.FromHexString(state.TailHash!));
            }
            if (valid) verified += state.TailLength;
            else { state.TailLength = 0; state.TailHash = null; state.Complete = false; }
        }
        if (state.TotalLength > 0 && verified > state.TotalLength)
        {
            state.Blocks.Clear(); state.TailLength = 0; state.TailHash = null; state.Complete = false;
            return 0;
        }
        return verified;
    }

    private static async Task CopyAsync(Stream input, FileStream output, HttpState state, string statePath,
        long written, Action<double, string>? progress, CancellationToken token)
    {
        using var blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (state.TailLength > 0)
        {
            var tail = new byte[state.TailLength];
            output.Position = written - tail.Length;
            await output.ReadExactlyAsync(tail, token).ConfigureAwait(false);
            blockHash.AppendData(tail);
        }
        output.Position = written;
        var buffer = new byte[65536];
        var lastCheckpoint = DateTimeOffset.UtcNow;
        void Checkpoint()
        {
            output.Flush(flushToDisk: true);
            state.TailHash = state.TailLength == 0 ? null : Convert.ToHexString(blockHash.GetCurrentHash());
            DownloadCheckpoint.Write(statePath, state);
            lastCheckpoint = DateTimeOffset.UtcNow;
        }
        try
        {
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var count = await DownloadNetworkRetry.ObserveAsync(
                    () => input.ReadAsync(buffer, readTimeout.Token).AsTask(), "HTTP 数据读取", token).ConfigureAwait(false);
                if (count == 0) break;
                var offset = 0;
                while (offset < count)
                {
                    var length = Math.Min(count - offset, BlockSize - state.TailLength);
                    if (written + length > MaximumBytes || state.TotalLength > 0 && written + length > state.TotalLength)
                        throw new InvalidDataException("Steam 文件超过声明大小或下载限制。");
                    await output.WriteAsync(buffer.AsMemory(offset, length), token).ConfigureAwait(false);
                    blockHash.AppendData(buffer.AsSpan(offset, length));
                    state.TailLength += length;
                    written += length;
                    offset += length;
                    if (state.TailLength == BlockSize)
                    {
                        state.Blocks.Add(Convert.ToHexString(blockHash.GetHashAndReset()));
                        state.TailLength = 0;
                        Checkpoint();
                    }
                }
                if (DateTimeOffset.UtcNow - lastCheckpoint >= TimeSpan.FromSeconds(1)) Checkpoint();
                var total = state.TotalLength > 0 ? state.TotalLength : state.ExpectedSize;
                progress?.Invoke(total > 0 ? Math.Clamp(written / (double)total * .7, 0, .7) : 0,
                    $"HTTP 下载：{written / 1048576d:F1} MiB，已保留断点。");
            }
            if (state.TotalLength > 0 && written != state.TotalLength || state.ExpectedSize > 0 && written != state.ExpectedSize)
                throw new DownloadTransportException("HTTP 数据读取",
                    new EndOfStreamException("Steam 工坊文件尚未完整，已保留下载断点。"));
            Checkpoint();
            state.TotalLength = written;
            output.Position = 0;
            state.FileHash = Convert.ToHexString(await SHA256.HashDataAsync(output, token).ConfigureAwait(false));
            state.Complete = true;
            DownloadCheckpoint.Write(statePath, state);
            progress?.Invoke(.7, "工坊文件下载和校验完成。");
        }
        catch
        {
            // Do not record a partially completed WriteAsync operation as durable bytes.
            try { output.SetLength(written); Checkpoint(); } catch { }
            throw;
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(Uri uri, long offset, HttpState state, CancellationToken token)
    {
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = !string.IsNullOrWhiteSpace(state.ETag)
                    ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(state.ETag))
                    : new RangeConditionHeaderValue(state.LastModified!.Value);
            }
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null || redirect == 3) throw new IOException("Steam 文件服务器重定向无效。");
            uri = ValidateUri(new Uri(uri, location).AbsoluteUri);
        }
        throw new IOException("Steam 文件地址无效。");
    }

    private static bool CanResume(HttpState state) => ValidEtag(state.ETag) || state.LastModified.HasValue;
    private static bool ValidEtag(string? value) => EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak;
    private static bool ValidatorsMatch(HttpState state, HttpResponseMessage response)
        => !string.IsNullOrWhiteSpace(state.ETag)
            ? response.Headers.ETag?.ToString() == state.ETag
            : response.Content.Headers.LastModified == state.LastModified;
    private static bool ValidHash(string? hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);

    private static void Reset(FileStream output, HttpState state)
    {
        output.SetLength(0); output.Position = 0;
        state.Blocks.Clear(); state.TailLength = 0; state.TailHash = null;
        state.TotalLength = 0; state.ETag = null; state.LastModified = null; state.Complete = false; state.FileHash = null;
    }

    internal static Uri ValidateUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort
            || !(uri.IdnHost.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("工坊文件地址不是受支持的 Steam HTTPS 内容地址。");
        return uri;
    }

    private sealed class HttpState
    {
        public int Format { get; set; } = 1;
        public string Resource { get; set; } = string.Empty;
        public long ExpectedSize { get; set; }
        public long TotalLength { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? LastModified { get; set; }
        public List<string> Blocks { get; set; } = [];
        public int TailLength { get; set; }
        public string? TailHash { get; set; }
        public bool Complete { get; set; }
        public string? FileHash { get; set; }
    }
}
