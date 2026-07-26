using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
        private readonly SteamHttpClient _steamClient;
        private readonly SteamApiClient _steamApiClient;
        private readonly SteamWebApiTokenResolver _tokenResolver;
        private readonly SteamHuntersCategoryEnricher _steamHuntersCategoryEnricher;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        public SteamScanner(
            PlayniteAchievementsSettings settings,
            SteamHttpClient steamClient,
            SteamApiClient steamApiClient,
            SteamWebApiTokenResolver tokenResolver,
            SteamHuntersCategoryEnricher steamHuntersCategoryEnricher,
            IPlayniteAPI api,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _steamClient = steamClient ?? throw new ArgumentNullException(nameof(steamClient));
            _steamApiClient = steamApiClient ?? throw new ArgumentNullException(nameof(steamApiClient));
            _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
            _steamHuntersCategoryEnricher = steamHuntersCategoryEnricher;
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
            _steamHuntersCategoryEnricher?.ClearCache();

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
                        // Log the exception object so the stack trace identifies where a silent
                        // timeout (e.g. HttpClient's TaskCanceledException) actually originated.
                        _logger?.Warn(ex, $"[SteamAch] Skipping game after retries: {game?.Name} (appId={appIdText}). Consecutive errors={consecutiveErrors}. {ex.GetType().Name}: {ex.Message}");
                    },
                    rateLimiter,
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
            return TransientErrorClassifier.IsTransient(ex, e =>
                e is SteamTransientException ? true : (bool?)null);
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
            if (game == null)
            {
                return false;
            }

            if (GameCustomDataLookup.TryGetSteamAppIdOverride(game.Id, out appId))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(game.GameId) &&
                   int.TryParse(game.GameId, out appId) &&
                   appId > 0;
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
            var hasAchievements = schema?.Achievements?.Count > 0;
            if (!hasAchievements && schema == null)
            {
                var language = string.IsNullOrWhiteSpace(_settings.Persisted.GlobalLanguage)
                    ? "english"
                    : _settings.Persisted.GlobalLanguage.Trim();
                var apiHasAchievements = await _steamApiClient
                    .GetGameHasAchievementsAsync(accessToken, appId, language, cancel)
                    .ConfigureAwait(false);
                if (apiHasAchievements == false)
                {
                    _logger?.Debug($"[SteamAch] Skipping stats scrape for appId={appId}; Steam API reports no achievements.");
                    return new GameAchievementData
                    {
                        AppId = appId,
                        GameName = game.Name,
                        ProviderKey = "Steam",
                        LibrarySourceName = game?.Source?.Name,
                        LastUpdatedUtc = DateTime.UtcNow,
                        HasAchievements = false,
                        PlayniteGameId = game.Id,
                        Achievements = new List<AchievementDetail>()
                    };
                }
            }

            var unlocked = await FetchUnlockedAsync(appId, game?.Name, steamUserId, accessToken, schema, cancel).ConfigureAwait(false);

            var gameData = new GameAchievementData
            {
                AppId = appId,
                GameName = game.Name,
                ProviderKey = "Steam",
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = hasAchievements,
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

                await EnrichSteamHuntersCategoriesAsync(appId, gameData.GameName, gameData.Achievements, gameData.PlayniteGameId, cancel).ConfigureAwait(false);
            }

            return gameData;
        }

        private Task EnrichSteamHuntersCategoriesAsync(
            int appId,
            string gameName,
            IList<AchievementDetail> achievements,
            Guid? playniteGameId,
            CancellationToken cancel)
        {
            if (_steamHuntersCategoryEnricher == null ||
                !ShouldUseSteamHuntersForCategories())
            {
                return Task.CompletedTask;
            }

            return _steamHuntersCategoryEnricher.EnrichAsync(appId, gameName, achievements, playniteGameId, cancel);
        }

        private static bool ShouldUseSteamHuntersForCategories()
        {
            return ProviderRegistry.Settings<SteamSettings>()?.UseSteamHuntersForCategories == true;
        }

        internal Task<SchemaAndPercentages> FetchSchemaAsync(string accessToken, int appId, CancellationToken cancel)
        {
            var language = string.IsNullOrWhiteSpace(_settings.Persisted.GlobalLanguage) ? "english" : _settings.Persisted.GlobalLanguage.Trim();
            return _steamApiClient.GetSchemaForGameDetailedAsync(
                accessToken,
                appId,
                language,
                cancel);
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
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A timeout-shaped OperationCanceledException (HttpClient timeout) without the run
                // token being cancelled is transient, not a user cancel.
                if (ex is OperationCanceledException || IsTransientError(ex))
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
                var apiNamesByRow = SteamAchievementApiNameResolver.Resolve(schema, scraped.Rows);

                foreach (var row in scraped.Rows)
                {
                    if (row == null ||
                        !apiNamesByRow.TryGetValue(row, out var apiName) ||
                        string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    data.ProgressNum[apiName] = row.ProgressNum;
                    data.ProgressDenom[apiName] = row.ProgressDenom;

                    if (row.IsUnlocked)
                    {
                        data.UnlockedApiNames.Add(apiName);
                        if (row.UnlockTimeUtc.HasValue)
                        {
                            data.UnlockTimesUtc[apiName] = row.UnlockTimeUtc.Value;
                        }
                    }
                }
            }

            return data;
        }

        // ---------------------------------------------------------------------
        // Achievements scraping
        // ---------------------------------------------------------------------

        internal async Task<AchievementsScrapeResponse> ScrapeAchievementsAsync(
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
