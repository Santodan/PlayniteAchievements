using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GameJolt
{
    /// <summary>
    /// Client for GameJolt's internal website JSON API (gamejolt.com/site-api/web/...). All requests
    /// go through an offscreen WebView carrying the stored session cookies, matching the Exophase path:
    /// the site-api is cookie-authenticated (no key/signature), and the browser clears Cloudflare where
    /// the raw HTTP stack would not.
    /// </summary>
    internal sealed class GameJoltApiClient
    {
        internal const string UrlBase = "https://gamejolt.com";
        internal const string UrlLogin = UrlBase + "/login";
        private const string UrlSiteApi = UrlBase + "/site-api";
        private const string UrlProfileFormat = UrlSiteApi + "/web/profile/{0}";
        private const string UrlTrophiesGameFormat = UrlSiteApi + "/web/discover/games/trophies/{0}";
        private const string UrlTrophyPercentagePath = "/site-api/web/profile/trophies/game-trophy-percentage/";

        internal static readonly string[] CookieDomains = { "gamejolt.com", ".gamejolt.com" };

        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly GameJoltCookieSnapshotStore _cookieSnapshotStore;
        private readonly OffscreenViewLeaseSource _offscreenViews;

        private readonly object _cookieSessionLock = new object();
        private List<HttpCookie> _preparedCookies;
        private bool _cookieSessionActive;
        private IDisposable _cookieSessionViewLease;

        internal GameJoltApiClient(IPlayniteAPI playniteApi, ILogger logger, GameJoltCookieSnapshotStore cookieSnapshotStore)
        {
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _logger = logger;
            _cookieSnapshotStore = cookieSnapshotStore;
            _offscreenViews = new OffscreenViewLeaseSource(_playniteApi, _logger);
        }

        /// <summary>
        /// Opens a per-refresh session: loads the cookie snapshot once and leases a single shared
        /// offscreen view so every per-game fetch reuses both instead of paying the cost per call.
        /// </summary>
        internal void BeginCookieSession()
        {
            List<HttpCookie> cookies = null;
            var loaded = _cookieSnapshotStore?.TryLoad(out cookies) ?? false;

            IDisposable previousViewLease;
            lock (_cookieSessionLock)
            {
                _preparedCookies = loaded ? cookies : null;
                _cookieSessionActive = true;
                previousViewLease = _cookieSessionViewLease;
                _cookieSessionViewLease = _offscreenViews.BeginLease();
            }

            previousViewLease?.Dispose();

            if (!loaded || cookies == null || cookies.Count == 0)
            {
                _logger?.Warn("[GameJolt] No snapshot cookies available for this refresh - unlock status may be unavailable.");
            }
        }

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
            return (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0, snapshotCookies);
        }

        /// <summary>
        /// Fetches the profile for a handle and returns the canonical username, or null when the profile
        /// has no user (not logged in, or unknown handle). Used by the session manager to confirm login.
        /// </summary>
        public async Task<string> GetProfileUsernameAsync(string handle, CancellationToken ct)
        {
            var url = string.Format(CultureInfo.InvariantCulture, UrlProfileFormat, GameJoltTrophyMapper.FormatUser(handle));
            var json = await FetchJsonAsync(url, ct).ConfigureAwait(false);
            return GameJoltTrophyMapper.ParseUsername(json);
        }

        /// <summary>
        /// Fetches and merges the achievement schema, per-trophy global unlock percentages, and the user's
        /// unlock status for a GameJolt game. Returns an empty list when the game has no trophies; unlock
        /// status is skipped when no username is supplied (the schema still returns as all-locked).
        ///
        /// All reads happen inside a single offscreen-view session with exactly ONE navigation (to establish
        /// the gamejolt.com origin); definitions, percentages, and unlocks are then read via in-page
        /// same-origin fetch(). Re-navigating the shared view per read intermittently wedged
        /// NavigateAndWaitAsync (a 15s timeout that produced zero percentages), so navigation is done once.
        /// </summary>
        public async Task<List<AchievementDetail>> GetAchievementsAsync(string gameId, string username, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return new List<AchievementDetail>();
            }

            var trophyId = gameId.Trim();
            var (snapshotLoaded, snapshotCookies) = AcquireFetchCookies();

            try
            {
                return await _offscreenViews.WithNavigableViewAsync(async view =>
                {
                    if (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0)
                    {
                        await RestoreCookiesAsync(view, snapshotCookies, ct).ConfigureAwait(false);
                    }

                    // The single navigation: the discover/definitions page is public and reliable, and
                    // establishes the gamejolt.com origin for the in-page fetches that follow.
                    var definitionsUrl = string.Format(CultureInfo.InvariantCulture, UrlTrophiesGameFormat, trophyId);
                    string navText = null;
                    try
                    {
                        await view.NavigateAndWaitAsync(definitionsUrl, timeoutMs: 15000).ConfigureAwait(false);
                        navText = await view.GetPageTextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, $"[GameJolt] Navigation to definitions failed for game {trophyId}; continuing with in-page fetch.");
                    }

                    var definitionsJson = await FetchViaPageScriptAsync(view, definitionsUrl, ct).ConfigureAwait(false);
                    if (!LooksLikeCompleteJson(definitionsJson) && LooksLikeCompleteJson(navText))
                    {
                        definitionsJson = navText;
                    }

                    var achievements = GameJoltTrophyMapper.BuildDefinitions(definitionsJson, trophyId);
                    _logger?.Info($"[GameJolt] Game {trophyId}: definitions length={definitionsJson?.Length ?? 0}, " +
                        $"parsed {achievements.Count} trophy definition(s).");
                    if (achievements.Count == 0)
                    {
                        return achievements;
                    }

                    await ApplyGlobalPercentagesAsync(view, achievements, ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        // Unlocks come from the complete achieved list embedded in the (authenticated)
                        // definitions response - the same source the website's trophy page uses to split
                        // achieved/unachieved (unpaginated, unlike the per-user trophies endpoint).
                        var achievedCount = GameJoltTrophyMapper.ApplyAchievedRecords(achievements, definitionsJson, trophyId);
                        _logger?.Info($"[GameJolt] Game {trophyId}: {achievedCount}/{achievements.Count} unlocked (from trophiesAchieved).");
                    }

                    return achievements;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[GameJolt] Failed to fetch achievements for game {trophyId}");
                return new List<AchievementDetail>();
            }
        }

        /// <summary>
        /// Applies each trophy's global unlock percentage (the value the website shows on trophy open) so
        /// rarity comes from real community data instead of the difficulty fallback. All percentages are
        /// fetched in ONE in-page batch (a single Promise.all over same-origin fetches on the already-
        /// navigated <paramref name="view"/>) rather than one navigation/request per trophy. A missing or
        /// failed value leaves the difficulty-based rarity in place.
        /// </summary>
        private async Task ApplyGlobalPercentagesAsync(IWebView view, IReadOnlyList<AchievementDetail> achievements, CancellationToken ct)
        {
            if (achievements == null || achievements.Count == 0)
            {
                return;
            }

            var byTrophyId = new Dictionary<long, AchievementDetail>();
            foreach (var achievement in achievements)
            {
                if (achievement?.ApiName != null &&
                    long.TryParse(achievement.ApiName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var trophyId) &&
                    !byTrophyId.ContainsKey(trophyId))
                {
                    byTrophyId[trophyId] = achievement;
                }
            }

            if (byTrophyId.Count == 0)
            {
                return;
            }

            var percentages = await FetchPercentagesBatchAsync(view, byTrophyId.Keys.ToList(), ct).ConfigureAwait(false);

            var applied = 0;
            foreach (var pair in percentages)
            {
                if (pair.Value.HasValue && byTrophyId.TryGetValue(pair.Key, out var achievement))
                {
                    GameJoltTrophyMapper.ApplyPercentage(achievement, pair.Value);
                    applied++;
                }
            }

            _logger?.Info($"[GameJolt] Applied global unlock percentage to {applied}/{byTrophyId.Count} trophy(ies).");
        }

        /// <summary>
        /// Fetches the global unlock percentage for every trophy id in one in-page batch: a single script
        /// issues all same-origin requests via <c>Promise.all</c> (the browser throttles concurrency per
        /// host) and stores the combined result, which is then polled once. Per-trophy failures resolve to
        /// null and are simply omitted. Requires <paramref name="view"/> to be on the gamejolt.com origin.
        /// </summary>
        private async Task<Dictionary<long, double?>> FetchPercentagesBatchAsync(IWebView view, IReadOnlyList<long> trophyIds, CancellationToken ct)
        {
            if (trophyIds == null || trophyIds.Count == 0)
            {
                return new Dictionary<long, double?>();
            }

            var idsLiteral = string.Join(",", trophyIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            var kickoff =
                "(function(){try{window.__gjB=undefined;var ids=[" + idsLiteral + "];" +
                "Promise.all(ids.map(function(id){" +
                "return fetch('" + UrlTrophyPercentagePath + "'+id,{headers:{'Accept':'application/json'},credentials:'include'})" +
                ".then(function(r){return r.json();})" +
                ".then(function(j){return {i:id,p:(j&&j.payload&&typeof j.payload.percentage!=='undefined')?j.payload.percentage:null};})" +
                ".catch(function(){return {i:id,p:null};});" +
                "})).then(function(a){window.__gjB=JSON.stringify(a);}).catch(function(){window.__gjB='__ERR__';});" +
                "return true;}catch(e){window.__gjB='__ERR__';return false;}})()";

            try
            {
                await view.EvaluateScriptAsync(kickoff).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[GameJolt] percentage batch kickoff failed");
                return new Dictionary<long, double?>();
            }

            // 84 requests at the browser's ~6/host concurrency complete in a few seconds; cap generously.
            for (var attempt = 0; attempt < 120; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct).ConfigureAwait(false);

                object result;
                try
                {
                    var eval = await view
                        .EvaluateScriptAsync("(typeof window.__gjB==='undefined')?'__PENDING__':window.__gjB")
                        .ConfigureAwait(false);
                    if (eval?.Success != true || eval.Result == null)
                    {
                        continue;
                    }

                    result = eval.Result;
                }
                catch
                {
                    continue;
                }

                var value = Convert.ToString(result);
                if (value == "__PENDING__")
                {
                    continue;
                }

                if (value == "__ERR__")
                {
                    _logger?.Warn("[GameJolt] percentage batch errored");
                    return new Dictionary<long, double?>();
                }

                return GameJoltTrophyMapper.ParsePercentageBatch(value);
            }

            _logger?.Warn("[GameJolt] percentage batch timed out");
            return new Dictionary<long, double?>();
        }

        /// <summary>
        /// Navigates a (per-call) offscreen view to a site-api URL and returns the JSON body, restoring
        /// session cookies first. Used only by the auth probe, which runs before the shared refresh
        /// session exists; per-game achievement reads use the single-navigation path in GetAchievementsAsync.
        /// Retries once after a short delay when the first read is empty.
        /// </summary>
        private async Task<string> FetchJsonAsync(string url, CancellationToken ct)
        {
            var (snapshotLoaded, snapshotCookies) = AcquireFetchCookies();

            try
            {
                return await _offscreenViews.WithNavigableViewAsync(async view =>
                {
                    if (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0)
                    {
                        await RestoreCookiesAsync(view, snapshotCookies, ct).ConfigureAwait(false);
                    }

                    var text = await NavigateAndReadJsonAsync(view, url, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                        text = await NavigateAndReadJsonAsync(view, url, ct).ConfigureAwait(false);
                    }

                    return text;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[GameJolt] Failed to fetch JSON via WebView: {url}");
                return null;
            }
        }

        private async Task<string> NavigateAndReadJsonAsync(IWebView view, string url, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await view.NavigateAndWaitAsync(url, timeoutMs: 15000).ConfigureAwait(false);
            var text = await view.GetPageTextAsync().ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text) &&
                (text.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 text.IndexOf("Verifying you are human", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _logger?.Warn("[GameJolt] Cloudflare challenge detected on site-api request");
                return null;
            }

            // Prefer an in-page same-origin fetch over the navigation's rendered page text. A top-level
            // browser navigation can return the SPA HTML shell, a truncated/partial render, or a different
            // (subset) response than the site's own XHR receives. We are on the gamejolt.com origin after
            // the navigation, so this returns exactly the canonical, complete JSON body the website gets.
            var fetched = await FetchViaPageScriptAsync(view, url, ct).ConfigureAwait(false);
            if (LooksLikeCompleteJson(fetched))
            {
                return fetched;
            }

            if (LooksLikeCompleteJson(text))
            {
                _logger?.Debug($"[GameJolt] Using navigation page text (in-page fetch unavailable) for {url}");
                return text;
            }

            _logger?.Warn($"[GameJolt] Neither in-page fetch nor navigation yielded complete JSON for {url} " +
                $"(navLen={text?.Length ?? 0}, prefix='{Prefix(text, 60)}').");
            return fetched ?? text;
        }

        /// <summary>
        /// Fetches a same-origin URL from inside the currently loaded page via <c>fetch()</c>, polling a
        /// window sentinel for the result (the SDK's EvaluateScriptAsync does not await promises). Returns
        /// the exact response body, avoiding the content-negotiation and text-rendering pitfalls of a
        /// top-level navigation. Requires the view to already be on the gamejolt.com origin.
        /// </summary>
        private async Task<string> FetchViaPageScriptAsync(IWebView view, string url, CancellationToken ct)
        {
            var kickoff =
                "(function(){try{window.__gjR=undefined;" +
                "fetch('" + url + "',{headers:{'Accept':'application/json'},credentials:'include'})" +
                ".then(function(r){return r.text();})" +
                ".then(function(t){window.__gjR=t;})" +
                ".catch(function(){window.__gjR='__ERR__';});return true;}" +
                "catch(e){window.__gjR='__ERR__';return false;}})()";

            try
            {
                await view.EvaluateScriptAsync(kickoff).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[GameJolt] in-page fetch kickoff failed");
                return null;
            }

            for (var attempt = 0; attempt < 40; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(250, ct).ConfigureAwait(false);

                object result;
                try
                {
                    var eval = await view
                        .EvaluateScriptAsync("(typeof window.__gjR==='undefined')?'__PENDING__':window.__gjR")
                        .ConfigureAwait(false);
                    if (eval?.Success != true || eval.Result == null)
                    {
                        continue;
                    }

                    result = eval.Result;
                }
                catch
                {
                    continue;
                }

                var value = Convert.ToString(result);
                if (value == "__PENDING__")
                {
                    continue;
                }

                if (value == "__ERR__")
                {
                    _logger?.Warn($"[GameJolt] in-page fetch errored for {url}");
                    return null;
                }

                return value;
            }

            _logger?.Warn($"[GameJolt] in-page fetch timed out for {url}");
            return null;
        }

        private static bool LooksLikeCompleteJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            var first = trimmed[0];
            var last = trimmed[trimmed.Length - 1];
            return (first == '{' && last == '}') || (first == '[' && last == ']');
        }

        private static string Prefix(string value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var slice = value.Length <= length ? value : value.Substring(0, length);
            return slice.Replace('\n', ' ').Replace('\r', ' ');
        }

        private async Task RestoreCookiesAsync(IWebView view, IReadOnlyList<HttpCookie> cookies, CancellationToken ct)
        {
            foreach (var domain in CookieDomains)
            {
                view.DeleteDomainCookies(domain);
            }

            foreach (var cookie in cookies ?? Enumerable.Empty<HttpCookie>())
            {
                ct.ThrowIfCancellationRequested();

                if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
                {
                    continue;
                }

                var cookieCopy = CloneCookie(cookie);
                view.SetCookies(BuildCookieOriginUrl(cookieCopy), cookieCopy);
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
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
                domain = "gamejolt.com";
            }

            return "https://" + domain;
        }
    }
}
