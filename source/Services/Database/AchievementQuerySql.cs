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

        /// <summary>
        /// The current user's unlocks of each game's achievements across their accounts, with the
        /// latest resolved time.
        /// </summary>
        private static readonly string SelfCtes = @"WITH SelfUnlocks AS (
                SELECT s.PlayniteGameId AS PlayniteGameId, s.ApiName AS ApiName, MAX(s.UnlockTimeUtc) AS UnlockTimeUtc
                FROM " + AchievementLayerSql.AllUsersViewName + @" s
                WHERE s.IsCurrentUser = 1 AND s.IsAuthored = 0 AND s.Unlocked = 1
                GROUP BY s.PlayniteGameId, s.ApiName
            )
            ";

        private const string SelfJoins = @"
            LEFT JOIN SelfUnlocks me ON me.PlayniteGameId = r.PlayniteGameId AND me.ApiName = r.ApiName";

        /// <summary>A page of rows, in the query's order, with the friend identity and self state for friend rows.</summary>
        public static Command Rows(AchievementQuery query)
        {
            var args = new List<object>();
            var sql = new StringBuilder();
            if (query.Friends)
            {
                sql.Append(SelfCtes);
            }

            sql.Append("SELECT").Append(RowColumns);
            sql.Append(query.Friends
                ? FriendColumns
                : @",
                NULL AS UserProviderKey, NULL AS ExternalUserId, NULL AS UserDisplayName, NULL AS AvatarUrl, NULL AS AvatarPath,
                0 AS OwnershipConfirmed, 0 AS SelfUnlocked, NULL AS SelfUnlockTimeUtc, 0 AS SelfHasGameData");
            sql.Append(From(query));
            if (query.Friends)
            {
                sql.Append(SelfJoins);
            }

            sql.Append(Where(query, args));
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
                MIN(Percent) AS MinPercent
            FROM (SELECT " + KeySql(query, group) + @" AS Key,
                r.Unlocked AS Unlocked,
                " + RarityTierSql + @" AS Tier,
                " + TrophySql + @" AS Trophy,
                CASE WHEN r.IsCapstone = 1 THEN 1 ELSE 0 END AS IsCapstone,
                pa_prestige(r.GlobalPercentUnlocked, r.Rarity) AS Prestige,
                CASE WHEN r.Points > 0 THEN r.Points ELSE 0 END AS Points,
                CASE WHEN " + SoftcoreSql + @" THEN 1 ELSE 0 END AS Softcore,
                r.UnlockTimeUtc AS UnlockTimeUtc,
                r.GlobalPercentUnlocked AS Percent" +
                From(query) + Where(query, args) + @"
            LIMIT -1)
            GROUP BY Key;";
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
            }
            else
            {
                conditions.Add("r.IsCurrentUser = 1");
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

            return @"
            WHERE " + string.Join(@"
              AND ", conditions);
        }

        private static string InList(string expression, IReadOnlyList<string> values, List<object> args)
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
                default:
                    throw new ArgumentOutOfRangeException(nameof(group), group, null);
            }
        }

        private static string OrderBy(AchievementQuery query)
        {
            var direction = query.Descending ? " DESC" : " ASC";
            var friend = query.Friends ? "u.DisplayName, " : string.Empty;
            var display = string.Join(", ", AchievementLayerSql.DisplayOrderSql.Split(',').Select(term => "r." + term.Trim()));
            switch (query.Sort)
            {
                case AchievementSort.UnlockTime:
                    return "r.UnlockTimeUtc" + direction + ", " + friend + "r.PlayniteGameId, r.ApiName, r.UserId";
                case AchievementSort.GlobalPercent:
                    return "r.GlobalPercentUnlocked" + direction + ", r.UnlockTimeUtc DESC, " + friend + "r.PlayniteGameId, r.ApiName, r.UserId";
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
