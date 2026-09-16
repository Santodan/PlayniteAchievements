using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// Chooses which games and which of their locked achievements the Unlock Next candidate pool
    /// keeps. Split from <see cref="OverviewDataBuilder"/> because the caps are the whole point of
    /// the pool - locked rows are excluded from the overview snapshot for memory reasons - and
    /// they are worth testing without a cache, a Playnite API, or a snapshot build.
    ///
    /// The pool is deliberately config-independent: it is a superset that dominates every
    /// per-widget combination of criterion, window, and per-game cap, because editing a widget
    /// option re-projects from the existing snapshot rather than rebuilding it.
    /// </summary>
    public static class UnlockNextCandidateSelector
    {
        /// <summary>Games hydrated for the pool, whatever the widgets ask for.</summary>
        public const int GameCap = 40;

        /// <summary>Games taken from each of the two candidate rankings before they are merged.</summary>
        public const int GamesPerRanking = 20;

        /// <summary>Locked rows retained per game from each of the two within-game rankings.</summary>
        public const int PerGameSlice = 10;

        /// <summary>Ceiling on the whole pool, so a large library cannot grow it without bound.</summary>
        public const int PoolCap = 600;

        /// <summary>
        /// Unfinished games worth hydrating, merged from the two rankings the widget criteria care
        /// about - most recently played and closest to completion - so narrowing by either in the
        /// projection stays exact within the cap.
        /// </summary>
        public static IReadOnlyList<GameSummaryItem> SelectGames(
            IEnumerable<GameSummaryItem> summaries)
        {
            var candidates = (summaries ?? new List<GameSummaryItem>())
                .Where(summary => summary?.PlayniteGameId != null &&
                    summary.PlayniteGameId.Value != Guid.Empty &&
                    summary.TotalAchievements > 0 &&
                    summary.UnlockedAchievements < summary.TotalAchievements)
                .ToList();

            var byRecency = candidates
                .OrderByDescending(summary => summary.LastPlayed ?? DateTime.MinValue)
                .Take(GamesPerRanking);
            var byCompletion = candidates
                .OrderByDescending(CompletionFraction)
                .ThenByDescending(summary => summary.LastPlayed ?? DateTime.MinValue)
                .Take(GamesPerRanking);

            // Interleaved rather than concatenated so a pool that hits its ceiling still carries
            // both rankings instead of only the most recently played games.
            return Interleave(byRecency, byCompletion)
                .Distinct()
                .Take(GameCap)
                .ToList();
        }

        /// <summary>
        /// The locked rows worth retaining for one game: the head of its own order (what "next in
        /// line" and "closest to completion" read) plus the most commonly unlocked (what "easiest"
        /// reads). Hidden rows stay in; the projection drops them unless the widget opts in.
        /// </summary>
        public static IReadOnlyList<AchievementDetail> SelectAchievements(
            IEnumerable<AchievementDetail> achievements)
        {
            var locked = (achievements ?? new List<AchievementDetail>())
                .Where(detail => detail != null && !detail.Unlocked)
                .ToList();

            var byOrder = locked
                .OrderBy(detail => detail.DefaultOrderIndex)
                .ThenBy(detail => detail.ApiName, StringComparer.OrdinalIgnoreCase)
                .Take(PerGameSlice);

            // An achievement with no global percentage is not known to be easy, so it never takes
            // a slot in the commonness slice; the order slice still carries it.
            var byCommonness = locked
                .Where(detail => detail.GlobalPercentUnlocked.HasValue)
                .OrderByDescending(detail => detail.GlobalPercentUnlocked.Value)
                .ThenBy(detail => detail.ApiName, StringComparer.OrdinalIgnoreCase)
                .Take(PerGameSlice);

            return byOrder.Concat(byCommonness).Distinct().ToList();
        }

        /// <summary>
        /// The raw completion fraction, not <see cref="GameSummaryItem.Progression"/>, which
        /// rounds to a whole percent and ties heavily across a library.
        /// </summary>
        public static double CompletionFraction(GameSummaryItem summary)
        {
            return summary == null || summary.TotalAchievements <= 0
                ? 0
                : (double)summary.UnlockedAchievements / summary.TotalAchievements;
        }

        private static IEnumerable<T> Interleave<T>(IEnumerable<T> first, IEnumerable<T> second)
        {
            using (var left = first.GetEnumerator())
            using (var right = second.GetEnumerator())
            {
                var leftHas = true;
                var rightHas = true;
                while (leftHas || rightHas)
                {
                    if (leftHas && (leftHas = left.MoveNext()))
                    {
                        yield return left.Current;
                    }

                    if (rightHas && (rightHas = right.MoveNext()))
                    {
                        yield return right.Current;
                    }
                }
            }
        }
    }
}
