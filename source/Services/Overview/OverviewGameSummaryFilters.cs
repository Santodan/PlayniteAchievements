using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.Overview
{
    public static class OverviewGameSummaryFilters
    {
        public const string CompleteFallback = "Complete";
        public const string InProgressFallback = "In Progress";
        public const string NoProgressFallback = "No Progress";
        public const string PlayedFallback = "Played";
        public const string UnplayedFallback = "Unplayed";

        public static IEnumerable<GameSummaryItem> ApplyActivityAndProgressFilters(
            IEnumerable<GameSummaryItem> games,
            ISet<string> selectedActivityFilters,
            ISet<string> selectedProgressFilters,
            string playedLabel = PlayedFallback,
            string unplayedLabel = UnplayedFallback,
            string completeLabel = CompleteFallback,
            string inProgressLabel = InProgressFallback,
            string noProgressLabel = NoProgressFallback)
        {
            return ApplyActivityAndProgressFilters(
                games,
                ResolveActivityScope(selectedActivityFilters, playedLabel, unplayedLabel),
                ResolveProgressScope(selectedProgressFilters, completeLabel, inProgressLabel, noProgressLabel));
        }

        public static IEnumerable<GameSummaryItem> ApplyActivityAndProgressFilters(
            IEnumerable<GameSummaryItem> games,
            GameActivityScope activityScope,
            GameProgressScope progressScope)
        {
            var filtered = games ?? Enumerable.Empty<GameSummaryItem>();

            var normalizedActivityScope = NormalizeActivityScope(activityScope);
            if (normalizedActivityScope != GameActivityScope.None &&
                normalizedActivityScope != GameActivityScope.All)
            {
                filtered = filtered.Where(game => MatchesActivityBucket(
                    game,
                    normalizedActivityScope.HasFlag(GameActivityScope.Played),
                    normalizedActivityScope.HasFlag(GameActivityScope.Unplayed)));
            }

            var normalizedProgressScope = NormalizeProgressScope(progressScope);
            if (normalizedProgressScope != GameProgressScope.None &&
                normalizedProgressScope != GameProgressScope.All)
            {
                filtered = filtered.Where(game => MatchesProgressBucket(
                    game,
                    normalizedProgressScope.HasFlag(GameProgressScope.Completed),
                    normalizedProgressScope.HasFlag(GameProgressScope.InProgress),
                    normalizedProgressScope.HasFlag(GameProgressScope.NoProgress)));
            }

            return filtered;
        }

        public static bool MatchesActivityBucket(
            GameSummaryItem game,
            bool includePlayed,
            bool includeUnplayed)
        {
            if (game == null)
            {
                return false;
            }

            var isPlayed = game.LastPlayed.HasValue || game.UnlockedAchievements > 0;
            var isUnplayed = !game.LastPlayed.HasValue && game.UnlockedAchievements == 0;

            return (includePlayed && isPlayed) || (includeUnplayed && isUnplayed);
        }

        public static bool MatchesProgressBucket(
            GameSummaryItem game,
            bool includeComplete,
            bool includeInProgress,
            bool includeNoProgress)
        {
            if (game == null)
            {
                return false;
            }

            if (game.IsCompleted)
            {
                return includeComplete;
            }

            if (game.UnlockedAchievements <= 0)
            {
                return includeNoProgress;
            }

            return includeInProgress;
        }

        private static GameActivityScope ResolveActivityScope(
            ISet<string> selectedActivityFilters,
            string playedLabel,
            string unplayedLabel)
        {
            if (selectedActivityFilters == null || selectedActivityFilters.Count == 0)
            {
                return GameActivityScope.None;
            }

            var scope = GameActivityScope.None;
            if (selectedActivityFilters.Contains(playedLabel))
            {
                scope |= GameActivityScope.Played;
            }

            if (selectedActivityFilters.Contains(unplayedLabel))
            {
                scope |= GameActivityScope.Unplayed;
            }

            return scope;
        }

        private static GameProgressScope ResolveProgressScope(
            ISet<string> selectedProgressFilters,
            string completeLabel,
            string inProgressLabel,
            string noProgressLabel)
        {
            if (selectedProgressFilters == null || selectedProgressFilters.Count == 0)
            {
                return GameProgressScope.None;
            }

            var scope = GameProgressScope.None;
            if (selectedProgressFilters.Contains(completeLabel))
            {
                scope |= GameProgressScope.Completed;
            }

            if (selectedProgressFilters.Contains(inProgressLabel))
            {
                scope |= GameProgressScope.InProgress;
            }

            if (selectedProgressFilters.Contains(noProgressLabel))
            {
                scope |= GameProgressScope.NoProgress;
            }

            return scope;
        }

        private static GameActivityScope NormalizeActivityScope(GameActivityScope scope)
        {
            return scope & GameActivityScope.All;
        }

        private static GameProgressScope NormalizeProgressScope(GameProgressScope scope)
        {
            return scope & GameProgressScope.All;
        }
    }
}
