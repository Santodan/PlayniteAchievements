using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// The one place that turns an unlock instant into the day key used by every
    /// <c>*UnlockCountsByDate</c> dictionary, and the shared increment for the global
    /// and per-game maps. Keys are local calendar days (00:00, Kind Unspecified); compare by value.
    /// A runtime time-zone change takes effect on the next full rebuild.
    /// </summary>
    public static class UnlockDayCounts
    {
        /// <summary>Local calendar day of a UTC unlock instant.</summary>
        public static DateTime DayOf(DateTime unlockUtc) => DateTimeUtilities.ToLocalDay(unlockUtc);

        /// <summary>Adds <paramref name="amount"/> to the day of <paramref name="unlockUtc"/> in both maps.</summary>
        public static void Add(
            IDictionary<DateTime, int> global,
            IDictionary<Guid, Dictionary<DateTime, int>> byGame,
            Guid? gameId,
            DateTime unlockUtc,
            int amount = 1)
        {
            AddDay(global, byGame, gameId, DayOf(unlockUtc), amount);
        }

        /// <summary>Adds <paramref name="amount"/> to an already-resolved day key in both maps.</summary>
        public static void AddDay(
            IDictionary<DateTime, int> global,
            IDictionary<Guid, Dictionary<DateTime, int>> byGame,
            Guid? gameId,
            DateTime day,
            int amount = 1)
        {
            if (amount <= 0)
            {
                return;
            }

            var key = day.Date;
            Increment(global, key, amount);

            if (byGame == null || !gameId.HasValue || gameId.Value == Guid.Empty)
            {
                return;
            }

            if (!byGame.TryGetValue(gameId.Value, out var gameCounts) || gameCounts == null)
            {
                gameCounts = new Dictionary<DateTime, int>();
                byGame[gameId.Value] = gameCounts;
            }

            Increment(gameCounts, key, amount);
        }

        /// <summary>Earliest day with a positive count, or null.</summary>
        public static DateTime? Earliest(IReadOnlyDictionary<DateTime, int> counts)
        {
            if (counts == null)
            {
                return null;
            }

            DateTime? earliest = null;
            foreach (var pair in counts)
            {
                if (pair.Value <= 0)
                {
                    continue;
                }

                var day = pair.Key.Date;
                if (!earliest.HasValue || day < earliest.Value)
                {
                    earliest = day;
                }
            }

            return earliest;
        }

        /// <summary>
        /// Regroups per-game day counts by a key per game (the platform), so the groups sum to
        /// <paramref name="global"/>. Games without a key, and global counts no game accounts for
        /// (unlocks recorded without a game id), land under <paramref name="remainderKey"/>.
        /// </summary>
        public static Dictionary<string, Dictionary<DateTime, int>> GroupByKey(
            IReadOnlyDictionary<DateTime, int> global,
            IReadOnlyDictionary<Guid, Dictionary<DateTime, int>> byGame,
            Func<Guid, string> keyOfGame,
            string remainderKey)
        {
            var groups = new Dictionary<string, Dictionary<DateTime, int>>(StringComparer.OrdinalIgnoreCase);
            var accounted = new Dictionary<DateTime, int>();

            if (byGame != null)
            {
                foreach (var game in byGame)
                {
                    if (game.Value == null || game.Value.Count == 0)
                    {
                        continue;
                    }

                    var key = keyOfGame?.Invoke(game.Key);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        key = remainderKey;
                    }

                    if (!groups.TryGetValue(key, out var counts))
                    {
                        counts = new Dictionary<DateTime, int>();
                        groups[key] = counts;
                    }

                    foreach (var day in game.Value)
                    {
                        if (day.Value <= 0)
                        {
                            continue;
                        }

                        Increment(counts, day.Key.Date, day.Value);
                        Increment(accounted, day.Key.Date, day.Value);
                    }
                }
            }

            if (global != null)
            {
                foreach (var day in global)
                {
                    accounted.TryGetValue(day.Key.Date, out var covered);
                    var rest = day.Value - covered;
                    if (rest <= 0)
                    {
                        continue;
                    }

                    if (!groups.TryGetValue(remainderKey, out var counts))
                    {
                        counts = new Dictionary<DateTime, int>();
                        groups[remainderKey] = counts;
                    }

                    Increment(counts, day.Key.Date, rest);
                }
            }

            return groups;
        }

        private static void Increment(IDictionary<DateTime, int> counts, DateTime key, int amount)
        {
            if (counts == null)
            {
                return;
            }

            counts[key] = counts.TryGetValue(key, out var existing) ? existing + amount : amount;
        }
    }
}
