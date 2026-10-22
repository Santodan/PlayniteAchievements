using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// Splits an overview snapshot's unlock day counts into one stacked timeline series per
    /// platform, keyed by each game's effective provider key like the "Achievements by Platform"
    /// pie, and colored and named through <see cref="ProviderRegistry"/> so recolors apply.
    /// </summary>
    public static class TimelinePlatformSeries
    {
        public const string UnknownKey = "Unknown";

        /// <summary>
        /// Every game in the snapshot, or only the games <paramref name="includeGame"/> accepts.
        /// Unfiltered, unlocks no game accounts for stack under Unknown; filtered, they are left
        /// out because they cannot match a filter.
        /// </summary>
        public static IReadOnlyList<TimelineSeriesCounts> FromSnapshot(
            OverviewDataSnapshot snapshot,
            Func<Guid, bool> includeGame = null)
        {
            if (snapshot == null)
            {
                return Array.Empty<TimelineSeriesCounts>();
            }

            var keyByGame = BuildKeyByGame(snapshot);
            var groups = UnlockDayCounts.GroupByKey(
                includeGame == null ? snapshot.GlobalUnlockCountsByDate : null,
                FilterGames(snapshot.UnlockCountsByDateByGame, includeGame),
                gameId => keyByGame.TryGetValue(gameId, out var key) ? key : null,
                UnknownKey);
            return ToSeries(groups);
        }

        /// <summary>
        /// One count per completed game, on the day it was completed: when its last capstone was
        /// unlocked for a game its capstones finished, otherwise its last unlock. No completion
        /// date is stored, so a game finished by its capstones whose capstone rows the snapshot
        /// lacks falls back to its last unlock too.
        /// </summary>
        public static Dictionary<Guid, Dictionary<DateTime, int>> CompletionCountsByGame(OverviewDataSnapshot snapshot)
        {
            var lastCapstoneUnlock = new Dictionary<Guid, DateTime>();
            var rows = snapshot?.Achievements;
            for (var i = 0; i < (rows?.Count ?? 0); i++)
            {
                var row = rows[i];
                if (row?.Unlocked == true && row.IsCapstone &&
                    row.UnlockTimeUtc.HasValue && row.PlayniteGameId is Guid rowGame &&
                    (!lastCapstoneUnlock.TryGetValue(rowGame, out var latest) || row.UnlockTimeUtc.Value > latest))
                {
                    lastCapstoneUnlock[rowGame] = row.UnlockTimeUtc.Value;
                }
            }

            var result = new Dictionary<Guid, Dictionary<DateTime, int>>();
            var games = snapshot?.GameSummaries;
            for (var i = 0; i < (games?.Count ?? 0); i++)
            {
                var game = games[i];
                if (game?.IsCompleted != true || !(game.PlayniteGameId is Guid gameId))
                {
                    continue;
                }

                var finishedByCapstones = game.CapstoneTotal > 0 && game.CapstoneUnlocked >= game.CapstoneTotal;
                var completedUtc = finishedByCapstones && lastCapstoneUnlock.TryGetValue(gameId, out var capstoneUtc)
                    ? capstoneUtc
                    : game.LastUnlockUtc;
                if (completedUtc.HasValue)
                {
                    result[gameId] = new Dictionary<DateTime, int> { [UnlockDayCounts.DayOf(completedUtc.Value)] = 1 };
                }
            }

            return result;
        }

        /// <summary>
        /// <paramref name="byGame"/>'s day counts stacked one series per platform, the platforms
        /// read from the snapshot's games; days no listed game accounts for are left out.
        /// </summary>
        public static IReadOnlyList<TimelineSeriesCounts> FromCounts(
            OverviewDataSnapshot snapshot,
            Dictionary<Guid, Dictionary<DateTime, int>> byGame)
        {
            if (snapshot == null)
            {
                return Array.Empty<TimelineSeriesCounts>();
            }

            var keyByGame = BuildKeyByGame(snapshot);
            return ToSeries(UnlockDayCounts.GroupByKey(
                null,
                byGame,
                gameId => keyByGame.TryGetValue(gameId, out var key) ? key : null,
                UnknownKey));
        }

        /// <summary>Day counts summed over every game in <paramref name="byGame"/>.</summary>
        public static Dictionary<DateTime, int> Sum(IReadOnlyDictionary<Guid, Dictionary<DateTime, int>> byGame)
        {
            var total = new Dictionary<DateTime, int>();
            foreach (var game in byGame?.Values ?? Enumerable.Empty<Dictionary<DateTime, int>>())
            {
                foreach (var day in game ?? new Dictionary<DateTime, int>())
                {
                    if (day.Value > 0)
                    {
                        total[day.Key] = total.TryGetValue(day.Key, out var existing) ? existing + day.Value : day.Value;
                    }
                }
            }

            return total;
        }

        /// <summary>Day counts summed over the games <paramref name="includeGame"/> accepts.</summary>
        public static Dictionary<DateTime, int> SumGames(OverviewDataSnapshot snapshot, Func<Guid, bool> includeGame)
        {
            var total = new Dictionary<DateTime, int>();
            var byGame = FilterGames(snapshot?.UnlockCountsByDateByGame, includeGame);
            if (byGame == null)
            {
                return total;
            }

            foreach (var game in byGame.Values)
            {
                foreach (var day in game)
                {
                    if (day.Value > 0)
                    {
                        total[day.Key] = total.TryGetValue(day.Key, out var existing) ? existing + day.Value : day.Value;
                    }
                }
            }

            return total;
        }

        private static IReadOnlyDictionary<Guid, Dictionary<DateTime, int>> FilterGames(
            Dictionary<Guid, Dictionary<DateTime, int>> byGame,
            Func<Guid, bool> includeGame)
        {
            if (byGame == null || includeGame == null)
            {
                return byGame;
            }

            return byGame
                .Where(pair => pair.Value != null && includeGame(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        /// <summary>One game's counts as a single series under its platform.</summary>
        public static IReadOnlyList<TimelineSeriesCounts> ForGame(OverviewDataSnapshot snapshot, Guid gameId)
        {
            Dictionary<DateTime, int> counts = null;
            snapshot?.UnlockCountsByDateByGame?.TryGetValue(gameId, out counts);
            var key = snapshot?.GameSummaries?
                .FirstOrDefault(game => game?.PlayniteGameId == gameId)?
                .ProviderKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = UnknownKey;
            }

            return ToSeries(new Dictionary<string, Dictionary<DateTime, int>>
            {
                [key] = counts ?? new Dictionary<DateTime, int>()
            });
        }

        private static Dictionary<Guid, string> BuildKeyByGame(OverviewDataSnapshot snapshot)
        {
            var keyByGame = new Dictionary<Guid, string>();
            if (snapshot.GameSummaries == null)
            {
                return keyByGame;
            }

            foreach (var game in snapshot.GameSummaries)
            {
                if (game?.PlayniteGameId.HasValue == true && !keyByGame.ContainsKey(game.PlayniteGameId.Value))
                {
                    keyByGame[game.PlayniteGameId.Value] = game.ProviderKey;
                }
            }

            return keyByGame;
        }

        private static IReadOnlyList<TimelineSeriesCounts> ToSeries(Dictionary<string, Dictionary<DateTime, int>> groups)
        {
            return groups
                .Select(group => new TimelineSeriesCounts(
                    group.Key,
                    ProviderRegistry.GetLocalizedName(group.Key),
                    ProviderRegistry.ResolveProviderVisualsOrFallback(group.Key).colorHex,
                    group.Value))
                .ToList();
        }
    }
}
