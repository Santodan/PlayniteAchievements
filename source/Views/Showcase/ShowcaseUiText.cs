using System;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Showcase
{
    internal static class ShowcaseUiText
    {
        public static string Localize(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? key : value;
        }

        public static string LocalizeValue(string key, string value)
        {
            return string.IsNullOrWhiteSpace(key) ? value ?? string.Empty : Localize(key);
        }

        public static string GetWidgetName(ShowcaseWidgetKind kind)
        {
            var definition = ShowcaseWidgetCatalog.Get(kind);
            return Localize(definition.NameKey);
        }

        public static string ScoreModeName(ShowcaseScoreMode value) =>
            EnumValueName("LOCPlayAch_Showcase_ScoreMode_", value);

        /// <summary>
        /// Names the score-history choices with the card choices' own strings, so "Both" means the
        /// same thing in both dropdowns. Only None needs a word of its own.
        /// </summary>
        public static string ScoreHistoryModeName(ShowcaseScoreHistoryMode value) =>
            value == ShowcaseScoreHistoryMode.None
                ? Localize("LOCPlayAch_Common_None")
                : EnumValueName("LOCPlayAch_Showcase_ScoreMode_", value);

        public static string PieModeName(ShowcasePieMode value) =>
            EnumValueName("LOCPlayAch_Showcase_PieMode_", value);

        public static string PointsGroupingName(ShowcasePointsGrouping value) =>
            EnumValueName("LOCPlayAch_Showcase_PointsGrouping_", value);

        public static string MosaicSourceName(ShowcaseMosaicSource value) =>
            EnumValueName("LOCPlayAch_Showcase_MosaicSource_", value);

        public static string ScreenshotVariantName(ShowcaseScreenshotVariant value) =>
            EnumValueName("LOCPlayAch_Showcase_ScreenshotVariant_", value);

        public static string SlideshowSourceName(ShowcaseSlideshowSource value) =>
            EnumValueName("LOCPlayAch_Showcase_SlideshowSource_", value);

        public static string MosaicContentName(ShowcaseMosaicContent value) =>
            EnumValueName("LOCPlayAch_Showcase_MosaicContent_", value);

        public static string AchievementGridSourceName(ShowcaseAchievementGridSource value) =>
            EnumValueName("LOCPlayAch_Showcase_AchievementGridSource_", value);

        public static string GameGridSourceName(ShowcaseGameGridSource value) =>
            EnumValueName("LOCPlayAch_Showcase_GameGridSource_", value);

        public static string GameMosaicSourceName(ShowcaseGameMosaicSource value) =>
            EnumValueName("LOCPlayAch_Showcase_GameMosaicSource_", value);

        public static string FitModeName(ShowcaseImageFitMode value) =>
            EnumValueName("LOCPlayAch_Showcase_ImageFit_", value);

        public static string TimelineRangeName(TimelineRange range) => TimelineRangeText.Describe(range);

        private static string EnumValueName<T>(string prefix, T value)
        {
            return Localize(prefix + Convert.ToString(value));
        }
    }
}
