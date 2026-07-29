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
        private const string UrlProfileTrophiesGameFormat = UrlSiteApi + "/web/profile/trophies/game/{0}/{1}";

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
        /// Fetches and merges the achievement schema and the user's unlock status for a GameJolt game.
        /// Returns an empty list when the game has no trophies; unlock status is skipped when no username
        /// is supplied (the schema still returns as all-locked).
        /// </summary>
        public async Task<List<AchievementDetail>> GetAchievementsAsync(string gameId, string username, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return new List<AchievementDetail>();
            }

            var trophyId = gameId.Trim();
            var definitionsUrl = string.Format(CultureInfo.InvariantCulture, UrlTrophiesGameFormat, trophyId);
            var definitionsJson = await FetchJsonAsync(definitionsUrl, ct).ConfigureAwait(false);

            var achievements = GameJoltTrophyMapper.BuildDefinitions(definitionsJson, trophyId);
            if (achievements.Count == 0 || string.IsNullOrWhiteSpace(username))
            {
                return achievements;
            }

            var unlocksUrl = string.Format(
                CultureInfo.InvariantCulture,
                UrlProfileTrophiesGameFormat,
                GameJoltTrophyMapper.FormatUser(username),
                trophyId);
            var unlocksJson = await FetchJsonAsync(unlocksUrl, ct).ConfigureAwait(false);
            GameJoltTrophyMapper.ApplyUnlocks(achievements, unlocksJson, trophyId);

            return achievements;
        }

        /// <summary>
        /// Navigates the offscreen view to a site-api URL (with session cookies restored) and returns the
        /// JSON body rendered as page text. Retries once after a short delay when the first read is empty.
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

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (text.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Verifying you are human", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _logger?.Warn("[GameJolt] Cloudflare challenge detected on site-api request");
                return null;
            }

            return text;
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
