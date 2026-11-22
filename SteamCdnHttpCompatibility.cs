using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using SteamKit2;

namespace SteamWorkshopManager;

// Each Steam content connection owns this policy. Never changes global TLS/proxy settings.
internal sealed class SteamCdnHttpCompatibility
{
    private readonly ConcurrentDictionary<string, bool> _compatibleHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _attemptedHosts = new(StringComparer.OrdinalIgnoreCase);

    internal bool WasCompatibilityAttempted(Uri address)
        => _attemptedHosts.ContainsKey(address.GetLeftPart(UriPartial.Authority));

    internal HttpClient CreateClient(HttpClientPurpose purpose)
    {
        var client = purpose == HttpClientPurpose.CDN
            ? new HttpClient(new CompatibilityHandler(this))
            : new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SteamKit", "3.4.0"));
        return client;
    }

    private sealed class CompatibilityHandler : HttpMessageHandler
    {
        private readonly SteamCdnHttpCompatibility _owner;
        private readonly HttpMessageInvoker _standard;
        private readonly HttpMessageInvoker _compatible;

        internal CompatibilityHandler(SteamCdnHttpCompatibility owner)
        {
            _owner = owner;
            _standard = new HttpMessageInvoker(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });
            _compatible = new HttpMessageInvoker(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                SslOptions = new() { EnabledSslProtocols = SslProtocols.Tls12 },
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // CDN calls are GETs, but enforce that before replaying any request.
            if (request.Method != HttpMethod.Get || request.Content != null || request.RequestUri == null)
                return await _standard.SendAsync(request, token).ConfigureAwait(false);
            var authority = request.RequestUri.GetLeftPart(UriPartial.Authority);
            if (_owner._compatibleHosts.ContainsKey(authority))
                return await SendCompatibleAsync(request, token).ConfigureAwait(false);
            try { return await _standard.SendAsync(request, token).ConfigureAwait(false); }
            catch (Exception exception) when (!token.IsCancellationRequested && IsHandshakeInterruption(exception))
            {
                token.ThrowIfCancellationRequested();
                _owner._attemptedHosts.TryAdd(authority, true);
                var response = await SendCompatibleAsync(request, token).ConfigureAwait(false);
                _owner._compatibleHosts.TryAdd(authority, true);
                return response;
            }
        }

        private async Task<HttpResponseMessage> SendCompatibleAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var replay = new HttpRequestMessage(HttpMethod.Get, request.RequestUri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            foreach (var header in request.Headers) replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await _compatible.SendAsync(replay, token).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _standard.Dispose(); _compatible.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static bool IsHandshakeInterruption(Exception exception)
    {
        var handshake = false;
        var interrupted = false;
        for (var cause = exception; cause != null; cause = cause.InnerException)
        {
            if (cause is HttpRequestException { HttpRequestError: HttpRequestError.UserAuthenticationError }
                or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) return false;
            handshake |= cause is AuthenticationException
                or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError };
            interrupted |= cause is EndOfStreamException
                or SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }
                || cause is IOException && (cause.Message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
                    || cause.Message.Contains("0 bytes from the transport stream", StringComparison.OrdinalIgnoreCase));
            // A certificate AuthenticationException without a transport interruption never falls back.
        }
        return handshake && interrupted;
    }
}
