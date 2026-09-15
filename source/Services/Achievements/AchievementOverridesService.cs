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

        /// <summary>
        /// Sets, moves or clears one of a game's capstones.
        /// </summary>
        /// <remarks>
        /// The first edit materializes the game: the provider's own capstones are written to the
        /// stored set alongside the edit, and from then on the stored set is the whole truth while
        /// provider flags no longer apply. That is what makes a seeded capstone and a nominated one
        /// behave identically afterwards, and it is what the single stored capstone already did
        /// before there could be more than one.
        ///
        /// A capstone claims its own achievement's category, so nominating one displaces whatever
        /// stood for that category before.
        /// </remarks>
        public CacheWriteResult SetCapstone(Guid playniteGameId, string apiName, bool isCapstone)
        {
            if (playniteGameId == Guid.Empty || string.IsNullOrWhiteSpace(apiName))
            {
                return CacheWriteResult.CreateFailure(
                    string.Empty,
                    "invalid_game_id",
                    ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"));
            }

            try
            {
                var achievements = ResolveCapstoneCandidates(playniteGameId);

                // Materializing means capturing the provider's own capstones alongside the edit, so
                // a game whose achievements cannot be read right now must not be materialized: the
                // stored set would be this one entry and every provider capstone would be dropped.
                if ((achievements == null || achievements.Count == 0) &&
                    !(_gameCustomDataStore.TryLoad(playniteGameId, out var existing) &&
                      existing?.CapstonesMaterialized == true))
                {
                    _logger?.Warn(
                        $"Refusing to set a capstone for gameId={playniteGameId}: no achievements are loaded to seed the set from.");
                    return CacheWriteResult.CreateFailure(
                        playniteGameId.ToString(),
                        "no_achievements_loaded",
                        ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed"));
                }

                var next = BuildNextCapstones(
                    playniteGameId,
                    achievements ?? new List<AchievementDetail>(),
                    apiName.Trim(),
                    isCapstone);

                // Summary rows carry the capstone counts the completion badge shows, so the rebuild
                // is warranted whenever the edit moves either that count or completion.
                var affectsSummaryData = CapstoneChangeAffectsSummaries(playniteGameId, achievements, next);

                _gameCustomDataStore.Update(
                    playniteGameId,
                    customData =>
                    {
                        customData.CapstonesMaterialized = true;
                        customData.Capstones = next;
                    },
                    affectsSummaryData,
                    // Capstones are not in the override mirror, which carries only the filtered
                    // ApiNames and the user-editable points and trophy type, so resyncing it here
                    // would clone the record and take the write connection for nothing.
                    affectsOverrideMirror: false);

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
        /// Every achievement that could be a capstone, which means the authored ones too.
        /// </summary>
        /// <remarks>
        /// The cache holds only what a provider supplied; an authored achievement lives in custom
        /// data and is projected onto the list by hydration, which this path does not run. Reading
        /// the cache alone left authored achievements invisible here, so seeding missed the ones a
        /// provider had marked, a category could not be resolved for them, and the pruning below
        /// treated a capstone on one as pointing at an achievement that no longer exists.
        /// </remarks>
        private List<AchievementDetail> ResolveCapstoneCandidates(Guid playniteGameId)
        {
            var achievements = _cacheService?.LoadGameData(playniteGameId.ToString())?.Achievements
                ?? new List<AchievementDetail>();
            var candidates = new List<AchievementDetail>(achievements);

            if (_gameCustomDataStore.TryLoad(playniteGameId, out var customData) &&
                customData?.CustomAchievements != null &&
                customData.CustomAchievements.Count > 0)
            {
                candidates.AddRange(
                    CustomAchievementProjectionService.ProjectAchievements(
                        playniteGameId,
                        customData.CustomAchievements));
            }

            return candidates;
        }

        /// <summary>
        /// The game's capstone set with one edit applied, seeded from the provider when the game
        /// has not been edited before.
        /// </summary>
        private List<CapstoneAssignment> BuildNextCapstones(
            Guid playniteGameId,
            IReadOnlyList<AchievementDetail> achievements,
            string apiName,
            bool isCapstone)
        {
            // One load of the record, not three: resolving it clones the whole graph, including the
            // per-achievement override map, which is the bulk of a heavily customized game.
            var stored = _gameCustomDataStore.TryLoad(playniteGameId, out var customData) ? customData : null;
            var materialized = stored?.CapstonesMaterialized == true;
            var current = materialized
                ? (stored.Capstones ?? new List<CapstoneAssignment>())
                    .Select(assignment => assignment?.Clone())
                    .Where(assignment => assignment != null)
                    .ToList()
                : CapstoneResolver.Materialize(achievements);

            // Categories come from the cache unhydrated, so a user's category override has to be
            // applied here the way hydration applies it. Without this the displacement below reads
            // the provider's category and can free a slot the user never pointed at.
            var categoryOverrides = GameCustomDataFile.CloneAchievementOverrideMap(stored?.AchievementOverrides)
                ?? new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);

            // Indexed once: the displacement check below asks for a category per stored capstone.
            var byApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements)
            {
                var key = (achievement?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && !byApiName.ContainsKey(key))
                {
                    byApiName[key] = achievement;
                }
            }

            var category = ResolveCategory(byApiName, categoryOverrides, apiName);
            var next = new List<CapstoneAssignment>();
            foreach (var assignment in current)
            {
                if (assignment.Matches(apiName))
                {
                    continue;
                }

                // One capstone stands for a category, so nominating one displaces whatever stood
                // for that category before.
                if (isCapstone &&
                    string.Equals(
                        ResolveCategory(byApiName, categoryOverrides, assignment.ApiName),
                        category,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                next.Add(assignment);
            }

            if (isCapstone)
            {
                next.Add(new CapstoneAssignment { ApiName = apiName });
            }

            // Drop entries whose achievement the provider no longer sends, so readers can trust the
            // stored count as the game's capstone total without re-reading its achievements to
            // check. Only when the achievements are actually in hand: an empty list here means a
            // game that could not be loaded, and pruning against it would wipe the set.
            if (byApiName.Count > 0)
            {
                next.RemoveAll(assignment => !byApiName.ContainsKey((assignment.ApiName ?? string.Empty).Trim()));
            }

            return next;
        }

        private static string ResolveCategory(
            IReadOnlyDictionary<string, AchievementDetail> byApiName,
            IReadOnlyDictionary<string, AchievementOverride> categoryOverrides,
            string apiName)
        {
            var trimmed = (apiName ?? string.Empty).Trim();
            if (categoryOverrides != null &&
                categoryOverrides.TryGetValue(trimmed, out var userOverride) &&
                !string.IsNullOrWhiteSpace(userOverride?.Category))
            {
                return AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(userOverride.Category);
            }

            byApiName.TryGetValue(trimmed, out var match);
            return AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                match?.ProviderCategory ?? match?.Category);
        }

        /// <summary>
        /// Whether a capstone edit changes anything a summary or rollup can see. Fails safe:
        /// anything it cannot determine is reported as a change, so summaries are never left stale.
        /// </summary>
        /// <remarks>
        /// Completion is no longer the only visible effect. Summary rows carry how many capstones a
        /// game has and how many are earned, and the completion badge shows that count, so adding
        /// or dropping one matters even on a game whose completion cannot move. A fully unlocked
        /// game used to be able to skip the rebuild for exactly that reason; it cannot now.
        /// </remarks>
        private bool CapstoneChangeAffectsSummaries(
            Guid playniteGameId,
            IReadOnlyList<AchievementDetail> achievements,
            List<CapstoneAssignment> next)
        {
            try
            {
                if (achievements == null || achievements.Count == 0)
                {
                    return true;
                }

                var set = GameCustomDataLookup.GetCapstoneSet(playniteGameId, null, _gameCustomDataStore);
                var before = Summarize(achievements, set.Assignments, set.Materialized);
                var after = Summarize(achievements, next, true);
                return before.Key != after.Key || before.Value != after.Value;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed evaluating capstone summary impact for gameId={playniteGameId}.");
                return true;
            }
        }

        /// <summary>
        /// What a given capstone set would put on the game's summary row: whether it reads as
        /// finished, and how many finishes it stands for. Resolves the set rather than reading the
        /// already stamped flags, because the point is to weigh a set that is not stored yet.
        /// </summary>
        private static KeyValuePair<bool, int> Summarize(
            IReadOnlyList<AchievementDetail> achievements,
            IEnumerable<CapstoneAssignment> assignments,
            bool materialized)
        {
            var resolver = CapstoneResolver.Resolve(achievements, assignments, materialized);
            var allUnlocked = achievements.All(a => a?.Unlocked == true);
            if (resolver.Count == 0)
            {
                return new KeyValuePair<bool, int>(allUnlocked, allUnlocked ? 1 : 0);
            }

            var unlocked = resolver.EffectiveApiNames.Count(apiName =>
                achievements.Any(a =>
                    a != null &&
                    a.Unlocked &&
                    string.Equals((a.ApiName ?? string.Empty).Trim(), apiName, StringComparison.OrdinalIgnoreCase)));

            return new KeyValuePair<bool, int>(
                allUnlocked || unlocked >= resolver.Count,
                unlocked);
        }

        public Task<CacheWriteResult> SetCapstoneAsync(Guid playniteGameId, string apiName, bool isCapstone)
        {
            return Task.Run(() => SetCapstone(playniteGameId, apiName, isCapstone));
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

        // Membership: which achievement carries which category label and type. It moves nothing a
        // library rollup reads - the game's counts, filters and summary art are all unchanged by
        // it - so these writes are scoped out of the library-wide summary and projection passes.
        // Callers repaint their own rows in place, which is what keeps the edit visible.
        public void SetAchievementCategoryOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryOverrides)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(
                gameId,
                customData =>
                {
                    ReplaceOverrideField(customData, categoryOverrides, (entry, value) => entry.Category = value);
                },
                affectsSummaryData: false);
        }

        public void SetAchievementCategoryTypeOverrides(Guid gameId, IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(
                gameId,
                customData =>
                {
                    ReplaceOverrideField(customData, categoryTypeOverrides, (entry, value) => entry.CategoryType = value);
                },
                affectsSummaryData: false);
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

            _gameCustomDataStore.Update(
                gameId,
                customData =>
                {
                    ReplaceOverrideField(customData, categoryOverrides, (entry, value) => entry.Category = value);
                    ReplaceOverrideField(customData, categoryTypeOverrides, (entry, value) => entry.CategoryType = value);
                },
                affectsSummaryData: false);
        }

        /// <summary>
        /// Writes a category rename's two halves - the ApiName-keyed membership and the label-keyed
        /// order, art and summary selection - in one store update.
        /// </summary>
        /// <param name="affectsSummaryData">
        /// False when the write cannot move any library-level rollup. Moving an achievement between
        /// this game's categories does not change what it has unlocked, so the default true would
        /// queue a library-wide overview recompute per click for nothing. Pass true only when the
        /// game-summary category selection actually changed, since that does drive the game's row.
        /// </param>
        public void SetAchievementCategoryAssignmentAndMetadata(
            Guid gameId,
            IReadOnlyDictionary<string, string> categoryOverrides,
            IReadOnlyDictionary<string, string> categoryTypeOverrides,
            IReadOnlyList<string> categoryOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
            GameSummaryCategoryData gameSummaryCategory,
            bool affectsSummaryData = true)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                ReplaceOverrideField(customData, categoryOverrides, (entry, value) => entry.Category = value);
                ReplaceOverrideField(customData, categoryTypeOverrides, (entry, value) => entry.CategoryType = value);
                customData.AchievementCategoryOrder = CopyCategoryOrder(categoryOrder);
                customData.AchievementCategoryImageOverrides = CopyCategoryImageOverrides(categoryImageOverrides);
                customData.GameSummaryCategory = GameCustomDataNormalizer.NormalizeGameSummaryCategory(gameSummaryCategory);
            },
            affectsSummaryData);
        }

        /// <param name="affectsSummaryData">
        /// False when the write leaves the game's summary art where it was. Category order and art
        /// on a category that is not the summary source are per-game display state, so the default
        /// true would queue a library-wide overview pass per edit for nothing.
        /// </param>
        public void SetAchievementCategoryMetadata(
            Guid gameId,
            IReadOnlyList<string> categoryOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
            GameSummaryCategoryData gameSummaryCategory,
            bool affectsSummaryData = true)
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
            },
            affectsSummaryData);
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

        private static Dictionary<string, AchievementOverride> CloneOverrides(GameCustomDataFile customData)
        {
            var clone = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            if (customData.AchievementOverrides == null)
            {
                return clone;
            }

            foreach (var pair in customData.AchievementOverrides)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                {
                    clone[pair.Key] = pair.Value.Clone();
                }
            }

            return clone;
        }

        private static void StoreOverrides(
            GameCustomDataFile customData,
            Dictionary<string, AchievementOverride> overrides)
        {
            var pruned = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in overrides)
            {
                if (pair.Value != null && !pair.Value.IsEmpty)
                {
                    pruned[pair.Key] = pair.Value;
                }
            }

            customData.AchievementOverrides = pruned.Count > 0 ? pruned : null;
        }

        /// <summary>
        /// Applies a change to one achievement's override record, pruning the row when nothing is
        /// left on it. The record is authoritative: normalization republishes the legacy mirror
        /// maps from it, so writing only a mirror map would have the change discarded.
        /// </summary>
        private static void MutateOverride(
            GameCustomDataFile customData,
            string apiName,
            Action<AchievementOverride> apply)
        {
            var overrides = CloneOverrides(customData);
            if (!overrides.TryGetValue(apiName, out var entry) || entry == null)
            {
                entry = new AchievementOverride();
            }

            apply(entry);
            overrides[apiName] = entry;
            StoreOverrides(customData, overrides);
        }

        /// <summary>
        /// Replaces one field across every override record from a whole-map write. The field is
        /// cleared on rows absent from <paramref name="values"/>, matching the replace semantics the
        /// legacy map writes had.
        /// </summary>
        private static void ReplaceOverrideField(
            GameCustomDataFile customData,
            IReadOnlyDictionary<string, string> values,
            Action<AchievementOverride, string> apply)
        {
            var overrides = CloneOverrides(customData);
            foreach (var entry in overrides.Values)
            {
                apply(entry, null);
            }

            if (values != null)
            {
                foreach (var pair in values)
                {
                    var apiName = (pair.Key ?? string.Empty).Trim();
                    var value = (pair.Value ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (!overrides.TryGetValue(apiName, out var entry) || entry == null)
                    {
                        entry = new AchievementOverride();
                        overrides[apiName] = entry;
                    }

                    apply(entry, value);
                }
            }

            StoreOverrides(customData, overrides);
        }

        /// <summary>
        /// Sets or clears one achievement's user-editable provider fields. A null value clears that
        /// override, so the achievement falls back to the provider's own value rather than to blank.
        /// </summary>
        /// <remarks>
        /// Unlock status is absent by design: it stays provider-owned so an edit cannot move
        /// unlocked counts or completion. <paramref name="unlockTimeUtc"/> only corrects the
        /// timestamp of an achievement that is already unlocked. Rarity is absent for provider
        /// achievements; a custom achievement carries its own on its definition.
        /// </remarks>
        public void SetAchievementFieldOverride(
            Guid gameId,
            string achievementApiName,
            AchievementEditableField field,
            object value)
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

            // This flag gates two things: the summary memo, and the Overview's per-game delta. So
            // it has to be true for any field a displayed row shows, not just the ones a summary
            // aggregate sums -- otherwise the edit lands in the store and nothing on screen
            // re-reads it, which is what left a hidden edit visible only inside the editor.
            //
            // Points and trophy type additionally move SQL-resolved aggregates, via the override
            // mirror. The rest are applied in code over the rows, so they need the re-read but not
            // the mirror. Only the unlock-time override changes nothing on a row the user can see:
            // the summary's last-unlock comes from the real recorded time in UserAchievements.
            var affectsSummaryData = field != AchievementEditableField.UnlockTimeUtc;
            var affectsOverrideMirror =
                field == AchievementEditableField.Points ||
                field == AchievementEditableField.TrophyType;

            _gameCustomDataStore.Update(
                gameId,
                customData => MutateOverride(customData, apiName, entry =>
                {
                    switch (field)
                    {
                        case AchievementEditableField.DisplayName:
                            entry.DisplayName = NormalizeText(value as string);
                            break;
                        case AchievementEditableField.Description:
                            entry.Description = NormalizeText(value as string);
                            break;
                        case AchievementEditableField.Points:
                            entry.Points = value as int?;
                            break;
                        case AchievementEditableField.TrophyType:
                            entry.TrophyType = NormalizeText(value as string);
                            break;
                        case AchievementEditableField.UnlockTimeUtc:
                            // A null timestamp is an explicit clear, not "no customization":
                            // leaving the record empty would fall straight back to the provider's
                            // own time, which is what made an unchecked box reappear. The
                            // provider value is still untouched, so Revert restores it.
                            var unlockTime = value as DateTime?;
                            entry.UnlockTimeUtc = unlockTime;
                            entry.ClearUnlockTime = !unlockTime.HasValue;
                            break;
                        case AchievementEditableField.Hidden:
                            // Either value can be a customization, so the caller passes null to
                            // mean "back to whatever the provider says" rather than "not hidden".
                            entry.Hidden = value as bool?;
                            break;
                    }
                }),
                affectsSummaryData,
                affectsOverrideMirror);
        }

        /// <summary>
        /// Drops every user override for the given achievements in one store update, so they show
        /// exactly what the provider supplies again.
        /// </summary>
        /// <remarks>
        /// Deleting the record is what makes revert structural: the provider's own values were
        /// never overwritten, so removing the overlay is all that is needed. Clearing field by
        /// field would not work for the unlock timestamp, where a cleared value is itself a stored
        /// state. Icon paths on the record are dropped with it; the files they point at are
        /// managed elsewhere.
        /// </remarks>
        public void ClearAchievementOverrides(Guid gameId, IEnumerable<string> achievementApiNames)
        {
            if (gameId == Guid.Empty || achievementApiNames == null)
            {
                return;
            }

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in achievementApiNames)
            {
                var apiName = AchievementNoteHelper.NormalizeApiName(name);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    targets.Add(apiName);
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            // A dropped record may have carried a points or trophy-type override, both of which
            // the summary SQL resolves through the mirror, so the rebuild is warranted.
            _gameCustomDataStore.Update(
                gameId,
                customData =>
                {
                    var overrides = CloneOverrides(customData);
                    foreach (var apiName in targets)
                    {
                        overrides.Remove(apiName);
                    }

                    StoreOverrides(customData, overrides);
                },
                affectsSummaryData: true);
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
                customData => MutateOverride(customData, apiName, entry => entry.Note = normalizedNote),
                affectsSummaryData: false);
        }

        /// <summary>
        /// Writes both icon override maps and the custom achievements' own icon paths in one store
        /// update. Custom achievements carry their icons on the definition rather than in the
        /// override maps, and the Icons tab edits both halves at once. Each Update is a load,
        /// normalize, serialize, write and change notification, so writing the halves separately
        /// paid that twice for a single edit.
        /// </summary>
        public void SetIconOverridesAndCustomAchievementIcons(
            Guid gameId,
            IReadOnlyDictionary<string, string> unlockedIconOverrides,
            IReadOnlyDictionary<string, string> lockedIconOverrides,
            IReadOnlyDictionary<string, (string Unlocked, string Locked)> customIconsByApiName)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                ApplyAchievementIconOverrides(customData, unlockedIconOverrides, lockedIconOverrides);
                ApplyCustomAchievementIcons(customData, customIconsByApiName);
            });
        }

        private static void ApplyAchievementIconOverrides(
            GameCustomDataFile customData,
            IReadOnlyDictionary<string, string> unlockedIconOverrides,
            IReadOnlyDictionary<string, string> lockedIconOverrides)
        {
            ReplaceOverrideField(customData, unlockedIconOverrides, (entry, value) => entry.UnlockedIconPath = value);
            ReplaceOverrideField(customData, lockedIconOverrides, (entry, value) => entry.LockedIconPath = value);
        }

        private static void ApplyCustomAchievementIcons(
            GameCustomDataFile customData,
            IReadOnlyDictionary<string, (string Unlocked, string Locked)> iconsByApiName)
        {
            if (iconsByApiName == null ||
                iconsByApiName.Count == 0 ||
                customData.CustomAchievements == null)
            {
                return;
            }

            foreach (var definition in customData.CustomAchievements)
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition?.Id);
                if (definition == null ||
                    string.IsNullOrWhiteSpace(apiName) ||
                    !iconsByApiName.TryGetValue(apiName, out var icons))
                {
                    continue;
                }

                definition.UnlockedIconPath = string.IsNullOrWhiteSpace(icons.Unlocked) ? null : icons.Unlocked.Trim();
                definition.LockedIconPath = string.IsNullOrWhiteSpace(icons.Locked) ? null : icons.Locked.Trim();
            }
        }

        /// <summary>
        /// Replaces the achievements authored by the user and keeps every ApiName-keyed
        /// customization attached when an edited custom ID changes.
        /// </summary>
        public void SetCustomAchievements(
            Guid gameId,
            IReadOnlyList<CustomAchievementDefinition> definitions,
            IReadOnlyDictionary<string, string> renamedApiNames = null)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                if (renamedApiNames != null && renamedApiNames.Count > 0)
                {
                    MigrateCustomAchievementApiNameReferences(customData, renamedApiNames);
                }

                customData.CustomAchievements = definitions != null
                    ? definitions.Select(definition => definition?.Clone()).Where(definition => definition != null).ToList()
                    : null;
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

        /// <summary>
        /// Assigns the custom provider a custom-only game displays as; null or blank clears it.
        /// </summary>
        public void SetCustomProvider(Guid gameId, string customProviderId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            _gameCustomDataStore.Update(gameId, customData =>
            {
                customData.CustomProviderId = string.IsNullOrWhiteSpace(customProviderId)
                    ? null
                    : customProviderId.Trim();
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

        /// <summary>
        /// Replaces the manual link's recorded unlocks for a game in one store update. The map holds
        /// only unlocked achievements, keyed by ApiName, with the unlock time or null when it is
        /// unknown.
        /// </summary>
        /// <remarks>
        /// Written as a whole map rather than one achievement at a time, matching the other
        /// collection-shaped facets, so ticking a whole selection costs one write rather than one per
        /// row. Locked achievements are simply absent: <c>ManualUnlockResolver</c> treats an absent
        /// key and a stored <c>false</c> identically, so storing the false entries would only grow
        /// the blob that every write has to serialize.
        /// <para>
        /// <paramref name="affectsSummaryData"/> is false on purpose. Unlocked counts and completion
        /// are read from the cache, not from this blob, and the caller re-applies the link to the
        /// cache itself, which raises its own invalidation. Marking this as summary-affecting would
        /// run that cascade a second time for identical numbers.
        /// </para>
        /// </remarks>
        public bool SetManualUnlockStates(
            Guid playniteGameId,
            IReadOnlyDictionary<string, DateTime?> unlockedApiNames)
        {
            if (playniteGameId == Guid.Empty)
            {
                return false;
            }

            var states = new Dictionary<string, bool>();
            var times = new Dictionary<string, DateTime?>();
            if (unlockedApiNames != null)
            {
                foreach (var pair in unlockedApiNames)
                {
                    var apiName = (pair.Key ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    states[apiName] = true;
                    if (pair.Value.HasValue)
                    {
                        times[apiName] = pair.Value;
                    }
                }
            }

            var wrote = false;
            _gameCustomDataStore.Update(
                playniteGameId,
                customData =>
                {
                    if (customData.ManualLink == null)
                    {
                        return;
                    }

                    customData.ManualLink.UnlockStates = states;
                    customData.ManualLink.UnlockTimes = times;
                    customData.ManualLink.LastModifiedUtc = DateTime.UtcNow;
                    wrote = true;
                },
                affectsSummaryData: false);

            return wrote;
        }

        /// <summary>
        /// Sets the provider key a manually tracked game presents as. Null or empty restores the
        /// platform derived from the source game id.
        /// </summary>
        /// <remarks>
        /// Only the stored override moves here. The cached game's effective platform is resolved
        /// from it by <c>ManualDisplayPlatformResolver</c> when the link is re-applied, so the
        /// caller re-projects rather than this writing two places.
        /// </remarks>
        public bool SetManualDisplayPlatform(Guid playniteGameId, string providerKey)
        {
            if (playniteGameId == Guid.Empty)
            {
                return false;
            }

            var normalized = ManualDisplayPlatformResolver.NormalizeOverride(providerKey);
            var wrote = false;
            _gameCustomDataStore.Update(
                playniteGameId,
                customData =>
                {
                    if (customData.ManualLink == null)
                    {
                        return;
                    }

                    customData.ManualLink.DisplayPlatformKeyOverride = normalized;
                    customData.ManualLink.LastModifiedUtc = DateTime.UtcNow;
                    wrote = true;
                },
                // The platform a game attributes to moves library rollups, so summaries rebuild.
                affectsSummaryData: true);

            return wrote;
        }

        /// <summary>
        /// Marks one user-owned achievement unlocked. Returns false when the achievement is not one
        /// the user owns, so a provider row can never be unlocked by mistake.
        /// </summary>
        /// <remarks>
        /// Two kinds qualify and each stores unlock state in its own place: an authored achievement
        /// carries it on its definition, and every achievement of a manually tracked game carries it
        /// in the link. A real provider's achievement is refused here rather than at the call site,
        /// so no caller can route around the rule.
        /// </remarks>
        public ManualUnlockWriteResult TryUnlockUserOwnedAchievement(
            Guid playniteGameId,
            string achievementApiName,
            DateTime? unlockTimeUtc = null)
        {
            var apiName = AchievementNoteHelper.NormalizeApiName(achievementApiName);
            if (playniteGameId == Guid.Empty || string.IsNullOrWhiteSpace(apiName))
            {
                return ManualUnlockWriteResult.NotApplicable;
            }

            if (!_gameCustomDataStore.TryLoad(playniteGameId, out var probe) || probe == null)
            {
                return ManualUnlockWriteResult.NotApplicable;
            }

            var stamp = unlockTimeUtc ?? DateTime.UtcNow;

            if (CustomAchievementProjectionService.IsCustomApiName(apiName))
            {
                var definitions = probe.CustomAchievements;
                if (definitions == null)
                {
                    return ManualUnlockWriteResult.NotApplicable;
                }

                var updated = new List<CustomAchievementDefinition>();
                var matched = false;
                foreach (var definition in definitions)
                {
                    var clone = definition?.Clone();
                    if (clone == null)
                    {
                        continue;
                    }

                    if (string.Equals(
                            CustomAchievementProjectionService.BuildApiName(clone.Id),
                            apiName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        if (clone.Unlocked)
                        {
                            return ManualUnlockWriteResult.AlreadyUnlocked;
                        }

                        clone.Unlocked = true;
                        clone.UnlockTimeUtc = stamp;
                    }

                    updated.Add(clone);
                }

                if (!matched)
                {
                    return ManualUnlockWriteResult.NotApplicable;
                }

                SetCustomAchievements(playniteGameId, updated);
                return ManualUnlockWriteResult.Custom;
            }

            if (probe.ManualLink == null)
            {
                return ManualUnlockWriteResult.NotApplicable;
            }

            if (probe.ManualLink.UnlockStates != null &&
                probe.ManualLink.UnlockStates.TryGetValue(apiName, out var already) &&
                already)
            {
                return ManualUnlockWriteResult.AlreadyUnlocked;
            }

            _gameCustomDataStore.Update(
                playniteGameId,
                customData =>
                {
                    if (customData.ManualLink == null)
                    {
                        return;
                    }

                    if (customData.ManualLink.UnlockStates == null)
                    {
                        customData.ManualLink.UnlockStates = new Dictionary<string, bool>();
                    }

                    if (customData.ManualLink.UnlockTimes == null)
                    {
                        customData.ManualLink.UnlockTimes = new Dictionary<string, DateTime?>();
                    }

                    customData.ManualLink.UnlockStates[apiName] = true;
                    customData.ManualLink.UnlockTimes[apiName] = stamp;
                    customData.ManualLink.LastModifiedUtc = DateTime.UtcNow;
                },
                // Counts and completion move, but the caller re-projects the link onto the cache and
                // that raises its own invalidation, so this write does not need to fire the cascade.
                affectsSummaryData: false);

            return ManualUnlockWriteResult.Manual;
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

        private static void MigrateCustomAchievementApiNameReferences(
            GameCustomDataFile customData,
            IReadOnlyDictionary<string, string> renamedApiNames)
        {
            if (customData == null || renamedApiNames == null || renamedApiNames.Count == 0)
            {
                return;
            }

            customData.ManualCapstoneApiName = MigrateApiName(customData.ManualCapstoneApiName, renamedApiNames);
            foreach (var capstone in customData.Capstones ?? Enumerable.Empty<CapstoneAssignment>())
            {
                if (capstone != null)
                {
                    capstone.ApiName = MigrateApiName(capstone.ApiName, renamedApiNames);
                }
            }

            customData.AchievementOrder = MigrateApiNameList(customData.AchievementOrder, renamedApiNames);
            customData.FilteredAchievementApiNames = MigrateApiNameList(customData.FilteredAchievementApiNames, renamedApiNames);
            customData.SummaryFilteredAchievementApiNames = MigrateApiNameList(customData.SummaryFilteredAchievementApiNames, renamedApiNames);
            customData.GoalAchievementApiNames = MigrateApiNameList(customData.GoalAchievementApiNames, renamedApiNames);
            customData.AchievementCategoryOverrides = MigrateApiNameMap(customData.AchievementCategoryOverrides, renamedApiNames);
            customData.AchievementCategoryTypeOverrides = MigrateApiNameMap(customData.AchievementCategoryTypeOverrides, renamedApiNames);
            customData.AchievementUnlockedIconOverrides = MigrateApiNameMap(customData.AchievementUnlockedIconOverrides, renamedApiNames);
            customData.AchievementLockedIconOverrides = MigrateApiNameMap(customData.AchievementLockedIconOverrides, renamedApiNames);
            customData.AchievementNotes = MigrateApiNameMap(customData.AchievementNotes, renamedApiNames);
            // The record is authoritative, so its keys must follow the rename or the customization
            // is orphaned and normalization republishes the old keys into the mirror maps.
            customData.AchievementOverrides = MigrateApiNameMap(customData.AchievementOverrides, renamedApiNames);
        }

        private static string MigrateApiName(
            string apiName,
            IReadOnlyDictionary<string, string> renamedApiNames)
        {
            var normalized = (apiName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return apiName;
            }

            return renamedApiNames.TryGetValue(normalized, out var migrated) &&
                   !string.IsNullOrWhiteSpace(migrated)
                ? migrated.Trim()
                : apiName;
        }

        private static List<string> MigrateApiNameList(
            IEnumerable<string> apiNames,
            IReadOnlyDictionary<string, string> renamedApiNames)
        {
            if (apiNames == null)
            {
                return null;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var apiName in apiNames)
            {
                var migrated = MigrateApiName(apiName, renamedApiNames);
                if (!string.IsNullOrWhiteSpace(migrated) && seen.Add(migrated))
                {
                    result.Add(migrated);
                }
            }

            return result.Count > 0 ? result : null;
        }

        private static Dictionary<string, TValue> MigrateApiNameMap<TValue>(
            IReadOnlyDictionary<string, TValue> source,
            IReadOnlyDictionary<string, string> renamedApiNames)
        {
            if (source == null)
            {
                return null;
            }

            var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var migratedKey = MigrateApiName(pair.Key, renamedApiNames);
                if (!string.IsNullOrWhiteSpace(migratedKey))
                {
                    result[migratedKey] = pair.Value;
                }
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
