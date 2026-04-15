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
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action<bool> _persistSettings;
        private readonly Action<IReadOnlyList<Guid>> _raiseGameDataChanged;
        private readonly Action<bool> _notifyCacheInvalidated;

        public AchievementOverridesService(
            GameCustomDataStore gameCustomDataStore,
            ICacheManager cacheService,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            Action<bool> persistSettings,
            Action<bool> notifyCacheInvalidated,
            Action<IReadOnlyList<Guid>> raiseGameDataChanged = null)
        {
            _gameCustomDataStore = gameCustomDataStore ?? throw new ArgumentNullException(nameof(gameCustomDataStore));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _persistSettings = persistSettings ?? throw new ArgumentNullException(nameof(persistSettings));
            _notifyCacheInvalidated = notifyCacheInvalidated ?? throw new ArgumentNullException(nameof(notifyCacheInvalidated));
            _raiseGameDataChanged = raiseGameDataChanged;
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

        public string GetPreferredProviderOverride(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            if (_settings?.Persisted?.PreferredProviderOverrides == null ||
                !_settings.Persisted.PreferredProviderOverrides.TryGetValue(playniteGameId, out var providerKey))
            {
                return null;
            }

            providerKey = providerKey?.Trim();
            return string.IsNullOrWhiteSpace(providerKey) ? null : providerKey;
        }

        public bool HasPreferredProviderOverride(Guid playniteGameId)
        {
            return !string.IsNullOrWhiteSpace(GetPreferredProviderOverride(playniteGameId));
        }

        public CacheWriteResult SetPreferredProviderOverride(Guid playniteGameId, string providerKey)
        {
            if (playniteGameId == Guid.Empty)
            {
                return CacheWriteResult.CreateFailure(
                    string.Empty,
                    "invalid_game_id",
                    ResourceProvider.GetString("LOCPlayAch_Capstone_Error_InvalidGame"));
            }

            try
            {
                if (_settings.Persisted.PreferredProviderOverrides == null)
                {
                    _settings.Persisted.PreferredProviderOverrides = new Dictionary<Guid, string>();
                }

                providerKey = providerKey?.Trim();
                if (string.IsNullOrWhiteSpace(providerKey))
                {
                    _settings.Persisted.PreferredProviderOverrides.Remove(playniteGameId);
                }
                else
                {
                    _settings.Persisted.PreferredProviderOverrides[playniteGameId] = providerKey;
                }

                _persistSettings(true);
                _notifyCacheInvalidated(true);
                _raiseGameDataChanged?.Invoke(new List<Guid> { playniteGameId });

                return CacheWriteResult.CreateSuccess(playniteGameId.ToString(), DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed setting preferred provider for gameId={playniteGameId}.");
                return CacheWriteResult.CreateFailure(
                    playniteGameId.ToString(),
                    "settings_save_failed",
                    ex.Message,
                    ex);
            }
        }

        public CacheWriteResult ClearPreferredProviderOverride(Guid playniteGameId)
        {
            return SetPreferredProviderOverride(playniteGameId, null);
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
