using PlayniteAchievements.Services.Cache;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Splices one game's freshly read contribution into an existing whole-library summary, so a
    /// single-game custom-data edit no longer forces the five unfiltered whole-library queries
    /// behind <see cref="Database.SummaryCacheReader.LoadCachedSummaryData"/> (638 game rows and
    /// every unlocked row in the library, measured at 600-1800ms).
    ///
    /// <para>
    /// The output is always a new envelope holding new lists. Rows for games the patch did not
    /// touch are carried by reference: nothing downstream mutates rows it receives (the overview
    /// and theme builders copy into fresh objects), and copying thousands of rows per edit would
    /// give back the cost this exists to remove.
    /// </para>
    /// <para>
    /// The rule that makes reference-sharing safe is that no row is ever hydrated twice.
    /// Hydration writes onto row objects in place, and
    /// <see cref="CustomAchievementSummaryMerger"/> *increments* counts and *appends* to
    /// PlatinumApiNames, so re-running it over a shared row double-counts. Callers must pass a
    /// slice that was read and hydrated on its own, never a re-hydration of the base.
    /// </para>
    /// </summary>
    internal static class OverviewSummaryPatcher
    {
        /// <summary>
        /// Replaces <paramref name="gameId"/>'s contribution to <paramref name="baseData"/> with
        /// <paramref name="hydratedSlice"/>. Returns null when the patch cannot be expressed, in
        /// which case the caller must fall back to a full rebuild.
        /// </summary>
        public static CachedSummaryData Patch(
            CachedSummaryData baseData,
            Guid gameId,
            CachedSummaryData hydratedSlice)
        {
            return Patch(
                baseData,
                new[] { gameId },
                new Dictionary<Guid, CachedSummaryData> { [gameId] = hydratedSlice });
        }

        public static CachedSummaryData Patch(
            CachedSummaryData baseData,
            IReadOnlyList<Guid> gameIds,
            IReadOnlyDictionary<Guid, CachedSummaryData> hydratedSlices)
        {
            if (baseData == null || gameIds == null || gameIds.Count == 0 || hydratedSlices == null)
            {
                return null;
            }

            var targets = new HashSet<Guid>(gameIds.Where(id => id != Guid.Empty));
            if (targets.Count == 0)
            {
                return null;
            }

            // Every named game must have been read, even if its slice came back empty (the game
            // was excluded, or lost its last visible achievement). A missing entry means the
            // caller could not read it, which is not the same thing and must not be treated as
            // "this game now contributes nothing".
            foreach (var id in targets)
            {
                if (!hydratedSlices.ContainsKey(id) || hydratedSlices[id] == null)
                {
                    return null;
                }
            }

            // A game that had rows in the base but reads back with none is the signature of a
            // scoped read that failed to match -- a GUID stored in a format the predicate does
            // not find, say. Falling back is what keeps that from silently deleting the game.
            foreach (var id in targets)
            {
                var hadRows = baseData.Games?.Any(game => game?.PlayniteGameId == id) == true;
                var hasRows = hydratedSlices[id].Games?.Any(game => game?.PlayniteGameId == id) == true;
                var isExcludedNow = hydratedSlices[id].Games?.Count == 0 &&
                                    hydratedSlices[id].Achievements?.Count == 0 &&
                                    hydratedSlices[id].RecentUnlocks?.Count == 0;

                if (hadRows && !hasRows && !isExcludedNow)
                {
                    return null;
                }
            }

            var result = new CachedSummaryData
            {
                // Under limit 0 -- the only limit this patches -- the flag is never set by the
                // reader or the merger, so it carries through unchanged. Bounded-limit entries
                // are dropped rather than patched, because a trimmed row cannot be recovered.
                HasMoreRecentUnlocks = baseData.HasMoreRecentUnlocks
            };

            result.Games = Splice(
                baseData.Games,
                targets,
                game => game?.PlayniteGameId,
                hydratedSlices.Values.SelectMany(slice => slice.Games ?? new List<CachedGameSummaryData>()));

            // Achievements first, then RecentUnlocks derived from it, preserving the aliasing the
            // reader establishes at limit 0: the two lists hold the same instances, and the merger
            // appends one instance to both. Building them independently would break that.
            var achievements = Splice(
                baseData.Achievements,
                targets,
                item => item?.PlayniteGameId,
                hydratedSlices.Values.SelectMany(slice => slice.Achievements ?? new List<CachedRecentUnlockData>()));

            result.Achievements = RecentUnlockOrder.Sorted(achievements);
            result.RecentUnlocks = result.Achievements
                .Where(item => item?.Unlocked == true && item.UnlockTimeUtc.HasValue)
                .ToList();

            result.UnlockCountsByDateByGame = PatchPerGameCounts(
                baseData.UnlockCountsByDateByGame,
                targets,
                hydratedSlices);

            result.GlobalUnlockCountsByDate = PatchGlobalCounts(
                baseData.GlobalUnlockCountsByDate,
                baseData.UnlockCountsByDateByGame,
                targets,
                hydratedSlices);

            return result;
        }

        /// <summary>
        /// Every row whose game the patch did not touch, by reference, plus the replacements.
        /// </summary>
        private static List<T> Splice<T>(
            List<T> baseRows,
            HashSet<Guid> targets,
            Func<T, Guid?> gameIdOf,
            IEnumerable<T> replacements)
            where T : class
        {
            var result = new List<T>();
            if (baseRows != null)
            {
                for (var i = 0; i < baseRows.Count; i++)
                {
                    var row = baseRows[i];
                    var id = gameIdOf(row);

                    // A row with no Playnite game id can never be one of the targets, so it is
                    // always retained: custom data is keyed by Playnite game id.
                    if (!id.HasValue || !targets.Contains(id.Value))
                    {
                        result.Add(row);
                    }
                }
            }

            foreach (var replacement in replacements)
            {
                if (replacement != null)
                {
                    result.Add(replacement);
                }
            }

            return result;
        }

        private static Dictionary<Guid, Dictionary<DateTime, int>> PatchPerGameCounts(
            Dictionary<Guid, Dictionary<DateTime, int>> baseCounts,
            HashSet<Guid> targets,
            IReadOnlyDictionary<Guid, CachedSummaryData> slices)
        {
            var result = new Dictionary<Guid, Dictionary<DateTime, int>>();
            if (baseCounts != null)
            {
                foreach (var pair in baseCounts)
                {
                    // Untouched games keep their inner dictionary by reference.
                    if (!targets.Contains(pair.Key))
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }

            foreach (var id in targets)
            {
                var replacement = ReadPerGameCounts(slices[id], id);

                // No entry rather than an empty one when the game contributes no unlocks, so the
                // shape matches what a full rebuild produces (it only creates an entry on a
                // positive count).
                if (replacement != null && replacement.Count > 0)
                {
                    result[id] = replacement;
                }
            }

            return result;
        }

        private static Dictionary<DateTime, int> PatchGlobalCounts(
            Dictionary<DateTime, int> baseGlobal,
            Dictionary<Guid, Dictionary<DateTime, int>> baseByGame,
            HashSet<Guid> targets,
            IReadOnlyDictionary<Guid, CachedSummaryData> slices)
        {
            var result = baseGlobal == null
                ? new Dictionary<DateTime, int>()
                : new Dictionary<DateTime, int>(baseGlobal);

            foreach (var id in targets)
            {
                // Subtract what this game used to contribute, then add what it contributes now.
                if (baseByGame != null && baseByGame.TryGetValue(id, out var previous) && previous != null)
                {
                    foreach (var pair in previous)
                    {
                        Decrement(result, pair.Key, pair.Value);
                    }
                }

                var replacement = ReadPerGameCounts(slices[id], id);
                if (replacement == null)
                {
                    continue;
                }

                foreach (var pair in replacement)
                {
                    Increment(result, pair.Key, pair.Value);
                }
            }

            return result;
        }

        /// <summary>
        /// A slice is read scoped to one game, but its timeline is still keyed by game, so read
        /// the entry rather than assuming the slice holds exactly one.
        /// </summary>
        private static Dictionary<DateTime, int> ReadPerGameCounts(CachedSummaryData slice, Guid gameId)
        {
            if (slice?.UnlockCountsByDateByGame == null)
            {
                return null;
            }

            return slice.UnlockCountsByDateByGame.TryGetValue(gameId, out var counts) ? counts : null;
        }

        private static void Increment(Dictionary<DateTime, int> counts, DateTime date, int by)
        {
            if (by == 0)
            {
                return;
            }

            counts.TryGetValue(date, out var current);
            counts[date] = current + by;
        }

        /// <summary>
        /// Mirrors how the exclusion path drops timeline counts: a date whose last contributor
        /// goes away is removed, not left sitting at zero, because that is the shape a full
        /// rebuild produces.
        /// </summary>
        private static void Decrement(Dictionary<DateTime, int> counts, DateTime date, int by)
        {
            if (by == 0 || !counts.TryGetValue(date, out var current))
            {
                return;
            }

            var remaining = current - by;
            if (remaining > 0)
            {
                counts[date] = remaining;
            }
            else
            {
                counts.Remove(date);
            }
        }
    }
}
