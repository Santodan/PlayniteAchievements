using HtmlAgilityPack;
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
        private sealed class FamilyManagementPageState
        {
            public string AccessToken { get; set; }

            public HashSet<int> SharedAppIds { get; } = new HashSet<int>();
        }

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
        private const int OwnedGameAppDetailsDelayMs = 1250;
        private static readonly TimeSpan OwnedGameAppDetails429Cooldown = TimeSpan.FromMinutes(3);

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SteamSessionManager _sessionManager;
        private readonly SteamApiClient _steamApiClient;
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

        private readonly ConcurrentDictionary<int, Lazy<Task<bool?>>> _hasAchievementsCache =
            new ConcurrentDictionary<int, Lazy<Task<bool?>>>();

        public SteamHttpClient(IPlayniteAPI api, ILogger logger, SteamSessionManager sessionManager, string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _failedSteamDateTimesCsvPath = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, FailedSteamDateTimesFileName);

            BuildHttpClientsOnce();
            _steamApiClient = new SteamApiClient(_apiHttp, _logger);

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

        public Task<Dictionary<int, int>> GetPlaytimesAsync(
            string apiKey, string steamId64, bool includePlayedFreeGames = true)
        {
            return _steamApiClient.GetOwnedGamesAsync(apiKey, steamId64, includePlayedFreeGames);
        }

        internal async Task<HashSet<int>> GetOwnedAppIdsFromSessionAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var ownedAppIds = await GetOwnedAppIdsFromDynamicStoreAsync(ct).ConfigureAwait(false);
            return new HashSet<int>(ownedAppIds.Where(appId => appId > 0));
        }

        internal async Task<OwnedGamesResolutionResult> GetOwnedGamesFromSessionAsync(CancellationToken ct, IProgress<OwnedGamesResolutionProgressInfo> progress = null)
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

            var familyPageState = await GetFamilyManagementPageStateAsync(ct).ConfigureAwait(false);
            if (familyPageState.SharedAppIds.Count > 0)
            {
                _logger?.Info($"[SteamAch] Family-management page state returned {familyPageState.SharedAppIds.Count} family-shared Steam app ids.");
                return await ResolveOwnedGamesFromAppIdsAsync(familyPageState.SharedAppIds, "Steam Family Sharing", ct, progress).ConfigureAwait(false);
            }

            var accessToken = familyPageState.AccessToken;
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
        // Player Summaries
        // ---------------------------------------------------------------------

        public async Task<List<SteamPlayerSummaries>> GetPlayerSummariesAsync(string apiKey, IEnumerable<ulong> steamIds, CancellationToken ct)
        {
            var ids = steamIds?.Where(x => x > 0).Distinct().ToList() ?? new List<ulong>();
            if (ids.Count == 0) return new List<SteamPlayerSummaries>();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger?.Warn("[SteamAch] An API key is required to fetch Steam friend data.");
                return new List<SteamPlayerSummaries>();
            }

            var apiResults = await _steamApiClient.GetPlayerSummariesAsync(apiKey, ids, ct).ConfigureAwait(false);
            return apiResults ?? new List<SteamPlayerSummaries>();
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

        public Task<bool?> GetAppHasAchievementsAsync(string apiKey, int appId, CancellationToken ct)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(apiKey)) return Task.FromResult<bool?>(false);

            return _hasAchievementsCache.GetOrAdd(appId,
                new Lazy<Task<bool?>>(() => FetchAppHasAchievementsAsync(apiKey, appId))).Value;
        }

        private async Task<bool?> FetchAppHasAchievementsAsync(string apiKey, int appId)
        {
            return await _steamApiClient.GetSchemaForGameAsync(apiKey, appId).ConfigureAwait(false);
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
                    _logger?.Info($"[SteamAch] Steam game details resolution canceled after {result.Games.Count} resolved game(s) out of {appIds.Count} requested for {progressLabel}.");
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
                        _logger?.Info($"[SteamAch] Steam game details resolution canceled after {result.Games.Count} resolved game(s) out of {appIds.Count} requested for {progressLabel}.");
                        return result;
                    }
                }

                var appId = appIds[index];
                var requestUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";
                var retriedAfterRateLimit = false;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        using (var response = await _apiHttp.GetAsync(requestUrl, ct).ConfigureAwait(false))
                        {
                            if (response.StatusCode == (HttpStatusCode)429)
                            {
                                var retryDelay = GetOwnedGameAppDetailsRetryDelay(response);
                                if (retriedAfterRateLimit)
                                {
                                    _logger?.Warn($"[SteamAch] App details request hit 429 again after cooldown. Aborting remaining owned-games import requests. appId={appId}");
                                    return result;
                                }

                                retriedAfterRateLimit = true;
                                _logger?.Warn($"[SteamAch] App details request hit 429 Too Many Requests. Pausing owned-games import for {(int)retryDelay.TotalMinutes} minute(s) before retrying. appId={appId}");
                                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                                continue;
                            }

                            if (response.StatusCode == HttpStatusCode.Forbidden)
                            {
                                _logger?.Warn($"[SteamAch] App details request returned 403 Forbidden. Aborting remaining owned-games import requests to avoid hammering Steam. appId={appId}");
                                return result;
                            }

                            if (!response.IsSuccessStatusCode)
                            {
                                _logger?.Warn($"[SteamAch] App details request failed for owned game appId={appId}: {(int)response.StatusCode} {response.ReasonPhrase}");
                                break;
                            }

                            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(json))
                            {
                                result.Games.AddRange(ParseOwnedGameAppDetails(json));
                            }

                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        result.WasCanceled = true;
                        _logger?.Info($"[SteamAch] Steam game details resolution canceled after {result.Games.Count} resolved game(s) out of {appIds.Count} requested for {progressLabel}.");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, $"[SteamAch] Failed loading public app details for owned game appId={appId}.");
                        break;
                    }
                }
            }

            _logger?.Info($"[SteamAch] Resolved {result.Games.Count} Steam games with public app details for {progressLabel}.");
            return result;
        }

        private static TimeSpan GetOwnedGameAppDetailsRetryDelay(HttpResponseMessage response)
        {
            try
            {
                var retryAfter = response?.Headers?.RetryAfter;
                if (retryAfter?.Delta.HasValue == true && retryAfter.Delta.Value > TimeSpan.Zero)
                {
                    return retryAfter.Delta.Value;
                }

                if (retryAfter?.Date.HasValue == true)
                {
                    var delay = retryAfter.Date.Value.UtcDateTime - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        return delay;
                    }
                }
            }
            catch
            {
            }

            return OwnedGameAppDetails429Cooldown;
        }

        private static IEnumerable<OwnedGame> ParseOwnedGameAppDetails(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                yield break;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch
            {
                yield break;
            }

            foreach (var property in root.Properties())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                {
                    continue;
                }

                var envelope = property.Value as JObject;
                if (envelope?["success"]?.Value<bool>() != true)
                {
                    continue;
                }

                var data = envelope["data"] as JObject;
                if (data == null)
                {
                    continue;
                }

                var type = data["type"]?.Value<string>()?.Trim();
                if (!IsImportableOwnedGameType(type))
                {
                    continue;
                }

                var name = data["name"]?.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                yield return new OwnedGame
                {
                    AppId = appId,
                    Name = name
                };
            }
        }

        private async Task<FamilyManagementPageState> GetFamilyManagementPageStateAsync(CancellationToken ct)
        {
            var state = new FamilyManagementPageState();
            var candidateUrls = new[]
            {
                "https://store.steampowered.com/account/familymanagement",
                "https://store.steampowered.com/points/shop/",
                "https://store.steampowered.com/"
            };

            foreach (var url in candidateUrls)
            {
                var page = await GetSteamPageAsync(url, true, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(state.AccessToken))
                {
                    state.AccessToken = TryExtractSteamWebApiAccessToken(page?.Html);
                }

                if (state.SharedAppIds.Count == 0)
                {
                    state.SharedAppIds.UnionWith(ExtractFamilySharedAppIdsFromHtml(page?.Html));
                }

                if (!string.IsNullOrWhiteSpace(state.AccessToken) && state.SharedAppIds.Count > 0)
                {
                    return state;
                }
            }

            var dynamicState = await TryExtractFamilyManagementPageStateFromScriptAsync(ct).ConfigureAwait(false);
            if (dynamicState != null)
            {
                if (string.IsNullOrWhiteSpace(state.AccessToken))
                {
                    state.AccessToken = dynamicState.AccessToken;
                }

                state.SharedAppIds.UnionWith(dynamicState.SharedAppIds);
            }

            return state;
        }

        private async Task<FamilyManagementPageState> TryExtractFamilyManagementPageStateFromScriptAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<FamilyManagementPageState>(TaskCreationOptions.RunContinuationsAsynchronously);

            await _api.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        await view.NavigateAndWaitAsync("https://store.steampowered.com/account/familymanagement", timeoutMs: 15000).ConfigureAwait(false);
                        await Task.Delay(1500, ct).ConfigureAwait(false);

                        if (!view.CanExecuteJavascriptInMainFrame)
                        {
                            tcs.TrySetResult(new FamilyManagementPageState());
                            return;
                        }

                        var script = @"(() => {
                            const seenTokens = new Set();
                            const tokens = [];
                            const seenAppIds = new Set();
                            const appIds = [];
                            const pushToken = (value) => {
                                if (typeof value !== 'string') return;
                                const trimmed = value.trim();
                                if (!trimmed || trimmed.length < 20) return;
                                if (seenTokens.has(trimmed)) return;
                                seenTokens.add(trimmed);
                                tokens.push(trimmed);
                            };
                            const pushAppId = (value) => {
                                const parsed = typeof value === 'number'
                                    ? value
                                    : parseInt(String(value || '').trim(), 10);
                                if (!Number.isInteger(parsed) || parsed <= 0) return;
                                if (seenAppIds.has(parsed)) return;
                                seenAppIds.add(parsed);
                                appIds.push(parsed);
                            };
                            const inspect = (obj, depth = 0) => {
                                if (!obj || depth > 3) return;
                                if (typeof obj === 'string') {
                                    pushToken(obj);
                                    return;
                                }
                                if (Array.isArray(obj)) {
                                    for (const item of obj) inspect(item, depth + 1);
                                    return;
                                }
                                if (typeof obj !== 'object') return;
                                for (const key of Object.keys(obj)) {
                                    let value;
                                    try { value = obj[key]; } catch { continue; }
                                    if (typeof key === 'string' && /token/i.test(key)) {
                                        pushToken(value);
                                    }
                                    if (typeof key === 'string' && /(?:^|_)(appid|app_id|steam_appid|id)$|shared_library_apps|excluded_appids|apps/i.test(key)) {
                                        if (Array.isArray(value)) {
                                            for (const item of value) inspect(item, depth + 1);
                                        } else {
                                            pushAppId(value);
                                        }
                                    }
                                    inspect(value, depth + 1);
                                }
                            };
                            const inspectScriptText = (text) => {
                                if (typeof text !== 'string' || !text) return;
                                const matches = text.match(/(?:appid|app_id|steam_appid)[""']?\s*[:=]\s*[""']?(\d{2,10})/gi) || [];
                                for (const match of matches) {
                                    const appIdMatch = match.match(/(\d{2,10})/);
                                    if (appIdMatch) pushAppId(appIdMatch[1]);
                                }
                                const hrefMatches = text.match(/\/app\/(\d{2,10})/gi) || [];
                                for (const match of hrefMatches) {
                                    const appIdMatch = match.match(/(\d{2,10})/);
                                    if (appIdMatch) pushAppId(appIdMatch[1]);
                                }
                            };
                            try {
                                inspect(window.application_config || null);
                                inspect(window.ApplicationConfig || null);
                                inspect(window.g_rgProfileData || null);
                                inspect(window.g_rgAccountData || null);
                                inspect(window.g_AccountData || null);
                                inspect(window.g_rgWalletInfo || null);
                                inspect(window.g_rgFamilyGroup || null);
                                inspect(window.family_group || null);
                                pushToken(window.webapi_token);
                                pushToken(window.loyalty_webapi_token);
                                pushToken(localStorage.getItem('webapi_token'));
                                pushToken(localStorage.getItem('loyalty_webapi_token'));
                                pushToken(sessionStorage.getItem('webapi_token'));
                                pushToken(sessionStorage.getItem('loyalty_webapi_token'));
                                for (const scriptElement of Array.from(document.scripts || [])) {
                                    inspectScriptText(scriptElement.textContent || '');
                                }
                                inspectScriptText(document.documentElement ? document.documentElement.innerHTML : '');
                            } catch {}
                            return JSON.stringify({ tokens, appIds });
                        })();";

                        var evaluationResult = await view.EvaluateScriptAsync(script).ConfigureAwait(false);
                        if (evaluationResult == null || !evaluationResult.Success)
                        {
                            if (evaluationResult != null && !string.IsNullOrWhiteSpace(evaluationResult.Message))
                            {
                                _logger?.Debug($"[SteamAch] Steam web API token script probe failed: {evaluationResult.Message}");
                            }

                            tcs.TrySetResult(new FamilyManagementPageState());
                            return;
                        }

                        var state = ExtractFamilyManagementPageStateFromEvaluateScriptResult(evaluationResult);
                        tcs.TrySetResult(state ?? new FamilyManagementPageState());
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult(new FamilyManagementPageState());
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[SteamAch] Failed extracting family-management page state from script.");
                    tcs.TrySetResult(new FamilyManagementPageState());
                }
            });

            return await tcs.Task.ConfigureAwait(false);
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
                using (var response = await _apiHttp.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var sharedAppIds = ExtractFamilySharedAppIds(json);
                    if (sharedAppIds.Count > 0)
                    {
                        return sharedAppIds;
                    }
                }
            }

            return new HashSet<int>();
        }

        private static string TryExtractSteamWebApiAccessToken(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var patterns = new[]
            {
                "(?:\\\"|')(?<key>webapi_token|webapiToken|loyalty_webapi_token|loyaltyWebapiToken)(?:\\\"|')\\s*[:=]\\s*(?:\\\"|')(?<token>[^\\\"']+)(?:\\\"|')",
                "(?:data-webapi-token|data-loyalty-webapi-token|data-loyalty_webapi_token)\\s*=\\s*(?:\\\"|')(?<token>[^\\\"']+)(?:\\\"|')"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var token = match.Groups["token"].Value?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                token = token.Replace("\\/", "/");
                try
                {
                    token = Regex.Unescape(token);
                }
                catch
                {
                }

                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }

            return null;
        }

        private static FamilyManagementPageState ExtractFamilyManagementPageStateFromEvaluateScriptResult(JavaScriptEvaluationResult evaluationResult)
        {
            var rawResult = evaluationResult?.Result?.ToString();
            if (string.IsNullOrWhiteSpace(rawResult))
            {
                return null;
            }

            var normalized = rawResult.Trim();
            if (normalized.Length >= 2 && normalized[0] == '"' && normalized[normalized.Length - 1] == '"')
            {
                try
                {
                    normalized = JToken.Parse(normalized).Value<string>() ?? normalized;
                }
                catch
                {
                }
            }

            try
            {
                var parsed = JObject.Parse(normalized);
                var state = new FamilyManagementPageState();

                foreach (var token in parsed["tokens"]?.Values<string>() ?? Enumerable.Empty<string>())
                {
                    var candidate = token?.Trim();
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        continue;
                    }

                    if (candidate.Length >= 20)
                    {
                        state.AccessToken = candidate;
                        break;
                    }
                }

                foreach (var token in parsed["appIds"] ?? Enumerable.Empty<JToken>())
                {
                    var appId = TryExtractFamilySharedAppId(token);
                    if (appId > 0)
                    {
                        state.SharedAppIds.Add(appId);
                    }
                }

                return state;
            }
            catch
            {
            }

            return new FamilyManagementPageState();
        }

        private static HashSet<int> ExtractFamilySharedAppIdsFromHtml(string html)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return result;
            }

            var matches = Regex.Matches(
                html,
                "(?:appid|app_id|steam_appid)[\\\"']?\\s*[:=]\\s*[\\\"']?(?<appId>\\d{2,10})|/app/(?<hrefAppId>\\d{2,10})",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var raw = match.Groups["appId"].Success
                    ? match.Groups["appId"].Value
                    : match.Groups["hrefAppId"].Value;

                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) && appId > 0)
                {
                    result.Add(appId);
                }
            }

            return result;
        }

        private static HashSet<int> ExtractFamilySharedAppIds(string json)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            try
            {
                var root = JObject.Parse(json);
                var appTokens = root.SelectTokens("$..apps[*]")
                    .Concat(root.SelectTokens("$..shared_library_apps[*]"));

                foreach (var token in appTokens)
                {
                    var appId = TryExtractFamilySharedAppId(token);
                    if (appId > 0)
                    {
                        result.Add(appId);
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static int TryExtractFamilySharedAppId(JToken token)
        {
            if (token == null)
            {
                return 0;
            }

            if (token.Type == JTokenType.Integer)
            {
                var rawValue = token.Value<long>();
                return rawValue > 0 && rawValue <= int.MaxValue ? (int)rawValue : 0;
            }

            if (token.Type == JTokenType.Object)
            {
                var appIdToken = token["appid"] ?? token["app_id"] ?? token["steam_appid"] ?? token["id"];
                if (appIdToken == null)
                {
                    return 0;
                }

                if (appIdToken.Type == JTokenType.Integer)
                {
                    var rawValue = appIdToken.Value<long>();
                    return rawValue > 0 && rawValue <= int.MaxValue ? (int)rawValue : 0;
                }

                if (int.TryParse(appIdToken.Value<string>(), out var parsedAppId) && parsedAppId > 0)
                {
                    return parsedAppId;
                }
            }

            return 0;
        }

        private static bool IsImportableOwnedGameType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return true;
            }

            return string.Equals(type, "game", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "demo", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "mod", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase);
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
