using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

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
        /// One count per completed game, on the day it was first completed by any of: its last
        /// capstone unlocking (every capstone unlocked), its last achievement unlocking (100%), or
        /// a platinum unlocking. No completion date is stored, so the day is read from the unlocks.
        /// </summary>
        public static Dictionary<Guid, Dictionary<DateTime, int>> CompletionCountsByGame(OverviewDataSnapshot snapshot) =>
            CompletionFinishes(snapshot).ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<DateTime, int> { [UnlockDayCounts.DayOf(pair.Value)] = 1 });

        /// <summary><paramref name="snapshot"/> seen as completions: each completed game counted on the day it finished.</summary>
        public static OverviewDataSnapshot CompletionSnapshot(OverviewDataSnapshot snapshot)
        {
            var byGame = CompletionCountsByGame(snapshot);
            return new OverviewDataSnapshot
            {
                GameSummaries = snapshot?.GameSummaries ?? new List<GameSummaryItem>(),
                GlobalUnlockCountsByDate = Sum(byGame),
                UnlockCountsByDateByGame = byGame,
                CurrentUserIdentities = snapshot?.CurrentUserIdentities
            };
        }

        /// <summary>Each completed game's earliest finish: the unlock time of the last capstone, the platinum, or the unlock that reached 100%.</summary>
        public static Dictionary<Guid, DateTime> CompletionFinishes(OverviewDataSnapshot snapshot)
        {
            var result = new Dictionary<Guid, DateTime>();
            foreach (var game in snapshot?.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (game == null || !(game.PlayniteGameId is Guid gameId))
                {
                    continue;
                }

                DateTime? finish = null;
                void Consider(DateTime? utc)
                {
                    if (utc.HasValue && (!finish.HasValue || utc.Value < finish.Value))
                    {
                        finish = utc;
                    }
                }

                if (game.CapstoneTotal > 0 && game.CapstoneUnlocked >= game.CapstoneTotal)
                {
                    Consider(game.LastCapstoneUnlockUtc);
                }

                if (game.TotalAchievements > 0 && game.UnlockedAchievements >= game.TotalAchievements)
                {
                    Consider(game.LastUnlockUtc);
                }

                Consider(game.FirstPlatinumUnlockUtc);

                // Finished by its capstones, but none has a time: the last unlock stands in.
                if (!finish.HasValue && game.IsCompleted)
                {
                    Consider(game.LastUnlockUtc);
                }

                if (finish.HasValue)
                {
                    result[gameId] = finish.Value;
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
