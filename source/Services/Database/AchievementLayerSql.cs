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
    /// values; it only ever gains columns. <see cref="LayerViewName"/> adds each layer's raw
    /// values for the plugin's own loader. Writes and identity checks use the tables.
    /// </remarks>
    internal static class AchievementLayerSql
    {
        public const string ViewName = "AchievementDefinitions";

        public const string LayerViewName = "AchievementDefinitionLayers";

        /// <summary>
        /// The current user's achievements with their unlock state: one row per achievement of each
        /// game the user has, carrying every column of <see cref="ViewName"/>.
        /// </summary>
        public const string UserViewName = "ResolvedAchievements";

        /// <summary>Every view this class creates, including names retired since.</summary>
        public static readonly IReadOnlyList<string> AllViewNames = new[] { UserViewName, ViewName, LayerViewName, "ResolvedDefinitions" };

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
            yield return "CREATE VIEW " + LayerViewName + " AS SELECT\n" + PublicColumns() + ",\n" + RawLayerColumns() + FromClause + ";";
            yield return "CREATE VIEW " + UserViewName + @" AS SELECT
                g.PlayniteGameId AS PlayniteGameId,
                g.GameName AS GameName,
                g.ProviderKey AS ProviderKey,
                ad.*,
                COALESCE(ua.Unlocked, 0) AS Unlocked,
                ua.UnlockTimeUtc AS UnlockTimeUtc,
                ua.ProgressNum AS ProgressNum,
                ua.ProgressDenom AS ProgressDenom
              FROM UserGameProgress ugp
              INNER JOIN Users u ON u.Id = ugp.UserId AND u.IsCurrentUser = 1
              INNER JOIN Games g ON g.Id = ugp.GameId
              INNER JOIN " + ViewName + @" ad ON ad.GameId = ugp.GameId
              LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.AchievementDefinitionId = ad.Id;";
        }

        private static string PublicColumns() => @"                ad.Id AS Id,
                ad.GameId AS GameId,
                ad.ApiName AS ApiName,
                " + Pick("DisplayName", AchievementField.DisplayName, custom: true) + @",
                " + Pick("Description", AchievementField.Description, custom: true) + @",
                " + Pick("UnlockedIconPath", AchievementField.UnlockedIcon) + @",
                " + Pick("LockedIconPath", AchievementField.LockedIcon) + @",
                " + Pick("Points", AchievementField.Points, custom: true) + @",
                ad.ScaledPoints AS ScaledPoints,
                COALESCE(" + Value("Category", AchievementField.Category, custom: true) + @", 'Default') AS Category,
                COALESCE(" + Value("Category", AchievementField.Category) + @", 'Default') AS ProviderCategory,
                " + Tags() + @" AS Tags,
                " + Pick("CategoryKind", AchievementField.CategoryKind) + @",
                " + Pick("TrophyType", AchievementField.TrophyType, custom: true) + @",
                COALESCE(c.Hidden, ad.Hidden) AS Hidden,
                ad.IsCapstone AS IsCapstone,
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
                c.CustomOrder AS CustomOrder";

        private static string RawLayerColumns() => @"                ad.DisplayName AS P_DisplayName,
                ad.Description AS P_Description,
                ad.UnlockedIconPath AS P_UnlockedIconPath,
                ad.LockedIconPath AS P_LockedIconPath,
                ad.Points AS P_Points,
                ad.Category AS P_Category,
                ad.Tags AS P_Tags,
                ad.CategoryKind AS P_CategoryKind,
                ad.TrophyType AS P_TrophyType,
                ad.GlobalPercentUnlocked AS P_GlobalPercentUnlocked,
                ad.Rarity AS P_Rarity,
                ad.MetadataOrder AS P_MetadataOrder,
                m.AchievementDefinitionId IS NOT NULL AS HasMetadata,
                " + string.Join(",\n                ", MetadataColumns.Select(column => "m." + column + " AS M_" + column)) + @"
";

        private const string FromClause = @"
              FROM PlatformDefinitions ad
              LEFT JOIN AchievementMetadataLayer m ON m.AchievementDefinitionId = ad.Id
              LEFT JOIN Games g ON g.Id = ad.GameId
              LEFT JOIN AchievementOverrides c ON c.PlayniteGameId = g.PlayniteGameId AND c.ApiName = ad.ApiName";

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
