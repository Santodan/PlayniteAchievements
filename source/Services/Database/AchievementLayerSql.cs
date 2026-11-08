using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// The metadata layer table and the <see cref="ViewName"/> view that resolves it over the
    /// platform's <c>AchievementDefinitions</c>, with the same precedence as
    /// <see cref="LayerResolver"/>. Reads that show an achievement's values go through the view;
    /// writes and identity checks use the tables.
    /// </summary>
    internal static class AchievementLayerSql
    {
        public const string ViewName = "ResolvedDefinitions";

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
                FOREIGN KEY (AchievementDefinitionId) REFERENCES AchievementDefinitions(Id) ON DELETE CASCADE
            );";

        // Definition deletes do not always run with foreign keys on, so a trigger drops the layer rows.
        public const string CreateTriggerSql = @"CREATE TRIGGER IF NOT EXISTS TR_AchievementDefinitions_DeleteMetadataLayer
                AFTER DELETE ON AchievementDefinitions
                BEGIN
                    DELETE FROM AchievementMetadataLayer WHERE AchievementDefinitionId = OLD.Id;
                END;";

        public static string InsertSql =>
            "INSERT OR REPLACE INTO AchievementMetadataLayer (AchievementDefinitionId, " +
            string.Join(", ", MetadataColumns) + ") VALUES (?" + string.Concat(MetadataColumns.Select(_ => ", ?")) + ");";

        public const string DeleteSql = "DELETE FROM AchievementMetadataLayer WHERE AchievementDefinitionId = ?;";

        /// <summary>Dropped and recreated on every start, so it always matches this code.</summary>
        public static IEnumerable<string> RecreateViewSql()
        {
            yield return "DROP VIEW IF EXISTS " + ViewName + ";";
            yield return "CREATE VIEW " + ViewName + @" AS SELECT
                ad.Id AS Id,
                ad.GameId AS GameId,
                ad.ApiName AS ApiName,
                " + Pick("DisplayName", AchievementField.DisplayName) + @",
                " + Pick("Description", AchievementField.Description) + @",
                " + Pick("UnlockedIconPath", AchievementField.UnlockedIcon) + @",
                " + Pick("LockedIconPath", AchievementField.LockedIcon) + @",
                " + Pick("Points", AchievementField.Points) + @",
                ad.ScaledPoints AS ScaledPoints,
                COALESCE(" + Value("Category", AchievementField.Category) + @", 'Default') AS Category,
                " + Tags() + @" AS Tags,
                " + Pick("CategoryKind", AchievementField.CategoryKind) + @",
                " + Pick("TrophyType", AchievementField.TrophyType) + @",
                ad.Hidden AS Hidden,
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
                ad.DisplayName AS P_DisplayName,
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
              FROM AchievementDefinitions ad
              LEFT JOIN AchievementMetadataLayer m ON m.AchievementDefinitionId = ad.Id;";
        }

        private static string IsCleared(AchievementField field) => "(COALESCE(m.Cleared, 0) & " + (int)field + ") <> 0";

        private static string Value(string column, AchievementField field) =>
            "CASE WHEN " + IsCleared(field) + " THEN NULL ELSE COALESCE(m." + column + ", ad." + column + ") END";

        private static string Pick(string column, AchievementField field) => Value(column, field) + " AS " + column;

        /// <summary>
        /// The tags <see cref="LayerResolver.ResolveTags"/> gives when metadata decides them: the
        /// source's, plus the platform's derived tags and Unobtainable.
        /// </summary>
        private static string Tags()
        {
            string Keep(string key) =>
                "CASE WHEN ('|' || COALESCE(ad.Tags, '') || '|') LIKE '%|" + key + "|%' THEN '|" + key + "' ELSE '' END";
            return "CASE WHEN m.Tags IS NULL AND NOT " + IsCleared(AchievementField.Tags) + @" THEN ad.Tags
                     ELSE NULLIF(TRIM(COALESCE(m.Tags, '') || " +
                   Keep(AchievementTagHelper.Softcore) + " || " +
                   Keep(AchievementTagHelper.Hardcore) + " || " +
                   Keep(AchievementTagHelper.Unobtainable) + ", '|'), '') END";
        }
    }
}
