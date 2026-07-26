using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Bridges direct edits of <see cref="GridOptionsCatalog"/> option objects to the flat
    /// compatibility properties declared in PersistedSettings.GridCompatibility.cs. Views that
    /// bind option objects directly (e.g. the grid-options editor) mutate the option objects,
    /// which would otherwise not raise PropertyChanged on PersistedSettings, freezing live views
    /// that bind the flat names. The bridge listens to the catalog's OptionChanged event and
    /// raises PropertyChanged for every flat property that reads the changed member. It only
    /// raises notifications and never writes values, so it cannot loop: option-object setters
    /// and the flat-property setters both have equality guards.
    /// </summary>
    public partial class PersistedSettings
    {
        private GridOptionsCatalog _gridOptionsBridgeCatalog;

        /// <summary>
        /// Attaches the notification bridge to <paramref name="catalog"/>, detaching from any
        /// previously observed catalog. Called from the GridOptions getter and setter so the
        /// bridge follows the current instance across lazy creation, deserialization and
        /// wholesale replacement (Clone / reset). Returns the catalog for getter chaining.
        /// </summary>
        private GridOptionsCatalog AttachGridOptionsBridge(GridOptionsCatalog catalog)
        {
            if (catalog == null || ReferenceEquals(_gridOptionsBridgeCatalog, catalog))
            {
                return catalog;
            }

            if (_gridOptionsBridgeCatalog != null)
            {
                _gridOptionsBridgeCatalog.OptionChanged -= OnGridOptionChanged;
            }

            _gridOptionsBridgeCatalog = catalog;
            catalog.OptionChanged += OnGridOptionChanged;
            return catalog;
        }

        private void OnGridOptionChanged(string kindName, string surfaceKey, string memberName)
        {
            if (GridOptionFlatNames.TryGetValue(FlatNameKey(kindName, surfaceKey, memberName), out var flatNames))
            {
                foreach (var flatName in flatNames)
                {
                    OnPropertyChanged(flatName);
                }
            }
        }

        private static string FlatNameKey(string kindName, string surfaceKey, string memberName)
        {
            return string.Concat(kindName, "|", surfaceKey, "|", memberName);
        }

        /// <summary>
        /// (kind, surface key, member name) -> flat compatibility property names. Derived
        /// mechanically from PersistedSettings.GridCompatibility.cs: every flat property's getter
        /// names the surface accessor and the member it reads. Column dictionary flat properties
        /// map from member name "Columns" (the option object's Columns property), which is raised
        /// when the whole layout object is replaced. The three StartPage wrapper properties
        /// (StartPageGameSummariesGrid, StartPageRecentUnlocksGrid,
        /// StartPageFriendsRecentUnlocksGrid) are excluded: the wrappers subscribe to the option
        /// objects directly and PersistedSettings already forwards their changes.
        /// </summary>
        private static readonly Dictionary<string, string[]> GridOptionFlatNames = BuildGridOptionFlatNames();

        private static Dictionary<string, string[]> BuildGridOptionFlatNames()
        {
            var map = new Dictionary<string, string[]>(StringComparer.Ordinal);

            void Add(string kindName, string surfaceKey, string memberName, params string[] flatNames)
            {
                map[FlatNameKey(kindName, surfaceKey, memberName)] = flatNames;
            }

            const string A = GridOptionsCatalog.AchievementKindName;
            const string G = GridOptionsCatalog.GameSummariesKindName;
            const string F = GridOptionsCatalog.FriendSummariesKindName;
            const string C = GridOptionsCatalog.CategorySummariesKindName;

            // Achievement / Default
            Add(A, GridOptionKeys.Achievement.Default, nameof(AchievementGridOptions.ShowRarityGlow), nameof(ModernDataGridShowRarityGlow));
            Add(A, GridOptionKeys.Achievement.Default, nameof(AchievementGridOptions.ColorNamesByRarity), nameof(ModernDataGridColorNamesByRarity));
            Add(A, GridOptionKeys.Achievement.Default, nameof(AchievementGridOptions.ColorRarityColumnsByRarity), nameof(ModernDataGridColorRarityColumnsByRarity));
            Add(A, GridOptionKeys.Achievement.Default, nameof(AchievementGridOptions.SortMode), nameof(AchievementDataGridSortMode));
            Add(A, GridOptionKeys.Achievement.Default, nameof(AchievementGridOptions.SortDescending), nameof(AchievementDataGridSortDescending));
            Add(A, GridOptionKeys.Achievement.Default, nameof(AchievementGridOptions.MaxHeight), nameof(AchievementDataGridMaxHeight));
            Add(A, GridOptionKeys.Achievement.Default, nameof(AchievementGridOptions.Columns),
                nameof(DataGridColumnVisibility), nameof(DataGridColumnWidths), nameof(DataGridColumnOrder));

            // Achievement / SingleGame
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.ColorRarityColumnsByRarity), nameof(ViewAchievementsAchievementGridColorRarityColumnsByRarity));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.StartInCategoryMode), nameof(ViewAchievementsAchievementGridStartInCategoryMode));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.HideCategorySummaryRow), nameof(ViewAchievementsAchievementGridHideCategorySummaryRow));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.ShowControlBar), nameof(ShowViewAchievementsAchievementGridControlBar));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.UnlockDateMode), nameof(ViewAchievementsAchievementsUnlockDateMode));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.SortMode), nameof(SingleGameGridSortMode));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.SortDescending), nameof(SingleGameGridSortDescending));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.RowHeight), nameof(SingleGameGridRowHeight));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.MaxRows), nameof(SingleGameGridMaxRows));
            Add(A, GridOptionKeys.Achievement.SingleGame, nameof(AchievementGridOptions.Columns),
                nameof(SingleGameColumnVisibility), nameof(SingleGameColumnWidths), nameof(SingleGameColumnOrder),
                nameof(SingleGameColumnAlignments), nameof(SingleGameColumnVerticalAlignments), nameof(SingleGameColumnHeaderAlignments));

            // Achievement / OverviewRecent
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.ShowRarityGlow), nameof(OverviewRecentAchievementsShowRarityGlow));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.ColorNamesByRarity), nameof(OverviewRecentAchievementsColorNamesByRarity));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.ColorRarityColumnsByRarity), nameof(OverviewRecentAchievementsColorRarityColumnsByRarity));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.UseCoverImages), nameof(OverviewRecentAchievementsUseCoverImages));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.ShowControlBar), nameof(ShowOverviewRecentAchievementsGridControlBar));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.ShowColumnHeaders), nameof(ShowOverviewRecentAchievementsGridColumnHeaders));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.UnlockDateMode), nameof(OverviewRecentAchievementsUnlockDateMode));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.RowHeight), nameof(OverviewRecentAchievementsGridRowHeight));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.MaxRows), nameof(OverviewRecentAchievementsGridMaxRows));
            Add(A, GridOptionKeys.Achievement.OverviewRecent, nameof(AchievementGridOptions.Columns),
                nameof(OverviewRecentAchievementColumnVisibility), nameof(OverviewRecentAchievementColumnWidths), nameof(OverviewRecentAchievementColumnOrder),
                nameof(OverviewRecentAchievementColumnAlignments), nameof(OverviewRecentAchievementColumnVerticalAlignments), nameof(OverviewRecentAchievementColumnHeaderAlignments));

            // Achievement / OverviewSelectedGame
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.ShowRarityGlow), nameof(OverviewSelectedGameShowRarityGlow));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.ColorNamesByRarity), nameof(OverviewSelectedGameColorNamesByRarity));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.ColorRarityColumnsByRarity), nameof(OverviewSelectedGameColorRarityColumnsByRarity));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.StartInCategoryMode), nameof(OverviewSelectedGameAchievementsStartInCategoryMode));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.HideCategorySummaryRow), nameof(OverviewSelectedGameAchievementsHideCategorySummaryRow));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.ShowControlBar), nameof(ShowOverviewSelectedGameGridControlBar));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.ShowColumnHeaders), nameof(ShowOverviewSelectedGameGridColumnHeaders));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.UnlockDateMode), nameof(OverviewSelectedGameAchievementsUnlockDateMode));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.SortMode), nameof(OverviewSelectedGameGridSortMode));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.SortDescending), nameof(OverviewSelectedGameGridSortDescending));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.RowHeight), nameof(OverviewSelectedGameGridRowHeight));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.MaxRows), nameof(OverviewSelectedGameGridMaxRows));
            Add(A, GridOptionKeys.Achievement.OverviewSelectedGame, nameof(AchievementGridOptions.Columns),
                nameof(OverviewSelectedGameAchievementColumnVisibility), nameof(OverviewSelectedGameAchievementColumnWidths), nameof(OverviewSelectedGameAchievementColumnOrder),
                nameof(OverviewSelectedGameAchievementColumnAlignments), nameof(OverviewSelectedGameAchievementColumnVerticalAlignments), nameof(OverviewSelectedGameAchievementColumnHeaderAlignments));

            // Achievement / FriendsOverviewRecent
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.UseCoverImages), nameof(FriendsOverviewAchievementsUseCoverImages));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.ShowRarityGlow), nameof(FriendsOverviewAchievementsShowRarityGlow));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.ColorNamesByRarity), nameof(FriendsOverviewAchievementsColorNamesByRarity));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.ColorRarityColumnsByRarity), nameof(FriendsOverviewAchievementsColorRarityColumnsByRarity));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.StartInCategoryMode), nameof(FriendsOverviewAchievementsStartInCategoryMode));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.HideCategorySummaryRow), nameof(FriendsOverviewAchievementsHideCategorySummaryRow));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.SortMode), nameof(FriendsOverviewAchievementsGridSortMode));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.SortDescending), nameof(FriendsOverviewAchievementsGridSortDescending));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.ShowColumnHeaders), nameof(ShowFriendsOverviewAchievementsGridColumnHeaders));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.ShowControlBar), nameof(ShowFriendsOverviewAchievementsGridControlBar));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.UnlockDateMode), nameof(FriendsOverviewAchievementsUnlockDateMode));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.RowHeight), nameof(FriendsOverviewAchievementsGridRowHeight));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.MaxRows), nameof(FriendsOverviewAchievementsGridMaxRows));
            Add(A, GridOptionKeys.Achievement.FriendsOverviewRecent, nameof(AchievementGridOptions.Columns),
                nameof(FriendsOverviewAchievementColumnVisibility), nameof(FriendsOverviewAchievementColumnWidths), nameof(FriendsOverviewAchievementColumnOrder),
                nameof(FriendsOverviewAchievementColumnAlignments), nameof(FriendsOverviewAchievementColumnVerticalAlignments), nameof(FriendsOverviewAchievementColumnHeaderAlignments));

            // Achievement / StartPageRecent
            Add(A, GridOptionKeys.Achievement.StartPageRecent, nameof(AchievementGridOptions.UnlockDateMode), nameof(StartPageAchievementsUnlockDateMode));
            Add(A, GridOptionKeys.Achievement.StartPageRecent, nameof(AchievementGridOptions.RowHeight), nameof(StartPageRecentAchievementsGridRowHeight));
            Add(A, GridOptionKeys.Achievement.StartPageRecent, nameof(AchievementGridOptions.MaxRows), nameof(StartPageRecentAchievementsGridMaxRows));
            Add(A, GridOptionKeys.Achievement.StartPageRecent, nameof(AchievementGridOptions.Columns),
                nameof(StartPageAchievementColumnVisibility), nameof(StartPageAchievementColumnWidths), nameof(StartPageAchievementColumnOrder),
                nameof(StartPageAchievementColumnAlignments), nameof(StartPageAchievementColumnVerticalAlignments), nameof(StartPageAchievementColumnHeaderAlignments));

            // Achievement / StartPageFriendAchievements
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.UseCoverImages), nameof(StartPageFriendsRecentAchievementsUseCoverImages));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.ShowRarityGlow), nameof(StartPageFriendsRecentAchievementsShowRarityGlow));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.ColorNamesByRarity), nameof(StartPageFriendsRecentAchievementsColorNamesByRarity));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.ColorRarityColumnsByRarity), nameof(StartPageFriendsRecentAchievementsColorRarityColumnsByRarity));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.ShowControlBar), nameof(ShowStartPageFriendsRecentAchievementsGridControlBar));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.ShowColumnHeaders), nameof(ShowStartPageFriendsRecentAchievementsGridColumnHeaders));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.UnlockDateMode), nameof(StartPageFriendsRecentAchievementsUnlockDateMode));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.RowHeight), nameof(StartPageFriendsRecentAchievementsGridRowHeight));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.MaxRows), nameof(StartPageFriendsRecentAchievementsGridMaxRows));
            Add(A, GridOptionKeys.Achievement.StartPageFriendAchievements, nameof(AchievementGridOptions.Columns),
                nameof(StartPageFriendAchievementColumnVisibility), nameof(StartPageFriendAchievementColumnWidths), nameof(StartPageFriendAchievementColumnOrder),
                nameof(StartPageFriendAchievementColumnAlignments), nameof(StartPageFriendAchievementColumnVerticalAlignments), nameof(StartPageFriendAchievementColumnHeaderAlignments));

            // Achievement / ViewFriendsAchievements
            Add(A, GridOptionKeys.Achievement.ViewFriendsAchievements, nameof(AchievementGridOptions.ColorRarityColumnsByRarity), nameof(ViewFriendsAchievementsColorRarityColumnsByRarity));

            // Achievement / DesktopTheme
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.StartInCategoryMode), nameof(DesktopThemeAchievementGridStartInCategoryMode));
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.HideCategorySummaryRow), nameof(DesktopThemeAchievementGridHideCategorySummaryRow));
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.ShowControlBar), nameof(ShowDesktopThemeAchievementGridControlBar));
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.ShowColumnHeaders), nameof(ShowDesktopThemeAchievementGridColumnHeaders));
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.UnlockDateMode), nameof(DesktopThemeAchievementsUnlockDateMode));
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.RowHeight), nameof(DesktopThemeAchievementGridRowHeight));
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.MaxRows), nameof(DesktopThemeAchievementGridMaxRows));
            Add(A, GridOptionKeys.Achievement.DesktopTheme, nameof(AchievementGridOptions.Columns),
                nameof(DesktopThemeColumnWidths), nameof(DesktopThemeColumnOrder),
                nameof(DesktopThemeColumnAlignments), nameof(DesktopThemeColumnVerticalAlignments), nameof(DesktopThemeColumnHeaderAlignments));

            // GameSummaries / Overview
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.UseCoverImages), nameof(OverviewGameSummariesUseCoverImages));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ShowCompletionGlow), nameof(ShowCompletionGlow));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ColorRarityColumnsByRarity), nameof(OverviewGameSummariesColorRarityColumnsByRarity));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ShowNameAboveProgress), nameof(OverviewGameSummariesShowNameAboveProgress));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ShowMetadataPlatform), nameof(ShowOverviewGameMetadataPlatform));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ShowMetadataPlaytime), nameof(ShowOverviewGameMetadataPlaytime));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ShowMetadataRegion), nameof(ShowOverviewGameMetadataRegion));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ShowColumnHeaders), nameof(ShowOverviewGameSummariesGridColumnHeaders));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.ShowControlBar), nameof(ShowOverviewGameSummariesGridControlBar));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.LastPlayedDateMode), nameof(OverviewGameSummariesLastPlayedDateMode));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.SortMode), nameof(OverviewGameSummariesGridSortMode));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.SortDescending), nameof(OverviewGameSummariesGridSortDescending));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.RowHeight), nameof(OverviewGameSummariesGridRowHeight));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.MaxRows), nameof(OverviewGameSummariesGridMaxRows));
            Add(G, GridOptionKeys.GameSummaries.Overview, nameof(GameSummaryGridOptions.Columns),
                nameof(OverviewGameSummariesColumnVisibility), nameof(OverviewGameSummariesColumnWidths), nameof(OverviewGameSummariesColumnOrder),
                nameof(OverviewGameSummariesColumnAlignments), nameof(OverviewGameSummariesColumnVerticalAlignments), nameof(OverviewGameSummariesColumnHeaderAlignments));

            // GameSummaries / StartPage
            Add(G, GridOptionKeys.GameSummaries.StartPage, nameof(GameSummaryGridOptions.LastPlayedDateMode), nameof(StartPageGameSummariesLastPlayedDateMode));
            Add(G, GridOptionKeys.GameSummaries.StartPage, nameof(GameSummaryGridOptions.ColorRarityColumnsByRarity), nameof(StartPageGameSummariesColorRarityColumnsByRarity));
            Add(G, GridOptionKeys.GameSummaries.StartPage, nameof(GameSummaryGridOptions.ShowNameAboveProgress), nameof(StartPageGameSummariesShowNameAboveProgress));
            Add(G, GridOptionKeys.GameSummaries.StartPage, nameof(GameSummaryGridOptions.RowHeight), nameof(StartPageGameSummariesGridRowHeight));
            Add(G, GridOptionKeys.GameSummaries.StartPage, nameof(GameSummaryGridOptions.MaxRows), nameof(StartPageGameSummariesGridMaxRows));
            Add(G, GridOptionKeys.GameSummaries.StartPage, nameof(GameSummaryGridOptions.Columns),
                nameof(StartPageGameSummariesColumnVisibility), nameof(StartPageGameSummariesColumnWidths), nameof(StartPageGameSummariesColumnOrder),
                nameof(StartPageGameSummariesColumnAlignments), nameof(StartPageGameSummariesColumnVerticalAlignments), nameof(StartPageGameSummariesColumnHeaderAlignments));

            // GameSummaries / ViewAchievements
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.UseCoverImages), nameof(ViewAchievementsGameSummariesUseCoverImages));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.ShowMetadataPlatform), nameof(ViewAchievementsGameSummariesShowMetadataPlatform));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.ShowMetadataPlaytime), nameof(ViewAchievementsGameSummariesShowMetadataPlaytime));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.ShowMetadataRegion), nameof(ViewAchievementsGameSummariesShowMetadataRegion));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.ShowCompletionGlow), nameof(ViewAchievementsGameSummariesShowCompletionGlow));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.ColorRarityColumnsByRarity), nameof(ViewAchievementsGameSummariesColorRarityColumnsByRarity));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.ShowNameAboveProgress), nameof(ViewAchievementsGameSummariesShowNameAboveProgress));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.ShowColumnHeaders), nameof(ShowViewAchievementsGameSummariesGridColumnHeaders));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.LastPlayedDateMode), nameof(ViewAchievementsGameSummariesLastPlayedDateMode));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.RowHeight), nameof(ViewAchievementsGameSummariesGridRowHeight));
            Add(G, GridOptionKeys.GameSummaries.ViewAchievements, nameof(GameSummaryGridOptions.Columns),
                nameof(ViewAchievementsGameSummariesColumnVisibility), nameof(ViewAchievementsGameSummariesColumnWidths), nameof(ViewAchievementsGameSummariesColumnOrder),
                nameof(ViewAchievementsGameSummariesColumnAlignments), nameof(ViewAchievementsGameSummariesColumnVerticalAlignments), nameof(ViewAchievementsGameSummariesColumnHeaderAlignments));

            // GameSummaries / FriendsOverview
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.UseCoverImages), nameof(FriendsOverviewGameSummariesUseCoverImages));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ShowMetadataPlatform), nameof(FriendsOverviewGameSummariesShowMetadataPlatform));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ShowMetadataPlaytime), nameof(FriendsOverviewGameSummariesShowMetadataPlaytime));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ShowMetadataRegion), nameof(FriendsOverviewGameSummariesShowMetadataRegion));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ShowCompletionGlow), nameof(FriendsOverviewGameSummariesShowCompletionGlow));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ColorRarityColumnsByRarity), nameof(FriendsOverviewGameSummariesColorRarityColumnsByRarity));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ShowNameAboveProgress), nameof(FriendsOverviewGameSummariesShowNameAboveProgress));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ShowColumnHeaders), nameof(ShowFriendsOverviewGameSummariesGridColumnHeaders));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.ShowControlBar), nameof(ShowFriendsOverviewGameSummariesGridControlBar));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.LastPlayedDateMode), nameof(FriendsOverviewGameSummariesLastPlayedDateMode));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.SortMode), nameof(FriendsOverviewGameSummariesGridSortMode));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.SortDescending), nameof(FriendsOverviewGameSummariesGridSortDescending));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.RowHeight), nameof(FriendsOverviewGameSummariesGridRowHeight));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.MaxRows), nameof(FriendsOverviewGameSummariesGridMaxRows));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverview, nameof(GameSummaryGridOptions.Columns),
                nameof(FriendsOverviewGameSummariesColumnVisibility), nameof(FriendsOverviewGameSummariesColumnWidths), nameof(FriendsOverviewGameSummariesColumnOrder),
                nameof(FriendsOverviewGameSummariesColumnAlignments), nameof(FriendsOverviewGameSummariesColumnVerticalAlignments), nameof(FriendsOverviewGameSummariesColumnHeaderAlignments));

            // GameSummaries / FriendsOverviewSelectedFriend
            Add(G, GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend, nameof(GameSummaryGridOptions.ColorRarityColumnsByRarity), nameof(FriendsOverviewSelectedFriendGameSummariesColorRarityColumnsByRarity));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend, nameof(GameSummaryGridOptions.ShowNameAboveProgress), nameof(FriendsOverviewSelectedFriendGameSummariesShowNameAboveProgress));
            Add(G, GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend, nameof(GameSummaryGridOptions.Columns),
                nameof(FriendsOverviewSelectedFriendGameSummariesColumnVisibility), nameof(FriendsOverviewSelectedFriendGameSummariesColumnWidths), nameof(FriendsOverviewSelectedFriendGameSummariesColumnOrder),
                nameof(FriendsOverviewSelectedFriendGameSummariesColumnAlignments), nameof(FriendsOverviewSelectedFriendGameSummariesColumnVerticalAlignments), nameof(FriendsOverviewSelectedFriendGameSummariesColumnHeaderAlignments));

            // GameSummaries / ViewFriendsAchievements
            Add(G, GridOptionKeys.GameSummaries.ViewFriendsAchievements, nameof(GameSummaryGridOptions.ColorRarityColumnsByRarity), nameof(ViewFriendsAchievementsGameSummariesColorRarityColumnsByRarity));
            Add(G, GridOptionKeys.GameSummaries.ViewFriendsAchievements, nameof(GameSummaryGridOptions.ShowNameAboveProgress), nameof(ViewFriendsAchievementsGameSummariesShowNameAboveProgress));

            // GameSummaries / ViewFriendsAchievementsSelectedFriend
            Add(G, GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend, nameof(GameSummaryGridOptions.ColorRarityColumnsByRarity), nameof(ViewFriendsAchievementsSelectedFriendGameSummariesColorRarityColumnsByRarity));
            Add(G, GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend, nameof(GameSummaryGridOptions.ShowNameAboveProgress), nameof(ViewFriendsAchievementsSelectedFriendGameSummariesShowNameAboveProgress));

            // FriendSummaries / FriendsOverview
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.ShowColumnHeaders), nameof(ShowFriendsOverviewFriendSummariesGridColumnHeaders));
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.ShowControlBar), nameof(ShowFriendsOverviewFriendSummariesGridControlBar));
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.LastUnlockDateMode), nameof(FriendsOverviewFriendSummariesLastUnlockDateMode));
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.SortMode), nameof(FriendsOverviewFriendSummariesGridSortMode));
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.SortDescending), nameof(FriendsOverviewFriendSummariesGridSortDescending));
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.RowHeight), nameof(FriendsOverviewFriendSummariesGridRowHeight));
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.MaxRows), nameof(FriendsOverviewFriendSummariesGridMaxRows));
            Add(F, GridOptionKeys.FriendSummaries.FriendsOverview, nameof(FriendSummaryGridOptions.Columns),
                nameof(FriendsOverviewFriendSummariesColumnVisibility), nameof(FriendsOverviewFriendSummariesColumnWidths), nameof(FriendsOverviewFriendSummariesColumnOrder),
                nameof(FriendsOverviewFriendSummariesColumnAlignments), nameof(FriendsOverviewFriendSummariesColumnVerticalAlignments), nameof(FriendsOverviewFriendSummariesColumnHeaderAlignments));

            // CategorySummaries / ViewAchievements
            Add(C, GridOptionKeys.CategorySummaries.ViewAchievements, nameof(CategorySummaryGridOptions.ShowColumnHeaders), nameof(ShowViewAchievementsCategorySummariesGridColumnHeaders));
            Add(C, GridOptionKeys.CategorySummaries.ViewAchievements, nameof(CategorySummaryGridOptions.RowHeight), nameof(ViewAchievementsCategorySummariesGridRowHeight));
            Add(C, GridOptionKeys.CategorySummaries.ViewAchievements, nameof(CategorySummaryGridOptions.UseCoverImages), nameof(ViewAchievementsCategorySummariesUseCoverImages));
            Add(C, GridOptionKeys.CategorySummaries.ViewAchievements, nameof(CategorySummaryGridOptions.ShowCompletionGlow), nameof(ViewAchievementsCategorySummariesShowCompletionGlow));
            Add(C, GridOptionKeys.CategorySummaries.ViewAchievements, nameof(CategorySummaryGridOptions.ColorRarityColumnsByRarity), nameof(ViewAchievementsCategorySummariesColorRarityColumnsByRarity));
            Add(C, GridOptionKeys.CategorySummaries.ViewAchievements, nameof(CategorySummaryGridOptions.ShowNameAboveProgress), nameof(ViewAchievementsCategorySummariesShowNameAboveProgress));
            Add(C, GridOptionKeys.CategorySummaries.ViewAchievements, nameof(CategorySummaryGridOptions.Columns),
                nameof(ViewAchievementsCategorySummariesColumnVisibility), nameof(ViewAchievementsCategorySummariesColumnWidths), nameof(ViewAchievementsCategorySummariesColumnOrder),
                nameof(ViewAchievementsCategorySummariesColumnAlignments), nameof(ViewAchievementsCategorySummariesColumnVerticalAlignments), nameof(ViewAchievementsCategorySummariesColumnHeaderAlignments));

            // CategorySummaries / OverviewSelectedGame
            Add(C, GridOptionKeys.CategorySummaries.OverviewSelectedGame, nameof(CategorySummaryGridOptions.ShowColumnHeaders), nameof(ShowOverviewSelectedGameCategorySummariesGridColumnHeaders));
            Add(C, GridOptionKeys.CategorySummaries.OverviewSelectedGame, nameof(CategorySummaryGridOptions.RowHeight), nameof(OverviewSelectedGameCategorySummariesGridRowHeight));
            Add(C, GridOptionKeys.CategorySummaries.OverviewSelectedGame, nameof(CategorySummaryGridOptions.UseCoverImages), nameof(OverviewSelectedGameCategorySummariesUseCoverImages));
            Add(C, GridOptionKeys.CategorySummaries.OverviewSelectedGame, nameof(CategorySummaryGridOptions.ShowCompletionGlow), nameof(OverviewSelectedGameCategorySummariesShowCompletionGlow));
            Add(C, GridOptionKeys.CategorySummaries.OverviewSelectedGame, nameof(CategorySummaryGridOptions.ColorRarityColumnsByRarity), nameof(OverviewSelectedGameCategorySummariesColorRarityColumnsByRarity));
            Add(C, GridOptionKeys.CategorySummaries.OverviewSelectedGame, nameof(CategorySummaryGridOptions.ShowNameAboveProgress), nameof(OverviewSelectedGameCategorySummariesShowNameAboveProgress));
            Add(C, GridOptionKeys.CategorySummaries.OverviewSelectedGame, nameof(CategorySummaryGridOptions.Columns),
                nameof(OverviewSelectedGameCategorySummariesColumnVisibility), nameof(OverviewSelectedGameCategorySummariesColumnWidths), nameof(OverviewSelectedGameCategorySummariesColumnOrder),
                nameof(OverviewSelectedGameCategorySummariesColumnAlignments), nameof(OverviewSelectedGameCategorySummariesColumnVerticalAlignments), nameof(OverviewSelectedGameCategorySummariesColumnHeaderAlignments));

            // CategorySummaries / FriendsOverview
            Add(C, GridOptionKeys.CategorySummaries.FriendsOverview, nameof(CategorySummaryGridOptions.ShowColumnHeaders), nameof(ShowFriendsOverviewCategorySummariesGridColumnHeaders));
            Add(C, GridOptionKeys.CategorySummaries.FriendsOverview, nameof(CategorySummaryGridOptions.RowHeight), nameof(FriendsOverviewCategorySummariesGridRowHeight));
            Add(C, GridOptionKeys.CategorySummaries.FriendsOverview, nameof(CategorySummaryGridOptions.UseCoverImages), nameof(FriendsOverviewCategorySummariesUseCoverImages));
            Add(C, GridOptionKeys.CategorySummaries.FriendsOverview, nameof(CategorySummaryGridOptions.ShowCompletionGlow), nameof(FriendsOverviewCategorySummariesShowCompletionGlow));
            Add(C, GridOptionKeys.CategorySummaries.FriendsOverview, nameof(CategorySummaryGridOptions.ColorRarityColumnsByRarity), nameof(FriendsOverviewCategorySummariesColorRarityColumnsByRarity));
            Add(C, GridOptionKeys.CategorySummaries.FriendsOverview, nameof(CategorySummaryGridOptions.ShowNameAboveProgress), nameof(FriendsOverviewCategorySummariesShowNameAboveProgress));
            Add(C, GridOptionKeys.CategorySummaries.FriendsOverview, nameof(CategorySummaryGridOptions.Columns),
                nameof(FriendsOverviewCategorySummariesColumnVisibility), nameof(FriendsOverviewCategorySummariesColumnWidths), nameof(FriendsOverviewCategorySummariesColumnOrder),
                nameof(FriendsOverviewCategorySummariesColumnAlignments), nameof(FriendsOverviewCategorySummariesColumnVerticalAlignments), nameof(FriendsOverviewCategorySummariesColumnHeaderAlignments));

            // CategorySummaries / ViewFriendsAchievements
            Add(C, GridOptionKeys.CategorySummaries.ViewFriendsAchievements, nameof(CategorySummaryGridOptions.ColorRarityColumnsByRarity), nameof(ViewFriendsAchievementsCategorySummariesColorRarityColumnsByRarity));
            Add(C, GridOptionKeys.CategorySummaries.ViewFriendsAchievements, nameof(CategorySummaryGridOptions.ShowNameAboveProgress), nameof(ViewFriendsAchievementsCategorySummariesShowNameAboveProgress));

            // CategorySummaries / DesktopTheme
            Add(C, GridOptionKeys.CategorySummaries.DesktopTheme, nameof(CategorySummaryGridOptions.ShowColumnHeaders), nameof(ShowDesktopThemeCategorySummariesGridColumnHeaders));
            Add(C, GridOptionKeys.CategorySummaries.DesktopTheme, nameof(CategorySummaryGridOptions.RowHeight), nameof(DesktopThemeCategorySummariesGridRowHeight));
            Add(C, GridOptionKeys.CategorySummaries.DesktopTheme, nameof(CategorySummaryGridOptions.UseCoverImages), nameof(DesktopThemeCategorySummariesUseCoverImages));
            Add(C, GridOptionKeys.CategorySummaries.DesktopTheme, nameof(CategorySummaryGridOptions.ShowCompletionGlow), nameof(DesktopThemeCategorySummariesShowCompletionGlow));
            Add(C, GridOptionKeys.CategorySummaries.DesktopTheme, nameof(CategorySummaryGridOptions.ColorRarityColumnsByRarity), nameof(DesktopThemeCategorySummariesColorRarityColumnsByRarity));
            Add(C, GridOptionKeys.CategorySummaries.DesktopTheme, nameof(CategorySummaryGridOptions.ShowNameAboveProgress), nameof(DesktopThemeCategorySummariesShowNameAboveProgress));
            Add(C, GridOptionKeys.CategorySummaries.DesktopTheme, nameof(CategorySummaryGridOptions.Columns),
                nameof(DesktopThemeCategorySummariesColumnVisibility), nameof(DesktopThemeCategorySummariesColumnWidths), nameof(DesktopThemeCategorySummariesColumnOrder),
                nameof(DesktopThemeCategorySummariesColumnAlignments), nameof(DesktopThemeCategorySummariesColumnVerticalAlignments), nameof(DesktopThemeCategorySummariesColumnHeaderAlignments));

            return map;
        }
    }
}
