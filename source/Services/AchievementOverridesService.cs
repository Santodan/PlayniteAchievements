using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services
{
    public sealed class AchievementOverridesService
    {
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly ICacheManager _cacheService;
        private readonly ILogger _logger;
        private readonly Action<bool> _notifyCacheInvalidated;

        public AchievementOverridesService(
            GameCustomDataStore gameCustomDataStore,
            ICacheManager cacheService,
            ILogger logger,
            Action<bool> notifyCacheInvalidated)
        {
            _gameCustomDataStore = gameCustomDataStore ?? throw new ArgumentNullException(nameof(gameCustomDataStore));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            _logger = logger;
            _notifyCacheInvalidated = notifyCacheInvalidated ?? throw new ArgumentNullException(nameof(notifyCacheInvalidated));
        }

        public CacheWriteResult SetCapstone(Guid playniteGameId, string capstoneApiName)
        {
            if (playniteGameId == Guid.Empty)
            {
                return CacheWriteResult.CreateFailure(
                    string.Empty,
                    "invalid_game_id",
                    ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed") ?? "Refresh failed");
            }

            try
            {
                _gameCustomDataStore.Update(playniteGameId, customData =>
                {
                    customData.ManualCapstoneApiName = capstoneApiName;
                });

                _notifyCacheInvalidated(true);

                return CacheWriteResult.CreateSuccess(playniteGameId.ToString(), DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed setting capstone for gameId={playniteGameId}.");
                return CacheWriteResult.CreateFailure(
                    playniteGameId.ToString(),
                    "settings_save_failed",
                    ex.Message,
                    ex);
            }
        }

        public void SetAchievementOrderOverride(Guid gameId, IReadOnlyList<string> orderedApiNames)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.AchievementOrder = orderedApiNames != null
                    ? new List<string>(orderedApiNames)
                    : null;
            });
            _notifyCacheInvalidated(true);
        }

        public void SetAchievementCategoryOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryOverrides)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.AchievementCategoryOverrides = CopyStringOverrides(categoryOverrides);
            });
            _notifyCacheInvalidated(true);
        }

        public void SetAchievementCategoryTypeOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.AchievementCategoryTypeOverrides = CopyStringOverrides(categoryTypeOverrides);
            });
            _notifyCacheInvalidated(true);
        }

        public void SetAchievementIconOverrides(
            Guid gameId,
            IReadOnlyDictionary<string, string> unlockedIconOverrides,
            IReadOnlyDictionary<string, string> lockedIconOverrides)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.AchievementUnlockedIconOverrides = CopyStringOverrides(unlockedIconOverrides);
                customData.AchievementLockedIconOverrides = CopyStringOverrides(lockedIconOverrides);
            });

            _notifyCacheInvalidated(true);
        }

        public void SetSeparateLockedIconOverride(Guid gameId, bool enabled)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.UseSeparateLockedIconsOverride = enabled ? true : (bool?)null;
            });
            _notifyCacheInvalidated(true);
        }

        public void SetExcludedByUser(Guid playniteGameId, bool excluded, bool clearCachedDataWhenExcluding)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            SetRefreshExclusion(playniteGameId, excluded);
            if (excluded && clearCachedDataWhenExcluding)
            {
                ClearGameData(playniteGameId, clearIconCache: false, persistAfter: false);
            }

            _notifyCacheInvalidated(true);
        }

        public void SetExcludedFromHiddenState(Guid playniteGameId, bool hidden)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            SetRefreshExclusion(playniteGameId, hidden);
            if (hidden)
            {
                ClearGameData(playniteGameId, clearIconCache: false, persistAfter: false);
            }

            _notifyCacheInvalidated(true);
        }

        public void SetExcludedFromSummaries(Guid playniteGameId, bool excluded)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(playniteGameId, customData =>
            {
                customData.ExcludedFromSummaries = excluded ? true : (bool?)null;
            });

            _notifyCacheInvalidated(true);
        }

        public void ClearGameData(Guid playniteGameId, string gameName = null, bool clearIconCache = true, bool persistAfter = true)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            RemoveManualTrackingLink(playniteGameId, gameName);
            if (clearIconCache)
            {
                _cacheService.RemoveGameCache(playniteGameId);
            }
            else
            {
                _cacheService.RemoveGameData(playniteGameId);
            }
        }

        private bool RemoveManualTrackingLink(Guid playniteGameId, string gameName)
        {
            if (!_gameCustomDataStore.TryLoad(playniteGameId, out var customData) ||
                customData.ManualLink == null)
            {
                return false;
            }

            _gameCustomDataStore.Update(playniteGameId, data =>
            {
                data.ManualLink = null;
            });

            if (string.IsNullOrWhiteSpace(gameName))
            {
                _logger?.Info($"Unlinked manual achievements for gameId={playniteGameId}");
            }
            else
            {
                _logger?.Info($"Unlinked manual achievements for '{gameName}'");
            }

            return true;
        }

        private void SetRefreshExclusion(Guid playniteGameId, bool excluded)
        {
            _gameCustomDataStore.Update(playniteGameId, customData =>
            {
                customData.ExcludedFromRefreshes = excluded ? true : (bool?)null;
            });
        }

        private static Dictionary<string, string> CopyStringOverrides(IReadOnlyDictionary<string, string> values)
        {
            if (values == null)
            {
                return null;
            }

            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                copy[pair.Key] = pair.Value;
            }

            return copy;
        }
    }
}
