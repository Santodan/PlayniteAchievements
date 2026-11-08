using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// The metadata layer table and the views that resolve the custom layer mirror
    /// (<c>AchievementOverrides</c>) and the metadata layer over the platform's
    /// <c>PlatformDefinitions</c>, with the same precedence as <see cref="LayerResolver"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ViewName"/> keeps the name and columns the definitions table had before the
    /// layers, so other add-ons that read the database keep their queries and see resolved
    /// values; it only ever gains columns. The plugin's loader and writers use the tables.
    /// </remarks>
    internal static class AchievementLayerSql
    {
        public const string ViewName = "AchievementDefinitions";

        /// <summary>
        /// The current user's achievements with their unlock state: one row per achievement of each
        /// game the user has, carrying every column of <see cref="ViewName"/>, plus the achievements
        /// the user authored (<c>IsAuthored</c>, no <c>Id</c>).
        /// </summary>
        public const string UserViewName = "ResolvedAchievements";

        /// <summary>Every view this class creates, including names retired since.</summary>
        public static readonly IReadOnlyList<string> AllViewNames = new[] { UserViewName, ViewName, "AchievementDefinitionLayers", "ResolvedDefinitions" };

        /// <summary>The metadata layer columns, in insert order, after AchievementDefinitionId.</summary>
        public static readonly IReadOnlyList<string> MetadataColumns = new[]
        {
            "DisplayName", "Description", "UnlockedIconPath", "LockedIconPath", "Points", "TrophyType",
            "Category", "CategoryKind", "Tags", "GlobalPercentUnlocked", "Rarity", "MetadataOrder", "Cleared"
        };

        public const string CreateTableSql = @"CREATE TABLE IF NOT EXISTS AchievementMetadataLayer (
                AchievementDefinitionId INTEGER PRIMARY KEY NOT NULL,
                DisplayName TEXT NULL,
                Description TEXT NULL,
                UnlockedIconPath TEXT NULL,
                LockedIconPath TEXT NULL,
                Points INTEGER NULL,
                TrophyType TEXT NULL,
                Category TEXT NULL,
                CategoryKind TEXT NULL,
                Tags TEXT NULL,
                GlobalPercentUnlocked REAL NULL,
                Rarity TEXT NULL,
                MetadataOrder INTEGER NULL,
                Cleared INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (AchievementDefinitionId) REFERENCES PlatformDefinitions(Id) ON DELETE CASCADE
            );";

        // Definition deletes do not always run with foreign keys on, so a trigger drops the layer rows.
        public const string CreateTriggerSql = @"CREATE TRIGGER IF NOT EXISTS TR_AchievementDefinitions_DeleteMetadataLayer
                AFTER DELETE ON PlatformDefinitions
                BEGIN
                    DELETE FROM AchievementMetadataLayer WHERE AchievementDefinitionId = OLD.Id;
                END;";

        /// <summary>Game-level custom state the views need, mirrored from the custom-data store.</summary>
        public const string CreateGameStateTableSql = @"CREATE TABLE IF NOT EXISTS GameCustomState (
                PlayniteGameId TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
                CapstonesMaterialized INTEGER NOT NULL DEFAULT 0,
                HasManualLink INTEGER NOT NULL DEFAULT 0,
                ExcludedFromSummaries INTEGER NOT NULL DEFAULT 0,
                ExcludedFromRefreshes INTEGER NOT NULL DEFAULT 0,
                UseSeparateLockedIcons INTEGER NOT NULL DEFAULT 0
            );";

        /// <summary>
        /// The user's settings per category path: its kind ("None" says it has none), its position in
        /// the custom category order and its custom art.
        /// </summary>
        public const string CreateCategoriesTableSql = @"CREATE TABLE IF NOT EXISTS AchievementCategories (
                PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                Category TEXT NOT NULL COLLATE NOCASE,
                Kind TEXT NULL,
                SortIndex INTEGER NULL,
                ImagePath TEXT NULL,
                PRIMARY KEY (PlayniteGameId, Category)
            );";

        /// <summary>Each achievement's capture files, as the plugin matches them to it.</summary>
        public const string CreateCapturesTableSql = @"CREATE TABLE IF NOT EXISTS AchievementCaptures (
                PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                ApiName TEXT NOT NULL COLLATE NOCASE,
                CleanPath TEXT NULL,
                NotificationPath TEXT NULL,
                FramedPath TEXT NULL,
                VideoPath TEXT NULL,
                PRIMARY KEY (PlayniteGameId, ApiName)
            );";

        public static string InsertSql =>
            "INSERT OR REPLACE INTO AchievementMetadataLayer (AchievementDefinitionId, " +
            string.Join(", ", MetadataColumns) + ") VALUES (?" + string.Concat(MetadataColumns.Select(_ => ", ?")) + ");";

        public const string DeleteSql = "DELETE FROM AchievementMetadataLayer WHERE AchievementDefinitionId = ?;";

        /// <summary>Dropped and recreated on every start, so they always match this code.</summary>
        public static IEnumerable<string> RecreateViewSql()
        {
            foreach (var view in AllViewNames)
            {
                yield return "DROP VIEW IF EXISTS " + view + ";";
            }

            yield return "CREATE VIEW " + ViewName + " AS SELECT\n" + PublicColumns() + FromClause + ";";
            yield return UserViewSql();
        }

        private static string PublicColumns() => @"                ad.Id AS Id,
                ad.GameId AS GameId,
                ad.ApiName AS ApiName,
                " + Pick("DisplayName", AchievementField.DisplayName, custom: true) + @",
                " + Pick("Description", AchievementField.Description, custom: true) + @",
                " + Pick("UnlockedIconPath", AchievementField.UnlockedIcon, custom: true) + @",
                " + Pick("LockedIconPath", AchievementField.LockedIcon, custom: true) + @",
                " + Pick("Points", AchievementField.Points, custom: true) + @",
                ad.ScaledPoints AS ScaledPoints,
                COALESCE(" + Value("Category", AchievementField.Category, custom: true) + @", 'Default') AS Category,
                COALESCE(" + Value("Category", AchievementField.Category) + @", 'Default') AS ProviderCategory,
                " + Tags() + @" AS Tags,
                CASE WHEN " + StoredKind + @" IS NULL THEN " + Value("CategoryKind", AchievementField.CategoryKind) + @"
                     WHEN " + StoredKind + @" = 'None' THEN NULL
                     ELSE " + StoredKind + @" END AS CategoryKind,
                " + Pick("TrophyType", AchievementField.TrophyType, custom: true) + @",
                COALESCE(c.Hidden, ad.Hidden) AS Hidden,
                CASE WHEN COALESCE(gs.CapstonesMaterialized, 0) = 1 THEN COALESCE(c.CapstoneAssigned, 0)
                     ELSE ad.IsCapstone END AS IsCapstone,
                CASE WHEN " + IsCleared(AchievementField.Rarity) + @" THEN NULL
                     WHEN m.Rarity IS NOT NULL THEN m.GlobalPercentUnlocked
                     ELSE ad.GlobalPercentUnlocked END AS GlobalPercentUnlocked,
                CASE WHEN " + IsCleared(AchievementField.Rarity) + @" THEN 'Common'
                     ELSE COALESCE(m.Rarity, ad.Rarity) END AS Rarity,
                ad.ProgressMax AS ProgressMax,
                " + Pick("MetadataOrder", AchievementField.Order) + @",
                ad.CreatedUtc AS CreatedUtc,
                ad.UpdatedUtc AS UpdatedUtc,
                COALESCE(c.IsFiltered, 0) AS IsFiltered,
                COALESCE(c.IsSummaryFiltered, 0) AS IsSummaryFiltered,
                c.Note AS Note,
                c.GoalOrder IS NOT NULL AS IsGoal,
                c.GoalOrder AS GoalOrder,
                c.CustomOrder AS CustomOrder,
                k.SortIndex AS CategoryOrder,
                k.ImagePath AS CategoryArtPath";

        private static readonly string FromClause = @"
              FROM PlatformDefinitions ad
              LEFT JOIN AchievementMetadataLayer m ON m.AchievementDefinitionId = ad.Id
              LEFT JOIN Games g ON g.Id = ad.GameId
              LEFT JOIN AchievementOverrides c ON c.PlayniteGameId = g.PlayniteGameId AND c.ApiName = ad.ApiName AND c.IsAuthored = 0
              LEFT JOIN GameCustomState gs ON gs.PlayniteGameId = g.PlayniteGameId
              LEFT JOIN AchievementCategories k ON k.PlayniteGameId = g.PlayniteGameId AND k.Category = " + ResolvedCategory;

        private const string CaptureColumns =
            "cap.CleanPath AS CleanCapturePath, cap.NotificationPath AS NotificationCapturePath, " +
            "cap.FramedPath AS FramedCapturePath, cap.VideoPath AS VideoCapturePath";

        /// <summary>The kind the user stored for the achievement's resolved category, if any.</summary>
        private const string StoredKind = "k.Kind";

        private static string ResolvedCategory => "COALESCE(" + Value("Category", AchievementField.Category, custom: true) + ", 'Default')";

        /// <summary>A column of the user's settings for the given game and category.</summary>
        private static string CategoryValue(string column, string gameId, string category) =>
            "(SELECT k." + column + " FROM AchievementCategories k WHERE k.PlayniteGameId = " + gameId + " AND k.Category = " + category + ")";

        /// <summary>The public view's columns, in order; the user view's two halves list them alike.</summary>
        private static readonly string[] PublicColumnNames =
        {
            "Id", "GameId", "ApiName", "DisplayName", "Description", "UnlockedIconPath", "LockedIconPath", "Points",
            "ScaledPoints", "Category", "ProviderCategory", "Tags", "CategoryKind", "TrophyType", "Hidden", "IsCapstone",
            "GlobalPercentUnlocked", "Rarity", "ProgressMax", "MetadataOrder", "CreatedUtc", "UpdatedUtc", "IsFiltered",
            "IsSummaryFiltered", "Note", "IsGoal", "GoalOrder", "CustomOrder", "CategoryOrder", "CategoryArtPath"
        };

        /// <summary>How an authored achievement's mirror row fills each public column.</summary>
        private static readonly Dictionary<string, string> AuthoredColumns = new Dictionary<string, string>
        {
            ["Id"] = "NULL",
            ["GameId"] = "NULL",
            ["Category"] = "COALESCE(a.Category, 'Default')",
            ["ProviderCategory"] = "COALESCE(a.Category, 'Default')",
            ["Hidden"] = "COALESCE(a.Hidden, 0)",
            ["Rarity"] = "COALESCE(a.Rarity, 'Common')",
            ["ProgressMax"] = "a.ProgressDenom",
            ["MetadataOrder"] = "NULL",
            ["CreatedUtc"] = "NULL",
            ["IsGoal"] = "a.GoalOrder IS NOT NULL",
            ["CategoryOrder"] = CategoryValue("SortIndex", "a.PlayniteGameId", "COALESCE(a.Category, 'Default')"),
            ["CategoryArtPath"] = CategoryValue("ImagePath", "a.PlayniteGameId", "COALESCE(a.Category, 'Default')")
        };

        /// <summary>
        /// The current user's achievements: each definition of a game the user has, with unlock
        /// state and the user's unlock time, plus the achievements the user authored, numbered in
        /// the order the plugin lists them (DisplayOrder).
        /// </summary>
        private static string UserViewSql()
        {
            var unlockTimeBit = (int)AchievementField.UnlockTime;
            var platform = string.Join(",\n                ", PublicColumnNames.Select(name => "ad." + name + " AS " + name));
            var authored = string.Join(",\n                ", PublicColumnNames.Select(name =>
                (AuthoredColumns.TryGetValue(name, out var expression) ? expression : "a." + name) + " AS " + name));
            return "CREATE VIEW " + UserViewName + @" AS SELECT u.*,
                ROW_NUMBER() OVER (PARTITION BY u.PlayniteGameId ORDER BY u.CustomOrder IS NULL, u.CustomOrder,
                    u.MetadataOrder IS NULL, u.MetadataOrder, u.IsAuthored, u.Id, u.ApiName) - 1 AS DisplayOrder
              FROM (SELECT
                " + platform + @",
                g.PlayniteGameId AS PlayniteGameId,
                g.GameName AS GameName,
                g.ProviderKey AS ProviderKey,
                COALESCE(ua.Unlocked, 0) AS Unlocked,
                CASE WHEN COALESCE(ua.Unlocked, 0) = 1 AND c.UnlockTimeUtc IS NOT NULL THEN c.UnlockTimeUtc
                     WHEN COALESCE(ua.Unlocked, 0) = 1 AND (COALESCE(c.Cleared, 0) & " + unlockTimeBit + @") <> 0 THEN NULL
                     ELSE ua.UnlockTimeUtc END AS UnlockTimeUtc,
                ua.ProgressNum AS ProgressNum,
                ua.ProgressDenom AS ProgressDenom,
                COALESCE(gs.ExcludedFromSummaries, 0) AS ExcludedFromSummaries,
                0 AS IsAuthored,
                " + CaptureColumns + @"
              FROM UserGameProgress ugp
              INNER JOIN Users u ON u.Id = ugp.UserId AND u.IsCurrentUser = 1
              INNER JOIN Games g ON g.Id = ugp.GameId
              INNER JOIN " + ViewName + @" ad ON ad.GameId = ugp.GameId
              LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.AchievementDefinitionId = ad.Id
              LEFT JOIN AchievementOverrides c ON c.PlayniteGameId = g.PlayniteGameId AND c.ApiName = ad.ApiName AND c.IsAuthored = 0
              LEFT JOIN GameCustomState gs ON gs.PlayniteGameId = g.PlayniteGameId
              LEFT JOIN AchievementCaptures cap ON cap.PlayniteGameId = g.PlayniteGameId AND cap.ApiName = ad.ApiName
            UNION ALL SELECT
                " + authored + @",
                a.PlayniteGameId AS PlayniteGameId,
                (SELECT gn.GameName FROM Games gn WHERE gn.PlayniteGameId = a.PlayniteGameId AND gn.GameName IS NOT NULL LIMIT 1) AS GameName,
                NULL AS ProviderKey,
                a.Unlocked AS Unlocked,
                a.UnlockTimeUtc AS UnlockTimeUtc,
                a.ProgressNum AS ProgressNum,
                a.ProgressDenom AS ProgressDenom,
                COALESCE(gs.ExcludedFromSummaries, 0) AS ExcludedFromSummaries,
                1 AS IsAuthored,
                " + CaptureColumns + @"
              FROM AchievementOverrides a
              LEFT JOIN GameCustomState gs ON gs.PlayniteGameId = a.PlayniteGameId
              LEFT JOIN AchievementCaptures cap ON cap.PlayniteGameId = a.PlayniteGameId AND cap.ApiName = a.ApiName
              WHERE a.IsAuthored = 1) u;";
        }

        private static string IsCleared(AchievementField field, string layer = "m") =>
            "(COALESCE(" + layer + ".Cleared, 0) & " + (int)field + ") <> 0";

        /// <summary>
        /// The first layer that supplies the column, set or cleared: custom (when it carries the
        /// field), then metadata, then the platform, as <see cref="LayerResolver"/> picks it.
        /// </summary>
        private static string Value(string column, AchievementField field, bool custom = false) =>
            "CASE " +
            (custom ? "WHEN c." + column + " IS NOT NULL THEN c." + column + " WHEN " + IsCleared(field, "c") + " THEN NULL " : string.Empty) +
            "WHEN m." + column + " IS NOT NULL THEN m." + column + " WHEN " + IsCleared(field) + " THEN NULL " +
            "ELSE ad." + column + " END";

        private static string Pick(string column, AchievementField field, bool custom = false) =>
            Value(column, field, custom) + " AS " + column;

        /// <summary>
        /// The tags <see cref="LayerResolver.ResolveTags"/> gives: the deciding layer's, plus the
        /// platform's derived tags always and its Unobtainable when metadata decides.
        /// </summary>
        private static string Tags()
        {
            string Keep(string key) =>
                "CASE WHEN ('|' || COALESCE(ad.Tags, '') || '|') LIKE '%|" + key + "|%' THEN '|" + key + "' ELSE '' END";
            var derived = Keep(AchievementTagHelper.Softcore) + " || " + Keep(AchievementTagHelper.Hardcore);
            return "CASE WHEN c.Tags IS NOT NULL OR " + IsCleared(AchievementField.Tags, "c") + @"
                     THEN NULLIF(TRIM(COALESCE(c.Tags, '') || " + derived + @", '|'), '')
                     WHEN m.Tags IS NOT NULL OR " + IsCleared(AchievementField.Tags) + @"
                     THEN NULLIF(TRIM(COALESCE(m.Tags, '') || " + derived + " || " + Keep(AchievementTagHelper.Unobtainable) + @", '|'), '')
                     ELSE ad.Tags END";
        }
    }
}
