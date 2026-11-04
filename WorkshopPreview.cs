using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ImGuiNET;
using StbImageSharp;

namespace SteamWorkshopManager;

internal sealed class WorkshopPreview : IDisposable
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private string _source = string.Empty;
    private long _generation;
    private PreviewImage? _image;
    private bool _busy;
    private bool _disposed;
    private string _error = string.Empty;
    private int _releaseRequested;
    private WorkshopPreviewTexture? _texture;
    private bool _uploadAttempted;

    public void Draw(string source, string cacheDirectory, float maxWidth, CancellationToken lifetime)
    {
        UseSource(source, cacheDirectory, lifetime);
        ProcessDeferredRelease();
        PreviewImage? image;
        string error;
        bool busy;
        lock (_gate) { image = _image; error = _error; busy = _busy; }
        if (image == null)
        {
            ImGui.TextUnformatted(busy ? "正在加载预览图…" : string.IsNullOrWhiteSpace(error) ? "暂无预览图。" : error);
            if (!busy && !string.IsNullOrWhiteSpace(error) && ImGui.Button("重试预览图")) Reset();
            return;
        }
        if (_texture != null && !_texture.IsCurrentContext)
        {
            _texture.Dispose();
            _texture = null;
            _uploadAttempted = false;
        }
        if (!_uploadAttempted)
        {
            _uploadAttempted = true;
            _texture = WorkshopPreviewTexture.TryCreate(image.Pixels, image.Width, image.Height);
        }
        var scale = Math.Min(Math.Max(1, maxWidth) / image.Width, Math.Max(120, ImGui.GetIO().DisplaySize.Y * .42f) / image.Height);
        var size = new Vector2(image.Width * scale, image.Height * scale);
        ImGui.BeginChild("##workshop-cover", size, ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        if (_texture != null)
            draw.AddImage(_texture.Id, origin, origin + size, Vector2.Zero, Vector2.One);
        else
        {
            // A separate child keeps this fallback below the 16-bit vertex limit,
            // and supports Vulkan hosts without borrowing an OpenGL texture ID.
            var pixelSize = new Vector2(size.X / image.RasterWidth, size.Y / image.RasterHeight);
            foreach (var run in image.Runs)
                draw.AddRectFilled(origin + new Vector2(run.X * pixelSize.X, run.Y * pixelSize.Y),
                    origin + new Vector2((run.X + run.Length) * pixelSize.X, (run.Y + 1) * pixelSize.Y), run.Color);
        }
        ImGui.Dummy(size);
        ImGui.EndChild();
    }

    private void UseSource(string source, string cacheDirectory, CancellationToken lifetime)
    {
        CancellationTokenSource cancellation;
        long generation;
        lock (_gate)
        {
            if (_disposed || _source == source) return;
            _cancellation?.Cancel();
            _source = source;
            _image = null;
            _error = string.Empty;
            _busy = true;
            generation = ++_generation;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            cancellation.CancelAfter(TimeSpan.FromSeconds(45));
            _cancellation = cancellation;
            Interlocked.Exchange(ref _releaseRequested, 1);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var image = await LoadAsync(source, cacheDirectory, cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                    if (generation == _generation && !cancellation.IsCancellationRequested) _image = image;
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                    if (generation == _generation && !lifetime.IsCancellationRequested && !_disposed)
                        _error = "预览图请求超时，请重试。";
            }
            catch (Exception exception)
            {
                lock (_gate)
                    if (generation == _generation) _error = $"预览图加载失败：{exception.GetBaseException().Message}";
            }
            finally
            {
                lock (_gate)
                    if (generation == _generation) { _busy = false; _cancellation = null; }
                cancellation.Dispose();
            }
        });
    }

    private static async Task<PreviewImage> LoadAsync(string source, string directory, CancellationToken token)
    {
        var uri = ValidateUri(source);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var cachePath = Path.Combine(directory, key + ".img");
        try
        {
            var file = new FileInfo(cachePath);
            if (file.Exists && file.Length is > 0 and <= MaxBytes)
            {
                var bytes = await File.ReadAllBytesAsync(cachePath, token).ConfigureAwait(false);
                var cached = await DecodeBoundedAsync(bytes, token).ConfigureAwait(false);
                try { File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow); } catch { }
                return cached;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Corrupt or inaccessible cache entries can be fetched again. */ }

        byte[]? data = null;
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location == null || redirect == 3) throw new IOException("图片地址重定向过多。");
                uri = ValidateUri(new Uri(uri, response.Headers.Location).AbsoluteUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("图片超过 8 MiB。");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[32768];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + count > MaxBytes) throw new IOException("图片超过 8 MiB。");
                memory.Write(buffer, 0, count);
            }
            data = memory.ToArray();
            break;
        }
        if (data == null) throw new IOException("没有获取到预览图。");
        var result = await DecodeBoundedAsync(data, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false);
            File.Move(temporary, cachePath, overwrite: true);
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.img").OrderByDescending(file => file.LastWriteTimeUtc).Skip(32))
                try { old.Delete(); } catch { }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Image display does not depend on writable persistent storage. */ }
        finally { try { File.Delete(temporary); } catch { } }
        return result;
    }

    private static Uri ValidateUri(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort
            || !(uri.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("steamuserimages-a.akamaihd.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("预览图不是受支持的 Steam HTTPS 图片地址。");
        return uri;
    }

    private static PreviewImage Decode(byte[] data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(data, writable: false);
        var info = ImageInfo.FromStream(stream) ?? throw new InvalidDataException("不支持此图片格式。");
        if (info.Width <= 0 || info.Height <= 0 || info.Width > 4096 || info.Height > 4096 || (long)info.Width * info.Height > 16000000)
            throw new InvalidDataException("预览图尺寸过大。");
        var decoded = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
        token.ThrowIfCancellationRequested();
        var scale = Math.Min(1, 1024d / Math.Max(decoded.Width, decoded.Height));
        var width = Math.Max(1, (int)(decoded.Width * scale));
        var height = Math.Max(1, (int)(decoded.Height * scale));
        var pixels = Resize(decoded.Data, decoded.Width, decoded.Height, width, height);
        var rasterScale = Math.Min(1, Math.Min(160d / Math.Max(width, height), Math.Sqrt(10000d / ((long)width * height))));
        var rasterWidth = Math.Max(1, (int)(width * rasterScale));
        var rasterHeight = Math.Max(1, (int)(height * rasterScale));
        var raster = Resize(pixels, width, height, rasterWidth, rasterHeight);
        var runs = new List<PixelRun>();
        uint ColorAt(int x, int y)
        {
            var offset = (y * rasterWidth + x) * 4;
            return (uint)(raster[offset] | raster[offset + 1] << 8 | raster[offset + 2] << 16 | raster[offset + 3] << 24);
        }
        for (var y = 0; y < rasterHeight; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < rasterWidth;)
            {
                var color = ColorAt(x, y);
                var end = x + 1;
                while (end < rasterWidth && ColorAt(end, y) == color) end++;
                if ((color >> 24) != 0) runs.Add(new PixelRun(x, y, end - x, color));
                x = end;
            }
        }
        return new PreviewImage(width, height, pixels, rasterWidth, rasterHeight, runs.ToArray());
    }

    private static async Task<PreviewImage> DecodeBoundedAsync(byte[] data, CancellationToken token)
    {
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        try { return Decode(data, token); }
        finally { DecodeGate.Release(); }
    }

    private static byte[] Resize(byte[] source, int sw, int sh, int width, int height)
    {
        if (sw == width && sh == height) return source;
        var result = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(sw - 1, (int)((x + .5) * sw / width));
                var sy = Math.Min(sh - 1, (int)((y + .5) * sh / height));
                Buffer.BlockCopy(source, (sy * sw + sx) * 4, result, (y * width + x) * 4, 4);
            }
        return result;
    }

    public void Reset()
    {
        lock (_gate)
        {
            ++_generation;
            _cancellation?.Cancel();
            _cancellation = null;
            _source = string.Empty;
            _image = null;
            _busy = false;
            _error = string.Empty;
            Interlocked.Exchange(ref _releaseRequested, 1);
        }
    }

    public void ProcessDeferredRelease()
    {
        if (Interlocked.Exchange(ref _releaseRequested, 0) == 0) return;
        _texture?.Dispose();
        _texture = null;
        _uploadAttempted = false;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Reset();
        ProcessDeferredRelease();
    }

    private sealed record PreviewImage(int Width, int Height, byte[] Pixels, int RasterWidth, int RasterHeight, PixelRun[] Runs);
    private readonly record struct PixelRun(int X, int Y, int Length, uint Color);
}
