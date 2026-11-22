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
            target.IncludeHiddenGamesInSummaries = source.IncludeHiddenGamesInSummaries;
            target.RefreshExclusionFilterPresetIds = new HashSet<Guid>(source.RefreshExclusionFilterPresetIds);
            target.SummaryExclusionFilterPresetIds = new HashSet<Guid>(source.SummaryExclusionFilterPresetIds);
            target.PeriodicUpdateHours = source.PeriodicUpdateHours;
            target.EnableFriendsPeriodicUpdates = source.EnableFriendsPeriodicUpdates;
            target.FriendsPeriodicUpdateHours = source.FriendsPeriodicUpdateHours;
            target.EnableInGamePolling = source.EnableInGamePolling;
            target.EnableAutoCapstoneGeneration = source.EnableAutoCapstoneGeneration;
            target.AutoCapstoneGameNameTemplate = source.AutoCapstoneGameNameTemplate;
            target.AutoCapstoneGameDescriptionTemplate = source.AutoCapstoneGameDescriptionTemplate;
            target.AutoCapstoneCategoryNameTemplate = source.AutoCapstoneCategoryNameTemplate;
            target.AutoCapstoneCategoryDescriptionTemplate = source.AutoCapstoneCategoryDescriptionTemplate;
            target.AutoCapstoneTemplateHistory = new List<string>(source.AutoCapstoneTemplateHistory ?? new List<string>());
            target.AutoCapstoneAppliedTemplates = source.AutoCapstoneAppliedTemplates;
            target.AutoCapstoneGameTags = source.AutoCapstoneGameTags;
            target.AutoCapstoneCategoryTags = source.AutoCapstoneCategoryTags;
            target.AutoCapstoneAppliedTags = source.AutoCapstoneAppliedTags;
            target.MetadataDefaults = (source.MetadataDefaults ?? MetadataChoices.CreateDefaults()).Clone();
            target.MetadataPlatformChoices = PersistedSettings.ClonePlatformChoices(source.MetadataPlatformChoices);
            target.MetadataChoicesSeeded = source.MetadataChoicesSeeded;
            target.MetadataConsoleVersion = source.MetadataConsoleVersion;
            target.InGamePollIntervalSeconds = source.InGamePollIntervalSeconds;
            target.InGamePollRefreshFriends = source.InGamePollRefreshFriends;
            target.InGameFriendRefreshMultiplier = source.InGameFriendRefreshMultiplier;
            target.InGameFriendBatchSize = source.InGameFriendBatchSize;
            target.RecentRefreshGamesCount = source.RecentRefreshGamesCount;
            target.DefaultOverviewRefreshMode = source.DefaultOverviewRefreshMode;
            target.AchievementTags = Services.Achievements.AchievementTagHelper.NormalizeDefinitions(source.AchievementTags, ensureSystemTags: true);
            target.CustomRefreshPresets = source.CustomRefreshPresets != null
                ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(source.CustomRefreshPresets))
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
            target.EnableWorkshopHotkey = source.EnableWorkshopHotkey;
            target.EnableAchievementEditorHotkey = source.EnableAchievementEditorHotkey;
            target.ViewAchievementsHotkey = source.ViewAchievementsHotkey;
            target.ManageAchievementsHotkey = source.ManageAchievementsHotkey;
            target.OverviewHotkey = source.OverviewHotkey;
            target.OpenSettingsHotkey = source.OpenSettingsHotkey;
            target.CategoryModeHotkey = source.CategoryModeHotkey;
            target.TestUnlockHotkey = source.TestUnlockHotkey;
            target.WorkshopHotkey = source.WorkshopHotkey;
            target.AchievementEditorHotkey = source.AchievementEditorHotkey;

            target.HiddenManageSidebarStatGroups = source.HiddenManageSidebarStatGroups;
            target.EnableCaptureTestFolder = source.EnableCaptureTestFolder;

            // Notification Settings
            target.EnableNotifications = source.EnableNotifications;
            target.EnableUnlockToasts = source.EnableUnlockToasts;
            target.EnableFriendUnlockToasts = source.EnableFriendUnlockToasts;
            target.EnableProgressToasts = source.EnableProgressToasts;
            target.SkipCompletionNotificationAfterCapstone = source.SkipCompletionNotificationAfterCapstone;
            target.ProgressToastIntervalPercent = source.ProgressToastIntervalPercent;
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
            target.NotificationDelaySeconds = source.NotificationDelaySeconds;
            target.CaptureDelaySeconds = source.CaptureDelaySeconds;
            target.CaptureAtUnlockAndAfterDelay = source.CaptureAtUnlockAndAfterDelay;
            target.MaxConcurrentToasts = source.MaxConcurrentToasts;
            target.ToastPosition = source.ToastPosition;
            target.EnableControllerVibration = source.EnableControllerVibration;
            target.ControllerVibrationStrengthPercent = source.ControllerVibrationStrengthPercent;
            target.ControllerVibrationDurationMs = source.ControllerVibrationDurationMs;
            target.UseHiddenUnlockSound = source.UseHiddenUnlockSound;
            target.EnableUnlockSounds = source.EnableUnlockSounds;
            target.AllowThemeUnlockSounds = source.AllowThemeUnlockSounds;
            target.UnlockSoundVolumePercent = source.UnlockSoundVolumePercent;
            target.UnlockSounds = source.UnlockSounds?.Clone() ?? UnlockSoundSettings.CreateDefault();
            target.ProviderUnlockSounds = source.ProviderUnlockSounds.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.Clone(),
                StringComparer.OrdinalIgnoreCase);
            target.UnlockSoundsSeededFromUniPlaySong = source.UnlockSoundsSeededFromUniPlaySong;
            target.EnableUnlockScreenshots = source.EnableUnlockScreenshots;
            target.UnlockScreenshotClean = source.UnlockScreenshotClean;
            target.UnlockScreenshotWithToast = source.UnlockScreenshotWithToast;
            target.UnlockScreenshotFramed = source.UnlockScreenshotFramed;
            target.UnlockScreenshotSuffixClean = source.UnlockScreenshotSuffixClean;
            target.UnlockScreenshotSuffixWithToast = source.UnlockScreenshotSuffixWithToast;
            target.UnlockScreenshotSuffixFramed = source.UnlockScreenshotSuffixFramed;
            target.UnlockScreenshotDirectory = source.UnlockScreenshotDirectory;
            target.IconCacheDirectory = source.IconCacheDirectory;
            target.LibraryLinks = LibraryLink.CloneAll(source.LibraryLinks);
            target.ScreenshotResolution = source.ScreenshotResolution;
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
            target.RecordingQuality = source.RecordingQuality;
            target.RecordingIncludeAudio = source.RecordingIncludeAudio;
            target.RecordingAudioSource = source.RecordingAudioSource;
            target.RecordingIncludeMicrophone = source.RecordingIncludeMicrophone;
            target.UnlockRecordingRarities = source.UnlockRecordingRarities;
            target.UnlockRecordingAlwaysCaptureCompletion = source.UnlockRecordingAlwaysCaptureCompletion;
            target.UnlockRecordingClean = source.UnlockRecordingClean;
            target.UnlockRecordingWithToast = source.UnlockRecordingWithToast;
            target.UnlockRecordingFramed = source.UnlockRecordingFramed;
            target.UnlockRecordingCleanRarities = source.UnlockRecordingCleanRarities;
            target.UnlockRecordingCleanAlwaysCaptureCompletion = source.UnlockRecordingCleanAlwaysCaptureCompletion;
            target.UnlockRecordingFramedRarities = source.UnlockRecordingFramedRarities;
            target.UnlockRecordingFramedAlwaysCaptureCompletion = source.UnlockRecordingFramedAlwaysCaptureCompletion;
            target.UnlockRecordingFramedSeconds = source.UnlockRecordingFramedSeconds;
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
            target.ShowLockedTitle = source.ShowLockedTitle;
            target.ShowLockedDescription = source.ShowLockedDescription;
            target.ShowHiddenTrophy = source.ShowHiddenTrophy;
            target.ShowHiddenPoints = source.ShowHiddenPoints;
            target.ShowLockedTrophy = source.ShowLockedTrophy;
            target.ShowLockedPoints = source.ShowLockedPoints;
            target.ShowHiddenRarity = source.ShowHiddenRarity;
            target.ShowLockedRarity = source.ShowLockedRarity;
            target.UseSeparateLockedIconsWhenAvailable = source.UseSeparateLockedIconsWhenAvailable;
            target.LockedFallbackIconPath = source.LockedFallbackIconPath;
            target.HiddenFallbackIconPath = source.HiddenFallbackIconPath;
            target.ModernCompactListShowRarityGlow = source.ModernCompactListShowRarityGlow;
            target.ModernUnlockedListShowRarityGlow = source.ModernUnlockedListShowRarityGlow;
            target.AnimateRarityGlows = source.AnimateRarityGlows;
            target.RarityGlowSoftTiers = source.RarityGlowSoftTiers;
            target.RarityGlowRayTiers = source.RarityGlowRayTiers;
            target.ShowHardcoreBorder = source.ShowHardcoreBorder;
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
            target.OverviewScoreCard1 = source.OverviewScoreCard1?.Clone();
            target.OverviewScoreCard2 = source.OverviewScoreCard2?.Clone();
            target.ShowOverviewScoreCard1 = source.ShowOverviewScoreCard1;
            target.ShowOverviewScoreCard2 = source.ShowOverviewScoreCard2;
            target.ShowTopMenuBarButton = source.ShowTopMenuBarButton;
            target.ShowSidebarButton = source.ShowSidebarButton;
            target.ShowCompletedProgressColoring = source.ShowCompletedProgressColoring;
            target.UseExophaseForSteamFriendOwnership = source.UseExophaseForSteamFriendOwnership;
            target.ShowFriendSpoilers = source.ShowFriendSpoilers;
            target.FriendsOverviewRecentUnlockLimit = source.FriendsOverviewRecentUnlockLimit;
            target.ShowCompactListRarityBar = source.ShowCompactListRarityBar;
            target.ShowCompactLockedListRarityBar = source.ShowCompactLockedListRarityBar;
            target.ShowCompactUnlockedListRarityBar = source.ShowCompactUnlockedListRarityBar;
            target.ProgressColumnAlignmentDefaulted = source.ProgressColumnAlignmentDefaulted;
            target.InlineSurfaceTransparencySeeded = source.InlineSurfaceTransparencySeeded;
            target.CommonGlowTierCleared = source.CommonGlowTierCleared;
            target.SurfaceOverrideMovedToPanel = source.SurfaceOverrideMovedToPanel;
            target.CategoryProgressColumnAlignmentDefaulted = source.CategoryProgressColumnAlignmentDefaulted;
            target.GridColumnHeaderAlignment = source.GridColumnHeaderAlignment;
            target.GridCellAlignment = source.GridCellAlignment;
            target.GridCellVerticalAlignment = source.GridCellVerticalAlignment;
            target.UnlockDateDisplayMode = source.UnlockDateDisplayMode;
            target.PlaytimeDisplayMode = source.PlaytimeDisplayMode;
            target.CategoryCompletionBadgeMode = source.CategoryCompletionBadgeMode;
            target.IconScaleMode = source.IconScaleMode;
            target.CornerRoundingRadius = source.CornerRoundingRadius;
            target.AchievementIconCornerRadius = source.AchievementIconCornerRadius;
            target.RoundGameArtCorners = source.RoundGameArtCorners;
            target.RoundAchievementIconCorners = source.RoundAchievementIconCorners;
            target.RoundLayoutCorners = source.RoundLayoutCorners;
            target.LayoutCornerRadius = source.LayoutCornerRadius;
            target.CategoryColumnDisplay = source.CategoryColumnDisplay;
            target.ProgressDetails = ProgressDetailOrder.Normalize(source.ProgressDetails);
            target.ProgressDetailShownCount = source.ProgressDetailShownCount;
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
            target.Showcase = source.Showcase?.Clone();
            target.OverviewMiniShowcase = source.OverviewMiniShowcase?.Clone();
            target.OverviewMiniShowcaseHeight = source.OverviewMiniShowcaseHeight;
            target.ShowOverviewMiniShowcase = source.ShowOverviewMiniShowcase;
            target.ControlBarStates = source.ControlBarStates;
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
            target.ViewAchievementsTimeWindow = source.ViewAchievementsTimeWindow;
            target.ViewAchievementsTimelineGranularity = source.ViewAchievementsTimelineGranularity;
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
