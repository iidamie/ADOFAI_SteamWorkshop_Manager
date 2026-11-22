using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace SteamWorkshopManager;

internal static class DownloadNetworkRetry
{
    internal const int MaximumAttempts = 4;

    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation, string phase,
        Action<string>? report, CancellationToken token, int attempts = MaximumAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await operation().ConfigureAwait(false); }
            catch (Exception exception) when (IsTransient(exception, token))
            {
                token.ThrowIfCancellationRequested();
                var failedPhase = Causes(exception).OfType<DownloadTransportException>().FirstOrDefault()?.Phase ?? phase;
                if (attempt >= attempts) throw new DownloadRetryExhaustedException(failedPhase, attempts, exception);
                var delay = TimeSpan.FromMilliseconds(Math.Min(8000, 1000 * (1 << Math.Min(attempt - 1, 3)))
                    + Random.Shared.Next(150, 650));
                report?.Invoke($"{failedPhase}：{DescribeCause(exception)}，{delay.TotalSeconds:F1} 秒后重试（{attempt + 1}/{attempts}），保留下载断点。");
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
    }

    internal static Task RunAsync(Func<Task> operation, string phase, Action<string>? report, CancellationToken token)
        => RunAsync(async () => { await operation().ConfigureAwait(false); return true; }, phase, report, token);

    // Only call around network APIs, never around disk writes, validation or installation.
    internal static async Task<T> ObserveAsync<T>(Func<Task<T>> operation, string phase, CancellationToken token)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (Exception exception) when (IsTransient(exception, token, networkBoundary: true))
        {
            throw new DownloadTransportException(phase, exception);
        }
    }

    internal static bool IsTransient(Exception exception, CancellationToken token, bool networkBoundary = false)
    {
        if (token.IsCancellationRequested) return false;
        var causes = Causes(exception).ToArray();
        if (causes.Any(error => error is DownloadRetryExhaustedException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)) return false;
        // Do not turn authentication/certificate failures into repeated login attempts.
        foreach (var authentication in causes.OfType<AuthenticationException>())
            if (!Causes(authentication).Any(error => error is EndOfStreamException or SocketException || IsEof(error))) return false;
        foreach (var http in causes.OfType<HttpRequestException>())
        {
            if (http.StatusCode is { } status && !RetryStatus(status)) return false;
            if (http.HttpRequestError is HttpRequestError.UserAuthenticationError or HttpRequestError.ConfigurationLimitExceeded
                or HttpRequestError.VersionNegotiationError or HttpRequestError.ExtendedConnectNotSupported) return false;
            if (http.HttpRequestError == HttpRequestError.SecureConnectionError
                && !Causes(http).Any(error => error is EndOfStreamException or SocketException || IsEof(error))) return false;
        }
        foreach (var web in causes.OfType<WebException>())
        {
            if (web.Status == WebExceptionStatus.TrustFailure) return false;
            if (web.Status == WebExceptionStatus.SecureChannelFailure
                && !Causes(web).Any(error => error is EndOfStreamException or SocketException || IsEof(error))) return false;
            if (web.Response is HttpWebResponse response && !RetryStatus(response.StatusCode)) return false;
        }
        return causes.Any(error => error is DownloadTransportException or HttpRequestException or SocketException
                or TimeoutException or OperationCanceledException
                || IsEof(error)
                || error is WebException web && web.Status is WebExceptionStatus.ConnectFailure or WebExceptionStatus.ConnectionClosed
                    or WebExceptionStatus.Timeout or WebExceptionStatus.ReceiveFailure or WebExceptionStatus.SendFailure or WebExceptionStatus.NameResolutionFailure
                || networkBoundary && error is IOException);
    }

    private static bool RetryStatus(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status is >= 500 and <= 599;

    private static bool IsEof(Exception error)
        => error is IOException or AuthenticationException
            && (error.Message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("0 bytes from the transport stream", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Exception> Causes(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var cause in Causes(inner)) yield return cause;
        }
        else if (error.InnerException != null)
            foreach (var cause in Causes(error.InnerException)) yield return cause;
    }

    internal static string DescribeCause(Exception error)
    {
        var causes = Causes(error).ToArray();
        if (causes.Any(cause => cause is EndOfStreamException || IsEof(cause)))
            return causes.Any(cause => cause is AuthenticationException
                or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
                ? "TLS 握手提前关闭（EOF）" : "连接提前关闭（EOF）";
        if (causes.OfType<HttpRequestException>().FirstOrDefault(cause => cause.StatusCode.HasValue) is { } http)
            return $"服务器暂时不可用（HTTP {(int)http.StatusCode!}）";
        if (causes.OfType<SocketException>().FirstOrDefault() is { } socket) return $"网络连接中断（{socket.SocketErrorCode}）";
        if (causes.Any(cause => cause is TimeoutException or OperationCanceledException)) return "网络连接超时";
        return "网络握手或传输中断";
    }

    internal static string FailureMessage(Exception error)
        => Causes(error).OfType<DownloadRetryExhaustedException>().FirstOrDefault()?.Message ?? error.GetBaseException().Message;
}

internal sealed class DownloadTransportException(string phase, Exception inner)
    : IOException($"{phase}：{DownloadNetworkRetry.DescribeCause(inner)}。", inner)
{
    internal string Phase { get; } = phase;
}

internal sealed class DownloadRetryExhaustedException(string phase, int attempts, Exception inner)
    : IOException($"{phase}：尝试 {attempts} 次后仍失败，{DownloadNetworkRetry.DescribeCause(inner)}。已保留下载断点，可稍后重试。", inner);
