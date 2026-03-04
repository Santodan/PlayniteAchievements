using System;
using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

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
        private readonly AchievementDetailHydrator _achievementHydrator;

        public GameDataHydrator(IPlayniteAPI api, PersistedSettings settings)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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

            // Populate ExcludedByUser from settings
            data.ExcludedByUser = _settings.ExcludedGameIds.Contains(gameId);

            // Set Game reference from Playnite database (SortingName is computed from this)
            data.Game = GetGame(gameId);

            // Populate runtime custom order from settings.
            data.AchievementOrder = null;
            if (_settings.AchievementOrderOverrides != null &&
                _settings.AchievementOrderOverrides.TryGetValue(gameId, out var configuredOrder))
            {
                var normalizedOrder = Services.AchievementOrderHelper.NormalizeApiNames(configuredOrder);
                if (normalizedOrder.Count > 0)
                {
                    data.AchievementOrder = normalizedOrder;
                }
            }

            // Hydrate achievements with settings overlays (capstone + category/category-type overrides).
            if (data.Achievements != null && data.Achievements.Count > 0)
            {
                _achievementHydrator.HydrateAllWithCapstoneOverride(data.Achievements, gameId);
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
    }
}
