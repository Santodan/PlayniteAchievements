using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// Centralized read-side service for cached achievement data and hydration overlays.
    /// </summary>
    public sealed class AchievementDataService
    {
        private readonly ICacheManager _cacheService;
        private readonly ICacheReadOptimizations _cacheReadOptimizations;
        private readonly GameDataHydrator _hydrator;
        private readonly ILogger _logger;

        public AchievementDataService(
            ICacheManager cacheService,
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _logger = logger;
            _cacheReadOptimizations = cacheService as ICacheReadOptimizations;
            _hydrator = new GameDataHydrator(api, settings.Persisted);
        }

        public GameAchievementData GetGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return GetMergedGameAchievementData(playniteGameId, includeAchievementOverlays: true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetRawGameAchievementData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                return _cacheService.LoadGameData(playniteGameId.ToString());
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GetGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetGameAchievementDataForSidebar(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                return GetMergedGameAchievementData(playniteGameId.ToString(), includeAchievementOverlays: false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get sidebar achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public List<GameAchievementData> GetAllGameAchievementData()
        {
            try
            {
                var result = LoadAllCachedGameData();
                HydrateAll(result, includeAchievementOverlays: true);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all achievement data");
                return new List<GameAchievementData>();
            }
        }

        public List<GameAchievementData> GetAllGameAchievementDataForSidebar()
        {
            try
            {
                var result = LoadAllCachedGameData();
                HydrateAll(result, includeAchievementOverlays: false);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all sidebar achievement data");
                return new List<GameAchievementData>();
            }
        }

        internal CachedSummaryData GetCachedSummaryData(int recentAchievementDetailLimit = 0)
        {
            try
            {
                return _cacheReadOptimizations?.LoadCachedSummaryDataFast(recentAchievementDetailLimit);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get cached summary data");
                return null;
            }
        }

        private List<GameAchievementData> LoadAllCachedGameData()
        {
            if (_cacheReadOptimizations != null)
            {
                return _cacheReadOptimizations.LoadAllGameDataFast() ?? new List<GameAchievementData>();
            }

            var gameIds = _cacheService.GetCachedGameIds();
            var result = new List<GameAchievementData>();
            foreach (var gameId in gameIds)
            {
                var gameData = _cacheService.LoadGameData(gameId);
                if (gameData != null)
                {
                    result.Add(gameData);
                }
            }

            return result;
        }

        private GameAchievementData GetMergedGameAchievementData(
            string playniteGameId,
            bool includeAchievementOverlays)
        {
            var data = _cacheService.LoadGameData(playniteGameId);
            if (includeAchievementOverlays)
            {
                _hydrator.Hydrate(data);
            }
            else
            {
                _hydrator.HydrateForSidebar(data);
            }

            return data;
        }

        private void HydrateAll(IEnumerable<GameAchievementData> games, bool includeAchievementOverlays)
        {
            if (includeAchievementOverlays)
            {
                _hydrator.HydrateAll(games);
            }
            else
            {
                _hydrator.HydrateAllForSidebar(games);
            }
        }
    }
}
