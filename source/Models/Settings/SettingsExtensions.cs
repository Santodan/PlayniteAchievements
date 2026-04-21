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

            // Notification Settings
            target.EnableNotifications = source.EnableNotifications;
            target.NotifyPeriodicUpdates = source.NotifyPeriodicUpdates;
            target.NotifyOnRebuild = source.NotifyOnRebuild;
            target.LastUpstreamReleaseNotificationVersion = source.LastUpstreamReleaseNotificationVersion;
            target.LastForkReleaseNotificationVersion = source.LastForkReleaseNotificationVersion;

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
            target.UseCoverImages = source.UseCoverImages;
            target.IncludeUnplayedGames = source.IncludeUnplayedGames;
            target.ShowSidebarPieCharts = source.ShowSidebarPieCharts;
            target.ShowSidebarGamesPieChart = source.ShowSidebarGamesPieChart;
            target.ShowSidebarProviderPieChart = source.ShowSidebarProviderPieChart;
            target.ShowSidebarRarityPieChart = source.ShowSidebarRarityPieChart;
            target.ShowSidebarTrophyPieChart = source.ShowSidebarTrophyPieChart;
            target.ShowSidebarPiePercentages = source.ShowSidebarPiePercentages;
            target.SidebarPieSmallSliceMode = source.SidebarPieSmallSliceMode;
            target.ShowSidebarBarCharts = source.ShowSidebarBarCharts;
            target.ShowSidebarGameMetadata = source.ShowSidebarGameMetadata;
            target.ShowTopMenuBarButton = source.ShowTopMenuBarButton;
            target.ShowCompactListRarityBar = source.ShowCompactListRarityBar;
            target.EnableCompactGridMode = source.EnableCompactGridMode;
            target.EnableGridTextWrapping = source.EnableGridTextWrapping;
            target.CompactListSortMode = source.CompactListSortMode;
            target.CompactListSortDescending = source.CompactListSortDescending;
            target.CompactUnlockedListSortMode = source.CompactUnlockedListSortMode;
            target.CompactUnlockedListSortDescending = source.CompactUnlockedListSortDescending;
            target.CompactLockedListSortMode = source.CompactLockedListSortMode;
            target.CompactLockedListSortDescending = source.CompactLockedListSortDescending;
            target.SidebarSelectedGameGridSortMode = source.SidebarSelectedGameGridSortMode;
            target.SidebarSelectedGameGridSortDescending = source.SidebarSelectedGameGridSortDescending;
            target.SingleGameGridSortMode = source.SingleGameGridSortMode;
            target.SingleGameGridSortDescending = source.SingleGameGridSortDescending;
            target.AchievementDataGridSortMode = source.AchievementDataGridSortMode;
            target.AchievementDataGridSortDescending = source.AchievementDataGridSortDescending;
            target.AchievementDataGridMaxHeight = source.AchievementDataGridMaxHeight;
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
            target.SidebarAchievementColumnWidths = source.SidebarAchievementColumnWidths != null
                ? new Dictionary<string, double>(source.SidebarAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.SidebarGameColumnWidths = source.SidebarGameColumnWidths != null
                ? new Dictionary<string, double>(source.SidebarGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnWidths = source.SingleGameColumnWidths != null
                ? new Dictionary<string, double>(source.SingleGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.DesktopThemeColumnWidths = source.DesktopThemeColumnWidths != null
                ? new Dictionary<string, double>(source.DesktopThemeColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.GamesOverviewColumnVisibility = source.GamesOverviewColumnVisibility != null
                ? new Dictionary<string, bool>(source.GamesOverviewColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.GamesOverviewColumnWidths = source.GamesOverviewColumnWidths != null
                ? new Dictionary<string, double>(source.GamesOverviewColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

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

