using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Summaries;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static PlayniteAchievements.Services.Database.SqlNadoCacheStore;

namespace PlayniteAchievements.Services.Database
{
    internal sealed class SummaryCacheReader
    {
        private readonly SqlNadoCacheStore _store;

        internal SummaryCacheReader(SqlNadoCacheStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        private sealed class CachedGameSummaryRow
        {
            public string CacheKey { get; set; }
            public long HasAchievements { get; set; }
            public long AchievementsUnlocked { get; set; }
            public long TotalAchievements { get; set; }
            public string LastUnlockUtc { get; set; }
            public string LastUpdatedUtc { get; set; }
            public string ProviderKey { get; set; }
            public string ProviderPlatformKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public long CommonCount { get; set; }
            public long UncommonCount { get; set; }
            public long RareCount { get; set; }
            public long UltraRareCount { get; set; }
            public long TotalCommonPossible { get; set; }
            public long TotalUncommonPossible { get; set; }
            public long TotalRarePossible { get; set; }
            public long TotalUltraRarePossible { get; set; }
            public long TrophyPlatinumCount { get; set; }
            public long TrophyGoldCount { get; set; }
            public long TrophySilverCount { get; set; }
            public long TrophyBronzeCount { get; set; }
            public long TrophyPlatinumTotal { get; set; }
            public long TrophyGoldTotal { get; set; }
            public long TrophySilverTotal { get; set; }
            public long TrophyBronzeTotal { get; set; }
            public long HasUnlockedCapstone { get; set; }
        }

        private sealed class CachedRecentUnlockRow
        {
            public string CacheKey { get; set; }
            public string ProviderKey { get; set; }
            public string ProviderPlatformKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string UnlockedIconPath { get; set; }
            public string LockedIconPath { get; set; }
            public int? Points { get; set; }
            public int? ScaledPoints { get; set; }
            public string Category { get; set; }
            public string CategoryType { get; set; }
            public string TrophyType { get; set; }
            public long Hidden { get; set; }
            public long IsCapstone { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public string UnlockTimeUtc { get; set; }
            public int? ProgressNum { get; set; }
            public int? ProgressDenom { get; set; }
        }

        private sealed class CachedUnlockTimelineRow
        {
            public string CacheKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string UnlockDateUtc { get; set; }
            public long UnlockCount { get; set; }
        }

        private sealed class CachedUnlockedScoreRow
        {
            public string CacheKey { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public int? Points { get; set; }
        }

        public CachedSummaryData LoadCachedSummaryData(int recentAchievementDetailLimit = 0)
        {
            return _store.WithReadDb(db =>
            {
                var gameRows = LoadCachedGameSummaryRows(db);
                var scoreTotalsByCacheKey = LoadCachedScoreTotals(db, unlockedOnly: true);
                var possibleScoreTotalsByCacheKey = LoadCachedScoreTotals(db, unlockedOnly: false);
                var timelineRows = LoadCachedUnlockTimelineRows(db);
                var requestedRecentLimit = recentAchievementDetailLimit > 0 ? recentAchievementDetailLimit : 0;
                var boundedRecentLimit = requestedRecentLimit > 0 ? requestedRecentLimit + 1 : 0;
                var recentRows = LoadCachedRecentUnlockRows(db, boundedRecentLimit);

                var result = new CachedSummaryData();

                for (var i = 0; i < gameRows.Count; i++)
                {
                    var row = gameRows[i];
                    if (row == null)
                    {
                        continue;
                    }

                    var cacheKey = row.CacheKey?.Trim();
                    scoreTotalsByCacheKey.TryGetValue(cacheKey ?? string.Empty, out var scoreTotals);
                    possibleScoreTotalsByCacheKey.TryGetValue(cacheKey ?? string.Empty, out var possibleScoreTotals);
                    var playniteGameId = ResolveCachedPlayniteGameId(row.CacheKey, row.PlayniteGameId);
                    result.Games.Add(new CachedGameSummaryData
                    {
                        CacheKey = cacheKey,
                        PlayniteGameId = playniteGameId,
                        ProviderKey = row.ProviderKey,
                        ProviderPlatformKey = row.ProviderPlatformKey,
                        AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                        ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                        GameName = row.GameName,
                        // Mirrors the visible-projection semantics: a game whose achievements
                        // are all filtered away is hidden, like the per-game hydrated path.
                        HasAchievements = row.HasAchievements != 0 && row.TotalAchievements > 0,
                        LastUpdatedUtc = ParseUtc(row.LastUpdatedUtc) ?? DateTime.UtcNow,
                        LastUnlockUtc = ParseUtc(row.LastUnlockUtc),
                        TotalAchievements = (int)Math.Max(0, row.TotalAchievements),
                        UnlockedAchievements = (int)Math.Max(0, row.AchievementsUnlocked),
                        CollectionScore = scoreTotals.CollectionScore,
                        PrestigeScore = scoreTotals.PrestigeScore,
                        CollectionScoreTotal = possibleScoreTotals.CollectionScore,
                        PrestigeScoreTotal = possibleScoreTotals.PrestigeScore,
                        Points = scoreTotals.Points,
                        CommonCount = (int)Math.Max(0, row.CommonCount),
                        UncommonCount = (int)Math.Max(0, row.UncommonCount),
                        RareCount = (int)Math.Max(0, row.RareCount),
                        UltraRareCount = (int)Math.Max(0, row.UltraRareCount),
                        TotalCommonPossible = (int)Math.Max(0, row.TotalCommonPossible),
                        TotalUncommonPossible = (int)Math.Max(0, row.TotalUncommonPossible),
                        TotalRarePossible = (int)Math.Max(0, row.TotalRarePossible),
                        TotalUltraRarePossible = (int)Math.Max(0, row.TotalUltraRarePossible),
                        TrophyPlatinumCount = (int)Math.Max(0, row.TrophyPlatinumCount),
                        TrophyGoldCount = (int)Math.Max(0, row.TrophyGoldCount),
                        TrophySilverCount = (int)Math.Max(0, row.TrophySilverCount),
                        TrophyBronzeCount = (int)Math.Max(0, row.TrophyBronzeCount),
                        TrophyPlatinumTotal = (int)Math.Max(0, row.TrophyPlatinumTotal),
                        TrophyGoldTotal = (int)Math.Max(0, row.TrophyGoldTotal),
                        TrophySilverTotal = (int)Math.Max(0, row.TrophySilverTotal),
                        TrophyBronzeTotal = (int)Math.Max(0, row.TrophyBronzeTotal),
                        IsCompleted = ((int)Math.Max(0, row.TotalAchievements) > 0 &&
                            (int)Math.Max(0, row.AchievementsUnlocked) >= (int)Math.Max(0, row.TotalAchievements)) ||
                            row.HasUnlockedCapstone != 0
                    });
                }

                for (var i = 0; i < timelineRows.Count; i++)
                {
                    var row = timelineRows[i];
                    if (row == null || row.UnlockCount <= 0)
                    {
                        continue;
                    }

                    var unlockDate = ParseUtc(row.UnlockDateUtc)?.Date;
                    if (!unlockDate.HasValue)
                    {
                        continue;
                    }

                    Increment(result.GlobalUnlockCountsByDate, unlockDate.Value, (int)Math.Max(0, row.UnlockCount));

                    var playniteGameId = ResolveCachedPlayniteGameId(row.CacheKey, row.PlayniteGameId);
                    if (!playniteGameId.HasValue)
                    {
                        continue;
                    }

                    if (!result.UnlockCountsByDateByGame.TryGetValue(playniteGameId.Value, out var gameCounts))
                    {
                        gameCounts = new Dictionary<DateTime, int>();
                        result.UnlockCountsByDateByGame[playniteGameId.Value] = gameCounts;
                    }

                    Increment(gameCounts, unlockDate.Value, (int)Math.Max(0, row.UnlockCount));
                }

                if (requestedRecentLimit > 0 && recentRows.Count > requestedRecentLimit)
                {
                    result.HasMoreRecentUnlocks = true;
                    recentRows = recentRows.Take(requestedRecentLimit).ToList();
                }

                result.RecentUnlocks = MapRecentUnlocks(recentRows);
                return result;
            });
        }

        private static List<CachedGameSummaryRow> LoadCachedGameSummaryRows(SQLiteDatabase db)
        {
            // Headline counts are recomputed from the joined (filter-aware) definition rows
            // rather than read from the persisted ugp scalars, which aggregate over ALL
            // achievements. The AchievementFilters anti-join lives INSIDE the LEFT JOIN
            // condition so games with zero visible definitions still produce their row (the
            // consumer hides rows whose recomputed TotalAchievements is 0).
            return db.Load<CachedGameSummaryRow>(
                @"WITH LatestProgress AS (
                    SELECT
                        ugp.Id AS UserGameProgressId,
                        ugp.GameId AS GameId,
                        TRIM(ugp.CacheKey) AS CacheKey,
                        ugp.HasAchievements AS HasAchievements,
                        ugp.LastUpdatedUtc AS LastUpdatedUtc,
                        g.ProviderKey AS ProviderKey,
                        g.ProviderPlatformKey AS ProviderPlatformKey,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        g.GameName AS GameName,
                        ROW_NUMBER() OVER (
                            PARTITION BY ugp.CacheKey
                            ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                        ) AS RowNum
                    FROM UserGameProgress ugp
                    INNER JOIN Users u ON u.Id = ugp.UserId
                    INNER JOIN Games g ON g.Id = ugp.GameId
                    WHERE u.IsCurrentUser = 1
                      AND ugp.CacheKey IS NOT NULL
                      AND TRIM(ugp.CacheKey) <> ''
                )
                SELECT
                    lp.CacheKey AS CacheKey,
                    lp.HasAchievements AS HasAchievements,
                    SUM(CASE WHEN ua.Unlocked = 1 THEN 1 ELSE 0 END) AS AchievementsUnlocked,
                    COUNT(ad.Id) AS TotalAchievements,
                    MAX(CASE WHEN ua.Unlocked = 1 THEN ua.UnlockTimeUtc END) AS LastUnlockUtc,
                    lp.LastUpdatedUtc AS LastUpdatedUtc,
                    lp.ProviderKey AS ProviderKey,
                    lp.ProviderPlatformKey AS ProviderPlatformKey,
                    lp.ProviderGameId AS ProviderGameId,
                    lp.ProviderGameKey AS ProviderGameKey,
                    lp.PlayniteGameId AS PlayniteGameId,
                    lp.GameName AS GameName,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'common' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS CommonCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'uncommon' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS UncommonCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'rare' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS RareCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'ultrarare' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS UltraRareCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'common' THEN 1 ELSE 0 END) AS TotalCommonPossible,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'uncommon' THEN 1 ELSE 0 END) AS TotalUncommonPossible,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'rare' THEN 1 ELSE 0 END) AS TotalRarePossible,
                    SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'ultrarare' THEN 1 ELSE 0 END) AS TotalUltraRarePossible,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'platinum' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophyPlatinumCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'gold' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophyGoldCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'silver' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophySilverCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'bronze' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophyBronzeCount,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'platinum' THEN 1 ELSE 0 END) AS TrophyPlatinumTotal,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'gold' THEN 1 ELSE 0 END) AS TrophyGoldTotal,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'silver' THEN 1 ELSE 0 END) AS TrophySilverTotal,
                    SUM(CASE WHEN LOWER(COALESCE(ad.TrophyType, '')) = 'bronze' THEN 1 ELSE 0 END) AS TrophyBronzeTotal,
                    MAX(CASE WHEN ad.IsCapstone = 1 AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS HasUnlockedCapstone
                FROM LatestProgress lp
                LEFT JOIN AchievementDefinitions ad
                    ON ad.GameId = lp.GameId
                   AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                                   WHERE af.PlayniteGameId = lp.PlayniteGameId
                                     AND af.ApiName = ad.ApiName)
                LEFT JOIN UserAchievements ua
                    ON ua.AchievementDefinitionId = ad.Id
                   AND ua.UserGameProgressId = lp.UserGameProgressId
                WHERE lp.RowNum = 1
                GROUP BY
                    lp.CacheKey,
                    lp.HasAchievements,
                    lp.LastUpdatedUtc,
                    lp.ProviderKey,
                    lp.ProviderPlatformKey,
                    lp.ProviderGameId,
                    lp.ProviderGameKey,
                    lp.PlayniteGameId,
                    lp.GameName
                ORDER BY lp.LastUpdatedUtc DESC, lp.CacheKey;").ToList();
        }

        private static Dictionary<string, (int CollectionScore, int PrestigeScore, int Points)> LoadCachedScoreTotals(
            SQLiteDatabase db,
            bool unlockedOnly)
        {
            var userAchievementJoin = unlockedOnly
                ? @"INNER JOIN UserAchievements ua
                    ON ua.AchievementDefinitionId = ad.Id
                   AND ua.UserGameProgressId = lp.UserGameProgressId
                   AND ua.Unlocked = 1"
                : string.Empty;

            var rows = db.Load<CachedUnlockedScoreRow>(
                @"WITH LatestProgress AS (
                    SELECT
                        ugp.Id AS UserGameProgressId,
                        ugp.GameId AS GameId,
                        TRIM(ugp.CacheKey) AS CacheKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        ROW_NUMBER() OVER (
                            PARTITION BY ugp.CacheKey
                            ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                        ) AS RowNum
                    FROM UserGameProgress ugp
                    INNER JOIN Users u ON u.Id = ugp.UserId
                    INNER JOIN Games g ON g.Id = ugp.GameId
                    WHERE u.IsCurrentUser = 1
                      AND ugp.CacheKey IS NOT NULL
                      AND TRIM(ugp.CacheKey) <> ''
                )
                SELECT
                    lp.CacheKey AS CacheKey,
                    ad.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                    ad.Rarity AS Rarity,
                    ad.Points AS Points
                FROM LatestProgress lp
                INNER JOIN AchievementDefinitions ad ON ad.GameId = lp.GameId
                " + userAchievementJoin + @"
                WHERE lp.RowNum = 1
                  AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                                  WHERE af.PlayniteGameId = lp.PlayniteGameId
                                    AND af.ApiName = ad.ApiName)
                ORDER BY lp.CacheKey;").ToList();

            var totals = new Dictionary<string, (int CollectionScore, int PrestigeScore, int Points)>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var cacheKey = row?.CacheKey?.Trim();
                if (string.IsNullOrWhiteSpace(cacheKey))
                {
                    continue;
                }

                totals.TryGetValue(cacheKey, out var current);
                var rarity = ParseStoredRarity(row.Rarity);
                totals[cacheKey] = (
                    AddClamped(current.CollectionScore, AchievementScoreCalculator.GetCollectionValue(rarity)),
                    AddClamped(current.PrestigeScore, AchievementScoreCalculator.GetPrestigeValue(row.GlobalPercentUnlocked, rarity)),
                    AddClamped(current.Points, row.Points ?? 0));
            }

            return totals;
        }

        private static List<CachedUnlockTimelineRow> LoadCachedUnlockTimelineRows(SQLiteDatabase db)
        {
            return db.Load<CachedUnlockTimelineRow>(
                @"WITH LatestProgress AS (
                    SELECT
                        ugp.Id AS UserGameProgressId,
                        TRIM(ugp.CacheKey) AS CacheKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        ROW_NUMBER() OVER (
                            PARTITION BY ugp.CacheKey
                            ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                        ) AS RowNum
                    FROM UserGameProgress ugp
                    INNER JOIN Users u ON u.Id = ugp.UserId
                    INNER JOIN Games g ON g.Id = ugp.GameId
                    WHERE u.IsCurrentUser = 1
                      AND ugp.CacheKey IS NOT NULL
                      AND TRIM(ugp.CacheKey) <> ''
                )
                SELECT
                    lp.CacheKey AS CacheKey,
                    lp.PlayniteGameId AS PlayniteGameId,
                    date(ua.UnlockTimeUtc) AS UnlockDateUtc,
                    COUNT(*) AS UnlockCount
                FROM LatestProgress lp
                INNER JOIN UserAchievements ua
                    ON ua.UserGameProgressId = lp.UserGameProgressId
                   AND ua.Unlocked = 1
                   AND ua.UnlockTimeUtc IS NOT NULL
                INNER JOIN AchievementDefinitions ad ON ad.Id = ua.AchievementDefinitionId
                WHERE lp.RowNum = 1
                  AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                                  WHERE af.PlayniteGameId = lp.PlayniteGameId
                                    AND af.ApiName = ad.ApiName)
                GROUP BY
                    lp.CacheKey,
                    lp.PlayniteGameId,
                    date(ua.UnlockTimeUtc)
                ORDER BY UnlockDateUtc DESC, lp.CacheKey;").ToList();
        }

        private static List<CachedRecentUnlockRow> LoadCachedRecentUnlockRows(SQLiteDatabase db, int recentAchievementLimit)
        {
            var sql = new StringBuilder(
                @"WITH LatestProgress AS (
                    SELECT
                        ugp.Id AS UserGameProgressId,
                        TRIM(ugp.CacheKey) AS CacheKey,
                        g.ProviderKey AS ProviderKey,
                        g.ProviderPlatformKey AS ProviderPlatformKey,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        g.GameName AS GameName,
                        ROW_NUMBER() OVER (
                            PARTITION BY ugp.CacheKey
                            ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                        ) AS RowNum
                    FROM UserGameProgress ugp
                    INNER JOIN Users u ON u.Id = ugp.UserId
                    INNER JOIN Games g ON g.Id = ugp.GameId
                    WHERE u.IsCurrentUser = 1
                      AND ugp.CacheKey IS NOT NULL
                      AND TRIM(ugp.CacheKey) <> ''
                )
                SELECT
                    lp.CacheKey AS CacheKey,
                    lp.ProviderKey AS ProviderKey,
                    lp.ProviderPlatformKey AS ProviderPlatformKey,
                    lp.ProviderGameId AS ProviderGameId,
                    lp.ProviderGameKey AS ProviderGameKey,
                    lp.PlayniteGameId AS PlayniteGameId,
                    lp.GameName AS GameName,
                    ad.ApiName AS ApiName,
                    ad.DisplayName AS DisplayName,
                    ad.Description AS Description,
                    ad.UnlockedIconPath AS UnlockedIconPath,
                    ad.LockedIconPath AS LockedIconPath,
                    ad.Points AS Points,
                    ad.ScaledPoints AS ScaledPoints,
                    ad.Category AS Category,
                    ad.CategoryType AS CategoryType,
                    ad.TrophyType AS TrophyType,
                    ad.Hidden AS Hidden,
                    ad.IsCapstone AS IsCapstone,
                    ad.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                    ad.Rarity AS Rarity,
                    ua.UnlockTimeUtc AS UnlockTimeUtc,
                    ua.ProgressNum AS ProgressNum,
                    ua.ProgressDenom AS ProgressDenom
                FROM LatestProgress lp
                INNER JOIN UserAchievements ua
                    ON ua.UserGameProgressId = lp.UserGameProgressId
                   AND ua.Unlocked = 1
                   AND ua.UnlockTimeUtc IS NOT NULL
                INNER JOIN AchievementDefinitions ad ON ad.Id = ua.AchievementDefinitionId
                WHERE lp.RowNum = 1
                  AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                                  WHERE af.PlayniteGameId = lp.PlayniteGameId
                                    AND af.ApiName = ad.ApiName)
                ORDER BY ua.UnlockTimeUtc DESC, lp.CacheKey, ad.Id");

            if (recentAchievementLimit > 0)
            {
                sql.Append(" LIMIT ?");
                sql.Append(';');
                return db.Load<CachedRecentUnlockRow>(sql.ToString(), recentAchievementLimit).ToList();
            }

            sql.Append(';');
            return db.Load<CachedRecentUnlockRow>(sql.ToString()).ToList();
        }

        private List<CachedRecentUnlockData> MapRecentUnlocks(
            IEnumerable<CachedRecentUnlockRow> rows)
        {
            var result = new List<CachedRecentUnlockData>();
            if (rows == null)
            {
                return result;
            }

            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ApiName))
                {
                    continue;
                }

                result.Add(new CachedRecentUnlockData
                {
                    CacheKey = row.CacheKey?.Trim(),
                    PlayniteGameId = ResolveCachedPlayniteGameId(row.CacheKey, row.PlayniteGameId),
                    ProviderKey = row.ProviderKey,
                    ProviderPlatformKey = row.ProviderPlatformKey,
                    AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                    ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                    GameName = row.GameName,
                    ApiName = row.ApiName,
                    DisplayName = row.DisplayName,
                    Description = row.Description,
                    UnlockedIconPath = _store.MakeAbsolutePath(row.UnlockedIconPath),
                    LockedIconPath = _store.MakeAbsolutePath(row.LockedIconPath),
                    Points = row.Points,
                    ScaledPoints = row.ScaledPoints,
                    Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category),
                    CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(row.CategoryType),
                    TrophyType = row.TrophyType,
                    Hidden = row.Hidden != 0,
                    IsCapstone = row.IsCapstone != 0,
                    GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                    Rarity = ParseStoredRarity(row.Rarity),
                    UnlockTimeUtc = ParseUtc(row.UnlockTimeUtc),
                    ProgressNum = row.ProgressNum,
                    ProgressDenom = row.ProgressDenom
                });
            }

            return result;
        }

        private static void Increment(IDictionary<DateTime, int> counts, DateTime date, int amount)
        {
            if (counts == null || amount <= 0)
            {
                return;
            }

            if (counts.TryGetValue(date, out var existing))
            {
                counts[date] = existing + amount;
            }
            else
            {
                counts[date] = amount;
            }
        }

        private static int AddClamped(int current, int value)
        {
            if (value <= 0)
            {
                return current;
            }

            if (current > int.MaxValue - value)
            {
                return int.MaxValue;
            }

            return current + value;
        }
    }
}
