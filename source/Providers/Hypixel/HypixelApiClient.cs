using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// HTTP client for the two keyless Hypixel sources: the public profile page, which carries a
    /// player's unlock state and tier progress, and <c>/v2/resources/achievements</c>, which carries
    /// the definitions.
    ///
    /// The player endpoints of the official API need an API key, and Hypixel's key policy forbids
    /// entering a key into a third-party mod or shipping one in a distributed binary, so player data
    /// comes from the profile page instead.
    /// </summary>
    internal sealed class HypixelApiClient : IDisposable
    {
        private const int MaxAttempts = 3;
        private const string ProjectUrl = "https://github.com/justin-delano/PlayniteAchievements";

        private static readonly Uri CatalogUri = new Uri("https://api.hypixel.net/v2/resources/achievements");

        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;

        public HypixelApiClient(ILogger logger)
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
        /// Fetches and parses the player's achievements page. Throws
        /// <see cref="HypixelPlayerNotFoundException"/> when the site has no such player.
        /// </summary>
        public async Task<HypixelProfile> FetchProfileAsync(string username, CancellationToken cancel)
        {
            var uri = HypixelParsing.BuildProfileUri(username);

            string html;
            try
            {
                html = await GetRawAsync(uri, cancel).ConfigureAwait(false);
            }
            catch (HypixelApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new HypixelPlayerNotFoundException(username);
            }

            return HypixelParsing.ParseProfile(html);
        }

        /// <summary>
        /// Fetches every achievement definition. Returns null when the response is not a success.
        /// </summary>
        public async Task<HypixelAchievementsResponse> FetchCatalogAsync(CancellationToken cancel)
        {
            var json = await GetRawAsync(CatalogUri, cancel).ConfigureAwait(false);
            var response = JsonConvert.DeserializeObject<HypixelAchievementsResponse>(json);
            return response?.Success == true && response.Achievements != null ? response : null;
        }

        /// <summary>
        /// User-Agent naming the plugin, its version and a contact URL, so the site's operator can
        /// tell the plugin's traffic apart and reach its maintainer.
        /// </summary>
        internal static string BuildUserAgent(string version)
        {
            var product = string.IsNullOrWhiteSpace(version)
                ? "PlayniteAchievements"
                : "PlayniteAchievements/" + version.Trim();
            return product + " (+" + ProjectUrl + ")";
        }

        private async Task<string> GetRawAsync(Uri uri, CancellationToken cancel)
        {
            var userAgent = BuildUserAgent(Common.PluginManifest.Version);
            Exception lastException = null;
            var backoff = TimeSpan.FromSeconds(1);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

                        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                        {
                            var status = (int)response.StatusCode;
                            if (status == 429 || status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                            {
                                _logger?.Warn($"[Hypixel] HTTP {status} on {uri} (attempt {attempt}/{MaxAttempts}).");
                                lastException = new HypixelApiException(response.StatusCode, uri);
                                await Task.Delay(backoff, cancel).ConfigureAwait(false);
                                backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
                                continue;
                            }

                            if (!response.IsSuccessStatusCode)
                            {
                                throw new HypixelApiException(response.StatusCode, uri);
                            }

                            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
                {
                    lastException = ex;
                    _logger?.Warn($"[Hypixel] Timeout on {uri} (attempt {attempt}/{MaxAttempts}).");
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger?.Warn(ex, $"[Hypixel] HTTP error on {uri} (attempt {attempt}/{MaxAttempts}).");
                }

                await Task.Delay(backoff, cancel).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
            }

            if (lastException is HypixelApiException statusException)
            {
                throw statusException;
            }

            throw new HttpRequestException("Request failed after retries.", lastException);
        }
    }
}
