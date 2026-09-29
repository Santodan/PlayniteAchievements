using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// The one place that turns an unlock instant into the day key used by every
    /// <c>*UnlockCountsByDate</c> dictionary, and the shared increment/decrement for the global
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

        /// <summary>
        /// Removes <paramref name="amount"/> from a day key in both maps, deleting keys that reach zero.
        /// Returns false when the global map held nothing for that day.
        /// </summary>
        public static bool RemoveDay(
            IDictionary<DateTime, int> global,
            IDictionary<Guid, Dictionary<DateTime, int>> byGame,
            Guid? gameId,
            DateTime day,
            int amount = 1)
        {
            if (amount <= 0)
            {
                return false;
            }

            var key = day.Date;
            var removed = Decrement(global, key, amount);

            if (byGame != null && gameId.HasValue && byGame.TryGetValue(gameId.Value, out var gameCounts) && gameCounts != null)
            {
                Decrement(gameCounts, key, amount);
                if (gameCounts.Count == 0)
                {
                    byGame.Remove(gameId.Value);
                }
            }

            return removed;
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

        private static void Increment(IDictionary<DateTime, int> counts, DateTime key, int amount)
        {
            if (counts == null)
            {
                return;
            }

            counts[key] = counts.TryGetValue(key, out var existing) ? existing + amount : amount;
        }

        private static bool Decrement(IDictionary<DateTime, int> counts, DateTime key, int amount)
        {
            if (counts == null || !counts.TryGetValue(key, out var existing))
            {
                return false;
            }

            var next = existing - amount;
            if (next <= 0)
            {
                counts.Remove(key);
            }
            else
            {
                counts[key] = next;
            }

            return true;
        }
    }
}
