using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Centralized read-side service for cached achievement data and hydration overlays.
    /// </summary>
    public sealed partial class AchievementDataService
    {
        private readonly ICacheManager _cacheService;
        private readonly ICacheReadOptimizations _cacheReadOptimizations;
        private readonly GameDataHydrator _hydrator;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly GameCustomDataStore _gameCustomDataStore;
        // The settings wrapper, not its PersistedSettings: CancelEdit replaces the
        // Persisted instance, so a captured instance would keep serving the values the
        // user reverted.
        private readonly PlayniteAchievementsSettings _settings;
        private PersistedSettings Persisted => _settings.Persisted;

        public AchievementDataService(
            ICacheManager cacheService,
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            GameCustomDataStore gameCustomDataStore = null)
        {
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _logger = logger;
            _api = api;
            _gameCustomDataStore = gameCustomDataStore;
            _settings = settings;
            _cacheReadOptimizations = cacheService as ICacheReadOptimizations;
            _hydrator = new GameDataHydrator(api, settings, _gameCustomDataStore);
        }

        public GameAchievementData GetGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return GetMergedGameAchievementData(playniteGameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetVisibleGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return ProjectVisibleGameAchievementData(
                    GetMergedGameAchievementData(playniteGameId));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get visible achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetRawGameAchievementData(Guid playniteGameId)
        {
            return GetRawGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetRawGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return _cacheService.LoadGameData(playniteGameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        /// <summary>
        /// The game's cached data without resolving its achievements, for callers that read only
        /// game-level fields such as the provider and name. A game with only custom achievements
        /// yields the synthetic custom data.
        /// </summary>
        public GameAchievementData GetGameHeader(Guid playniteGameId) =>
            GetRawGameAchievementData(playniteGameId)
            ?? CreateSyntheticCustomGameData(playniteGameId, LoadCustomData(playniteGameId));

        /// <summary>
        /// Raw cached data plus the game's custom achievement projections, with no overlays
        /// applied. A game with only custom achievements yields the synthetic custom data.
        /// </summary>
        public GameAchievementData GetRawGameAchievementDataWithCustomAchievements(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                var data = _cacheService.LoadGameData(playniteGameId.ToString());
                if (data == null)
                {
                    return CreateSyntheticCustomGameData(playniteGameId, LoadCustomData(playniteGameId));
                }

                _hydrator.AppendCustomAchievements(data);
                return data;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public List<string> GetCachedGameIds()
        {
            try
            {
                return _cacheService.GetCachedGameIds() ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get cached game ids");
                return new List<string>();
            }
        }

        public bool HasCachedGameData()
        {
            return GetCachedGameIds().Count > 0;
        }

        public GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GetGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetVisibleGameAchievementData(Guid playniteGameId)
        {
            return GetVisibleGameAchievementData(playniteGameId.ToString());
        }

        /// <summary>
        /// Every cached game without its achievements (custom-only games keep theirs): enough for
        /// identity lookups (provider key, app id, Playnite id, name) at a fraction of loading
        /// every achievement.
        /// </summary>
        public List<GameAchievementData> GetAllCachedGameDataForLookup()
        {
            try
            {
                var result = _cacheReadOptimizations?.LoadAllGameHeadersFast() ?? new List<GameAchievementData>();
                AppendSyntheticCustomOnlyGameData(result);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load cached game identities");
                return new List<GameAchievementData>();
            }
        }
        /// <summary>
        /// The library summary from the resolved rows, with games Playnite hides left out when
        /// summaries leave hidden games out.
        /// </summary>
        /// <param name="days">Whether to read the unlock days the Overview's charts count.</param>
        internal CachedSummaryData GetCachedSummaryDataForOverview(int recentAchievementDetailLimit = 0, bool days = true)
        {
            try
            {
                var hidden = new HashSet<Guid>();
                AddPlayniteHiddenGameIds(hidden);
                var summary = _cacheReadOptimizations?.LoadCachedSummaryDataFast(
                    Math.Max(0, recentAchievementDetailLimit),
                    hidden,
                    AuthoredGame,
                    days);
                var separateLockedIcons = new Dictionary<Guid, bool>();
                foreach (var row in summary?.RecentUnlocks ?? Enumerable.Empty<CachedRecentUnlockData>())
                {
                    var gameId = row.PlayniteGameId ?? Guid.Empty;
                    if (!separateLockedIcons.TryGetValue(gameId, out var separate))
                    {
                        separate = GameCustomDataLookup.ShouldUseSeparateLockedIcons(gameId, Persisted, _gameCustomDataStore);
                        separateLockedIcons[gameId] = separate;
                    }

                    row.UseSeparateLockedIconsWhenAvailable = separate;
                }

                return summary;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get cached summary data");
                return null;
            }
        }

        /// <summary>The resolved reads and the change feed; null without the SQL cache.</summary>
        internal ICacheReadOptimizations CacheReads => _cacheReadOptimizations;

        /// <summary>The library summary without the unlock days, which no theme list counts.</summary>
        internal CachedSummaryData GetCachedSummaryDataForTheme(int recentAchievementDetailLimit = 0) =>
            GetCachedSummaryDataForOverview(recentAchievementDetailLimit, days: false);

        /// <summary>The current user's rows under the summary rules, without the games Playnite hides when summaries leave them out.</summary>
        internal AchievementQuery SummaryQuery()
        {
            var hidden = new HashSet<Guid>();
            AddPlayniteHiddenGameIds(hidden);
            return AchievementQuery.CurrentUser.WithSummaryRules().ExceptGames(hidden);
        }

        /// <summary>Every row <paramref name="query"/> reads, in its order, as the summary maps them; none without the SQL cache.</summary>
        internal List<CachedRecentUnlockData> ReadRows(AchievementQuery query) =>
            _cacheReadOptimizations?.LoadRows(query, null) ?? new List<CachedRecentUnlockData>();

        /// <summary>A game with only authored achievements: named by Playnite, shown as its custom provider.</summary>
        private CachedGameSummaryData AuthoredGame(Guid gameId)
        {
            var customData = LoadCustomData(gameId);
            return new CachedGameSummaryData
            {
                ProviderKey = CustomAchievementProjectionService.ProviderKey,
                ProviderPlatformKey = ResolveCustomProviderPlatformKey(customData?.CustomProviderId),
                GameName = GetGame(gameId)?.Name,
                RaritySource = CustomAchievementProjectionService.ResolveRaritySource(customData?.CustomAchievements) ?? RaritySource.None,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        private Dictionary<Guid, GameCustomDataFile> LoadCustomDataByGameId() =>
            (_gameCustomDataStore?.LoadAll() ?? new List<GameCustomDataFile>())
                .Where(row => row != null && row.PlayniteGameId != Guid.Empty)
                .GroupBy(row => row.PlayniteGameId)
                .ToDictionary(group => group.Key, group => group.Last());

        /// <summary>
        /// With IncludeHiddenGamesInSummaries off, games hidden in Playnite are left out of
        /// summaries the same way as Excluded from Summaries. They join only this resolved set,
        /// never the custom-data flag, so the game menu and Manage Achievements keep showing
        /// the user's own exclusion.
        /// </summary>
        private void AddPlayniteHiddenGameIds(HashSet<Guid> excluded)
        {
            if (excluded == null || Persisted?.IncludeHiddenGamesInSummaries != false)
            {
                return;
            }

            try
            {
                var games = _api?.Database?.Games;
                if (games == null)
                {
                    return;
                }

                foreach (var game in games)
                {
                    if (game?.Hidden == true)
                    {
                        excluded.Add(game.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to resolve Playnite-hidden games for summary exclusion.");
            }
        }

        private static Dictionary<string, CategoryImageOverrideData> CloneCategoryImageOverrideMap(
            IReadOnlyDictionary<string, CategoryImageOverrideData> source)
        {
            var map = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            if (source == null || source.Count == 0)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = AchievementCategoryHelper.NormalizeCategoryOrDefault(pair.Key);
                var art = NormalizeText(pair.Value?.Art);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(art))
                {
                    continue;
                }

                map[key] = new CategoryImageOverrideData
                {
                    Art = art
                };
            }

            return map;
        }


        private GameAchievementData ProjectVisibleGameAchievementData(GameAchievementData source)
        {
            if (source == null)
            {
                return null;
            }

            var sourceAchievements = source.Achievements ?? new List<AchievementDetail>();
            var visibleAchievements = sourceAchievements.Where(achievement => achievement != null && !achievement.IsFiltered).ToList();
            if (visibleAchievements.Count == sourceAchievements.Count)
            {
                return source;
            }

            return new GameAchievementData
            {
                LastUpdatedUtc = source.LastUpdatedUtc,
                ProviderKey = source.ProviderKey,
                ProviderPlatformKey = source.ProviderPlatformKey,
                LibrarySourceName = source.LibrarySourceName,
                RaritySource = source.RaritySource,
                PlatformFields = source.PlatformFields,
                MetadataFields = source.MetadataFields,
                HasAchievements = source.HasAchievements && visibleAchievements.Count > 0,
                ExcludedByUser = source.ExcludedByUser,
                IsAppIdOverridden = source.IsAppIdOverridden,
                GameName = source.GameName,
                AppId = source.AppId,
                ProviderGameKey = source.ProviderGameKey,
                PlayniteGameId = source.PlayniteGameId,
                Game = source.Game,
                AchievementOrder = source.AchievementOrder != null
                    ? new List<string>(source.AchievementOrder)
                    : null,
                AchievementCategoryOrder = source.AchievementCategoryOrder != null
                    ? new List<string>(source.AchievementCategoryOrder)
                    : null,
                AchievementCategoryImageOverrides = CloneCategoryImageOverrideMap(source.AchievementCategoryImageOverrides),
                GameSummaryCategory = source.GameSummaryCategory,
                ExcludedFromSummaries = source.ExcludedFromSummaries,
                UseSeparateLockedIconsWhenAvailable = source.UseSeparateLockedIconsWhenAvailable,
                Achievements = visibleAchievements
            };
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private GameAchievementData GetMergedGameAchievementData(string playniteGameId)
        {
            var data = _cacheService.LoadGameData(playniteGameId);
            if (data == null && Guid.TryParse(playniteGameId, out var parsedGameId))
            {
                data = CreateSyntheticCustomGameData(parsedGameId, LoadCustomData(parsedGameId));
            }

            _hydrator.Hydrate(data);

            return data;
        }

        private void AppendSyntheticCustomOnlyGameData(ICollection<GameAchievementData> result)
        {
            if (result == null || _gameCustomDataStore == null)
            {
                return;
            }

            var existingIds = new HashSet<Guid>(
                result
                    .Where(data => data?.PlayniteGameId.HasValue == true && data.PlayniteGameId.Value != Guid.Empty)
                    .Select(data => data.PlayniteGameId.Value));

            foreach (var customData in LoadCustomDataByGameId().Values)
            {
                if (customData == null ||
                    customData.PlayniteGameId == Guid.Empty ||
                    existingIds.Contains(customData.PlayniteGameId) ||
                    !CustomAchievementProjectionService.HasCustomAchievements(customData))
                {
                    continue;
                }

                var synthetic = CreateSyntheticCustomGameData(customData.PlayniteGameId, customData);
                if (synthetic == null)
                {
                    continue;
                }

                result.Add(synthetic);
                existingIds.Add(customData.PlayniteGameId);
            }
        }

        private GameCustomDataFile LoadCustomData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty || _gameCustomDataStore == null)
            {
                return null;
            }

            return _gameCustomDataStore.TryLoad(playniteGameId, out var data)
                ? data
                : null;
        }

        private GameAchievementData CreateSyntheticCustomGameData(
            Guid playniteGameId,
            GameCustomDataFile customData)
        {
            if (!CustomAchievementProjectionService.HasCustomAchievements(customData))
            {
                return null;
            }

            return CustomAchievementProjectionService.CreateSyntheticGameData(
                playniteGameId,
                GetGame(playniteGameId),
                customData.CustomAchievements,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                ResolveCustomProviderPlatformKey(customData.CustomProviderId));
        }

        /// <summary>
        /// The display key for an assigned custom provider, or null when the id is blank or no
        /// longer names a stored provider (the game then displays as plain Custom).
        /// </summary>
        private static string ResolveCustomProviderPlatformKey(string customProviderId)
        {
            if (string.IsNullOrWhiteSpace(customProviderId))
            {
                return null;
            }

            return PlayniteAchievementsPlugin.Instance?.CustomProviderStore?.ResolveDisplayKey(customProviderId);
        }

        private Playnite.SDK.Models.Game GetGame(Guid playniteGameId)
        {
            try
            {
                return _api?.Database?.Games?.Get(playniteGameId);
            }
            catch
            {
                return null;
            }
        }
    }
}
