using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
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
    /// the chart displays. Provider slices use their provider key and Completions slices their
    /// <see cref="ProgressFilterChoices"/> key.
    /// </summary>
    public static class OverviewLinkedSliceKeys
    {
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

        /// <summary>
        /// Games whose completion (see <see cref="TimelinePlatformSeries.CompletionCountsByGame"/>)
        /// falls on a day inside <paramref name="span"/>.
        /// </summary>
        public static HashSet<Guid> GamesCompletedDuring(OverviewDataSnapshot snapshot, UnlockDaySpan span)
        {
            return new HashSet<Guid>(TimelinePlatformSeries.CompletionCountsByGame(snapshot)
                .Where(game => game.Value.Keys.Any(span.Contains))
                .Select(game => game.Key));
        }

        /// <summary>
        /// <paramref name="snapshot"/> with its completions cut to those finished inside
        /// <paramref name="span"/>, by the same rule the whole count uses: a game with capstones
        /// counts each capstone unlocked in the span, one without counts once when it was
        /// completed in the span. No completion is left over as possible, so the cut view holds
        /// only completions. Everything else is as <see cref="Build"/> copies it.
        /// </summary>
        public static OverviewDataSnapshot ClipCompletionsToSpan(OverviewDataSnapshot snapshot, UnlockDaySpan span)
        {
            if (snapshot == null)
            {
                return null;
            }

            var games = (snapshot.GameSummaries ?? new List<GameSummaryItem>()).Where(game => game != null).ToList();
            var clipped = Build(snapshot, games, keptIsAll: false);
            var completedGames = GamesCompletedDuring(snapshot, span);
            var capstonesByGame = snapshot.Unlocks
                .Where(fact => fact.Tally.IsCapstone && fact.Day is DateTime day && span.Contains(day))
                .GroupBy(fact => fact.GameId)
                .ToDictionary(group => group.Key, group => group.Sum(fact => fact.Count));

            var completions = 0;
            var completionsByGame = new Dictionary<Guid, int>();
            foreach (var game in games)
            {
                if (!(game.PlayniteGameId is Guid gameId))
                {
                    continue;
                }

                var inSpan = game.CapstoneTotal > 0
                    ? (capstonesByGame.TryGetValue(gameId, out var capstones) ? capstones : 0)
                    : (completedGames.Contains(gameId) ? 1 : 0);
                completions += inSpan;
                if (inSpan > 0)
                {
                    completionsByGame[gameId] = (completionsByGame.TryGetValue(gameId, out var sum) ? sum : 0) + inSpan;
                }
            }

            clipped.CompletionSpan = span;
            clipped.CompletionsByGame = completionsByGame;
            clipped.Completions = completions;
            clipped.PossibleCompletions = completions;
            clipped.CompletedGames = completedGames.Count;
            return clipped;
        }

        /// <summary>
        /// <paramref name="snapshot"/> as seen through an achievement filter: only the unlocks
        /// <paramref name="keep"/> accepts count, undated ones included (they add no day counts).
        /// Achievement totals, rarity and trophy counts, the per-provider counts and the per-day
        /// counts are rebuilt from those unlocks, with no locked remainder (the filters describe
        /// unlocks); the game-level totals (games, completions) are kept as they were, except that
        /// completions drop to zero when <paramref name="keepCompletions"/> is false.
        /// </summary>
        public static OverviewDataSnapshot ClipToAchievements(
            OverviewDataSnapshot snapshot,
            Func<UnlockFact, bool> keep,
            bool keepCompletions = true)
        {
            if (snapshot == null)
            {
                return null;
            }

            var clipped = Filtered(snapshot, snapshot.Unlocks.Where(keep).ToList(), keepCompletions);
            clipped.IsAchievementClip = true;
            clipped.GlobalProgressionPercent = snapshot.GlobalProgressionPercent;

            // Each unlock counts under its game's platform, the key the games are grouped by.
            var providerByGame = ProviderByGame(snapshot);
            foreach (var fact in clipped.Unlocks)
            {
                var provider = providerByGame.TryGetValue(fact.GameId, out var key) ? key : "Unknown";
                clipped.UnlockedByProvider[provider] = (clipped.UnlockedByProvider.TryGetValue(provider, out var count) ? count : 0) + fact.Count;
                clipped.TotalByProvider[provider] = clipped.UnlockedByProvider[provider];
                AddTier(clipped, fact.Tally, unlocksOnly: true);
            }

            clipped.TotalUnlocked = clipped.Unlocks.Sum(fact => fact.Count);
            clipped.TotalAchievements = clipped.TotalUnlocked;
            clipped.TotalLocked = 0;
            return clipped;
        }

        /// <summary>
        /// <paramref name="snapshot"/> with its achievement counts narrowed to the achievements a
        /// widget's achievement filters keep. The unlocks and per-day counts keep the ones
        /// <paramref name="keep"/> accepts; the achievement totals, locked ones included, and the
        /// rarity, trophy and per-provider counts are summed from the game tallies it accepts.
        /// Game-level totals (games, completions) are kept as they were: achievement filters never
        /// narrow games. Completions drop to zero when <paramref name="keepCompletions"/> is false,
        /// as when the Rarity filter leaves Capstone out. A view already cut to the unlocks an
        /// overview filter keeps holds no locked remainder to count, so it is cut again instead.
        /// </summary>
        public static OverviewDataSnapshot FilterAchievements(
            OverviewDataSnapshot snapshot,
            Func<AchievementTally, bool> keep,
            bool keepCompletions = true)
        {
            if (snapshot == null)
            {
                return null;
            }

            if (snapshot.IsAchievementClip)
            {
                return ClipToAchievements(snapshot, fact => keep(fact.Tally), keepCompletions);
            }

            var filtered = Filtered(snapshot, snapshot.Unlocks.Where(fact => keep(fact.Tally)).ToList(), keepCompletions);
            filtered.TallyFilter = keep;
            foreach (var game in snapshot.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (game == null)
                {
                    continue;
                }

                var provider = string.IsNullOrWhiteSpace(game.ProviderKey) ? "Unknown" : game.ProviderKey;
                var gameUnlocked = 0;
                var gameTotal = 0;
                foreach (var tally in (game.Tallies ?? Array.Empty<AchievementTally>()).Where(keep))
                {
                    gameUnlocked += tally.Unlocked;
                    gameTotal += tally.Total;
                    AddTier(filtered, tally, unlocksOnly: false);
                }

                filtered.TotalUnlocked += gameUnlocked;
                filtered.TotalAchievements += gameTotal;
                filtered.UnlockedByProvider[provider] = (filtered.UnlockedByProvider.TryGetValue(provider, out var unlocked) ? unlocked : 0) + gameUnlocked;
                filtered.TotalByProvider[provider] = (filtered.TotalByProvider.TryGetValue(provider, out var total) ? total : 0) + gameTotal;
            }

            filtered.TotalLocked = Math.Max(0, filtered.TotalAchievements - filtered.TotalUnlocked);
            filtered.GlobalProgressionPercent = filtered.TotalAchievements > 0
                ? (double)filtered.TotalUnlocked / filtered.TotalAchievements * 100
                : 0;
            return filtered;
        }

        // A view of the same games over <paramref name="unlocks"/>, with their day counts and the
        // game-level totals; the achievement totals are left for the caller.
        private static OverviewDataSnapshot Filtered(OverviewDataSnapshot snapshot, List<UnlockFact> unlocks, bool keepCompletions)
        {
            var filtered = new OverviewDataSnapshot
            {
                GameSummaries = snapshot.GameSummaries,
                Unlocks = unlocks,
                CurrentUserIdentities = snapshot.CurrentUserIdentities,
                TotalGames = snapshot.TotalGames,
                CompletedGames = snapshot.CompletedGames,
                Completions = keepCompletions ? snapshot.Completions : 0,
                PossibleCompletions = keepCompletions ? snapshot.PossibleCompletions : 0
            };
            UnlockFact.CountDays(unlocks, filtered.GlobalUnlockCountsByDate, filtered.UnlockCountsByDateByGame);
            return filtered;
        }

        private static Dictionary<Guid, string> ProviderByGame(OverviewDataSnapshot snapshot)
        {
            var providerByGame = new Dictionary<Guid, string>();
            foreach (var game in snapshot.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (game?.PlayniteGameId is Guid id && !providerByGame.ContainsKey(id))
                {
                    providerByGame[id] = string.IsNullOrWhiteSpace(game.ProviderKey) ? "Unknown" : game.ProviderKey;
                }
            }

            return providerByGame;
        }

        // Adds a tally's tier and trophy counts: its unlocks, and its total as what was possible,
        // or with unlocksOnly its unlocks as both, since a cut view holds no locked remainder.
        private static void AddTier(OverviewDataSnapshot snapshot, AchievementTally tally, bool unlocksOnly)
        {
            var possible = unlocksOnly ? tally.Unlocked : tally.Total;
            switch (tally.Rarity)
            {
                case RarityTier.UltraRare:
                    snapshot.TotalUltraRare += tally.Unlocked;
                    snapshot.TotalUltraRarePossible += possible;
                    break;
                case RarityTier.Rare:
                    snapshot.TotalRare += tally.Unlocked;
                    snapshot.TotalRarePossible += possible;
                    break;
                case RarityTier.Uncommon:
                    snapshot.TotalUncommon += tally.Unlocked;
                    snapshot.TotalUncommonPossible += possible;
                    break;
                case RarityTier.Common:
                    snapshot.TotalCommon += tally.Unlocked;
                    snapshot.TotalCommonPossible += possible;
                    break;
            }

            switch (tally.Trophy)
            {
                case "platinum":
                    snapshot.TotalPlatinum += tally.Unlocked;
                    snapshot.TotalPlatinumPossible += possible;
                    break;
                case "gold":
                    snapshot.TotalGold += tally.Unlocked;
                    snapshot.TotalGoldPossible += possible;
                    break;
                case "silver":
                    snapshot.TotalSilver += tally.Unlocked;
                    snapshot.TotalSilverPossible += possible;
                    break;
                case "bronze":
                    snapshot.TotalBronze += tally.Unlocked;
                    snapshot.TotalBronzePossible += possible;
                    break;
            }
        }

        /// <summary>
        /// The part of <paramref name="source"/> that covers <paramref name="kept"/>: its
        /// summary totals, its per-day unlock counts, and its unlocks. When every game is kept
        /// the source itself comes back, so widgets share its derived-series cache.
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

            // A points score card narrowed by its control bar reads these, so the kept games'
            // provider points are summed per family as the full library's are.
            var points = new PlatformScoreTotals();
            foreach (var game in games)
            {
                if (game != null)
                {
                    points.AddToFamily(game.PointsFamily, game.PlatformScorePoints, game.PlatformScorePointsTotal);
                }
            }

            snapshot.ApplyPlatformScores(points);
            var ids = new HashSet<Guid>(games
                .Where(game => game?.PlayniteGameId.HasValue == true)
                .Select(game => game.PlayniteGameId.Value));

            snapshot.UnlockCountsByDateByGame = (source.UnlockCountsByDateByGame ??
                    new Dictionary<Guid, Dictionary<DateTime, int>>())
                .Where(pair => pair.Value != null && ids.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            snapshot.GlobalUnlockCountsByDate = TimelinePlatformSeries.SumGames(source, ids.Contains);
            snapshot.Unlocks = source.Unlocks.Where(fact => ids.Contains(fact.GameId)).ToList();
            snapshot.CurrentUserIdentities = source.CurrentUserIdentities;
            return snapshot;
        }
        /// <summary>
        /// A linked view narrowed further to <paramref name="kept"/>, as a widget's own control
        /// bar does. A view cut to the unlocks an achievement filter keeps stays cut: its rows
        /// are exactly those unlocks, so the narrowed view is cut again from its own rows. A view
        /// whose completions were cut to a span is cut to that span again.
        /// </summary>
        public static OverviewDataSnapshot NarrowToGames(
            OverviewDataSnapshot linked,
            IReadOnlyList<GameSummaryItem> kept)
        {
            var narrowed = Build(linked, kept, keptIsAll: false);
            if (linked?.CompletionSpan is UnlockDaySpan span)
            {
                return ClipCompletionsToSpan(narrowed, span);
            }

            return linked?.IsAchievementClip == true
                ? ClipToAchievements(narrowed, _ => true)
                : narrowed;
        }
    }
}
