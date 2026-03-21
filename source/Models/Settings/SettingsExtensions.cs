using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Tagging;
#if !TEST
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.GOG;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.PSN;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Providers.Xbox;
#endif

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Extension methods for settings operations including copying, cloning, and migration.
    /// </summary>
    public static class SettingsExtensions
    {
        /// <summary>
        /// Copies all persisted settings from one PersistedSettings instance to another.
        /// This includes provider settings dictionary, update settings, notifications, display preferences,
        /// theme integration settings and RetroAchievements settings.
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

            // Provider Settings Dictionary (contains all provider-specific settings)
            target.ProviderSettings = source.ProviderSettings != null
                ? new Dictionary<string, string>(source.ProviderSettings, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Global Settings
            target.GlobalLanguage = source.GlobalLanguage;

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
            target.ShowGamesWithNoUnlocks = source.ShowGamesWithNoUnlocks;
            target.ShowUnplayedGames = source.ShowUnplayedGames;
            target.ShowTopMenuBarButton = source.ShowTopMenuBarButton;
            target.ShowCompactListRarityBar = source.ShowCompactListRarityBar;
            target.EnableCompactGridMode = source.EnableCompactGridMode;
            target.AchievementDataGridMaxHeight = source.AchievementDataGridMaxHeight;
            target.EnableParallelProviderRefresh = source.EnableParallelProviderRefresh;
            target.ScanDelayMs = source.ScanDelayMs;
            target.MaxRetryAttempts = source.MaxRetryAttempts;

            // RetroAchievements Global Settings (non-provider specific)
            target.RaRarityStats = source.RaRarityStats;
            target.RaPointsMode = source.RaPointsMode;
            target.HashIndexMaxAgeDays = source.HashIndexMaxAgeDays;
            target.EnableArchiveScanning = source.EnableArchiveScanning;
            target.EnableDiscHashing = source.EnableDiscHashing;
            target.EnableRaNameFallback = source.EnableRaNameFallback;

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
        /// Provider-specific settings are cloned via the ProviderSettings dictionary.
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
                // Provider Settings Dictionary (contains all provider-specific settings)
                ProviderSettings = source.ProviderSettings != null
                    ? new Dictionary<string, string>(source.ProviderSettings, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),

                // Global Settings
                GlobalLanguage = source.GlobalLanguage,

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
                ShowGamesWithNoUnlocks = source.ShowGamesWithNoUnlocks,
                ShowUnplayedGames = source.ShowUnplayedGames,
                ShowTopMenuBarButton = source.ShowTopMenuBarButton,
                ShowCompactListRarityBar = source.ShowCompactListRarityBar,
                EnableCompactGridMode = source.EnableCompactGridMode,
                AchievementDataGridMaxHeight = source.AchievementDataGridMaxHeight,
                EnableParallelProviderRefresh = source.EnableParallelProviderRefresh,
                ScanDelayMs = source.ScanDelayMs,
                MaxRetryAttempts = source.MaxRetryAttempts,

                // RetroAchievements Global Settings (non-provider specific)
                RaRarityStats = source.RaRarityStats,
                RaPointsMode = source.RaPointsMode,
                HashIndexMaxAgeDays = source.HashIndexMaxAgeDays,
                EnableArchiveScanning = source.EnableArchiveScanning,
                EnableDiscHashing = source.EnableDiscHashing,
                EnableRaNameFallback = source.EnableRaNameFallback,

                // UI Column Settings
                DataGridColumnVisibility = source.DataGridColumnVisibility != null
                    ? new Dictionary<string, bool>(source.DataGridColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                DataGridColumnWidths = source.DataGridColumnWidths != null
                    ? new Dictionary<string, double>(source.DataGridColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SidebarAchievementColumnWidths = source.SidebarAchievementColumnWidths != null
                    ? new Dictionary<string, double>(source.SidebarAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SidebarGameColumnWidths = source.SidebarGameColumnWidths != null
                    ? new Dictionary<string, double>(source.SidebarGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnWidths = source.SingleGameColumnWidths != null
                    ? new Dictionary<string, double>(source.SingleGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                DesktopThemeColumnWidths = source.DesktopThemeColumnWidths != null
                    ? new Dictionary<string, double>(source.DesktopThemeColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                GamesOverviewColumnVisibility = source.GamesOverviewColumnVisibility != null
                    ? new Dictionary<string, bool>(source.GamesOverviewColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                GamesOverviewColumnWidths = source.GamesOverviewColumnWidths != null
                    ? new Dictionary<string, double>(source.GamesOverviewColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),

                // General Settings
                FirstTimeSetupCompleted = source.FirstTimeSetupCompleted,
                SeenThemeMigration = source.SeenThemeMigration,
                ThemeMigrationVersionCache = source.ThemeMigrationVersionCache != null
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
                    : new Dictionary<string, ThemeMigrationCacheEntry>(StringComparer.OrdinalIgnoreCase),

                // User Preferences (Survive Cache Clear)
                ExcludedGameIds = source.ExcludedGameIds != null
                    ? new HashSet<Guid>(source.ExcludedGameIds)
                    : new HashSet<Guid>(),
                ExcludedFromSummariesGameIds = source.ExcludedFromSummariesGameIds != null
                    ? new HashSet<Guid>(source.ExcludedFromSummariesGameIds)
                    : new HashSet<Guid>(),
                ManualCapstones = source.ManualCapstones != null
                    ? new Dictionary<Guid, string>(source.ManualCapstones)
                    : new Dictionary<Guid, string>(),
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

                // Tagging Settings
                TaggingSettings = source.TaggingSettings?.Clone() ?? new TaggingSettings()
            };

            return clone;
        }

        /// <summary>
        /// Migrates settings from an old format to a new format.
        /// Migrates flat provider properties to the ProviderSettings dictionary.
        /// </summary>
        /// <param name="settings">The settings instance to migrate.</param>
        /// <param name="oldVersion">The old settings version (if applicable).</param>
        /// <returns>The same settings instance with migrated values, for method chaining.</returns>
#if !TEST
        public static PersistedSettings Migrate(
            this PersistedSettings settings,
            int? oldVersion = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            // Migrate flat provider properties to dictionary if dictionary is empty
            // but we have evidence of existing settings (e.g., SteamUserId is populated)
            if (settings.ProviderSettings.Count == 0 && !string.IsNullOrEmpty(settings.SteamUserId))
            {
                // Steam
                var steam = new SteamSettings
                {
                    IsEnabled = settings.SteamEnabled,
                    SteamUserId = settings.SteamUserId,
                    SteamApiKey = settings.SteamApiKey
                };
                settings.ProviderSettings["Steam"] = steam.SerializeToJson();

                // Epic
                var epic = new EpicSettings
                {
                    IsEnabled = settings.EpicEnabled
                };
                settings.ProviderSettings["Epic"] = epic.SerializeToJson();

                // GOG
                var gog = new GogSettings
                {
                    IsEnabled = settings.GogEnabled
                };
                settings.ProviderSettings["GOG"] = gog.SerializeToJson();

                // PSN
                var psn = new PsnSettings
                {
                    IsEnabled = settings.PsnEnabled
                };
                settings.ProviderSettings["PSN"] = psn.SerializeToJson();

                // Xbox
                var xbox = new XboxSettings
                {
                    IsEnabled = settings.XboxEnabled,
                    LowResIcons = settings.XboxLowResIcons
                };
                settings.ProviderSettings["Xbox"] = xbox.SerializeToJson();

                // RetroAchievements
                var retro = new RetroAchievementsSettings
                {
                    IsEnabled = settings.RetroAchievementsEnabled,
                    RaUsername = settings.RaUsername,
                    RaWebApiKey = settings.RaWebApiKey
                };
                settings.ProviderSettings["RetroAchievements"] = retro.SerializeToJson();

                // Exophase - migrate collection properties too
                var exophase = new ExophaseSettings
                {
                    IsEnabled = settings.ExophaseEnabled,
                    ManagedProviders = settings.ExophaseManagedProviders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    IncludedGames = settings.ExophaseIncludedGames ?? new HashSet<Guid>(),
                    SlugOverrides = settings.ExophaseSlugOverrides ?? new Dictionary<Guid, string>()
                };
                settings.ProviderSettings["Exophase"] = exophase.SerializeToJson();

                // ShadPS4
                var shadps4 = new ShadPS4Settings
                {
                    IsEnabled = settings.ShadPS4Enabled,
                    GameDataPath = settings.ShadPS4GameDataPath
                };
                settings.ProviderSettings["ShadPS4"] = shadps4.SerializeToJson();

                // RPCS3
                var rpcs3 = new Rpcs3Settings
                {
                    IsEnabled = settings.Rpcs3Enabled,
                    ExecutablePath = settings.Rpcs3ExecutablePath
                };
                settings.ProviderSettings["RPCS3"] = rpcs3.SerializeToJson();

                // Xenia
                var xenia = new XeniaSettings
                {
                    IsEnabled = settings.XeniaEnabled,
                    AccountPath = settings.XeniaAccountPath
                };
                settings.ProviderSettings["Xenia"] = xenia.SerializeToJson();

                // Manual - migrate achievement links too
                var manual = new ManualSettings
                {
                    IsEnabled = settings.ManualEnabled,
                    ManualTrackingOverrideEnabled = settings.ManualTrackingOverrideEnabled,
                    AchievementLinks = settings.ManualAchievementLinks ?? new Dictionary<Guid, ManualAchievementLink>()
                };
                settings.ProviderSettings["Manual"] = manual.SerializeToJson();
            }

            return settings;
        }
#endif
    }
}
