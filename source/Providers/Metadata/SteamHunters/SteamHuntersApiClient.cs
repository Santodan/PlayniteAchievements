using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata
{
    internal sealed class SteamHuntersApiClient
    {
        private const string BaseUrl = "https://steamhunters.com/api";

        // Characters of an unusable body kept in the log.
        private const int BodySnippetLength = 120;

        // Unspaced bulk refreshes sent about two requests a second and Cloudflare answered Error
        // 1015 (rate limited) after 150-180 of them (2026-10-18), a block that outlasts the retry
        // backoff. Requests are spaced by a short gap and unusable responses retried with backoff.
        private const int RequestGapMs = 250;
        private static readonly RateLimiter SharedRateLimiter = new RateLimiter(1000, 3);

        private readonly Func<string, CancellationToken, Task<string>> _fetchPageText;
        private readonly ILogger _logger;
        private readonly RateLimiter _rateLimiter;
        private readonly int _requestGapMs;
        private int _madeRequest;

        public SteamHuntersApiClient(
            Func<string, CancellationToken, Task<string>> fetchPageText,
            ILogger logger,
            RateLimiter rateLimiter = null,
            int requestGapMs = RequestGapMs)
        {
            _fetchPageText = fetchPageText ?? throw new ArgumentNullException(nameof(fetchPageText));
            _logger = logger;
            _rateLimiter = rateLimiter ?? SharedRateLimiter;
            _requestGapMs = Math.Max(0, requestGapMs);
        }

        /// <summary>
        /// Fetches the achievement groups through an offscreen CEF view (the Steam scan's
        /// shared leased view while a scan or friend refresh holds the lease).
        /// steamhunters.com tarpits the .NET HTTP stack's TLS fingerprint, so the browser is
        /// the only reliable transport; the JSON endpoint renders as a plain-text document,
        /// so the page text is the raw JSON body. Returns null on failure.
        /// includeMultiplayerGroup moves base-game achievements tagged Multiplayer out of the
        /// base game into a group of their own, named "Multiplayer" and carrying no DlcAppId.
        /// </summary>
        public async Task<SteamHuntersAchievementGroupsResponse> GetAchievementGroupsAsync(
            int appId,
            CancellationToken cancel)
        {
            if (appId <= 0)
            {
                return null;
            }

            var url = BaseUrl + "/GetAchievementGroups/v1?appId=" +
                appId.ToString(CultureInfo.InvariantCulture) +
                "&includeMultiplayerGroup=true";

            try
            {
                return await FetchJsonAsync<SteamHuntersAchievementGroupsResponse>(url, cancel).ConfigureAwait(false);
            }
            catch (SteamHuntersUnusableResponseException ex)
            {
                _logger?.Warn($"[SteamHunters] Group request failed for appId={appId} after retries: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches an app's achievement list, used for each achievement's moderator-set
        /// obtainability. Same CEF transport as the groups request. This endpoint belongs to the
        /// site's internal API rather than its third-party section, so callers treat a null
        /// result (failure or an unparseable body) as "no obtainability data".
        /// </summary>
        public async Task<List<SteamHuntersAchievement>> GetAchievementsAsync(
            int appId,
            CancellationToken cancel)
        {
            if (appId <= 0)
            {
                return null;
            }

            var url = BaseUrl + "/apps/" + appId.ToString(CultureInfo.InvariantCulture) + "/achievements";

            try
            {
                return await FetchJsonAsync<List<SteamHuntersAchievement>>(url, cancel).ConfigureAwait(false);
            }
            catch (SteamHuntersUnusableResponseException ex)
            {
                _logger?.Debug($"[SteamHunters] Achievement request failed for appId={appId} after retries: {ex.Message}");
                return null;
            }
        }

        // Spaces requests by the request gap and retries an empty or non-JSON body (a navigation
        // timeout or an error page) with the rate limiter's backoff.
        private async Task<T> FetchJsonAsync<T>(string url, CancellationToken cancel)
            where T : class
        {
            if (_requestGapMs > 0 && Interlocked.Exchange(ref _madeRequest, 1) == 1)
            {
                await Task.Delay(_requestGapMs, cancel).ConfigureAwait(false);
            }

            return await _rateLimiter.ExecuteWithRetryAsync(
                async () =>
                {
                    var body = await _fetchPageText(url, cancel).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        throw new SteamHuntersUnusableResponseException("no content");
                    }

                    try
                    {
                        return JsonConvert.DeserializeObject<T>(body)
                            ?? throw new SteamHuntersUnusableResponseException($"empty JSON (length={body.Length})");
                    }
                    catch (JsonException ex)
                    {
                        throw new SteamHuntersUnusableResponseException(
                            $"not JSON (length={body.Length}): {Snippet(body)}",
                            ex);
                    }
                },
                ex => ex is SteamHuntersUnusableResponseException,
                cancel).ConfigureAwait(false);
        }

        private static string Snippet(string body)
        {
            var flattened = string.Join(" ", body.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return flattened.Length <= BodySnippetLength
                ? flattened
                : flattened.Substring(0, BodySnippetLength) + "...";
        }
    }

    internal sealed class SteamHuntersUnusableResponseException : Exception
    {
        public SteamHuntersUnusableResponseException(string message, Exception inner = null)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// SteamHunters' moderator-set obtainability, from the site's Obtainability enum.
    /// </summary>
    internal enum SteamHuntersObtainability
    {
        Obtainable = 0,
        BrokenButObtainable = 1,
        ConditionallyObtainable = 2,
        Unobtainable = 3
    }

    internal sealed class SteamHuntersAchievement
    {
        public string ApiName { get; set; }

        public SteamHuntersObtainability Obtainability { get; set; }
    }

    internal sealed class SteamHuntersAchievementGroupsResponse
    {
        public string GroupBy { get; set; }

        public List<SteamHuntersAchievementGroup> Groups { get; set; } =
            new List<SteamHuntersAchievementGroup>();
    }

    internal sealed class SteamHuntersAchievementGroup
    {
        public string Name { get; set; }

        public int? DlcAppId { get; set; }

        public string DlcAppName { get; set; }

        public List<string> AchievementApiNames { get; set; } = new List<string>();
    }
}
