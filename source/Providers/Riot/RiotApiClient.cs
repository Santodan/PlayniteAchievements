using Playnite.SDK;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Raised when Riot rejects the API key. Development keys expire every 24 hours and then answer
    /// 403, so this is an ordinary state the refresh pipeline surfaces as "authentication required"
    /// rather than an error.
    /// </summary>
    internal sealed class RiotAuthorizationException : Exception
    {
        public RiotAuthorizationException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }

    /// <summary>Raised when a Riot ID does not resolve to an account on the selected region.</summary>
    internal sealed class RiotAccountNotFoundException : Exception
    {
        public RiotAccountNotFoundException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Thin client over the Riot web API. Every request carries the key on the request message,
    /// never on <c>DefaultRequestHeaders</c>, so the key can change between calls without
    /// rebuilding the client.
    /// </summary>
    internal sealed class RiotApiClient : IDisposable
    {
        private const int MaxAttempts = 4;
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;

        public RiotApiClient(ILogger logger)
        {
            _logger = logger;

            _handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false
            };

            _http = new HttpClient(_handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
        }

        /// <summary>
        /// Resolves a Riot ID to its PUUID. account-v1 is served from the regional host, not the
        /// platform host the challenge endpoints use.
        /// </summary>
        public async Task<RiotAccountDto> GetAccountByRiotIdAsync(
            string platform,
            string gameName,
            string tagLine,
            string apiKey,
            CancellationToken cancel)
        {
            var uri = new Uri(
                $"{RiotRegions.GetRegionalHost(platform)}/riot/account/v1/accounts/by-riot-id/" +
                $"{Uri.EscapeDataString(gameName ?? string.Empty)}/{Uri.EscapeDataString(tagLine ?? string.Empty)}");

            var json = await GetStringAsync(uri, apiKey, cancel).ConfigureAwait(false);
            var account = Newtonsoft.Json.JsonConvert.DeserializeObject<RiotAccountDto>(json);

            if (string.IsNullOrWhiteSpace(account?.Puuid))
            {
                throw new RiotAccountNotFoundException($"No PUUID returned for Riot ID '{gameName}#{tagLine}'.");
            }

            return account;
        }

        /// <summary>Raw <c>player-data</c> JSON for the given PUUID.</summary>
        public Task<string> GetPlayerDataJsonAsync(string platform, string puuid, string apiKey, CancellationToken cancel)
        {
            var uri = new Uri(
                $"{RiotRegions.GetPlatformHost(platform)}/lol/challenges/v1/player-data/{Uri.EscapeDataString(puuid ?? string.Empty)}");
            return GetStringAsync(uri, apiKey, cancel);
        }

        /// <summary>Raw <c>challenges/percentiles</c> JSON, which supplies rarity for locked challenges.</summary>
        public Task<string> GetPercentilesJsonAsync(string platform, string apiKey, CancellationToken cancel)
        {
            var uri = new Uri($"{RiotRegions.GetPlatformHost(platform)}/lol/challenges/v1/challenges/percentiles");
            return GetStringAsync(uri, apiKey, cancel);
        }

        private async Task<string> GetStringAsync(Uri uri, string apiKey, CancellationToken cancel)
        {
            var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("X-Riot-Token", (apiKey ?? string.Empty).Trim());
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                return request;
            }, cancel).ConfigureAwait(false);

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new RiotAuthorizationException(
                        response.StatusCode,
                        $"Riot API rejected the key with {(int)response.StatusCode} on {uri.AbsolutePath}.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new RiotAccountNotFoundException($"Riot API returned 404 for {uri.AbsolutePath}.");
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancel)
        {
            Exception lastException = null;
            var backoff = TimeSpan.FromSeconds(1);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    using (var request = requestFactory())
                    {
                        var response = await _http
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
                            .ConfigureAwait(false);

                        if (response.StatusCode == (HttpStatusCode)429)
                        {
                            var delay = GetRetryAfterDelay(response) ?? backoff;
                            _logger?.Warn($"[Riot] Rate limited. Backing off {delay.TotalSeconds:0.0}s (attempt {attempt}/{MaxAttempts}).");
                            response.Dispose();
                            await Task.Delay(delay, cancel).ConfigureAwait(false);
                            backoff = NextBackoff(backoff);
                            continue;
                        }

                        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                        {
                            _logger?.Warn($"[Riot] Server error {(int)response.StatusCode} on {request.RequestUri} (attempt {attempt}/{MaxAttempts}).");
                            response.Dispose();
                            await Task.Delay(backoff, cancel).ConfigureAwait(false);
                            backoff = NextBackoff(backoff);
                            continue;
                        }

                        return response;
                    }
                }
                catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
                {
                    lastException = ex;
                    _logger?.Warn($"[Riot] Timeout on attempt {attempt}/{MaxAttempts}.");
                    await Task.Delay(backoff, cancel).ConfigureAwait(false);
                    backoff = NextBackoff(backoff);
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger?.Warn(ex, $"[Riot] HTTP error on attempt {attempt}/{MaxAttempts}.");
                    await Task.Delay(backoff, cancel).ConfigureAwait(false);
                    backoff = NextBackoff(backoff);
                }
            }

            throw new HttpRequestException("Riot API request failed after retries.", lastException);
        }

        private static TimeSpan NextBackoff(TimeSpan current)
            => TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * 2, MaxBackoff.TotalSeconds));

        private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
        {
            try
            {
                var retryAfter = response?.Headers?.RetryAfter;
                if (retryAfter == null)
                {
                    return null;
                }

                if (retryAfter.Delta.HasValue)
                {
                    return retryAfter.Delta.Value;
                }

                if (retryAfter.Date.HasValue)
                {
                    var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                    return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
                }
            }
            catch
            {
                // A malformed Retry-After just means we use our own backoff.
            }

            return null;
        }
    }
}
