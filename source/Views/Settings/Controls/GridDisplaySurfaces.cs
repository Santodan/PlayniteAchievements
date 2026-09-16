using System;
using System.Collections.Generic;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// Which grid options record a surface edits.
    /// </summary>
    public enum GridOptionKind
    {
        Achievement,
        GameSummaries,
        FriendSummaries,
        CategorySummaries
    }

    /// <summary>
    /// The <see cref="GridOptionsEditor"/> rows a single grid surface offers. Only deviations from
    /// the editor's own flag defaults are stated, so a surface that offers every applicable row
    /// needs no arguments. Plain data: the editor reads these, they know nothing of the control.
    /// </summary>
    public sealed class GridDisplayRowCapabilities
    {
        public GridDisplayRowCapabilities(
            bool showColumnHeadersRow = true,
            bool showControlBarRow = true,
            bool showRowHeightRow = true,
            bool showMaxRowsRow = true,
            bool showSortRow = true,
            bool showMaxHeightRow = false,
            bool showCoverImagesRow = true,
            bool showRarityGlowRow = true,
            bool showColorNamesRow = true,
            bool showCategoryModeRow = false,
            bool showDateModeRow = true,
            bool showMetadataRows = true,
            bool showCompletionGlowRow = true,
            bool showGameSortPinOrderChoice = false)
        {
            ShowColumnHeadersRow = showColumnHeadersRow;
            ShowControlBarRow = showControlBarRow;
            ShowRowHeightRow = showRowHeightRow;
            ShowMaxRowsRow = showMaxRowsRow;
            ShowSortRow = showSortRow;
            ShowMaxHeightRow = showMaxHeightRow;
            ShowCoverImagesRow = showCoverImagesRow;
            ShowRarityGlowRow = showRarityGlowRow;
            ShowColorNamesRow = showColorNamesRow;
            ShowCategoryModeRow = showCategoryModeRow;
            ShowDateModeRow = showDateModeRow;
            ShowMetadataRows = showMetadataRows;
            ShowCompletionGlowRow = showCompletionGlowRow;
            ShowGameSortPinOrderChoice = showGameSortPinOrderChoice;
        }

        public bool ShowColumnHeadersRow { get; }
        public bool ShowControlBarRow { get; }
        public bool ShowRowHeightRow { get; }
        public bool ShowMaxRowsRow { get; }
        public bool ShowSortRow { get; }
        public bool ShowMaxHeightRow { get; }
        public bool ShowCoverImagesRow { get; }
        public bool ShowRarityGlowRow { get; }
        public bool ShowColorNamesRow { get; }
        public bool ShowCategoryModeRow { get; }
        public bool ShowDateModeRow { get; }
        public bool ShowMetadataRows { get; }
        public bool ShowCompletionGlowRow { get; }
        public bool ShowGameSortPinOrderChoice { get; }
    }

    /// <summary>
    /// The single source of truth for which <see cref="GridOptionsEditor"/> rows each grid surface
    /// offers, and what to call that surface in a window title. Mirrors
    /// <see cref="GridOptionsCatalog"/>'s four-dictionary shape, and follows the same
    /// per-surface-key policy-table idiom as AchievementDataGridControl's default column
    /// visibility and order tables.
    ///
    /// Consumed by <see cref="GridOptionsEditor.SurfaceKind"/>/<see cref="GridOptionsEditor.SurfaceKey"/>,
    /// so settings pages and the per-grid display settings popup cannot disagree about which rows
    /// a surface offers.
    /// </summary>
    public static class GridDisplaySurfaces
    {
        private static readonly GridDisplayRowCapabilities AllRows = new GridDisplayRowCapabilities();

        // Achievement grids. Sort is hidden where the grid's order is imposed by the surface
        // (recent-unlock feeds, friend achievement views) rather than chosen by the user.
        private static readonly Dictionary<string, GridDisplayRowCapabilities> AchievementSurfaces =
            new Dictionary<string, GridDisplayRowCapabilities>(StringComparer.OrdinalIgnoreCase)
            {
                // The legacy theme DataGrid record, reached by the ModernDataGrid aliases and the
                // pre-catalog "DataGrid" migration. Not a neutral default despite the name: it
                // contributes only a max height, which is why an unrecognised grid must not be
                // pointed at it (see GridOptionsCatalog.TryResolveAchievementId).
                [GridOptionKeys.Achievement.Default] = new GridDisplayRowCapabilities(
                    showColumnHeadersRow: false,
                    showControlBarRow: false,
                    showRowHeightRow: false,
                    showMaxRowsRow: false,
                    showMaxHeightRow: true,
                    showCoverImagesRow: false,
                    showDateModeRow: false),
                [GridOptionKeys.Achievement.SingleGame] = new GridDisplayRowCapabilities(
                    showCoverImagesRow: false,
                    showCategoryModeRow: true),
                [GridOptionKeys.Achievement.OverviewRecent] = new GridDisplayRowCapabilities(
                    showSortRow: false),
                [GridOptionKeys.Achievement.OverviewSelectedGame] = new GridDisplayRowCapabilities(
                    showCoverImagesRow: false,
                    showCategoryModeRow: true),
                [GridOptionKeys.Achievement.FriendsOverviewRecent] = new GridDisplayRowCapabilities(
                    showSortRow: false,
                    showCategoryModeRow: true),

                // The four child friend surfaces read StartInCategoryMode from their parent
                // surface (AchievementDataGridControl.ResolveStartInCategoryModeOptionsId), so the
                // category mode row appears only on the parent, where the value actually lives.
                [GridOptionKeys.Achievement.FriendsOverviewSelectedFriend] = new GridDisplayRowCapabilities(
                    showSortRow: false),
                [GridOptionKeys.Achievement.FriendsOverviewSelectedGame] = new GridDisplayRowCapabilities(
                    showSortRow: false),
                [GridOptionKeys.Achievement.FriendsOverviewSelectedFriendGame] = new GridDisplayRowCapabilities(
                    showSortRow: false),
                [GridOptionKeys.Achievement.ViewFriendsAchievements] = new GridDisplayRowCapabilities(
                    showCategoryModeRow: true),
                [GridOptionKeys.Achievement.ViewFriendsAchievementsSelectedFriend] = AllRows,

                // Migration donors for the pre-showcase start page widgets. Live start page grids
                // resolve to per-instance showcase surfaces below.
                [GridOptionKeys.Achievement.StartPageRecent] = AllRows,
                [GridOptionKeys.Achievement.StartPageFriendAchievements] = AllRows,

                // Showcase and start page widget grids, matched on their base key because the live
                // surfaces carry a per-instance suffix. The sort combo's default keeps the
                // projection order (unlock recency), so sorting stays offered.
                [ShowcaseGridSurfaces.RecentAchievements] = AllRows,
                // Sort stays off (the control sorts externally) and cover images do not apply,
                // but the appearance rows now reach this grid's own record rather than the
                // legacy Achievement[Default] one, so they are offered here.
                [GridOptionKeys.Achievement.DesktopTheme] = new GridDisplayRowCapabilities(
                    showSortRow: false,
                    showCoverImagesRow: false,
                    showMaxHeightRow: true,
                    showCategoryModeRow: true)
            };

        // Game summary grids. Header rows that render a single game (the achievements windows) and
        // the theme grid's summary row offer no control bar, row limit or sort.
        private static readonly Dictionary<string, GridDisplayRowCapabilities> GameSummarySurfaces =
            new Dictionary<string, GridDisplayRowCapabilities>(StringComparer.OrdinalIgnoreCase)
            {
                [GridOptionKeys.GameSummaries.Overview] = AllRows,
                [GridOptionKeys.GameSummaries.StartPage] = AllRows,
                [GridOptionKeys.GameSummaries.ViewAchievements] = new GridDisplayRowCapabilities(
                    showControlBarRow: false,
                    showMaxRowsRow: false,
                    showSortRow: false),
                [GridOptionKeys.GameSummaries.FriendsOverview] = AllRows,
                [GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend] = AllRows,
                [GridOptionKeys.GameSummaries.ViewFriendsAchievements] = AllRows,
                [GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend] = AllRows,
                [GridOptionKeys.GameSummaries.DesktopTheme] = new GridDisplayRowCapabilities(
                    showControlBarRow: false,
                    showMaxRowsRow: false,
                    showSortRow: false),

                // Showcase and start page widget game grids, matched on their base key. This is the
                // one surface whose source order is user-controlled (a pinned-games grid), so it
                // offers the order-preserving PinOrder sort choice.
                [ShowcaseGridSurfaces.GameSummaries] = new GridDisplayRowCapabilities(
                    showGameSortPinOrderChoice: true)
            };

        private static readonly Dictionary<string, GridDisplayRowCapabilities> FriendSummarySurfaces =
            new Dictionary<string, GridDisplayRowCapabilities>(StringComparer.OrdinalIgnoreCase)
            {
                [GridOptionKeys.FriendSummaries.FriendsOverview] = AllRows,
                [GridOptionKeys.FriendSummaries.ViewFriendsAchievements] = AllRows
            };

        // CategorySummaryGridOptions does not derive from GridCommonOptions, so the editor already
        // collapses the common rows for every category surface.
        private static readonly Dictionary<string, GridDisplayRowCapabilities> CategorySummarySurfaces =
            new Dictionary<string, GridDisplayRowCapabilities>(StringComparer.OrdinalIgnoreCase)
            {
                [GridOptionKeys.CategorySummaries.ViewAchievements] = AllRows,
                [GridOptionKeys.CategorySummaries.OverviewSelectedGame] = AllRows,
                [GridOptionKeys.CategorySummaries.FriendsOverview] = AllRows,
                [GridOptionKeys.CategorySummaries.ViewFriendsAchievements] = AllRows,
                [GridOptionKeys.CategorySummaries.DesktopTheme] = AllRows
            };

        private static readonly Dictionary<string, string> AchievementTitleKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [GridOptionKeys.Achievement.OverviewRecent] = "LOCPlayAch_Settings_OverviewRecentAchievementsGrid",
                [GridOptionKeys.Achievement.OverviewSelectedGame] = "LOCPlayAch_Settings_SelectedGameGridSort",
                [GridOptionKeys.Achievement.SingleGame] = "LOCPlayAch_Settings_SelectedGameGridSort"
            };

        private static readonly Dictionary<string, string> GameSummaryTitleKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [GridOptionKeys.GameSummaries.Overview] = "LOCPlayAch_Settings_GameSummariesGridSort"
            };

        /// <summary>
        /// The rows the given surface offers. An unmapped key degrades to the kind's all-rows
        /// default rather than throwing, so a surface added to the catalog without a table entry
        /// stays editable.
        /// </summary>
        public static GridDisplayRowCapabilities Resolve(GridOptionKind kind, string surfaceKey)
        {
            if (string.IsNullOrEmpty(surfaceKey))
            {
                return AllRows;
            }

            var table = GetTable(kind);
            if (table.TryGetValue(surfaceKey, out var capabilities) && capabilities != null)
            {
                return capabilities;
            }

            // Showcase and start page widget surfaces carry a per-instance suffix, so every
            // instance of a widget kind shares its base key's row set.
            var baseKey = ShowcaseGridSurfaces.GetBaseKey(surfaceKey);
            if (!string.Equals(baseKey, surfaceKey, StringComparison.OrdinalIgnoreCase)
                && table.TryGetValue(baseKey, out var baseCapabilities)
                && baseCapabilities != null)
            {
                return baseCapabilities;
            }

            return AllRows;
        }

        /// <summary>
        /// The localization key naming the surface, for a window title.
        /// </summary>
        public static string ResolveTitleKey(GridOptionKind kind, string surfaceKey)
        {
            switch (kind)
            {
                case GridOptionKind.Achievement:
                    return LookupTitleKey(AchievementTitleKeys, surfaceKey, "LOCPlayAch_Achievements");
                case GridOptionKind.GameSummaries:
                    return LookupTitleKey(GameSummaryTitleKeys, surfaceKey, "LOCPlayAch_Overview_GameSummaries");
                case GridOptionKind.FriendSummaries:
                    return "LOCPlayAch_FriendsOverview_FriendSummaries";
                case GridOptionKind.CategorySummaries:
                    return "LOCPlayAch_Settings_CategoryGrid";
                default:
                    return "LOCPlayAch_Achievements";
            }
        }

        internal static IReadOnlyDictionary<string, GridDisplayRowCapabilities> GetTable(GridOptionKind kind)
        {
            switch (kind)
            {
                case GridOptionKind.Achievement:
                    return AchievementSurfaces;
                case GridOptionKind.GameSummaries:
                    return GameSummarySurfaces;
                case GridOptionKind.FriendSummaries:
                    return FriendSummarySurfaces;
                case GridOptionKind.CategorySummaries:
                    return CategorySummarySurfaces;
                default:
                    return AchievementSurfaces;
            }
        }

        private static string LookupTitleKey(
            Dictionary<string, string> table,
            string surfaceKey,
            string fallbackKey)
        {
            if (!string.IsNullOrEmpty(surfaceKey) && table.TryGetValue(surfaceKey, out var key))
            {
                return key;
            }

            return fallbackKey;
        }
    }
}
