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

        // Platform scores leave out softcore unlocks: the derived Softcore tag marks the unlock.
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
                r.PlayniteGameId AS PlayniteGameId, r.GameName AS GameName,
                COALESCE(r.ProviderKey, ag.ProviderKey, CASE WHEN r.IsAuthored = 1 THEN '" + CustomAchievementProjectionService.ProviderKey + @"' END) AS ProviderKey,
                COALESCE(g.ProviderPlatformKey, ag.ProviderPlatformKey) AS ProviderPlatformKey,
                COALESCE(g.ProviderGameId, ag.ProviderGameId) AS ProviderGameId,
                COALESCE(g.ProviderGameKey, ag.ProviderGameKey) AS ProviderGameKey, g.IconPath AS GameIconPath, g.CoverPath AS GameCoverPath,
                r.Unlocked AS Unlocked, r.UnlockTimeUtc AS UnlockTimeUtc, r.ProgressNum AS ProgressNum,
                r.ProgressDenom AS ProgressDenom, r.ExcludedFromSummaries AS ExcludedFromSummaries, r.IsAuthored AS IsAuthored,
                r.CleanCapturePath AS CleanCapturePath, r.NotificationCapturePath AS NotificationCapturePath,
                r.FramedCapturePath AS FramedCapturePath, r.VideoCapturePath AS VideoCapturePath,
                r.UserId AS UserId, r.IsCurrentUser AS IsCurrentUser";

        // An authored row has no provider game, so it reads its Playnite game's as the summary
        // names that game: the current user's most recently updated provider game of it, else the
        // Custom provider.
        private const string AuthoredGameJoin = @"
            LEFT JOIN Games ag ON ag.Id = CASE WHEN r.IsAuthored = 1 THEN (
                SELECT pg.Id FROM UserGameProgress up
                INNER JOIN Users cu ON cu.Id = up.UserId AND cu.IsCurrentUser = 1
                INNER JOIN Games pg ON pg.Id = up.GameId
                WHERE pg.PlayniteGameId = r.PlayniteGameId AND TRIM(COALESCE(up.CacheKey, '')) <> ''
                ORDER BY up.LastUpdatedUtc DESC, up.Id DESC LIMIT 1) END";

        private const string FriendColumns = @",
                u.ProviderKey AS UserProviderKey, u.ExternalUserId AS ExternalUserId, u.DisplayName AS UserDisplayName,
                u.AvatarUrl AS AvatarUrl, u.AvatarPath AS AvatarPath, " + OwnershipConfirmedSql + @" AS OwnershipConfirmed,
                CASE WHEN me.ApiName IS NULL THEN 0 ELSE 1 END AS SelfUnlocked, me.UnlockTimeUtc AS SelfUnlockTimeUtc,
                CASE WHEN EXISTS (
                    SELECT 1 FROM Games sg
                    INNER JOIN UserGameProgress sp ON sp.GameId = sg.Id
                    INNER JOIN Users su ON su.Id = sp.UserId AND su.IsCurrentUser = 1
                    WHERE sg.PlayniteGameId = r.PlayniteGameId) THEN 1 ELSE 0 END AS SelfHasGameData";

        /// <summary>
        /// One row's identity, as <see cref="SortedKeys"/> reads them: user, provider game and
        /// definition, or for an authored row, which has none of them, its Playnite game and API name.
        /// </summary>
        internal sealed class RowKey
        {
            public long? UserId { get; set; }
            public long? GameId { get; set; }
            public long? DefinitionId { get; set; }
            public string PlayniteGameId { get; set; }
            public string ApiName { get; set; }
        }

        /// <summary>
        /// The current user's unlocks of each game's achievements across their accounts, with the
        /// latest resolved time; only of the Playnite games of <paramref name="gameIds"/> when given.
        /// </summary>
        private static string SelfCtes(IReadOnlyList<long> gameIds) => @"WITH SelfUnlocks AS (
                SELECT s.PlayniteGameId AS PlayniteGameId, s.ApiName AS ApiName, MAX(s.UnlockTimeUtc) AS UnlockTimeUtc
                FROM " + AchievementLayerSql.AllUsersViewName + @" s
                WHERE s.IsCurrentUser = 1 AND s.IsAuthored = 0 AND s.Unlocked = 1" +
                (gameIds == null ? string.Empty : " AND s.PlayniteGameId IN (SELECT sg.PlayniteGameId FROM Games sg WHERE " + IdList("sg.Id", gameIds.Select(id => (long?)id)) + ")") + @"
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
                (gameIds == null ? string.Empty : " AND " + IdList("c.GameId", gameIds.Select(id => (long?)id))) + @"
                GROUP BY c.GameId, c.ApiName
            ),
            CompareGames AS (
                SELECT GameId FROM CompareUnlocks
                UNION
                SELECT co.GameId FROM FriendOwnership co
                WHERE co.UserId IN (" + AccountUsers(query.CompareAccounts, args) + @")
                  AND (co.LastOwnershipRefreshUtc IS NOT NULL OR co.PlaytimeForeverMinutes > 0)" +
                (gameIds == null ? string.Empty : " AND " + IdList("co.GameId", gameIds.Select(id => (long?)id))) + @"
            )";
            return unlocks;
        }

        /// <summary>
        /// The identities of the matching rows in the query's order, without the display columns,
        /// which <see cref="Rows(AchievementQuery, IReadOnlyList{RowKey})"/> then reads for some of them.
        /// </summary>
        public static Command SortedKeys(AchievementQuery query)
        {
            var args = new List<object>();
            var sql = "SELECT r.UserId AS UserId, r.GameId AS GameId, r.Id AS DefinitionId, " +
                      "CASE WHEN r.IsAuthored = 1 THEN r.PlayniteGameId END AS PlayniteGameId, " +
                      "CASE WHEN r.IsAuthored = 1 THEN r.ApiName END AS ApiName" +
                      From(query, args) + Where(query, args) + " ORDER BY " + OrderBy(query) + " LIMIT ? OFFSET ?;";
            args.Add(query.Limit > 0 ? query.Limit : -1);
            args.Add(query.Offset);
            return new Command(sql, args.ToArray());
        }

        /// <summary>
        /// The keys of <see cref="SortedKeys"/> cut, per Playnite game, to its first
        /// <paramref name="slice"/> rows in the query's order and its <paramref name="slice"/> rows
        /// with the highest global percent (API name ignoring case, then the query's order, breaking
        /// ties; a row with no percent never ranks): game by game in the query's order, each game's
        /// first rows, then the rest of its highest.
        /// </summary>
        public static Command SlicedKeys(AchievementQuery query, int slice)
        {
            var args = new List<object>();
            var order = OrderBy(query);
            var sql = @"SELECT UserId, GameId, DefinitionId, PlayniteGameId, ApiName FROM (
                SELECT *, MIN(Position) OVER (PARTITION BY Game) AS GamePosition FROM (
                    SELECT r.UserId AS UserId, r.GameId AS GameId, r.Id AS DefinitionId,
                        CASE WHEN r.IsAuthored = 1 THEN r.PlayniteGameId END AS PlayniteGameId,
                        CASE WHEN r.IsAuthored = 1 THEN r.ApiName END AS ApiName, r.PlayniteGameId AS Game,
                        ROW_NUMBER() OVER (ORDER BY " + order + @") AS Position,
                        ROW_NUMBER() OVER (PARTITION BY r.PlayniteGameId ORDER BY " + order + @") AS InGame,
                        CASE WHEN r.GlobalPercentUnlocked IS NOT NULL THEN ROW_NUMBER() OVER (PARTITION BY r.PlayniteGameId
                            ORDER BY r.GlobalPercentUnlocked IS NULL, r.GlobalPercentUnlocked DESC, r.ApiName COLLATE pa_ignorecase, " + order + @") END AS ByPercent" +
                      From(query, args) + Where(query, args) + @"))
                WHERE InGame <= ? OR ByPercent <= ?
                ORDER BY GamePosition, InGame > ?, CASE WHEN InGame <= ? THEN InGame ELSE ByPercent END;";
            args.AddRange(new object[] { slice, slice, slice, slice });
            return new Command(sql, args.ToArray());
        }

        /// <summary>
        /// The rows of <paramref name="keys"/> that <paramref name="query"/> matches, in no order,
        /// with every column <see cref="Rows(AchievementQuery)"/> reads; a few rows beyond the keys
        /// may match, which the caller drops.
        /// </summary>
        public static Command Rows(AchievementQuery query, IReadOnlyList<RowKey> keys) =>
            Rows(query, keys ?? Array.Empty<RowKey>(), ordered: false);

        /// <summary>A page of rows, in the query's order, with the friend identity and self state for friend rows.</summary>
        public static Command Rows(AchievementQuery query) => Rows(query, null, ordered: true);

        private static Command Rows(AchievementQuery query, IReadOnlyList<RowKey> keys, bool ordered)
        {
            var args = new List<object>();
            var sql = new StringBuilder();
            var compare = query.Friends && query.CompareAccounts != null;
            var gameIds = keys?.Where(key => key.GameId.HasValue).Select(key => key.GameId.Value).Distinct().ToList();
            if (query.Friends)
            {
                sql.Append(SelfCtes(gameIds));
                if (compare)
                {
                    sql.Append(CompareCtes(query, gameIds, args));
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
            sql.Append(From(query, args));
            sql.Append(AuthoredGameJoin);
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
                var terms = new List<string>();
                var platform = keys.Where(key => key.PlayniteGameId == null).ToList();
                if (platform.Count > 0)
                {
                    terms.Add(IdList("r.UserId", platform.Select(key => key.UserId)) +
                              " AND " + IdList("r.GameId", gameIds.Select(id => (long?)id)) +
                              " AND " + IdList("r.Id", platform.Select(key => key.DefinitionId)));
                }

                var authored = keys.Where(key => key.PlayniteGameId != null).ToList();
                if (authored.Count > 0)
                {
                    terms.Add("r.IsAuthored = 1 AND " +
                              GameIdTextList("r.PlayniteGameId", authored.Select(key => key.PlayniteGameId)) +
                              " AND " + TextList("r.ApiName", authored.Select(key => key.ApiName)));
                }

                sql.Append(" AND ").Append(terms.Count == 1 ? terms[0] : "((" + string.Join(") OR (", terms) + "))");
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
        /// The provider's values of the platform rows of <paramref name="keys"/>, one row per user
        /// and definition: each field as the metadata and platform layers resolve it without the
        /// custom layer, and the user's unlock time as the platform reported it. These are what the
        /// achievement editor compares a row's shown values with. Authored keys have none.
        /// </summary>
        public static Command EditorBaselines(IReadOnlyList<RowKey> keys)
        {
            var platform = (keys ?? Array.Empty<RowKey>()).Where(key => key.PlayniteGameId == null && key.DefinitionId.HasValue).ToList();
            var sql = @"SELECT ad.Id AS DefinitionId, ugp.UserId AS UserId,
                " + AchievementLayerSql.ProviderValue("DisplayName", Models.Achievements.AchievementField.DisplayName) + @" AS DisplayName,
                " + AchievementLayerSql.ProviderValue("Description", Models.Achievements.AchievementField.Description) + @" AS Description,
                " + AchievementLayerSql.ProviderValue("UnlockedIconPath", Models.Achievements.AchievementField.UnlockedIcon) + @" AS UnlockedIconPath,
                " + AchievementLayerSql.ProviderValue("LockedIconPath", Models.Achievements.AchievementField.LockedIcon) + @" AS LockedIconPath,
                " + AchievementLayerSql.ProviderValue("Points", Models.Achievements.AchievementField.Points) + @" AS Points,
                " + AchievementLayerSql.ProviderValue("TrophyType", Models.Achievements.AchievementField.TrophyType) + @" AS TrophyType,
                " + AchievementLayerSql.ProviderTags() + @" AS Tags,
                ad.Hidden AS Hidden, ad.IsCapstone AS IsCapstone, ua.UnlockTimeUtc AS UnlockTimeUtc
            FROM PlatformDefinitions ad
            LEFT JOIN AchievementMetadataLayer m ON m.AchievementDefinitionId = ad.Id
            INNER JOIN UserGameProgress ugp ON ugp.GameId = ad.GameId AND " + IdList("ugp.UserId", platform.Select(key => key.UserId)) + @"
            LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.AchievementDefinitionId = ad.Id AND ua.Unlocked = 1
            WHERE " + IdList("ad.Id", platform.Select(key => key.DefinitionId)) + ";";
            return new Command(sql, Array.Empty<object>());
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
                MIN(CASE WHEN Unlocked = 1 AND Percent > 0 THEN Percent END) AS MinPercent,
                MIN(CASE WHEN Unlocked = 1 AND BaseCapstone = 1 AND Percent > 0 THEN Percent END) AS MinBasePercent,
                SUM(Hidden * (1 - Unlocked)) AS HiddenLockedCount,
                SUM(Authored) AS AuthoredCount,
                MIN(CategoryOrder) AS MinCategoryOrder,
                MIN(DisplayOrder) AS FirstDisplayOrder
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
                CASE WHEN r.IsCapstone = 1 THEN pa_classified(r.CategoryKind, NULL, 'Base') ELSE 0 END AS BaseCapstone,
                CASE WHEN r.Hidden = 1 THEN 1 ELSE 0 END AS Hidden,
                r.IsAuthored AS Authored,
                pa_prestige(r.GlobalPercentUnlocked, r.Rarity) AS Prestige,
                CASE WHEN r.Points > 0 THEN r.Points ELSE 0 END AS Points,
                CASE WHEN " + SoftcoreSql + @" THEN 1 ELSE 0 END AS Softcore,
                r.UnlockTimeUtc AS UnlockTimeUtc,
                r.GlobalPercentUnlocked AS Percent,
                r.CategoryOrder AS CategoryOrder,
                " + (group == AchievementGroup.Category ? DisplayOrderText : "NULL") + @" AS DisplayOrder" +
                From(query, args) + Where(query, args) + @"
            LIMIT -1";
            if (!query.DistinctAchievements)
            {
                return rows;
            }

            // Definition values are the same on every user's row; the unlock is the best of them.
            return @"SELECT Key, MAX(Unlocked) AS Unlocked, MAX(Tier) AS Tier, MAX(Trophy) AS Trophy,
                MAX(IsCapstone) AS IsCapstone, MAX(BaseCapstone) AS BaseCapstone, MAX(Hidden) AS Hidden, MAX(Authored) AS Authored, MAX(Prestige) AS Prestige, MAX(Points) AS Points,
                MIN(Softcore) AS Softcore, MAX(CASE WHEN Unlocked = 1 THEN UnlockTimeUtc END) AS UnlockTimeUtc,
                MAX(Percent) AS Percent, MIN(CategoryOrder) AS CategoryOrder, MIN(DisplayOrder) AS DisplayOrder
            FROM (" + rows + @")
            GROUP BY Key, DefinitionGameId, DefinitionApiName
            LIMIT -1";
        }

        // AchievementLayerSql.DisplayOrderSql as text that sorts the same way, for the first row of a group.
        private const string DisplayOrderText =
            "printf('%d%011d%d%011d%d%015d', r.CustomOrder IS NULL, COALESCE(r.CustomOrder, 0), r.MetadataOrder IS NULL, " +
            "COALESCE(r.MetadataOrder, 0), r.IsAuthored, COALESCE(r.Id, 0))";

        /// <summary>A query's identity: its rows SQL and arguments, which hold every part of it.</summary>
        public static string KeyOf(AchievementQuery query)
        {
            var command = Rows(query);
            return command.Sql + "\u001e" + string.Join("\u001f", command.Args.Select(arg => Convert.ToString(arg, CultureInfo.InvariantCulture)));
        }

        /// <summary>The distinct group keys of the matching rows. Tag lists stored tag texts; the reader splits them.</summary>
        public static Command Keys(AchievementQuery query, AchievementGroup group)
        {
            var args = new List<object>();
            var key = KeySql(query, group);
            var sql = "SELECT DISTINCT " + key + " AS Key" + From(query, args) + Where(query, args) + ";";
            return new Command(sql, args.ToArray());
        }

        // Friend queries join the shown names of the accounts that have one, as fn.column3.
        private static string From(AchievementQuery query, List<object> args)
        {
            var sql = @"
            FROM " + AchievementLayerSql.AllUsersViewName + @" r
            LEFT JOIN Games g ON g.Id = r.GameId
            LEFT JOIN Users u ON u.Id = r.UserId";
            if (!query.Friends)
            {
                return sql;
            }

            sql += @"
            INNER JOIN FriendOwnership fo ON fo.UserId = r.UserId AND fo.GameId = r.GameId";
            if (query.FriendNames == null)
            {
                return sql;
            }

            foreach (var name in query.FriendNames)
            {
                args.Add(name.Key.ProviderKey);
                args.Add(name.Key.ExternalUserId);
                args.Add(name.Value);
            }

            return sql + @"
            LEFT JOIN (VALUES " + string.Join(", ", query.FriendNames.Select(_ => "(?, ?, ?)")) + @") fn
              ON fn.column1 = u.ProviderKey COLLATE NOCASE AND fn.column2 = u.ExternalUserId COLLATE NOCASE";
        }

        private static string Where(AchievementQuery query, List<object> args)
        {
            // Scope conditions pick whose rows and which games; row filters test the row's own
            // values, and kept achievements pass them. Each list binds its own arguments, and the
            // filters come after every scope condition in the text, so their arguments follow.
            var conditions = new List<string>();
            var filters = new List<string>();
            var filterArgs = new List<object>();
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
                // Games and the custom layer store the id as Guid.ToString() writes it; inlined
                // as ExceptGameIds is.
                conditions.Add(query.GameIds.Count == 0
                    ? "0"
                    : "r.PlayniteGameId IN (" + string.Join(", ", query.GameIds.Select(id => "'" + id.ToString("D") + "'")) + ")");
            }

            if (query.ExceptGameIds != null)
            {
                // Inlined: a Guid's text is hex and dashes only, and the list can outgrow a parameter limit.
                conditions.Add("COALESCE(r.PlayniteGameId, '') NOT IN (" +
                    string.Join(", ", query.ExceptGameIds.Select(id => "'" + id.ToString("D") + "'")) + ")");
            }

            if (query.LatestProgressOnly && !query.Friends)
            {
                conditions.Add(LatestProgressSql);
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

            if (query.SummaryRules)
            {
                conditions.Add("r.ExcludedFromSummaries = 0 AND r.IsFiltered = 0 AND r.IsSummaryFiltered = 0");
            }

            if (query.ProviderKey != null)
            {
                conditions.Add(ProviderSql + " = ? COLLATE NOCASE");
                args.Add(query.ProviderKey);
            }

            if (query.Achievements != null)
            {
                conditions.Add(query.Achievements.Count == 0 ? "0" : AchievementPairsSql(query.Achievements));
            }

            if (query.Unlocked.HasValue)
            {
                filters.Add(query.Unlocked.Value ? "r.Unlocked = 1" : "r.Unlocked = 0");
            }

            var capstoneOrRarity = query.CapstonesOnly && query.CapstoneOrRarity && query.Rarities != null;
            if (query.Rarities != null)
            {
                var rarity = InList(RarityTierSql, query.Rarities.Select(tier => tier.ToString()).ToList(), filterArgs);
                filters.Add(capstoneOrRarity ? "(" + rarity + " OR r.IsCapstone = 1)" : rarity);
            }

            if (query.TrophyTypes != null)
            {
                filters.Add(InList(
                    TrophySql,
                    query.TrophyTypes.Where(type => !string.IsNullOrWhiteSpace(type)).Select(type => type.Trim().ToLowerInvariant()).ToList(),
                    filterArgs));
            }

            if (query.ClassificationKeys != null)
            {
                var keys = query.ClassificationKeys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList();
                filters.Add(keys.Count == 0
                    ? "0"
                    : "pa_classified(r.CategoryKind, r.Tags" + string.Concat(keys.Select(_ => ", ?")) + ") = 1");
                filterArgs.AddRange(keys);
            }

            if (query.UnlockedFromDay.HasValue || query.UnlockedToDay.HasValue)
            {
                filters.Add("r.Unlocked = 1 AND r.UnlockTimeUtc IS NOT NULL");
                if (query.UnlockedFromDay.HasValue)
                {
                    filters.Add("pa_localday(r.UnlockTimeUtc) >= ?");
                    filterArgs.Add(Day(query.UnlockedFromDay.Value));
                }

                if (query.UnlockedToDay.HasValue)
                {
                    filters.Add("pa_localday(r.UnlockTimeUtc) <= ?");
                    filterArgs.Add(Day(query.UnlockedToDay.Value));
                }
            }

            if (SearchQuery.From(query.SearchText).HasValue)
            {
                // The fields each surface's search index joins today.
                filters.Add(query.Friends
                    ? "pa_search(?, " + FriendName(query) + ", r.GameName, r.DisplayName, r.Description, r.Category, r.ProviderKey) = 1"
                    : query.SearchFields == AchievementSearchFields.Editor
                        ? "pa_search(?, r.GameName, r.DisplayName, r.Description, r.ApiName) = 1"
                        : "pa_search(?, r.GameName, r.DisplayName) = 1");
                filterArgs.Add(query.SearchText);
            }

            foreach (var group in query.KeyGroups ?? Array.Empty<IReadOnlyList<string>>())
            {
                filters.Add(group.Count == 0 ? "0" : "(" + string.Join(" OR ", group.Select(key => KeyCondition(key, filterArgs))) + ")");
            }

            if (query.RequireUnlockTime)
            {
                filters.Add("r.Unlocked = 1 AND r.UnlockTimeUtc IS NOT NULL");
            }

            if (query.HideHiddenLocked)
            {
                filters.Add("NOT (r.Hidden = 1 AND r.Unlocked = 0)");
            }

            if (query.CapstonesOnly && !capstoneOrRarity)
            {
                filters.Add("r.IsCapstone = 1");
            }

            if (query.CategoryLabels != null)
            {
                filters.Add(InList(
                    "pa_category(r.Category)",
                    query.CategoryLabels.Select(AchievementCategoryHelper.NormalizeCategoryOrDefault).Distinct(StringComparer.Ordinal).ToList(),
                    filterArgs));
            }

            if (query.Customized.HasValue && !query.Friends)
            {
                filters.Add(query.Customized.Value ? CustomizedSql : "NOT " + CustomizedSql);
            }

            if (filters.Count > 0)
            {
                conditions.Add(query.KeptAchievements == null
                    ? string.Join(@"
              AND ", filters)
                    : "((" + string.Join(" AND ", filters) + ") OR " + AchievementPairsSql(query.KeptAchievements) + ")");
                args.AddRange(filterArgs);
            }

            return @"
            WHERE " + string.Join(@"
              AND ", conditions);
        }

        // The rows of these achievements, inline: a Guid's text is hex and dashes only, and the
        // API names are quoted literals, so a long list binds no variables.
        private static string AchievementPairsSql(IReadOnlyList<KeyValuePair<Guid, string>> achievements) =>
            "(r.PlayniteGameId IN (" + string.Join(", ", achievements.Select(pair => pair.Key).Distinct().Select(id => "'" + id.ToString("D") + "'")) +
            ") AND " + TextList("(r.PlayniteGameId || char(31) || r.ApiName) COLLATE NOCASE",
                achievements.Select(pair => pair.Key.ToString("D") + "\u001f" + pair.Value)) + ")";

        // The provider game of each Playnite game the current user's cache read picks: the most
        // recently updated progress of it, as SqlNadoCacheStore.LoadCurrentUserGameData orders
        // them. Not correlated, so SQLite builds the list once per statement.
        private const string LatestProgressSql = @"(r.IsAuthored = 1 OR r.GameId IN (
                SELECT lp.GameId FROM (
                    SELECT up.GameId AS GameId, ROW_NUMBER() OVER (
                        PARTITION BY pg.PlayniteGameId ORDER BY up.LastUpdatedUtc DESC, up.Id DESC) AS Latest
                    FROM UserGameProgress up
                    INNER JOIN Users cu ON cu.Id = up.UserId AND cu.IsCurrentUser = 1
                    INNER JOIN Games pg ON pg.Id = up.GameId
                    WHERE pg.PlayniteGameId IS NOT NULL AND TRIM(COALESCE(up.CacheKey, '')) <> '') lp
                WHERE lp.Latest = 1))";

        // A stored override that differs from what the metadata and platform layers resolve
        // without it, as AchievementCustomizationRules compares a row with its provider values.
        private static string OverrideDiffers(string column, Models.Achievements.AchievementField field, bool trim = false)
        {
            var stored = trim ? "NULLIF(TRIM(cx." + column + "), '')" : "cx." + column;
            var provider = AchievementLayerSql.ProviderValue(column, field);
            return "(" + stored + " IS NOT NULL AND " + stored + " IS NOT " + (trim ? "NULLIF(TRIM(" + provider + "), '')" : provider) + ")";
        }

        // Whether the editor marks the row customized: authored, an override row carrying a
        // change, or a capstone mark that differs from the platform's.
        private static readonly string CustomizedSql = @"(r.IsAuthored = 1
                OR EXISTS (SELECT 1 FROM " + AchievementLayerSql.AchievementsTable + @" cx
                    LEFT JOIN PlatformDefinitions ad ON ad.Id = r.Id
                    LEFT JOIN AchievementMetadataLayer m ON m.AchievementDefinitionId = ad.Id
                    WHERE cx.PlayniteGameId = r.PlayniteGameId AND cx.ApiName = r.ApiName AND cx.IsAuthored = 0 AND (
                        TRIM(COALESCE(cx.Note, '')) <> '' OR cx.GoalOrder IS NOT NULL OR cx.FilterOrder IS NOT NULL
                        OR cx.SummaryFilterOrder IS NOT NULL OR cx.UnlockTimeUtc IS NOT NULL OR cx.Cleared <> 0
                        OR " + OverrideDiffers("DisplayName", Models.Achievements.AchievementField.DisplayName, trim: true) + @"
                        OR " + OverrideDiffers("Description", Models.Achievements.AchievementField.Description, trim: true) + @"
                        OR " + OverrideDiffers("Points", Models.Achievements.AchievementField.Points) + @"
                        OR " + OverrideDiffers("TrophyType", Models.Achievements.AchievementField.TrophyType, trim: true) + @"
                        OR " + OverrideDiffers("UnlockedIconPath", Models.Achievements.AchievementField.UnlockedIcon, trim: true) + @"
                        OR " + OverrideDiffers("LockedIconPath", Models.Achievements.AchievementField.LockedIcon, trim: true) + @"
                        OR " + OverrideDiffers("Tags", Models.Achievements.AchievementField.Tags, trim: true) + @"
                        OR (cx.Hidden IS NOT NULL AND cx.Hidden IS NOT ad.Hidden)
                        OR (cx.Category IS NOT NULL AND pa_category(cx.Category) IS NOT pa_category(" +
                            AchievementLayerSql.ProviderValue("Category", Models.Achievements.AchievementField.Category) + @"))))
                OR (r.IsAuthored = 0 AND r.IsCapstone IS NOT (SELECT pd.IsCapstone FROM PlatformDefinitions pd WHERE pd.Id = r.Id)))";

        // A row's game provider: its platform key, else its provider key, else for an authored row
        // its Playnite game's.
        private const string ProviderSql =
            "COALESCE(NULLIF(TRIM(g.ProviderPlatformKey), ''), r.ProviderKey, (SELECT COALESCE(NULLIF(TRIM(pg.ProviderPlatformKey), ''), pg.ProviderKey) " +
            "FROM Games pg WHERE pg.PlayniteGameId = r.PlayniteGameId LIMIT 1))";

        // Never NULL, so NOT of it is the rows without progress.
        private const string HasProgressSql = "(r.ProgressNum IS NOT NULL AND COALESCE(r.ProgressDenom, 0) > 0)";

        private const string HasNoteSql = "TRIM(COALESCE(r.Note, '')) <> ''";

        // One key of AchievementQuery.KeyGroups.
        private static string KeyCondition(string key, List<object> args)
        {
            switch (key)
            {
                case "Unlocked":
                    return "r.Unlocked = 1";
                case "Locked":
                    return "r.Unlocked = 0";
                case "Visible":
                    return "COALESCE(r.Hidden, 0) = 0";
                case "Hidden":
                    return "r.Hidden = 1";
                case "InProgress":
                    return "(r.Unlocked = 0 AND " + HasProgressSql + ")";
                case "NoProgress":
                    return "NOT " + HasProgressSql;
                case "HasNotes":
                    return HasNoteSql;
                case "NoNotes":
                    return "NOT " + HasNoteSql;
                case "Capstone":
                    return "r.IsCapstone = 1";
                case "Common":
                case "Uncommon":
                case "Rare":
                case "UltraRare":
                    args.Add(key);
                    return RarityTierSql + " = ?";
                case "Platinum":
                case "Gold":
                case "Silver":
                case "Bronze":
                    args.Add(key.ToLowerInvariant());
                    return TrophySql + " = ?";
                default:
                    args.Add(key);
                    return "pa_classified(r.CategoryKind, r.Tags, ?) = 1";
            }
        }

        // The name a friend row shows: the one From joins for a named account, else the stored one.
        private static string FriendName(AchievementQuery query) =>
            query.FriendNames == null ? "u.DisplayName" : "COALESCE(fn.column3, u.DisplayName)";

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

        // Ids the database gave, inline: a long formats as digits only, and a page of keys would
        // otherwise bind thousands of variables. A null id matches no row.
        private static string IdList(string expression, IEnumerable<long?> ids)
        {
            var values = ids.Where(id => id.HasValue).Select(id => id.Value.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
            return values.Count == 0 ? "0" : expression + " IN (" + string.Join(", ", values) + ")";
        }

        // Playnite game ids the database gave, inline as text; a value that is not a Guid matches no row.
        private static string GameIdTextList(string expression, IEnumerable<string> ids) =>
            TextList(expression, ids.Where(id => Guid.TryParse(id, out _)));

        // Strings inline as SQL literals with single quotes doubled, so a page of keys binds no
        // variables. A null matches no row; so does a NUL character, which would end the SQL text.
        private static string TextList(string expression, IEnumerable<string> values)
        {
            var literals = values
                .Where(value => value != null && value.IndexOf('\0') < 0)
                .Distinct(StringComparer.Ordinal)
                .Select(value => "'" + value.Replace("'", "''") + "'")
                .ToList();
            return literals.Count == 0 ? "0" : expression + " IN (" + string.Join(", ", literals) + ")";
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
                case AchievementGroup.DayTally:
                    return "COALESCE(r.PlayniteGameId, '') || char(31) || COALESCE(" + KeySql(query, AchievementGroup.Day) + ", '') || char(31) || " + TallyFacetsSql;
                case AchievementGroup.Tally:
                    return "COALESCE(r.PlayniteGameId, '') || char(31) || " + TallyFacetsSql;
                default:
                    throw new ArgumentOutOfRangeException(nameof(group), group, null);
            }
        }

        private const string TallyFacetsSql = "COALESCE(r.Tags, '') || char(31) || " + RarityTierSql +
            " || char(31) || COALESCE(" + TrophySql + ", '') || char(31) || (r.IsCapstone IS 1)";

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

        private static string OrderBy(AchievementQuery query) =>
            (query.GoalsFirst
                ? "CASE WHEN r.IsGoal = 1 AND r.Unlocked = 0 THEN 0 ELSE 1 END, CASE WHEN r.IsGoal = 1 AND r.Unlocked = 0 THEN r.GoalOrder END, "
                : string.Empty) + SortOrder(query);

        private static string SortOrder(AchievementQuery query)
        {
            var direction = query.Descending ? " DESC" : " ASC";
            var friend = query.Friends ? FriendName(query) + " COLLATE NOCASE, " : string.Empty;
            var display = string.Join(", ", AchievementLayerSql.DisplayOrderSql.Split(',').Select(term => "r." + term.Trim()));

            // Equal keys order by game name, as the in-memory theme sorts broke ties, then by the
            // game's own order, so a key and a row read agree and no two reads differ.
            var tieBreak = ", " + friend + "r.GameName COLLATE NOCASE, r.PlayniteGameId, " + display + ", r.UserId";
            var unlock = UnlockBucketSql + ", r.UnlockTimeUtc" + direction;
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
                        ? FriendName(query) + " COLLATE NOCASE" + direction + ", " + unlock + tieBreak
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
                case AchievementSort.Provider:
                    return ProviderSql + " COLLATE NOCASE" + direction + ", " + unlock + tieBreak;
                case AchievementSort.Progress:
                    return "CASE WHEN r.Unlocked = 1 THEN 1.0 WHEN " + HasProgressSql + " THEN MIN(1.0, 1.0 * r.ProgressNum / r.ProgressDenom) ELSE 0 END" +
                           direction + ", " + unlock + tieBreak;
                case AchievementSort.Status:
                    return "r.Unlocked" + direction + ", " + unlock + tieBreak;
                case AchievementSort.Hidden:
                    return "COALESCE(r.Hidden, 0)" + direction + ", r.DisplayName COLLATE NOCASE" + tieBreak;
                case AchievementSort.Capstone:
                    return "r.IsCapstone" + direction + ", r.DisplayName COLLATE NOCASE" + tieBreak;
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
