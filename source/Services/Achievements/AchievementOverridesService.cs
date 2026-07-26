using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Achievements
{
    public sealed class AchievementOverridesService
    {
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly ICacheManager _cacheService;
        private readonly ILogger _logger;

        public AchievementOverridesService(
            GameCustomDataStore gameCustomDataStore,
            ICacheManager cacheService,
            ILogger logger)
        {
            _gameCustomDataStore = gameCustomDataStore ?? throw new ArgumentNullException(nameof(gameCustomDataStore));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            _logger = logger;
        }

        public CacheWriteResult SetCapstone(Guid playniteGameId, string capstoneApiName)
        {
            if (playniteGameId == Guid.Empty)
            {
                return CacheWriteResult.CreateFailure(
                    string.Empty,
                    "invalid_game_id",
                    ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"));
            }

            try
            {
                _gameCustomDataStore.Update(playniteGameId, customData =>
                {
                    customData.ManualCapstoneApiName = capstoneApiName;
                });

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

        public Task<CacheWriteResult> SetCapstoneAsync(Guid playniteGameId, string capstoneApiName)
        {
            return Task.Run(() => SetCapstone(playniteGameId, capstoneApiName));
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
        }

        public void SetAchievementCategoryOverrides(
            Guid gameId,
            IReadOnlyDictionary<string, string> categoryOverrides,
            IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.AchievementCategoryOverrides = CopyStringOverrides(categoryOverrides);
                customData.AchievementCategoryTypeOverrides = CopyStringOverrides(categoryTypeOverrides);
            });
        }

        public void SetAchievementCategoryMetadata(
            Guid gameId,
            IReadOnlyList<string> categoryOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
            GameSummaryCategoryData gameSummaryCategory)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.AchievementCategoryOrder = CopyCategoryOrder(categoryOrder);
                customData.AchievementCategoryImageOverrides = CopyCategoryImageOverrides(categoryImageOverrides);
                customData.GameSummaryCategory = GameCustomDataNormalizer.NormalizeGameSummaryCategory(gameSummaryCategory);
            });
        }

        public void SetAchievementFilters(
            Guid gameId,
            IEnumerable<string> filteredAchievementApiNames,
            IEnumerable<string> summaryFilteredAchievementApiNames)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.FilteredAchievementApiNames = CopyApiNames(filteredAchievementApiNames);
                customData.SummaryFilteredAchievementApiNames = CopyApiNames(summaryFilteredAchievementApiNames);
            });
        }

        public void SetAchievementNote(Guid gameId, string achievementApiName, string note)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            var apiName = AchievementNoteHelper.NormalizeApiName(achievementApiName);
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return;
            }

            var normalizedNote = AchievementNoteHelper.NormalizeNote(note);
            _gameCustomDataStore.Update(gameId, customData =>
            {
                var notes = customData.AchievementNotes != null
                    ? new Dictionary<string, string>(customData.AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(normalizedNote))
                {
                    notes.Remove(apiName);
                }
                else
                {
                    notes[apiName] = normalizedNote;
                }

                customData.AchievementNotes = notes.Count > 0 ? notes : null;
            });
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
        }

        public void SetProviderOverride(Guid gameId, ProviderOverrideData providerOverride)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.ProviderOverride = providerOverride?.Clone();
            });
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
        }

        public bool IsExcludedFromSummaries(Guid playniteGameId) =>
            GameCustomDataLookup.IsExcludedFromSummaries(playniteGameId, null, _gameCustomDataStore);

        public bool IsExcludedFromRefreshes(Guid playniteGameId) =>
            GameCustomDataLookup.IsExcludedFromRefreshes(playniteGameId, null, _gameCustomDataStore);

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
            var removedFromStore = false;
            if (_gameCustomDataStore.TryLoad(playniteGameId, out var customData) &&
                customData?.ManualLink != null)
            {
                _gameCustomDataStore.Update(playniteGameId, data =>
                {
                    data.ManualLink = null;
                });

                removedFromStore = true;
            }

            var removedFromSettings = false;
            var manualSettings = ProviderRegistry.Settings<ManualSettings>();
            if (manualSettings?.AchievementLinks != null &&
                manualSettings.AchievementLinks.Remove(playniteGameId))
            {
                removedFromSettings = true;
                ProviderRegistry.Write(manualSettings);
            }

            if (!removedFromStore && !removedFromSettings)
            {
                return false;
            }

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

        private static List<string> CopyApiNames(IEnumerable<string> values)
        {
            if (values == null)
            {
                return null;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                var normalized = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                result.Add(normalized);
            }

            return result.Count > 0 ? result : null;
        }

        private static List<string> CopyCategoryOrder(IEnumerable<string> values)
        {
            if (values == null)
            {
                return null;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(value);
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                result.Add(normalized);
            }

            return result.Count > 0 ? result : null;
        }

        private static Dictionary<string, CategoryImageOverrideData> CopyCategoryImageOverrides(
            IReadOnlyDictionary<string, CategoryImageOverrideData> values)
        {
            if (values == null)
            {
                return null;
            }

            var copy = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                var key = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                var art = NormalizeText(pair.Value?.Art);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(art))
                {
                    continue;
                }

                copy[key] = new CategoryImageOverrideData
                {
                    Art = art
                };
            }

            return copy.Count > 0 ? copy : null;
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
