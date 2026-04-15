using System;
using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
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
        private readonly PersistedSettings _settings;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly AchievementDetailHydrator _achievementHydrator;

        public GameDataHydrator(
            IPlayniteAPI api,
            PersistedSettings settings,
            GameCustomDataStore gameCustomDataStore = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _gameCustomDataStore = gameCustomDataStore;
            _achievementHydrator = new AchievementDetailHydrator(settings);
        }

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
            var customData = GameCustomDataLookup.ResolveGameCustomData(gameId, _settings, _gameCustomDataStore);

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

            // Hydrate achievements with settings overlays (capstone + category/category-type overrides).
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
        /// Hydrates sidebar-relevant runtime properties and applies capstone overlays
        /// needed for completion calculations.
        /// </summary>
        public void HydrateForSidebar(GameAchievementData data)
        {
            if (data?.PlayniteGameId == null)
            {
                return;
            }

            var gameId = data.PlayniteGameId.Value;
            var customData = GameCustomDataLookup.ResolveGameCustomData(gameId, _settings, _gameCustomDataStore);

            data.ExcludedFromSummaries = customData.ExcludedFromSummaries;
            data.UseSeparateLockedIconsWhenAvailable = customData.UseSeparateLockedIcons;
            data.Game = GetGame(gameId);

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
        /// Hydrates multiple GameAchievementData instances for sidebar use only.
        /// </summary>
        public void HydrateAllForSidebar(IEnumerable<GameAchievementData> games)
        {
            if (games == null)
            {
                return;
            }

            foreach (var game in games)
            {
                HydrateForSidebar(game);
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
            if (achievements == null || achievements.Count == 0)
            {
                return;
            }

            var unlockedOverrides = GameCustomDataLookup.GetAchievementUnlockedIconOverrides(gameId);
            var lockedOverrides = GameCustomDataLookup.GetAchievementLockedIconOverrides(gameId);
            if (!AchievementIconOverrideHelper.HasOverrides(unlockedOverrides, lockedOverrides))
            {
                return;
            }

            var managedCustomIconService = PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService;
            var gameIdText = gameId.ToString("D");

            for (var i = 0; i < achievements.Count; i++)
            {
                var achievement = achievements[i];
                var apiName = NormalizeText(achievement?.ApiName);
                if (achievement == null || string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var unlockedOverride = AchievementIconOverrideHelper.GetOverrideValue(unlockedOverrides, apiName);
                if (!string.IsNullOrWhiteSpace(unlockedOverride))
                {
                    achievement.UnlockedIconPath = ResolveIconOverridePath(
                        unlockedOverride,
                        gameIdText,
                        managedCustomIconService);
                }

                var lockedOverride = AchievementIconOverrideHelper.GetOverrideValue(lockedOverrides, apiName);
                if (!string.IsNullOrWhiteSpace(lockedOverride))
                {
                    achievement.LockedIconPath = ResolveIconOverridePath(
                        lockedOverride,
                        gameIdText,
                        managedCustomIconService);
                }
            }
        }

        private static string ResolveIconOverridePath(
            string path,
            string gameIdText,
            ManagedCustomIconService managedCustomIconService)
        {
            var normalized = NormalizeText(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return managedCustomIconService?.ResolveManagedDisplayPath(normalized, gameIdText) ?? normalized;
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
