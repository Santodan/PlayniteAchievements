using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Xbox.Models;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xbox
{
    /// <summary>
    /// Scans Xbox games for achievement data.
    /// Supports Xbox One/Series X|S, Xbox 360, and PC Game Pass games.
    /// </summary>
    internal sealed class XboxScanner
    {
        private sealed class XboxTransientException : Exception
        {
            public XboxTransientException(string message) : base(message) { }
            public XboxTransientException(string message, Exception innerException) : base(message, innerException) { }
        }

        private readonly PlayniteAchievementsSettings _settings;
        private readonly XboxSettings _providerSettings;
        private readonly XboxSessionManager _sessionManager;
        private readonly XboxApiClient _apiClient;
        private readonly ILogger _logger;

        // Xbox library plugin ID from Playnite
        internal static readonly Guid XboxLibraryPluginId = Guid.Parse("7e4fbb5b-4594-4c5a-8a69-1e3f41b39c52");

        public XboxScanner(
            PlayniteAchievementsSettings settings,
            XboxSettings providerSettings,
            XboxSessionManager sessionManager,
            XboxApiClient apiClient,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            try
            {
                _logger?.Info("[XboxAch] Probing Xbox login status before scan...");
                var authData = await _sessionManager.GetAuthorizationAsync(cancel).ConfigureAwait(false);
                if (authData == null)
                {
                    _logger?.Warn("[XboxAch] Xbox auth check failed: not logged in. Aborting scan.");
                    return new RebuildPayload
                    {
                        Summary = new RebuildSummary(),
                        AuthRequired = true
                    };
                }
                _logger?.Info("[XboxAch] Xbox auth verified.");

                if (gamesToRefresh is null || gamesToRefresh.Count == 0)
                {
                    _logger?.Info("[XboxAch] No games found to scan.");
                    return new RebuildPayload { Summary = new RebuildSummary() };
                }

                var xuid = authData.DisplayClaims?.xui?.FirstOrDefault()?.xid;
                if (string.IsNullOrWhiteSpace(xuid))
                {
                    _logger?.Warn("[XboxAch] Could not get XUID from auth data.");
                    return new RebuildPayload { Summary = new RebuildSummary() };
                }

                var rateLimiter = new RateLimiter(
                    _settings.Persisted.ScanDelayMs,
                    _settings.Persisted.MaxRetryAttempts);

                return await ProviderRefreshExecutor.RunProviderGamesAsync(
                    gamesToRefresh,
                    onGameStarting,
                    async (game, token) =>
                    {
                        var data = await rateLimiter.ExecuteWithRetryAsync(
                            () => FetchGameDataAsync(game, authData, xuid, token),
                            IsTransientError,
                            token).ConfigureAwait(false);

                        return new ProviderRefreshExecutor.ProviderGameResult
                        {
                            Data = data
                        };
                    },
                    onGameCompleted,
                    isAuthRequiredException: ex => ex is XboxAuthRequiredException,
                    onGameError: (game, ex, consecutiveErrors) =>
                    {
                        _logger?.Warn($"[XboxAch] Skipping game after retries: {game?.Name}. Consecutive errors={consecutiveErrors}. {ex.GetType().Name}: {ex.Message}");
                    },
                    delayBetweenGamesAsync: null,
                    delayAfterErrorAsync: (consecutiveErrors, token) => rateLimiter.DelayAfterErrorAsync(consecutiveErrors, token),
                    cancel).ConfigureAwait(false);
            }
            catch (XboxAuthRequiredException)
            {
                return new RebuildPayload
                {
                    Summary = new RebuildSummary(),
                    AuthRequired = true
                };
            }
        }

        /// <summary>
        /// Determines if an exception is a transient error that should trigger retry.
        /// </summary>
        private static bool IsTransientError(Exception ex)
        {
            if (ex is OperationCanceledException) return false;
            if (ex is XboxTransientException) return true;

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

        private async Task<GameAchievementData> FetchGameDataAsync(
            Game game,
            AuthorizationData authData,
            string xuid,
            CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            // Resolve title ID
            var titleId = await ResolveTitleIdAsync(game, authData, cancel).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(titleId))
            {
                _logger?.Warn($"[XboxAch] Could not resolve title ID for game: {game.Name}");
                return null;
            }

            // Determine if Xbox 360 game
            var isXbox360 = IsXbox360Game(game);

            // Try fetching achievements - Xbox 360 first if platform matches, otherwise Xbox One first
            List<AchievementDetail> achievements = null;

            if (isXbox360)
            {
                achievements = await TryGetXbox360AchievementsAsync(xuid, titleId, authData, cancel).ConfigureAwait(false);
                if (achievements == null || achievements.Count == 0)
                {
                    achievements = await TryGetXboxOneAchievementsAsync(xuid, titleId, game.Name, authData, cancel).ConfigureAwait(false);
                }
            }
            else
            {
                achievements = await TryGetXboxOneAchievementsAsync(xuid, titleId, game.Name, authData, cancel).ConfigureAwait(false);
                if (achievements == null || achievements.Count == 0)
                {
                    achievements = await TryGetXbox360AchievementsAsync(xuid, titleId, authData, cancel).ConfigureAwait(false);
                }
            }

            // Parse titleId to int for AppId
            int appId = 0;
            if (!string.IsNullOrWhiteSpace(titleId) && int.TryParse(titleId, out var parsedId))
            {
                appId = parsedId;
            }

            return new GameAchievementData
            {
                AppId = appId,
                GameName = game.Name,
                ProviderKey = "Xbox",
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = achievements != null && achievements.Count > 0,
                PlayniteGameId = game.Id,
                Achievements = achievements ?? new List<AchievementDetail>()
            };
        }

        private async Task<string> ResolveTitleIdAsync(Game game, AuthorizationData authData, CancellationToken cancel)
        {
            // Console games: GameId = "CONSOLE_{titleId}"
            if (game.GameId?.StartsWith("CONSOLE_") == true)
            {
                var parts = game.GameId.Split('_');
                if (parts.Length >= 2)
                {
                    return parts[1];
                }
            }

            // PC games: Use Title Hub API to resolve PFN to titleId
            if (!string.IsNullOrWhiteSpace(game.GameId))
            {
                try
                {
                    var titleInfo = await _apiClient.GetTitleInfoByPfnAsync(game.GameId, authData, cancel).ConfigureAwait(false);
                    if (titleInfo != null && !string.IsNullOrWhiteSpace(titleInfo.titleId))
                    {
                        return titleInfo.titleId;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[XboxAch] Failed to get title info for PFN: {game.GameId}");
                }
            }

            return null;
        }

        private static bool IsXbox360Game(Game game)
        {
            return game.Platforms?.Any(p => p.SpecificationId == "xbox360") == true;
        }

        private async Task<List<AchievementDetail>> TryGetXboxOneAchievementsAsync(
            string xuid,
            string titleId,
            string gameName,
            AuthorizationData authData,
            CancellationToken cancel)
        {
            try
            {
                var response = await _apiClient.GetXboxOneAchievementsAsync(xuid, titleId, authData, cancel).ConfigureAwait(false);

                if (response?.achievements == null || response.achievements.Count == 0)
                {
                    return null;
                }

                // Filter by game name if no title ID was provided
                var achievements = response.achievements;
                if (string.IsNullOrWhiteSpace(titleId) && !string.IsNullOrWhiteSpace(gameName))
                {
                    achievements = achievements
                        .Where(a => a.titleAssociations?.Any(t => string.Equals(t.name, gameName, StringComparison.OrdinalIgnoreCase)) == true)
                        .ToList();
                }

                return achievements.Select(ConvertToAchievementDetail).ToList();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Debug(ex, $"[XboxAch] Failed to get Xbox One achievements for title {titleId}");
                return null;
            }
        }

        private async Task<List<AchievementDetail>> TryGetXbox360AchievementsAsync(
            string xuid,
            string titleId,
            AuthorizationData authData,
            CancellationToken cancel)
        {
            try
            {
                // Get both unlocked and all achievements in parallel
                var unlockedTask = _apiClient.GetXbox360UnlockedAsync(xuid, titleId, authData, cancel);
                var allTask = _apiClient.GetXbox360AllAsync(xuid, titleId, authData, cancel);

                await Task.WhenAll(unlockedTask, allTask).ConfigureAwait(false);

                var unlockedResponse = await unlockedTask.ConfigureAwait(false);
                var allResponse = await allTask.ConfigureAwait(false);

                if (unlockedResponse?.achievements == null && allResponse?.achievements == null)
                {
                    return null;
                }

                // Merge unlocked and all achievements
                var mergedAchievements = new Dictionary<int, Xbox360Achievement>();

                // Add unlocked achievements first (they have unlock times)
                if (unlockedResponse?.achievements != null)
                {
                    foreach (var ach in unlockedResponse.achievements)
                    {
                        mergedAchievements[ach.id] = ach;
                    }
                }

                // Add all achievements (locked ones won't have unlock times)
                if (allResponse?.achievements != null)
                {
                    foreach (var ach in allResponse.achievements)
                    {
                        if (!mergedAchievements.ContainsKey(ach.id))
                        {
                            mergedAchievements[ach.id] = ach;
                        }
                    }
                }

                return mergedAchievements.Values.Select(ConvertToAchievementDetail).ToList();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Debug(ex, $"[XboxAch] Failed to get Xbox 360 achievements for title {titleId}");
                return null;
            }
        }

        private AchievementDetail ConvertToAchievementDetail(XboxOneAchievement xboxAch)
        {
            var isUnlocked = xboxAch.progression?.timeUnlocked != default && xboxAch.progression?.timeUnlocked != DateTime.MinValue;
            var gamerscore = 0;
            var reward = xboxAch.rewards?.FirstOrDefault(r => string.Equals(r.type, "Gamerscore", StringComparison.OrdinalIgnoreCase));
            if (reward != null && int.TryParse(reward.value, out var gs))
            {
                gamerscore = gs;
            }

            // Log mediaAssets for debugging
            if (xboxAch.mediaAssets == null || xboxAch.mediaAssets.Count == 0)
            {
                _logger.Debug($"[XboxAch] No mediaAssets for achievement: {xboxAch.name} (id: {xboxAch.id})");
            }
            else
            {
                // _logger.Debug($"[XboxAch] Achievement '{xboxAch.name}' has {xboxAch.mediaAssets.Count} media assets:");
                // foreach (var asset in xboxAch.mediaAssets)
                // {
                //     _logger.Debug($"  - name: '{asset.name}', type: '{asset.type}', url: '{asset.url}'");
                // }
            }

            // Use first media asset directly (matches reference implementation pattern)
            var rawUrl = xboxAch.mediaAssets?.FirstOrDefault()?.url;
            var iconUrl = _providerSettings.LowResIcons
                ? AddXboxResizeParam(rawUrl)
                : rawUrl;

            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                _logger.Warn($"[XboxAch] No icon URL found for achievement: {xboxAch.name}");
            }

            return new AchievementDetail
            {
                ApiName = xboxAch.id,
                DisplayName = xboxAch.name ?? string.Empty,
                Description = isUnlocked ? xboxAch.description : xboxAch.lockedDescription,
                UnlockedIconPath = iconUrl,
                LockedIconPath = iconUrl,
                Points = gamerscore,
                Rarity = GetRarityFromXboxPoints(gamerscore),
                Category = null,
                Hidden = xboxAch.isSecret,
                GlobalPercentUnlocked = null,
                UnlockTimeUtc = isUnlocked ? xboxAch.progression.timeUnlocked : (DateTime?)null,
                Unlocked = isUnlocked,
                ProgressNum = null,
                ProgressDenom = null
            };
        }

        private static AchievementDetail ConvertToAchievementDetail(Xbox360Achievement xboxAch)
        {
            var isUnlocked = xboxAch.unlocked || xboxAch.unlockedOnline;
            // SqlDateTime.MinValue (1753-01-01) is returned by Xbox 360 API for invalid dates
            var unlockTime = isUnlocked && IsValidXboxDate(xboxAch.timeUnlocked)
                ? xboxAch.timeUnlocked
                : (DateTime?)null;

            // Xbox 360 achievement icon URL format
            var iconUrl = $"https://image-ssl.xboxlive.com/global/t.{xboxAch.titleId:x}/ach/0/{xboxAch.imageId:x}";

            return new AchievementDetail
            {
                ApiName = xboxAch.id.ToString(),
                DisplayName = xboxAch.name ?? string.Empty,
                Description = isUnlocked ? xboxAch.lockedDescription : xboxAch.description,
                UnlockedIconPath = iconUrl,
                LockedIconPath = iconUrl,
                Points = xboxAch.gamerscore,
                Rarity = GetRarityFromXboxPoints(xboxAch.gamerscore),
                Category = null,
                Hidden = xboxAch.isSecret,
                GlobalPercentUnlocked = null,
                UnlockTimeUtc = unlockTime,
                Unlocked = isUnlocked,
                ProgressNum = null,
                ProgressDenom = null
            };
        }

        /// <summary>
        /// Validates that a DateTime is not a sentinel/invalid value.
        /// Xbox 360 API returns 1753-01-01 (SqlDateTime.MinValue) for missing dates.
        /// </summary>
        private static bool IsValidXboxDate(DateTime date)
        {
            if (date == default) return false;
            if (date == DateTime.MinValue) return false;
            // SqlDateTime.MinValue: 1753-01-01 00:00:00
            if (date.Year == 1753 && date.Month == 1 && date.Day == 1) return false;
            return true;
        }

        /// <summary>
        /// Adds the w=128 resize parameter to Xbox EDS image URLs to request smaller icons.
        /// This significantly reduces download time for achievement icons.
        /// </summary>
        private static string AddXboxResizeParam(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            // EDS image proxy supports w= parameter for width scaling
            if (url.IndexOf("images-eds-ssl.xboxlive.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Request 128px width - matches decode size, height auto-scales
                var separator = url.IndexOf("?") >= 0 ? "&" : "?";
                return $"{url}{separator}w=128";
            }

            return url;
        }

        private static RarityTier GetRarityFromXboxPoints(int? points)
        {
            var value = Math.Max(0, points ?? 0);
            if (value >= 100)
            {
                return RarityTier.UltraRare;
            }

            if (value >= 50)
            {
                return RarityTier.Rare;
            }

            if (value >= 25)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }
    }
}
