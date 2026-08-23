using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
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
                // A capstone's only summary-visible effect is GameAchievementData.IsCompleted, so
                // the summary and projection rebuild is only warranted when completion actually
                // flips. Setting one on a still-locked achievement cannot flip it.
                var affectsSummaryData = CapstoneChangeFlipsCompletion(playniteGameId, capstoneApiName);

                _gameCustomDataStore.Update(
                    playniteGameId,
                    customData =>
                    {
                        customData.ManualCapstoneApiName = capstoneApiName;
                    },
                    affectsSummaryData);

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

        /// <summary>
        /// Whether changing the manual capstone changes the game's completion state, which is the
        /// only thing about a capstone any summary or rollup can see. Fails safe: anything it
        /// cannot determine is reported as a change, so summaries are never left stale.
        /// </summary>
        private bool CapstoneChangeFlipsCompletion(Guid playniteGameId, string nextCapstoneApiName)
        {
            try
            {
                var achievements = _cacheService?.LoadGameData(playniteGameId.ToString())?.Achievements;
                if (achievements == null || achievements.Count == 0)
                {
                    return true;
                }

                // A fully unlocked game counts as complete whatever the capstone is.
                if (achievements.All(a => a?.Unlocked == true))
                {
                    return false;
                }

                var previousCapstone = _gameCustomDataStore.TryLoad(playniteGameId, out var customData)
                    ? customData?.ManualCapstoneApiName
                    : null;

                return IsCapstoneUnlocked(achievements, previousCapstone) !=
                       IsCapstoneUnlocked(achievements, nextCapstoneApiName);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed evaluating capstone completion impact for gameId={playniteGameId}.");
                return true;
            }
        }

        /// <summary>
        /// Mirrors hydration: a manual capstone replaces the provider's own capstone flags
        /// outright, so with one set only that achievement counts.
        /// </summary>
        private static bool IsCapstoneUnlocked(
            IReadOnlyList<AchievementDetail> achievements,
            string manualCapstoneApiName)
        {
            if (string.IsNullOrWhiteSpace(manualCapstoneApiName))
            {
                return achievements.Any(a => a?.IsCapstone == true && a.Unlocked);
            }

            var trimmed = manualCapstoneApiName.Trim();
            return achievements.Any(a =>
                a != null &&
                a.Unlocked &&
                string.Equals((a.ApiName ?? string.Empty).Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
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

        /// <summary>
        /// Replaces the game's goal achievements. List position carries the goal order.
        /// </summary>
        public void SetGoalAchievements(Guid gameId, IReadOnlyList<string> orderedApiNames)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            var normalized = AchievementOrderHelper.NormalizeApiNames(orderedApiNames);
            // Goals only pin existing achievements to the top of a list, so no count, filter or
            // library rollup can move; summary and projection subscribers skip this.
            _gameCustomDataStore.Update(
                gameId,
                customData =>
                {
                    customData.GoalAchievementApiNames = normalized.Count > 0 ? normalized : null;
                },
                affectsSummaryData: false);
        }

        /// <summary>
        /// Adds or removes a single goal. New goals append, so the goal added first stays on top.
        /// Returns the achievement's resulting position in the goal list, or -1 when it is not a
        /// goal, which saves the caller a second read to find out.
        /// </summary>
        /// <remarks>
        /// The list is read and rewritten inside one store mutation. Reading it up front via
        /// <see cref="GameCustomDataLookup"/> would clone the game's entire custom data, notes and
        /// icon overrides included, just to see one list.
        /// </remarks>
        public int SetAchievementGoal(Guid gameId, string achievementApiName, bool isGoal)
        {
            if (gameId == Guid.Empty)
            {
                return -1;
            }

            var apiName = (achievementApiName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return -1;
            }

            var resultIndex = -1;
            _gameCustomDataStore.Update(
                gameId,
                customData =>
                {
                    var goals = AchievementOrderHelper.NormalizeApiNames(customData.GoalAchievementApiNames);
                    var existingIndex = goals.FindIndex(entry =>
                        string.Equals(entry, apiName, StringComparison.OrdinalIgnoreCase));

                    if (isGoal)
                    {
                        if (existingIndex < 0)
                        {
                            goals.Add(apiName);
                            existingIndex = goals.Count - 1;
                        }

                        resultIndex = existingIndex;
                    }
                    else if (existingIndex >= 0)
                    {
                        goals.RemoveAt(existingIndex);
                    }

                    customData.GoalAchievementApiNames = goals.Count > 0 ? goals : null;
                },
                affectsSummaryData: false);

            return resultIndex;
        }

        /// <summary>
        /// Drops goals that have since been unlocked. Display already treats an unlocked goal as
        /// no longer a goal, so this only keeps the stored list tidy.
        /// </summary>
        public bool PruneUnlockedGoals(Guid gameId, IEnumerable<string> unlockedApiNames)
        {
            if (gameId == Guid.Empty || unlockedApiNames == null)
            {
                return false;
            }

            var unlocked = new HashSet<string>(
                unlockedApiNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (unlocked.Count == 0)
            {
                return false;
            }

            // Cheap pre-check against the cached file. Update always saves and raises, so an
            // unlock that clears nothing, by far the common case, must not reach it.
            if (!_gameCustomDataStore.TryLoad(gameId, out var existing) ||
                existing?.GoalAchievementApiNames == null ||
                !existing.GoalAchievementApiNames.Any(entry =>
                    !string.IsNullOrWhiteSpace(entry) && unlocked.Contains(entry.Trim())))
            {
                return false;
            }

            var pruned = false;
            _gameCustomDataStore.Update(
                gameId,
                customData =>
                {
                    var goals = AchievementOrderHelper.NormalizeApiNames(customData.GoalAchievementApiNames);
                    var remaining = goals.Where(entry => !unlocked.Contains(entry)).ToList();
                    if (remaining.Count == goals.Count)
                    {
                        return;
                    }

                    customData.GoalAchievementApiNames = remaining.Count > 0 ? remaining : null;
                    pruned = true;
                },
                affectsSummaryData: false);

            return pruned;
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

            // A note is annotation only: it cannot change counts, filters or library rollups.
            var apiName = AchievementNoteHelper.NormalizeApiName(achievementApiName);
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return;
            }

            var normalizedNote = AchievementNoteHelper.NormalizeNote(note);
            _gameCustomDataStore.Update(
                gameId,
                customData =>
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
                },
                affectsSummaryData: false);
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

        public void SetExophaseEnrichmentSlugOverride(Guid gameId, string slug)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.ExophaseEnrichmentSlugOverride = string.IsNullOrWhiteSpace(slug) ? null : slug.Trim();
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
