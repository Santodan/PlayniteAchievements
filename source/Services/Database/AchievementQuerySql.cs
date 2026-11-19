using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Search;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// Turns an <see cref="AchievementQuery"/> into parameterized SQL over
    /// <see cref="AchievementLayerSql.AllUsersViewName"/>: a page of rows, totals per group, or the
    /// distinct group keys of the matching rows.
    /// </summary>
    /// <remarks>
    /// The SQL calls the functions <see cref="AchievementSqlFunctions"/> registers, so it runs only
    /// on the plugin's own connections.
    /// </remarks>
    internal static class AchievementQuerySql
    {
        internal sealed class Command
        {
            public Command(string sql, object[] args)
            {
                Sql = sql;
                Args = args;
            }

            public string Sql { get; }

            public object[] Args { get; }
        }

        private const int IntMax = int.MaxValue;

        private const string RarityTierSql =
            "CASE LOWER(TRIM(r.Rarity)) WHEN 'uncommon' THEN 'Uncommon' WHEN 'rare' THEN 'Rare' WHEN 'ultrarare' THEN 'UltraRare' ELSE 'Common' END";

        private const string TrophySql = "NULLIF(LOWER(TRIM(r.TrophyType)), '')";

        // Matches SummaryCacheReader's platform score rule: the derived Softcore tag marks the unlock.
        private const string SoftcoreSql = "('|' || COALESCE(r.Tags, '') || '|') LIKE '%|Softcore|%'";

        // Whether the friend's ownership row came from their game list or carries playtime, as
        // SqlNadoCacheStore.FriendOwnershipConfirmedSql decides it.
        private const string OwnershipConfirmedSql =
            "CASE WHEN fo.LastOwnershipRefreshUtc IS NOT NULL OR fo.PlaytimeForeverMinutes > 0 THEN 1 ELSE 0 END";

        private static readonly string RowColumns = @"
                r.Id AS DefinitionId, r.GameId AS GameId, r.ApiName AS ApiName, r.DisplayName AS DisplayName,
                r.Description AS Description, r.UnlockedIconPath AS UnlockedIconPath, r.LockedIconPath AS LockedIconPath,
                r.Points AS Points, r.ScaledPoints AS ScaledPoints, r.Category AS Category, r.ProviderCategory AS ProviderCategory,
                r.Tags AS Tags, r.CategoryKind AS CategoryKind, r.TrophyType AS TrophyType, r.Hidden AS Hidden,
                r.IsCapstone AS IsCapstone, r.GlobalPercentUnlocked AS GlobalPercentUnlocked, r.Rarity AS Rarity,
                r.ProgressMax AS ProgressMax, r.IsFiltered AS IsFiltered, r.IsSummaryFiltered AS IsSummaryFiltered,
                r.Note AS Note, r.IsGoal AS IsGoal, r.GoalOrder AS GoalOrder, r.CustomOrder AS CustomOrder,
                r.CategoryOrder AS CategoryOrder, r.CategoryArtPath AS CategoryArtPath,
                r.PlayniteGameId AS PlayniteGameId, r.GameName AS GameName, r.ProviderKey AS ProviderKey,
                g.ProviderPlatformKey AS ProviderPlatformKey, g.ProviderGameId AS ProviderGameId,
                g.ProviderGameKey AS ProviderGameKey, g.IconPath AS GameIconPath, g.CoverPath AS GameCoverPath,
                r.Unlocked AS Unlocked, r.UnlockTimeUtc AS UnlockTimeUtc, r.ProgressNum AS ProgressNum,
                r.ProgressDenom AS ProgressDenom, r.ExcludedFromSummaries AS ExcludedFromSummaries, r.IsAuthored AS IsAuthored,
                r.CleanCapturePath AS CleanCapturePath, r.NotificationCapturePath AS NotificationCapturePath,
                r.FramedCapturePath AS FramedCapturePath, r.VideoCapturePath AS VideoCapturePath,
                r.UserId AS UserId, r.IsCurrentUser AS IsCurrentUser";

        private const string FriendColumns = @",
                u.ProviderKey AS UserProviderKey, u.ExternalUserId AS ExternalUserId, u.DisplayName AS UserDisplayName,
                u.AvatarUrl AS AvatarUrl, u.AvatarPath AS AvatarPath, " + OwnershipConfirmedSql + @" AS OwnershipConfirmed,
                CASE WHEN me.ApiName IS NULL THEN 0 ELSE 1 END AS SelfUnlocked, me.UnlockTimeUtc AS SelfUnlockTimeUtc,
                CASE WHEN EXISTS (
                    SELECT 1 FROM Games sg
                    INNER JOIN UserGameProgress sp ON sp.GameId = sg.Id
                    INNER JOIN Users su ON su.Id = sp.UserId AND su.IsCurrentUser = 1
                    WHERE sg.PlayniteGameId = r.PlayniteGameId) THEN 1 ELSE 0 END AS SelfHasGameData";

        /// <summary>One friend row's identity, as <see cref="PageKeys"/> reads a page of them.</summary>
        internal sealed class PageKey
        {
            public long UserId { get; set; }
            public long? GameId { get; set; }
            public string ApiName { get; set; }
            public string PlayniteGameId { get; set; }
        }

        /// <summary>
        /// The current user's unlocks of each game's achievements across their accounts, with the
        /// latest resolved time; only of <paramref name="playniteGameIds"/> when given.
        /// </summary>
        private static string SelfCtes(IReadOnlyList<string> playniteGameIds, List<object> args) => @"WITH SelfUnlocks AS (
                SELECT s.PlayniteGameId AS PlayniteGameId, s.ApiName AS ApiName, MAX(s.UnlockTimeUtc) AS UnlockTimeUtc
                FROM " + AchievementLayerSql.AllUsersViewName + @" s
                WHERE s.IsCurrentUser = 1 AND s.IsAuthored = 0 AND s.Unlocked = 1" +
                (playniteGameIds == null ? string.Empty : " AND " + InList("s.PlayniteGameId", playniteGameIds, args)) + @"
                GROUP BY s.PlayniteGameId, s.ApiName
            )";

        private const string SelfJoins = @"
            LEFT JOIN SelfUnlocks me ON me.PlayniteGameId = r.PlayniteGameId AND me.ApiName = r.ApiName";

        private const string CompareJoins = @"
            LEFT JOIN CompareUnlocks cmp ON cmp.GameId = r.GameId AND cmp.ApiName = r.ApiName
            LEFT JOIN CompareGames cg ON cg.GameId = r.GameId";

        /// <summary>
        /// The compare accounts' unlocks per provider game and achievement, and the provider games
        /// they have: an unlock in it, or ownership from their game list or with playtime.
        /// </summary>
        private static string CompareCtes(AchievementQuery query, IReadOnlyList<long> gameIds, List<object> args)
        {
            var unlocks = @",
            CompareUnlocks AS (
                SELECT c.GameId AS GameId, c.ApiName AS ApiName, MAX(c.UnlockTimeUtc) AS UnlockTimeUtc
                FROM " + AchievementLayerSql.AllUsersViewName + @" c
                WHERE c.UserId IN (" + AccountUsers(query.CompareAccounts, args) + @") AND c.Unlocked = 1" +
                (gameIds == null ? string.Empty : " AND " + InList("c.GameId", gameIds.Cast<object>().ToList(), args)) + @"
                GROUP BY c.GameId, c.ApiName
            ),
            CompareGames AS (
                SELECT GameId FROM CompareUnlocks
                UNION
                SELECT co.GameId FROM FriendOwnership co
                WHERE co.UserId IN (" + AccountUsers(query.CompareAccounts, args) + @")
                  AND (co.LastOwnershipRefreshUtc IS NOT NULL OR co.PlaytimeForeverMinutes > 0)" +
                (gameIds == null ? string.Empty : " AND " + InList("co.GameId", gameIds.Cast<object>().ToList(), args)) + @"
            )";
            return unlocks;
        }

        /// <summary>
        /// The identities of a page of friend rows in the query's order: the sort and the page
        /// without the display columns, which <see cref="Rows(AchievementQuery, IReadOnlyList{PageKey})"/>
        /// then reads for these rows only.
        /// </summary>
        public static Command PageKeys(AchievementQuery query)
        {
            var args = new List<object>();
            var sql = "SELECT r.UserId AS UserId, r.GameId AS GameId, r.ApiName AS ApiName, r.PlayniteGameId AS PlayniteGameId" +
                      From(query) + Where(query, args) + " ORDER BY " + OrderBy(query) + " LIMIT ? OFFSET ?;";
            args.Add(query.Limit > 0 ? query.Limit : -1);
            args.Add(query.Offset);
            return new Command(sql, args.ToArray());
        }

        /// <summary>
        /// The rows of <paramref name="keys"/> that <paramref name="query"/> matches, in no order,
        /// with every column <see cref="Rows(AchievementQuery)"/> reads; a few rows beyond the keys
        /// may match, which the caller drops.
        /// </summary>
        public static Command Rows(AchievementQuery query, IReadOnlyList<PageKey> keys) =>
            Rows(query, keys ?? Array.Empty<PageKey>(), ordered: false);

        /// <summary>A page of rows, in the query's order, with the friend identity and self state for friend rows.</summary>
        public static Command Rows(AchievementQuery query) => Rows(query, null, ordered: true);

        private static Command Rows(AchievementQuery query, IReadOnlyList<PageKey> keys, bool ordered)
        {
            var args = new List<object>();
            var sql = new StringBuilder();
            var compare = query.Friends && query.CompareAccounts != null;
            if (query.Friends)
            {
                sql.Append(SelfCtes(
                    keys?.Select(key => key.PlayniteGameId).Where(id => id != null).Distinct(StringComparer.Ordinal).ToList(),
                    args));
                if (compare)
                {
                    sql.Append(CompareCtes(query, keys?.Where(key => key.GameId.HasValue).Select(key => key.GameId.Value).Distinct().ToList(), args));
                }

                sql.Append(' ');
            }

            sql.Append("SELECT").Append(RowColumns);
            sql.Append(query.Friends
                ? FriendColumns
                : @",
                NULL AS UserProviderKey, NULL AS ExternalUserId, NULL AS UserDisplayName, NULL AS AvatarUrl, NULL AS AvatarPath,
                0 AS OwnershipConfirmed, 0 AS SelfUnlocked, NULL AS SelfUnlockTimeUtc, 0 AS SelfHasGameData");
            sql.Append(compare
                ? @",
                CASE WHEN cmp.ApiName IS NULL THEN 0 ELSE 1 END AS CompareUnlocked, cmp.UnlockTimeUtc AS CompareUnlockTimeUtc,
                CASE WHEN cg.GameId IS NULL THEN 0 ELSE 1 END AS CompareHasGame"
                : @",
                0 AS CompareUnlocked, NULL AS CompareUnlockTimeUtc, 0 AS CompareHasGame");
            sql.Append(From(query));
            if (query.Friends)
            {
                sql.Append(SelfJoins);
            }

            if (compare)
            {
                sql.Append(CompareJoins);
            }

            sql.Append(Where(query, args));
            if (keys != null)
            {
                // Each term narrows the view's arms on their own columns.
                sql.Append(" AND ").Append(InList("r.UserId", keys.Select(key => key.UserId).Distinct().Cast<object>().ToList(), args));
                sql.Append(" AND ").Append(InList("r.GameId", keys.Where(key => key.GameId.HasValue).Select(key => key.GameId.Value).Distinct().Cast<object>().ToList(), args));
                sql.Append(" AND ").Append(InList("r.ApiName", keys.Select(key => key.ApiName).Distinct(StringComparer.Ordinal).Cast<object>().ToList(), args));
            }

            if (!ordered)
            {
                return new Command(sql.Append(';').ToString(), args.ToArray());
            }

            sql.Append(" ORDER BY ").Append(OrderBy(query));
            if (query.Limit > 0 || query.Offset > 0)
            {
                sql.Append(" LIMIT ? OFFSET ?");
                args.Add(query.Limit > 0 ? query.Limit : -1);
                args.Add(query.Offset);
            }

            return new Command(sql.Append(';').ToString(), args.ToArray());
        }

        /// <summary>
        /// One row per group of the matching rows with their counts and sums. Tag groups by the
        /// stored tag text; the reader splits it.
        /// </summary>
        public static Command Totals(AchievementQuery query, AchievementGroup group)
        {
            var args = new List<object>();

            // Each row's values are computed once in the inner select; LIMIT -1 keeps SQLite from
            // flattening it into the aggregate, which would repeat them per measure. Collection
            // scores follow from the tier counts, since a tier has one collection value.
            var sql = @"SELECT Key,
                COUNT(*) AS Count,
                SUM(Unlocked) AS UnlockedCount,
                " + TierCounts() + @",
                " + TrophyCounts() + @",
                SUM(IsCapstone) AS CapstoneCount,
                SUM(IsCapstone * Unlocked) AS CapstoneUnlocked,
                MIN(" + Collection(unlocked: true) + ", " + IntMax + @") AS CollectionScore,
                MIN(SUM(Prestige * Unlocked), " + IntMax + @") AS PrestigeScore,
                MIN(" + Collection(unlocked: false) + ", " + IntMax + @") AS CollectionScoreTotal,
                MIN(SUM(Prestige), " + IntMax + @") AS PrestigeScoreTotal,
                MIN(SUM(Points * Unlocked), " + IntMax + @") AS Points,
                MIN(SUM(Points * Unlocked * (1 - Softcore)), " + IntMax + @") AS PlatformScorePoints,
                MIN(SUM(Points), " + IntMax + @") AS PointsTotal,
                MIN(CASE WHEN Unlocked = 1 THEN UnlockTimeUtc END) AS FirstUnlockUtc,
                MAX(CASE WHEN Unlocked = 1 THEN UnlockTimeUtc END) AS LastUnlockUtc,
                MAX(CASE WHEN Unlocked = 1 AND IsCapstone = 1 THEN UnlockTimeUtc END) AS LastCapstoneUnlockUtc,
                MIN(CASE WHEN Unlocked = 1 AND Trophy = 'platinum' THEN UnlockTimeUtc END) AS FirstPlatinumUnlockUtc,
                TOTAL(Percent) AS PercentSum,
                COUNT(Percent) AS PercentCount,
                MIN(Percent) AS MinPercent,
                SUM(Hidden * (1 - Unlocked)) AS HiddenLockedCount,
                MIN(CategoryOrder) AS MinCategoryOrder
            FROM (" + TotalsRows(query, group, args) + @")
            GROUP BY Key;";
            return new Command(sql, args.ToArray());
        }

        // One row per matching row, or with DistinctAchievements one per achievement of a provider
        // game across the matching users.
        private static string TotalsRows(AchievementQuery query, AchievementGroup group, List<object> args)
        {
            var rows = "SELECT " + KeySql(query, group) + @" AS Key,
                r.GameId AS DefinitionGameId,
                r.ApiName AS DefinitionApiName,
                r.Unlocked AS Unlocked,
                " + RarityTierSql + @" AS Tier,
                " + TrophySql + @" AS Trophy,
                CASE WHEN r.IsCapstone = 1 THEN 1 ELSE 0 END AS IsCapstone,
                CASE WHEN r.Hidden = 1 THEN 1 ELSE 0 END AS Hidden,
                pa_prestige(r.GlobalPercentUnlocked, r.Rarity) AS Prestige,
                CASE WHEN r.Points > 0 THEN r.Points ELSE 0 END AS Points,
                CASE WHEN " + SoftcoreSql + @" THEN 1 ELSE 0 END AS Softcore,
                r.UnlockTimeUtc AS UnlockTimeUtc,
                r.GlobalPercentUnlocked AS Percent,
                r.CategoryOrder AS CategoryOrder" +
                From(query) + Where(query, args) + @"
            LIMIT -1";
            if (!query.DistinctAchievements)
            {
                return rows;
            }

            // Definition values are the same on every user's row; the unlock is the best of them.
            return @"SELECT Key, MAX(Unlocked) AS Unlocked, MAX(Tier) AS Tier, MAX(Trophy) AS Trophy,
                MAX(IsCapstone) AS IsCapstone, MAX(Hidden) AS Hidden, MAX(Prestige) AS Prestige, MAX(Points) AS Points,
                MIN(Softcore) AS Softcore, MAX(CASE WHEN Unlocked = 1 THEN UnlockTimeUtc END) AS UnlockTimeUtc,
                MAX(Percent) AS Percent, MIN(CategoryOrder) AS CategoryOrder
            FROM (" + rows + @")
            GROUP BY Key, DefinitionGameId, DefinitionApiName
            LIMIT -1";
        }

        /// <summary>The number of matching rows; the query's page is ignored.</summary>
        public static Command Count(AchievementQuery query)
        {
            var args = new List<object>();
            var sql = "SELECT COUNT(*)" + From(query) + Where(query, args) + ";";
            return new Command(sql, args.ToArray());
        }

        /// <summary>The distinct group keys of the matching rows. Tag lists stored tag texts; the reader splits them.</summary>
        public static Command Keys(AchievementQuery query, AchievementGroup group)
        {
            var args = new List<object>();
            var key = KeySql(query, group);
            var sql = "SELECT DISTINCT " + key + " AS Key" + From(query) + Where(query, args) + ";";
            return new Command(sql, args.ToArray());
        }

        private static string From(AchievementQuery query) => @"
            FROM " + AchievementLayerSql.AllUsersViewName + @" r
            LEFT JOIN Games g ON g.Id = r.GameId
            LEFT JOIN Users u ON u.Id = r.UserId" +
            (query.Friends ? @"
            INNER JOIN FriendOwnership fo ON fo.UserId = r.UserId AND fo.GameId = r.GameId" : string.Empty);

        private static string Where(AchievementQuery query, List<object> args)
        {
            var conditions = new List<string>();
            if (query.Friends)
            {
                conditions.Add("r.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL");
                if (!string.IsNullOrWhiteSpace(query.FriendProviderKey))
                {
                    // A term on the view's own column, so SQLite narrows the view to the friend.
                    var friend = "SELECT fu.Id FROM Users fu WHERE fu.ProviderKey = ?";
                    args.Add(query.FriendProviderKey.Trim());
                    if (!string.IsNullOrWhiteSpace(query.FriendExternalUserId))
                    {
                        friend += " AND fu.ExternalUserId = ?";
                        args.Add(query.FriendExternalUserId.Trim());
                    }

                    conditions.Add("r.UserId IN (" + friend + ")");
                }

                if (query.FriendAccounts != null)
                {
                    conditions.Add("r.UserId IN (" + AccountUsers(query.FriendAccounts, args) + ")");
                }
            }
            else
            {
                conditions.Add("r.IsCurrentUser = 1");
            }

            if (query.MatchesNothing)
            {
                conditions.Add("0");
            }

            if (query.GameIds != null)
            {
                // Games and the override mirror store the id as Guid.ToString() writes it.
                conditions.Add(query.GameIds.Count == 0
                    ? "0"
                    : "r.PlayniteGameId IN (" + string.Join(", ", query.GameIds.Select(_ => "?")) + ")");
                args.AddRange(query.GameIds.Select(id => (object)id.ToString("D")));
            }

            if (!string.IsNullOrWhiteSpace(query.GameProviderKey))
            {
                // A term on the view's own column, so SQLite narrows the view to the game.
                var gameConditions = new List<string>();
                args.Add(query.GameProviderKey.Trim());
                if (!string.IsNullOrWhiteSpace(query.ProviderGameKey))
                {
                    gameConditions.Add("pg.ProviderGameKey = ?");
                    args.Add(query.ProviderGameKey.Trim());
                }

                if (query.AppId > 0)
                {
                    gameConditions.Add("pg.ProviderGameId = ?");
                    args.Add(query.AppId);
                }

                conditions.Add("r.GameId IN (SELECT pg.Id FROM Games pg WHERE pg.ProviderKey = ? AND (" +
                    (gameConditions.Count == 0 ? "0" : string.Join(" OR ", gameConditions)) + "))");
            }

            if (query.Unlocked.HasValue)
            {
                conditions.Add(query.Unlocked.Value ? "r.Unlocked = 1" : "r.Unlocked = 0");
            }

            if (query.Rarities != null)
            {
                conditions.Add(InList(RarityTierSql, query.Rarities.Select(tier => tier.ToString()).ToList(), args));
            }

            if (query.TrophyTypes != null)
            {
                conditions.Add(InList(
                    TrophySql,
                    query.TrophyTypes.Where(type => !string.IsNullOrWhiteSpace(type)).Select(type => type.Trim().ToLowerInvariant()).ToList(),
                    args));
            }

            if (query.ClassificationKeys != null)
            {
                var keys = query.ClassificationKeys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList();
                conditions.Add(keys.Count == 0
                    ? "0"
                    : "pa_classified(r.CategoryKind, r.Tags" + string.Concat(keys.Select(_ => ", ?")) + ") = 1");
                args.AddRange(keys);
            }

            if (query.UnlockedFromDay.HasValue || query.UnlockedToDay.HasValue)
            {
                conditions.Add("r.Unlocked = 1 AND r.UnlockTimeUtc IS NOT NULL");
                if (query.UnlockedFromDay.HasValue)
                {
                    conditions.Add("pa_localday(r.UnlockTimeUtc) >= ?");
                    args.Add(Day(query.UnlockedFromDay.Value));
                }

                if (query.UnlockedToDay.HasValue)
                {
                    conditions.Add("pa_localday(r.UnlockTimeUtc) <= ?");
                    args.Add(Day(query.UnlockedToDay.Value));
                }
            }

            if (SearchQuery.From(query.SearchText).HasValue)
            {
                // The fields each surface's search index joins today.
                conditions.Add(query.Friends
                    ? "pa_search(?, u.DisplayName, r.GameName, r.DisplayName, r.Description, r.Category, r.ProviderKey) = 1"
                    : "pa_search(?, r.GameName, r.DisplayName) = 1");
                args.Add(query.SearchText);
            }

            if (query.SummaryRules)
            {
                conditions.Add("r.ExcludedFromSummaries = 0 AND r.IsFiltered = 0 AND r.IsSummaryFiltered = 0");
            }

            if (query.RequireUnlockTime)
            {
                conditions.Add("r.Unlocked = 1 AND r.UnlockTimeUtc IS NOT NULL");
            }

            if (query.HideHiddenLocked)
            {
                conditions.Add("NOT (r.Hidden = 1 AND r.Unlocked = 0)");
            }

            if (query.CapstonesOnly)
            {
                conditions.Add("r.IsCapstone = 1");
            }

            if (query.CategoryLabels != null)
            {
                conditions.Add(InList(
                    "pa_category(r.Category)",
                    query.CategoryLabels.Select(AchievementCategoryHelper.NormalizeCategoryOrDefault).Distinct(StringComparer.Ordinal).ToList(),
                    args));
            }

            return @"
            WHERE " + string.Join(@"
              AND ", conditions);
        }

        // The Users ids of the accounts; no row when there are none.
        private static string AccountUsers(IReadOnlyList<FriendAccountRef> accounts, List<object> args)
        {
            if (accounts == null || accounts.Count == 0)
            {
                return "SELECT NULL WHERE 0";
            }

            var terms = new List<string>();
            foreach (var account in accounts)
            {
                terms.Add("(fu.ProviderKey = ? AND fu.ExternalUserId = ?)");
                args.Add(account.ProviderKey);
                args.Add(account.ExternalUserId);
            }

            return "SELECT fu.Id FROM Users fu WHERE " + string.Join(" OR ", terms);
        }

        private static string InList(string expression, IReadOnlyList<string> values, List<object> args) =>
            InList(expression, values.Cast<object>().ToList(), args);

        private static string InList(string expression, IReadOnlyList<object> values, List<object> args)
        {
            if (values.Count == 0)
            {
                return "0";
            }

            args.AddRange(values);
            return expression + " IN (" + string.Join(", ", values.Select(_ => "?")) + ")";
        }

        private static string KeySql(AchievementQuery query, AchievementGroup group)
        {
            switch (group)
            {
                case AchievementGroup.Game:
                    return "r.PlayniteGameId";
                case AchievementGroup.Friend:
                    if (!query.Friends)
                    {
                        throw new ArgumentException("Friend groups need a friend query.", nameof(group));
                    }

                    return "TRIM(u.ProviderKey) || char(31) || TRIM(u.ExternalUserId)";
                case AchievementGroup.Provider:
                    return "r.ProviderKey";
                case AchievementGroup.Day:
                    return "CASE WHEN r.Unlocked = 1 THEN pa_localday(r.UnlockTimeUtc) END";
                case AchievementGroup.Rarity:
                    return RarityTierSql;
                case AchievementGroup.Trophy:
                    return TrophySql;
                case AchievementGroup.Tag:
                    return "r.Tags";
                case AchievementGroup.ProviderGame:
                    return ProviderGameKeySql;
                case AchievementGroup.FriendGame:
                    return KeySql(query, AchievementGroup.Friend) + " || char(30) || " + ProviderGameKeySql;
                case AchievementGroup.Category:
                    return "r.Category";
                default:
                    throw new ArgumentOutOfRangeException(nameof(group), group, null);
            }
        }

        // AchievementGroupKeys.ProviderGame reads it back.
        private const string ProviderGameKeySql =
            "COALESCE(g.ProviderKey, r.ProviderKey, '') || char(31) || COALESCE(g.ProviderGameKey, '') || char(31) || " +
            "COALESCE(g.ProviderGameId, 0) || char(31) || COALESCE(r.PlayniteGameId, '')";

        // Unlocked rows with a time, then unlocked rows without one, then locked rows, in either
        // direction, as AchievementSortHelper buckets them.
        private const string UnlockBucketSql =
            "CASE WHEN r.Unlocked = 1 AND r.UnlockTimeUtc IS NOT NULL THEN 0 WHEN r.Unlocked = 1 THEN 1 ELSE 2 END";

        private const string TrophyRankSql =
            "CASE LOWER(TRIM(r.TrophyType)) WHEN 'platinum' THEN 4 WHEN 'gold' THEN 3 WHEN 'silver' THEN 2 WHEN 'bronze' THEN 1 ELSE 0 END";

        private static string OrderBy(AchievementQuery query)
        {
            var direction = query.Descending ? " DESC" : " ASC";
            var friend = query.Friends ? "u.DisplayName COLLATE NOCASE, " : string.Empty;
            var tieBreak = ", " + friend + "r.PlayniteGameId, r.GameId, r.ApiName, r.UserId";
            var unlock = UnlockBucketSql + ", r.UnlockTimeUtc" + direction;
            var display = string.Join(", ", AchievementLayerSql.DisplayOrderSql.Split(',').Select(term => "r." + term.Trim()));
            switch (query.Sort)
            {
                case AchievementSort.UnlockTime:
                    return unlock + tieBreak;
                case AchievementSort.GlobalPercent:
                    return "r.GlobalPercentUnlocked" + direction + ", r.UnlockTimeUtc DESC" + tieBreak;
                case AchievementSort.Rarity:
                    return "pa_raritysort(r.GlobalPercentUnlocked, r.Rarity)" + direction + ", " + unlock + tieBreak;
                case AchievementSort.Name:
                    return "r.DisplayName COLLATE NOCASE" + direction + tieBreak;
                case AchievementSort.Game:
                    return "r.GameName COLLATE NOCASE" + direction + ", " + unlock + tieBreak;
                case AchievementSort.Friend:
                    return query.Friends
                        ? "u.DisplayName COLLATE NOCASE" + direction + ", " + unlock + tieBreak
                        : unlock + tieBreak;
                case AchievementSort.Points:
                    return "COALESCE(r.ScaledPoints, r.Points, 0)" + direction + tieBreak;
                case AchievementSort.Trophy:
                    return TrophyRankSql + direction + ", pa_raritysort(r.GlobalPercentUnlocked, r.Rarity)" + tieBreak;
                case AchievementSort.Category:
                    return "COALESCE(r.CategoryOrder, 2147483647)" + direction + ", pa_category(r.Category)" + direction + ", " + unlock + tieBreak;
                case AchievementSort.Kind:
                    return "pa_kindsort(r.CategoryKind)" + direction + ", " + unlock + tieBreak;
                case AchievementSort.Tags:
                    return "pa_tagcount(r.Tags)" + direction + ", r.Tags COLLATE NOCASE" + direction + ", " + unlock + tieBreak;
                case AchievementSort.Note:
                    return "CASE WHEN TRIM(COALESCE(r.Note, '')) <> '' THEN 1 ELSE 0 END" + direction + ", r.DisplayName COLLATE NOCASE" + tieBreak;
                case AchievementSort.CollectionScore:
                    return "pa_collection(r.Rarity)" + direction + tieBreak;
                case AchievementSort.PrestigeScore:
                    return "pa_prestige(r.GlobalPercentUnlocked, r.Rarity)" + direction + tieBreak;
                default:
                    return friend + "r.GameName, r.PlayniteGameId, " + display + ", r.UserId";
            }
        }

        private static string Day(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static readonly string[] Tiers = { "Common", "Uncommon", "Rare", "UltraRare" };

        private static string TierCounts() => string.Join(",\n                ", Tiers.SelectMany(tier => new[]
        {
            "SUM(Tier = '" + tier + "') AS " + tier + "Count",
            "SUM(Unlocked = 1 AND Tier = '" + tier + "') AS " + tier + "Unlocked"
        }));

        private static string TrophyCounts() => string.Join(",\n                ", new[] { "Platinum", "Gold", "Silver", "Bronze" }.SelectMany(trophy => new[]
        {
            "SUM(Trophy = '" + trophy.ToLowerInvariant() + "') AS " + trophy + "Count",
            "SUM(Unlocked = 1 AND Trophy = '" + trophy.ToLowerInvariant() + "') AS " + trophy + "Unlocked"
        }));

        // pa_collection over a constant runs once per statement.
        private static string Collection(bool unlocked) => string.Join(" + ", Tiers.Select(tier =>
            "SUM(" + (unlocked ? "Unlocked = 1 AND " : string.Empty) + "Tier = '" + tier + "') * pa_collection('" + tier + "')"));
    }
}
