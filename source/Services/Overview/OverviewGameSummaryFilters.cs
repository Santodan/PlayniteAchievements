using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

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

        /// <summary>
        /// Filters games by the active provider/platform selection. A game passes when its provider
        /// is fully selected (all platforms), or the provider is partially selected and the game's
        /// platforms overlap the selected set. An empty selection leaves the games unfiltered.
        /// </summary>
        public static IEnumerable<TGame> ApplyProviderPlatformFilter<TGame>(
            IEnumerable<TGame> games,
            IEnumerable<ProviderFilterGroup> providerGroups)
            where TGame : GameSummaryItem
        {
            var filtered = games ?? Enumerable.Empty<TGame>();

            var activeGroups = (providerGroups ?? Enumerable.Empty<ProviderFilterGroup>())
                .Where(group => group != null && group.HasAnySelected)
                .ToList();
            if (activeGroups.Count == 0)
            {
                return filtered;
            }

            var fullySelectedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedPlatformsByProvider =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in activeGroups)
            {
                if (group.IsFullySelected)
                {
                    fullySelectedProviders.Add(group.ProviderKey);
                }
                else
                {
                    selectedPlatformsByProvider[group.ProviderKey] =
                        new HashSet<string>(group.SelectedPlatformNames, StringComparer.OrdinalIgnoreCase);
                }
            }

            return filtered.Where(game =>
                MatchesProviderPlatform(game, fullySelectedProviders, selectedPlatformsByProvider));
        }

        public static bool MatchesProviderPlatform(
            GameSummaryItem game,
            ISet<string> fullySelectedProviders,
            IReadOnlyDictionary<string, HashSet<string>> selectedPlatformsByProvider)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.ProviderKey))
            {
                return false;
            }

            // Fully-selected provider: every game of that provider passes, even ones with no
            // platform metadata.
            if (fullySelectedProviders != null && fullySelectedProviders.Contains(game.ProviderKey))
            {
                return true;
            }

            // Partially-selected provider: only games whose platforms overlap the selection.
            if (selectedPlatformsByProvider != null &&
                selectedPlatformsByProvider.TryGetValue(game.ProviderKey, out var selectedPlatforms))
            {
                return game.Platforms != null && game.Platforms.Any(selectedPlatforms.Contains);
            }

            return false;
        }

        /// <summary>
        /// Builds the dropdown box text: the placeholder when nothing is selected, the provider name
        /// for a fully-selected provider, or the selected platform names for a partial selection.
        /// </summary>
        public static string BuildProviderFilterText(
            IEnumerable<ProviderFilterGroup> providerGroups,
            string placeholder)
        {
            var groups = (providerGroups ?? Enumerable.Empty<ProviderFilterGroup>())
                .Where(group => group != null && group.HasAnySelected)
                .ToList();
            if (groups.Count == 0)
            {
                return placeholder;
            }

            var parts = new List<string>();
            foreach (var group in groups)
            {
                if (group.IsFullySelected)
                {
                    parts.Add(group.DisplayName);
                }
                else
                {
                    parts.AddRange(group.SelectedPlatformNames);
                }
            }

            return parts.Count > 0 ? string.Join(", ", parts) : placeholder;
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
