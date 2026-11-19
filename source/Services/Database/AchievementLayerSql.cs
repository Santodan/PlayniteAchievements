using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using SqlNado;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// The metadata and custom layer tables and the views that resolve the custom layer
    /// (<see cref="AchievementsTable"/>) and the metadata layer over the platform's
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

        /// <summary>
        /// Every user's achievements: the columns of <see cref="UserViewName"/> plus <c>UserId</c> and
        /// <c>IsCurrentUser</c>, for the current user and for friends. <see cref="UserViewName"/> is
        /// its <c>IsCurrentUser = 1</c> rows, numbered. Authored rows have a NULL <c>UserId</c>.
        /// </summary>
        public const string AllUsersViewName = "UserAchievementRows";

        /// <summary>
        /// The order the plugin lists a game's achievements in, over the columns of
        /// <see cref="AllUsersViewName"/>; <see cref="UserViewName"/> numbers rows by it (DisplayOrder).
        /// </summary>
        internal const string DisplayOrderSql = "CustomOrder IS NULL, CustomOrder, MetadataOrder IS NULL, MetadataOrder, IsAuthored, Id, ApiName";

        /// <summary>Every view this class creates, dependents first, including names retired since.</summary>
        public static readonly IReadOnlyList<string> AllViewNames = new[] { UserViewName, AllUsersViewName, ViewName, "AchievementDefinitionLayers", "ResolvedDefinitions" };

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

        /// <summary>The user's custom data per achievement: overrides, flags, positions and authored achievements.</summary>
        public const string AchievementsTable = "CustomLayerAchievements";

        /// <summary>The user's custom data per game: its flags and every setting no query reads (SettingsJson).</summary>
        public const string GamesTable = "CustomLayerGames";

        /// <summary>The user's settings per category: its kind, position and art.</summary>
        public const string CategoriesTable = "CustomLayerCategories";

        /// <summary>
        /// The custom layer tables earlier versions rebuilt from <c>GameCustomData</c> on every
        /// change. Those versions still rebuild them when run on this database, so the custom layer
        /// lives under its own names, which they never touch.
        /// </summary>
        public static readonly IReadOnlyList<string> RetiredMirrorTables = new[] { "AchievementOverrides", "GameCustomState", "AchievementCategories", "AchievementCategoryKinds", "AchievementFilters" };

        /// <summary>CacheMetadata key set when the <c>GameCustomData</c> payloads moved into the custom layer tables.</summary>
        public const string CustomLayerMigratedKey = "custom_layer_migrated";

        private static readonly string CreateAchievementsTableSql = @"CREATE TABLE IF NOT EXISTS " + AchievementsTable + @" (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                ApiName TEXT NOT NULL COLLATE NOCASE,
                DisplayName TEXT NULL,
                Description TEXT NULL,
                Points INTEGER NULL,
                TrophyType TEXT NULL COLLATE NOCASE,
                Category TEXT NULL,
                Tags TEXT NULL,
                Hidden INTEGER NULL,
                Cleared INTEGER NOT NULL DEFAULT 0,
                Note TEXT NULL,
                GoalOrder INTEGER NULL,
                CustomOrder INTEGER NULL,
                UnlockedIconPath TEXT NULL,
                LockedIconPath TEXT NULL,
                UnlockTimeUtc TEXT NULL,
                CapstoneAssigned INTEGER NOT NULL DEFAULT 0,
                IsAuthored INTEGER NOT NULL DEFAULT 0,
                Unlocked INTEGER NOT NULL DEFAULT 0,
                ProgressNum INTEGER NULL,
                ProgressDenom INTEGER NULL,
                ScaledPoints INTEGER NULL,
                CategoryKind TEXT NULL,
                GlobalPercentUnlocked REAL NULL,
                Rarity TEXT NULL,
                IsCapstone INTEGER NOT NULL DEFAULT 0,
                IsFiltered INTEGER NOT NULL DEFAULT 0,
                IsSummaryFiltered INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (PlayniteGameId, ApiName)
            );";

        private static readonly string CreateGamesTableSql = @"CREATE TABLE IF NOT EXISTS " + GamesTable + @" (
                PlayniteGameId TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
                CapstonesMaterialized INTEGER NOT NULL DEFAULT 0,
                HasManualLink INTEGER NOT NULL DEFAULT 0,
                ExcludedFromSummaries INTEGER NOT NULL DEFAULT 0,
                ExcludedFromRefreshes INTEGER NOT NULL DEFAULT 0,
                UseSeparateLockedIcons INTEGER NOT NULL DEFAULT 0,
                SettingsJson TEXT NOT NULL DEFAULT '{}'
            );";

        /// <summary>
        /// The user's settings per category path: its kind ("None" says it has none), its position in
        /// the custom category order and its custom art.
        /// </summary>
        private static readonly string CreateCategoriesTableSql = @"CREATE TABLE IF NOT EXISTS " + CategoriesTable + @" (
                PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                Category TEXT NOT NULL COLLATE NOCASE,
                Kind TEXT NULL,
                SortIndex INTEGER NULL,
                ImagePath TEXT NULL,
                PRIMARY KEY (PlayniteGameId, Category)
            );";

        /// <summary>Creates the custom layer tables. Both the cache and the custom-data store call it.</summary>
        public static void EnsureCustomLayerTables(SQLiteDatabase db)
        {
            db.ExecuteNonQuery(CreateAchievementsTableSql);
            db.ExecuteNonQuery(CreateGamesTableSql);
            db.ExecuteNonQuery(CreateCategoriesTableSql);
        }

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
            yield return AllUsersViewSql();
            yield return "CREATE VIEW " + UserViewName + " AS SELECT " +
                string.Join(", ", PublicColumnNames.Concat(UserColumnNames)) +
                ", ROW_NUMBER() OVER (PARTITION BY PlayniteGameId ORDER BY " + DisplayOrderSql + ") - 1 AS DisplayOrder" +
                " FROM " + AllUsersViewName + " WHERE IsCurrentUser = 1;";
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
              LEFT JOIN " + AchievementsTable + @" c ON c.PlayniteGameId = g.PlayniteGameId AND c.ApiName = ad.ApiName AND c.IsAuthored = 0
              LEFT JOIN " + GamesTable + @" gs ON gs.PlayniteGameId = g.PlayniteGameId
              LEFT JOIN " + CategoriesTable + @" k ON k.PlayniteGameId = g.PlayniteGameId AND k.Category = " + ResolvedCategory;

        private const string CaptureColumns =
            "cap.CleanPath AS CleanCapturePath, cap.NotificationPath AS NotificationCapturePath, " +
            "cap.FramedPath AS FramedCapturePath, cap.VideoPath AS VideoCapturePath";

        /// <summary>The kind the user stored for the achievement's resolved category, if any.</summary>
        private const string StoredKind = "k.Kind";

        private static string ResolvedCategory => "COALESCE(" + Value("Category", AchievementField.Category, custom: true) + ", 'Default')";

        /// <summary>A column of the user's settings for the given game and category.</summary>
        private static string CategoryValue(string column, string gameId, string category) =>
            "(SELECT k." + column + " FROM " + CategoriesTable + " k WHERE k.PlayniteGameId = " + gameId + " AND k.Category = " + category + ")";

        /// <summary>The public view's columns, in order; the user view's two halves list them alike.</summary>
        private static readonly string[] PublicColumnNames =
        {
            "Id", "GameId", "ApiName", "DisplayName", "Description", "UnlockedIconPath", "LockedIconPath", "Points",
            "ScaledPoints", "Category", "ProviderCategory", "Tags", "CategoryKind", "TrophyType", "Hidden", "IsCapstone",
            "GlobalPercentUnlocked", "Rarity", "ProgressMax", "MetadataOrder", "CreatedUtc", "UpdatedUtc", "IsFiltered",
            "IsSummaryFiltered", "Note", "IsGoal", "GoalOrder", "CustomOrder", "CategoryOrder", "CategoryArtPath"
        };

        /// <summary>The user view's columns after the public ones, in order, before DisplayOrder.</summary>
        private static readonly string[] UserColumnNames =
        {
            "PlayniteGameId", "GameName", "ProviderKey", "Unlocked", "UnlockTimeUtc", "ProgressNum", "ProgressDenom",
            "ExcludedFromSummaries", "IsAuthored", "CleanCapturePath", "NotificationCapturePath", "FramedCapturePath",
            "VideoCapturePath"
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
        /// Every user's achievements: each definition of a game the user has, with unlock state and
        /// unlock time, plus the achievements the current user authored. Unlock-time overrides and
        /// clears and authored achievements apply to the current user only.
        /// </summary>
        /// <remarks>
        /// No window function, so SQLite applies any filter before reading the layers; queries
        /// order by <see cref="DisplayOrderSql"/> instead of a row number. Unlocked and locked
        /// definitions are separate halves with a constant <c>Unlocked</c>, so a filter on it skips
        /// the other half and an unlocked read starts from the unlocked rows.
        /// </remarks>
        private static string AllUsersViewSql()
        {
            var authored = string.Join(",\n                ", PublicColumnNames.Select(name =>
                (AuthoredColumns.TryGetValue(name, out var expression) ? expression : "a." + name) + " AS " + name));
            return "CREATE VIEW " + AllUsersViewName + " AS " + PlatformRowsSql(unlocked: true) + @"
            UNION ALL " + PlatformRowsSql(unlocked: false) + @"
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
                " + CaptureColumns + @",
                NULL AS UserId,
                1 AS IsCurrentUser
              FROM " + AchievementsTable + @" a
              LEFT JOIN " + GamesTable + @" gs ON gs.PlayniteGameId = a.PlayniteGameId
              LEFT JOIN AchievementCaptures cap ON cap.PlayniteGameId = a.PlayniteGameId AND cap.ApiName = a.ApiName
              WHERE a.IsAuthored = 1;";
        }

        /// <summary>
        /// The definitions of each game a user has, the unlocked ones or the rest. Unlock-time
        /// overrides and clears apply to the current user's unlocked rows of games without a manual
        /// link, as <see cref="Models.Settings.AchievementOverride.ApplyUnlockTimeTo"/> applies them.
        /// </summary>
        private static string PlatformRowsSql(bool unlocked)
        {
            var platform = string.Join(",\n                ", PublicColumnNames.Select(name => "ad." + name + " AS " + name));
            var unlockTime = unlocked
                ? @"CASE WHEN usr.IsCurrentUser = 0 OR COALESCE(gs.HasManualLink, 0) = 1 THEN ua.UnlockTimeUtc
                     WHEN c.UnlockTimeUtc IS NOT NULL THEN c.UnlockTimeUtc
                     WHEN (COALESCE(c.Cleared, 0) & " + (int)AchievementField.UnlockTime + @") <> 0 THEN NULL
                     ELSE ua.UnlockTimeUtc END"
                : "ua.UnlockTimeUtc";
            var achievements = unlocked
                ? @"INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
              INNER JOIN " + ViewName + @" ad ON ad.Id = ua.AchievementDefinitionId AND ad.GameId = ugp.GameId"
                : @"INNER JOIN " + ViewName + @" ad ON ad.GameId = ugp.GameId
              LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.AchievementDefinitionId = ad.Id";
            return @"SELECT
                " + platform + @",
                g.PlayniteGameId AS PlayniteGameId,
                g.GameName AS GameName,
                g.ProviderKey AS ProviderKey,
                " + (unlocked ? "1" : "0") + @" AS Unlocked,
                " + unlockTime + @" AS UnlockTimeUtc,
                ua.ProgressNum AS ProgressNum,
                ua.ProgressDenom AS ProgressDenom,
                COALESCE(gs.ExcludedFromSummaries, 0) AS ExcludedFromSummaries,
                0 AS IsAuthored,
                " + CaptureColumns + @",
                usr.Id AS UserId,
                usr.IsCurrentUser AS IsCurrentUser
              FROM UserGameProgress ugp
              INNER JOIN Users usr ON usr.Id = ugp.UserId
              INNER JOIN Games g ON g.Id = ugp.GameId
              " + achievements + @"
              LEFT JOIN " + AchievementsTable + @" c ON c.PlayniteGameId = g.PlayniteGameId AND c.ApiName = ad.ApiName AND c.IsAuthored = 0
              LEFT JOIN " + GamesTable + @" gs ON gs.PlayniteGameId = g.PlayniteGameId
              LEFT JOIN AchievementCaptures cap ON cap.PlayniteGameId = g.PlayniteGameId AND cap.ApiName = ad.ApiName" +
              (unlocked ? string.Empty : @"
              WHERE ua.Unlocked IS NOT 1");
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
