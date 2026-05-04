using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamScanner
    {
        private static readonly Regex GenericAchievementNamePattern = new Regex(
            @"^(ach(ieve(ment)?)?|stat|unlock|trophy|badge)[_\-\s]?\d+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        private readonly SteamHttpClient _steamClient;
        private readonly SteamApiClient _steamApiClient;
        private readonly SteamWebApiTokenResolver _tokenResolver;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        public SteamScanner(
            PlayniteAchievementsSettings settings,
            SteamHttpClient steamClient,
            SteamApiClient steamApiClient,
            SteamWebApiTokenResolver tokenResolver,
            IPlayniteAPI api,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _steamClient = steamClient ?? throw new ArgumentNullException(nameof(steamClient));
            _steamApiClient = steamApiClient ?? throw new ArgumentNullException(nameof(steamApiClient));
            _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
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
                _logger?.Info("[SteamAch] Probing Steam login status before scan...");
                var tokenResolution = await _tokenResolver.ResolveAsync(cancel).ConfigureAwait(false);
                var steamUserId = tokenResolution.UserId?.Trim();
                if (!tokenResolution.IsSuccess || string.IsNullOrWhiteSpace(steamUserId))
                {
                    _logger?.Warn("[SteamAch] Steam authentication check failed. Aborting scan.");
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
                            () => FetchGameDataAsync(game, steamUserId, tokenResolution.Token, token),
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

        internal async Task<IReadOnlyList<Game>> FilterOwnedGamesAsync(
            IReadOnlyList<Game> gamesToRefresh,
            CancellationToken cancel)
        {
            HashSet<int> ownedAppIds;
            try
            {
                ownedAppIds = await _steamClient.GetOwnedAppIdsFromSessionAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[SteamAch] Failed to resolve owned Steam app IDs from the authenticated session. Continuing without ownership filtering.");
                return gamesToRefresh ?? Array.Empty<Game>();
            }

            if (ownedAppIds.Count == 0)
            {
                _logger?.Info("[SteamAch] Authenticated Steam session reported zero owned app IDs. Continuing without ownership filtering.");
                return gamesToRefresh ?? Array.Empty<Game>();
            }

            _logger?.Info($"[SteamAch] Steam web session returned {ownedAppIds.Count} owned app IDs. Skipping strict ownership filtering because session ownership may not include family-shared titles.");
            return gamesToRefresh ?? Array.Empty<Game>();
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
            string accessToken,
            CancellationToken cancel)
        {
            if (!TryGetPlatformAppId(game, out var appId))
            {
                _logger?.Warn($"Could not get AppId from game {game.Name}");
                return null;
            }

            var schema = await FetchSchemaAsync(accessToken, appId, cancel).ConfigureAwait(false);
            var unlocked = await FetchUnlockedAsync(appId, game?.Name, steamUserId, accessToken, schema, cancel).ConfigureAwait(false);

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

        private Task<SchemaAndPercentages> FetchSchemaAsync(string accessToken, int appId, CancellationToken cancel)
        {
            var language = SteamApiClient.NormalizeSteamLanguage(_settings.Persisted.GlobalLanguage);
            return FetchSchemaWithLocalizedHiddenTextAsync(
                accessToken,
                appId,
                language,
                cancel);
        }

        private async Task<SchemaAndPercentages> FetchSchemaWithLocalizedHiddenTextAsync(string accessToken, int appId, string language, CancellationToken cancel)
        {
            var schema = await _steamApiClient.GetSchemaForGameDetailedAsync(
                accessToken,
                appId,
                language,
                cancel).ConfigureAwait(false);

            if (schema?.Achievements == null || schema.Achievements.Count == 0 ||
                string.IsNullOrWhiteSpace(language) ||
                string.Equals(language.Trim(), "english", StringComparison.OrdinalIgnoreCase))
            {
                return schema;
            }

            try
            {
                using (var httpClient = CreateSteamCommunityHttpClient())
                {
                    var localizedCommunitySchema = await TryGetSteamCommunityStatsSchemaAsync(httpClient, appId, language, cancel).ConfigureAwait(false);
                    var englishCommunitySchema = await TryGetSteamCommunityStatsSchemaAsync(httpClient, appId, "english", cancel).ConfigureAwait(false);
                    var localizedBridge = BuildCommunityLocalizedBridge(
                        englishCommunitySchema?.Achievements,
                        localizedCommunitySchema?.Achievements);
                    ApplyLocalizedCommunityTextToSchema(schema.Achievements, localizedBridge);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed applying localized hidden achievement text bridge for appId={appId}.");
            }

            return schema;
        }

        private async Task<UserUnlockedAchievements> FetchUnlockedAsync(
            int appId,
            string gameName,
            string steamUserId,
            string accessToken,
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
                scraped = await ScrapeAchievementsAsync(steamUserId, appId, accessToken, cancel, includeLocked: true, gameName: gameName)
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
            string accessToken,
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

            var language = SteamApiClient.NormalizeSteamLanguage(_settings.Persisted.GlobalLanguage);
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
                await MaybeClassifyNoAchievementsBySchemaAsync(res, accessToken, appId, cancel).ConfigureAwait(false);
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
                            await MaybeClassifyNoAchievementsBySchemaAsync(fallbackClassified, accessToken, appId, cancel).ConfigureAwait(false);
                            return fallbackClassified;
                        }
                    }
                }
            }

            await MaybeClassifyNoAchievementsBySchemaAsync(result, accessToken, appId, cancel).ConfigureAwait(false);
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

        private static HttpClient CreateSteamCommunityHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            return client;
        }

        private async Task<SchemaAndPercentages> TryGetSteamCommunityStatsSchemaAsync(HttpClient httpClient, int appId, string language, CancellationToken cancel)
        {
            if (httpClient == null || appId <= 0)
            {
                return null;
            }

            try
            {
                var resolvedLanguage = SteamApiClient.NormalizeSteamLanguage(language);
                var url = $"https://steamcommunity.com/stats/{appId}/achievements?l={resolvedLanguage}";
                using (var response = await httpClient.GetAsync(url, cancel).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        return null;
                    }

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var rows = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]") ??
                               doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveTxtHolder')]");
                    if (rows == null || rows.Count == 0)
                    {
                        return null;
                    }

                    var achievements = new List<SchemaAchievement>();
                    var titleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    foreach (var row in rows)
                    {
                        var title = WebUtility.HtmlDecode(row.SelectSingleNode(".//h3")?.InnerText ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            continue;
                        }

                        titleCounts[title] = titleCounts.TryGetValue(title, out var currentCount)
                            ? currentCount + 1
                            : 1;

                        var description = WebUtility.HtmlDecode(row.SelectSingleNode(".//h5")?.InnerText ?? string.Empty).Trim();
                        var hidden = row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null || string.IsNullOrWhiteSpace(description);
                        var iconUrl = row.SelectSingleNode(".//img")?.GetAttributeValue("src", string.Empty)?.Trim();

                        achievements.Add(new SchemaAchievement
                        {
                            Name = title,
                            DisplayName = title,
                            Description = description,
                            Icon = string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl,
                            IconGray = string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl,
                            Hidden = hidden ? 1 : 0
                        });
                    }

                    achievements = achievements
                        .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.DisplayName))
                        .Where(achievement => titleCounts.TryGetValue(achievement.DisplayName, out var count) && count == 1)
                        .ToList();

                    if (achievements.Count == 0)
                    {
                        return null;
                    }

                    return new SchemaAndPercentages
                    {
                        Achievements = achievements,
                        GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, SchemaAchievement> BuildSchemaLookupByText(IReadOnlyList<SchemaAchievement> achievements)
        {
            var result = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || achievements.Count == 0)
            {
                return result;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var lookupKey = BuildAchievementLookupKey(achievement.DisplayName, achievement.Description);
                if (!string.IsNullOrWhiteSpace(lookupKey) && !result.ContainsKey(lookupKey))
                {
                    result[lookupKey] = achievement;
                }
            }

            return result;
        }

        private static Dictionary<string, SchemaAchievement> BuildSchemaLookupByTitle(IReadOnlyList<SchemaAchievement> achievements)
        {
            var titleGroups = new Dictionary<string, List<SchemaAchievement>>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || achievements.Count == 0)
            {
                return new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var lookupKey = BuildAchievementTitleLookupKey(achievement.DisplayName);
                if (string.IsNullOrWhiteSpace(lookupKey))
                {
                    continue;
                }

                if (!titleGroups.TryGetValue(lookupKey, out var bucket))
                {
                    bucket = new List<SchemaAchievement>();
                    titleGroups[lookupKey] = bucket;
                }

                bucket.Add(achievement);
            }

            return titleGroups
                .Where(group => group.Value.Count == 1)
                .ToDictionary(group => group.Key, group => group.Value[0], StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, SchemaAchievement> BuildSchemaLookupByIcon(IReadOnlyList<SchemaAchievement> achievements, bool useLockedIcon)
        {
            var lookup = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || achievements.Count == 0)
            {
                return lookup;
            }

            foreach (var achievement in achievements)
            {
                var iconValue = useLockedIcon ? achievement?.IconGray : achievement?.Icon;
                var iconHash = ExtractAchievementIconHash(iconValue);
                if (string.IsNullOrWhiteSpace(iconHash))
                {
                    continue;
                }

                if (!lookup.TryGetValue(iconHash, out var existing))
                {
                    lookup[iconHash] = achievement;
                    continue;
                }

                if (existing != null && !ReferenceEquals(existing, achievement))
                {
                    lookup[iconHash] = null;
                }
            }

            return lookup;
        }

        private static string ExtractAchievementIconHash(string iconValue)
        {
            iconValue = iconValue?.Trim();
            if (string.IsNullOrWhiteSpace(iconValue))
            {
                return null;
            }

            if (Uri.TryCreate(iconValue, UriKind.Absolute, out var absoluteUri))
            {
                iconValue = absoluteUri.AbsolutePath;
            }

            var querySeparator = iconValue.IndexOf('?');
            if (querySeparator >= 0)
            {
                iconValue = iconValue.Substring(0, querySeparator);
            }

            var fragmentSeparator = iconValue.IndexOf('#');
            if (fragmentSeparator >= 0)
            {
                iconValue = iconValue.Substring(0, fragmentSeparator);
            }

            var lastSlash = iconValue.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < iconValue.Length - 1)
            {
                iconValue = iconValue.Substring(lastSlash + 1);
            }

            iconValue = iconValue.Trim();
            return string.IsNullOrWhiteSpace(iconValue)
                ? null
                : iconValue.ToLowerInvariant();
        }

        private static bool ShouldPreferSchemaDisplayName(string existingDisplayName, string incomingDisplayName, string apiName, bool preferSourceText)
        {
            incomingDisplayName = incomingDisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(incomingDisplayName) || IsLowQualityDisplayName(incomingDisplayName, apiName))
            {
                return false;
            }

            if (preferSourceText)
            {
                return !string.Equals(existingDisplayName?.Trim(), incomingDisplayName, StringComparison.OrdinalIgnoreCase);
            }

            return IsLowQualityDisplayName(existingDisplayName, apiName);
        }

        private static bool ShouldPreferSchemaDescription(string existingDescription, string incomingDescription, bool preferSourceText)
        {
            incomingDescription = incomingDescription?.Trim();
            if (string.IsNullOrWhiteSpace(incomingDescription) || IsLowQualityDescription(incomingDescription))
            {
                return false;
            }

            if (preferSourceText)
            {
                return !string.Equals(existingDescription?.Trim(), incomingDescription, StringComparison.OrdinalIgnoreCase);
            }

            return IsLowQualityDescription(existingDescription);
        }

        private static string BuildAchievementLookupKey(string displayName, string description)
        {
            var normalizedName = NormalizeAchievementLookupPart(displayName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            return normalizedName + "|" + NormalizeAchievementLookupPart(description);
        }

        private static string BuildAchievementTitleLookupKey(string displayName)
        {
            var normalizedName = NormalizeAchievementLookupPart(displayName);
            return string.IsNullOrWhiteSpace(normalizedName)
                ? null
                : "title:" + normalizedName;
        }

        private static string NormalizeAchievementLookupPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = WebUtility.HtmlDecode(value).Trim().ToLowerInvariant();
            value = Regex.Replace(value, @"\s+", " ");
            return value;
        }

        private static bool IsLowQualityDisplayName(string displayName, string apiName)
        {
            displayName = displayName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return true;
            }

            apiName = apiName?.Trim();
            if (!string.IsNullOrWhiteSpace(apiName) && string.Equals(displayName, apiName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GenericAchievementNamePattern.IsMatch(displayName);
        }

        private static bool IsLowQualityDescription(string description)
        {
            description = description?.Trim();
            return string.IsNullOrWhiteSpace(description);
        }

        private static IReadOnlyList<SchemaAchievement> BuildCommunityLocalizedBridge(
            IReadOnlyList<SchemaAchievement> englishAchievements,
            IReadOnlyList<SchemaAchievement> localizedAchievements)
        {
            if (englishAchievements == null || localizedAchievements == null ||
                englishAchievements.Count == 0 || localizedAchievements.Count == 0)
            {
                return Array.Empty<SchemaAchievement>();
            }

            var localizedByIcon = BuildSchemaLookupByIcon(localizedAchievements, useLockedIcon: false);
            var localizedByLockedIcon = BuildSchemaLookupByIcon(localizedAchievements, useLockedIcon: true);
            var bridge = new List<SchemaAchievement>();

            foreach (var englishAchievement in englishAchievements)
            {
                if (englishAchievement == null || string.IsNullOrWhiteSpace(englishAchievement.DisplayName))
                {
                    continue;
                }

                SchemaAchievement localizedAchievement = null;

                var iconHash = ExtractAchievementIconHash(englishAchievement.Icon);
                if (!string.IsNullOrWhiteSpace(iconHash) &&
                    localizedByIcon.TryGetValue(iconHash, out var byIcon) &&
                    byIcon != null)
                {
                    localizedAchievement = byIcon;
                }

                if (localizedAchievement == null)
                {
                    var lockedIconHash = ExtractAchievementIconHash(englishAchievement.IconGray);
                    if (!string.IsNullOrWhiteSpace(lockedIconHash) &&
                        localizedByLockedIcon.TryGetValue(lockedIconHash, out var byLockedIcon) &&
                        byLockedIcon != null)
                    {
                        localizedAchievement = byLockedIcon;
                    }
                }

                if (localizedAchievement == null)
                {
                    continue;
                }

                bridge.Add(new SchemaAchievement
                {
                    Name = englishAchievement.Name,
                    DisplayName = englishAchievement.DisplayName,
                    Description = englishAchievement.Description,
                    LocalizedDisplayName = localizedAchievement.DisplayName,
                    LocalizedDescription = localizedAchievement.Description,
                    Icon = localizedAchievement.Icon,
                    IconGray = localizedAchievement.IconGray,
                    Hidden = localizedAchievement.Hidden,
                    GlobalPercent = localizedAchievement.GlobalPercent
                });
            }

            return bridge;
        }

        private static void ApplyLocalizedCommunityTextToSchema(
            IReadOnlyList<SchemaAchievement> targetAchievements,
            IReadOnlyList<SchemaAchievement> localizedBridge)
        {
            if (targetAchievements == null || localizedBridge == null ||
                targetAchievements.Count == 0 || localizedBridge.Count == 0)
            {
                return;
            }

            var bridgeByText = BuildSchemaLookupByText(localizedBridge);
            var bridgeByTitle = BuildSchemaLookupByTitle(localizedBridge);

            foreach (var target in targetAchievements)
            {
                if (target == null)
                {
                    continue;
                }

                SchemaAchievement bridgeMatch = null;
                var textLookupKey = BuildAchievementLookupKey(target.DisplayName, target.Description);
                if (!string.IsNullOrWhiteSpace(textLookupKey) &&
                    bridgeByText.TryGetValue(textLookupKey, out var byText))
                {
                    bridgeMatch = byText;
                }

                if (bridgeMatch == null)
                {
                    var titleLookupKey = BuildAchievementTitleLookupKey(target.DisplayName);
                    if (!string.IsNullOrWhiteSpace(titleLookupKey) &&
                        bridgeByTitle.TryGetValue(titleLookupKey, out var byTitle))
                    {
                        bridgeMatch = byTitle;
                    }
                }

                if (bridgeMatch == null)
                {
                    continue;
                }

                if (ShouldPreferSchemaDisplayName(target.DisplayName, bridgeMatch.LocalizedDisplayName, target.Name, preferSourceText: true))
                {
                    target.DisplayName = bridgeMatch.LocalizedDisplayName;
                }

                if (ShouldPreferSchemaDescription(target.Description, bridgeMatch.LocalizedDescription, preferSourceText: true))
                {
                    target.Description = bridgeMatch.LocalizedDescription;
                }
            }
        }

        private async Task MaybeClassifyNoAchievementsBySchemaAsync(AchievementsScrapeResponse res, string accessToken, int appId, CancellationToken ct)
        {
            if (res == null || appId <= 0) return;
            if (!res.StatsUnavailable) return;
            if (string.IsNullOrWhiteSpace(accessToken)) return;

            bool? has;
            try
            {
                has = await _steamApiClient.GetGameHasAchievementsAsync(accessToken, appId, "english", ct).ConfigureAwait(false);
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
    }
}
