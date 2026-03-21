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
                var data = _cacheService.LoadGameData(playniteGameId);
                _hydrator.Hydrate(data);
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

        public List<GameAchievementData> GetAllGameAchievementData()
        {
            try
            {
                List<GameAchievementData> result;
                if (_cacheService is CacheManager optimizedCacheManager)
                {
                    result = optimizedCacheManager.LoadAllGameDataFast() ?? new List<GameAchievementData>();
                }
                else
                {
                    var gameIds = _cacheService.GetCachedGameIds();
                    result = new List<GameAchievementData>();
                    foreach (var gameId in gameIds)
                    {
                        var gameData = _cacheService.LoadGameData(gameId);
                        if (gameData != null)
                        {
                            result.Add(gameData);
                        }
                    }
                }

                _hydrator.HydrateAll(result);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all achievement data");
                return new List<GameAchievementData>();
            }
        }
    }
}
