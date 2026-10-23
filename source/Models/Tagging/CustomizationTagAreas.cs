using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Tagging
{
    /// <summary>
    /// One customization area and the tag pair it writes: the custom kind when the game
    /// carries that customization, the default kind when it does not.
    /// </summary>
    public sealed class CustomizationTagArea
    {
        public CustomizationTagArea(
            CustomizationArea area,
            TagType customTag,
            TagType defaultTag,
            string nounResourceKey,
            string englishNoun)
        {
            Area = area;
            CustomTag = customTag;
            DefaultTag = defaultTag;
            NounResourceKey = nounResourceKey;
            EnglishNoun = englishNoun;
        }

        public CustomizationArea Area { get; }

        public TagType CustomTag { get; }

        public TagType DefaultTag { get; }

        /// <summary>Resource key of the area noun, composed into both tag names and used as the settings label.</summary>
        public string NounResourceKey { get; }

        /// <summary>English noun for the hardcoded fallback names.</summary>
        public string EnglishNoun { get; }
    }

    /// <summary>
    /// The customization areas in settings order. Sync, the default-name catalog and the
    /// settings view all read this table, so an area is added in one place.
    /// </summary>
    public static class CustomizationTagAreas
    {
        public const string CustomFormatKey = "LOCPlayAch_Tagging_CustomFormat";
        public const string DefaultFormatKey = "LOCPlayAch_Tagging_DefaultFormat";

        public static readonly IReadOnlyList<CustomizationTagArea> All = new[]
        {
            new CustomizationTagArea(CustomizationArea.Icons, TagType.CustomIcons, TagType.DefaultIcons,
                "LOCPlayAch_ManageAchievements_Tab_CustomIcons", "Icons"),
            new CustomizationTagArea(CustomizationArea.TrophyTypes, TagType.CustomTrophyTypes, TagType.DefaultTrophyTypes,
                "LOCPlayAch_Tagging_Area_TrophyTypes", "Trophy Types"),
            new CustomizationTagArea(CustomizationArea.Points, TagType.CustomPoints, TagType.DefaultPoints,
                "LOCPlayAch_Column_Points", "Points"),
            new CustomizationTagArea(CustomizationArea.Notes, TagType.CustomNotes, TagType.DefaultNotes,
                "LOCPlayAch_Tagging_Area_Notes", "Notes"),
            new CustomizationTagArea(CustomizationArea.Categories, TagType.CustomCategories, TagType.DefaultCategories,
                "LOCPlayAch_ManageAchievements_Tab_Category", "Categories"),
            new CustomizationTagArea(CustomizationArea.Order, TagType.CustomOrder, TagType.DefaultOrder,
                "LOCPlayAch_ManageAchievements_Tab_AchievementOrder", "Order"),
            new CustomizationTagArea(CustomizationArea.Capstones, TagType.CustomCapstones, TagType.DefaultCapstones,
                "LOCPlayAch_Tagging_Area_Capstones", "Capstones"),
            new CustomizationTagArea(CustomizationArea.Goals, TagType.CustomGoals, TagType.DefaultGoals,
                "LOCPlayAch_Tagging_Area_Goals", "Goals"),
            new CustomizationTagArea(CustomizationArea.Source, TagType.CustomSource, TagType.DefaultSource,
                "LOCPlayAch_Tagging_Area_Source", "Source"),
            new CustomizationTagArea(CustomizationArea.ManualTracking, TagType.CustomManualTracking, TagType.DefaultManualTracking,
                "LOCPlayAch_ManageAchievements_Tab_ManualTracking", "Manual Tracking"),
            new CustomizationTagArea(CustomizationArea.Notifications, TagType.CustomNotifications, TagType.DefaultNotifications,
                "LOCPlayAch_Tagging_Area_Notifications", "Notifications")
        };

        private static readonly Dictionary<TagType, CustomizationTagArea> ByTag =
            All.SelectMany(area => new[]
                {
                    new KeyValuePair<TagType, CustomizationTagArea>(area.CustomTag, area),
                    new KeyValuePair<TagType, CustomizationTagArea>(area.DefaultTag, area)
                })
                .ToDictionary(pair => pair.Key, pair => pair.Value);

        /// <summary>
        /// The area a tag kind belongs to, and whether it is the custom side of the pair.
        /// False for the kinds outside the customization areas.
        /// </summary>
        public static bool TryGetArea(TagType tagType, out CustomizationTagArea area, out bool isCustom)
        {
            if (ByTag.TryGetValue(tagType, out area))
            {
                isCustom = area.CustomTag == tagType;
                return true;
            }

            isCustom = false;
            return false;
        }

        /// <summary>The kind each area contributes for a game with the given customizations.</summary>
        public static IEnumerable<TagType> TagsFor(CustomizationArea areas)
        {
            return All.Select(area => (areas & area.Area) != 0 ? area.CustomTag : area.DefaultTag);
        }
    }
}
