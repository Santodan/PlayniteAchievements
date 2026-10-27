using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// API client for Exophase game search and achievement fetching.
    /// Uses WebView for all requests to bypass Cloudflare protection.
    /// </summary>
    public sealed class ExophaseApiClient
    {
        private const string SearchUrl = "https://api.exophase.com/public/archive/games";
        private const string PsnSearchUrl = "https://api.exophase.com/public/archive/psn";
        private const string XboxSearchUrl = "https://api.exophase.com/public/archive/xbox";
        private const string AchievementPageBaseUrl = "https://www.exophase.com/game/{0}/achievements/";
        internal const string ExophaseApiNamePrefix = "exophase_";
        // Stable-id key format keyed on the award's master id (the li's data-master attribute,
        // equal to the earned endpoint's masterAwardId). The colon cannot appear in legacy
        // display-derived keys, so the two key families never collide.
        internal const string ExophaseStableApiNamePrefix = "exophase:";
        private const int AchievementDomReadyPollDelayMs = 250;
        private const int AchievementDomReadyPollAttempts = 8;

        // A public page's list can still be rendering when navigation ends (long lists stopped near
        // 50 rows): its row count is read until it holds for two reads.
        private const int AwardRowsPollDelayMs = 400;
        private const int AwardRowsPollAttempts = 10;

        // Rows of the award list, as ParseAchievementsHtml finds them; -1 when the page has no list.
        private const string AwardRowCountScript =
            "(function(){try{var lists=document.querySelectorAll('ul[class*=achievement],ul[class*=trophy],ul[class*=challenge]');" +
            "if(!lists.length){return -1;}var n=0;for(var i=0;i<lists.length;i++){n+=lists[i].querySelectorAll(':scope > li').length;}" +
            "return n;}catch(e){return -1;}})()";
        // Above this many images, the in-browser CDN warm is skipped; DiskImageService's
        // lazy-thumbnail retry path warms each thumbnail on download instead.
        private const int MaxImageWarmCount = 400;

        // Shared by every client so the provider, rarity and metadata searches back off together.
        // Retries only, up to four, waiting roughly 4, 6, 10 and 18 seconds after a throttled answer.
        private static readonly RateLimiter SearchRateLimiter = new RateLimiter(2000, 4);

        // The page text of a throttled answer is the status alone, possibly wrapped in characters
        // the browser view adds around plain text.
        private static readonly Regex ThrottledBody = new Regex(@"^\W*429\W*$", RegexOptions.CultureInvariant);

        // Renderer-side mirror of ContainsAchievementMarkup + HasUnlockDataPopulated so readiness
        // can be polled without serializing the full page source across the CEF boundary each pass.
        private const string AchievementDomReadyScript =
            "(function(){try{" +
            "if(!document.querySelector('.award-title,.award-average,.award-earned,[data-earned],[data-average]')){return false;}" +
            "if(document.querySelector('.award.visible.earned,.award.earned')){return true;}" +
            "var es=document.querySelectorAll('[data-earned]');" +
            "for(var i=0;i<es.length;i++){var v=parseInt(es[i].getAttribute('data-earned'),10);if(v>0){return true;}}" +
            "return false;}catch(e){return false;}})()";

        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly ExophaseCookieSnapshotStore _cookieSnapshotStore;
        private readonly OffscreenViewLeaseSource _offscreenViews;

        // Per-refresh cookie cache: when a session is active, the encrypted snapshot is loaded and
        // validated once (in BeginCookieSession) and every fetch reuses it instead of decrypting the
        // file per call. Mirrors SteamFriendsProvider's prepared-state pattern.
        private readonly object _cookieSessionLock = new object();
        private List<HttpCookie> _preparedCookies;
        private bool _cookieSessionActive;
        private IDisposable _cookieSessionViewLease;

        internal ExophaseApiClient(IPlayniteAPI playniteApi, ILogger logger, ExophaseCookieSnapshotStore cookieSnapshotStore)
        {
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _logger = logger;
            _cookieSnapshotStore = cookieSnapshotStore;
            _offscreenViews = new OffscreenViewLeaseSource(_playniteApi, _logger);
        }

        /// <summary>
        /// Opens a per-refresh cookie session: loads and validates the encrypted cookie snapshot once
        /// so subsequent fetches reuse it instead of decrypting the file per call. Returns whether the
        /// loaded snapshot contains all critical authentication cookies.
        /// </summary>
        internal bool BeginCookieSession()
        {
            List<HttpCookie> cookies = null;
            var loaded = _cookieSnapshotStore?.TryLoad(out cookies) ?? false;

            // Every Exophase page fetch goes through an offscreen view (Cloudflare bypass),
            // so the session also leases one shared view for the whole refresh instead of
            // creating and disposing a view per page.
            IDisposable previousViewLease;
            lock (_cookieSessionLock)
            {
                _preparedCookies = loaded ? cookies : null;
                _cookieSessionActive = true;
                previousViewLease = _cookieSessionViewLease;
                _cookieSessionViewLease = _offscreenViews.BeginLease();
            }

            previousViewLease?.Dispose();

            if (loaded && cookies != null && cookies.Count > 0)
            {
                WarnIfMissingCriticalCookies(cookies);
                return ExophaseCookieSnapshotStore.HasCriticalCookies(cookies);
            }

            // A client built without a snapshot store reads public pages only.
            if (_cookieSnapshotStore != null)
            {
                _logger?.Warn("[Exophase] No snapshot cookies available for this refresh - fetches may not show unlocked achievements.");
            }

            return false;
        }

        /// <summary>
        /// Closes the per-refresh cookie session and drops the cached cookies. After this, fetches fall
        /// back to loading the snapshot per call.
        /// </summary>
        internal void EndCookieSession()
        {
            IDisposable viewLease;
            lock (_cookieSessionLock)
            {
                _preparedCookies = null;
                _cookieSessionActive = false;
                viewLease = _cookieSessionViewLease;
                _cookieSessionViewLease = null;
            }

            viewLease?.Dispose();
        }

        /// <summary>
        /// Returns the cookies to restore before a fetch. When a cookie session is active the cached
        /// snapshot is reused (no disk I/O, validation already logged once); otherwise the snapshot is
        /// loaded for this call only, preserving behavior for callers that do not open a session.
        /// </summary>
        private (bool Loaded, List<HttpCookie> Cookies) AcquireFetchCookies()
        {
            lock (_cookieSessionLock)
            {
                if (_cookieSessionActive)
                {
                    var cookies = _preparedCookies;
                    return (cookies != null && cookies.Count > 0, cookies);
                }
            }

            List<HttpCookie> snapshotCookies = null;
            var snapshotLoaded = _cookieSnapshotStore?.TryLoad(out snapshotCookies) ?? false;
            if (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0)
            {
                WarnIfMissingCriticalCookies(snapshotCookies);
            }

            return (snapshotLoaded, snapshotCookies);
        }

        private void WarnIfMissingCriticalCookies(IReadOnlyList<HttpCookie> cookies)
        {
            var missingCritical = ExophaseCookieSnapshotStore.GetMissingCriticalCookies(cookies);
            if (missingCritical.Count > 0)
            {
                _logger?.Warn($"[Exophase] Missing critical auth cookies: {string.Join(", ", missingCritical)}. " +
                    $"Achievement unlock status may not be accurate. User may need to re-authenticate.");
            }
        }

        /// <summary>
        /// Extracts the game slug from an Exophase achievement/trophy/challenges URL.
        /// Examples:
        /// - https://www.exophase.com/game/shogun-showdown-steam/achievements/ -> shogun-showdown-steam
        /// - https://www.exophase.com/game/shogun-showdown-psn/trophies/ -> shogun-showdown-psn
        /// - https://www.exophase.com/game/prince-of-persia-the-lost-crown-uplay/challenges/ -> prince-of-persia-the-lost-crown-uplay
        /// </summary>
        public static string ExtractSlugFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            // Drop any fragment or query string first: profile links look like
            // /game/{slug}/achievements/#4768201, and the trailing "#..." would otherwise
            // prevent the end-anchored pattern below from matching (yielding a null slug).
            var separatorIndex = url.IndexOfAny(new[] { '#', '?' });
            if (separatorIndex >= 0)
            {
                url = url.Substring(0, separatorIndex);
            }

            // Match pattern: /game/{slug}/ followed by achievements, trophies, challenges, or end of URL
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/game/([^/]+)(?:/(?:achievements|trophies|challenges))?/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            return null;
        }

        /// <summary>
        /// The game slug typed into a search box: one an exophase.com game address names, or a bare
        /// slug that ends in a platform ("hades-steam"), so a plain game name is never read as one.
        /// Null otherwise.
        /// </summary>
        public static string TryParseGameSlug(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (trimmed.IndexOf('/') >= 0 || trimmed.IndexOf("exophase.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var slug = ExtractSlugFromUrl(trimmed);
                return string.IsNullOrWhiteSpace(slug) ? null : slug.ToLowerInvariant();
            }

            return System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)+$") &&
                   ExophaseFriendPlatformMatcher.ExtractPlatformSlugFromGameSlug(trimmed) != null
                ? trimmed.ToLowerInvariant()
                : null;
        }

        /// <summary>
        /// Normalizes user input for a game slug: a full exophase.com game URL is reduced to its
        /// slug, and a bare slug is accepted as-is when it contains no whitespace.
        /// </summary>
        public static bool TryNormalizeSlugInput(string input, out string slug)
        {
            slug = null;
            var trimmed = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return false;
            }

            if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                trimmed.IndexOf("exophase.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf('/') >= 0)
            {
                slug = ExtractSlugFromUrl(trimmed);
                return !string.IsNullOrWhiteSpace(slug);
            }

            foreach (var ch in trimmed)
            {
                if (char.IsWhiteSpace(ch))
                {
                    return false;
                }
            }

            slug = trimmed;
            return true;
        }

        /// <summary>
        /// Builds the achievement page URL from a game slug or full URL.
        /// Supports both new format (slug only) and legacy format (full URL) for backward compatibility.
        /// PlayStation games use /trophies/, Ubisoft/Uplay uses /challenges/, others use /achievements/.
        /// When the caller already knows the platform, pass <paramref name="platformHint"/> (an
        /// Exophase platform slug or provider platform key) so the endpoint is driven by the known
        /// platform rather than inferred from the slug's suffix; otherwise the suffix is used.
        /// </summary>
        public static string BuildUrlFromSlug(string slugOrUrl, string platformHint = null)
        {
            if (string.IsNullOrWhiteSpace(slugOrUrl))
            {
                return null;
            }

            // If it's already a full URL, extract the slug and rebuild (normalizes URL)
            if (slugOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var extractedSlug = ExtractSlugFromUrl(slugOrUrl);
                if (!string.IsNullOrWhiteSpace(extractedSlug))
                {
                    // Preserve original endpoint type when a full URL is provided.
                    var wasTrophies = slugOrUrl.IndexOf("/trophies", StringComparison.OrdinalIgnoreCase) >= 0;
                    var wasChallenges = slugOrUrl.IndexOf("/challenges", StringComparison.OrdinalIgnoreCase) >= 0;
                    var endpoint = wasTrophies ? "trophies" : (wasChallenges ? "challenges" : "achievements");
                    return $"https://www.exophase.com/game/{extractedSlug}/{endpoint}/";
                }
                // If we can't extract, return as-is (legacy fallback)
                return slugOrUrl;
            }

            // Resolve the endpoint from the known platform when a hint is supplied, otherwise infer
            // it from the slug's own platform suffix. Both paths route through the one canonical
            // family map so PSN -> trophies and Ubisoft -> challenges regardless of the token variant.
            var platformSlug = !string.IsNullOrWhiteSpace(platformHint)
                ? platformHint
                : ExophaseFriendPlatformMatcher.ExtractPlatformSlugFromGameSlug(slugOrUrl);
            var endpointType = ExophaseFriendPlatformMatcher.ResolveExophaseEndpoint(platformSlug);
            return $"https://www.exophase.com/game/{slugOrUrl}/{endpointType}/";
        }

        /// <summary>
        /// Searches for games on Exophase using WebView to bypass Cloudflare.
        /// </summary>
        public async Task<List<ExophaseGame>> SearchGamesAsync(string query, CancellationToken ct)
        {
            return await SearchGamesAsync(query, null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Searches for games on Exophase with optional platform filter.
        /// </summary>
        /// <param name="query">Game name to search for.</param>
        /// <param name="platformSlug">Optional platform slug to filter by (e.g., "steam", "ps4", "ps3", "xbox", "xbox-one", "xbox-360").</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<List<ExophaseGame>> SearchGamesAsync(string query, string platformSlug, CancellationToken ct)
        {
            return (await TrySearchGamesAsync(query, platformSlug, ct).ConfigureAwait(false)).Games;
        }

        /// <summary>
        /// The search's games, and whether Exophase answered at all: false for a failed request,
        /// a challenge page or a search still throttled, which say nothing about the game.
        /// </summary>
        internal async Task<(List<ExophaseGame> Games, bool Answered)> TrySearchGamesAsync(string query, string platformSlug, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return (new List<ExophaseGame>(), true);
            }

            try
            {
                var url = BuildSearchUrl(query, platformSlug);

                // Use WebView to bypass Cloudflare protection. A throttled search answers with the
                // bare body "429"; it is retried with backoff rather than read as no results.
                var json = await SearchRateLimiter.ExecuteWithRetryAsync(
                    async () =>
                    {
                        var body = await FetchJsonViaWebViewAsync(url, ct).ConfigureAwait(false);
                        if (body != null && ThrottledBody.IsMatch(body))
                        {
                            throw new ExophaseThrottledException();
                        }

                        return body;
                    },
                    ex => ex is ExophaseThrottledException,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return (new List<ExophaseGame>(), false);
                }

                if (Regex.IsMatch(json, "\"games\"\\s*:\\s*false\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    _logger?.Debug("[Exophase] Search returned games:false; treating as no matches");
                    return (new List<ExophaseGame>(), true);
                }

                var result = Serialization.FromJson<ExophaseSearchResult>(json);
                if (result?.Games?.List == null || result.Games.List.Count == 0)
                {
                    return (new List<ExophaseGame>(), true);
                }

                // Filter to only games with achievements (endpoint_awards URL)
                var filtered = result.Games.List
                    .Where(g => g != null && !string.IsNullOrWhiteSpace(g.EndpointAwards))
                    .ToList();

                return (filtered, true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExophaseThrottledException)
            {
                _logger?.Warn($"[Exophase] Search for '{query}' still throttled after retries.");
                return (new List<ExophaseGame>(), false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Exophase search failed");
                return (new List<ExophaseGame>(), false);
            }
        }

        private sealed class ExophaseThrottledException : Exception
        {
        }

        /// <summary>
        /// Builds the archive search URL for a query and optional platform slug. The
        /// generic archive endpoint's platform parameter matches nothing for PSN
        /// platforms, so those route through the environment archive endpoints the
        /// site's own browse pages use, filtered server-side via Exophase's numeric
        /// platform ids where known (PS3 = 7, PS4 = 8, Xbox 360 = 41). Other
        /// platforms keep the legacy generic-endpoint URL.
        /// </summary>
        internal static string BuildSearchUrl(string query, string platformSlug)
        {
            var escapedQuery = Uri.EscapeDataString(query ?? string.Empty);
            var normalizedPlatform = platformSlug?.Trim().ToLowerInvariant();

            switch (normalizedPlatform)
            {
                case "ps3":
                    return $"{PsnSearchUrl}?q={escapedQuery}&sort=added&platforms=7";
                case "ps4":
                    return $"{PsnSearchUrl}?q={escapedQuery}&sort=added&platforms=8";
                case "psn":
                case "ps5":
                case "psvita":
                case "vita":
                    return $"{PsnSearchUrl}?q={escapedQuery}&sort=added";
                case "xbox-360":
                    return $"{XboxSearchUrl}?q={escapedQuery}&sort=added&platforms=41";
                case "xbox":
                case "xbox-one":
                    return $"{XboxSearchUrl}?q={escapedQuery}&sort=added";
                default:
                    var url = $"{SearchUrl}?q={escapedQuery}&sort=added";
                    if (!string.IsNullOrWhiteSpace(normalizedPlatform))
                    {
                        url += $"&platform={Uri.EscapeDataString(normalizedPlatform)}";
                    }

                    return url;
            }
        }

        /// <summary>
        /// Fetches JSON from a URL using offscreen WebView to bypass Cloudflare.
        /// </summary>
        internal async Task<string> FetchJsonViaWebViewAsync(string url, CancellationToken ct)
        {
            try
            {
                return await _offscreenViews.WithNavigableViewAsync(async view =>
                {
                    // Navigate and wait for page load (follows ExophaseSessionManager pattern)
                    await view.NavigateAndWaitAsync(url, timeoutMs: 15000).ConfigureAwait(false);

                    // Get page text (JSON API response displayed as plain text)
                    var pageText = await view.GetPageTextAsync().ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(pageText))
                    {
                        return null;
                    }

                    // Check if we got a Cloudflare challenge page
                    if (pageText.Contains("Just a moment") ||
                        pageText.Contains("Cloudflare") ||
                        pageText.Contains("Verifying you are human"))
                    {
                        _logger?.Warn("[Exophase] Cloudflare challenge detected, search may fail");
                        return null;
                    }

                    return pageText;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Exophase] Failed to fetch JSON via WebView");
                return null;
            }
        }

        /// <summary>
        /// Fetches and parses achievement page HTML to extract achievements.
        /// Uses WebView to bypass Cloudflare protection.
        /// </summary>
        /// <param name="achievementUrl">The achievement page URL (endpoint_awards value).</param>
        /// <param name="acceptLanguage">The Accept-Language header value for localization (not used with WebView).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of AchievementDetail objects, or null if error.</returns>
        public async Task<List<AchievementDetail>> FetchAchievementsAsync(
            string achievementUrl,
            string acceptLanguage,
            CancellationToken ct,
            bool waitForImages = false)
        {
            var fetched = await FetchAchievementsWithHtmlAsync(achievementUrl, acceptLanguage, ct, waitForImages)
                .ConfigureAwait(false);
            return fetched?.Achievements;
        }

        /// <summary>
        /// Same fetch+parse as <see cref="FetchAchievementsAsync"/>, but also returns the raw page HTML so
        /// callers can extract page-level details (e.g. the game header banner) without a second request.
        /// Returns null on hard failure (missing url, empty HTML, or exception); on success the result always
        /// carries the HTML, while <see cref="AchievementFetchResult.Achievements"/> may be null if parsing failed.
        /// </summary>
        internal async Task<AchievementFetchResult> FetchAchievementsWithHtmlAsync(
            string achievementUrl,
            string acceptLanguage,
            CancellationToken ct,
            bool waitForImages = false)
        {
            if (string.IsNullOrWhiteSpace(achievementUrl))
            {
                _logger?.Warn("[Exophase] FetchAchievementsAsync: achievementUrl is null or empty");
                return null;
            }

            try
            {
                var html = await FetchHtmlViaWebViewAsync(achievementUrl, ct, waitForImages).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(html))
                {
                    _logger?.Warn($"[Exophase] No HTML fetched for achievement URL: {achievementUrl}");
                    return null;
                }

                var result = ParseAchievementsHtml(html);

                if (result == null)
                {
                    // Guessed page names miss routinely; callers decide what a missing page means.
                    _logger?.Debug($"[Exophase] ParseAchievementsHtml returned null for {achievementUrl}");
                }

                return new AchievementFetchResult { Achievements = result, Html = html };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[Exophase] Failed to fetch achievements from {achievementUrl}");
                return null;
            }
        }

        internal sealed class AchievementFetchResult
        {
            public List<AchievementDetail> Achievements { get; set; }

            public string Html { get; set; }
        }

        /// <summary>
        /// An award page read once into what the metadata lookups use, without the page source:
        /// its awards, its award lists by section, its own game image, the same game's pages on
        /// other platforms, and whether Exophase answered with its human check instead. An address
        /// Exophase answers with 404 gives <see cref="ExophaseAwardPage.Missing"/>. Returns null on
        /// hard failure, like <see cref="FetchAchievementsWithHtmlAsync"/>.
        /// </summary>
        internal async Task<ExophaseAwardPage> FetchAwardPageAsync(string achievementUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(achievementUrl))
            {
                return null;
            }

            try
            {
                var status = new System.Runtime.CompilerServices.StrongBox<int?>();
                var html = await FetchHtmlViaWebViewAsync(achievementUrl, ct, httpStatus: status).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                {
                    _logger?.Warn($"[Exophase] No HTML fetched for achievement URL: {achievementUrl}");
                    return null;
                }

                if (status.Value == 404)
                {
                    _logger?.Debug($"[Exophase] No award page at {achievementUrl} (HTTP 404).");
                    return ExophaseAwardPage.Missing;
                }

                var isChallenge = html.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  html.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0;
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var achievements = ParseAchievementsDocument(doc);
                if (achievements == null)
                {
                    _logger?.Debug($"[Exophase] ParseAchievementsHtml returned null for {achievementUrl}");
                }

                var sections = ExophaseGamePageParser.ParseAwardSectionsFrom(doc, out var gameArt);
                return new ExophaseAwardPage(
                    achievements,
                    sections,
                    gameArt,
                    ExophaseGamePageParser.ParseRelatedPagesFrom(doc),
                    isChallenge);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[Exophase] Failed to fetch achievements from {achievementUrl}");
                return null;
            }
        }

        private const string HttpStatusScript =
            "(function () { var nav = performance.getEntriesByType && performance.getEntriesByType('navigation')[0]; " +
            "return nav && nav.responseStatus ? String(nav.responseStatus) : ''; })()";

        /// <summary>The loaded page's HTTP status; null when the browser does not report it.</summary>
        private static async Task<int?> GetHttpStatusAsync(IWebView view)
        {
            var eval = await view.EvaluateScriptAsync(HttpStatusScript).ConfigureAwait(false);
            return eval?.Success == true && int.TryParse(Convert.ToString(eval.Result), out var status) && status > 0
                ? status
                : (int?)null;
        }

        // Developer probe: what the browser shows for an award page address, as JSON.
        private const string AwardPageProbeScript = @"(function () {
  var nav = (performance.getEntriesByType && performance.getEntriesByType('navigation')[0]) || {};
  var meta = function (sel, attr) { var el = document.querySelector(sel); return el ? el.getAttribute(attr) : null; };
  var text = function (sel) { var el = document.querySelector(sel); return el ? (el.innerText || '').trim().substring(0, 200) : null; };
  return JSON.stringify({
    href: location.href,
    title: document.title,
    status: nav.responseStatus === undefined ? null : nav.responseStatus,
    navType: nav.type || null,
    awardRows: document.querySelectorAll('ul.achievement > li, ul.trophy > li, ul.challenge > li, ul[class*=achievement] > li, ul[class*=trophy] > li, ul[class*=challenge] > li').length,
    h1: text('h1'),
    canonical: meta('link[rel=canonical]', 'href'),
    ogUrl: meta('meta[property=""og:url""]', 'content'),
    ogTitle: meta('meta[property=""og:title""]', 'content'),
    robots: meta('meta[name=robots]', 'content'),
    languageLinks: Array.prototype.map.call(document.querySelectorAll('a[href*=""/trophies/""], a[href*=""/achievements/""]'), function (a) { return a.getAttribute('href'); })
      .filter(function (h) { return /\/(trophies|achievements)\/[a-z]{2}(-[A-Z]{2})?\/?$/.test(h); }).slice(0, 30),
    htmlLength: document.documentElement.outerHTML.length,
    bodyText: (document.body ? document.body.innerText : '').replace(/\s+/g, ' ').trim().substring(0, 600)
  });
})()";

        /// <summary>
        /// Developer probe: loads <paramref name="url"/> the way award pages load and returns what
        /// the page shows (address after redirects, title, HTTP status, award rows, headings) as
        /// JSON, with the page source in <paramref name="html"/>.
        /// </summary>
        internal async Task<(string Json, string Html, long Ms)> ProbeAwardPageAsync(string url, CancellationToken ct)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            return await _offscreenViews.WithNavigableViewAsync(async view =>
            {
                string json;
                string html = null;
                try
                {
                    await view.NavigateAndWaitAsync(url, timeoutMs: 20000).ConfigureAwait(false);
                    await WaitForAwardRowsAsync(view, url, ct).ConfigureAwait(false);
                    var eval = await view.EvaluateScriptAsync(AwardPageProbeScript).ConfigureAwait(false);
                    json = eval?.Success == true ? Convert.ToString(eval.Result) : "{\"error\":\"script failed: " + eval?.Message + "\"}";
                    html = await view.GetPageSourceAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    json = "{\"error\":\"" + ex.GetType().Name + ": " + ex.Message.Replace("\"", "'") + "\"}";
                }

                return (json, html, timer.ElapsedMilliseconds);
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches HTML from a URL using offscreen WebView to bypass Cloudflare.
        /// Restores cookies from snapshot before fetching to ensure authenticated session.
        /// </summary>
        /// <param name="httpStatus">When given, receives the page's HTTP status, or null when the
        /// browser does not report it.</param>
        private async Task<string> FetchHtmlViaWebViewAsync(
            string url,
            CancellationToken ct,
            bool waitForImages = false,
            System.Runtime.CompilerServices.StrongBox<int?> httpStatus = null)
        {
            // Load cookies before creating the WebView. Reuses the per-refresh cache when a cookie
            // session is active; otherwise loads the snapshot for this call.
            var (snapshotLoaded, snapshotCookies) = AcquireFetchCookies();

            try
            {
                return await _offscreenViews.WithNavigableViewAsync(async view =>
                {
                    // Restore cookies from snapshot if available
                    if (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0)
                    {
                        await RestoreCookiesAsync(view, snapshotCookies, ct).ConfigureAwait(false);
                    }
                    else if (_cookieSnapshotStore != null)
                    {
                        _logger?.Warn("[Exophase] No snapshot cookies to restore - fetching may not show unlocked achievements");
                    }

                    await view.NavigateAndWaitAsync(url, timeoutMs: 20000).ConfigureAwait(false);
                    if (httpStatus != null)
                    {
                        httpStatus.Value = await GetHttpStatusAsync(view).ConfigureAwait(false);
                    }

                    // A client without a cookie store reads public pages, which never carry unlock
                    // data, so it takes the page as loaded instead of waiting for unlocks to appear.
                    var waitForUnlocks = _cookieSnapshotStore != null;
                    if (waitForUnlocks)
                    {
                        // Wait for JavaScript to populate unlock status (loaded async after initial render)
                        await Task.Delay(1000, ct).ConfigureAwait(false);

                        // Poll a cheap renderer-side readiness signal, then serialize the (multi-MB for
                        // large games) page source across the CEF boundary once instead of per attempt.
                        await PollAsync(
                            async _ =>
                            {
                                var eval = await view.EvaluateScriptAsync(AchievementDomReadyScript).ConfigureAwait(false);
                                return eval?.Success == true &&
                                       string.Equals(Convert.ToString(eval.Result), bool.TrueString, StringComparison.OrdinalIgnoreCase);
                            },
                            ready => ready,
                            AchievementDomReadyPollAttempts,
                            AchievementDomReadyPollDelayMs,
                            ct).ConfigureAwait(false);
                    }
                    else if (httpStatus?.Value != 404)
                    {
                        // A missing page has no rows to wait for.
                        await WaitForAwardRowsAsync(view, url, ct).ConfigureAwait(false);
                    }

                    var html = await view.GetPageSourceAsync().ConfigureAwait(false);
                    if (waitForUnlocks && (!ContainsAchievementMarkup(html) || !HasUnlockDataPopulated(html)))
                    {
                        // The readiness script can race the final DOM mutation; retry the fetch once.
                        await Task.Delay(AchievementDomReadyPollDelayMs, ct).ConfigureAwait(false);
                        html = await view.GetPageSourceAsync().ConfigureAwait(false);
                    }

                    if (string.IsNullOrWhiteSpace(html))
                    {
                        return null;
                    }

                    // Check if we got a Cloudflare challenge page
                    if (html.Contains("Just a moment") ||
                        html.Contains("Cloudflare") ||
                        html.Contains("Verifying you are human"))
                    {
                        _logger?.Warn("[Exophase] Cloudflare challenge detected on achievement page");
                        return null;
                    }

                    // Warm Exophase's CDN before callers download award thumbnails over HTTP.
                    // Award images carry the real CDN URL only in data-normal (src is empty or a
                    // placeholder) and the offscreen page never scrolls, so the browser would never
                    // request data-normal on its own. Force each image's real URL to load, then wait
                    // for those requests to finish. The CDN generates the (lazily-created) thumbnail
                    // on this first request, so the subsequent HTTP download hits 200 instead of 404.
                    if (waitForImages)
                    {
                        var imageCount = await GetDocumentImageCountAsync(view).ConfigureAwait(false);
                        if (imageCount > MaxImageWarmCount)
                        {
                            _logger?.Info($"[Exophase] Skipping in-browser CDN warm for {imageCount} images (cap {MaxImageWarmCount}); each download attempt still triggers CDN generation, and thumbnails not ready this run are picked up by a later refresh.");
                        }
                        else
                        {
                            await ForceLazyImagesToLoadAsync(view, ct).ConfigureAwait(false);
                            await WaitForImagesLoadedAsync(view, ct).ConfigureAwait(false);
                        }
                    }

                    return html;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Exophase] Failed to fetch HTML via WebView");
                return null;
            }
        }

        /// <summary>
        /// Waits for a public page's award list to finish rendering: none when the page has no
        /// list (a guessed page that does not exist), otherwise until the row count holds for two
        /// reads, within a cap.
        /// </summary>
        private async Task WaitForAwardRowsAsync(IWebView view, string url, CancellationToken ct)
        {
            var first = await CountAwardRowsAsync(view).ConfigureAwait(false);
            if (first < 0)
            {
                return;
            }

            var previous = first;
            var steady = 0;
            for (var attempt = 0; attempt < AwardRowsPollAttempts && steady < 2; attempt++)
            {
                await Task.Delay(AwardRowsPollDelayMs, ct).ConfigureAwait(false);
                var count = await CountAwardRowsAsync(view).ConfigureAwait(false);
                steady = count == previous ? steady + 1 : 0;
                previous = count;
            }

            if (previous != first)
            {
                _logger?.Debug($"[Exophase] {url}: award rows grew from {first} to {previous} while rendering.");
            }
        }

        private static async Task<int> CountAwardRowsAsync(IWebView view)
        {
            var eval = await view.EvaluateScriptAsync(AwardRowCountScript).ConfigureAwait(false);
            if (eval?.Success != true || eval.Result == null)
            {
                return -1;
            }

            try
            {
                return Convert.ToInt32(eval.Result);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Fetches a rendered public Exophase page through the same WebView path used by achievement pages.
        /// </summary>
        internal async Task<string> FetchRenderedHtmlAsync(string url, CancellationToken ct, int postLoadDelayMs = 1000)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var (snapshotLoaded, snapshotCookies) = AcquireFetchCookies();

            try
            {
                return await _offscreenViews.WithNavigableViewAsync(async view =>
                {
                    if (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0)
                    {
                        await RestoreCookiesAsync(view, snapshotCookies, ct).ConfigureAwait(false);
                    }

                    await view.NavigateAndWaitAsync(url, timeoutMs: 20000).ConfigureAwait(false);
                    if (postLoadDelayMs > 0)
                    {
                        await Task.Delay(postLoadDelayMs, ct).ConfigureAwait(false);
                    }

                    var html = await view.GetPageSourceAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        return null;
                    }

                    if (html.Contains("Just a moment") ||
                        html.Contains("Cloudflare") ||
                        html.Contains("Verifying you are human"))
                    {
                        _logger?.Warn("[Exophase] Cloudflare challenge detected on rendered page");
                        return null;
                    }

                    return html;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Exophase] Failed to fetch rendered HTML via WebView");
                return null;
            }
        }

        /// <summary>
        /// Fetches a rendered profile page and reads the server-rendered JS globals the public API
        /// needs: window.playerProfileId (the numeric top-level profile id the games-list endpoint
        /// takes) and window.playerGames (the rich SSR blob holding the 50 most-recently-played games,
        /// the only source of game-level canonical_id/appid). Both are read via script evaluation so
        /// the browser has already decoded the \uXXXX-escaped SSR literal.
        /// </summary>
        internal async Task<ProfilePageFetchResult> FetchProfilePageWithGlobalsAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var (snapshotLoaded, snapshotCookies) = AcquireFetchCookies();

            try
            {
                return await _offscreenViews.WithNavigableViewAsync(async view =>
                {
                    if (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0)
                    {
                        await RestoreCookiesAsync(view, snapshotCookies, ct).ConfigureAwait(false);
                    }

                    await view.NavigateAndWaitAsync(url, timeoutMs: 20000).ConfigureAwait(false);
                    await Task.Delay(1000, ct).ConfigureAwait(false);

                    var html = await view.GetPageSourceAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        return null;
                    }

                    if (html.Contains("Just a moment") ||
                        html.Contains("Cloudflare") ||
                        html.Contains("Verifying you are human"))
                    {
                        _logger?.Warn("[Exophase] Cloudflare challenge detected on profile page");
                        return null;
                    }

                    var result = new ProfilePageFetchResult { Html = html };

                    var idEval = await view
                        .EvaluateScriptAsync("(typeof window.playerProfileId !== 'undefined' && window.playerProfileId !== null) ? String(window.playerProfileId) : ''")
                        .ConfigureAwait(false);
                    if (idEval?.Success == true && idEval.Result != null)
                    {
                        result.PlayerProfileId = Convert.ToString(idEval.Result);
                    }

                    var gamesEval = await view
                        .EvaluateScriptAsync("(typeof window.playerGames === 'string') ? window.playerGames : (window.playerGames ? JSON.stringify(window.playerGames) : '')")
                        .ConfigureAwait(false);
                    if (gamesEval?.Success == true && gamesEval.Result != null)
                    {
                        result.PlayerGamesJson = Convert.ToString(gamesEval.Result);
                    }

                    return result;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Exophase] Failed to fetch profile page with globals via WebView");
                return null;
            }
        }

        internal sealed class ProfilePageFetchResult
        {
            public string Html { get; set; }

            // Raw string values of the SSR globals; empty when the page did not define them.
            public string PlayerProfileId { get; set; }

            public string PlayerGamesJson { get; set; }
        }

        /// <summary>
        /// Restores cookies to a WebView from a snapshot.
        /// </summary>
        private async Task RestoreCookiesAsync(IWebView view, IReadOnlyList<HttpCookie> cookies, CancellationToken ct)
        {
            // Delete existing cookies from ALL possible Exophase domains
            view.DeleteDomainCookies(".exophase.com");
            view.DeleteDomainCookies("exophase.com");
            view.DeleteDomainCookies(".www.exophase.com");
            view.DeleteDomainCookies("www.exophase.com");

            foreach (var cookie in cookies ?? Enumerable.Empty<HttpCookie>())
            {
                ct.ThrowIfCancellationRequested();

                if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
                {
                    continue;
                }

                var cookieCopy = CloneCookie(cookie);
                var originUrl = BuildCookieOriginUrl(cookieCopy);
                view.SetCookies(originUrl, cookieCopy);
            }

            await Task.Delay(250, ct);
        }

        private static HttpCookie CloneCookie(HttpCookie cookie)
        {
            if (cookie == null)
            {
                return null;
            }

            return new HttpCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Expires = cookie.Expires,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                SameSite = cookie.SameSite,
                Priority = cookie.Priority
            };
        }

        private static string BuildCookieOriginUrl(HttpCookie cookie)
        {
            var domain = (cookie?.Domain ?? string.Empty).Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = "www.exophase.com";
            }

            return "https://" + domain;
        }

        /// <summary>
        /// Polls a value factory until a predicate is satisfied or max attempts are exhausted.
        /// Returns the last value regardless of whether the predicate was met.
        /// </summary>
        internal static Task<T> PollAsync<T>(
            Func<CancellationToken, Task<T>> valueFactory,
            Func<T, bool> readyPredicate,
            int maxAttempts,
            int delayMs,
            CancellationToken ct)
        {
            return AsyncPoll.UntilAsync(valueFactory, readyPredicate, maxAttempts, delayMs, ct);
        }

        /// <summary>
        /// Forces lazily-loaded award images to fetch their real CDN URL. Exophase award <c>img</c>
        /// tags keep the real URL in <c>data-normal</c> (with an empty or placeholder <c>src</c>) and
        /// the offscreen page is never scrolled, so the browser never requests those URLs on its own.
        /// Assigning the real URL to <c>src</c> (and forcing eager loading) makes the browser fetch it,
        /// which is what actually warms the CDN so a later HTTP download hits 200 instead of 404.
        /// </summary>
        private static async Task ForceLazyImagesToLoadAsync(IWebView view, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // For each image, promote the first available lazy-load attribute (mirrors ResolveImageUrl)
            // to src and mark it eager so an offscreen image still loads.
            const string script =
                "(function(){try{" +
                "var imgs=Array.prototype.slice.call(document.querySelectorAll('img'));var n=0;" +
                "imgs.forEach(function(img){" +
                "var url=img.getAttribute('data-normal')||img.getAttribute('data-src')||" +
                "img.getAttribute('data-lazy-src')||img.getAttribute('data-original');" +
                "if(!url){return;}" +
                "try{img.loading='eager';}catch(e){}" +
                "if(img.src!==url){img.src=url;}n++;});" +
                "return n;}catch(e){return -1;}})()";

            try
            {
                await view.EvaluateScriptAsync(script).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best-effort warming; fall through to WaitForImagesLoadedAsync regardless.
            }
        }

        /// <summary>
        /// Best-effort wait until the page's images have finished loading in the WebView, so the CDN has
        /// generated the (lazily-created) award thumbnails before callers download them over HTTP.
        /// Bounded and self-cancelling: if the offscreen view is not actually fetching images (the pending
        /// count stops decreasing), it stops early rather than burning the full timeout.
        /// </summary>
        private static async Task WaitForImagesLoadedAsync(IWebView view, CancellationToken ct)
        {
            const int maxAttempts = 24;   // ~6s cap at 250ms
            const int delayMs = 250;
            const int maxStall = 4;       // give up if pending doesn't move for ~1s

            var lastPending = -1;
            var stallCount = 0;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var eval = await view.EvaluateScriptAsync(
                    "(function(){var i=Array.prototype.slice.call(document.images);" +
                    "return i.length + '|' + i.filter(function(x){return !x.complete;}).length;})()")
                    .ConfigureAwait(false);

                var total = -1;
                var pending = -1;
                if (eval?.Success == true && eval.Result != null)
                {
                    var parts = eval.Result.ToString().Split('|');
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], out total);
                        int.TryParse(parts[1], out pending);
                    }
                }

                // No images to wait for (or the view does not expose them) - nothing to warm.
                if (total <= 0 || pending == 0)
                {
                    return;
                }

                if (pending == lastPending)
                {
                    if (++stallCount >= maxStall)
                    {
                        return;
                    }
                }
                else
                {
                    stallCount = 0;
                    lastPending = pending;
                }

                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns the page's img element count, or -1 when evaluation fails.
        /// </summary>
        private static async Task<int> GetDocumentImageCountAsync(IWebView view)
        {
            try
            {
                var eval = await view.EvaluateScriptAsync("document.images.length").ConfigureAwait(false);
                if (eval?.Success == true && eval.Result != null &&
                    int.TryParse(Convert.ToString(eval.Result), out var count))
                {
                    return count;
                }
            }
            catch
            {
                // Best-effort; the caller falls back to warming.
            }

            return -1;
        }

        private static bool ContainsAchievementMarkup(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            return html.IndexOf("award-title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("award-average", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("award-earned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("data-earned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("data-average", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Checks if unlock data has been populated by JavaScript.
        /// Returns true if we see any earned achievements, indicating the page JS has executed.
        /// </summary>
        private static bool HasUnlockDataPopulated(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            // Check for earned class on achievement elements - indicates JS has populated unlock status
            // Pattern: class="col-12 t1 award visible earned" or similar
            return html.IndexOf("award visible earned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   Regex.IsMatch(html, @"data-earned=""[1-9]\d*""", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Parses achievement HTML to extract achievement details.
        /// </summary>
        private List<AchievementDetail> ParseAchievementsHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return ParseAchievementsDocument(doc);
        }

        /// <summary>Achievement details from an already loaded page.</summary>
        private List<AchievementDetail> ParseAchievementsDocument(HtmlDocument doc)
        {
            try
            {
                // XPath: //ul[contains(@class,'achievement') or contains(@class,'trophy') or contains(@class,'challenge')]/li
                var achievementNodes = doc.DocumentNode.SelectNodes(
                    "//ul[contains(@class,'achievement') or contains(@class,'trophy') or contains(@class,'challenge')]/li");

                if (achievementNodes == null || achievementNodes.Count == 0)
                {
                    _logger?.Debug("[Exophase] No achievement list found in HTML");
                    return null;
                }

                var achievements = new List<AchievementDetail>(achievementNodes.Count);

                foreach (var node in achievementNodes)
                {
                    try
                    {
                        var achievement = ParseAchievementNode(node);
                        if (achievement != null)
                        {
                            achievements.Add(achievement);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[Exophase] Failed to parse achievement node.");
                    }
                }

                var legacyKeyCount = achievements.Count(a =>
                    a.ApiName?.StartsWith(ExophaseStableApiNamePrefix, StringComparison.Ordinal) != true);
                if (legacyKeyCount > 0)
                {
                    _logger?.Debug($"[Exophase] {legacyKeyCount} achievement nodes carried no stable award id; using legacy display-derived keys for them.");
                }

                return achievements.Count > 0 ? achievements : null;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Exophase] Failed to parse achievements HTML.");
                return null;
            }
        }

        /// <summary>
        /// Parses a single achievement li node.
        /// </summary>
        private AchievementDetail ParseAchievementNode(HtmlNode node)
        {
            // Extract data-average for GlobalPercentUnlocked
            double? globalPercent = null;
            var dataAverage = node.GetAttributeValue("data-average", "");
            if (!string.IsNullOrWhiteSpace(dataAverage) &&
                double.TryParse(dataAverage, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                globalPercent = percent;
            }

            // Fallback for JS-rendered markup where percentage is text like "95.49% (37.00)".
            if (!globalPercent.HasValue)
            {
                var averageNode = node.SelectSingleNode(".//div[contains(@class,'award-average')]//span") ??
                                 node.SelectSingleNode(".//div[contains(@class,'award-average')]");
                var averageText = WebUtility.HtmlDecode(averageNode?.InnerText?.Trim() ?? "");
                if (!string.IsNullOrWhiteSpace(averageText))
                {
                    var percentMatch = System.Text.RegularExpressions.Regex.Match(
                        averageText,
                        @"([0-9]+(?:\.[0-9]+)?)\s*%",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

                    if (percentMatch.Success &&
                        double.TryParse(percentMatch.Groups[1].Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsedPercent))
                    {
                        globalPercent = parsedPercent;
                    }
                }
            }

            // Extract data-earned for unlock status (0 = locked, Unix timestamp = unlocked)
            var dataEarned = node.GetAttributeValue("data-earned", "0");
            var isUnlocked = dataEarned != "0" && !string.IsNullOrEmpty(dataEarned);

            // data-earned carries the exact unix unlock time when unlocked; prefer it over
            // parsing the localized display text.
            DateTime? unlockTimeUtc = null;
            if (long.TryParse(dataEarned, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var earnedUnixSeconds) &&
                earnedUnixSeconds > 0)
            {
                unlockTimeUtc = DateTimeOffset.FromUnixTimeSeconds(earnedUnixSeconds).UtcDateTime;
            }

            // Check class attribute for unlock-related classes
            var nodeClass = node.GetAttributeValue("class", "");

            // JS-rendered pages may provide unlock state in award-earned text instead of data-earned.
            var earnedNode = node.SelectSingleNode(".//div[contains(@class,'award-earned')]");
            var earnedText = WebUtility.HtmlDecode(earnedNode?.InnerText?.Trim() ?? "");

            // Also check for earned class on the node itself
            var hasEarnedClass = nodeClass.IndexOf("earned", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasUnlockedClass = nodeClass.IndexOf("unlocked", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasCompletedClass = nodeClass.IndexOf("completed", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!string.IsNullOrWhiteSpace(earnedText))
            {
                if (!unlockTimeUtc.HasValue)
                {
                    unlockTimeUtc = ParseExophaseTimestamp(earnedText);
                }

                if (!isUnlocked)
                {
                    var wasUnlockedByText = unlockTimeUtc.HasValue ||
                                 earnedText.IndexOf("earned offline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 earnedText.IndexOf("earned online", StringComparison.OrdinalIgnoreCase) >= 0;

                    isUnlocked = wasUnlockedByText || hasEarnedClass || hasUnlockedClass || hasCompletedClass;
                }
            }
            else
            {
                // No earned text - check classes as fallback
                if (!isUnlocked && (hasEarnedClass || hasUnlockedClass || hasCompletedClass))
                {
                    isUnlocked = true;
                }
            }

            // Extract icon URL from Exophase image attributes. Blizzard/WoW pages put
            // the real CDN URL in data-normal and often leave src empty.
            var iconUrl = ResolveAchievementIconUrl(node);

            // Extract display name from a text or heading
            var nameNode = node.SelectSingleNode(".//a") ?? node.SelectSingleNode(".//h3") ?? node.SelectSingleNode(".//strong");
            var displayName = WebUtility.HtmlDecode(nameNode?.InnerText?.Trim() ?? "");

            // Extract description from div.award-description/p or similar
            var descNode = node.SelectSingleNode(".//div[contains(@class,'award-description')]/p") ??
                           node.SelectSingleNode(".//div[contains(@class,'description')]") ??
                           node.SelectSingleNode(".//p");
            var description = WebUtility.HtmlDecode(descNode?.InnerText?.Trim() ?? "");

            // Check for hidden/secret class
            var isHidden = node.GetAttributeValue("class", "").Contains("secret");

            // Extract platform-specific points/type from award-points section.
            var awardPointsNode = node.SelectSingleNode(".//div[contains(@class,'award-points')]");
            var parsedPoints = ParseAwardPointsValue(awardPointsNode);
            var trophyType = ParseTrophyType(awardPointsNode);
            var isCapstone = string.Equals(trophyType, "platinum", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(displayName))
            {
                _logger?.Warn("[Exophase] Skipping achievement node - no display name found");
                return null;
            }

            // Key on the stable award id (data-master, mirrored in the id attribute); fall back
            // to the legacy display-derived key only when the node carries no id.
            var apiName = TryParseStableAwardId(node, out var stableAwardId)
                ? ExophaseStableApiNamePrefix + stableAwardId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : GenerateApiName(displayName);

            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = displayName,
                Description = description,
                LockedIconPath = iconUrl,
                UnlockedIconPath = iconUrl,
                Points = parsedPoints,
                TrophyType = trophyType,
                IsCapstone = isCapstone,
                Hidden = isHidden,
                GlobalPercentUnlocked = NormalizePercent(globalPercent),
                Rarity = RarityTier.Common,
                Unlocked = isUnlocked,
                UnlockTimeUtc = unlockTimeUtc
            };
        }

        internal static bool TryParseStableAwardId(HtmlNode node, out long stableAwardId)
        {
            stableAwardId = 0;
            var candidate = node.GetAttributeValue("data-master", "");
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = node.GetAttributeValue("id", "");
            }

            return long.TryParse(candidate?.Trim(), System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture, out stableAwardId) &&
                   stableAwardId > 0;
        }

        private static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        internal static string ResolveAchievementIconUrl(HtmlNode node)
        {
            var imgNode = node?.SelectSingleNode(
                    ".//img[contains(concat(' ', normalize-space(@class), ' '), ' award-image ')]") ??
                node?.SelectSingleNode(".//img");

            if (imgNode == null)
            {
                return string.Empty;
            }

            var iconUrl = NormalizeIconUrlCandidate(imgNode.GetAttributeValue("data-normal", string.Empty));
            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                iconUrl = NormalizeIconUrlCandidate(imgNode.GetAttributeValue("src", string.Empty));
            }

            return ToOriginalAwardIconUrl(iconUrl);
        }

        private static readonly Regex AwardIconSizePath = new Regex(
            @"(m\.exophase\.com/[a-z0-9-]+/awards/)[a-z](/)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The original upload of an Exophase award icon. Pages link the 64px copy under
        /// <c>/awards/s/</c>; <c>/awards/o/</c> is the file Exophase received (512px for PS5
        /// trophies), while the larger fixed sizes can be upscaled from a smaller original.
        /// </summary>
        internal static string ToOriginalAwardIconUrl(string url)
        {
            return string.IsNullOrWhiteSpace(url) ? url : AwardIconSizePath.Replace(url, "$1o$2", 1);
        }

        internal static string ResolveImageUrl(HtmlNode node)
        {
            var imgNode = string.Equals(node?.Name, "img", StringComparison.OrdinalIgnoreCase)
                ? node
                : node?.SelectSingleNode(".//img[@data-normal or @data-src or @data-lazy-src or @data-original or @srcset or @src]");
            if (imgNode == null)
            {
                return string.Empty;
            }

            return FirstNonEmpty(
                NormalizeIconUrlCandidate(imgNode.GetAttributeValue("data-normal", string.Empty)),
                NormalizeIconUrlCandidate(imgNode.GetAttributeValue("data-src", string.Empty)),
                NormalizeIconUrlCandidate(imgNode.GetAttributeValue("data-lazy-src", string.Empty)),
                NormalizeIconUrlCandidate(imgNode.GetAttributeValue("data-original", string.Empty)),
                NormalizeIconUrlCandidate(SelectSrcSetCandidate(imgNode.GetAttributeValue("srcset", string.Empty))),
                NormalizeIconUrlCandidate(imgNode.GetAttributeValue("src", string.Empty)));
        }

        private static string SelectSrcSetCandidate(string srcset)
        {
            if (string.IsNullOrWhiteSpace(srcset))
            {
                return string.Empty;
            }

            var candidates = srcset
                .Split(',')
                .Select(candidate => candidate.Trim())
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => candidate.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .ToList();
            return candidates.Count == 0 ? string.Empty : candidates[candidates.Count - 1];
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string NormalizeIconUrlCandidate(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var normalized = WebUtility.HtmlDecode(url.Trim());
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (normalized.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + normalized;
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                var host = Regex.IsMatch(normalized, @"^/(?:[a-z0-9-]+)/(?:games|awards)/", RegexOptions.IgnoreCase)
                    ? "https://m.exophase.com"
                    : "https://www.exophase.com";
                return host + normalized;
            }

            if (Regex.IsMatch(normalized, @"^(?:m\.|www\.)?exophase\.com/", RegexOptions.IgnoreCase))
            {
                return "https://" + normalized;
            }

            return normalized;
        }

        private static int? ParseAwardPointsValue(HtmlNode awardPointsNode)
        {
            if (awardPointsNode == null)
            {
                return null;
            }

            var valueNode = awardPointsNode.SelectSingleNode(".//span") ?? awardPointsNode;
            var text = WebUtility.HtmlDecode(valueNode?.InnerText?.Trim() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)");
            if (!match.Success)
            {
                return null;
            }

            return int.TryParse(match.Groups[1].Value, out var points)
                ? (int?)points
                : null;
        }

        private static string ParseTrophyType(HtmlNode awardPointsNode)
        {
            if (awardPointsNode == null)
            {
                return null;
            }

            var iconNode = awardPointsNode.SelectSingleNode(".//i");
            var classNames = iconNode?.GetAttributeValue("class", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(classNames))
            {
                return null;
            }

            if (classNames.IndexOf("exo-icon-trophy-platinum", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "platinum";
            }

            if (classNames.IndexOf("exo-icon-trophy-gold", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "gold";
            }

            if (classNames.IndexOf("exo-icon-trophy-silver", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "silver";
            }

            if (classNames.IndexOf("exo-icon-trophy-bronze", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "bronze";
            }

            return null;
        }

        /// <summary>
        /// Parses an Exophase timestamp string into UTC DateTime.
        /// Examples: "Jan 15, 2024, 8:30 PM", "January 15, 2024, 8:30 PM"
        /// </summary>
        private DateTime? ParseExophaseTimestamp(string timestamp)
        {
            if (string.IsNullOrWhiteSpace(timestamp))
            {
                return null;
            }

            // Exophase often appends timezone label in parentheses (e.g., "(UTC+0)").
            var normalizedTimestamp = timestamp.Trim();
            var timezoneSuffixIndex = normalizedTimestamp.IndexOf(" (UTC", StringComparison.OrdinalIgnoreCase);
            if (timezoneSuffixIndex > 0)
            {
                normalizedTimestamp = normalizedTimestamp.Substring(0, timezoneSuffixIndex).Trim();
            }

            // Try common Exophase formats
            var formats = new[]
            {
                "MMM d, yyyy, h:mm tt",      // Jan 15, 2024, 8:30 PM
                "MMM d, yyyy, h:mm:ss tt",   // Jan 15, 2024, 8:30:00 PM
                "MMMM d, yyyy, h:mm tt",     // January 15, 2024, 8:30 PM
                "MMMM d, yyyy, h:mm:ss tt",  // January 15, 2024, 8:30:00 PM
                "MMM d, yyyy",               // Jan 15, 2024
                "MMMM d, yyyy",              // January 15, 2024
                "d MMM yyyy, h:mm tt",       // 15 Jan 2024, 8:30 PM
                "d MMMM yyyy, h:mm tt",      // 15 January 2024, 8:30 PM
            };

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(normalizedTimestamp, format,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
                    out var parsed))
                {
                    // Convert to UTC
                    return parsed.ToUniversalTime();
                }
            }

            // Fallback to generic parsing
            if (DateTime.TryParse(normalizedTimestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
                out var fallbackParsed))
            {
                return fallbackParsed.ToUniversalTime();
            }

            _logger?.Debug($"[Exophase] Failed to parse timestamp: {timestamp}");
            return null;
        }

        internal static string NormalizeLegacyManualApiName(string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return null;
            }

            var candidate = apiName.Trim();
            if (candidate.StartsWith(ExophaseStableApiNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Stable-id keys are opaque; never re-normalize them through the name pipeline.
                return candidate;
            }

            if (candidate.StartsWith(ExophaseApiNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(ExophaseApiNamePrefix.Length);
            }

            var normalized = NormalizeApiNameCore(candidate);
            return string.IsNullOrWhiteSpace(normalized)
                ? null
                : $"{ExophaseApiNamePrefix}{normalized}";
        }

        /// <summary>
        /// Generates a stable API name from the display name for tracking purposes.
        /// </summary>
        internal static string GenerateApiName(string displayName)
        {
            var normalized = NormalizeApiNameCore(displayName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return $"{ExophaseApiNamePrefix}{Guid.NewGuid():N}";
            }

            return $"{ExophaseApiNamePrefix}{normalized}";
        }

        private static string NormalizeApiNameCore(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.ToLowerInvariant();
            var safeChars = new char[normalized.Length];
            var pos = 0;

            foreach (var c in normalized)
            {
                if (char.IsLetterOrDigit(c))
                {
                    safeChars[pos++] = c;
                }
                else if (char.IsWhiteSpace(c) || c == '_' || c == '-')
                {
                    safeChars[pos++] = '_';
                }
            }

            return new string(safeChars, 0, pos).Trim('_');
        }

        /// <summary>
        /// Maps GlobalLanguage setting to Accept-Language header value.
        /// </summary>
        public static string MapLanguageToAcceptLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "en-US,en;q=0.9";
            }

            var lower = language.ToLowerInvariant().Trim();
            return lower switch
            {
                "english" => "en-US,en;q=0.9",
                "french" or "français" or "fr" => "fr-FR,fr;q=0.9",
                "german" or "deutsch" or "de" => "de-DE,de;q=0.9",
                "spanish" or "español" or "es" => "es-ES,es;q=0.9",
                "italian" or "italiano" or "it" => "it-IT,it;q=0.9",
                "portuguese" or "pt" => "pt-PT,pt;q=0.9",
                "brazilian" or "pt-br" or "brazilian portuguese" => "pt-BR,pt-BR;q=0.9",
                "russian" or "русский" or "ru" => "ru-RU,ru;q=0.9",
                "polish" or "polski" or "pl" => "pl-PL,pl;q=0.9",
                "dutch" or "nederlands" or "nl" => "nl-NL,nl;q=0.9",
                "swedish" or "svenska" or "sv" => "sv-SE,sv;q=0.9",
                "finnish" or "suomi" or "fi" => "fi-FI,fi;q=0.9",
                "danish" or "dansk" or "da" => "da-DK,da;q=0.9",
                "norwegian" or "norsk" or "no" => "nb-NO,nb;q=0.9",
                "japanese" or "日本語" or "ja" => "ja-JP,ja;q=0.9",
                "korean" or "한국어" or "ko" => "ko-KR,ko;q=0.9",
                "schinese" or "simplified chinese" or "简体中文" or "zh-cn" => "zh-CN,zh;q=0.9",
                "tchinese" or "traditional chinese" or "繁體中文" or "zh-tw" => "zh-TW,zh;q=0.9",
                "arabic" or "العربية" or "ar" => "ar-SA,ar;q=0.9",
                "czech" or "čeština" or "cs" => "cs-CZ,cs;q=0.9",
                "hungarian" or "magyar" or "hu" => "hu-HU,hu;q=0.9",
                "turkish" or "türkçe" or "tr" => "tr-TR,tr;q=0.9",
                _ => "en-US,en;q=0.9"
            };
        }
    }

    #region API Response Models

    [DataContract]
    public sealed class ExophaseSearchResult
    {
        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "games")]
        public ExophaseGames Games { get; set; }
    }

    [DataContract]
    public sealed class ExophaseGames
    {
        [DataMember(Name = "list")]
        public List<ExophaseGame> List { get; set; }

        [DataMember(Name = "paging")]
        public ExophasePaging Paging { get; set; }
    }

    [DataContract]
    public sealed class ExophaseGame
    {
        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "platforms")]
        public List<ExophasePlatform> Platforms { get; set; }

        [DataMember(Name = "endpoint_awards")]
        public string EndpointAwards { get; set; }

        [DataMember(Name = "images")]
        public ExophaseImages Images { get; set; }

        // Regional release marker Exophase renders after same-title entries
        // (NA/EU/JP/AS). Optional: absent for single-release games and for
        // endpoints that omit the field, in which case region preference in
        // matching silently degrades to the base-region tie-break.
        [DataMember(Name = "region", IsRequired = false, EmitDefaultValue = false)]
        public string Region { get; set; }
    }

    [DataContract]
    public sealed class ExophasePlatform
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "slug")]
        public string Slug { get; set; }
    }

    [DataContract]
    public sealed class ExophaseImages
    {
        [DataMember(Name = "o")]
        public string O { get; set; }

        [DataMember(Name = "l")]
        public string L { get; set; }

        [DataMember(Name = "m")]
        public string M { get; set; }
    }

    [DataContract]
    public sealed class ExophasePaging
    {
        [DataMember(Name = "total")]
        public int Total { get; set; }

        [DataMember(Name = "page")]
        public int Page { get; set; }

        [DataMember(Name = "limit")]
        public int Limit { get; set; }
    }

    #endregion
}
