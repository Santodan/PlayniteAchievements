using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The sorts the mosaics offer, named once for the widget settings' Sort By choice and the
    /// control bar's Sort By. The bar's choice is held by its <see cref="GridSortSelector"/>, by
    /// sort mode name, and orders the tiles in place of the widget's configured sort while set.
    /// </summary>
    public static class MosaicSortChoices
    {
        /// <summary>The Icon mosaic's sorts, Default (the Source's own order) first.</summary>
        public static readonly IReadOnlyList<CompactListSortMode> AchievementModes = new[]
        {
            CompactListSortMode.None,
            CompactListSortMode.UnlockTime,
            CompactListSortMode.Rarity
        };

        /// <summary>The Game mosaic's sorts, Default (pin order) first.</summary>
        public static readonly IReadOnlyList<GameSummariesSortMode> GameModes = new[]
        {
            GameSummariesSortMode.PinOrder,
            GameSummariesSortMode.RecentUnlock,
            GameSummariesSortMode.LastPlayed,
            GameSummariesSortMode.TotalAchievements,
            GameSummariesSortMode.Progress,
            GameSummariesSortMode.Alphabetical,
            GameSummariesSortMode.DateAdded,
            GameSummariesSortMode.ReleaseDate,
            GameSummariesSortMode.Playtime,
            GameSummariesSortMode.Remaining
        };

        public static string AchievementName(CompactListSortMode value)
        {
            switch (value)
            {
                case CompactListSortMode.UnlockTime:
                    return L("LOCPlayAch_Common_UnlockTime");
                case CompactListSortMode.Rarity:
                    return L("LOCPlayAch_Column_Rarity");
                default:
                    return L("LOCPlayAch_Common_Default");
            }
        }

        public static string GameName(GameSummariesSortMode value)
        {
            switch (value)
            {
                case GameSummariesSortMode.RecentUnlock:
                    return L("LOCPlayAch_Common_LastUnlock");
                case GameSummariesSortMode.LastPlayed:
                    return L("LOCPlayAch_Common_LastPlayed");
                case GameSummariesSortMode.TotalAchievements:
                    return L("LOCPlayAch_Column_Total");
                case GameSummariesSortMode.Progress:
                    return L("LOCPlayAch_Progress");
                case GameSummariesSortMode.Alphabetical:
                    return L("LOCPlayAch_Common_Name");
                case GameSummariesSortMode.DateAdded:
                    return L("LOCPlayAch_Common_DateAdded");
                case GameSummariesSortMode.ReleaseDate:
                    return L("LOCPlayAch_Common_ReleaseDate");
                case GameSummariesSortMode.Playtime:
                    return L("LOCPlayAch_Common_Label_Playtime");
                case GameSummariesSortMode.Remaining:
                    return L("LOCPlayAch_Common_Locked");
                default:
                    return L("LOCPlayAch_Common_Default");
            }
        }

        /// <summary>
        /// Lists the Icon mosaic's sorts in the bar's Sort By (Default is the dropdown's own first
        /// row) and has a pick re-sort the tiles through <paramref name="refresh"/>.
        /// </summary>
        public static void BindAchievementSelector(GridSortSelector selector, Action refresh) =>
            Bind(selector, AchievementModes.Skip(1).Select(mode => new GridSortOption(mode.ToString(), AchievementName(mode))), refresh);

        /// <summary>The Game mosaic's counterpart of <see cref="BindAchievementSelector"/>.</summary>
        public static void BindGameSelector(GridSortSelector selector, Action refresh) =>
            Bind(selector, GameModes.Skip(1).Select(mode => new GridSortOption(mode.ToString(), GameName(mode))), refresh);

        /// <summary>The bar's sort, or null while it is on Default.</summary>
        public static bool TryGetBarSort<TMode>(GridSortSelector selector, out TMode mode, out ListSortDirection direction)
            where TMode : struct
        {
            direction = selector?.Direction ?? ListSortDirection.Ascending;
            mode = default(TMode);
            return selector?.SelectedKey != null && Enum.TryParse(selector.SelectedKey, out mode);
        }

        // The selector holds the mosaic's bar sort itself: a pick shows at once, then re-sorts.
        private static void Bind(GridSortSelector selector, IEnumerable<GridSortOption> options, Action refresh)
        {
            if (selector == null)
            {
                return;
            }

            if (selector.Options.Count == 0)
            {
                selector.Options = options.ToList();
            }

            selector.Apply = (key, direction) =>
            {
                selector.ShowState(key, direction);
                refresh?.Invoke();
            };
        }

        private static string L(string key) => ResourceProvider.GetString(key);
    }
}
