using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services
{
    public sealed class AchievementOverridesService
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ICacheManager _cacheService;
        private readonly ILogger _logger;
        private readonly Action<bool> _persistSettings;
        private readonly Action<bool> _notifyCacheInvalidated;
        private readonly Action<List<Guid>> _raiseGameDataChanged;

        public AchievementOverridesService(
            PlayniteAchievementsSettings settings,
            ICacheManager cacheService,
            ILogger logger,
            Action<bool> persistSettings,
            Action<bool> notifyCacheInvalidated,
            Action<List<Guid>> raiseGameDataChanged)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
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
                    ResourceProvider.GetString("LOCPlayAch_Capstone_Error_InvalidGame"));
            }

            try
            {
                if (string.IsNullOrWhiteSpace(capstoneApiName))
                {
                    _settings.Persisted.ManualCapstones.Remove(playniteGameId);
                }
                else
                {
                    _settings.Persisted.ManualCapstones[playniteGameId] = capstoneApiName.Trim();
                }

                _persistSettings(true);
                _notifyCacheInvalidated(true);
                _raiseGameDataChanged?.Invoke(new List<Guid> { playniteGameId });

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
            if (gameId == Guid.Empty || _settings?.Persisted == null)
            {
                return;
            }

            var normalizedOrder = AchievementOrderHelper.NormalizeApiNames(orderedApiNames);
            var updated = _settings.Persisted.AchievementOrderOverrides != null
                ? _settings.Persisted.AchievementOrderOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null ? new List<string>(kvp.Value) : new List<string>())
                : new Dictionary<Guid, List<string>>();

            if (normalizedOrder.Count == 0)
            {
                updated.Remove(gameId);
            }
            else
            {
                updated[gameId] = normalizedOrder;
            }

            _settings.Persisted.AchievementOrderOverrides = updated;
            _persistSettings(true);
            _notifyCacheInvalidated(true);
        }

        public void SetAchievementCategoryOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryOverrides)
        {
            if (gameId == Guid.Empty || _settings?.Persisted == null)
            {
                return;
            }

            var normalizedCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (categoryOverrides != null)
            {
                foreach (var pair in categoryOverrides)
                {
                    var apiName = (pair.Key ?? string.Empty).Trim();
                    var category = (pair.Value ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                    {
                        continue;
                    }

                    normalizedCategories[apiName] = category;
                }
            }

            var updated = _settings.Persisted.AchievementCategoryOverrides != null
                ? _settings.Persisted.AchievementCategoryOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null
                        ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                : new Dictionary<Guid, Dictionary<string, string>>();

            if (normalizedCategories.Count == 0)
            {
                updated.Remove(gameId);
            }
            else
            {
                updated[gameId] = normalizedCategories;
            }

            _settings.Persisted.AchievementCategoryOverrides = updated;
            _persistSettings(true);
            _notifyCacheInvalidated(true);
        }

        public void SetAchievementCategoryTypeOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            if (gameId == Guid.Empty || _settings?.Persisted == null)
            {
                return;
            }

            var normalizedCategoryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (categoryTypeOverrides != null)
            {
                foreach (var pair in categoryTypeOverrides)
                {
                    var apiName = (pair.Key ?? string.Empty).Trim();
                    var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                    if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(categoryType))
                    {
                        continue;
                    }

                    normalizedCategoryTypes[apiName] = categoryType;
                }
            }

            var updated = _settings.Persisted.AchievementCategoryTypeOverrides != null
                ? _settings.Persisted.AchievementCategoryTypeOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null
                        ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                : new Dictionary<Guid, Dictionary<string, string>>();

            if (normalizedCategoryTypes.Count == 0)
            {
                updated.Remove(gameId);
            }
            else
            {
                updated[gameId] = normalizedCategoryTypes;
            }

            _settings.Persisted.AchievementCategoryTypeOverrides = updated;
            _persistSettings(true);
            _notifyCacheInvalidated(true);
        }

        public void SetExcludedByUser(Guid playniteGameId, bool excluded, bool clearCachedDataWhenExcluding)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            if (excluded)
            {
                _settings.Persisted.ExcludedGameIds.Add(playniteGameId);
                if (clearCachedDataWhenExcluding)
                {
                    ClearGameData(playniteGameId, clearIconCache: false, persistAfter: false);
                }
            }
            else
            {
                _settings.Persisted.ExcludedGameIds.Remove(playniteGameId);
            }

            _persistSettings(true);
            _notifyCacheInvalidated(true);
            _raiseGameDataChanged?.Invoke(new List<Guid> { playniteGameId });
        }

        public void SetExcludedFromHiddenState(Guid playniteGameId, bool hidden)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            if (hidden)
            {
                _settings.Persisted.ExcludedGameIds.Add(playniteGameId);
                ClearGameData(playniteGameId, clearIconCache: false, persistAfter: false);
            }
            else
            {
                _settings.Persisted.ExcludedGameIds.Remove(playniteGameId);
            }

            _persistSettings(true);
            _notifyCacheInvalidated(true);
            _raiseGameDataChanged?.Invoke(new List<Guid> { playniteGameId });
        }

        public void SetExcludedFromSummaries(Guid playniteGameId, bool excluded)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            if (excluded)
            {
                _settings.Persisted.ExcludedFromSummariesGameIds.Add(playniteGameId);
            }
            else
            {
                _settings.Persisted.ExcludedFromSummariesGameIds.Remove(playniteGameId);
            }

            _persistSettings(true);
            _notifyCacheInvalidated(true);
            _raiseGameDataChanged?.Invoke(new List<Guid> { playniteGameId });
        }

        public void ClearGameData(Guid playniteGameId, string gameName = null, bool clearIconCache = true, bool persistAfter = true)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            var removedManualLink = RemoveManualTrackingLink(playniteGameId, gameName);
            if (clearIconCache)
            {
                _cacheService.RemoveGameCache(playniteGameId);
            }
            else
            {
                _cacheService.RemoveGameData(playniteGameId);
            }

            if (removedManualLink && persistAfter)
            {
                _persistSettings(true);
            }
        }

        private bool RemoveManualTrackingLink(Guid playniteGameId, string gameName)
        {
            var manualSettings = ProviderRegistry.Settings<ManualSettings>();
            if (manualSettings?.AchievementLinks == null ||
                !manualSettings.AchievementLinks.Remove(playniteGameId))
            {
                return false;
            }

            ProviderRegistry.Write(manualSettings);

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
    }
}
