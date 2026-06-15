using System;
using System.Collections.Generic;
using System.Linq;
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
            var filtered = games ?? Enumerable.Empty<GameSummaryItem>();

            if (selectedActivityFilters != null && selectedActivityFilters.Count > 0)
            {
                var includePlayed = selectedActivityFilters.Contains(playedLabel);
                var includeUnplayed = selectedActivityFilters.Contains(unplayedLabel);

                filtered = filtered.Where(game => MatchesActivityBucket(game, includePlayed, includeUnplayed));
            }

            if (selectedProgressFilters != null && selectedProgressFilters.Count > 0)
            {
                var includeComplete = selectedProgressFilters.Contains(completeLabel);
                var includeInProgress = selectedProgressFilters.Contains(inProgressLabel);
                var includeNoProgress = selectedProgressFilters.Contains(noProgressLabel);

                filtered = filtered.Where(game => MatchesProgressBucket(
                    game,
                    includeComplete,
                    includeInProgress,
                    includeNoProgress));
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
    }
}
