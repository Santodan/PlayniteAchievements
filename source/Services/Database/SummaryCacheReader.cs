using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// The current user's library summary, read from the resolved rows with the summary rules:
    /// every override, authored achievement, capstone set, tag edit and exclusion is already
    /// applied by the view.
    /// </summary>
    internal static class SummaryCacheReader
    {
        /// <param name="identities">
        /// Each cached game's identity and whether its progress records achievements, which a game
        /// with authored achievements has regardless; the most recently updated one per Playnite
        /// game is kept.
        /// </param>
        /// <param name="authoredGame">The identity of a game with only authored achievements.</param>
        /// <param name="limit">The newest dated unlocks to read into <see cref="CachedSummaryData.RecentUnlocks"/>; 0 for none.</param>
        /// <param name="excludedGameIds">Games left out on top of the summary rules.</param>
        /// <param name="days">Whether to read <see cref="CachedSummaryData.Unlocks"/> and the day counts.</param>
        public static CachedSummaryData Load(
            SQLiteDatabase db,
            IEnumerable<CachedGameSummaryData> identities,
            Func<Guid, CachedGameSummaryData> authoredGame,
            int limit,
            IEnumerable<Guid> excludedGameIds,
            Func<string, string> absolutePath,
            bool days = true)
        {
            var query = AchievementQuery.CurrentUser.WithSummaryRules().ExceptGames(excludedGameIds);
            var known = new Dictionary<Guid, CachedGameSummaryData>();
            foreach (var game in identities ?? Enumerable.Empty<CachedGameSummaryData>())
            {
                if (game?.PlayniteGameId is Guid id &&
                    (!known.TryGetValue(id, out var other) || game.LastUpdatedUtc > other.LastUpdatedUtc))
                {
                    known[id] = game;
                }
            }

            var result = new CachedSummaryData();
            var games = new Dictionary<Guid, CachedGameSummaryData>();
            foreach (var group in AchievementReadService.Totals(db, query, AchievementGroup.Tally)
                         .GroupBy(tally => Guid.TryParse(Part(tally, 0), out var id) ? id : Guid.Empty)
                         .Where(group => group.Key != Guid.Empty))
            {
                var game = known.TryGetValue(group.Key, out var identity) ? identity : authoredGame?.Invoke(group.Key) ?? new CachedGameSummaryData();
                game.PlayniteGameId = group.Key;
                game.CacheKey ??= group.Key.ToString("D");
                games[group.Key] = game;
                result.Games.Add(game);

                var totals = new AchievementTotalsRecord();
                foreach (var tally in group)
                {
                    AchievementReadService.AddTotals(totals, tally);
                    var capstone = Part(tally, 4) == "1";
                    var platinum = Part(tally, 3) == "platinum";
                    game.CapstonesNotPlatinum += capstone && !platinum ? (int)tally.Count : 0;
                    game.PlatinumsNotCapstone += !capstone && platinum ? (int)tally.Count : 0;
                }

                game.Tallies = Tallies(group);
                game.HasAchievements |= totals.AuthoredCount > 0;
                game.TotalAchievements = (int)totals.Count;
                game.UnlockedAchievements = (int)totals.UnlockedCount;
                game.CollectionScore = (int)totals.CollectionScore;
                game.CollectionScoreTotal = (int)totals.CollectionScoreTotal;
                game.PrestigeScore = (int)totals.PrestigeScore;
                game.PrestigeScoreTotal = (int)totals.PrestigeScoreTotal;
                game.Points = (int)totals.Points;
                game.PlatformScorePoints = (int)totals.PlatformScorePoints;
                game.PlatformScorePointsTotal = (int)totals.PointsTotal;
                game.CommonCount = (int)totals.CommonUnlocked;
                game.UncommonCount = (int)totals.UncommonUnlocked;
                game.RareCount = (int)totals.RareUnlocked;
                game.UltraRareCount = (int)totals.UltraRareUnlocked;
                game.TotalCommonPossible = (int)totals.CommonCount;
                game.TotalUncommonPossible = (int)totals.UncommonCount;
                game.TotalRarePossible = (int)totals.RareCount;
                game.TotalUltraRarePossible = (int)totals.UltraRareCount;
                game.TrophyPlatinumCount = (int)totals.PlatinumUnlocked;
                game.TrophyGoldCount = (int)totals.GoldUnlocked;
                game.TrophySilverCount = (int)totals.SilverUnlocked;
                game.TrophyBronzeCount = (int)totals.BronzeUnlocked;
                game.TrophyPlatinumTotal = (int)totals.PlatinumCount;
                game.TrophyGoldTotal = (int)totals.GoldCount;
                game.TrophySilverTotal = (int)totals.SilverCount;
                game.TrophyBronzeTotal = (int)totals.BronzeCount;
                game.CapstoneTotal = (int)totals.CapstoneCount;
                game.CapstoneUnlocked = (int)totals.CapstoneUnlocked;
                game.LastUnlockUtc = AchievementSqlFunctions.ParseUtc(totals.LastUnlockUtc);
                game.LastCapstoneUnlockUtc = AchievementSqlFunctions.ParseUtc(totals.LastCapstoneUnlockUtc);
                game.FirstPlatinumUnlockUtc = AchievementSqlFunctions.ParseUtc(totals.FirstPlatinumUnlockUtc);

                // Finishing takes every capstone, not any one of them.
                game.IsCompleted = (game.UnlockedAchievements >= game.TotalAchievements) ||
                                   (game.CapstoneTotal > 0 && game.CapstoneUnlocked >= game.CapstoneTotal);
            }

            // Days are local calendar days, as UnlockDayCounts.DayOf gives them.
            if (days)
            {
                result.Unlocks = AchievementReadService.Totals(db, query.WhereUnlocked(true), AchievementGroup.DayTally)
                    .Select(Overview.UnlockFact.From)
                    .Where(fact => fact != null)
                    .ToList();
                Overview.UnlockFact.CountDays(result.Unlocks, result.GlobalUnlockCountsByDate, result.UnlockCountsByDateByGame);
            }
            if (limit > 0)
            {
                var rows = AchievementReadService.Rows(db, query.WhereUnlocked(true).WhereUnlockTimeKnown()
                        .OrderBy(AchievementSort.UnlockTime, descending: true)
                        .Page(limit + 1))
                    .Select(row => MapRow(row, games, absolutePath))
                    .ToList();
                result.RecentUnlocks = rows.Take(limit).ToList();
            }

            return result;
        }

        /// <summary>One game's tallies from its <see cref="AchievementGroup.Tally"/> totals; null when it counts none.</summary>
        public static AchievementTally[] Tallies(IEnumerable<AchievementTotalsRecord> tallies)
        {
            var builder = new AchievementTallyBuilder();
            foreach (var tally in tallies)
            {
                builder.Add(
                    Part(tally, 1),
                    (RarityTier)Enum.Parse(typeof(RarityTier), Part(tally, 2)),
                    Part(tally, 3),
                    Part(tally, 4) == "1",
                    (int)tally.UnlockedCount,
                    (int)tally.Count);
            }

            return builder.Build();
        }

        // Game, tags, tier, trophy grade, capstone flag.
        private static string Part(AchievementTotalsRecord tally, int index) => tally.Key.Split('\u001f')[index];

        // Authored rows have no provider game, so they take their game's identity when known; the
        // row read names the same one otherwise.
        internal static CachedRecentUnlockData MapRow(
            AchievementRowRecord row,
            IReadOnlyDictionary<Guid, CachedGameSummaryData> games,
            Func<string, string> absolutePath)
        {
            var gameId = Guid.TryParse(row.PlayniteGameId, out var id) ? id : (Guid?)null;
            var game = gameId.HasValue && games != null && games.TryGetValue(gameId.Value, out var known) ? known : null;
            var authored = row.IsAuthored != 0 && game != null;
            return new CachedRecentUnlockData
            {
                CacheKey = game?.CacheKey,
                PlayniteGameId = gameId,
                ProviderKey = authored ? game?.ProviderKey : row.ProviderKey,
                ProviderPlatformKey = authored ? game?.ProviderPlatformKey : row.ProviderPlatformKey,
                AppId = authored ? game?.AppId ?? 0 : (int)Math.Max(0, row.ProviderGameId ?? 0),
                ProviderGameKey = authored ? game?.ProviderGameKey : string.IsNullOrWhiteSpace(row.ProviderGameKey) ? null : row.ProviderGameKey.Trim(),
                GameName = row.GameName ?? game?.GameName,
                ApiName = row.ApiName,
                DisplayName = row.DisplayName,
                Description = row.Description,
                UnlockedIconPath = absolutePath(row.UnlockedIconPath),
                LockedIconPath = absolutePath(row.LockedIconPath),
                Points = row.Points,
                ScaledPoints = row.ScaledPoints,
                Category = CategoryPathHelper.NormalizePath(AchievementCategoryHelper.NormalizeCategoryOrDefault(row.Category)),
                ProviderCategory = AchievementCategoryHelper.NormalizeCategoryOrDefault(row.ProviderCategory),
                Tags = AchievementTagHelper.Normalize(row.Tags),
                CategoryKind = CategoryKindHelper.Normalize(row.CategoryKind),
                TrophyType = row.TrophyType,
                Hidden = row.Hidden != 0,
                IsCapstone = row.IsCapstone != 0,
                AchievementNote = row.Note,
                GoalOrder = (int?)row.GoalOrder,
                CategoryOrder = (int?)row.CategoryOrder,
                CategoryArtPath = absolutePath(row.CategoryArtPath),
                GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                Rarity = AchievementSqlFunctions.ParseStoredRarity(row.Rarity),
                Unlocked = row.Unlocked != 0,
                UnlockTimeUtc = AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc),
                ProgressNum = row.ProgressNum,
                ProgressDenom = row.ProgressDenom
            };
        }
    }
}
