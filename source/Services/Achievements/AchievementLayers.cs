using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services.Metadata;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Moves values between an <see cref="AchievementDetail"/> and its layers. A row's visible
    /// fields are always the resolved result; its <see cref="AchievementDetail.PlatformLayer"/> and
    /// <see cref="AchievementDetail.MetadataLayer"/> keep what each layer supplied, which is what
    /// the cache stores.
    /// </summary>
    internal static class AchievementLayers
    {
        /// <summary>
        /// The row's platform layer, or a snapshot of its fields when it has none yet (a fresh
        /// provider payload). The captured provider labels win over hydrated ones. The category is
        /// kept as given, Default included, so a row no layer categorizes reads as it did. Order is
        /// never the platform's: only a metadata source orders achievements.
        /// </summary>
        public static AchievementLayerValues PlatformOf(AchievementDetail row) =>
            row.PlatformLayer ?? new AchievementLayerValues(
                row.DisplayName,
                row.Description,
                row.UnlockedIconPath,
                row.LockedIconPath,
                row.Points,
                row.TrophyType,
                row.ProviderCategory ?? row.Category,
                row.ProviderCategoryKind ?? row.CategoryKind,
                row.ProviderTags ?? row.Tags,
                row.GlobalPercentUnlocked,
                row.Rarity,
                hidden: row.Hidden);

        /// <summary>
        /// Writes the resolved values of the row's layers into its fields, pinning its platform
        /// layer first so the row can be resolved again.
        /// </summary>
        public static ResolvedAchievementValues Resolve(AchievementDetail row)
        {
            row.PlatformLayer = PlatformOf(row);
            var resolved = LayerResolver.Resolve(row.PlatformLayer, row.MetadataLayer, row.CustomLayer);
            var values = resolved.Values;
            row.DisplayName = values.DisplayName;
            row.Description = values.Description;
            row.UnlockedIconPath = values.UnlockedIconPath;
            row.LockedIconPath = values.LockedIconPath;
            row.Points = values.Points;
            row.TrophyType = values.TrophyType;
            row.Category = values.Category;
            row.CategoryKind = values.CategoryKind;
            row.Tags = values.Tags;
            row.GlobalPercentUnlocked = values.GlobalPercentUnlocked;
            row.Rarity = values.Rarity ?? RarityTier.Common;
            row.MetadataOrderIndex = values.Order;
            row.Hidden = values.Hidden ?? false;
            return resolved;
        }

        /// <summary>
        /// The row as its platform gave it, for storing in the definitions table: the row itself
        /// when it has no platform layer yet, else a copy carrying the layer's values.
        /// </summary>
        public static AchievementDetail PlatformRow(AchievementDetail row)
        {
            var platform = row.PlatformLayer;
            if (platform == null)
            {
                return row;
            }

            return new AchievementDetail
            {
                ApiName = row.ApiName,
                DisplayName = platform.DisplayName,
                Description = platform.Description,
                UnlockedIconPath = platform.UnlockedIconPath,
                LockedIconPath = platform.LockedIconPath,
                Points = platform.Points,
                ScaledPoints = row.ScaledPoints,
                TrophyType = platform.TrophyType,
                Category = platform.Category ?? AchievementCategoryHelper.DefaultCategoryLabel,
                CategoryKind = platform.CategoryKind,
                Tags = platform.Tags,
                GlobalPercentUnlocked = platform.GlobalPercentUnlocked,
                Rarity = platform.Rarity ?? RarityTier.Common,
                Hidden = platform.Hidden ?? row.Hidden,
                IsCapstone = row.IsCapstone,
                ProgressDenom = row.ProgressDenom
            };
        }

        /// <summary>Gives the row's platform layer a stored rarity and resolves the row again.</summary>
        public static void KeepPlatformRarity(AchievementDetail row, double? percent, RarityTier rarity)
        {
            var p = PlatformOf(row);
            row.PlatformLayer = new AchievementLayerValues(
                p.DisplayName, p.Description, p.UnlockedIconPath, p.LockedIconPath, p.Points, p.TrophyType,
                p.Category, p.CategoryKind, p.Tags, percent, rarity, hidden: p.Hidden);
            Resolve(row);
        }

        /// <summary>
        /// The value areas whose shown values came from <paramref name="layer"/>, judged the way
        /// <see cref="DataLayers.Present"/> judges a whole game. Capstones are left out: the
        /// platform's come from its own capstone rows and metadata's from a borrowed capstone.
        /// </summary>
        public static CustomizationArea AreasFrom(IEnumerable<AchievementDetail> rows, DataLayer layer)
        {
            var projected = (rows ?? Enumerable.Empty<AchievementDetail>())
                .Where(row => row != null)
                .Select(row => Project(row.ApiName, LayerResolver.Resolve(PlatformOf(row), row.MetadataLayer, null), layer));
            return DataLayers.Present(projected) & ~CustomizationArea.Capstones;
        }

        private static AchievementDetail Project(string apiName, ResolvedAchievementValues resolved, DataLayer layer)
        {
            bool From(AchievementField field) => resolved.OriginOf(field) == layer;
            var values = resolved.Values;
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = From(AchievementField.DisplayName) ? values.DisplayName : null,
                Description = From(AchievementField.Description) ? values.Description : null,
                UnlockedIconPath = From(AchievementField.UnlockedIcon) ? values.UnlockedIconPath : null,
                LockedIconPath = From(AchievementField.LockedIcon) ? values.LockedIconPath : null,
                Points = From(AchievementField.Points) ? values.Points : null,
                TrophyType = From(AchievementField.TrophyType) ? values.TrophyType : null,
                Category = From(AchievementField.Category) ? values.Category : null,
                Tags = From(AchievementField.Tags) ? values.Tags : null
            };
        }
    }
}
