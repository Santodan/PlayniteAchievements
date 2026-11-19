using System;
using System.Collections.Generic;
using System.Globalization;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// The current user's unlocks that share a game, a local unlock day, a stored tag text, a
    /// rarity tier, a trophy grade and a capstone flag, read as one totals group: their count,
    /// scores and global percents. Day charts, score history, statistics and every filtered view
    /// count from these rather than from rows.
    /// </summary>
    public sealed class UnlockFact
    {
        public Guid GameId { get; set; }

        /// <summary>The local unlock day; null for unlocks without a time.</summary>
        public DateTime? Day { get; set; }

        /// <summary>The facets, with the stored tags as the key; unlocked and total are the count.</summary>
        public AchievementTally Tally { get; set; }

        public int Count => Tally.Unlocked;

        public int CollectionScore { get; set; }

        public int PrestigeScore { get; set; }

        /// <summary>Points toward a platform score: softcore unlocks are left out.</summary>
        public int PlatformScorePoints { get; set; }

        public double PercentSum { get; set; }

        public int PercentCount { get; set; }

        /// <summary>The lowest positive global percent among them, and among the base-game capstones.</summary>
        public double? MinPercent { get; set; }

        public double? MinBasePercent { get; set; }

        /// <summary>One <see cref="Database.AchievementGroup.DayTally"/> group.</summary>
        internal static UnlockFact From(Database.AchievementTotalsRecord totals)
        {
            var parts = (totals?.Key ?? string.Empty).Split('\u001f');
            if (parts.Length != 6)
            {
                return null;
            }

            return new UnlockFact
            {
                GameId = Guid.TryParse(parts[0], out var id) ? id : Guid.Empty,
                Day = parts[1].Length == 0 ? (DateTime?)null : DateTime.ParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Tally = new AchievementTally(
                    parts[2],
                    (RarityTier)Enum.Parse(typeof(RarityTier), parts[3]),
                    AchievementTally.NormalizeTrophy(parts[4]),
                    parts[5] == "1",
                    (int)totals.Count,
                    (int)totals.Count),
                CollectionScore = (int)totals.CollectionScore,
                PrestigeScore = (int)totals.PrestigeScore,
                PlatformScorePoints = (int)totals.PlatformScorePoints,
                PercentSum = totals.PercentSum,
                PercentCount = (int)totals.PercentCount,
                MinPercent = totals.MinPercent,
                MinBasePercent = totals.MinBasePercent
            };
        }

        /// <summary>Adds the dated unlocks of <paramref name="facts"/> to both day count maps.</summary>
        public static void CountDays(
            IEnumerable<UnlockFact> facts,
            IDictionary<DateTime, int> global,
            IDictionary<Guid, Dictionary<DateTime, int>> byGame)
        {
            foreach (var fact in facts ?? Array.Empty<UnlockFact>())
            {
                if (fact?.Day is DateTime day)
                {
                    UnlockDayCounts.AddDay(global, byGame, fact.GameId, day, fact.Count);
                }
            }
        }
    }

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
