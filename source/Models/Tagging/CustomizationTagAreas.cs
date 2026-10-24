using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Tagging
{
    /// <summary>Where a game's data in an area comes from: one tag kind per layer.</summary>
    public enum TagLayer
    {
        /// <summary>The game's own platform supplied the data.</summary>
        Platform,

        /// <summary>A metadata source supplied the data.</summary>
        Metadata,

        /// <summary>The user customized the data.</summary>
        Custom,

        /// <summary>No source supplied the data.</summary>
        None
    }

    /// <summary>
    /// Where a game's data in each sourced area came from, for tagging.
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
    /// One customization area and the tag kinds it writes, by layer. An area writes only the
    /// layers that can apply to it: Order has no Metadata (no source supplies an order), Goals no
    /// Platform (no platform supplies goals), Notifications only Custom.
    /// </summary>
    public sealed class CustomizationTagArea
    {
        public CustomizationTagArea(
            CustomizationArea area,
            IReadOnlyDictionary<TagLayer, TagType> tags,
            string nounResourceKey)
        {
            Area = area;
            Tags = tags;
            NounResourceKey = nounResourceKey;
        }

        public CustomizationArea Area { get; }

        /// <summary>The area's tag kinds by layer.</summary>
        public IReadOnlyDictionary<TagLayer, TagType> Tags { get; }

        /// <summary>Resource key of the area noun, the settings row label.</summary>
        public string NounResourceKey { get; }

        /// <summary>Whether a refresh records this area's platform and metadata layers.</summary>
        public bool IsSourced => (CustomizationTagAreas.SourcedAreas & Area) != 0;

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
        /// <summary>The areas whose platform and metadata layers a refresh records.</summary>
        public const CustomizationArea SourcedAreas =
            CustomizationArea.Icons |
            CustomizationArea.TrophyTypes |
            CustomizationArea.Points |
            CustomizationArea.Text |
            CustomizationArea.Categories |
            CustomizationArea.Tags |
            CustomizationArea.Capstones;

        /// <summary>
        /// Resource key of an area kind's whole name. Each kind is its own string so every language
        /// can phrase it fully; names are never composed from a shared noun.
        /// </summary>
        public static string NameResourceKey(TagType tagType) => "LOCPlayAch_Tagging_Tag_" + tagType;

        public static readonly IReadOnlyList<CustomizationTagArea> All = new[]
        {
            Area(CustomizationArea.Icons, "LOCPlayAch_ManageAchievements_Tab_CustomIcons",
                platform: TagType.PlatformIcons, metadata: TagType.MetadataIcons, custom: TagType.CustomIcons, none: TagType.NoIcons),
            Area(CustomizationArea.TrophyTypes, "LOCPlayAch_Tagging_Area_Trophies",
                TagType.PlatformTrophies, TagType.MetadataTrophies, TagType.CustomTrophies, TagType.NoTrophies),
            Area(CustomizationArea.Points, "LOCPlayAch_Column_Points",
                TagType.PlatformPoints, TagType.MetadataPoints, TagType.CustomPoints, TagType.NoPoints),
            // Every achievement has a name, so text has no None kind.
            Area(CustomizationArea.Text, "LOCPlayAch_Tagging_Area_Text",
                platform: TagType.PlatformText, metadata: TagType.MetadataText, custom: TagType.CustomText),
            Area(CustomizationArea.Categories, "LOCPlayAch_ManageAchievements_Tab_Category",
                TagType.PlatformCategories, TagType.MetadataCategories, TagType.CustomCategories, TagType.NoCategories),
            Area(CustomizationArea.Tags, "LOCPlayAch_Tagging_Area_Tags",
                TagType.PlatformTags, TagType.MetadataTags, TagType.CustomTags, TagType.NoTags),
            Area(CustomizationArea.Capstones, "LOCPlayAch_Tagging_Area_Capstones",
                TagType.PlatformCapstones, TagType.MetadataCapstones, TagType.CustomCapstones, TagType.NoCapstones),
            Area(CustomizationArea.Order, "LOCPlayAch_ManageAchievements_Tab_AchievementOrder",
                platform: TagType.PlatformOrder, custom: TagType.CustomOrder),
            Area(CustomizationArea.Goals, "LOCPlayAch_Tagging_Area_Goals",
                custom: TagType.CustomGoals, none: TagType.NoGoals),
            Area(CustomizationArea.Notes, "LOCPlayAch_Tagging_Area_Notes",
                custom: TagType.CustomNotes, none: TagType.NoNotes),
            Area(CustomizationArea.Notifications, "LOCPlayAch_Tagging_Area_Notifications",
                custom: TagType.CustomNotifications),
            Area(CustomizationArea.Source, "LOCPlayAch_Tagging_Area_Source",
                custom: TagType.CustomSource),
            Area(CustomizationArea.ManualTracking, "LOCPlayAch_ManageAchievements_Tab_ManualTracking",
                custom: TagType.CustomManualTracking)
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
        /// The kinds the areas contribute for a game. A sourced area gets every layer that applies
        /// and None when no layer does; while its platform and metadata masks are unknown it gets
        /// Custom, and None when the cached data has no value for it either. An area no refresh
        /// records is Custom when customized, and otherwise its Platform (the platform's own,
        /// as for Order) or None (nothing at all, as for Goals).
        /// </summary>
        public static IEnumerable<TagType> TagsFor(CustomizationArea custom, GameDataLayers layers)
        {
            foreach (var area in All)
            {
                var isCustom = (custom & area.Area) != 0;
                bool fromPlatform, fromMetadata, hasAny;
                if (area.IsSourced)
                {
                    fromPlatform = layers.IsKnown && (layers.Platform.Value & area.Area) != 0 ||
                                   area.Area == CustomizationArea.Text && TextPredatesMasks(layers);
                    fromMetadata = layers.IsKnown && (layers.Metadata.Value & area.Area) != 0;
                    hasAny = layers.IsKnown
                        ? fromPlatform || fromMetadata || isCustom
                        : isCustom || (layers.Present & area.Area) != 0;
                }
                else
                {
                    fromPlatform = !isCustom;
                    fromMetadata = false;
                    hasAny = isCustom;
                }

                if (fromPlatform && area.Tag(TagLayer.Platform) is TagType platform)
                {
                    yield return platform;
                }

                if (fromMetadata && area.Tag(TagLayer.Metadata) is TagType metadata)
                {
                    yield return metadata;
                }

                if (isCustom)
                {
                    yield return area.CustomTag;
                }

                if (!hasAny && area.Tag(TagLayer.None) is TagType none)
                {
                    yield return none;
                }
            }
        }

        /// <summary>
        /// True for masks recorded before text had layers: they name neither layer for text although
        /// the game has some, and no source supplied text then, so it is the platform's.
        /// </summary>
        private static bool TextPredatesMasks(GameDataLayers layers) =>
            layers.IsKnown &&
            ((layers.Platform.Value | layers.Metadata.Value) & CustomizationArea.Text) == 0 &&
            (layers.Present & CustomizationArea.Text) != 0;

        private static CustomizationTagArea Area(
            CustomizationArea area,
            string nounKey,
            TagType? platform = null,
            TagType? metadata = null,
            TagType? custom = null,
            TagType? none = null)
        {
            var tags = new Dictionary<TagLayer, TagType>();
            void Add(TagLayer layer, TagType? tagType)
            {
                if (tagType.HasValue)
                {
                    tags[layer] = tagType.Value;
                }
            }

            Add(TagLayer.Platform, platform);
            Add(TagLayer.Metadata, metadata);
            Add(TagLayer.Custom, custom);
            Add(TagLayer.None, none);
            return new CustomizationTagArea(area, tags, nounKey);
        }
    }
}
