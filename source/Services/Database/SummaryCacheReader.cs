using PlayniteAchievements.Common;
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
            public long CapstoneTotal { get; set; }
            public long CapstoneUnlocked { get; set; }
            public long CapstonesNotPlatinum { get; set; }
            public long PlatinumsNotCapstone { get; set; }
            public string PlatinumApiNames { get; set; }
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
            public long Unlocked { get; set; }
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

        /// <summary>
        /// Narrows every query below to one game. Appended after the shared "WHERE lp.RowNum = 1"
        /// anchor -- deliberately in the outer WHERE rather than inside the LatestProgress CTE,
        /// so it cannot change which row that CTE's ROW_NUMBER picks per cache key.
        /// <para>
        /// The second arm is required, not defensive: a Games row may carry no PlayniteGameId, in
        /// which case the cache key is itself the game's GUID (see
        /// SqlNadoCacheStore.ResolveCachedPlayniteGameId). Without it a scoped read would silently
        /// return nothing for those games.
        /// </para>
        /// <para>
        /// COLLATE NOCASE because SQLite compares TEXT case-sensitively while Guid.TryParse -- the
        /// comparison every other reader of this column performs -- does not. A writer storing an
        /// uppercase GUID would otherwise make the scoped read quietly miss.
        /// </para>
        /// </summary>
        private const string GameScopePredicate = @"
                  AND (TRIM(lp.PlayniteGameId) = ? COLLATE NOCASE
                       OR (COALESCE(TRIM(lp.PlayniteGameId), '') = ''
                           AND TRIM(lp.CacheKey) = ? COLLATE NOCASE))";

        private static string ScopeSql(Guid? scopeGameId)
        {
            return scopeGameId.HasValue ? GameScopePredicate : string.Empty;
        }

        /// <summary>
        /// The two positional parameters <see cref="GameScopePredicate"/> binds, or an empty set
        /// when the read is unscoped. Both arms match on the same GUID string.
        /// </summary>
        private static object[] ScopeArgs(Guid? scopeGameId)
        {
            if (!scopeGameId.HasValue)
            {
                return Array.Empty<object>();
            }

            var id = scopeGameId.Value.ToString();
            return new object[] { id, id };
        }

        /// <summary>
        /// Everything one game contributes to the library summary, in the same shape and built by
        /// the same code as the whole-library read. Always unbounded (limit 0): a bounded read
        /// trims rows library-wide, which one game's slice cannot reproduce.
        /// </summary>
        public CachedSummaryData LoadCachedSummaryDataForGame(Guid playniteGameId)
        {
            return playniteGameId == Guid.Empty
                ? new CachedSummaryData()
                : LoadCachedSummaryData(0, playniteGameId);
        }

        private sealed class UnlockedApiNameRow
        {
            public string CacheKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string ApiName { get; set; }
        }

        /// <summary>
        /// ApiNames per game allowed by the parameter-count limit of one query.
        /// </summary>
        private const int UnlockedApiNameChunkSize = 400;

        /// <summary>
        /// Which of the asked-for achievements the current user has unlocked, per game.
        /// </summary>
        /// <remarks>
        /// For the stored-capstone correction on a bounded summary read. That read carries only
        /// the most recent unlocks, so it cannot say whether an older capstone was earned; asking
        /// for exactly the capstones keeps the answer to a handful of rows per game instead of
        /// every unlock in the library, which the bound exists to avoid.
        /// </remarks>
        public Dictionary<Guid, HashSet<string>> LoadUnlockedApiNames(
            IReadOnlyDictionary<Guid, HashSet<string>> wanted)
        {
            var result = new Dictionary<Guid, HashSet<string>>();
            if (wanted == null || wanted.Count == 0)
            {
                return result;
            }

            var apiNames = wanted.Values
                .Where(set => set != null)
                .SelectMany(set => set)
                .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (apiNames.Count == 0)
            {
                return result;
            }

            return _store.WithReadDb(db =>
            {
                for (var offset = 0; offset < apiNames.Count; offset += UnlockedApiNameChunkSize)
                {
                    var chunk = apiNames.Skip(offset).Take(UnlockedApiNameChunkSize).ToList();
                    var placeholders = string.Join(", ", chunk.Select(_ => "?"));
                    var rows = db.Load<UnlockedApiNameRow>(
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
                            ad.ApiName AS ApiName
                        FROM LatestProgress lp
                        INNER JOIN UserAchievements ua
                            ON ua.UserGameProgressId = lp.UserGameProgressId
                           AND ua.Unlocked = 1
                        INNER JOIN AchievementDefinitions ad ON ad.Id = ua.AchievementDefinitionId
                        WHERE lp.RowNum = 1
                          AND ad.ApiName IN (" + placeholders + ");",
                        chunk.Cast<object>().ToArray()).ToList();

                    foreach (var row in rows)
                    {
                        var playniteGameId = ResolveCachedPlayniteGameId(row?.CacheKey, row?.PlayniteGameId);
                        var apiName = row?.ApiName?.Trim();
                        if (!playniteGameId.HasValue ||
                            string.IsNullOrWhiteSpace(apiName) ||
                            !wanted.TryGetValue(playniteGameId.Value, out var asked) ||
                            asked?.Contains(apiName) != true)
                        {
                            continue;
                        }

                        if (!result.TryGetValue(playniteGameId.Value, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            result[playniteGameId.Value] = set;
                        }

                        set.Add(apiName);
                    }
                }

                return result;
            });
        }

        public CachedSummaryData LoadCachedSummaryData(
            int recentAchievementDetailLimit = 0,
            Guid? scopeGameId = null)
        {
            return _store.WithReadDb(db =>
            {
                // One scope per query. This read is five whole-library queries and only its
                // total was ever measured, so which of them owns a multi-second read was a
                // guess. Each scope carries its row count, because the duration alone cannot
                // separate "slow because of volume" from "slow because of a sort".
                var logger = _store._logger;

                // Scoped reads get their own tag suffix so a log separates the cheap per-game
                // patch reads from the whole-library rebuilds they replaced.
                var tagSuffix = scopeGameId.HasValue ? ".Scoped" : string.Empty;

                List<CachedGameSummaryRow> gameRows;
                using (var scope = PerfScope.Start(logger, "Cache.Summary.GameRows" + tagSuffix, thresholdMs: 25))
                {
                    gameRows = LoadCachedGameSummaryRows(db, scopeGameId);
                    scope?.SetContext("rows=" + gameRows.Count);
                }

                Dictionary<string, (int CollectionScore, int PrestigeScore, int Points)> scoreTotalsByCacheKey;
                using (var scope = PerfScope.Start(logger, "Cache.Summary.ScoreTotalsUnlocked" + tagSuffix, thresholdMs: 25))
                {
                    scoreTotalsByCacheKey = LoadCachedScoreTotals(db, unlockedOnly: true, scopeGameId: scopeGameId);
                    scope?.SetContext("games=" + scoreTotalsByCacheKey.Count);
                }

                Dictionary<string, (int CollectionScore, int PrestigeScore, int Points)> possibleScoreTotalsByCacheKey;
                using (var scope = PerfScope.Start(logger, "Cache.Summary.ScoreTotalsPossible" + tagSuffix, thresholdMs: 25))
                {
                    possibleScoreTotalsByCacheKey = LoadCachedScoreTotals(db, unlockedOnly: false, scopeGameId: scopeGameId);
                    scope?.SetContext("games=" + possibleScoreTotalsByCacheKey.Count);
                }

                List<CachedUnlockTimelineRow> timelineRows;
                using (var scope = PerfScope.Start(logger, "Cache.Summary.UnlockTimeline" + tagSuffix, thresholdMs: 25))
                {
                    timelineRows = LoadCachedUnlockTimelineRows(db, scopeGameId);
                    scope?.SetContext("rows=" + timelineRows.Count);
                }

                var requestedRecentLimit = recentAchievementDetailLimit > 0 ? recentAchievementDetailLimit : 0;
                var boundedRecentLimit = requestedRecentLimit > 0 ? requestedRecentLimit + 1 : 0;

                List<CachedRecentUnlockRow> recentRows;
                using (var scope = PerfScope.Start(logger, "Cache.Summary.RecentUnlocks" + tagSuffix, thresholdMs: 25))
                {
                    recentRows = LoadCachedRecentUnlockRows(
                        db,
                        boundedRecentLimit,
                        includeAllUnlockedAchievements: requestedRecentLimit == 0,
                        scopeGameId: scopeGameId);
                    // limit=0 is the overview's request and takes the unbounded variant: every
                    // unlocked row with all definition columns and no LIMIT. The row count is the
                    // point of this scope.
                    scope?.SetContext("rows=" + recentRows.Count + " limit=" + boundedRecentLimit);
                }

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
                        CapstoneTotal = (int)Math.Max(0, row.CapstoneTotal),
                        CapstoneUnlocked = (int)Math.Max(0, row.CapstoneUnlocked),
                        CapstonesNotPlatinum = (int)Math.Max(0, row.CapstonesNotPlatinum),
                        PlatinumsNotCapstone = (int)Math.Max(0, row.PlatinumsNotCapstone),
                        PlatinumApiNames = row.PlatinumApiNames,
                        // Finishing takes every capstone, not any one of them: a platinum earned
                        // while a DLC pack is still open has not finished the game. These counts
                        // are the provider seed; a game whose capstones the user has edited is
                        // corrected from its stored set once custom data is applied.
                        IsCompleted = ((int)Math.Max(0, row.TotalAchievements) > 0 &&
                            (int)Math.Max(0, row.AchievementsUnlocked) >= (int)Math.Max(0, row.TotalAchievements)) ||
                            (row.CapstoneTotal > 0 && row.CapstoneUnlocked >= row.CapstoneTotal)
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

                var mappedAchievements = MapAchievementDetails(recentRows);
                if (requestedRecentLimit == 0)
                {
                    result.Achievements = mappedAchievements;
                    result.RecentUnlocks = mappedAchievements
                        .Where(item => item?.Unlocked == true && item.UnlockTimeUtc.HasValue)
                        .ToList();
                }
                else
                {
                    result.RecentUnlocks = mappedAchievements;
                }

                // Canary on the whole-library summary set. This is the largest object the read
                // produces -- thousands of rows, and on a reported library about the size of the
                // ~2.7 MB that each whole-library warm was measured to retain after a forced full
                // collection. The snapshot built from it is already tracked and reads zero alive,
                // so if a live count climbs here the retainer is holding the source data rather
                // than the projection. One entry per read, not per row.
                Common.LeakWatch.Track("CachedSummaryData", result);

                return result;
            });
        }

        private static List<CachedGameSummaryRow> LoadCachedGameSummaryRows(
            SQLiteDatabase db,
            Guid? scopeGameId = null)
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
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'platinum' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophyPlatinumCount,
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'gold' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophyGoldCount,
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'silver' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophySilverCount,
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'bronze' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS TrophyBronzeCount,
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'platinum' THEN 1 ELSE 0 END) AS TrophyPlatinumTotal,
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'gold' THEN 1 ELSE 0 END) AS TrophyGoldTotal,
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'silver' THEN 1 ELSE 0 END) AS TrophySilverTotal,
                    SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'bronze' THEN 1 ELSE 0 END) AS TrophyBronzeTotal,
                    SUM(CASE WHEN ad.IsCapstone = 1 THEN 1 ELSE 0 END) AS CapstoneTotal,
                    SUM(CASE WHEN ad.IsCapstone = 1 AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS CapstoneUnlocked,
                    -- Whether the capstones are exactly the platinums, by identity rather than by
                    -- tally: one capstone and one platinum that are different achievements are two
                    -- finish lines, not one. The platinum ApiNames come along so a game whose
                    -- capstones the user has edited can be re-decided against its stored set.
                    SUM(CASE WHEN ad.IsCapstone = 1
                             AND LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) <> 'platinum'
                        THEN 1 ELSE 0 END) AS CapstonesNotPlatinum,
                    SUM(CASE WHEN COALESCE(ad.IsCapstone, 0) <> 1
                             AND LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'platinum'
                        THEN 1 ELSE 0 END) AS PlatinumsNotCapstone,
                    GROUP_CONCAT(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'platinum'
                                 THEN ad.ApiName END, '~|~') AS PlatinumApiNames
                FROM LatestProgress lp
                LEFT JOIN AchievementDefinitions ad
                    ON ad.GameId = lp.GameId
                   AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                                   WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                     AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
                LEFT JOIN UserAchievements ua
                    ON ua.AchievementDefinitionId = ad.Id
                   AND ua.UserGameProgressId = lp.UserGameProgressId
                -- The override mirror supplies the user-editable trophy type, so the trophy counts
                -- above agree with what the achievement list shows.
                LEFT JOIN AchievementOverrides aov
                    ON aov.PlayniteGameId = lp.PlayniteGameId
                   AND aov.ApiName = ad.ApiName
                WHERE lp.RowNum = 1" + ScopeSql(scopeGameId) + @"
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
                ORDER BY lp.LastUpdatedUtc DESC, lp.CacheKey;", ScopeArgs(scopeGameId)).ToList();
        }

        private static Dictionary<string, (int CollectionScore, int PrestigeScore, int Points)> LoadCachedScoreTotals(
            SQLiteDatabase db,
            bool unlockedOnly,
            Guid? scopeGameId = null)
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
                    -- Points is user-editable, so the score total must read the override first.
                    -- Rarity above deliberately is not: it stays provider-owned.
                    COALESCE(aov.Points, ad.Points) AS Points
                FROM LatestProgress lp
                INNER JOIN AchievementDefinitions ad ON ad.GameId = lp.GameId
                LEFT JOIN AchievementOverrides aov
                    ON aov.PlayniteGameId = lp.PlayniteGameId
                   AND aov.ApiName = ad.ApiName
                " + userAchievementJoin + @"
                WHERE lp.RowNum = 1" + ScopeSql(scopeGameId) + @"
                  AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                                  WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                    AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
                ORDER BY lp.CacheKey;", ScopeArgs(scopeGameId)).ToList();

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

        private static List<CachedUnlockTimelineRow> LoadCachedUnlockTimelineRows(
            SQLiteDatabase db,
            Guid? scopeGameId = null)
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
                WHERE lp.RowNum = 1" + ScopeSql(scopeGameId) + @"
                  AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                                  WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                    AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
                GROUP BY
                    lp.CacheKey,
                    lp.PlayniteGameId,
                    date(ua.UnlockTimeUtc)
                ORDER BY UnlockDateUtc DESC, lp.CacheKey;", ScopeArgs(scopeGameId)).ToList();
        }

        private static List<CachedRecentUnlockRow> LoadCachedRecentUnlockRows(
            SQLiteDatabase db,
            int recentAchievementLimit,
            bool includeAllUnlockedAchievements,
            Guid? scopeGameId = null)
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
                    ua.Unlocked AS Unlocked,
                    ua.UnlockTimeUtc AS UnlockTimeUtc,
                    ua.ProgressNum AS ProgressNum,
                    ua.ProgressDenom AS ProgressDenom
                FROM LatestProgress lp
                INNER JOIN UserAchievements ua
                    ON ua.UserGameProgressId = lp.UserGameProgressId");
            if (!includeAllUnlockedAchievements)
            {
                sql.Append(@"
                   AND ua.Unlocked = 1
                   AND ua.UnlockTimeUtc IS NOT NULL");
            }
            else
            {
                // The unbounded overview read: every unlocked achievement, including those
                // without an unlock timestamp. Locked rows stay out of the snapshot - one
                // display row per locked definition (tens of thousands in a large library vs
                // a few thousand unlocks) costs hundreds of MB across the summary memo, the
                // display items, and the search text on the 32-bit host. The few pinned-but-
                // locked achievements are hydrated separately by OverviewDataBuilder.
                sql.Append(@"
                   AND ua.Unlocked = 1");
            }

            sql.Append(@"
                INNER JOIN AchievementDefinitions ad ON ad.Id = ua.AchievementDefinitionId
                WHERE lp.RowNum = 1");
            sql.Append(ScopeSql(scopeGameId));
            sql.Append(@"
                  AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                                  WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                    AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
                -- ApiName, not ad.Id, breaks ties among rows sharing an unlock timestamp. The
                -- row objects this produces carry ApiName but not ad.Id, so a per-game patch
                -- spliced into an existing result could not otherwise reproduce this order and
                -- would drift from a full rebuild. Ordering among distinct timestamps is
                -- unaffected; only exact ties move, and they move to a stable, reproducible key.
                ORDER BY ua.UnlockTimeUtc DESC, lp.CacheKey, ad.ApiName");

            // Positional parameters bind in SQL order: the scope predicate above precedes LIMIT.
            var scopeArgs = ScopeArgs(scopeGameId);

            if (recentAchievementLimit > 0)
            {
                sql.Append(" LIMIT ?");
                sql.Append(';');
                var args = scopeArgs.Concat(new object[] { recentAchievementLimit }).ToArray();
                return db.Load<CachedRecentUnlockRow>(sql.ToString(), args).ToList();
            }

            sql.Append(';');
            return db.Load<CachedRecentUnlockRow>(sql.ToString(), scopeArgs).ToList();
        }

        private List<CachedRecentUnlockData> MapAchievementDetails(
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
                    Unlocked = row.Unlocked != 0,
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
