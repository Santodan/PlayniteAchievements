using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Extension methods for settings operations including copying, cloning, and migration.
    /// </summary>
    public static class SettingsExtensions
    {
        /// <summary>
        /// Copies all persisted settings from one PersistedSettings instance to another.
        /// This includes Steam settings, update settings, notifications, display preferences,
        /// theme integration settings, RetroAchievements settings, and rarity thresholds.
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

            // Steam Settings
            target.SteamUserId = source.SteamUserId;
            target.GogUserId = source.GogUserId;
            target.ExophaseUserId = source.ExophaseUserId;
            target.EpicAccountId = source.EpicAccountId;
            target.EpicAccessToken = source.EpicAccessToken;
            target.EpicRefreshToken = source.EpicRefreshToken;
            target.EpicTokenType = source.EpicTokenType;
            target.EpicTokenExpiryUtc = source.EpicTokenExpiryUtc;
            target.EpicRefreshTokenExpiryUtc = source.EpicRefreshTokenExpiryUtc;
            target.SteamApiKey = source.SteamApiKey;
            target.GlobalLanguage = source.GlobalLanguage;
            target.SteamEnabled = source.SteamEnabled;
            target.EpicEnabled = source.EpicEnabled;
            target.GogEnabled = source.GogEnabled;
            target.PsnEnabled = source.PsnEnabled;
            target.RetroAchievementsEnabled = source.RetroAchievementsEnabled;
            target.ManualEnabled = source.ManualEnabled;
            target.LegacyManualImportPath = source.LegacyManualImportPath;
            target.ManualTrackingOverrideEnabled = source.ManualTrackingOverrideEnabled;

            // Update and Refresh Settings
            target.EnablePeriodicUpdates = source.EnablePeriodicUpdates;
            target.AutoExcludeHiddenGames = source.AutoExcludeHiddenGames;
            target.PeriodicUpdateHours = source.PeriodicUpdateHours;
            target.RecentRefreshGamesCount = source.RecentRefreshGamesCount;
            target.CustomRefreshPresets = source.CustomRefreshPresets != null
                ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(source.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                : new List<CustomRefreshPreset>();

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
            target.ShowRarityGlow = source.ShowRarityGlow;
            target.UseCoverImages = source.UseCoverImages;
            target.IncludeUnplayedGames = source.IncludeUnplayedGames;
            target.ShowSidebarPieCharts = source.ShowSidebarPieCharts;
            target.ShowSidebarGamesPieChart = source.ShowSidebarGamesPieChart;
            target.ShowSidebarProviderPieChart = source.ShowSidebarProviderPieChart;
            target.ShowSidebarRarityPieChart = source.ShowSidebarRarityPieChart;
            target.ShowSidebarTrophyPieChart = source.ShowSidebarTrophyPieChart;
            target.ShowSidebarBarCharts = source.ShowSidebarBarCharts;

            // RetroAchievements Settings
            target.RaUsername = source.RaUsername;
            target.RaWebApiKey = source.RaWebApiKey;
            target.RaRarityStats = source.RaRarityStats;
            target.HashIndexMaxAgeDays = source.HashIndexMaxAgeDays;
            target.EnableArchiveScanning = source.EnableArchiveScanning;
            target.EnableDiscHashing = source.EnableDiscHashing;
            target.EnableRaNameFallback = source.EnableRaNameFallback;
            target.DataGridColumnVisibility = source.DataGridColumnVisibility != null
                ? new Dictionary<string, bool>(source.DataGridColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.DataGridColumnWidths = source.DataGridColumnWidths != null
                ? new Dictionary<string, double>(source.DataGridColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.SidebarAchievementColumnWidths = source.SidebarAchievementColumnWidths != null
                ? new Dictionary<string, double>(source.SidebarAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.SingleGameColumnWidths = source.SingleGameColumnWidths != null
                ? new Dictionary<string, double>(source.SingleGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.GamesOverviewColumnVisibility = source.GamesOverviewColumnVisibility != null
                ? new Dictionary<string, bool>(source.GamesOverviewColumnVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            target.GamesOverviewColumnWidths = source.GamesOverviewColumnWidths != null
                ? new Dictionary<string, double>(source.GamesOverviewColumnWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target.PointsColumnAutoEnabled = source.PointsColumnAutoEnabled;
            target.ExcludedFromSummariesGameIds = source.ExcludedFromSummariesGameIds != null
                ? new HashSet<Guid>(source.ExcludedFromSummariesGameIds)
                : new HashSet<Guid>();
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

            // Rarity Threshold Settings (order matters due to cross-property validation)
            target.UncommonThreshold = source.UncommonThreshold;
            target.RareThreshold = source.RareThreshold;
            target.UltraRareThreshold = source.UltraRareThreshold;
        }

        /// <summary>
        /// Creates a deep copy of a PersistedSettings instance.
        /// All property values are copied to a new instance.
        /// </summary>
        /// <param name="source">The source settings to clone.</param>
        /// <returns>A new PersistedSettings instance with copied values, or null if source is null.</returns>
        public static PersistedSettings Clone(this PersistedSettings source)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new PersistedSettings
            {
                // Steam Settings
                SteamUserId = source.SteamUserId,
                GogUserId = source.GogUserId,
                ExophaseUserId = source.ExophaseUserId,
                EpicAccountId = source.EpicAccountId,
                EpicAccessToken = source.EpicAccessToken,
                EpicRefreshToken = source.EpicRefreshToken,
                EpicTokenType = source.EpicTokenType,
                EpicTokenExpiryUtc = source.EpicTokenExpiryUtc,
                EpicRefreshTokenExpiryUtc = source.EpicRefreshTokenExpiryUtc,
                SteamApiKey = source.SteamApiKey,
                GlobalLanguage = source.GlobalLanguage,
                SteamEnabled = source.SteamEnabled,
                EpicEnabled = source.EpicEnabled,
                GogEnabled = source.GogEnabled,
                PsnEnabled = source.PsnEnabled,
                RetroAchievementsEnabled = source.RetroAchievementsEnabled,
                ManualEnabled = source.ManualEnabled,
                LegacyManualImportPath = source.LegacyManualImportPath,
                ManualTrackingOverrideEnabled = source.ManualTrackingOverrideEnabled,

                // Update and Refresh Settings
                EnablePeriodicUpdates = source.EnablePeriodicUpdates,
                AutoExcludeHiddenGames = source.AutoExcludeHiddenGames,
                PeriodicUpdateHours = source.PeriodicUpdateHours,
                RecentRefreshGamesCount = source.RecentRefreshGamesCount,
                CustomRefreshPresets = source.CustomRefreshPresets != null
                    ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(source.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                    : new List<CustomRefreshPreset>(),

                // Notification Settings
                EnableNotifications = source.EnableNotifications,
                NotifyPeriodicUpdates = source.NotifyPeriodicUpdates,
                NotifyOnRebuild = source.NotifyOnRebuild,

                // Display Preferences
                ShowHiddenIcon = source.ShowHiddenIcon,
                ShowHiddenTitle = source.ShowHiddenTitle,
                ShowHiddenDescription = source.ShowHiddenDescription,
                ShowHiddenSuffix = source.ShowHiddenSuffix,
                ShowLockedIcon = source.ShowLockedIcon,
                ShowRarityGlow = source.ShowRarityGlow,
                UseCoverImages = source.UseCoverImages,
                IncludeUnplayedGames = source.IncludeUnplayedGames,
                ShowSidebarPieCharts = source.ShowSidebarPieCharts,
                ShowSidebarGamesPieChart = source.ShowSidebarGamesPieChart,
                ShowSidebarProviderPieChart = source.ShowSidebarProviderPieChart,
                ShowSidebarRarityPieChart = source.ShowSidebarRarityPieChart,
                ShowSidebarTrophyPieChart = source.ShowSidebarTrophyPieChart,
                ShowSidebarBarCharts = source.ShowSidebarBarCharts,
                EnableCompactGridMode = source.EnableCompactGridMode,

                // RetroAchievements Settings
                RaUsername = source.RaUsername,
                RaWebApiKey = source.RaWebApiKey,
                RaRarityStats = source.RaRarityStats,
                HashIndexMaxAgeDays = source.HashIndexMaxAgeDays,
                EnableArchiveScanning = source.EnableArchiveScanning,
                EnableDiscHashing = source.EnableDiscHashing,
                EnableRaNameFallback = source.EnableRaNameFallback,
                DataGridColumnVisibility = source.DataGridColumnVisibility != null
                    ? new Dictionary<string, bool>(source.DataGridColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                DataGridColumnWidths = source.DataGridColumnWidths != null
                    ? new Dictionary<string, double>(source.DataGridColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SidebarAchievementColumnWidths = source.SidebarAchievementColumnWidths != null
                    ? new Dictionary<string, double>(source.SidebarAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnWidths = source.SingleGameColumnWidths != null
                    ? new Dictionary<string, double>(source.SingleGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                GamesOverviewColumnVisibility = source.GamesOverviewColumnVisibility != null
                    ? new Dictionary<string, bool>(source.GamesOverviewColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                GamesOverviewColumnWidths = source.GamesOverviewColumnWidths != null
                    ? new Dictionary<string, double>(source.GamesOverviewColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                PointsColumnAutoEnabled = source.PointsColumnAutoEnabled,
                ExcludedFromSummariesGameIds = source.ExcludedFromSummariesGameIds != null
                    ? new HashSet<Guid>(source.ExcludedFromSummariesGameIds)
                    : new HashSet<Guid>(),
                AchievementOrderOverrides = source.AchievementOrderOverrides != null
                    ? source.AchievementOrderOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value != null
                            ? new List<string>(kvp.Value)
                            : new List<string>())
                    : new Dictionary<Guid, List<string>>(),
                AchievementCategoryOverrides = source.AchievementCategoryOverrides != null
                    ? source.AchievementCategoryOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value != null
                            ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                    : new Dictionary<Guid, Dictionary<string, string>>(),
                AchievementCategoryTypeOverrides = source.AchievementCategoryTypeOverrides != null
                    ? source.AchievementCategoryTypeOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value != null
                            ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                    : new Dictionary<Guid, Dictionary<string, string>>(),

                // Rarity Threshold Settings (order matters due to cross-property validation)
                UncommonThreshold = source.UncommonThreshold,
                RareThreshold = source.RareThreshold,
                UltraRareThreshold = source.UltraRareThreshold
            };

            return clone;
        }

        /// <summary>
        /// Migrates settings from an old format to a new format.
        /// This method can be used to handle settings migration when the settings structure changes.
        /// Currently a no-op placeholder for future migration needs.
        /// </summary>
        /// <param name="settings">The settings instance to migrate.</param>
        /// <param name="oldVersion">The old settings version (if applicable).</param>
        /// <returns>The same settings instance with migrated values, for method chaining.</returns>
        public static PersistedSettings Migrate(
            this PersistedSettings settings,
            int? oldVersion = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            return settings;
        }
    }
}
