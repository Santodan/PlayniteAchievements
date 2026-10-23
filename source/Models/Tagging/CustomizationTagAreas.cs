using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Tagging
{
    /// <summary>The role of one tag kind within its customization area.</summary>
    public enum TagLayer
    {
        /// <summary>The game's own platform supplied the data.</summary>
        Platform,

        /// <summary>A metadata source supplied the data.</summary>
        Metadata,

        /// <summary>The user customized the data.</summary>
        Custom,

        /// <summary>A layered area's data comes from nowhere.</summary>
        None,

        /// <summary>A paired area's data is not customized.</summary>
        Default
    }

    /// <summary>
    /// Where a game's data in each layered area came from, for tagging.
    /// </summary>
    public struct GameDataLayers
    {
        /// <summary>Areas the platform supplied at the last refresh; null when no refresh recorded it.</summary>
        public CustomizationArea? Platform;

        /// <summary>Areas a metadata source supplied at the last refresh; null when no refresh recorded it.</summary>
        public CustomizationArea? Metadata;

        /// <summary>Areas the cached data has any value for, used while the masks are unknown.</summary>
        public CustomizationArea Present;

        public bool IsKnown => Platform.HasValue && Metadata.HasValue;
    }

    /// <summary>
    /// One customization area and the tag kinds it writes. A layered area writes Platform,
    /// Metadata, Custom and None kinds; a paired area Custom and Default; a single area Custom.
    /// </summary>
    public sealed class CustomizationTagArea
    {
        public CustomizationTagArea(
            CustomizationArea area,
            IReadOnlyDictionary<TagLayer, TagType> tags,
            string nounResourceKey,
            string englishNoun,
            string customEnglishName = null)
        {
            Area = area;
            Tags = tags;
            NounResourceKey = nounResourceKey;
            EnglishNoun = englishNoun;
            CustomEnglishName = customEnglishName;
        }

        public CustomizationArea Area { get; }

        /// <summary>The area's tag kinds by layer.</summary>
        public IReadOnlyDictionary<TagLayer, TagType> Tags { get; }

        /// <summary>Resource key of the area noun, the settings row label.</summary>
        public string NounResourceKey { get; }

        /// <summary>English noun for the hardcoded fallback names.</summary>
        public string EnglishNoun { get; }

        /// <summary>
        /// English fallback for a single area's custom tag, whose name reads on its own instead of
        /// "Custom {noun}".
        /// </summary>
        public string CustomEnglishName { get; }

        public bool IsLayered => Tags.ContainsKey(TagLayer.Platform);

        public TagType CustomTag => Tags[TagLayer.Custom];

        public TagType? Tag(TagLayer layer) => Tags.TryGetValue(layer, out var tagType) ? tagType : (TagType?)null;

        /// <summary>The tag kinds this area writes.</summary>
        public IEnumerable<TagType> TagTypes => Tags.Values;
    }

    /// <summary>
    /// The customization areas in settings order. Sync, the default-name catalog and the
    /// settings view all read this table, so an area is added in one place.
    /// </summary>
    public static class CustomizationTagAreas
    {
        /// <summary>
        /// Resource key of an area kind's whole name. Each kind is its own string so every language
        /// can phrase it fully; names are never composed from a shared noun.
        /// </summary>
        public static string NameResourceKey(TagType tagType) => "LOCPlayAch_Tagging_Tag_" + tagType;

        public static readonly IReadOnlyList<CustomizationTagArea> All = new[]
        {
            Layered(CustomizationArea.Icons, TagType.PlatformIcons, TagType.MetadataIcons, TagType.CustomIcons, TagType.NoIcons,
                "LOCPlayAch_ManageAchievements_Tab_CustomIcons", "Icons"),
            Layered(CustomizationArea.TrophyTypes, TagType.PlatformTrophyTypes, TagType.MetadataTrophyTypes, TagType.CustomTrophyTypes, TagType.NoTrophyTypes,
                "LOCPlayAch_Tagging_Area_TrophyTypes", "Trophy Types"),
            Layered(CustomizationArea.Points, TagType.PlatformPoints, TagType.MetadataPoints, TagType.CustomPoints, TagType.NoPoints,
                "LOCPlayAch_Column_Points", "Points"),
            Layered(CustomizationArea.Categories, TagType.PlatformCategories, TagType.MetadataCategories, TagType.CustomCategories, TagType.NoCategories,
                "LOCPlayAch_ManageAchievements_Tab_Category", "Categories"),
            Layered(CustomizationArea.Tags, TagType.PlatformTags, TagType.MetadataTags, TagType.CustomTags, TagType.NoTags,
                "LOCPlayAch_Tagging_Area_Tags", "Tags"),
            Layered(CustomizationArea.Capstones, TagType.PlatformCapstones, TagType.MetadataCapstones, TagType.CustomCapstones, TagType.NoCapstones,
                "LOCPlayAch_Tagging_Area_Capstones", "Capstones"),
            Paired(CustomizationArea.Notes, TagType.CustomNotes, TagType.DefaultNotes,
                "LOCPlayAch_Tagging_Area_Notes", "Notes"),
            Paired(CustomizationArea.Order, TagType.CustomOrder, TagType.DefaultOrder,
                "LOCPlayAch_ManageAchievements_Tab_AchievementOrder", "Order"),
            Paired(CustomizationArea.Goals, TagType.CustomGoals, TagType.DefaultGoals,
                "LOCPlayAch_Tagging_Area_Goals", "Goals"),
            Paired(CustomizationArea.Notifications, TagType.CustomNotifications, TagType.DefaultNotifications,
                "LOCPlayAch_Tagging_Area_Notifications", "Notifications"),
            new CustomizationTagArea(CustomizationArea.Source,
                new Dictionary<TagLayer, TagType> { [TagLayer.Custom] = TagType.CustomSource },
                "LOCPlayAch_Tagging_Area_Source", "Source", "Source Override"),
            new CustomizationTagArea(CustomizationArea.ManualTracking,
                new Dictionary<TagLayer, TagType> { [TagLayer.Custom] = TagType.CustomManualTracking },
                "LOCPlayAch_ManageAchievements_Tab_ManualTracking", "Manual Tracking", "Manual Tracking")
        };

        private static readonly Dictionary<TagType, (CustomizationTagArea Area, TagLayer Layer)> ByTag =
            All.SelectMany(area => area.Tags.Select(pair => (pair.Value, area, pair.Key)))
                .ToDictionary(entry => entry.Item1, entry => (entry.area, entry.Key));

        /// <summary>
        /// The area and layer a tag kind belongs to. False for the kinds outside the customization areas.
        /// </summary>
        public static bool TryGetArea(TagType tagType, out CustomizationTagArea area, out TagLayer layer)
        {
            if (ByTag.TryGetValue(tagType, out var entry))
            {
                area = entry.Area;
                layer = entry.Layer;
                return true;
            }

            area = null;
            layer = default;
            return false;
        }

        /// <summary>
        /// The kinds the areas contribute for a game. A layered area gets every layer that applies,
        /// and None when no layer does; while the platform and metadata masks are unknown it gets
        /// Custom, and None when the cached data has no value for it either.
        /// </summary>
        public static IEnumerable<TagType> TagsFor(CustomizationArea custom, GameDataLayers layers)
        {
            foreach (var area in All)
            {
                var isCustom = (custom & area.Area) != 0;
                if (!area.IsLayered)
                {
                    if (isCustom)
                    {
                        yield return area.CustomTag;
                    }
                    else if (area.Tag(TagLayer.Default) is TagType standard)
                    {
                        yield return standard;
                    }

                    continue;
                }

                var fromPlatform = layers.IsKnown && (layers.Platform.Value & area.Area) != 0;
                var fromMetadata = layers.IsKnown && (layers.Metadata.Value & area.Area) != 0;
                if (fromPlatform)
                {
                    yield return area.Tags[TagLayer.Platform];
                }

                if (fromMetadata)
                {
                    yield return area.Tags[TagLayer.Metadata];
                }

                if (isCustom)
                {
                    yield return area.CustomTag;
                }

                var hasAny = layers.IsKnown
                    ? fromPlatform || fromMetadata || isCustom
                    : isCustom || (layers.Present & area.Area) != 0;
                if (!hasAny)
                {
                    yield return area.Tags[TagLayer.None];
                }
            }
        }

        private static CustomizationTagArea Layered(
            CustomizationArea area,
            TagType platform,
            TagType metadata,
            TagType custom,
            TagType none,
            string nounKey,
            string englishNoun) =>
            new CustomizationTagArea(
                area,
                new Dictionary<TagLayer, TagType>
                {
                    [TagLayer.Platform] = platform,
                    [TagLayer.Metadata] = metadata,
                    [TagLayer.Custom] = custom,
                    [TagLayer.None] = none
                },
                nounKey,
                englishNoun);

        private static CustomizationTagArea Paired(
            CustomizationArea area,
            TagType custom,
            TagType standard,
            string nounKey,
            string englishNoun) =>
            new CustomizationTagArea(
                area,
                new Dictionary<TagLayer, TagType>
                {
                    [TagLayer.Custom] = custom,
                    [TagLayer.Default] = standard
                },
                nounKey,
                englishNoun);
    }
}
