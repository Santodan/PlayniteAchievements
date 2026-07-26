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
            target.ViewAchievementsHotkey = source.ViewAchievementsHotkey;
            target.ManageAchievementsHotkey = source.ManageAchievementsHotkey;
            target.OverviewHotkey = source.OverviewHotkey;
            target.OpenSettingsHotkey = source.OpenSettingsHotkey;
            target.CategoryModeHotkey = source.CategoryModeHotkey;

            // Notification Settings
            target.EnableNotifications = source.EnableNotifications;
            target.NotifyPeriodicUpdates = source.NotifyPeriodicUpdates;
            target.NotifyOnRebuild = source.NotifyOnRebuild;
            target.EnableUnlockToasts = source.EnableUnlockToasts;
            target.EnableFriendUnlockToasts = source.EnableFriendUnlockToasts;
            target.ToastShowHeader = source.ToastShowHeader;
            target.ToastShowName = source.ToastShowName;
            target.ToastShowRarityBadge = source.ToastShowRarityBadge;
            target.ToastShowRarityGlow = source.ToastShowRarityGlow;
            target.ToastRarityColoredName = source.ToastRarityColoredName;
            target.ToastShowRarityPercent = source.ToastShowRarityPercent;
            target.ToastShowDescription = source.ToastShowDescription;
            target.ToastShowCategory = source.ToastShowCategory;
            target.ToastShowGameName = source.ToastShowGameName;
            target.ToastShowUnlockTime = source.ToastShowUnlockTime;
            target.ToastDurationSeconds = source.ToastDurationSeconds;
            target.MaxConcurrentToasts = source.MaxConcurrentToasts;
            target.ToastPosition = source.ToastPosition;
            target.EnableUnlockScreenshots = source.EnableUnlockScreenshots;
            target.UnlockScreenshotClean = source.UnlockScreenshotClean;
            target.UnlockScreenshotWithToast = source.UnlockScreenshotWithToast;
            target.UnlockScreenshotFramed = source.UnlockScreenshotFramed;
            target.FrameShowHeader = source.FrameShowHeader;
            target.FrameShowName = source.FrameShowName;
            target.FrameShowDescription = source.FrameShowDescription;
            target.FrameShowCategory = source.FrameShowCategory;
            target.FrameShowGameName = source.FrameShowGameName;
            target.FrameShowRarityBadge = source.FrameShowRarityBadge;
            target.FrameShowRarityPercent = source.FrameShowRarityPercent;
            target.FrameShowRarityGlow = source.FrameShowRarityGlow;
            target.FrameRarityColoredName = source.FrameRarityColoredName;
            target.FrameShowUnlockTime = source.FrameShowUnlockTime;
            target.UnlockScreenshotDirectory = source.UnlockScreenshotDirectory;
            target.EnableUnlockRecordings = source.EnableUnlockRecordings;
            target.FfmpegPath = source.FfmpegPath;
            target.UnlockRecordingDirectory = source.UnlockRecordingDirectory;
            target.RecordingClipSeconds = source.RecordingClipSeconds;
            target.RecordingFps = source.RecordingFps;
            target.RecordingResolution = source.RecordingResolution;
            target.RecordingEncoder = source.RecordingEncoder;
            target.RecordingCaptureBackend = source.RecordingCaptureBackend;
            target.RecordingIncludeAudio = source.RecordingIncludeAudio;
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
            target.UseUniformRarityBadges = source.UseUniformRarityBadges;
            target.UseTrophiesForRarity = source.UseTrophiesForRarity;
            target.RoundRarityPercentages = source.RoundRarityPercentages;
            target.RarityColors = source.RarityColors?.Clone() ?? RarityColorSettings.CreateDefault();
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
