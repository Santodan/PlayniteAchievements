using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamScanner
    {
        private sealed class SteamTransientException : Exception
        {
            public SteamTransientException(string message)
                : base(message)
            {
            }

            public SteamTransientException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }

        private readonly PlayniteAchievementsSettings _settings;
        private readonly SteamSettings _providerSettings;
        private readonly SteamHttpClient _steamClient;
        private readonly SteamSessionManager _sessionManager;
        private readonly SteamApiClient _steamApiClient;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        public SteamScanner(
            PlayniteAchievementsSettings settings,
            SteamSettings providerSettings,
            SteamHttpClient steamClient,
            SteamSessionManager sessionManager,
            SteamApiClient steamApiClient,
            IPlayniteAPI api,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _steamClient = steamClient ?? throw new ArgumentNullException(nameof(steamClient));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _steamApiClient = steamApiClient ?? throw new ArgumentNullException(nameof(steamApiClient));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            _steamClient.ResetSteamDatetimeParseFailuresForScan();

            try
            {
                var apiKey = _providerSettings.SteamApiKey?.Trim();
                var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
                if (!hasApiKey)
                {
                    _logger?.Warn("[SteamAch] Steam API key is missing. Falling back to web-authenticated scanning with anonymous schema metadata where available.");
                }

                _logger?.Info("[SteamAch] Probing Steam login status before scan...");
                var probeResult = await _sessionManager.ProbeAuthStateAsync(cancel).ConfigureAwait(false);
                var steamUserId = probeResult?.UserId?.Trim();
                if (!probeResult.IsSuccess || string.IsNullOrWhiteSpace(steamUserId))
                {
                    _logger?.Warn("[SteamAch] Steam web auth check failed: not logged in. Aborting scan.");
                    return new RebuildPayload
                    {
                        Summary = new RebuildSummary(),
                        AuthRequired = true
                    };
                }
                _logger?.Info("[SteamAch] Steam web auth verified.");

                if (gamesToRefresh is null || gamesToRefresh.Count == 0)
                {
                    _logger?.Info("[SteamAch] No games found to scan.");
                    return new RebuildPayload { Summary = new RebuildSummary() };
                }

                // Create rate limiter with exponential backoff
                var rateLimiter = new RateLimiter(
                    _settings.Persisted.ScanDelayMs,
                    _settings.Persisted.MaxRetryAttempts);

                return await ProviderRefreshExecutor.RunProviderGamesAsync(
                    gamesToRefresh,
                    onGameStarting,
                    async (game, token) =>
                    {
                        if (!TryGetPlatformAppId(game, out var appId))
                        {
                            _logger?.Warn($"Skipping game without valid AppId: {game?.Name}");
                            return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                        }

                        var data = await rateLimiter.ExecuteWithRetryAsync(
                            () => FetchGameDataAsync(game, steamUserId, token),
                            IsTransientError,
                            token).ConfigureAwait(false);

                        return new ProviderRefreshExecutor.ProviderGameResult
                        {
                            Data = data
                        };
                    },
                    onGameCompleted,
                    isAuthRequiredException: _ => false,
                    onGameError: (game, ex, consecutiveErrors) =>
                    {
                        var appIdText = TryGetPlatformAppId(game, out var appId) ? appId.ToString() : "?";
                        _logger?.Warn($"[SteamAch] Skipping game after retries: {game?.Name} (appId={appIdText}). Consecutive errors={consecutiveErrors}. {ex.GetType().Name}: {ex.Message}");
                    },
                    delayBetweenGamesAsync: (index, token) => rateLimiter.DelayBeforeNextAsync(token),
                    delayAfterErrorAsync: (consecutiveErrors, token) => rateLimiter.DelayAfterErrorAsync(consecutiveErrors, token),
                    cancel).ConfigureAwait(false);
            }
            finally
            {
                ShowDatetimeParseFailureToastIfNeeded();
            }
        }

        /// <summary>
        /// Determines if an exception is a transient error that should trigger retry.
        /// </summary>
        private static bool IsTransientError(Exception ex)
        {
            if (ex is OperationCanceledException) return false;
            if (ex is SteamTransientException) return true;

            // WebException with transient status codes
            if (ex is WebException webEx && webEx.Response is HttpWebResponse response)
            {
                var statusCode = (int)response.StatusCode;
                // 429 Too Many Requests, 503 Service Unavailable, 502 Bad Gateway, 504 Gateway Timeout
                if (statusCode == 429 || statusCode == 502 || statusCode == 503 || statusCode == 504)
                    return true;
            }

            // Network-related exceptions
            var message = ex.Message ?? string.Empty;
            if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (message.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 &&
                message.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (message.IndexOf("temporarily", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            if (ex.InnerException != null && !ReferenceEquals(ex.InnerException, ex))
            {
                return IsTransientError(ex.InnerException);
            }

            return false;
        }

        private void ShowDatetimeParseFailureToastIfNeeded()
        {
            var parseFailureCount = _steamClient.ConsumeSteamDatetimeParseFailuresForScan();
            if (parseFailureCount <= 0)
            {
                return;
            }

            var persistedCount = _steamClient.FlushSteamDatetimeParseFailuresForScan();
            if (persistedCount < parseFailureCount)
            {
                _logger?.Warn($"[SteamAch] Datetime parse failures detected={parseFailureCount}, persisted_to_csv={persistedCount}. CSV write may have failed for some entries.");
            }

            var message = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Error_SteamDatetimeParse"),
                parseFailureCount);
            _logger?.Warn($"[SteamAch] {message}");

            try
            {
                var title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
                _api?.Notifications?.Add(new NotificationMessage(
                    $"PlayniteAchievements-SteamDatetimeParse-{Guid.NewGuid()}",
                    $"{title}\n{message}",
                    NotificationType.Error));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SteamAch] Failed to show datetime parse failure toast.");
            }
        }

        private static bool TryGetPlatformAppId(Game game, out int appId)
        {
            appId = 0;
            return game != null &&
                   !string.IsNullOrWhiteSpace(game.GameId) &&
                   int.TryParse(game.GameId, out appId);
        }

        // ---------------------------------------------------------------------
        // Game data building
        // ---------------------------------------------------------------------

        private async Task<GameAchievementData> FetchGameDataAsync(
            Game game,
            string steamUserId,
            CancellationToken cancel)
        {
            if (!TryGetPlatformAppId(game, out var appId))
            {
                _logger?.Warn($"Could not get AppId from game {game.Name}");
                return null;
            }

            var schema = await FetchSchemaAsync(appId, cancel).ConfigureAwait(false);
            var unlocked = await FetchUnlockedAsync(appId, game?.Name, steamUserId, schema, cancel).ConfigureAwait(false);

            var gameData = new GameAchievementData
            {
                AppId = appId,
                GameName = game.Name,
                ProviderKey = "Steam",
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = schema?.Achievements != null && schema.Achievements.Count > 0,
                PlayniteGameId = game.Id,
                Achievements = new List<AchievementDetail>()
            };

            if (gameData.HasAchievements)
            {
                var unlockedApiNames = unlocked?.UnlockedApiNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unlockedTimes = unlocked?.UnlockTimesUtc ?? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                var progressNumDict = unlocked?.ProgressNum ?? new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
                var progressDenomDict = unlocked?.ProgressDenom ?? new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

                foreach (var schemaAch in schema.Achievements)
                {
                    if (string.IsNullOrWhiteSpace(schemaAch.Name))
                        continue;

                    var isUnlocked = unlockedApiNames.Contains(schemaAch.Name);
                    DateTime? unlockTime = null;
                    if (unlockedTimes.TryGetValue(schemaAch.Name, out var time))
                    {
                        unlockTime = time;
                    }

                    double? globalPercent = null;
                    if (schema.GlobalPercentages?.TryGetValue(schemaAch.Name, out var percent) == true)
                    {
                        globalPercent = percent;
                    }

                    progressNumDict.TryGetValue(schemaAch.Name, out var progressNum);
                    progressDenomDict.TryGetValue(schemaAch.Name, out var progressDenom);

                    var detail = new AchievementDetail
                    {
                        ApiName = schemaAch.Name,
                        DisplayName = !string.IsNullOrWhiteSpace(schemaAch.DisplayName)
                            ? schemaAch.DisplayName
                            : schemaAch.Name,
                        Description = schemaAch.Description ?? string.Empty,
                        UnlockedIconPath = schemaAch.Icon,
                        LockedIconPath = schemaAch.IconGray,
                        Points = null,
                        Category = null,
                        Hidden = schemaAch.Hidden == 1,
                        UnlockTimeUtc = unlockTime,
                        Unlocked = isUnlocked,
                        ProgressNum = progressNum,
                        ProgressDenom = progressDenom,
                        Rarity = GetFallbackRarity(
                            schemaAch.Hidden == 1,
                            progressNum,
                            progressDenom)
                    };

                    var normalizedPercent = NormalizePercent(globalPercent);
                    detail.GlobalPercentUnlocked = normalizedPercent;
                    if (normalizedPercent.HasValue)
                    {
                        detail.Rarity = PercentRarityHelper.GetRarityTier(normalizedPercent.Value);
                    }

                    gameData.Achievements.Add(detail);
                }
            }

            return gameData;
        }

        private Task<SchemaAndPercentages> FetchSchemaAsync(int appId, CancellationToken cancel)
        {
            var language = string.IsNullOrWhiteSpace(_settings.Persisted.GlobalLanguage) ? "english" : _settings.Persisted.GlobalLanguage.Trim();
            var apiKey = _providerSettings.SteamApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return TryGetAnonymousSteamSchemaAsync(appId, cancel);
            }

            return _steamApiClient.GetSchemaForGameDetailedAsync(
                apiKey,
                appId,
                language,
                cancel);
        }

        private async Task<UserUnlockedAchievements> FetchUnlockedAsync(
            int appId,
            string gameName,
            string steamUserId,
            SchemaAndPercentages schema,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(steamUserId))
                return new UserUnlockedAchievements();

            // If the schema confirms no achievements, skip HTML scraping entirely.
            // This avoids locale-dependent "no achievements" page handling.
            if (schema != null && (schema.Achievements == null || schema.Achievements.Count == 0))
            {
                return new UserUnlockedAchievements
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    AppId = appId,
                    UnlockedApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    UnlockTimesUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
                    ProgressNum = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase),
                    ProgressDenom = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
                };
            }

            AchievementsScrapeResponse scraped = null;
            try
            {
                scraped = await ScrapeAchievementsAsync(steamUserId, appId, cancel, includeLocked: true, gameName: gameName)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (IsTransientError(ex))
                {
                    throw new SteamTransientException($"[SteamAch] Transient scrape exception for appId={appId}.", ex);
                }

                _logger?.Debug(ex, $"[SteamAch] User achievements scrape failed (non-transient, appId={appId}).");
                return new UserUnlockedAchievements();
            }

            if (scraped == null || scraped.TransientFailure)
            {
                var detail = scraped?.DetailCode.ToString() ?? "Unknown";
                var status = scraped?.StatusCode ?? 0;
                throw new SteamTransientException($"[SteamAch] Transient scrape result for appId={appId}. detail={detail}, status={status}");
            }

            var iconFileToAchievements = new Dictionary<string, List<SchemaAchievement>>(StringComparer.OrdinalIgnoreCase);

            if (schema?.Achievements != null)
            {
                // _logger?.Info($"[SteamAch] FetchUnlockedAsync: Schema has {schema.Achievements.Count} achievements for appId={appId}");

                foreach (var ach in schema.Achievements)
                {
                    if (string.IsNullOrWhiteSpace(ach.Name))
                        continue;

                    var iconFile = ExtractIconFilename(ach.Icon);
                    if (!string.IsNullOrWhiteSpace(iconFile))
                    {
                        if (!iconFileToAchievements.ContainsKey(iconFile))
                            iconFileToAchievements[iconFile] = new List<SchemaAchievement>();
                        iconFileToAchievements[iconFile].Add(ach);
                    }

                    var iconGrayFile = ExtractIconFilename(ach.IconGray);
                    if (!string.IsNullOrWhiteSpace(iconGrayFile))
                    {
                        if (!iconFileToAchievements.ContainsKey(iconGrayFile))
                            iconFileToAchievements[iconGrayFile] = new List<SchemaAchievement>();
                        iconFileToAchievements[iconGrayFile].Add(ach);
                    }
                }

                // _logger?.Info($"[SteamAch] FetchUnlockedAsync: Built iconFileToAchievements with {iconFileToAchievements.Count} icon entries");
            }
            else
            {
                // _logger?.Warn($"[SteamAch] FetchUnlockedAsync: Schema is null or has no achievements for appId={appId}");
            }

            var data = new UserUnlockedAchievements
            {
                LastUpdatedUtc = DateTime.UtcNow,
                AppId = appId,
                UnlockedApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                UnlockTimesUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
                ProgressNum = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase),
                ProgressDenom = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
            };

            if (scraped.Rows != null)
            {
                // _logger?.Info($"[SteamAch] FetchUnlockedAsync: Scraped {scraped.Rows.Count} rows for appId={appId}");
                int withTime = 0, withoutTime = 0, matched = 0, fallbackMatches = 0;

                foreach (var row in scraped.Rows)
                {
                    var iconFile = ExtractIconFilename(row.IconUrl);
                    if (!string.IsNullOrWhiteSpace(iconFile) && iconFileToAchievements.TryGetValue(iconFile, out var achievements))
                    {
                        string apiName = null;

                        if (achievements.Count == 1)
                        {
                            // Icon maps to exactly one achievement - use it directly
                            apiName = achievements[0].Name;
                        }
                        else
                        {
                            // Multiple achievements share this icon - prioritize: Description, then DisplayName
                            var descMatches = achievements.Where(a =>
                                string.Equals(a.Description, row.Description, StringComparison.OrdinalIgnoreCase)).ToList();

                            if (descMatches.Count == 1)
                            {
                                apiName = descMatches[0].Name;
                            }
                            else
                            {
                                // Description matched zero or multiple - fall back to DisplayName
                                apiName = achievements.FirstOrDefault(a =>
                                    string.Equals(a.DisplayName, row.DisplayName, StringComparison.OrdinalIgnoreCase))?.Name;
                            }

                            if (apiName != null)
                                fallbackMatches++;
                        }

                        if (!string.IsNullOrWhiteSpace(apiName))
                        {
                            data.ProgressNum[apiName] = row.ProgressNum;
                            data.ProgressDenom[apiName] = row.ProgressDenom;

                            if (row.IsUnlocked)
                            {
                                data.UnlockedApiNames.Add(apiName);
                                if (row.UnlockTimeUtc.HasValue)
                                {
                                    data.UnlockTimesUtc[apiName] = row.UnlockTimeUtc.Value;
                                }

                                matched++;
                                if (row.UnlockTimeUtc.HasValue) withTime++; else withoutTime++;
                            }
                        }
                    }
                }
                // _logger?.Info($"[SteamAch] FetchUnlockedAsync: Processed rows - withTime={withTime}, withoutTime={withoutTime}, matched={matched}, fallbackMatches={fallbackMatches}");
            }

            return data;
        }

        private static string ExtractIconFilename(string iconUrl)
        {
            if (string.IsNullOrWhiteSpace(iconUrl))
                return null;

            var queryIndex = iconUrl.IndexOf('?');
            if (queryIndex > 0)
                iconUrl = iconUrl.Substring(0, queryIndex);

            var lastSlash = iconUrl.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash >= iconUrl.Length - 1)
                return null;

            return iconUrl.Substring(lastSlash + 1);
        }

        // ---------------------------------------------------------------------
        // Achievements scraping
        // ---------------------------------------------------------------------

        private async Task<AchievementsScrapeResponse> ScrapeAchievementsAsync(
            string steamId64,
            int appId,
            CancellationToken cancel,
            bool includeLocked = false,
            string gameName = null)
        {
            var res = new AchievementsScrapeResponse();

            var resolved = ResolveSteamId64(steamId64);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                res.TransientFailure = true;
                res.SetDetail(SteamScrapeDetail.NoSteamSession);
                res.Rows = new List<ScrapedAchievement>();
                return res;
            }

            var language = string.IsNullOrWhiteSpace(_settings.Persisted.GlobalLanguage) ? "english" : _settings.Persisted.GlobalLanguage.Trim();
            var requested = BuildAchievementsUrl(resolved, appId, language);
            res.RequestedUrl = requested;

            var page = await _steamClient.GetAchievementsPageAsync(resolved, appId, language, cancel).ConfigureAwait(false);
            var html = page?.Html ?? string.Empty;

            res.StatusCode = page != null ? (int)page.StatusCode : 0;
            res.FinalUrl = page?.FinalUrl ?? requested;

            if (res.StatusCode == 429)
            {
                res.TransientFailure = true;
                res.SetDetail(SteamScrapeDetail.TooManyRequests);
                return res;
            }

            // Some Steam redirects (for example appId->stats key redirects) can drop or change
            // the language query parameter. When that happens, unlock time strings can come back
            // in a different locale than parser expectations. Retry once with explicit language.
            if (ShouldRetryForRedirectLanguageLoss(res.FinalUrl, language))
            {
                var redirectedStatsKey = ExtractStatsKey(res.FinalUrl);
                if (string.IsNullOrWhiteSpace(redirectedStatsKey))
                {
                    redirectedStatsKey = appId.ToString();
                }

                var languageRetryUrl = BuildAchievementsUrl(resolved, redirectedStatsKey, language);
                _logger?.Debug(
                    $"[SteamAch] Redirect dropped/changed language (requested={language}, finalUrl={res.FinalUrl}). " +
                    $"Retrying once with explicit language. Url={languageRetryUrl}");

                SteamPageResult languageRetryPage;
                if (string.Equals(redirectedStatsKey, appId.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    languageRetryPage = await _steamClient.GetAchievementsPageAsync(resolved, appId, language, cancel).ConfigureAwait(false);
                }
                else
                {
                    languageRetryPage = await _steamClient.GetAchievementsPageByKeyAsync(resolved, redirectedStatsKey, language, cancel).ConfigureAwait(false);
                }

                if (languageRetryPage != null)
                {
                    res.RequestedUrl = languageRetryUrl;
                    res.StatusCode = (int)languageRetryPage.StatusCode;
                    res.FinalUrl = languageRetryPage.FinalUrl ?? languageRetryUrl;
                    html = languageRetryPage.Html ?? string.Empty;

                    if (res.StatusCode == 429)
                    {
                        res.TransientFailure = true;
                        res.SetDetail(SteamScrapeDetail.TooManyRequests);
                        return res;
                    }
                }
            }

            if (LooksLoggedOutHeuristic(html, res.FinalUrl))
            {
                if (TryGetPrivateOrUnavailable(html, out var reason, res.FinalUrl))
                {
                    res.TransientFailure = false;
                    res.StatsUnavailable = true;
                    res.SetDetail(reason == SteamScrapeDetail.None ? SteamScrapeDetail.Unavailable : reason);
                    return res;
                }

                res.TransientFailure = true;
                res.SetDetail(SteamScrapeDetail.CookiesBadAfterRefresh);
                return res;
            }

            if (page?.WasRedirected == true && !IsStatsForApp(res.FinalUrl))
            {
                res.StatsUnavailable = true;
                res.SetDetail(SteamScrapeDetail.RedirectOffStats);
                await MaybeClassifyNoAchievementsBySchemaAsync(res, appId, cancel).ConfigureAwait(false);
                return res;
            }

            var result = ClassifyScrapeOrPrivate(res, html, includeLocked, language, gameName);

            if (result.TransientFailure && result.DetailCode == SteamScrapeDetail.NoRowsUnknown)
            {
                var canonicalStatsKey = ExtractStatsKey(result.FinalUrl);
                if (!string.IsNullOrWhiteSpace(canonicalStatsKey) &&
                    !string.Equals(canonicalStatsKey, appId.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var fallbackRequested = BuildAchievementsUrl(resolved, canonicalStatsKey, language);
                    _logger?.Debug($"[SteamAch] NoRowsUnknown for appId={appId}; retrying once with canonical stats key '{canonicalStatsKey}'. Url={fallbackRequested}");

                    var fallbackPage = await _steamClient.GetAchievementsPageByKeyAsync(resolved, canonicalStatsKey, language, cancel).ConfigureAwait(false);
                    var fallbackHtml = fallbackPage?.Html ?? string.Empty;
                    var fallbackResponse = new AchievementsScrapeResponse
                    {
                        RequestedUrl = fallbackRequested,
                        FinalUrl = fallbackPage?.FinalUrl ?? fallbackRequested,
                        StatusCode = fallbackPage != null ? (int)fallbackPage.StatusCode : 0
                    };

                    if (fallbackResponse.StatusCode == 429)
                    {
                        fallbackResponse.TransientFailure = true;
                        fallbackResponse.SetDetail(SteamScrapeDetail.TooManyRequests);
                        return fallbackResponse;
                    }

                    if (!LooksLoggedOutHeuristic(fallbackHtml, fallbackResponse.FinalUrl))
                    {
                        var fallbackClassified = ClassifyScrapeOrPrivate(fallbackResponse, fallbackHtml, includeLocked, language, gameName);
                        if (fallbackClassified.SuccessWithRows)
                        {
                            await MaybeClassifyNoAchievementsBySchemaAsync(fallbackClassified, appId, cancel).ConfigureAwait(false);
                            return fallbackClassified;
                        }
                    }
                }
            }

            await MaybeClassifyNoAchievementsBySchemaAsync(result, appId, cancel).ConfigureAwait(false);
            return result;
        }

        private static string BuildAchievementsUrl(string steamId64, int appId, string language) =>
            $"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/?tab=achievements&l={language ?? "english"}";

        private static string BuildAchievementsUrl(string steamId64, string statsKey, string language) =>
            $"https://steamcommunity.com/profiles/{steamId64}/stats/{statsKey}/?tab=achievements&l={language ?? "english"}";

        private static string ExtractStatsKey(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var parts = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "stats", StringComparison.OrdinalIgnoreCase))
                {
                    return parts[i + 1];
                }
            }

            return null;
        }

        private static bool ShouldRetryForRedirectLanguageLoss(string finalUrl, string requestedLanguage)
        {
            if (string.IsNullOrWhiteSpace(finalUrl) || string.IsNullOrWhiteSpace(requestedLanguage))
            {
                return false;
            }

            // For english responses, losing l=english is usually harmless.
            if (string.Equals(requestedLanguage.Trim(), "english", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.AbsolutePath.IndexOf("/stats/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var expected = requestedLanguage.Trim();
            if (!TryGetQueryValue(finalUrl, "l", out var actualLanguage))
            {
                return true;
            }

            return !string.Equals(actualLanguage, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetQueryValue(string url, string key, out string value)
        {
            value = null;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var query = uri.Query;
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            var parts = query.TrimStart('?')
                .Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var kv = part.Split(new[] { '=' }, 2);
                var currentKey = Uri.UnescapeDataString(kv[0] ?? string.Empty);
                if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var currentValue = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                value = currentValue;
                return true;
            }

            return false;
        }

        private async Task MaybeClassifyNoAchievementsBySchemaAsync(AchievementsScrapeResponse res, int appId, CancellationToken ct)
        {
            if (res == null || appId <= 0) return;
            if (!res.StatsUnavailable) return;

            var apiKey = _providerSettings.SteamApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            bool? has;
            try
            {
                has = await _steamClient.GetAppHasAchievementsAsync(apiKey, appId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FAF] Failed to check if app {appId} has achievements via API.");
                return;
            }

            if (has == false)
                res.SetDetail(SteamScrapeDetail.NoAchievements);
        }

        private AchievementsScrapeResponse ClassifyScrapeOrPrivate(
            AchievementsScrapeResponse res,
            string html,
            bool includeLocked,
            string language = "english",
            string gameName = null)
        {
            res.Rows = new List<ScrapedAchievement>();
            res.TransientFailure = true;
            res.StatsUnavailable = false;
            res.SetDetail(SteamScrapeDetail.NoRowsUnknown);

            var parsed = _steamClient.ParseAchievements(html, includeLocked, language, gameName) ?? new List<ScrapedAchievement>();

            if (parsed.Count > 0)
            {
                res.Rows = parsed;
                res.TransientFailure = false;
                res.SetDetail(SteamScrapeDetail.Scraped);
                return res;
            }

            if (TryGetPrivateOrUnavailable(html, out var why, res.FinalUrl))
            {
                res.TransientFailure = false;
                res.StatsUnavailable = true;
                res.SetDetail(why == SteamScrapeDetail.None ? SteamScrapeDetail.Unavailable : why);
                return res;
            }

            if (SteamHttpClient.HasAnyAchievementRows(html))
            {
                if (SteamHttpClient.HasOnlyHiddenAchievementRows(html))
                {
                    res.TransientFailure = false;
                    res.SetDetail(SteamScrapeDetail.AllHidden);
                    return res;
                }

                var hasUnlockedMarkers = html.IndexOf("achieveUnlockTime", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!hasUnlockedMarkers && !includeLocked)
                {
                    res.TransientFailure = false;
                    res.SetDetail(SteamScrapeDetail.AllHidden);
                    return res;
                }

                res.SetDetail(hasUnlockedMarkers
                    ? SteamScrapeDetail.UnlockedMarkerButParseFailed
                    : SteamScrapeDetail.RowsMarkerButParseFailed);
                return res;
            }

            return res;
        }

        private static bool IsStatsForApp(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            var p = u.AbsolutePath.TrimEnd('/');
            return p.IndexOf("/stats/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (p.IndexOf("/profiles/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.IndexOf("/id/", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsLoginLikeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLoggedOutHeuristic(string html, string finalUrl)
        {
            if (IsLoginLikeUrl(finalUrl)) return true;

            if (string.IsNullOrEmpty(html)) return false;

            if (SteamHttpClient.LooksUnauthenticatedStatsPayload(html, finalUrl)) return true;

            if (SteamHttpClient.LooksLoggedOutHeader(html)) return true;

            var hasLoggedOutSteamIdMarker =
                html.IndexOf("g_steamID = \"0\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                html.IndexOf("g_steamID = false", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasLoggedOutSteamIdMarker && !SteamHttpClient.HasAnyAchievementRows(html))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetPrivateOrUnavailable(string html, out SteamScrapeDetail detail, string finalUrl = null)
        {
            detail = SteamScrapeDetail.None;
            if (string.IsNullOrEmpty(html)) return false;

            if (SteamHttpClient.LooksUnauthenticatedStatsPayload(html, finalUrl))
            {
                detail = SteamScrapeDetail.RequiresLoginForStats;
                return true;
            }

            if (SteamHttpClient.LooksPrivateOrRestrictedStatsPayload(html, finalUrl))
            {
                detail = SteamScrapeDetail.ProfilePrivate;
                return true;
            }

            if (SteamHttpClient.LooksProfileNotFoundStatsPayload(html, finalUrl))
            {
                detail = SteamScrapeDetail.ProfileNotFound;
                return true;
            }

            if (SteamHttpClient.LooksStructurallyUnavailableStatsPayload(html, finalUrl))
            {
                detail = SteamScrapeDetail.Unavailable;
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------------
        // Persona / helpers
        // ---------------------------------------------------------------------

        private string ResolveSteamId64(string steamIdMaybe)
        {
            if (!string.IsNullOrWhiteSpace(steamIdMaybe) && ulong.TryParse(steamIdMaybe.Trim(), out _))
            {
                return steamIdMaybe.Trim();
            }

            return ProviderRegistry.Settings<SteamSettings>().SteamUserId?.Trim();
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

        private static RarityTier GetFallbackRarity(bool hidden, int? progressNum, int? progressDenom)
        {
            if ((progressNum.HasValue || progressDenom.HasValue) &&
                progressDenom.HasValue &&
                progressDenom.Value > 0)
            {
                return RarityTier.Uncommon;
            }

            if (hidden)
            {
                return RarityTier.Rare;
            }

            return RarityTier.Common;
        }

        private async Task<SchemaAndPercentages> TryGetAnonymousSteamSchemaAsync(int appId, CancellationToken cancel)
        {
            if (appId <= 0)
            {
                return null;
            }

            cancel.ThrowIfCancellationRequested();

            try
            {
                using (var httpClient = CreateAnonymousSteamHttpClient())
                {
                    var bootstrapSchema = await TryGetSteamHuntersBootstrapSchemaAsync(appId, cancel).ConfigureAwait(false);
                    if (bootstrapSchema?.Achievements?.Count > 0)
                    {
                        _logger?.Info($"[SteamAch] Using anonymous SteamHunters bootstrap schema for appId={appId} count={bootstrapSchema.Achievements.Count}");
                        return bootstrapSchema;
                    }

                    var apiSchema = await TryGetSteamHuntersApiSchemaAsync(httpClient, appId, cancel).ConfigureAwait(false);
                    if (apiSchema?.Achievements?.Count > 0)
                    {
                        _logger?.Info($"[SteamAch] Using anonymous SteamHunters API schema for appId={appId} count={apiSchema.Achievements.Count}");
                        return apiSchema;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[SteamAch] Anonymous Steam schema fallback failed for appId={appId}.");
            }

            return null;
        }

        private async Task<SchemaAndPercentages> TryGetSteamHuntersApiSchemaAsync(HttpClient httpClient, int appId, CancellationToken cancel)
        {
            if (httpClient == null || appId <= 0)
            {
                return null;
            }

            cancel.ThrowIfCancellationRequested();
            var response = await httpClient.GetAsync($"https://steamhunters.com/api/apps/{appId}/achievements", cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            var items = JArray.Parse(payload);
            if (items.Count == 0)
            {
                return null;
            }

            var percentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var achievements = items
                .OfType<JObject>()
                .Select(item => CreateSteamHuntersAchievement(appId, item, percentages))
                .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.Name))
                .GroupBy(achievement => achievement.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            return achievements.Count == 0
                ? null
                : new SchemaAndPercentages
                {
                    Achievements = achievements,
                    GlobalPercentages = percentages
                };
        }

        private async Task<SchemaAndPercentages> TryGetSteamHuntersBootstrapSchemaAsync(int appId, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var html = await TryGetUrlContentViaCurlAsync($"https://steamhunters.com/apps/{appId}/achievements?group=&sort=name", cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var modelJson = ExtractSteamHuntersModelJson(html);
            if (string.IsNullOrWhiteSpace(modelJson))
            {
                return null;
            }

            var model = JObject.Parse(modelJson);
            var items = model.SelectToken("listData.pagedList.items") as JArray;
            if (items == null || items.Count == 0)
            {
                return null;
            }

            var percentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var achievements = items
                .OfType<JObject>()
                .Select(item => CreateSteamHuntersAchievement(appId, item, percentages))
                .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.Name))
                .GroupBy(achievement => achievement.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            return achievements.Count == 0
                ? null
                : new SchemaAndPercentages
                {
                    Achievements = achievements,
                    GlobalPercentages = percentages
                };
        }

        private SchemaAchievement CreateSteamHuntersAchievement(int appId, JObject item, IDictionary<string, double> percentages)
        {
            var apiName = item?["apiName"]?.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return null;
            }

            var steamPercentage = item["steamPercentage"]?.Value<double?>() ?? item["estimatedSteamPercentage"]?.Value<double?>();
            if (steamPercentage.HasValue && !double.IsNaN(steamPercentage.Value) && !double.IsInfinity(steamPercentage.Value))
            {
                percentages[apiName] = steamPercentage.Value;
            }

            return new SchemaAchievement
            {
                Name = apiName,
                DisplayName = WebUtility.HtmlDecode(item["name"]?.Value<string>()?.Trim() ?? apiName),
                Description = WebUtility.HtmlDecode(item["description"]?.Value<string>()?.Trim() ?? string.Empty),
                Icon = ResolveSteamHuntersIcon(appId, item["icon"]?.Value<string>()),
                IconGray = ResolveSteamHuntersIcon(appId, item["iconGray"]?.Value<string>()),
                Hidden = item["hidden"]?.Value<bool?>() == true ? 1 : 0,
                GlobalPercent = steamPercentage
            };
        }

        private static string ResolveSteamHuntersIcon(int appId, string iconHashOrUrl)
        {
            iconHashOrUrl = iconHashOrUrl?.Trim();
            if (string.IsNullOrWhiteSpace(iconHashOrUrl))
            {
                return null;
            }

            Uri absoluteIconUri;
            if (Uri.TryCreate(iconHashOrUrl, UriKind.Absolute, out absoluteIconUri))
            {
                return absoluteIconUri.ToString();
            }

            return $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{iconHashOrUrl}";
        }

        private static HttpClient CreateAnonymousSteamHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
            client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.9));
            return client;
        }

        private async Task<string> TryGetUrlContentViaCurlAsync(string url, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string tempFilePath = null;
            try
            {
                cancel.ThrowIfCancellationRequested();
                tempFilePath = Path.Combine(Path.GetTempPath(), $"playniteachievements_steam_{Guid.NewGuid():N}.html");
                var startInfo = new ProcessStartInfo
                {
                    FileName = "curl.exe",
                    Arguments = $"-L --compressed --silent --show-error -A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36\" -H \"Accept-Language: en-US,en;q=0.9\" -o {QuoteCommandLineArgument(tempFilePath)} {QuoteCommandLineArgument(url)}",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    if (!process.Start())
                    {
                        return null;
                    }

                    var errorTask = process.StandardError.ReadToEndAsync();
                    var exited = await Task.Run(() => process.WaitForExit(20000), cancel).ConfigureAwait(false);
                    if (!exited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        return null;
                    }

                    var error = await errorTask.ConfigureAwait(false);
                    if (process.ExitCode != 0)
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            _logger?.Warn($"[SteamAch] SteamHunters curl fallback failed for {url}: {error.Trim()}");
                        }

                        return null;
                    }
                }

                if (string.IsNullOrWhiteSpace(tempFilePath) || !File.Exists(tempFilePath))
                {
                    return null;
                }

                var content = File.ReadAllText(tempFilePath);
                return LooksLikeCloudflareChallenge(content) ? null : content;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[SteamAch] SteamHunters curl fallback failed for {url}.");
                return null;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempFilePath))
                {
                    try
                    {
                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool LooksLikeCloudflareChallenge(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            return content.IndexOf("Just a moment...", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   content.IndexOf("cf-browser-verification", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   content.IndexOf("challenge-platform", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractSteamHuntersModelJson(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var varIndex = html.IndexOf("var sh", StringComparison.OrdinalIgnoreCase);
            if (varIndex < 0)
            {
                return null;
            }

            var modelIndex = html.IndexOf("model:", varIndex, StringComparison.OrdinalIgnoreCase);
            if (modelIndex < 0)
            {
                return null;
            }

            var jsonStart = html.IndexOf('{', modelIndex);
            if (jsonStart < 0)
            {
                return null;
            }

            return ExtractBalancedJsonObject(html, jsonStart);
        }

        private static string ExtractBalancedJsonObject(string text, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(text) || startIndex < 0 || startIndex >= text.Length || text[startIndex] != '{')
            {
                return null;
            }

            var depth = 0;
            var inString = false;
            var isEscaped = false;

            for (var index = startIndex; index < text.Length; index++)
            {
                var current = text[index];

                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        isEscaped = true;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current != '}')
                {
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    return text.Substring(startIndex, index - startIndex + 1);
                }
            }

            return null;
        }

        private static string QuoteCommandLineArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
