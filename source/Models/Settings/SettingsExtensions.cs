using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Tagging;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Extension methods for settings operations including copying and cloning.
    /// </summary>
    public static class SettingsExtensions
    {
        /// <summary>
        /// Copies all persisted settings from one PersistedSettings instance to another.
        /// This includes provider settings dictionary, update settings, notifications, display preferences,
        /// and theme integration settings.
        /// </summary>
        /// <param name="target">The target settings instance to copy to.</param>
        /// <param name="source">The source settings instance to copy from.</param>
        /// <exception cref="ArgumentNullException">Thrown when target is null.</exception>
        public static void CopyFrom(this PersistedSettings target, PersistedSettings source)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (source == null)
            {
                return;
            }

            // Provider Settings Dictionary (contains all provider-specific settings as JObject)
            target.ProviderSettings = source.ProviderSettings != null
                ? new Dictionary<string, JObject>(source.ProviderSettings, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            // Global Settings
            target.GlobalLanguage = source.GlobalLanguage;

            // Update and Refresh Settings
            target.EnablePeriodicUpdates = source.EnablePeriodicUpdates;
            target.IncludeHiddenGamesInBulkScans = source.IncludeHiddenGamesInBulkScans;
            target.PeriodicUpdateHours = source.PeriodicUpdateHours;
            target.RecentRefreshGamesCount = source.RecentRefreshGamesCount;
            target.CustomRefreshPresets = source.CustomRefreshPresets != null
                ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(source.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                : new List<CustomRefreshPreset>();

            // Hotkey Settings
            target.EnableAchievementHotkeys = source.EnableAchievementHotkeys;
            target.EnableGlobalAchievementHotkeys = source.EnableGlobalAchievementHotkeys;
            target.ViewAchievementsHotkey = source.ViewAchievementsHotkey;
            target.ManageAchievementsHotkey = source.ManageAchievementsHotkey;
            target.OverviewHotkey = source.OverviewHotkey;

            // Notification Settings
            target.EnableNotifications = source.EnableNotifications;
            target.NotifyPeriodicUpdates = source.NotifyPeriodicUpdates;
            target.NotifyOnRebuild = source.NotifyOnRebuild;

            // Display Preferences
            target.ShowHiddenIcon = source.ShowHiddenIcon;
            target.ShowHiddenTitle = source.ShowHiddenTitle;
            target.ShowHiddenDescription = source.ShowHiddenDescription;
            target.ShowHiddenSuffix = source.ShowHiddenSuffix;
            target.ShowLockedIcon = source.ShowLockedIcon;
            target.PreserveAchievementIconResolution = source.PreserveAchievementIconResolution;
            target.UseSeparateLockedIconsWhenAvailable = source.UseSeparateLockedIconsWhenAvailable;
            target.SeparateLockedIconEnabledGameIds = source.SeparateLockedIconEnabledGameIds != null
                ? new HashSet<Guid>(source.SeparateLockedIconEnabledGameIds)
                : new HashSet<Guid>();
            target.ShowRarityGlow = source.ShowRarityGlow;
            target.UseUniformRarityBadges = source.UseUniformRarityBadges;
            target.UseCoverImages = source.UseCoverImages;
            target.IncludeUnplayedGames = source.IncludeUnplayedGames;
            target.ShowOverviewCollectionScoreCard = source.ShowOverviewCollectionScoreCard;
            target.ShowOverviewPrestigeScoreCard = source.ShowOverviewPrestigeScoreCard;
            target.ShowOverviewPieCharts = source.ShowOverviewPieCharts;
            target.ShowOverviewGamesPieChart = source.ShowOverviewGamesPieChart;
            target.ShowOverviewProviderPieChart = source.ShowOverviewProviderPieChart;
            target.ShowOverviewRarityPieChart = source.ShowOverviewRarityPieChart;
            target.ShowOverviewTrophyPieChart = source.ShowOverviewTrophyPieChart;
            target.ShowOverviewPiePercentages = source.ShowOverviewPiePercentages;
            target.OverviewPieSmallSliceMode = source.OverviewPieSmallSliceMode;
            target.ShowOverviewBarCharts = source.ShowOverviewBarCharts;
            target.ShowOverviewGameMetadata = source.ShowOverviewGameMetadata;
            target.ShowTopMenuBarButton = source.ShowTopMenuBarButton;
            target.ShowCompactListRarityBar = source.ShowCompactListRarityBar;
            target.ShowCompletionBorder = source.ShowCompletionBorder;
            target.ShowOverviewGameSummariesGridColumnHeaders = source.ShowOverviewGameSummariesGridColumnHeaders;
            target.ShowOverviewRecentAchievementsGridColumnHeaders = source.ShowOverviewRecentAchievementsGridColumnHeaders;
            target.ShowOverviewSelectedGameGridColumnHeaders = source.ShowOverviewSelectedGameGridColumnHeaders;
            target.ShowDesktopThemeAchievementGridColumnHeaders = source.ShowDesktopThemeAchievementGridColumnHeaders;
            target.GridColumnHeaderAlignment = source.GridColumnHeaderAlignment;
            target.GridCellAlignment = source.GridCellAlignment;
            target.GridCellVerticalAlignment = source.GridCellVerticalAlignment;
            target.CompactListSortMode = source.CompactListSortMode;
            target.CompactListSortDescending = source.CompactListSortDescending;
            target.CompactUnlockedListSortMode = source.CompactUnlockedListSortMode;
            target.CompactUnlockedListSortDescending = source.CompactUnlockedListSortDescending;
            target.CompactLockedListSortMode = source.CompactLockedListSortMode;
            target.CompactLockedListSortDescending = source.CompactLockedListSortDescending;
            target.OverviewGameSummariesGridSortMode = source.OverviewGameSummariesGridSortMode;
            target.OverviewGameSummariesGridSortDescending = source.OverviewGameSummariesGridSortDescending;
            target.OverviewSelectedGameGridSortMode = source.OverviewSelectedGameGridSortMode;
            target.OverviewSelectedGameGridSortDescending = source.OverviewSelectedGameGridSortDescending;
            target.SingleGameGridSortMode = source.SingleGameGridSortMode;
            target.SingleGameGridSortDescending = source.SingleGameGridSortDescending;
            target.AchievementDataGridSortMode = source.AchievementDataGridSortMode;
            target.AchievementDataGridSortDescending = source.AchievementDataGridSortDescending;
            target.AchievementDataGridMaxHeight = source.AchievementDataGridMaxHeight;
            target.SingleGameGridRowHeight = source.SingleGameGridRowHeight;
            target.OverviewGameSummariesGridRowHeight = source.OverviewGameSummariesGridRowHeight;
            target.OverviewRecentAchievementsGridRowHeight = source.OverviewRecentAchievementsGridRowHeight;
            target.OverviewSelectedGameGridRowHeight = source.OverviewSelectedGameGridRowHeight;
            target.StartPageGameSummariesGridRowHeight = source.StartPageGameSummariesGridRowHeight;
            target.StartPageRecentAchievementsGridRowHeight = source.StartPageRecentAchievementsGridRowHeight;
            target.DesktopThemeAchievementGridRowHeight = source.DesktopThemeAchievementGridRowHeight;
            target.SingleGameGridMaxRows = source.SingleGameGridMaxRows;
            target.OverviewGameSummariesGridMaxRows = source.OverviewGameSummariesGridMaxRows;
            target.OverviewRecentAchievementsGridMaxRows = source.OverviewRecentAchievementsGridMaxRows;
            target.OverviewSelectedGameGridMaxRows = source.OverviewSelectedGameGridMaxRows;
            target.StartPageGameSummariesGridMaxRows = source.StartPageGameSummariesGridMaxRows;
            target.StartPageRecentAchievementsGridMaxRows = source.StartPageRecentAchievementsGridMaxRows;
            target.DesktopThemeAchievementGridMaxRows = source.DesktopThemeAchievementGridMaxRows;
            target.StartPageGameSummariesGrid = source.StartPageGameSummariesGrid?.Clone() ??
                new StartPageGameSummariesGridSettings();
            target.StartPageRecentUnlocksGrid = source.StartPageRecentUnlocksGrid?.Clone() ??
                new StartPageRecentUnlocksGridSettings();
            target.StartPagePieCharts = source.StartPagePieCharts?.Clone() ??
                new StartPagePieWidgetSettings();
            target.StartPageActivityScope = source.StartPageActivityScope;
            target.StartPageProgressScope = source.StartPageProgressScope;
            target.EnableParallelProviderRefresh = source.EnableParallelProviderRefresh;
            target.ScanDelayMs = source.ScanDelayMs;
            target.MaxRetryAttempts = source.MaxRetryAttempts;

            // UI Column Settings
            target.DataGridColumnVisibility = source.DataGridColumnVisibility != null
                ? new Dictionary<string, bool>(source.DataGridColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.DataGridColumnWidths = source.DataGridColumnWidths != null
                ? new Dictionary<string, double>(source.DataGridColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.DataGridColumnOrder = source.DataGridColumnOrder != null
                ? new Dictionary<string, int>(source.DataGridColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.OverviewRecentAchievementColumnVisibility = source.OverviewRecentAchievementColumnVisibility != null
                ? new Dictionary<string, bool>(source.OverviewRecentAchievementColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.OverviewRecentAchievementColumnWidths = source.OverviewRecentAchievementColumnWidths != null
                ? new Dictionary<string, double>(source.OverviewRecentAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.OverviewRecentAchievementColumnOrder = source.OverviewRecentAchievementColumnOrder != null
                ? new Dictionary<string, int>(source.OverviewRecentAchievementColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.OverviewRecentAchievementColumnAlignments = source.OverviewRecentAchievementColumnAlignments != null
                ? new Dictionary<string, GridAlignment>(source.OverviewRecentAchievementColumnAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewRecentAchievementColumnVerticalAlignments = source.OverviewRecentAchievementColumnVerticalAlignments != null
                ? new Dictionary<string, GridVerticalAlignment>(source.OverviewRecentAchievementColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewRecentAchievementColumnHeaderAlignments = source.OverviewRecentAchievementColumnHeaderAlignments != null
                ? new Dictionary<string, GridAlignment>(source.OverviewRecentAchievementColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewSelectedGameAchievementColumnVisibility = source.OverviewSelectedGameAchievementColumnVisibility != null
                ? new Dictionary<string, bool>(source.OverviewSelectedGameAchievementColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.OverviewSelectedGameAchievementColumnWidths = source.OverviewSelectedGameAchievementColumnWidths != null
                ? new Dictionary<string, double>(source.OverviewSelectedGameAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.OverviewSelectedGameAchievementColumnOrder = source.OverviewSelectedGameAchievementColumnOrder != null
                ? new Dictionary<string, int>(source.OverviewSelectedGameAchievementColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.OverviewSelectedGameAchievementColumnAlignments = source.OverviewSelectedGameAchievementColumnAlignments != null
                ? new Dictionary<string, GridAlignment>(source.OverviewSelectedGameAchievementColumnAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewSelectedGameAchievementColumnVerticalAlignments = source.OverviewSelectedGameAchievementColumnVerticalAlignments != null
                ? new Dictionary<string, GridVerticalAlignment>(source.OverviewSelectedGameAchievementColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewSelectedGameAchievementColumnHeaderAlignments = source.OverviewSelectedGameAchievementColumnHeaderAlignments != null
                ? new Dictionary<string, GridAlignment>(source.OverviewSelectedGameAchievementColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnVisibility = source.SingleGameColumnVisibility != null
                ? new Dictionary<string, bool>(source.SingleGameColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnWidths = source.SingleGameColumnWidths != null
                ? new Dictionary<string, double>(source.SingleGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnOrder = source.SingleGameColumnOrder != null
                ? new Dictionary<string, int>(source.SingleGameColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnAlignments = source.SingleGameColumnAlignments != null
                ? new Dictionary<string, GridAlignment>(source.SingleGameColumnAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnVerticalAlignments = source.SingleGameColumnVerticalAlignments != null
                ? new Dictionary<string, GridVerticalAlignment>(source.SingleGameColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnHeaderAlignments = source.SingleGameColumnHeaderAlignments != null
                ? new Dictionary<string, GridAlignment>(source.SingleGameColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.DesktopThemeColumnWidths = source.DesktopThemeColumnWidths != null
                ? new Dictionary<string, double>(source.DesktopThemeColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.DesktopThemeColumnOrder = source.DesktopThemeColumnOrder != null
                ? new Dictionary<string, int>(source.DesktopThemeColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.DesktopThemeColumnAlignments = source.DesktopThemeColumnAlignments != null
                ? new Dictionary<string, GridAlignment>(source.DesktopThemeColumnAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.DesktopThemeColumnVerticalAlignments = source.DesktopThemeColumnVerticalAlignments != null
                ? new Dictionary<string, GridVerticalAlignment>(source.DesktopThemeColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            target.DesktopThemeColumnHeaderAlignments = source.DesktopThemeColumnHeaderAlignments != null
                ? new Dictionary<string, GridAlignment>(source.DesktopThemeColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewGameSummariesColumnVisibility = source.OverviewGameSummariesColumnVisibility != null
                ? new Dictionary<string, bool>(source.OverviewGameSummariesColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.OverviewGameSummariesColumnWidths = source.OverviewGameSummariesColumnWidths != null
                ? new Dictionary<string, double>(source.OverviewGameSummariesColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.OverviewGameSummariesColumnOrder = source.OverviewGameSummariesColumnOrder != null
                ? new Dictionary<string, int>(source.OverviewGameSummariesColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.OverviewGameSummariesColumnAlignments = source.OverviewGameSummariesColumnAlignments != null
                ? new Dictionary<string, GridAlignment>(source.OverviewGameSummariesColumnAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewGameSummariesColumnVerticalAlignments = source.OverviewGameSummariesColumnVerticalAlignments != null
                ? new Dictionary<string, GridVerticalAlignment>(source.OverviewGameSummariesColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewGameSummariesColumnHeaderAlignments = source.OverviewGameSummariesColumnHeaderAlignments != null
                ? new Dictionary<string, GridAlignment>(source.OverviewGameSummariesColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.StartPageAchievementColumnVisibility = source.StartPageAchievementColumnVisibility != null
                ? new Dictionary<string, bool>(source.StartPageAchievementColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.StartPageAchievementColumnWidths = source.StartPageAchievementColumnWidths != null
                ? new Dictionary<string, double>(source.StartPageAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.StartPageAchievementColumnOrder = source.StartPageAchievementColumnOrder != null
                ? new Dictionary<string, int>(source.StartPageAchievementColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.StartPageAchievementColumnAlignments = source.StartPageAchievementColumnAlignments != null
                ? new Dictionary<string, GridAlignment>(source.StartPageAchievementColumnAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.StartPageAchievementColumnVerticalAlignments = source.StartPageAchievementColumnVerticalAlignments != null
                ? new Dictionary<string, GridVerticalAlignment>(source.StartPageAchievementColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            target.StartPageAchievementColumnHeaderAlignments = source.StartPageAchievementColumnHeaderAlignments != null
                ? new Dictionary<string, GridAlignment>(source.StartPageAchievementColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.StartPageGameSummariesColumnVisibility = source.StartPageGameSummariesColumnVisibility != null
                ? new Dictionary<string, bool>(source.StartPageGameSummariesColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.StartPageGameSummariesColumnWidths = source.StartPageGameSummariesColumnWidths != null
                ? new Dictionary<string, double>(source.StartPageGameSummariesColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.StartPageGameSummariesColumnOrder = source.StartPageGameSummariesColumnOrder != null
                ? new Dictionary<string, int>(source.StartPageGameSummariesColumnOrder, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            target.StartPageGameSummariesColumnAlignments = source.StartPageGameSummariesColumnAlignments != null
                ? new Dictionary<string, GridAlignment>(source.StartPageGameSummariesColumnAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.StartPageGameSummariesColumnVerticalAlignments = source.StartPageGameSummariesColumnVerticalAlignments != null
                ? new Dictionary<string, GridVerticalAlignment>(source.StartPageGameSummariesColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            target.StartPageGameSummariesColumnHeaderAlignments = source.StartPageGameSummariesColumnHeaderAlignments != null
                ? new Dictionary<string, GridAlignment>(source.StartPageGameSummariesColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            target.OverviewLeftColumnRatio = source.OverviewLeftColumnRatio;
            target.WindowPlacements = source.WindowPlacements != null
                ? source.WindowPlacements.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.Clone(),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase);
            target.OverviewTimelineRange = source.OverviewTimelineRange;
            target.ViewAchievementsTimelineRange = source.ViewAchievementsTimelineRange;
            target.ViewAchievementsTimelineVisible = source.ViewAchievementsTimelineVisible;

            // General Settings
            target.FirstTimeSetupCompleted = source.FirstTimeSetupCompleted;
            target.SeenThemeMigration = source.SeenThemeMigration;
            target.ThemeMigrationVersionCache = source.ThemeMigrationVersionCache != null
                ? source.ThemeMigrationVersionCache.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value == null
                        ? null
                        : new ThemeMigrationCacheEntry
                        {
                            ThemeName = kvp.Value.ThemeName,
                            ThemePath = kvp.Value.ThemePath,
                            MigratedThemeVersion = kvp.Value.MigratedThemeVersion,
                            MigratedAtUtc = kvp.Value.MigratedAtUtc
                        },
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ThemeMigrationCacheEntry>(StringComparer.OrdinalIgnoreCase);

            // User Preferences (Survive Cache Clear)
            target.ExcludedGameIds = source.ExcludedGameIds != null
                ? new HashSet<Guid>(source.ExcludedGameIds)
                : new HashSet<Guid>();
            target.ExcludedFromSummariesGameIds = source.ExcludedFromSummariesGameIds != null
                ? new HashSet<Guid>(source.ExcludedFromSummariesGameIds)
                : new HashSet<Guid>();
            target.ManualCapstones = source.ManualCapstones != null
                ? new Dictionary<Guid, string>(source.ManualCapstones)
                : new Dictionary<Guid, string>();
            target.AchievementOrderOverrides = source.AchievementOrderOverrides != null
                ? source.AchievementOrderOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null
                        ? new List<string>(kvp.Value)
                        : new List<string>())
                : new Dictionary<Guid, List<string>>();
            target.AchievementCategoryOverrides = source.AchievementCategoryOverrides != null
                ? source.AchievementCategoryOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null
                        ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                : new Dictionary<Guid, Dictionary<string, string>>();
            target.AchievementCategoryTypeOverrides = source.AchievementCategoryTypeOverrides != null
                ? source.AchievementCategoryTypeOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value != null
                        ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                : new Dictionary<Guid, Dictionary<string, string>>();

            // Tagging Settings
            target.TaggingSettings = source.TaggingSettings?.Clone() ?? new TaggingSettings();
        }

        /// <summary>
        /// Creates a deep copy of a PersistedSettings instance.
        /// This extension method delegates to the instance method for consistency.
        /// </summary>
        /// <param name="source">The source settings to clone.</param>
        /// <returns>A new PersistedSettings instance with copied values, or null if source is null.</returns>
        public static PersistedSettings Clone(this PersistedSettings source)
        {
            return source?.Clone();
        }
    }
}

