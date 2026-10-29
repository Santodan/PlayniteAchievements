using System;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;

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

        /// <summary>A score card choice: the score's name, or None for an empty Overview slot.</summary>
        public static string ScoreCardSlotName(string value) =>
            ScoreCardSlots.TryGetCardType(value, out var type)
                ? ScoreCardTypeName(type)
                : Localize("LOCPlayAch_Common_None");

        /// <summary>The score's name as card pickers show it.</summary>
        public static string ScoreCardTypeName(ScoreCardType value) =>
            value.IsPoints
                ? ScoreCardViewModel.GetLabel(value)
                : ScoreCardViewModel.GetShortLabel(value);

        // Left and Centered reuse the grid alignment labels; only Stacked is its own string.
        public static string ProfileLayoutName(ShowcaseProfileLayout value)
        {
            switch (value)
            {
                case ShowcaseProfileLayout.Centered:
                    return Localize("LOCPlayAch_Settings_GridAlignment_Center");
                case ShowcaseProfileLayout.Stacked:
                    return Localize("LOCPlayAch_Showcase_ProfileLayout_Stacked");
                default:
                    return Localize("LOCPlayAch_Common_Left");
            }
        }

        // Cover reuses the "Cover" column header.
        public static string ScoreCardArtName(ShowcaseScoreCardArt value) =>
            value == ShowcaseScoreCardArt.GameCover
                ? Localize("LOCPlayAch_Column_Cover")
                : EnumValueName("LOCPlayAch_Showcase_ScoreCardArt_", value);

        // Both and None reuse the shared strings rather than adding identical ones.
        public static string ProfileMedalModeName(ShowcaseProfileMedalMode value) =>
            value == ShowcaseProfileMedalMode.Both
                ? Localize("LOCPlayAch_Common_Both")
                : value == ShowcaseProfileMedalMode.None
                ? Localize("LOCPlayAch_Common_None")
                : EnumValueName("LOCPlayAch_Showcase_ProfileMedalMode_", value);

        // Every mode reuses a shared label: the completions stat, or the dimension's own name.
        public static string PieModeName(ShowcasePieMode value)
        {
            switch (value)
            {
                case ShowcasePieMode.Provider:
                    return Localize("LOCPlayAch_Common_Platform");
                case ShowcasePieMode.Rarity:
                    return Localize("LOCPlayAch_Column_Rarity");
                case ShowcasePieMode.Trophy:
                    return Localize("LOCPlayAch_Column_Trophy");
                case ShowcasePieMode.Tag:
                    return Localize("LOCPlayAch_Common_Tag");
                case ShowcasePieMode.CompletedGames:
                default:
                    return Localize("LOCPlayAch_Showcase_Stat_CompletedGames");
            }
        }

        // Game reuses the "Game" column header.
        public static string PointsGroupingName(ShowcasePointsGrouping value) =>
            value == ShowcasePointsGrouping.Game
                ? Localize("LOCPlayAch_Column_Game")
                : EnumValueName("LOCPlayAch_Showcase_PointsGrouping_", value);

        // All shares the achievement grid's label; Unlock Next is the mosaic's own string, which
        // the grid shares in turn.
        public static string MosaicSourceName(ShowcaseMosaicSource value) =>
            value == ShowcaseMosaicSource.UnlockNext
                ? EnumValueName("LOCPlayAch_Showcase_MosaicSource_", value)
                : Localize("LOCPlayAch_Showcase_AchievementGridSource_All");

        // Closest to completion and easiest share the Unlock Next labels; only Fewest remaining is
        // its own string.
        public static string FinishNextCriterionName(FinishNextCriterion value)
        {
            switch (value)
            {
                case FinishNextCriterion.FewestRemaining:
                    return Localize("LOCPlayAch_Showcase_FinishNextCriterion_FewestRemaining");
                case FinishNextCriterion.EasiestRemaining:
                    return UnlockNextCriterionName(UnlockNextCriterion.Easiest);
                default:
                    return UnlockNextCriterionName(UnlockNextCriterion.ClosestToCompletion);
            }
        }

        public static string UnlockNextCriterionName(UnlockNextCriterion value) =>
            EnumValueName("LOCPlayAch_Showcase_UnlockNextCriterion_", value);



        // The content kinds share the overview's Achievements and Games labels.
        public static string MosaicContentName(ShowcaseMosaicContent value) =>
            value == ShowcaseMosaicContent.Games
                ? Localize("LOCPlayAch_Overview_GameSummaries")
                : Localize("LOCPlayAch_Achievements");

        // The grid sources shared with a mosaic reuse the mosaic's label rather than adding a
        // second identical string to translate.
        public static string AchievementGridSourceName(ShowcaseAchievementGridSource value) =>
            value == ShowcaseAchievementGridSource.UnlockNext
                ? MosaicSourceName(ShowcaseMosaicSource.UnlockNext)
                : EnumValueName("LOCPlayAch_Showcase_AchievementGridSource_", value);

        public static string GameGridSourceName(ShowcaseGameGridSource value)
        {
            switch (value)
            {
                case ShowcaseGameGridSource.PlayniteFavorites:
                    return GameMosaicSourceName(ShowcaseGameMosaicSource.PlayniteFavorites);
                case ShowcaseGameGridSource.FinishNext:
                    return GameMosaicSourceName(ShowcaseGameMosaicSource.FinishNext);
                default:
                    return GameMosaicSourceName(ShowcaseGameMosaicSource.All);
            }
        }

        public static string GameMosaicSourceName(ShowcaseGameMosaicSource value) =>
            EnumValueName("LOCPlayAch_Showcase_GameMosaicSource_", value);

        public static string FitModeName(ShowcaseImageFitMode value) =>
            EnumValueName("LOCPlayAch_Showcase_ImageFit_", value);

        // Off/Left/Right already exist as shared setting labels, so this maps to those keys
        // instead of adding a prefixed key per member like the enums above.
        public static string InfoPanelPositionName(ShowcaseInfoPanelPosition value)
        {
            switch (value)
            {
                case ShowcaseInfoPanelPosition.Left:
                    return Localize("LOCPlayAch_Common_Left");
                case ShowcaseInfoPanelPosition.Right:
                    return Localize("LOCPlayAch_Common_Right");
                case ShowcaseInfoPanelPosition.Bottom:
                    return Localize("LOCPlayAch_Common_Bottom");
                default:
                    return Localize("LOCPlayAch_Settings_Override_Off");
            }
        }

        // Alignment values share the dock and grid alignment labels.
        public static string HorizontalAlignmentName(ShowcaseHorizontalAlignment value)
        {
            switch (value)
            {
                case ShowcaseHorizontalAlignment.Left:
                    return Localize("LOCPlayAch_Common_Left");
                case ShowcaseHorizontalAlignment.Right:
                    return Localize("LOCPlayAch_Common_Right");
                default:
                    return Localize("LOCPlayAch_Settings_GridAlignment_Center");
            }
        }

        public static string VerticalAlignmentName(ShowcaseVerticalAlignment value)
        {
            switch (value)
            {
                case ShowcaseVerticalAlignment.Top:
                    return Localize("LOCPlayAch_Common_Top");
                case ShowcaseVerticalAlignment.Bottom:
                    return Localize("LOCPlayAch_Common_Bottom");
                default:
                    return Localize("LOCPlayAch_Settings_GridAlignment_Center");
            }
        }

        public static string TextSizeName(ShowcaseTextSize value) =>
            EnumValueName("LOCPlayAch_Showcase_TextSize_", value);

        private static string EnumValueName<T>(string prefix, T value)
        {
            return Localize(prefix + Convert.ToString(value));
        }
    }
}
