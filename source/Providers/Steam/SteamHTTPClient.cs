using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Steam.Models;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    public sealed class SteamHttpClient : IDisposable
    {
        internal sealed class OwnedGamesResolutionResult
        {
            public List<OwnedGame> Games { get; } = new List<OwnedGame>();

            public bool WasCanceled { get; set; }
        }

        internal sealed class OwnedGamesResolutionProgressInfo
        {
            public string Text { get; set; }

            public int Current { get; set; }

            public int Max { get; set; }
        }

        private static readonly Uri CommunityBase = new Uri("https://steamcommunity.com/");
        private static readonly Uri StoreBase = new Uri("https://store.steampowered.com/");
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private const int MaxAttempts = 3;
        private const int OwnedGameAppDetailsDelayMs = 500;

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SteamSessionManager _sessionManager;
        private readonly string _failedSteamDateTimesCsvPath;
        private readonly object _failedSteamDateTimesLock = new object();
        private readonly ConcurrentQueue<SteamDatetimeParseFailureEntry> _pendingSteamDatetimeParseFailures =
            new ConcurrentQueue<SteamDatetimeParseFailureEntry>();
        private readonly CookieContainer _cookieJar = new CookieContainer();
        private readonly object _cookieLock = new object();
        private readonly object _cookieSyncStateLock = new object();
        private DateTime _lastCefCookieSyncUtc = DateTime.MinValue;
        private int _steamDatetimeParseFailuresInCurrentScan;

        private const string FailedSteamDateTimesFileName = "failed_steam_datetimes.csv";
        private const string FailedSteamDateTimesHeaderLegacy = "error_time_utc,steam_language,raw_scraped_time";
        private const string FailedSteamDateTimesHeaderCurrent = "error_time_utc,steam_language,game_name,achievement_name,raw_scraped_time";
        private static readonly TimeSpan CEFJarSyncInterval = TimeSpan.FromSeconds(30);

        private struct SteamDatetimeParseFailureEntry
        {
            public string ErrorTimeUtc { get; set; }
            public string SteamLanguage { get; set; }
            public string GameName { get; set; }
            public string AchievementName { get; set; }
            public string RawScrapedTime { get; set; }
        }

        private HttpClient _http;
        private HttpClientHandler _handler;
        private HttpClient _apiHttp;
        private HttpClientHandler _apiHandler;

        public HttpClient ApiHttpClient => _apiHttp;

        public SteamHttpClient(IPlayniteAPI api, ILogger logger, SteamSessionManager sessionManager, string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _failedSteamDateTimesCsvPath = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, FailedSteamDateTimesFileName);

            BuildHttpClientsOnce();

            LoadCookiesFromCefIntoJar();
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
            _apiHttp?.Dispose();
            _apiHandler?.Dispose();
        }

        // ---------------------------------------------------------------------
        // Session Management
        // ---------------------------------------------------------------------

        private async Task<bool> EnsureSessionAsync(CancellationToken ct, bool forceRefresh = false)
        {
            ct.ThrowIfCancellationRequested();

            if (!forceRefresh)
            {
                // Keep HttpClient cookie jar in sync with CEF cookies even when a full
                // session refresh isn't due yet. This avoids stale-cookie drift where
                // browser pages are authenticated but scraper requests look logged out.
                SyncCookieJarFromCefIfNeeded(force: false);
                lock (_cookieLock)
                {
                    if (HasCookiesInJar()) return true;
                }
            }

            _logger?.Debug($"[SteamAch] Probing Steam session (Force={forceRefresh})...");
            var result = await _sessionManager.ProbeAuthStateAsync(ct).ConfigureAwait(false);
            SyncCookieJarFromCefIfNeeded(force: true);
            lock (_cookieLock)
            {
                if (HasCookiesInJar()) return true;
            }

            return result.IsSuccess;
        }

        private void SyncCookieJarFromCefIfNeeded(bool force)
        {
            if (!force)
            {
                var now = DateTime.UtcNow;
                lock (_cookieSyncStateLock)
                {
                    if ((now - _lastCefCookieSyncUtc) < CEFJarSyncInterval)
                    {
                        return;
                    }

                    _lastCefCookieSyncUtc = now;
                }
            }
            else
            {
                lock (_cookieSyncStateLock)
                {
                    _lastCefCookieSyncUtc = DateTime.UtcNow;
                }
            }

            LoadCookiesFromCefIntoJar();
        }

        private void LoadCookiesFromCefIntoJar()
        {
            using (PerfScope.Start(_logger, "Steam.LoadCookiesFromCefIntoJar", thresholdMs: 50))
            {
                lock (_cookieLock)
                {
                    SteamSessionManager.LoadCefCookiesIntoJar(_api, _logger, _cookieJar);
                }
            }
        }

        private bool HasCookiesInJar()
        {
            bool Has(CookieCollection cc) =>
                cc.Cast<Cookie>().Any(c => c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

            try
            {
                return Has(_cookieJar.GetCookies(CommunityBase))
                    || Has(_cookieJar.GetCookies(StoreBase));
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------------
        // Steam Web API
        // ---------------------------------------------------------------------

        public async Task<string> GetWebApiTokenAsync(CancellationToken ct)
        {
            const string url = "https://store.steampowered.com/pointssummary/ajaxgetasyncconfig";

            if (!await EnsureSessionAsync(ct).ConfigureAwait(false))
            {
                _logger?.Warn("[SteamAch] Steam session is not available; cannot resolve store web API token.");
                return null;
            }

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                    req.Headers.Referrer = StoreBase;

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            _logger?.Debug($"[SteamAch] Store token request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                            return null;
                        }

                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            return null;
                        }

                        var root = JsonConvert.DeserializeObject<SteamAsyncConfigResponse>(json);
                        var token = root?.Data?.WebApiToken?.Trim();
                        if (string.IsNullOrWhiteSpace(token))
                        {
                            _logger?.Debug($"[SteamAch] Store token response did not include a usable token (success={root?.Success?.ToString() ?? "null"}).");
                            return null;
                        }

                        return token;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SteamAch] Store token request failed.");
                return null;
            }
        }

        internal async Task<OwnedGamesResolutionResult> GetOwnedGamesFromSessionAsync(
            CancellationToken ct,
            IProgress<OwnedGamesResolutionProgressInfo> progress = null)
        {
            ct.ThrowIfCancellationRequested();

            _logger?.Info("[SteamAch] Loading owned Steam games from authenticated store session.");
            var ownedAppIds = await GetOwnedAppIdsFromDynamicStoreAsync(ct).ConfigureAwait(false);
            if (ownedAppIds.Count == 0)
            {
                return new OwnedGamesResolutionResult();
            }

            return await GetAppDetailsForOwnedGamesAsync(ownedAppIds, "owned Steam games", ct, progress).ConfigureAwait(false);
        }

        internal async Task<HashSet<int>> GetOwnedAppIdsFromSessionAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var ownedAppIds = await GetOwnedAppIdsFromDynamicStoreAsync(ct).ConfigureAwait(false);
            return new HashSet<int>(ownedAppIds.Where(appId => appId > 0));
        }

        internal async Task<OwnedGamesResolutionResult> ResolveOwnedGamesFromAppIdsAsync(
            IEnumerable<int> appIds,
            string librarySourceName,
            CancellationToken ct,
            IProgress<OwnedGamesResolutionProgressInfo> progress = null)
        {
            var resolvedGames = await GetAppDetailsForOwnedGamesAsync(appIds, librarySourceName, ct, progress).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(librarySourceName))
            {
                foreach (var game in resolvedGames.Games)
                {
                    game.LibrarySourceName = librarySourceName;
                }
            }

            return resolvedGames;
        }

        internal async Task<OwnedGamesResolutionResult> GetFamilySharedGamesFromSessionAsync(
            string steamUserId,
            CancellationToken ct,
            IProgress<OwnedGamesResolutionProgressInfo> progress = null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(steamUserId))
            {
                return new OwnedGamesResolutionResult();
            }

            _logger?.Info("[SteamAch] Loading family-shared Steam games from authenticated store session.");

            var accessToken = await GetWebApiTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger?.Warn("[SteamAch] Unable to locate a Steam web API access token from the authenticated store session. Family-shared imports will be skipped.");
                return new OwnedGamesResolutionResult();
            }

            var sharedAppIds = await GetFamilySharedAppIdsFromAccessTokenAsync(accessToken, steamUserId, ct).ConfigureAwait(false);
            if (sharedAppIds.Count == 0)
            {
                return new OwnedGamesResolutionResult();
            }

            _logger?.Info($"[SteamAch] Steam Family Groups session returned {sharedAppIds.Count} family-shared Steam app ids.");
            return await ResolveOwnedGamesFromAppIdsAsync(sharedAppIds, "Steam Family Sharing", ct, progress).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------------
        // Profile / Achievements
        // ---------------------------------------------------------------------

        public Task<SteamPageResult> GetAchievementsPageAsync(string steamId64, int appId, string language, CancellationToken ct)
            => GetSteamPageAsync($"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/?tab=achievements&l={language ?? "english"}", true, ct);

        public Task<SteamPageResult> GetAchievementsPageByKeyAsync(string steamId64, string statsKey, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(statsKey))
            {
                return Task.FromResult(new SteamPageResult());
            }

            return GetSteamPageAsync($"https://steamcommunity.com/profiles/{steamId64}/stats/{statsKey.Trim()}/?tab=achievements&l={language ?? "english"}", true, ct);
        }

        // ---------------------------------------------------------------------
        // Parsing / Schema
        // ---------------------------------------------------------------------

        public List<ScrapedAchievement> ParseAchievements(string html, bool includeLocked, string language = "english", string gameName = null)
        {
            var safe = html ?? string.Empty;
            if (safe.Length < 200) return new List<ScrapedAchievement>();

            var doc = new HtmlDocument();
            doc.LoadHtml(safe);

            var modernNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]");
            if (modernNodes != null && modernNodes.Count > 0)
            {
                return ParseAchievementRows(doc, modernNodes, includeLocked, language, gameName, preferLegacySiblingIcon: false);
            }

            // Legacy stats pages (e.g., TF2/L4D era) don't wrap rows in a single container.
            // They store title/description in achieveTxtHolder and icon in a preceding achieveImgHolder sibling.
            var legacyNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveTxtHolder')]");
            if (legacyNodes != null && legacyNodes.Count > 0)
            {
                return ParseAchievementRows(doc, legacyNodes, includeLocked, language, gameName, preferLegacySiblingIcon: true);
            }

            var fallbackNodes = doc.DocumentNode.SelectNodes("//*[contains(@class,'achievement') and (.//h3 or .//div[contains(@class,'achieveUnlockTime')])]");
            if (fallbackNodes != null && fallbackNodes.Count > 0)
            {
                return ParseAchievementRows(doc, fallbackNodes, includeLocked, language, gameName, preferLegacySiblingIcon: false);
            }

            return new List<ScrapedAchievement>();
        }

        private async Task<List<int>> GetOwnedAppIdsFromDynamicStoreAsync(CancellationToken ct)
        {
            var url = $"https://store.steampowered.com/dynamicstore/userdata/?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var page = await GetSteamPageAsync(url, true, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(page?.Html))
            {
                _logger?.Warn("[SteamAch] Steam owned-games lookup returned an empty dynamic store payload.");
                return new List<int>();
            }

            try
            {
                var root = JObject.Parse(page.Html);
                var ownedApps = root["rgOwnedApps"] as JArray;
                var appIds = ownedApps?
                    .Values<int>()
                    .Where(appId => appId > 0)
                    .Distinct()
                    .ToList() ?? new List<int>();

                _logger?.Info($"[SteamAch] Dynamic store returned {appIds.Count} owned Steam app ids.");
                return appIds;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[SteamAch] Failed parsing Steam dynamic store owned-games payload.");
                return new List<int>();
            }
        }

        private async Task<OwnedGamesResolutionResult> GetAppDetailsForOwnedGamesAsync(
            IEnumerable<int> ownedAppIds,
            string progressLabel,
            CancellationToken ct,
            IProgress<OwnedGamesResolutionProgressInfo> progress = null)
        {
            var appIds = ownedAppIds?
                .Where(appId => appId > 0)
                .Distinct()
                .ToList() ?? new List<int>();
            var result = new OwnedGamesResolutionResult();
            if (appIds.Count == 0)
            {
                return result;
            }

            for (var index = 0; index < appIds.Count; index++)
            {
                if (ct.IsCancellationRequested)
                {
                    result.WasCanceled = true;
                    break;
                }

                progress?.Report(new OwnedGamesResolutionProgressInfo
                {
                    Text = $"Loading {progressLabel} ({index + 1}/{appIds.Count})...",
                    Current = index + 1,
                    Max = appIds.Count
                });

                if (index > 0)
                {
                    try
                    {
                        await Task.Delay(OwnedGameAppDetailsDelayMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        result.WasCanceled = true;
                        break;
                    }
                }

                var appId = appIds[index];
                var requestUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";

                try
                {
                    var page = await GetSteamPageAsync(requestUrl, true, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(page?.Html))
                    {
                        continue;
                    }

                    var root = JObject.Parse(page.Html);
                    var appNode = root[appId.ToString(CultureInfo.InvariantCulture)] as JObject;
                    if (appNode?["success"]?.Value<bool>() != true)
                    {
                        continue;
                    }

                    var data = appNode["data"] as JObject;
                    var name = data?["name"]?.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    result.Games.Add(new OwnedGame
                    {
                        AppId = appId,
                        Name = name,
                        LibrarySourceName = "Steam"
                    });
                }
                catch (OperationCanceledException)
                {
                    result.WasCanceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[SteamAch] Failed resolving app details for Steam appId={appId}.");
                }
            }

            return result;
        }

        private async Task<HashSet<int>> GetFamilySharedAppIdsFromAccessTokenAsync(string accessToken, string steamUserId, CancellationToken ct)
        {
            var requestUrls = new[]
            {
                "https://api.steampowered.com/IFamilyGroupsService/GetSharedLibraryApps/v1/"
                    + $"?access_token={Uri.EscapeDataString(accessToken)}"
                    + "&family_groupid=0"
                    + $"&steamid={Uri.EscapeDataString(steamUserId)}"
                    + "&include_own=0"
                    + "&include_excluded=1"
                    + "&include_free=1"
                    + "&include_non_games=1"
                    + "&language=english",
                "https://api.steampowered.com/IFamilyGroupsService/GetSharedLibraryApps/v1/"
                    + $"?access_token={Uri.EscapeDataString(accessToken)}"
                    + "&family_groupid=0"
                    + "&include_own=0"
                    + "&include_excluded=1"
                    + "&include_free=1"
                    + "&include_non_games=1"
                    + "&language=english"
            };

            foreach (var url in requestUrls)
            {
                try
                {
                    using (var response = await _apiHttp.GetAsync(url, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            continue;
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var root = JObject.Parse(json);
                        var appIds = root.SelectTokens("$..appid")
                            .Select(token => token.Value<int?>())
                            .Where(appId => appId.HasValue && appId.Value > 0)
                            .Select(appId => appId.Value)
                            .ToHashSet();
                        if (appIds.Count > 0)
                        {
                            return appIds;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[SteamAch] Failed resolving Steam Family Groups shared app ids from access token.");
                }
            }

            return new HashSet<int>();
        }

        private List<ScrapedAchievement> ParseAchievementRows(
            HtmlDocument doc,
            HtmlNodeCollection nodes,
            bool includeLocked,
            string language,
            string gameName,
            bool preferLegacySiblingIcon)
        {
            if (doc == null || nodes == null || nodes.Count == 0)
            {
                return new List<ScrapedAchievement>();
            }

            // Parse the summary progress and use it as the primary unlock source:
            // Steam orders unlocked achievements first, so the first N rows are unlocked.
            var hasProgressSummary = TryParseAchievementCountsFromProgressBar(doc, out var unlockedCountFromProgressBar, out var totalCountFromProgressBar);
            if (hasProgressSummary)
            {
                unlockedCountFromProgressBar = Math.Max(0, Math.Min(unlockedCountFromProgressBar, totalCountFromProgressBar));
                unlockedCountFromProgressBar = Math.Min(unlockedCountFromProgressBar, nodes.Count);
            }

            var results = new List<ScrapedAchievement>();
            int rowIndex = 0;

            foreach (var row in nodes)
            {
                var rowIndexInList = rowIndex;
                rowIndex++;

                if (row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null)
                {
                    continue;
                }

                var unlockNode = row.SelectSingleNode(".//div[contains(@class,'achieveUnlockTime')]");
                var hasUnlockMarker = unlockNode != null;
                var unlockText = ExtractUnlockText(unlockNode);
                var unlockUtc = SteamTimeParser.TryParseSteamUnlockTime(unlockText, language);
                var title = WebUtility.HtmlDecode(row.SelectSingleNode(".//h3")?.InnerText ?? "").Trim();
                var desc = WebUtility.HtmlDecode(row.SelectSingleNode(".//h5")?.InnerText ?? "").Trim();

                // Primary indicator: progress summary ordering (first N rows unlocked).
                // Fallback if summary is missing: presence of Steam's unlock marker.
                bool isUnlocked = hasProgressSummary
                    ? rowIndexInList < unlockedCountFromProgressBar
                    : hasUnlockMarker;

                if (hasUnlockMarker && !unlockUtc.HasValue && !string.IsNullOrWhiteSpace(unlockText))
                {
                    RecordDatetimeParseFailure(language, unlockText, gameName, title);

                    var snippet = unlockText.Substring(0, Math.Min(50, unlockText.Length));
                    var message = $"[SteamAch] ParseAchievements: Failed to parse time (lang={language}) from '{unlockText}' (hex: {BitConverter.ToString(Encoding.UTF8.GetBytes(snippet))})";

                    // Parsing failures are non-fatal when we have the progress summary fallback.
                    if (hasProgressSummary)
                    {
                        _logger?.Debug(message);
                    }
                    else
                    {
                        _logger?.Warn(message);
                    }
                }

                if (!includeLocked && !isUnlocked)
                {
                    continue;
                }

                int? progressNum = null;
                int? progressDenom = null;

                var progressBar = row.SelectSingleNode(".//div[contains(@class,'achievementProgressBar')]");
                if (progressBar != null)
                {
                    var progressText = progressBar.SelectSingleNode(".//div[contains(@class,'progressText')]");
                    if (progressText != null)
                    {
                        var text = WebUtility.HtmlDecode(progressText.InnerText).Trim();
                        var match = Regex.Match(text, @"^(\d+)\s*/\s*(\d+)$");
                        if (match.Success)
                        {
                            progressNum = int.Parse(match.Groups[1].Value);
                            progressDenom = int.Parse(match.Groups[2].Value);
                        }
                    }
                }

                var iconUrl = ResolveAchievementIconUrl(row, preferLegacySiblingIcon);
                var primaryKeyPart = !string.IsNullOrWhiteSpace(title) ? title : iconUrl;
                var secondaryKeyPart = !string.IsNullOrWhiteSpace(desc) ? desc : (unlockUtc.HasValue ? unlockUtc.Value.ToString("O") : "");

                results.Add(new ScrapedAchievement
                {
                    Key = (primaryKeyPart + "|" + secondaryKeyPart).Trim(),
                    DisplayName = title,
                    Description = desc,
                    IconUrl = iconUrl,
                    UnlockTimeUtc = unlockUtc,
                    IsUnlocked = isUnlocked,
                    ProgressNum = progressNum,
                    ProgressDenom = progressDenom
                });
            }

            return results;
        }

        private static string ResolveAchievementIconUrl(HtmlNode row, bool preferLegacySiblingIcon)
        {
            if (row == null)
            {
                return string.Empty;
            }

            if (preferLegacySiblingIcon)
            {
                var legacyImg = row.SelectSingleNode("./preceding-sibling::div[contains(@class,'achieveImgHolder')][1]//img");
                var legacyIconUrl = legacyImg?.GetAttributeValue("src", "")?.Trim() ?? string.Empty;
                if (!IsDecorativeAchievementImage(legacyIconUrl))
                {
                    return legacyIconUrl;
                }
            }

            var primaryImg = row.SelectSingleNode(".//div[contains(@class,'achieveImgHolder')]//img");
            var primaryIconUrl = primaryImg?.GetAttributeValue("src", "")?.Trim() ?? string.Empty;
            if (!IsDecorativeAchievementImage(primaryIconUrl))
            {
                return primaryIconUrl;
            }

            var allImgs = row.SelectNodes(".//img");
            if (allImgs != null)
            {
                foreach (var img in allImgs)
                {
                    var candidate = img?.GetAttributeValue("src", "")?.Trim() ?? string.Empty;
                    if (!IsDecorativeAchievementImage(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return string.Empty;
        }

        private static bool IsDecorativeAchievementImage(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return true;
            }

            return url.IndexOf("achieveBG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("/trans.gif", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Parses the progress bar section to extract unlocked and total achievement counts.
        /// Steam always displays unlocked achievements first, so first N rows are unlocked.
        /// This works across languages by finding the first two numbers in the summary text.
        /// </summary>
        private static bool TryParseAchievementCountsFromProgressBar(HtmlDocument doc, out int unlockedCount, out int totalCount)
        {
            unlockedCount = 0;
            totalCount = 0;

            if (TryParseAchievementCountsFromModernSummary(doc, out unlockedCount, out totalCount))
            {
                return true;
            }

            if (TryParseAchievementCountsFromLegacySummary(doc, out unlockedCount, out totalCount))
            {
                return true;
            }

            return false;
        }

        private static bool TryParseAchievementCountsFromModernSummary(HtmlDocument doc, out int unlockedCount, out int totalCount)
        {
            unlockedCount = 0;
            totalCount = 0;

            // Look for the achievements summary section
            var summaryNode = doc.DocumentNode.SelectSingleNode("//div[@id='topSummaryAchievements']");
            if (summaryNode == null) return false;

            // Newer pages store the summary in a child div; older pages store text directly in topSummaryAchievements.
            var textNode = summaryNode.SelectSingleNode("./div[not(contains(@class,'achieveBar'))]");
            var text = WebUtility.HtmlDecode((textNode?.InnerText ?? summaryNode.InnerText) ?? string.Empty);
            return TryParseUnlockedAndTotalFromText(text, out unlockedCount, out totalCount);
        }

        private static bool TryParseAchievementCountsFromLegacySummary(HtmlDocument doc, out int unlockedCount, out int totalCount)
        {
            unlockedCount = 0;
            totalCount = 0;

            var summaryNode = doc.DocumentNode.SelectSingleNode("//div[@id='achievementStats_all']");
            if (summaryNode == null)
            {
                summaryNode = doc.DocumentNode.SelectSingleNode(
                    "//div[starts-with(@id,'achievementStats_') and not(contains(translate(@style,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'display:none'))]");
            }

            if (summaryNode == null)
            {
                return false;
            }

            var statusTextNode = summaryNode.SelectSingleNode(".//div[contains(@class,'achievementStatusText')]") ?? summaryNode;
            var text = WebUtility.HtmlDecode(statusTextNode.InnerText ?? string.Empty);
            return TryParseUnlockedAndTotalFromText(text, out unlockedCount, out totalCount);
        }

        private static bool TryParseUnlockedAndTotalFromText(string text, out int unlockedCount, out int totalCount)
        {
            unlockedCount = 0;
            totalCount = 0;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Find first two numbers - they represent (unlocked, total) regardless of language
            // English: "12 of 15 (80%) achievements earned"
            // French: "12 sur 15 (80%) succès débloqués"
            // German: "12 von 15 (80%) Errungenschaften freigeschaltet"
            var numberMatches = Regex.Matches(text, @"\b(\d+)\b");
            if (numberMatches.Count >= 2 &&
                int.TryParse(numberMatches[0].Groups[1].Value, out var parsedUnlockedCount) &&
                int.TryParse(numberMatches[1].Groups[1].Value, out var parsedTotalCount) &&
                parsedTotalCount > 0)
            {
                unlockedCount = Math.Max(0, Math.Min(parsedUnlockedCount, parsedTotalCount));
                totalCount = parsedTotalCount;
                return true;
            }

            return false;
        }

        private static string ExtractUnlockText(HtmlNode unlockNode)
        {
            if (unlockNode == null)
            {
                return string.Empty;
            }

            var raw = WebUtility.HtmlDecode(unlockNode.InnerText ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var firstLine = raw
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

            return firstLine ?? raw.Trim();
        }

        // ---------------------------------------------------------------------
        // HTTP Core
        // ---------------------------------------------------------------------

        private async Task<SteamPageResult> GetSteamPageAsync(string url, bool requiresCookies, CancellationToken ct)
        {
            // _logger.Info($"[FAF] GetSteamPageAsync: Requesting {url} (Cookies={requiresCookies})");
            var result = new SteamPageResult { RequestedUrl = url, FinalUrl = url, Html = "", StatusCode = 0, WasRedirected = false };
            if (string.IsNullOrWhiteSpace(url)) return result;

            Uri uri;
            try { uri = new Uri(url); } catch { return result; }

            bool isSteamAuth = uri.Host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                               uri.Host.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase);

            if (requiresCookies && isSteamAuth)
            {
                await EnsureSessionAsync(ct).ConfigureAwait(false);
            }

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                // _logger.Info($"[FAF] GetSteamPageAsync: Attempt {attempt} for {url}");

                using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    // Browser-like headers in typical order
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                    req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
                    req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                    req.Headers.TryAddWithoutValidation("Connection", "keep-alive");

                    if (isSteamAuth)
                        req.Headers.Referrer = uri.Host.Contains("store") ? StoreBase : CommunityBase;

                    try
                    {
                        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            result.StatusCode = resp.StatusCode;
                            result.FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
                            result.WasRedirected = !string.Equals(result.FinalUrl, url, StringComparison.OrdinalIgnoreCase);

                            // _logger.Info($"[FAF] GetSteamPageAsync: Response {resp.StatusCode} for {url}. FinalUrl={result.FinalUrl}");

                            if (requiresCookies && isSteamAuth && attempt == 1)
                            {
                                bool softRedirect = result.FinalUrl.Contains("/login") || result.FinalUrl.Contains("openid");
                                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden || softRedirect)
                                {
                                    // Try CEF fallback before refreshing session
                                    _logger.Info($"[SteamAch] HTTP auth failed, attempting CEF fallback for {url}");
                                    try
                                    {
                                        var (cefFinalUrl, cefHtml) = await _sessionManager.GetSteamPageAsyncCef(url, ct).ConfigureAwait(false);
                                        if (!string.IsNullOrEmpty(cefHtml))
                                        {
                                            if (!LooksUnauthenticatedStatsPayload(cefHtml, cefFinalUrl))
                                            {
                                                // Additional check: verify the HTML actually contains achievement content
                                                if (HasAnyAchievementRows(cefHtml))
                                                {
                                                    _logger.Info($"[SteamAch] CEF fallback succeeded for {url}");
                                                    result.Html = cefHtml;
                                                    result.FinalUrl = cefFinalUrl;
                                                    result.StatusCode = HttpStatusCode.OK;
                                                    result.WasRedirected = !string.Equals(result.FinalUrl, url, StringComparison.OrdinalIgnoreCase);
                                                    return result;
                                                }
                                                _logger.Warn($"[SteamAch] CEF fallback for {url} returned HTML with no achievement rows (length={cefHtml.Length}, finalUrl={cefFinalUrl})");
                                            }
                                            else
                                            {
                                                _logger.Warn($"[SteamAch] CEF fallback returned unauthenticated content for {url}");
                                            }
                                        }
                                        else
                                        {
                                            _logger.Warn($"[SteamAch] CEF fallback returned empty HTML for {url}");
                                        }
                                    }
                                    catch (Exception cefEx)
                                    {
                                        _logger.Warn(cefEx, $"[SteamAch] CEF fallback failed for {url}");
                                    }

                                    await EnsureSessionAsync(ct, forceRefresh: true).ConfigureAwait(false);
                                    continue;
                                }
                            }

                            var responseHtml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                            if (requiresCookies && isSteamAuth && attempt == 1 &&
                                LooksUnauthenticatedStatsPayload(responseHtml, result.FinalUrl))
                            {
                                _logger?.Warn($"[SteamAch] Auth-like stats payload detected for {url} (Status={resp.StatusCode}, Url={result.FinalUrl}). Forcing session refresh and retrying once.");
                                await EnsureSessionAsync(ct, forceRefresh: true).ConfigureAwait(false);
                                continue;
                            }

                            result.Html = responseHtml;
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"[SteamAch] GetSteamPageAsync: Exception on attempt {attempt} for {url}");
                        if (attempt < MaxAttempts) continue;
                        return result;
                    }
                }
            }
            return result;
        }

        private void BuildHttpClientsOnce()
        {
            _handler?.Dispose(); _http?.Dispose();
            _apiHandler?.Dispose(); _apiHttp?.Dispose();

            _cookieJar.PerDomainCapacity = 300;

            _handler = new HttpClientHandler
            {
                CookieContainer = _cookieJar,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = true
            };
            _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };

            _apiHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false
            };
            _apiHttp = new HttpClient(_apiHandler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ---------------------------------------------------------------------
        // Static Helpers
        // ---------------------------------------------------------------------

        public static bool LooksUnauthenticatedStatsPayload(string html, string finalUrl = null)
        {
            return SteamStatsPageClassifier.LooksUnauthenticatedStatsPayload(html, finalUrl);
        }

        public static bool LooksPrivateOrRestrictedStatsPayload(string html, string finalUrl = null)
        {
            return SteamStatsPageClassifier.LooksPrivateOrRestrictedStatsPayload(html, finalUrl);
        }

        public static bool LooksProfileNotFoundStatsPayload(string html, string finalUrl = null)
        {
            return SteamStatsPageClassifier.LooksProfileNotFoundStatsPayload(html, finalUrl);
        }

        public static bool LooksStructurallyUnavailableStatsPayload(string html, string finalUrl = null)
        {
            return SteamStatsPageClassifier.LooksStructurallyUnavailableStatsPayload(html, finalUrl);
        }

        public static bool LooksLoggedOutHeader(string html)
        {
            return SteamStatsPageClassifier.LooksLoggedOutHeader(html);
        }

        public static bool HasAnyAchievementRows(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;
            return html.IndexOf("achievements_list", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("achieveRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("achieveImgHolder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool HasOnlyHiddenAchievementRows(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]") ??
                        doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveTxtHolder')]") ??
                        doc.DocumentNode.SelectNodes("//*[contains(@class,'achievement') and (.//h3 or .//div[contains(@class,'achieveUnlockTime')])]");

            if (nodes == null || nodes.Count == 0)
            {
                return false;
            }

            var hasHiddenRow = false;
            foreach (var row in nodes)
            {
                var isHidden = row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null;
                if (!isHidden)
                {
                    return false;
                }

                hasHiddenRow = true;
            }

            return hasHiddenRow;
        }

        public void ResetSteamDatetimeParseFailuresForScan()
        {
            Interlocked.Exchange(ref _steamDatetimeParseFailuresInCurrentScan, 0);
            while (_pendingSteamDatetimeParseFailures.TryDequeue(out _))
            {
            }
        }

        public int ConsumeSteamDatetimeParseFailuresForScan()
        {
            return Interlocked.Exchange(ref _steamDatetimeParseFailuresInCurrentScan, 0);
        }

        public int FlushSteamDatetimeParseFailuresForScan()
        {
            if (string.IsNullOrWhiteSpace(_failedSteamDateTimesCsvPath))
            {
                while (_pendingSteamDatetimeParseFailures.TryDequeue(out _))
                {
                }
                return 0;
            }

            var drained = new List<SteamDatetimeParseFailureEntry>();
            while (_pendingSteamDatetimeParseFailures.TryDequeue(out var pending))
            {
                drained.Add(pending);
            }

            if (drained.Count == 0)
            {
                return 0;
            }

            try
            {
                var directory = Path.GetDirectoryName(_failedSteamDateTimesCsvPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                lock (_failedSteamDateTimesLock)
                {
                    RotateLegacyDatetimeCsvIfNeeded();

                    var writeHeader = !File.Exists(_failedSteamDateTimesCsvPath);
                    using (var stream = new FileStream(_failedSteamDateTimesCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        if (writeHeader)
                        {
                            writer.WriteLine(FailedSteamDateTimesHeaderCurrent);
                        }

                        foreach (var entry in drained)
                        {
                            writer.WriteLine(string.Join(",",
                                EscapeCsv(entry.ErrorTimeUtc),
                                EscapeCsv(entry.SteamLanguage),
                                EscapeCsv(entry.GameName),
                                EscapeCsv(entry.AchievementName),
                                EscapeCsv(entry.RawScrapedTime)));
                        }
                    }
                }

                return drained.Count;
            }
            catch (Exception ex)
            {
                // Requeue so a later scan can retry persistence instead of losing the data.
                foreach (var entry in drained)
                {
                    _pendingSteamDatetimeParseFailures.Enqueue(entry);
                }

                _logger?.Warn(ex, "[SteamAch] Failed to flush datetime parse failures to CSV.");
                return 0;
            }
        }

        private void RecordDatetimeParseFailure(string language, string rawUnlockTime, string gameName, string achievementName)
        {
            Interlocked.Increment(ref _steamDatetimeParseFailuresInCurrentScan);
            _pendingSteamDatetimeParseFailures.Enqueue(new SteamDatetimeParseFailureEntry
            {
                ErrorTimeUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                SteamLanguage = string.IsNullOrWhiteSpace(language) ? "english" : language.Trim(),
                GameName = string.IsNullOrWhiteSpace(gameName) ? string.Empty : gameName.Trim(),
                AchievementName = string.IsNullOrWhiteSpace(achievementName) ? string.Empty : achievementName.Trim(),
                RawScrapedTime = rawUnlockTime ?? string.Empty
            });
        }

        private void RotateLegacyDatetimeCsvIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(_failedSteamDateTimesCsvPath) || !File.Exists(_failedSteamDateTimesCsvPath))
            {
                return;
            }

            try
            {
                string firstLine;
                using (var stream = new FileStream(_failedSteamDateTimesCsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    firstLine = reader.ReadLine();
                }

                if (!string.Equals(firstLine?.Trim(), FailedSteamDateTimesHeaderLegacy, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_failedSteamDateTimesCsvPath) ?? string.Empty;
                var legacyName = "failed_steam_datetimes_legacy_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
                var legacyPath = Path.Combine(directory, legacyName);
                File.Move(_failedSteamDateTimesCsvPath, legacyPath);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SteamAch] Failed to rotate legacy Steam datetime CSV format.");
            }
        }

        private static string EscapeCsv(string value)
        {
            var safe = value ?? string.Empty;
            if (safe.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return safe;
            }

            return "\"" + safe.Replace("\"", "\"\"") + "\"";
        }
    }
}
