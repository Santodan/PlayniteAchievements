using System;
using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Services.Hydration
{
    /// <summary>
    /// Hydrates GameAchievementData with non-persisted properties derived from
    /// Playnite API and plugin settings.
    /// </summary>
    public class GameDataHydrator
    {
        private readonly IPlayniteAPI _api;
        // The settings wrapper, not its PersistedSettings: CancelEdit replaces the
        // Persisted instance, and this hydrator outlives a settings dialog.
        private readonly PlayniteAchievementsSettings _settingsHost;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly AchievementDetailHydrator _achievementHydrator;

        public GameDataHydrator(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            GameCustomDataStore gameCustomDataStore = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settingsHost = settings ?? throw new ArgumentNullException(nameof(settings));
            _gameCustomDataStore = gameCustomDataStore;
            _achievementHydrator = new AchievementDetailHydrator(settings);
        }

        private PersistedSettings Persisted => _settingsHost.Persisted;

        /// <summary>
        /// Hydrates a single GameAchievementData with non-persisted properties.
        /// </summary>
        public void Hydrate(GameAchievementData data)
        {
            if (data?.PlayniteGameId == null)
            {
                return;
            }

            var gameId = data.PlayniteGameId.Value;
            var customData = GameCustomDataLookup.ResolveGameCustomData(gameId, Persisted, _gameCustomDataStore);

            // Populate ExcludedByUser from settings
            data.ExcludedByUser = customData.ExcludedFromRefreshes;
            data.ExcludedFromSummaries = customData.ExcludedFromSummaries;
            data.UseSeparateLockedIconsWhenAvailable = customData.UseSeparateLockedIcons;

            // Set Game reference from Playnite database (SortingName is computed from this)
            data.Game = GetGame(gameId);

            // Populate runtime custom order from settings.
            data.AchievementOrder = null;
            var configuredOrder = customData.AchievementOrder;
            if (configuredOrder.Count > 0)
            {
                data.AchievementOrder = configuredOrder;
            }

            data.GoalAchievements = customData.GoalAchievementApiNames != null && customData.GoalAchievementApiNames.Count > 0
                ? new List<string>(customData.GoalAchievementApiNames)
                : null;

            data.AchievementCategoryOrder = customData.AchievementCategoryOrder != null && customData.AchievementCategoryOrder.Count > 0
                ? new List<string>(customData.AchievementCategoryOrder)
                : null;
            data.AchievementCategoryImageOverrides = customData.AchievementCategoryImageOverrides != null &&
                                                     customData.AchievementCategoryImageOverrides.Count > 0
                ? CloneCategoryImageOverrideMap(customData.AchievementCategoryImageOverrides, gameId)
                : null;
            data.GameSummaryCategory = customData.GameSummaryCategory;

            // Hydrate achievements with settings overlays (capstone + category/category-type overrides).
            AppendCustomAchievements(data, gameId, customData);
            if (data.Achievements != null && data.Achievements.Count > 0)
            {
                _achievementHydrator.HydrateAllWithCapstoneOverride(
                    data.Achievements,
                    gameId,
                    data.EffectiveProviderKey,
                    customData);

                ApplyAchievementIconOverrides(gameId, data.Achievements);
            }
        }

        /// <summary>
        /// Hydrates overview-relevant runtime properties and applies capstone overlays
        /// needed for completion calculations.
        /// </summary>
        public void HydrateForOverview(GameAchievementData data)
        {
            if (data?.PlayniteGameId == null)
            {
                return;
            }

            var gameId = data.PlayniteGameId.Value;
            var customData = GameCustomDataLookup.ResolveGameCustomData(gameId, Persisted, _gameCustomDataStore);

            data.ExcludedFromSummaries = customData.ExcludedFromSummaries;
            data.UseSeparateLockedIconsWhenAvailable = customData.UseSeparateLockedIcons;
            data.Game = GetGame(gameId);
            data.AchievementCategoryOrder = customData.AchievementCategoryOrder != null && customData.AchievementCategoryOrder.Count > 0
                ? new List<string>(customData.AchievementCategoryOrder)
                : null;
            data.AchievementCategoryImageOverrides = customData.AchievementCategoryImageOverrides != null &&
                                                     customData.AchievementCategoryImageOverrides.Count > 0
                ? CloneCategoryImageOverrideMap(customData.AchievementCategoryImageOverrides, gameId)
                : null;
            data.GameSummaryCategory = customData.GameSummaryCategory;

            AppendCustomAchievements(data, gameId, customData);
            if (data.Achievements != null && data.Achievements.Count > 0)
            {
                _achievementHydrator.HydrateAllWithCapstoneOverride(
                    data.Achievements,
                    gameId,
                    data.EffectiveProviderKey,
                    customData);

                ApplyAchievementIconOverrides(gameId, data.Achievements);
            }
        }

        /// <summary>
        /// Appends the game's custom achievement projections with no overlays applied. Custom
        /// achievements exist only in custom data, so a raw cache read omits them; callers that
        /// need the un-overlaid row set including custom rows go through here.
        /// </summary>
        public void AppendCustomAchievements(GameAchievementData data)
        {
            if (data?.PlayniteGameId == null)
            {
                return;
            }

            var gameId = data.PlayniteGameId.Value;
            var customData = GameCustomDataLookup.ResolveGameCustomData(gameId, Persisted, _gameCustomDataStore);
            AppendCustomAchievements(data, gameId, customData);
        }

        private static void AppendCustomAchievements(
            GameAchievementData data,
            Guid gameId,
            ResolvedGameCustomData customData)
        {
            if (data == null)
            {
                return;
            }

            data.Achievements ??= new List<AchievementDetail>();
            for (var i = data.Achievements.Count - 1; i >= 0; i--)
            {
                var achievement = data.Achievements[i];
                if (achievement?.IsCustom == true ||
                    CustomAchievementProjectionService.IsCustomApiName(achievement?.ApiName))
                {
                    data.Achievements.RemoveAt(i);
                }
            }

            var definitions = customData?.CustomAchievements;
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            var managedCustomIconService = PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService;
            var projected = CustomAchievementProjectionService.ProjectAchievements(
                gameId,
                definitions,
                managedCustomIconService);
            if (projected.Count == 0)
            {
                return;
            }

            data.HasAchievements = true;
            data.Achievements.AddRange(projected);
        }

        /// <summary>
        /// Hydrates multiple GameAchievementData instances with non-persisted properties.
        /// </summary>
        public void HydrateAll(IEnumerable<GameAchievementData> games)
        {
            if (games == null)
            {
                return;
            }

            foreach (var game in games)
            {
                Hydrate(game);
            }
        }

        /// <summary>
        /// Hydrates multiple GameAchievementData instances for overview use only.
        /// </summary>
        public void HydrateAllForOverview(IEnumerable<GameAchievementData> games)
        {
            if (games == null)
            {
                return;
            }

            foreach (var game in games)
            {
                HydrateForOverview(game);
            }
        }

        private Playnite.SDK.Models.Game GetGame(Guid playniteGameId)
        {
            try
            {
                return _api.Database.Games.Get(playniteGameId);
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyAchievementIconOverrides(Guid gameId, IList<AchievementDetail> achievements)
        {
            AchievementIconOverrideHelper.ApplyOverrides(
                gameId,
                achievements,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                achievement => achievement.ApiName,
                (achievement, path) => achievement.UnlockedIconPath = path,
                (achievement, path) => achievement.LockedIconPath = path);
        }

        private static string ResolveIconOverridePath(
            string path,
            string gameIdText,
            ManagedCustomIconService managedCustomIconService) =>
            AchievementIconOverrideHelper.ResolveOverridePath(path, gameIdText, managedCustomIconService);

        private static Dictionary<string, CategoryImageOverrideData> CloneCategoryImageOverrideMap(
            IReadOnlyDictionary<string, CategoryImageOverrideData> source,
            Guid gameId)
        {
            var result = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            var managedCustomIconService = PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService;
            var gameIdText = gameId.ToString("D");
            foreach (var pair in source)
            {
                var category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(category) || pair.Value == null)
                {
                    continue;
                }

                result[category] = new CategoryImageOverrideData
                {
                    Art = ResolveIconOverridePath(pair.Value.Art, gameIdText, managedCustomIconService)
                };
            }

            return result;
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
