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

            // Friend Settings (Friends must be copied before FriendMergeGroups because the
            // merge-group setter normalizes against the current Friends collection)
            target.EnableFriendsFeatures = source.EnableFriendsFeatures;
            target.IncludeUnownedFriendGames = source.IncludeUnownedFriendGames;
            target.AutoDiscoverFriendProviderKeys = source.AutoDiscoverFriendProviderKeys != null
                ? new HashSet<string>(source.AutoDiscoverFriendProviderKeys, StringComparer.OrdinalIgnoreCase)
                : PersistedSettings.CreateDefaultAutoDiscoverFriendProviderKeys();
            target.Friends = new System.Collections.ObjectModel.ObservableCollection<FriendSettingsEntry>(
                (source.Friends ?? new System.Collections.ObjectModel.ObservableCollection<FriendSettingsEntry>())
                .Where(friend => friend != null)
                .Select(friend => friend.Clone()));
            target.FriendMergeGroups = new System.Collections.ObjectModel.ObservableCollection<FriendMergeGroup>(
                (source.FriendMergeGroups ?? new System.Collections.ObjectModel.ObservableCollection<FriendMergeGroup>())
                .Where(group => group != null)
                .Select(group => group.Clone()));

            // Global Settings
            target.GlobalLanguage = source.GlobalLanguage;

            // Update and Refresh Settings
            target.EnablePeriodicUpdates = source.EnablePeriodicUpdates;
            target.IncludeHiddenGamesInBulkScans = source.IncludeHiddenGamesInBulkScans;
            target.PeriodicUpdateHours = source.PeriodicUpdateHours;
            target.EnableFriendsPeriodicUpdates = source.EnableFriendsPeriodicUpdates;
            target.FriendsPeriodicUpdateHours = source.FriendsPeriodicUpdateHours;
            target.EnableInGamePolling = source.EnableInGamePolling;
            target.InGamePollIntervalSeconds = source.InGamePollIntervalSeconds;
            target.InGamePollRefreshFriends = source.InGamePollRefreshFriends;
            target.InGameFriendRefreshMultiplier = source.InGameFriendRefreshMultiplier;
            target.InGameFriendBatchSize = source.InGameFriendBatchSize;
            target.RecentRefreshGamesCount = source.RecentRefreshGamesCount;
            target.DefaultOverviewRefreshMode = source.DefaultOverviewRefreshMode;
            target.CustomRefreshPresets = source.CustomRefreshPresets != null
                ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(source.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                : new List<CustomRefreshPreset>();

            // Hotkey Settings
            target.EnableAchievementHotkeys = source.EnableAchievementHotkeys;
            target.EnableGlobalAchievementHotkeys = source.EnableGlobalAchievementHotkeys;
            target.EnableViewAchievementsHotkey = source.EnableViewAchievementsHotkey;
            target.EnableManageAchievementsHotkey = source.EnableManageAchievementsHotkey;
            target.EnableOverviewHotkey = source.EnableOverviewHotkey;
            target.EnableOpenSettingsHotkey = source.EnableOpenSettingsHotkey;
            target.EnableCategoryModeHotkey = source.EnableCategoryModeHotkey;
            target.EnableTestUnlockHotkey = source.EnableTestUnlockHotkey;
            target.ViewAchievementsHotkey = source.ViewAchievementsHotkey;
            target.ManageAchievementsHotkey = source.ManageAchievementsHotkey;
            target.OverviewHotkey = source.OverviewHotkey;
            target.OpenSettingsHotkey = source.OpenSettingsHotkey;
            target.CategoryModeHotkey = source.CategoryModeHotkey;
            target.TestUnlockHotkey = source.TestUnlockHotkey;

            // Notification Settings
            target.EnableNotifications = source.EnableNotifications;
            target.NotifyPeriodicUpdates = source.NotifyPeriodicUpdates;
            target.NotifyOnRebuild = source.NotifyOnRebuild;
            target.EnableUnlockToasts = source.EnableUnlockToasts;
            target.EnableFriendUnlockToasts = source.EnableFriendUnlockToasts;
            target.NotificationStyle = source.NotificationStyle?.Clone() ?? NotificationStyleSettings.CreateDefault();
            target.ToastUseThemeStyling = source.ToastUseThemeStyling;
            target.FrameUseThemeStyling = source.FrameUseThemeStyling;
            target.ProviderNotificationStyles = source.ProviderNotificationStyles != null
                ? source.ProviderNotificationStyles.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.Clone(),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, NotificationStyleSettings>(StringComparer.OrdinalIgnoreCase);
            target.ToastDurationSeconds = source.ToastDurationSeconds;
            target.MaxConcurrentToasts = source.MaxConcurrentToasts;
            target.ToastPosition = source.ToastPosition;
            target.EnableControllerVibration = source.EnableControllerVibration;
            target.ControllerVibrationStrengthPercent = source.ControllerVibrationStrengthPercent;
            target.ControllerVibrationDurationMs = source.ControllerVibrationDurationMs;
            target.EnableUnlockScreenshots = source.EnableUnlockScreenshots;
            target.UnlockScreenshotClean = source.UnlockScreenshotClean;
            target.UnlockScreenshotWithToast = source.UnlockScreenshotWithToast;
            target.UnlockScreenshotFramed = source.UnlockScreenshotFramed;
            target.UnlockScreenshotSuffixClean = source.UnlockScreenshotSuffixClean;
            target.UnlockScreenshotSuffixWithToast = source.UnlockScreenshotSuffixWithToast;
            target.UnlockScreenshotSuffixFramed = source.UnlockScreenshotSuffixFramed;
            target.UnlockScreenshotDirectory = source.UnlockScreenshotDirectory;
            target.UnlockScreenshotCleanRarities = source.UnlockScreenshotCleanRarities;
            target.UnlockScreenshotCleanAlwaysCaptureCompletion = source.UnlockScreenshotCleanAlwaysCaptureCompletion;
            target.UnlockScreenshotWithToastRarities = source.UnlockScreenshotWithToastRarities;
            target.UnlockScreenshotWithToastAlwaysCaptureCompletion = source.UnlockScreenshotWithToastAlwaysCaptureCompletion;
            target.UnlockScreenshotFramedRarities = source.UnlockScreenshotFramedRarities;
            target.UnlockScreenshotFramedAlwaysCaptureCompletion = source.UnlockScreenshotFramedAlwaysCaptureCompletion;
            target.EnableUnlockRecordings = source.EnableUnlockRecordings;
            target.UnlockRecordingDirectory = source.UnlockRecordingDirectory;
            target.RecordingClipSeconds = source.RecordingClipSeconds;
            target.RecordingFps = source.RecordingFps;
            target.RecordingResolution = source.RecordingResolution;
            target.RecordingIncludeAudio = source.RecordingIncludeAudio;
            target.RecordingAudioSource = source.RecordingAudioSource;
            target.RecordingIncludeMicrophone = source.RecordingIncludeMicrophone;
            target.UnlockRecordingRarities = source.UnlockRecordingRarities;
            target.UnlockRecordingAlwaysCaptureCompletion = source.UnlockRecordingAlwaysCaptureCompletion;
            target.ProviderNotificationOverrides = source.ProviderNotificationOverrides != null
                ? source.ProviderNotificationOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.Clone(),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ProviderNotificationOverride>(StringComparer.OrdinalIgnoreCase);

            // Display Preferences
            target.ShowHiddenIcon = source.ShowHiddenIcon;
            target.ShowHiddenTitle = source.ShowHiddenTitle;
            target.ShowHiddenDescription = source.ShowHiddenDescription;
            target.ShowHiddenSuffix = source.ShowHiddenSuffix;
            target.ShowLockedIcon = source.ShowLockedIcon;
            target.UseSeparateLockedIconsWhenAvailable = source.UseSeparateLockedIconsWhenAvailable;
            target.SeparateLockedIconEnabledGameIds = source.SeparateLockedIconEnabledGameIds != null
                ? new HashSet<Guid>(source.SeparateLockedIconEnabledGameIds)
                : new HashSet<Guid>();
            target.ModernCompactListShowRarityGlow = source.ModernCompactListShowRarityGlow;
            target.ModernUnlockedListShowRarityGlow = source.ModernUnlockedListShowRarityGlow;
            target.AnimateRarityGlows = source.AnimateRarityGlows;
            target.RarityGlowPulseMinOpacity = source.RarityGlowPulseMinOpacity;
            target.RarityGlowPulseMaxOpacity = source.RarityGlowPulseMaxOpacity;
            target.RarityGlowPulseSpeed = source.RarityGlowPulseSpeed;
            target.UseUniformRarityBadges = source.UseUniformRarityBadges;
            target.UseTrophiesForRarity = source.UseTrophiesForRarity;
            target.RoundRarityPercentages = source.RoundRarityPercentages;
            target.RarityColors = source.RarityColors?.Clone() ?? RarityColorSettings.CreateDefault();
            target.ProviderColorOverrides = source.ProviderColorOverrides != null
                ? new Dictionary<string, string>(
                    source.ProviderColorOverrides,
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            target.ShowTopMenuBarButton = source.ShowTopMenuBarButton;
            target.ShowCompletedProgressColoring = source.ShowCompletedProgressColoring;
            target.UseExophaseForSteamFriendOwnership = source.UseExophaseForSteamFriendOwnership;
            target.ShowFriendSpoilers = source.ShowFriendSpoilers;
            target.FriendsOverviewRecentUnlockLimit = source.FriendsOverviewRecentUnlockLimit;
            target.ShowCompactListRarityBar = source.ShowCompactListRarityBar;
            target.ProgressColumnAlignmentDefaulted = source.ProgressColumnAlignmentDefaulted;
            target.InlineSurfaceTransparencySeeded = source.InlineSurfaceTransparencySeeded;
            target.GridColumnHeaderAlignment = source.GridColumnHeaderAlignment;
            target.GridCellAlignment = source.GridCellAlignment;
            target.GridCellVerticalAlignment = source.GridCellVerticalAlignment;
            target.UnlockDateDisplayMode = source.UnlockDateDisplayMode;
            target.PlaytimeDisplayMode = source.PlaytimeDisplayMode;
            target.FriendNameDisplayMode = source.FriendNameDisplayMode;
            target.EnableAchievementCompactListControl = source.EnableAchievementCompactListControl;
            target.EnableAchievementDataGridControl = source.EnableAchievementDataGridControl;
            target.EnableAchievementCompactUnlockedListControl = source.EnableAchievementCompactUnlockedListControl;
            target.EnableAchievementCompactLockedListControl = source.EnableAchievementCompactLockedListControl;
            target.EnableAchievementProgressBarControl = source.EnableAchievementProgressBarControl;
            target.EnableAchievementStatsControl = source.EnableAchievementStatsControl;
            target.EnableAchievementButtonControl = source.EnableAchievementButtonControl;
            target.EnableAchievementViewItemControl = source.EnableAchievementViewItemControl;
            target.EnableAchievementPieChartControl = source.EnableAchievementPieChartControl;
            target.EnableAchievementBarChartControl = source.EnableAchievementBarChartControl;
            target.CompactListSortMode = source.CompactListSortMode;
            target.CompactListSortDescending = source.CompactListSortDescending;
            target.CompactUnlockedListSortMode = source.CompactUnlockedListSortMode;
            target.CompactUnlockedListSortDescending = source.CompactUnlockedListSortDescending;
            target.CompactLockedListSortMode = source.CompactLockedListSortMode;
            target.CompactLockedListSortDescending = source.CompactLockedListSortDescending;
            target.StartPagePieCharts = source.StartPagePieCharts?.Clone() ??
                new StartPagePieWidgetSettings();
            target.GridOptions = source.GridOptions?.Clone() ?? new GridOptionsCatalog();
            target.StartPageActivityScope = source.StartPageActivityScope;
            target.StartPageProgressScope = source.StartPageProgressScope;
            target.EnableParallelProviderRefresh = source.EnableParallelProviderRefresh;
            target.ScanDelayMs = source.ScanDelayMs;
            target.MaxRetryAttempts = source.MaxRetryAttempts;
            target.ResourceOverrides = source.ResourceOverrides != null
                ? source.ResourceOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.Clone(),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase);

            // Layout State
            target.OverviewLeftColumnRatio = source.OverviewLeftColumnRatio;
            target.FriendsOverviewFriendColumnRatio = source.FriendsOverviewFriendColumnRatio;
            target.FriendsOverviewGameColumnRatio = source.FriendsOverviewGameColumnRatio;
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
            target.EnableAutomaticThemeMigration = source.EnableAutomaticThemeMigration;
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

            // Fork compatibility settings
            target.ShowRarityGlow = source.ShowRarityGlow;
            target.DisabledRealtimeNotificationGameIds = source.DisabledRealtimeNotificationGameIds != null
                ? new HashSet<Guid>(source.DisabledRealtimeNotificationGameIds)
                : new HashSet<Guid>();
            target.PreferredProviderOverrides = source.PreferredProviderOverrides != null
                ? new Dictionary<Guid, string>(source.PreferredProviderOverrides)
                : new Dictionary<Guid, string>();
            target.ExtraLocalPaths = source.ExtraLocalPaths;
            target.ExcludedLocalPaths = source.ExcludedLocalPaths;
            target.LastAllGamesCollectorScore = source.LastAllGamesCollectorScore;
            target.LastAllGamesCollectorLevel = source.LastAllGamesCollectorLevel;
            target.LastAllGamesCollectorLevelProgress = source.LastAllGamesCollectorLevelProgress;
            target.LastAllGamesCollectorRank = source.LastAllGamesCollectorRank;
            target.LastAllGamesPrestigeScore = source.LastAllGamesPrestigeScore;
            target.LastAllGamesPrestigeLevel = source.LastAllGamesPrestigeLevel;
            target.LastAllGamesPrestigeLevelProgress = source.LastAllGamesPrestigeLevelProgress;
            target.LastAllGamesPrestigeRank = source.LastAllGamesPrestigeRank;
            target.DefaultAchievementSortMode = source.DefaultAchievementSortMode;
            target.DefaultAchievementSortDescending = source.DefaultAchievementSortDescending;
            target.CustomSortPath = source.CustomSortPath;
            target.CustomSortDescending = source.CustomSortDescending;
            target.GamesOverviewCustomSortPath = source.GamesOverviewCustomSortPath;
            target.GamesOverviewCustomSortDescending = source.GamesOverviewCustomSortDescending;
            target.GamesOverviewCustomSecondarySorts = source.GamesOverviewCustomSecondarySorts;
            target.GamesOverviewCustomSortUsesSourceOrder = source.GamesOverviewCustomSortUsesSourceOrder;
            target.RecentAchievementsCustomSortPath = source.RecentAchievementsCustomSortPath;
            target.RecentAchievementsCustomSortDescending = source.RecentAchievementsCustomSortDescending;
            target.RecentAchievementsCustomSecondarySorts = source.RecentAchievementsCustomSecondarySorts;
            target.RecentAchievementsCustomSortUsesSourceOrder = source.RecentAchievementsCustomSortUsesSourceOrder;
            target.SidebarAllAchievementsCustomSortPath = source.SidebarAllAchievementsCustomSortPath;
            target.SidebarAllAchievementsCustomSortDescending = source.SidebarAllAchievementsCustomSortDescending;
            target.SidebarAllAchievementsCustomSecondarySorts = source.SidebarAllAchievementsCustomSecondarySorts;
            target.SidebarAllAchievementsCustomSortUsesSourceOrder = source.SidebarAllAchievementsCustomSortUsesSourceOrder;
            target.SidebarSelectedGameCustomSortPath = source.SidebarSelectedGameCustomSortPath;
            target.SidebarSelectedGameCustomSortDescending = source.SidebarSelectedGameCustomSortDescending;
            target.SidebarSelectedGameCustomSecondarySorts = source.SidebarSelectedGameCustomSecondarySorts;
            target.SidebarSelectedGameCustomSortUsesSourceOrder = source.SidebarSelectedGameCustomSortUsesSourceOrder;
            target.DefaultUnlockNotificationStyle = source.DefaultUnlockNotificationStyle;
            target.ProviderUnlockNotificationStyles = source.ProviderUnlockNotificationStyles != null
                ? new Dictionary<string, string>(source.ProviderUnlockNotificationStyles, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            target.LastUpstreamReleaseNotificationVersion = source.LastUpstreamReleaseNotificationVersion;
            target.LastForkReleaseNotificationVersion = source.LastForkReleaseNotificationVersion;
            target.EnableGridTextWrapping = source.EnableGridTextWrapping;
            target.EnableCompactGridMode = source.EnableCompactGridMode;
            target.SidebarDefaultRefreshModeKey = source.SidebarDefaultRefreshModeKey;
            target.SidebarDefaultPlayStatusFilter = source.SidebarDefaultPlayStatusFilter;
            target.OverviewProviderFilterKeys = source.OverviewProviderFilterKeys;
            target.OverviewCompletenessFilterKeys = source.OverviewCompletenessFilterKeys;
            target.OverviewPlayStatusFilterKeys = source.OverviewPlayStatusFilterKeys;
            target.GamesOverviewGridSortMode = source.GamesOverviewGridSortMode;
            target.GamesOverviewGridSortDescending = source.GamesOverviewGridSortDescending;
            target.SidebarSelectedGameGridSortMode = source.SidebarSelectedGameGridSortMode;
            target.SidebarSelectedGameGridSortDescending = source.SidebarSelectedGameGridSortDescending;
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
