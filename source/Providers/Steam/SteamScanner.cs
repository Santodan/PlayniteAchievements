using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Providers.Steam.Local;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Cache;
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

        /// <summary>
        /// The user's stats for a game could not be read: an expired Steam session, a private or
        /// missing profile, or a stats page that yielded no usable rows. Non-transient, so it fails
        /// the game fast instead of retrying, and the refresh writes nothing rather than recording
        /// every achievement as locked.
        /// </summary>
        private sealed class SteamStatsUnavailableException : Exception
        {
            public SteamStatsUnavailableException(string message)
                : base(message)
            {
            }

            public SteamStatsUnavailableException(string message, Exception innerException)
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
        private readonly Func<ICacheManager> _cacheAccessor;
        private readonly SteamLocalStatsReader _localStatsReader;
        private readonly SteamCategoryArtDownloader _categoryArtDownloader;

        public SteamScanner(
            PlayniteAchievementsSettings settings,
            SteamHttpClient steamClient,
            SteamApiClient steamApiClient,
            SteamWebApiTokenResolver tokenResolver,
            IPlayniteAPI api,
            ILogger logger,
            Func<ICacheManager> cacheAccessor = null,
            SteamLocalStatsReader localStatsReader = null,
            SteamCategoryArtDownloader categoryArtDownloader = null)
        {
            _categoryArtDownloader = categoryArtDownloader;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _steamClient = steamClient ?? throw new ArgumentNullException(nameof(steamClient));
            _steamApiClient = steamApiClient ?? throw new ArgumentNullException(nameof(steamApiClient));
            _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _cacheAccessor = cacheAccessor ??
                (() => PlayniteAchievementsPlugin.Instance?.CacheManager);
            _localStatsReader = localStatsReader ?? new SteamLocalStatsReader();
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
                if (gamesToRefresh is null || gamesToRefresh.Count == 0)
                {
                    _logger?.Info("[SteamAch] No games found to scan.");
                    return new RebuildPayload { Summary = new RebuildSummary() };
                }

                var configuredSteamUserId = ProviderRegistry.Settings<SteamSettings>()?.SteamUserId?.Trim();
                var steamUserId = configuredSteamUserId;
                string accessToken = null;
                var webAvailable = false;
                try
                {
                    _logger?.Info("[SteamAch] Probing Steam login status before scan...");
                    var tokenResolution = await _tokenResolver.ResolveAsync(cancel).ConfigureAwait(false);
                    steamUserId = tokenResolution.UserId?.Trim() ?? steamUserId;
                    accessToken = tokenResolution.IsSuccess ? tokenResolution.Token : null;
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[SteamAch] Web authentication probe failed; continuing without a token.");
                }

                // Steam's achievement services answer without a token for public profiles, so the
                // web path needs only the account id. Private and friends-only data is read from
                // the achievements page in the signed-in session; the API refused it even with the
                // session's token (EResult 15 for the user's own private profile, 2026-10-16).
                webAvailable = !string.IsNullOrWhiteSpace(steamUserId);
                if (webAvailable && string.IsNullOrWhiteSpace(accessToken))
                {
                    _logger?.Info("[SteamAch] No Steam web token; refreshing through the public Web API.");
                }

                var rateLimiter = new RateLimiter(
                    _settings.Persisted.ScanDelayMs,
                    _settings.Persisted.MaxRetryAttempts);
                var madeWebRequest = false;
                var localFailures = 0;
                var hiddenWithoutToken = 0;
                var authSignals = new AuthSignals();
                var pageFirst = new PageFirstHint();

                var payload = await ProviderRefreshExecutor.RunProviderGamesAsync(
                    gamesToRefresh,
                    onGameStarting,
                    async (game, token) =>
                    {
                        if (!TryGetPlatformAppId(game, out var appId))
                        {
                            _logger?.Warn($"Skipping game without valid AppId: {game?.Name}");
                            return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                        }

                        if (!webAvailable)
                        {
                            var localData = TryBuildGameDataFromLocal(game, steamUserId);
                            if (localData == null)
                            {
                                Interlocked.Increment(ref localFailures);
                            }
                            return localData == null
                                ? ProviderRefreshExecutor.ProviderGameResult.Skipped()
                                : new ProviderRefreshExecutor.ProviderGameResult { Data = localData };
                        }

                        if (madeWebRequest)
                        {
                            await rateLimiter.DelayBeforeNextAsync(token).ConfigureAwait(false);
                        }
                        madeWebRequest = true;

                        GameAchievementData data;
                        try
                        {
                            data = await rateLimiter.ExecuteWithRetryAsync(
                                () => FetchGameDataAsync(game, steamUserId, accessToken, authSignals, pageFirst, token),
                                IsTransientError,
                                token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            // Last-chance local recovery after web retries. A failed local read
                            // returns null, preserving the existing cache via the normal skip path.
                            data = TryBuildGameDataFromLocal(game, steamUserId);
                            if (data == null)
                            {
                                throw;
                            }

                            _logger?.Info($"[SteamLocal] Web refresh failed; recovered appId={appId} from local stats.");
                        }

                        if (data == null)
                        {
                            data = TryBuildGameDataFromLocal(game, steamUserId);
                        }

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
                        if (string.IsNullOrWhiteSpace(accessToken) && ex is SteamStatsUnavailableException)
                        {
                            Interlocked.Increment(ref hiddenWithoutToken);
                        }
                    },
                    delayBetweenGamesAsync: null,
                    delayAfterErrorAsync: null,
                    cancel).ConfigureAwait(false);

                // A complete local fallback is a successful offline refresh. Surface auth only
                // when at least one requested game could use neither the web nor its local files.
                // Signed out, only public data is readable, so a private profile also needs
                // sign-in. A rejected token means the Steam session expired even when anonymous
                // data kept the refresh going.
                if (authSignals.Any)
                {
                    _logger?.Warn($"[SteamAch] Steam sign-in needed: token rejected on {authSignals.TokenRejectedCount} request(s), hidden data without a signed-in session for {authSignals.SignInNeededCount} game(s).");
                }

                payload.AuthRequired =
                    (!webAvailable && localFailures > 0) ||
                    hiddenWithoutToken > 0 ||
                    authSignals.Any;
                return payload;
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
                e is SteamStatsUnavailableException
                    ? false
                    : (e is SteamTransientException ? true : (bool?)null));
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
            AuthSignals authSignals,
            PageFirstHint pageFirst,
            CancellationToken cancel)
        {
            if (!TryGetPlatformAppId(game, out var appId))
            {
                _logger?.Warn($"Could not get AppId from game {game.Name}");
                return null;
            }

            // Steam answers GetGameAchievements with EResult 42 for apps without achievements,
            // which is the one definitive "no achievements" signal. Any other failure is
            // indeterminate and must not be written as an achievement-less game.
            var schemaResult = await FetchSchemaResultAsync(accessToken, appId, cancel).ConfigureAwait(false);
            authSignals?.Record(schemaResult.TokenRejected, signInNeeded: false);
            if (schemaResult.Outcome == SteamApiOutcome.NoAchievements)
            {
                // EResult 42 is Steam's own "no achievements"; an EResult 1 response with an empty
                // list (e.g. Dota 2) means the app defines none.
                var reason = schemaResult.EResult == SteamApiClient.EResultNoMatch
                    ? "Steam reports no achievement schema (eresult=42)"
                    : $"schema lists zero achievements (eresult={schemaResult.EResult?.ToString() ?? "-"})";
                _logger?.Info($"[SteamAch] appId={appId} ({game?.Name}) has no achievements: {reason}.");
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

            if (!schemaResult.IsOk)
            {
                throw new SteamTransientException(
                    $"[SteamAch] Schema unavailable for appId={appId}: {schemaResult}.");
            }

            var schema = schemaResult.Value;
            var hasAchievements = schema?.Achievements?.Count > 0;

            var unlockResult = await FetchUnlockedAsync(appId, game?.Name, steamUserId, accessToken, schema, authSignals, pageFirst, cancel).ConfigureAwait(false);
            var unlocked = unlockResult.Unlocks;
            schema = unlockResult.Schema ?? schema;

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
                        ProgressDenom = progressDenom
                    };

                    var normalizedPercent = NormalizePercent(globalPercent);
                    detail.GlobalPercentUnlocked = normalizedPercent;
                    if (normalizedPercent.HasValue)
                    {
                        detail.Rarity = PercentRarityHelper.GetRarityTier(normalizedPercent.Value);
                    }

                    gameData.Achievements.Add(detail);
                }

                await ApplyCategoriesAsync(appId, gameData.GameName, gameData.Achievements, schema, gameData.PlayniteGameId, cancel).ConfigureAwait(false);
            }

            return gameData;
        }

        /// <summary>
        /// Categories come from Steam's achievement sets, with each set's art, and archived
        /// achievements are Unobtainable. SteamHunters' grouping is metadata: the overlay applies
        /// it after the scan, like any other source.
        /// </summary>
        internal async Task ApplyCategoriesAsync(
            int appId,
            string gameName,
            IList<AchievementDetail> achievements,
            SchemaAndPercentages schema,
            Guid? playniteGameId,
            CancellationToken cancel)
        {
            SteamAchievementSetMapper.ApplySets(achievements, schema, gameName);

            if (playniteGameId.HasValue && schema?.HasAchievementSets == true && _categoryArtDownloader != null)
            {
                await _categoryArtDownloader
                    .DownloadAsync(
                        playniteGameId.Value,
                        SteamAchievementSetMapper.BuildCategoryImagePlan(schema, gameName, appId),
                        cancel)
                    .ConfigureAwait(false);
            }

            SteamAchievementSetMapper.ApplyArchived(achievements, schema);
        }

        private GameAchievementData TryBuildGameDataFromLocal(Game game, string steamUserId)
        {
            if (game == null ||
                !TryGetPlatformAppId(game, out var appId) ||
                !SteamIdHelper.TryGetAccountId3(steamUserId, out var accountId3))
            {
                return null;
            }

            var steamSettings = ProviderRegistry.Settings<SteamSettings>();
            var steamPath = SteamInstallLocator.ResolveSteamPath(
                steamSettings?.SteamInstallPathOverride,
                _logger);
            var statsPath = SteamInstallLocator.BuildUserGameStatsPath(steamPath, accountId3, appId);
            var schemaPath = SteamInstallLocator.BuildSchemaPath(steamPath, appId);
            if (!System.IO.File.Exists(statsPath) || !System.IO.File.Exists(schemaPath))
            {
                return null;
            }

            var cached = _cacheAccessor?.Invoke()?.LoadGameData(game.Id.ToString());
            if (cached?.Achievements == null ||
                cached.Achievements.Count == 0 ||
                !string.Equals(cached.ProviderKey, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var read = _localStatsReader.TryRead(statsPath, schemaPath);
            if (!read.Success)
            {
                return null;
            }

            var cachedKeys = new HashSet<string>(
                cached.Achievements
                    .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.ApiName))
                    .Select(achievement => achievement.ApiName.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (read.UnlockByApiName.Keys.Any(apiName => !cachedKeys.Contains(apiName)))
            {
                _logger?.Debug($"[SteamLocal] Cached schema is stale for appId={appId}; using web refresh.");
                return null;
            }

            var result = new GameAchievementData
            {
                AppId = appId,
                ProviderKey = "Steam",
                ProviderPlatformKey = cached.ProviderPlatformKey,
                ProviderGameKey = cached.ProviderGameKey,
                LibrarySourceName = game.Source?.Name,
                GameName = game.Name,
                PlayniteGameId = game.Id,
                HasAchievements = true,
                LastUpdatedUtc = DateTime.UtcNow,
                Achievements = new List<AchievementDetail>(),
                // Rebuilt from local files and the cache, without the network: metadata sources
                // are not asked either, and the game keeps what its last full refresh saved.
                KeepStoredMetadata = true
            };

            foreach (var source in cached.Achievements)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.ApiName))
                {
                    continue;
                }

                var unlocked = read.UnlockByApiName.TryGetValue(source.ApiName, out var unlockTime);
                result.Achievements.Add(new AchievementDetail
                {
                    ApiName = source.ApiName,
                    DisplayName = source.DisplayName,
                    Description = source.Description,
                    UnlockedIconPath = source.UnlockedIconPath,
                    LockedIconPath = source.LockedIconPath,
                    Points = source.Points,
                    ScaledPoints = source.ScaledPoints,
                    Tags = source.Tags,
                    CategoryKind = source.CategoryKind,
                    Category = source.Category,
                    TrophyType = source.TrophyType,
                    IsCapstone = source.IsCapstone,
                    Hidden = source.Hidden,
                    GlobalPercentUnlocked = source.GlobalPercentUnlocked,
                    Rarity = source.Rarity,
                    ProgressNum = source.ProgressNum,
                    ProgressDenom = source.ProgressDenom,
                    Unlocked = unlocked,
                    UnlockTimeUtc = unlocked ? unlockTime : null
                });
            }

            _logger?.Info(
                $"[SteamLocal] Loaded appId={appId} from local stats " +
                $"({result.UnlockedCount}/{result.AchievementCount} unlocked).");
            return result;
        }

        private string ResolveLanguage() =>
            string.IsNullOrWhiteSpace(_settings.Persisted.GlobalLanguage) ? "english" : _settings.Persisted.GlobalLanguage.Trim();

        internal Task<SchemaAndPercentages> FetchSchemaAsync(string accessToken, int appId, CancellationToken cancel)
        {
            return _steamApiClient.GetSchemaForGameDetailedAsync(
                accessToken,
                appId,
                ResolveLanguage(),
                cancel);
        }

        internal Task<SteamApiResult<SchemaAndPercentages>> FetchSchemaResultAsync(string accessToken, int appId, CancellationToken cancel)
        {
            return _steamApiClient.GetGameSchemaAsync(accessToken, appId, ResolveLanguage(), cancel);
        }

        internal sealed class UnlockFetchResult
        {
            public UserUnlockedAchievements Unlocks { get; set; }

            // Set when the schema was refetched because the player data referenced
            // achievements the original schema did not define.
            public SchemaAndPercentages Schema { get; set; }
        }

        /// <summary>
        /// Outcome of reading one player's unlocks by internal key from the Web API, falling back
        /// to the achievements page. <see cref="Data"/> is null when neither yielded unlocks.
        /// </summary>
        internal sealed class UserAchievementsFetch
        {
            public SteamUserAchievements Data { get; set; }

            // The achievements page's own schema, when the page was read.
            public SchemaAndPercentages PageSchema { get; set; }

            public SteamApiOutcome ApiOutcome { get; set; }

            // Both the Web API and a logged-in achievements page declined to show this player's
            // data: the profile or its game details are private to the viewer.
            public bool ConfirmedPrivate { get; set; }

            // Steam refused the supplied access token; any data came from the anonymous retry.
            public bool TokenRejected { get; set; }

            // The player's data is hidden and no request was made as a signed-in viewer (no
            // token, a rejected token, and no logged-in achievements page), so privacy cannot be
            // told apart from a missing sign-in.
            public bool SignInNeeded { get; set; }

            public bool AuthProblem => TokenRejected || SignInNeeded;
        }

        /// <summary>
        /// Auth problems seen during one refresh, reported as AuthRequired at the end so the user
        /// is asked to sign in again even when anonymous data kept the refresh going.
        /// </summary>
        internal sealed class AuthSignals
        {
            private int _tokenRejected;
            private int _signInNeeded;

            public int TokenRejectedCount => Volatile.Read(ref _tokenRejected);

            public int SignInNeededCount => Volatile.Read(ref _signInNeeded);

            public bool Any => TokenRejectedCount > 0 || SignInNeededCount > 0;

            public void Record(bool tokenRejected, bool signInNeeded)
            {
                if (tokenRejected)
                {
                    Interlocked.Increment(ref _tokenRejected);
                }

                if (signInNeeded)
                {
                    Interlocked.Increment(ref _signInNeeded);
                }
            }
        }

        /// <summary>
        /// Remembers, for one refresh, that the Web API refused a player's data while the
        /// achievements page served it. Later games for that player then go to the page first
        /// instead of spending a refused API call each. Not persisted.
        /// </summary>
        internal sealed class PageFirstHint
        {
            private int _pageFirst;

            public bool PageFirst => Volatile.Read(ref _pageFirst) != 0;

            public void Set() => Interlocked.Exchange(ref _pageFirst, 1);
        }

        /// <summary>
        /// Reads a player's unlocks: IPlayerService/GetUserAchievements first, then the
        /// achievements page (HTTP, then the logged-in browser session when the HTTP page was not
        /// logged in). The API serves only data that is public; private and friends-only data
        /// comes from the page rendered in the viewer's signed-in session. With a set
        /// <paramref name="pageFirst"/> hint the page is tried before the API.
        /// </summary>
        internal async Task<UserAchievementsFetch> FetchUserAchievementsAsync(
            string steamId64,
            int appId,
            string accessToken,
            CancellationToken cancel,
            PageFirstHint pageFirst = null)
        {
            var result = new UserAchievementsFetch();

            SteamSsrAchievementsPage page = null;
            if (pageFirst?.PageFirst == true)
            {
                page = await FetchAchievementsPageAsync(steamId64, appId, cancel).ConfigureAwait(false);
                if (page?.UserAchievementsState == SteamSsrQueryState.Success)
                {
                    // The API refused this player's data earlier in this refresh.
                    result.ApiOutcome = SteamApiOutcome.AccessDenied;
                    result.PageSchema = page.Schema;
                    result.Data = page.UserAchievements;
                    return result;
                }

                // The page failed (for example the session ended mid-refresh); the API decides.
            }

            var api = await _steamApiClient
                .GetUserAchievementsAsync(accessToken, steamId64, appId, cancel)
                .ConfigureAwait(false);
            result.ApiOutcome = api.Outcome;
            result.TokenRejected = api.TokenRejected;
            if (api.IsOk)
            {
                result.Data = api.Value;
                return result;
            }

            if (page == null)
            {
                _logger?.Debug($"[SteamAch] GetUserAchievements for appId={appId}, steamId={steamId64}: {api}. Trying the achievements page.");
                page = await FetchAchievementsPageAsync(steamId64, appId, cancel).ConfigureAwait(false);
            }

            result.PageSchema = page?.Schema;
            if (page?.UserAchievementsState == SteamSsrQueryState.Success)
            {
                if (pageFirst != null && !pageFirst.PageFirst &&
                    (api.Outcome == SteamApiOutcome.AccessDenied || api.Outcome == SteamApiOutcome.Unauthorized))
                {
                    pageFirst.Set();
                    _logger?.Info($"[SteamAch] Web API refuses data for steamId={steamId64} ({api}); reading the achievements page first for the rest of this refresh.");
                }

                result.Data = page.UserAchievements;
                return result;
            }

            var hidden = api.Outcome == SteamApiOutcome.AccessDenied || api.Outcome == SteamApiOutcome.Unauthorized;
            var viewerLoggedIn = page?.ViewerLoggedIn == true;
            result.ConfirmedPrivate =
                hidden &&
                viewerLoggedIn &&
                page.UserAchievementsState == SteamSsrQueryState.Error;
            // Steam answers an invalid token as an anonymous request (EResult 15 for hidden data,
            // not HTTP 401), so a loaded achievements page that is not signed in is the evidence
            // that the session is gone, whatever token was sent.
            result.SignInNeeded =
                hidden &&
                !viewerLoggedIn &&
                (page != null || string.IsNullOrWhiteSpace(accessToken) || api.TokenRejected);
            return result;
        }

        private async Task<SteamSsrAchievementsPage> FetchAchievementsPageAsync(
            string steamId64,
            int appId,
            CancellationToken cancel)
        {
            var language = ResolveLanguage();
            SteamSsrAchievementsPage page = null;
            try
            {
                var http = await _steamClient
                    .GetAchievementsSsrPageAsync(steamId64, appId, language, cancel)
                    .ConfigureAwait(false);
                if ((int)(http?.StatusCode ?? 0) == 429)
                {
                    throw new SteamTransientException($"[SteamAch] Achievements page rate limited for appId={appId}.");
                }

                page = SteamAchievementsSsrParser.Parse(http?.Html, appId);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (SteamTransientException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Achievements page request failed for appId={appId}.");
            }

            if (page?.UserAchievementsState == SteamSsrQueryState.Success || page?.ViewerLoggedIn == true)
            {
                return page;
            }

            // The HTTP page was unreadable or not logged in; the browser session renders with the
            // viewer's visibility.
            try
            {
                var browser = await _steamClient
                    .GetAchievementsSsrPageViaBrowserAsync(steamId64, appId, language, cancel)
                    .ConfigureAwait(false);
                var browserPage = SteamAchievementsSsrParser.Parse(browser?.Html, appId);
                if (browserPage != null)
                {
                    return browserPage;
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Browser achievements page failed for appId={appId}.");
            }

            return page;
        }

        /// <summary>
        /// Own unlocks: Web API and achievements page by internal key, then the legacy stats
        /// page scraper. A schema missing achievements the player has data for (a schema change
        /// between requests) is refetched once.
        /// </summary>
        private async Task<UnlockFetchResult> FetchUnlockedAsync(
            int appId,
            string gameName,
            string steamUserId,
            string accessToken,
            SchemaAndPercentages schema,
            AuthSignals authSignals,
            PageFirstHint pageFirst,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(steamUserId))
            {
                return new UnlockFetchResult { Unlocks = new UserUnlockedAchievements() };
            }

            var fetch = await FetchUserAchievementsAsync(steamUserId, appId, accessToken, cancel, pageFirst).ConfigureAwait(false);
            authSignals?.Record(fetch.TokenRejected, fetch.SignInNeeded);
            if (fetch.SignInNeeded && fetch.Data == null)
            {
                // Without a signed-in viewer, a hidden profile cannot be read and the old stats
                // page redirects to the same achievements page, so skip the legacy scrape.
                throw new SteamStatsUnavailableException(
                    $"[SteamAch] Achievements for appId={appId} are hidden and Steam is not signed in (Web API: {fetch.ApiOutcome}).");
            }
            if (fetch.Data != null)
            {
                var mapping = SteamAchievementSetMapper.MapUnlocks(appId, schema, fetch.Data, fetch.PageSchema);
                SchemaAndPercentages refreshed = null;
                if (mapping.UnknownKeyCount > 0)
                {
                    var refetch = await FetchSchemaResultAsync(accessToken, appId, cancel).ConfigureAwait(false);
                    if (refetch.IsOk)
                    {
                        refreshed = refetch.Value;
                        mapping = SteamAchievementSetMapper.MapUnlocks(appId, refreshed, fetch.Data, fetch.PageSchema);
                    }

                    if (mapping.UnknownKeyCount > 0)
                    {
                        _logger?.Warn($"[SteamAch] appId={appId}: {mapping.UnknownKeyCount} unlock entries reference achievements missing from the schema; skipped.");
                    }
                }

                return new UnlockFetchResult { Unlocks = mapping.Unlocks, Schema = refreshed };
            }

            if (fetch.ConfirmedPrivate)
            {
                throw new SteamStatsUnavailableException(
                    $"[SteamAch] Achievements for appId={appId} are not visible to this account (Web API: {fetch.ApiOutcome}, achievements page: no player data).");
            }

            var legacy = await FetchUnlockedFromStatsPageAsync(appId, gameName, steamUserId, accessToken, schema, cancel).ConfigureAwait(false);
            return new UnlockFetchResult { Unlocks = legacy };
        }

        // Legacy community stats page scraper (/profiles/{id}/stats/{appid}/?tab=achievements).
        // Steam redirected that page to the redesigned achievements page in October 2026; kept
        // as the last fallback.
        private async Task<UserUnlockedAchievements> FetchUnlockedFromStatsPageAsync(
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

                throw new SteamStatsUnavailableException(
                    $"[SteamAch] User achievements scrape failed (non-transient, appId={appId}).", ex);
            }

            if (scraped == null || scraped.TransientFailure)
            {
                var detail = scraped?.DetailCode.ToString() ?? "Unknown";
                var status = scraped?.StatusCode ?? 0;
                throw new SteamTransientException($"[SteamAch] Transient scrape result for appId={appId}. detail={detail}, status={status}");
            }

            // A scrape that produced no usable rows without being transient means the stats could not
            // be read: an expired session, a private or missing profile, a redirect off the stats
            // page, or hidden-only rows. Reporting it as zero unlocks would relock every achievement,
            // so fail the game instead. NoAchievements is the one API-confirmed empty case and
            // legitimately yields an empty set. AllHidden is page-confirmed empty when the schema is
            // itself all-hidden; see SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks.
            var confirmedAllHiddenZeroUnlocks =
                scraped.DetailCode == SteamScrapeDetail.AllHidden &&
                SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks(schema, scraped.HiddenRemainingCount);

            if (!scraped.SuccessWithRows &&
                scraped.DetailCode != SteamScrapeDetail.NoAchievements &&
                !confirmedAllHiddenZeroUnlocks)
            {
                throw new SteamStatsUnavailableException(
                    $"[SteamAch] Stats unavailable for appId={appId}. detail={scraped.DetailCode}, status={scraped.StatusCode}");
            }

            if (confirmedAllHiddenZeroUnlocks)
            {
                _logger?.Info(
                    $"[SteamAch] appId={appId}: all {schema.Achievements.Count} schema achievements " +
                    "hidden and none unlocked; writing empty unlock set.");
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
                    res.HiddenRemainingCount = SteamStatsPageClassifier.TryGetHiddenRemainingCount(html);
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
    }
}
