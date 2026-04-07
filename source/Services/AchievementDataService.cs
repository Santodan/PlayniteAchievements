using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.Linq;

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
        private readonly PersistedSettings _persistedSettings;

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
            _persistedSettings = settings.Persisted;
            _hydrator = new GameDataHydrator(api, _persistedSettings);
        }

        public GameAchievementData GetGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                var data = LoadGameDataWithPreferredProvider(playniteGameId);
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
                return LoadGameDataWithPreferredProvider(playniteGameId.ToString());
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
                    result = optimizedCacheManager.LoadAllGameDataFast(GetPreferredProviderOverridesByCacheKey()) ?? new List<GameAchievementData>();
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

        private GameAchievementData LoadGameDataWithPreferredProvider(string playniteGameId)
        {
            var preferredProviderKey = GetPreferredProviderKey(playniteGameId);
            if (!string.IsNullOrWhiteSpace(preferredProviderKey) && _cacheService is CacheManager optimizedCacheManager)
            {
                return optimizedCacheManager.LoadGameData(playniteGameId, preferredProviderKey);
            }

            return _cacheService.LoadGameData(playniteGameId);
        }

        private string GetPreferredProviderKey(string playniteGameId)
        {
            if (_persistedSettings?.PreferredProviderOverrides == null ||
                string.IsNullOrWhiteSpace(playniteGameId) ||
                !Guid.TryParse(playniteGameId, out var parsedId) ||
                !_persistedSettings.PreferredProviderOverrides.TryGetValue(parsedId, out var providerKey))
            {
                return null;
            }

            providerKey = providerKey?.Trim();
            return string.IsNullOrWhiteSpace(providerKey) ? null : providerKey;
        }

        private IReadOnlyDictionary<string, string> GetPreferredProviderOverridesByCacheKey()
        {
            if (_persistedSettings?.PreferredProviderOverrides == null ||
                _persistedSettings.PreferredProviderOverrides.Count == 0)
            {
                return null;
            }

            return _persistedSettings.PreferredProviderOverrides
                .Where(pair => pair.Key != Guid.Empty && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => pair.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase);
        }
    }
}
