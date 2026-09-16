using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Thin client over the Guild Wars 2 v2 API. Every request carries the token on the request
    /// message, never on DefaultRequestHeaders, so the key can change between calls without
    /// rebuilding the client.
    /// </summary>
    internal sealed class Gw2ApiClient : IDisposable
    {
        private const string BaseUrl = "https://api.guildwars2.com/v2";

        /// <summary>
        /// The API rejects a longer id list with 400 "id list too long; this endpoint is limited to
        /// 200 ids at once".
        /// </summary>
        internal const int MaxIdsPerRequest = 200;

        /// <summary>Scope needed to read account achievement progress.</summary>
        internal const string ProgressionPermission = "progression";

        private const int MaxAttempts = 4;
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;

        public Gw2ApiClient(ILogger logger)
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
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
        }

        /// <summary>
        /// The current game build id. The achievement catalog only changes with a build, so this is
        /// what decides whether the cached catalog is still current.
        /// </summary>
        public async Task<int> GetBuildIdAsync(CancellationToken cancel)
        {
            var json = await GetStringAsync(BaseUrl + "/build", null, cancel).ConfigureAwait(false);
            var build = Deserialize<Gw2Build>(json, "build");
            return build?.Id ?? 0;
        }

        /// <summary>
        /// Validates a token and reports its scopes. Throws <see cref="Gw2AuthorizationException"/>
        /// when the key itself is rejected.
        /// </summary>
        public async Task<Gw2TokenInfo> GetTokenInfoAsync(string apiKey, CancellationToken cancel)
        {
            var json = await GetStringAsync(BaseUrl + "/tokeninfo", apiKey, cancel).ConfigureAwait(false);
            return Deserialize<Gw2TokenInfo>(json, "tokeninfo");
        }

        /// <summary>The account behind the token: its GUID and its Player.1234 display name.</summary>
        public async Task<Gw2Account> GetAccountAsync(string apiKey, CancellationToken cancel)
        {
            var json = await GetStringAsync(BaseUrl + "/account", apiKey, cancel).ConfigureAwait(false);
            return Deserialize<Gw2Account>(json, "account");
        }

        /// <summary>
        /// The account's achievement progress. Only achievements the account has touched are
        /// returned, so anything missing from the result has no progress at all.
        /// </summary>
        public async Task<List<Gw2AccountAchievement>> GetAccountAchievementsAsync(string apiKey, CancellationToken cancel)
        {
            var json = await GetStringAsync(BaseUrl + "/account/achievements", apiKey, cancel).ConfigureAwait(false);
            return Deserialize<List<Gw2AccountAchievement>>(json, "account/achievements")
                   ?? new List<Gw2AccountAchievement>();
        }

        /// <summary>The 19 achievement groups, in one request.</summary>
        public async Task<List<Gw2Group>> GetGroupsAsync(string language, CancellationToken cancel)
        {
            var json = await GetStringAsync(
                BaseUrl + "/achievements/groups?ids=all&lang=" + Uri.EscapeDataString(language),
                null,
                cancel).ConfigureAwait(false);

            return Deserialize<List<Gw2Group>>(json, "achievements/groups") ?? new List<Gw2Group>();
        }

        /// <summary>
        /// The 355 achievement categories, in one request. SuccessStory fetches these one id at a
        /// time; ids=all is a single call.
        /// </summary>
        public async Task<List<Gw2Category>> GetCategoriesAsync(string language, CancellationToken cancel)
        {
            var json = await GetStringAsync(
                BaseUrl + "/achievements/categories?ids=all&lang=" + Uri.EscapeDataString(language),
                null,
                cancel).ConfigureAwait(false);

            return Deserialize<List<Gw2Category>>(json, "achievements/categories") ?? new List<Gw2Category>();
        }

        /// <summary>
        /// Achievement definitions for the given ids, batched at the API's 200-id ceiling. Roughly
        /// 35 requests for the full catalog, well inside the 600/minute rate limit.
        /// </summary>
        public async Task<List<Gw2Achievement>> GetAchievementsAsync(
            IReadOnlyList<int> ids,
            string language,
            IProgress<int> progress,
            CancellationToken cancel)
        {
            var results = new List<Gw2Achievement>(ids?.Count ?? 0);
            if (ids == null || ids.Count == 0)
            {
                return results;
            }

            var fetched = 0;
            for (var offset = 0; offset < ids.Count; offset += MaxIdsPerRequest)
            {
                cancel.ThrowIfCancellationRequested();

                var batch = ids.Skip(offset).Take(MaxIdsPerRequest).ToList();
                var joined = string.Join(",", batch.Select(id => id.ToString(CultureInfo.InvariantCulture)));

                var json = await GetStringAsync(
                    BaseUrl + "/achievements?ids=" + joined + "&lang=" + Uri.EscapeDataString(language),
                    null,
                    cancel).ConfigureAwait(false);

                var batchResults = Deserialize<List<Gw2Achievement>>(json, "achievements");
                if (batchResults != null)
                {
                    results.AddRange(batchResults);
                }

                fetched += batch.Count;
                progress?.Report(fetched);
            }

            return results;
        }

        private static T Deserialize<T>(string json, string endpoint)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (JsonException ex)
            {
                throw new Gw2ApiException($"Could not read the Guild Wars 2 {endpoint} response.", ex);
            }
        }

        private async Task<string> GetStringAsync(string url, string apiKey, CancellationToken cancel)
        {
            var uri = new Uri(url);

            var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
                }

                return request;
            }, cancel).ConfigureAwait(false);

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new Gw2AuthorizationException(
                        response.StatusCode,
                        $"The Guild Wars 2 API rejected the API key with {(int)response.StatusCode} on {uri.AbsolutePath}.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new Gw2ApiException(
                        $"The Guild Wars 2 API returned {(int)response.StatusCode} on {uri.AbsolutePath}.");
                }

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
                            _logger?.Warn($"[GW2] Rate limited. Backing off {delay.TotalSeconds:0.0}s (attempt {attempt}/{MaxAttempts}).");
                            response.Dispose();
                            await Task.Delay(delay, cancel).ConfigureAwait(false);
                            backoff = NextBackoff(backoff);
                            continue;
                        }

                        // 503 "API not active" is how this API disables an endpoint during
                        // maintenance, so a server error is worth retrying rather than failing on.
                        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                        {
                            _logger?.Warn($"[GW2] Server error {(int)response.StatusCode} on {request.RequestUri} (attempt {attempt}/{MaxAttempts}).");
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
                    _logger?.Warn($"[GW2] Timeout on attempt {attempt}/{MaxAttempts}.");
                    await Task.Delay(backoff, cancel).ConfigureAwait(false);
                    backoff = NextBackoff(backoff);
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger?.Warn(ex, $"[GW2] HTTP error on attempt {attempt}/{MaxAttempts}.");
                    await Task.Delay(backoff, cancel).ConfigureAwait(false);
                    backoff = NextBackoff(backoff);
                }
            }

            throw new Gw2ApiException("The Guild Wars 2 API request failed after retries.", lastException);
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
