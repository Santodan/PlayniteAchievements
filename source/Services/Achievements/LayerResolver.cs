using PlayniteAchievements.Models.Achievements;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// An achievement's values resolved across layers, with the layer each field came from.
    /// </summary>
    public sealed class ResolvedAchievementValues
    {
        internal ResolvedAchievementValues(AchievementLayerValues values, IReadOnlyDictionary<AchievementField, DataLayer> origin)
        {
            Values = values;
            Origin = origin;
        }

        public AchievementLayerValues Values { get; }

        /// <summary>
        /// The layer that decided each single field (never a combined flag such as Icons);
        /// <see cref="DataLayer.None"/> when no layer supplied it.
        /// </summary>
        private IReadOnlyDictionary<AchievementField, DataLayer> Origin { get; }

        public DataLayer OriginOf(AchievementField field) =>
            Origin.TryGetValue(field, out var layer) ? layer : DataLayer.None;

    }

    /// <summary>
    /// Resolves an achievement's values as custom over metadata over platform: each field comes
    /// from the highest layer that supplies it, set or cleared. Tags are the one exception, see
    /// <see cref="ResolveTags"/>.
    /// </summary>
    /// <remarks>
    /// Paired values (an icon pair, a category and its kind) stay paired because the layer that
    /// writes them supplies both, clearing the half it has no value for. The resolver itself has
    /// no per-field rules beyond Tags.
    /// </remarks>
    public static class LayerResolver
    {
        /// <summary>Every single field, in a stable order.</summary>
        private static readonly IReadOnlyList<AchievementField> SingleFields = new[]
        {
            AchievementField.DisplayName,
            AchievementField.Description,
            AchievementField.UnlockedIcon,
            AchievementField.LockedIcon,
            AchievementField.Points,
            AchievementField.TrophyType,
            AchievementField.Category,
            AchievementField.CategoryKind,
            AchievementField.Tags,
            AchievementField.Rarity,
            AchievementField.Order,
            AchievementField.Hidden,
            AchievementField.UnlockTime
        };

        public static ResolvedAchievementValues Resolve(
            AchievementLayerValues platform,
            AchievementLayerValues metadata,
            AchievementLayerValues custom)
        {
            platform = platform ?? AchievementLayerValues.Empty;
            metadata = metadata ?? AchievementLayerValues.Empty;
            custom = custom ?? AchievementLayerValues.Empty;

            var origin = new Dictionary<AchievementField, DataLayer>();
            foreach (var field in SingleFields)
            {
                origin[field] = Decider(field, platform, metadata, custom);
            }

            AchievementLayerValues From(AchievementField field) => Layer(origin[field], platform, metadata, custom);

            var rarity = From(AchievementField.Rarity);
            var resolved = new AchievementLayerValues(
                From(AchievementField.DisplayName).DisplayName,
                From(AchievementField.Description).Description,
                From(AchievementField.UnlockedIcon).UnlockedIconPath,
                From(AchievementField.LockedIcon).LockedIconPath,
                From(AchievementField.Points).Points,
                From(AchievementField.TrophyType).TrophyType,
                From(AchievementField.Category).Category,
                From(AchievementField.CategoryKind).CategoryKind,
                ResolveTags(origin[AchievementField.Tags], platform, metadata, custom),
                rarity.GlobalPercentUnlocked,
                rarity.Rarity,
                From(AchievementField.Order).Order,
                From(AchievementField.Hidden).Hidden,
                From(AchievementField.UnlockTime).UnlockTimeUtc);

            return new ResolvedAchievementValues(resolved, origin);
        }

        /// <summary>
        /// The tags of the deciding layer, plus what only the platform can know: the derived
        /// unlock-mode tags always, and Unobtainable when metadata decides (the platform retired
        /// the achievement). A custom tag set is the user's full assignment and replaces
        /// Unobtainable too; a cleared custom set leaves only the derived tags.
        /// </summary>
        internal static string ResolveTags(
            DataLayer decider,
            AchievementLayerValues platform,
            AchievementLayerValues metadata,
            AchievementLayerValues custom)
        {
            var derived = AchievementTagHelper.DerivedOnly(platform.Tags);
            switch (decider)
            {
                case DataLayer.Custom:
                    return AchievementTagHelper.Combine(new[] { AchievementTagHelper.StripDerived(custom.Tags), derived });
                case DataLayer.Metadata:
                    return AchievementTagHelper.Combine(new[]
                    {
                        AchievementTagHelper.StripDerived(metadata.Tags),
                        derived,
                        AchievementTagHelper.IsUnobtainable(platform.Tags) ? AchievementTagHelper.Unobtainable : null
                    });
                default:
                    return AchievementTagHelper.Normalize(platform.Tags);
            }
        }

        private static DataLayer Decider(
            AchievementField field,
            AchievementLayerValues platform,
            AchievementLayerValues metadata,
            AchievementLayerValues custom)
        {
            if ((custom.Supplied & field) != 0) return DataLayer.Custom;
            if ((metadata.Supplied & field) != 0) return DataLayer.Metadata;
            if ((platform.Supplied & field) != 0) return DataLayer.Platform;
            return DataLayer.None;
        }

        private static AchievementLayerValues Layer(
            DataLayer layer,
            AchievementLayerValues platform,
            AchievementLayerValues metadata,
            AchievementLayerValues custom)
        {
            switch (layer)
            {
                case DataLayer.Custom: return custom;
                case DataLayer.Metadata: return metadata;
                case DataLayer.Platform: return platform;
                default: return AchievementLayerValues.Empty;
            }
        }
    }
}
