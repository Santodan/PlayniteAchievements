using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>The overview filters a linked widget can leave out: the ones it sets itself.</summary>
    [Flags]
    public enum OverviewLinkedFilter
    {
        None = 0,
        Provider = 1,
        Completeness = 2,
        UnlockSpan = 4,
        Rarity = 8,
        Trophy = 16,
        Tag = 32
    }

    /// <summary>
    /// Slice identities a linked pie exchanges with the overview, independent of the labels
    /// the chart displays. Provider slices use their provider key.
    /// </summary>
    public static class OverviewLinkedSliceKeys
    {
        public const string Complete = "Complete";
        public const string Incomplete = "Incomplete";
        public const string Locked = "Locked";
    }

    /// <summary>When a linked widget narrows to the overview's selected game.</summary>
    public enum OverviewLinkedSelection
    {
        /// <summary>Stays on the filtered library.</summary>
        Ignore,

        /// <summary>Narrows whenever a game is selected.</summary>
        Always,

        /// <summary>Narrows only when the selected game has rarity data to chart.</summary>
        WhenRarityData,

        /// <summary>Narrows only when the selected game has trophies to chart.</summary>
        WhenTrophyData
    }

    /// <summary>
    /// An inclusive range of local unlock days, the shape a timeline bar or a calendar day
    /// covers. Day keys match <see cref="OverviewDataSnapshot.UnlockCountsByDateByGame"/>.
    /// </summary>
    public readonly struct UnlockDaySpan : IEquatable<UnlockDaySpan>
    {
        public UnlockDaySpan(DateTime start, DateTime end)
        {
            Start = start.Date <= end.Date ? start.Date : end.Date;
            End = start.Date <= end.Date ? end.Date : start.Date;
        }

        public DateTime Start { get; }

        public DateTime End { get; }

        public bool Contains(DateTime day) => day.Date >= Start && day.Date <= End;

        public bool Equals(UnlockDaySpan other) => Start == other.Start && End == other.End;

        public override bool Equals(object obj) => obj is UnlockDaySpan other && Equals(other);

        public override int GetHashCode() => Start.GetHashCode() * 397 ^ End.GetHashCode();
    }

    /// <summary>
    /// Snapshots for the overview's linked widgets: the overview's own snapshot cut down to the
    /// games its filters keep. Pure, so the rules can be checked without a view model.
    /// </summary>
    public static class OverviewLinkedSnapshots
    {
        /// <summary>
        /// Games with at least one unlock on a day inside <paramref name="span"/>, by the
        /// snapshot's per-game day counts.
        /// </summary>
        public static HashSet<Guid> GamesUnlockedDuring(OverviewDataSnapshot snapshot, UnlockDaySpan span)
        {
            var result = new HashSet<Guid>();
            foreach (var game in snapshot?.UnlockCountsByDateByGame ?? new Dictionary<Guid, Dictionary<DateTime, int>>())
            {
                if (game.Value != null && game.Value.Any(day => day.Value > 0 && span.Contains(day.Key)))
                {
                    result.Add(game.Key);
                }
            }

            return result;
        }

        /// <summary>
        /// Whether a linked widget with this selection rule narrows to <paramref name="selected"/>.
        /// </summary>
        public static bool NarrowsTo(GameSummaryItem selected, OverviewLinkedSelection selection)
        {
            if (selected?.PlayniteGameId.HasValue != true)
            {
                return false;
            }

            switch (selection)
            {
                case OverviewLinkedSelection.Always:
                    return true;
                case OverviewLinkedSelection.WhenRarityData:
                    return selected.HasRarityPieChartData;
                case OverviewLinkedSelection.WhenTrophyData:
                    return selected.HasTrophyPieChartData;
                default:
                    return false;
            }
        }

        /// <summary><see cref="ClipToAchievements"/> keeping the unlocks whose local day falls in <paramref name="span"/>.</summary>
        public static OverviewDataSnapshot ClipToSpan(OverviewDataSnapshot snapshot, UnlockDaySpan span)
        {
            return ClipToAchievements(
                snapshot,
                item => item?.UnlockTimeUtc.HasValue == true && span.Contains(UnlockDayCounts.DayOf(item.UnlockTimeUtc.Value)));
        }

        /// <summary>
        /// <paramref name="snapshot"/> as seen through an achievement filter: only the unlocks
        /// <paramref name="keep"/> accepts count. Achievement totals, rarity and trophy counts,
        /// the per-provider counts, the per-day counts and the unlocked rows are rebuilt from those
        /// unlocks, with no locked remainder (the filters describe unlocks); the game-level totals
        /// (games, completions) are kept as they were, except that completions drop to zero when
        /// <paramref name="keepCompletions"/> is false.
        /// </summary>
        public static OverviewDataSnapshot ClipToAchievements(
            OverviewDataSnapshot snapshot,
            Func<AchievementDisplayItem, bool> keep,
            bool keepCompletions = true)
        {
            if (snapshot == null)
            {
                return null;
            }

            var rows = (snapshot.Achievements ?? new List<AchievementDisplayItem>())
                .Where(item => item?.Unlocked == true && item.UnlockTimeUtc.HasValue && keep(item))
                .ToList();
            var byGame = new Dictionary<Guid, Dictionary<DateTime, int>>();
            var global = new Dictionary<DateTime, int>();
            foreach (var row in rows)
            {
                var day = UnlockDayCounts.DayOf(row.UnlockTimeUtc.Value);
                global[day] = global.TryGetValue(day, out var total) ? total + 1 : 1;
                if (row.PlayniteGameId.HasValue)
                {
                    if (!byGame.TryGetValue(row.PlayniteGameId.Value, out var counts))
                    {
                        counts = new Dictionary<DateTime, int>();
                        byGame[row.PlayniteGameId.Value] = counts;
                    }

                    counts[day] = counts.TryGetValue(day, out var count) ? count + 1 : 1;
                }
            }

            var clipped = new OverviewDataSnapshot
            {
                IsAchievementClip = true,
                GameSummaries = snapshot.GameSummaries,
                Achievements = rows,
                RecentAchievements = (snapshot.RecentAchievements ?? new List<AchievementDisplayItem>())
                    .Where(item => item?.UnlockTimeUtc.HasValue == true && keep(item))
                    .ToList(),
                CurrentUserIdentities = snapshot.CurrentUserIdentities,
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalGames = snapshot.TotalGames,
                CompletedGames = snapshot.CompletedGames,
                Completions = keepCompletions ? snapshot.Completions : 0,
                PossibleCompletions = keepCompletions ? snapshot.PossibleCompletions : 0,
                GlobalProgressionPercent = snapshot.GlobalProgressionPercent,
                GlobalUnlockCountsByDate = global,
                UnlockCountsByDateByGame = byGame
            };

            clipped.TotalUnlocked = rows.Count;
            clipped.TotalAchievements = rows.Count;
            clipped.TotalLocked = 0;

            // Each unlock counts under its game's platform, the key the games are grouped by.
            var providerByGame = new Dictionary<Guid, string>();
            foreach (var game in snapshot.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (game?.PlayniteGameId.HasValue == true && !providerByGame.ContainsKey(game.PlayniteGameId.Value))
                {
                    providerByGame[game.PlayniteGameId.Value] = game.ProviderKey;
                }
            }

            foreach (var row in rows)
            {
                string provider = null;
                if (row.PlayniteGameId.HasValue)
                {
                    providerByGame.TryGetValue(row.PlayniteGameId.Value, out provider);
                }

                if (string.IsNullOrWhiteSpace(provider))
                {
                    provider = string.IsNullOrWhiteSpace(row.ProviderKey) ? "Unknown" : row.ProviderKey;
                }

                clipped.UnlockedByProvider.TryGetValue(provider, out var providerUnlocked);
                clipped.UnlockedByProvider[provider] = providerUnlocked + 1;
                clipped.TotalByProvider[provider] = providerUnlocked + 1;

                switch (row.Rarity)
                {
                    case Models.Achievements.RarityTier.UltraRare:
                        clipped.TotalUltraRare++;
                        break;
                    case Models.Achievements.RarityTier.Rare:
                        clipped.TotalRare++;
                        break;
                    case Models.Achievements.RarityTier.Uncommon:
                        clipped.TotalUncommon++;
                        break;
                    default:
                        clipped.TotalCommon++;
                        break;
                }

                switch ((row.TrophyType ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "platinum":
                        clipped.TotalPlatinum++;
                        break;
                    case "gold":
                        clipped.TotalGold++;
                        break;
                    case "silver":
                        clipped.TotalSilver++;
                        break;
                    case "bronze":
                        clipped.TotalBronze++;
                        break;
                }
            }

            clipped.TotalCommonPossible = clipped.TotalCommon;
            clipped.TotalUncommonPossible = clipped.TotalUncommon;
            clipped.TotalRarePossible = clipped.TotalRare;
            clipped.TotalUltraRarePossible = clipped.TotalUltraRare;
            clipped.TotalPlatinumPossible = clipped.TotalPlatinum;
            clipped.TotalGoldPossible = clipped.TotalGold;
            clipped.TotalSilverPossible = clipped.TotalSilver;
            clipped.TotalBronzePossible = clipped.TotalBronze;
            return clipped;
        }

        /// <summary>
        /// <paramref name="snapshot"/> with its achievement counts narrowed to the achievements a
        /// widget's achievement filters keep. The unlocked rows and per-day counts keep the rows
        /// <paramref name="keepRow"/> accepts; the achievement totals, locked ones included, and
        /// the rarity, trophy and per-provider counts are summed from the tallies
        /// <paramref name="keepTally"/> accepts. Game-level totals (games, completions) are kept as
        /// they were: achievement filters never narrow games. Completions drop to zero when
        /// <paramref name="keepCompletions"/> is false, as when the Rarity filter leaves Capstone
        /// out. A view already cut to the unlocks an overview filter keeps holds no locked
        /// remainder to count, so it is cut again instead.
        /// </summary>
        public static OverviewDataSnapshot FilterAchievements(
            OverviewDataSnapshot snapshot,
            Func<AchievementDisplayItem, bool> keepRow,
            Func<Models.Achievements.AchievementTally, bool> keepTally,
            bool keepCompletions = true)
        {
            if (snapshot == null)
            {
                return null;
            }

            if (snapshot.IsAchievementClip)
            {
                return ClipToAchievements(snapshot, keepRow, keepCompletions);
            }

            var rows = (snapshot.Achievements ?? new List<AchievementDisplayItem>())
                .Where(item => item?.Unlocked == true && keepRow(item))
                .ToList();
            var byGame = new Dictionary<Guid, Dictionary<DateTime, int>>();
            var global = new Dictionary<DateTime, int>();
            foreach (var row in rows.Where(item => item.UnlockTimeUtc.HasValue))
            {
                var day = UnlockDayCounts.DayOf(row.UnlockTimeUtc.Value);
                global[day] = global.TryGetValue(day, out var total) ? total + 1 : 1;
                if (row.PlayniteGameId.HasValue)
                {
                    if (!byGame.TryGetValue(row.PlayniteGameId.Value, out var counts))
                    {
                        counts = new Dictionary<DateTime, int>();
                        byGame[row.PlayniteGameId.Value] = counts;
                    }

                    counts[day] = counts.TryGetValue(day, out var count) ? count + 1 : 1;
                }
            }

            var filtered = new OverviewDataSnapshot
            {
                TallyFilter = keepTally,
                GameSummaries = snapshot.GameSummaries,
                Achievements = rows,
                RecentAchievements = (snapshot.RecentAchievements ?? new List<AchievementDisplayItem>())
                    .Where(item => item != null && keepRow(item))
                    .ToList(),
                CurrentUserIdentities = snapshot.CurrentUserIdentities,
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalGames = snapshot.TotalGames,
                CompletedGames = snapshot.CompletedGames,
                Completions = keepCompletions ? snapshot.Completions : 0,
                PossibleCompletions = keepCompletions ? snapshot.PossibleCompletions : 0,
                GlobalUnlockCountsByDate = global,
                UnlockCountsByDateByGame = byGame
            };

            foreach (var game in snapshot.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (game == null)
                {
                    continue;
                }

                var provider = string.IsNullOrWhiteSpace(game.ProviderKey) ? "Unknown" : game.ProviderKey;
                var gameUnlocked = 0;
                var gameTotal = 0;
                foreach (var tally in game.Tallies ?? Array.Empty<Models.Achievements.AchievementTally>())
                {
                    if (!keepTally(tally))
                    {
                        continue;
                    }

                    gameUnlocked += tally.Unlocked;
                    gameTotal += tally.Total;
                    AddTier(filtered, tally);
                    AddTrophy(filtered, tally);
                }

                filtered.TotalUnlocked += gameUnlocked;
                filtered.TotalAchievements += gameTotal;
                filtered.UnlockedByProvider.TryGetValue(provider, out var providerUnlocked);
                filtered.UnlockedByProvider[provider] = providerUnlocked + gameUnlocked;
                filtered.TotalByProvider.TryGetValue(provider, out var providerTotal);
                filtered.TotalByProvider[provider] = providerTotal + gameTotal;
            }

            filtered.TotalLocked = Math.Max(0, filtered.TotalAchievements - filtered.TotalUnlocked);
            filtered.GlobalProgressionPercent = filtered.TotalAchievements > 0
                ? (double)filtered.TotalUnlocked / filtered.TotalAchievements * 100
                : 0;
            return filtered;
        }

        private static void AddTier(OverviewDataSnapshot snapshot, Models.Achievements.AchievementTally tally)
        {
            switch (tally.Rarity)
            {
                case Models.Achievements.RarityTier.UltraRare:
                    snapshot.TotalUltraRare += tally.Unlocked;
                    snapshot.TotalUltraRarePossible += tally.Total;
                    break;
                case Models.Achievements.RarityTier.Rare:
                    snapshot.TotalRare += tally.Unlocked;
                    snapshot.TotalRarePossible += tally.Total;
                    break;
                case Models.Achievements.RarityTier.Uncommon:
                    snapshot.TotalUncommon += tally.Unlocked;
                    snapshot.TotalUncommonPossible += tally.Total;
                    break;
                case Models.Achievements.RarityTier.Common:
                    snapshot.TotalCommon += tally.Unlocked;
                    snapshot.TotalCommonPossible += tally.Total;
                    break;
            }
        }

        private static void AddTrophy(OverviewDataSnapshot snapshot, Models.Achievements.AchievementTally tally)
        {
            switch (tally.Trophy)
            {
                case "platinum":
                    snapshot.TotalPlatinum += tally.Unlocked;
                    snapshot.TotalPlatinumPossible += tally.Total;
                    break;
                case "gold":
                    snapshot.TotalGold += tally.Unlocked;
                    snapshot.TotalGoldPossible += tally.Total;
                    break;
                case "silver":
                    snapshot.TotalSilver += tally.Unlocked;
                    snapshot.TotalSilverPossible += tally.Total;
                    break;
                case "bronze":
                    snapshot.TotalBronze += tally.Unlocked;
                    snapshot.TotalBronzePossible += tally.Total;
                    break;
            }
        }

        /// <summary>
        /// The part of <paramref name="source"/> that covers <paramref name="kept"/>: its
        /// summary totals, its per-day unlock counts, and its unlocked rows. When every game
        /// is kept the source itself comes back, so widgets share its derived-series cache.
        /// </summary>
        public static OverviewDataSnapshot Build(
            OverviewDataSnapshot source,
            IReadOnlyList<GameSummaryItem> kept,
            bool keptIsAll)
        {
            if (source == null)
            {
                return null;
            }

            if (keptIsAll)
            {
                return source;
            }

            var games = kept ?? Array.Empty<GameSummaryItem>();
            var snapshot = OverviewDataSnapshot.FromGameSummaries(games);
            var ids = new HashSet<Guid>(games
                .Where(game => game?.PlayniteGameId.HasValue == true)
                .Select(game => game.PlayniteGameId.Value));

            snapshot.UnlockCountsByDateByGame = (source.UnlockCountsByDateByGame ??
                    new Dictionary<Guid, Dictionary<DateTime, int>>())
                .Where(pair => pair.Value != null && ids.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            snapshot.GlobalUnlockCountsByDate = TimelinePlatformSeries.SumGames(source, ids.Contains);
            snapshot.Achievements = (source.Achievements ?? new List<AchievementDisplayItem>())
                .Where(item => item?.PlayniteGameId.HasValue == true && ids.Contains(item.PlayniteGameId.Value))
                .ToList();
            snapshot.RecentAchievements = (source.RecentAchievements ?? new List<AchievementDisplayItem>())
                .Where(item => item?.PlayniteGameId.HasValue == true && ids.Contains(item.PlayniteGameId.Value))
                .ToList();
            snapshot.CurrentUserIdentities = source.CurrentUserIdentities;
            return snapshot;
        }

        /// <summary>
        /// A linked view narrowed further to <paramref name="kept"/>, as a widget's own control
        /// bar does. A view cut to the unlocks an achievement filter keeps stays cut: its rows
        /// are exactly those unlocks, so the narrowed view is cut again from its own rows.
        /// </summary>
        public static OverviewDataSnapshot NarrowToGames(
            OverviewDataSnapshot linked,
            IReadOnlyList<GameSummaryItem> kept)
        {
            var narrowed = Build(linked, kept, keptIsAll: false);
            return linked?.IsAchievementClip == true
                ? ClipToAchievements(narrowed, _ => true)
                : narrowed;
        }
    }
}
