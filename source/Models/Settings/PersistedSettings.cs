using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services.Achievements;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Persisted user settings for PlayniteAchievements plugin.
    /// These settings are serialized to the plugin settings JSON file.
    /// </summary>
    public partial class PersistedSettings : ObservableObject
    {
        public const double DefaultAchievementDataGridMaxHeight = 600d;
        public const double MinimumGridRowHeight = 32d;
        public const int DefaultStartPageGridMaxRows = 25;
        public const int MinimumGridMaxRows = 1;
        public const double DefaultOverviewLeftColumnRatio = 0.5d;
        public const double MinOverviewLeftColumnRatio = 0.01d;
        public const double MaxOverviewLeftColumnRatio = 0.99d;
        public const double DefaultFriendsOverviewFriendColumnRatio = 0.25d;
        public const double DefaultFriendsOverviewGameColumnRatio = 0.31d;
        public const double MinFriendsOverviewColumnRatio = 0.01d;
        public const double MaxFriendsOverviewColumnRatio = 0.98d;
        public const GameActivityScope DefaultStartPageActivityScope = GameActivityScope.Played;
        public const GameProgressScope DefaultStartPageProgressScope =
            GameProgressScope.Completed | GameProgressScope.InProgress;
        public const string DefaultViewAchievementsHotkey = "Ctrl+Alt+V";
        public const string DefaultManageAchievementsHotkey = "Ctrl+Alt+M";
        public const string DefaultOverviewHotkey = "Ctrl+Alt+O";
        public const string DefaultOpenSettingsHotkey = "Ctrl+Alt+P";
        public const string DefaultCategoryModeHotkey = "C";

        /// <summary>
        /// Column key of the Progress column in game-summaries grids (matches the XAML ColumnKey and
        /// the canonical key produced by <see cref="OverviewSettingsMigration"/>). The footer badge
        /// layout in this column responds to its horizontal alignment, defaulting to Right.
        /// </summary>
        public const string ProgressColumnKey = "GameSummaryProgression";

        public PersistedSettings()
        {
            AttachStartPageSettingsHandlers();
        }

        #region Backing Fields

        private string _globalLanguage = "english";
        private bool _enablePeriodicUpdates = true;
        private bool _includeHiddenGamesInBulkScans = true;
        private int _periodicUpdateHours = 6;
        private bool _enableFriendsPeriodicUpdates = false;
        private int _friendsPeriodicUpdateHours = 24;
        private bool _enableInGamePolling = true;
        private int _inGamePollIntervalSeconds = 15;
        private bool _inGamePollRefreshFriends = false;
        private int _inGameFriendRefreshMultiplier = 4;
        private int _inGameFriendBatchSize = 10;
        private bool _enableNotifications = true;
        private bool _notifyPeriodicUpdates = true;
        private bool _notifyOnRebuild = true;
        private bool _enableUnlockToasts = true;
        private bool _enableFriendUnlockToasts = true;
        private bool _toastShowHeader = true;
        private bool _toastShowName = true;
        private bool _toastShowRarityGlow = true;
        private bool _toastShowRarityBadge = true;
        private bool _toastRarityColoredName = true;
        private bool _toastShowRarityPercent = true;
        private bool _toastShowDescription = true;
        private bool _toastShowCategory = true;
        private bool _toastShowGameName = true;
        private bool _toastShowUnlockTime = false;
        private int _toastDurationSeconds = 6;
        private int _maxConcurrentToasts = 3;
        private bool _enableUnlockScreenshots = false;
        private bool _unlockScreenshotClean = false;
        private bool _unlockScreenshotWithToast = true;
        private bool _unlockScreenshotFramed = false;
        private bool _frameShowHeader = true;
        private bool _frameShowName = true;
        private bool _frameShowDescription = true;
        private bool _frameShowCategory = true;
        private bool _frameShowGameName = true;
        private bool _frameShowRarityBadge = true;
        private bool _frameShowRarityPercent = true;
        private bool _frameShowRarityGlow = true;
        private bool _frameRarityColoredName = true;
        private bool _frameShowUnlockTime = true;
        private string _unlockScreenshotDirectory;
        private bool _enableUnlockRecordings = false;
        private string _ffmpegPath;
        private string _unlockRecordingDirectory;
        private int _recordingClipSeconds = 15;
        private int _recordingFps = 30;
        private RecordingResolution _recordingResolution = RecordingResolution.Native;
        private RecordingEncoder _recordingEncoder = RecordingEncoder.Auto;
        private RecordingCaptureBackend _recordingCaptureBackend = RecordingCaptureBackend.Auto;
        private bool _recordingIncludeAudio = false;
        private Dictionary<string, ProviderNotificationOverride> _providerNotificationOverrides =
            new Dictionary<string, ProviderNotificationOverride>(StringComparer.OrdinalIgnoreCase);
        private ToastScreenCorner _toastPosition = ToastScreenCorner.BottomRight;
        private int _recentRefreshGamesCount = 10;
        private RefreshModeType _defaultOverviewRefreshMode = RefreshModeType.Installed;
        private bool _enableAchievementHotkeys = true;
        private bool _enableGlobalAchievementHotkeys = false;
        private string _viewAchievementsHotkey = DefaultViewAchievementsHotkey;
        private string _manageAchievementsHotkey = DefaultManageAchievementsHotkey;
        private string _overviewHotkey = DefaultOverviewHotkey;
        private string _openSettingsHotkey = DefaultOpenSettingsHotkey;
        private string _categoryModeHotkey = DefaultCategoryModeHotkey;
        private bool _showHiddenIcon = false;
        private bool _showHiddenTitle = false;
        private bool _showHiddenDescription = false;
        private bool _showHiddenSuffix = true;
        private bool _showLockedIcon = true;
        private bool _useSeparateLockedIconsWhenAvailable = false;
        private HashSet<Guid> _separateLockedIconEnabledGameIds = new HashSet<Guid>();
        private bool _modernCompactListShowRarityGlow = true;
        private bool _modernUnlockedListShowRarityGlow = true;
        private bool _useUniformRarityBadges = false;
        private bool _useTrophiesForRarity = false;
        private bool _roundRarityPercentages = false;
        private RarityColorSettings _rarityColors = RarityColorSettings.CreateDefault();
        private bool _includeUnplayedGames = true;
        private bool _showOverviewCollectionScoreCard = true;
        private bool _showOverviewPrestigeScoreCard = true;
        private bool _showOverviewPieCharts = true;
        private bool _showOverviewGamesPieChart = true;
        private bool _showOverviewProviderPieChart = true;
        private bool _showOverviewRarityPieChart = true;
        private bool _showOverviewTrophyPieChart = true;
        private bool _showOverviewPiePercentages = true;
        private bool _showFriendSpoilers;
        private int _friendsOverviewRecentUnlockLimit = 200;
        private OverviewPieSmallSliceMode _overviewPieSmallSliceMode = OverviewPieSmallSliceMode.Round;
        private bool _overviewPieChartVisibilityInitializedFromIndividualSettings;
        private bool _showOverviewBarCharts = true;
        private bool _showTopMenuBarButton = true;
        private bool _showCompletedProgressColoring = true;
        private bool _showCompactListRarityBar = true;
        private bool _progressColumnAlignmentDefaulted = true;
        private bool _inlineSurfaceTransparencySeeded = true;

        private GridAlignment _gridColumnHeaderAlignment = GridAlignment.Center;
        private GridAlignment _gridCellAlignment = GridAlignment.Left;
        private GridVerticalAlignment _gridCellVerticalAlignment = GridVerticalAlignment.Center;
        private DateDisplayMode _unlockDateDisplayMode = DateDisplayMode.DateAndTime;
        private PlaytimeDisplayMode _playtimeDisplayMode = PlaytimeDisplayMode.HoursAndMinutes;
        private bool _enableAchievementCompactListControl = true;
        private bool _enableAchievementDataGridControl = true;
        private bool _enableAchievementCompactUnlockedListControl = true;
        private bool _enableAchievementCompactLockedListControl = true;
        private bool _enableAchievementProgressBarControl = true;
        private bool _enableAchievementStatsControl = true;
        private bool _enableAchievementButtonControl = true;
        private bool _enableAchievementViewItemControl = true;
        private bool _enableAchievementPieChartControl = true;
        private bool _enableAchievementBarChartControl = true;
        private StartPageGameSummariesGridSettings _startPageGameSummariesGrid;
        private StartPageRecentUnlocksGridSettings _startPageRecentUnlocksGrid;
        private StartPageFriendsRecentUnlocksGridSettings _startPageFriendsRecentUnlocksGrid;
        private StartPagePieWidgetSettings _startPagePieCharts =
            new StartPagePieWidgetSettings();
        private GridOptionsCatalog _gridOptions = new GridOptionsCatalog();
        private GameActivityScope _startPageActivityScope = DefaultStartPageActivityScope;
        private GameProgressScope _startPageProgressScope = DefaultStartPageProgressScope;
        private bool _enableParallelProviderRefresh = true;
        private int _scanDelayMs = 200;
        private int _maxRetryAttempts = 3;
        // Deserialization target: left empty so a loaded config is taken verbatim. Newtonsoft
        // populates an existing non-null dictionary in place rather than replacing it, so a
        // pre-seeded field would re-introduce removed ("Follow Playnite") overrides on every load.
        // Fresh installs seed transparent inline surfaces via the plugin-reference constructor.
        private Dictionary<string, ResourceOverrideSetting> _resourceOverrides =
            new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase);
        private List<CustomRefreshPreset> _customRefreshPresets = new List<CustomRefreshPreset>();
        private double _overviewLeftColumnRatio = DefaultOverviewLeftColumnRatio;
        private double _friendsOverviewFriendColumnRatio = DefaultFriendsOverviewFriendColumnRatio;
        private double _friendsOverviewGameColumnRatio = DefaultFriendsOverviewGameColumnRatio;
        private Dictionary<string, WindowPlacementState> _windowPlacements =
            new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase);
        private TimelineRange _overviewTimelineRange = TimelineRange.OneYear;
        private TimelineRange _viewAchievementsTimelineRange = TimelineRange.OneYear;
        private bool _viewAchievementsTimelineVisible = false;
        private bool _firstTimeSetupCompleted = false;
        private bool _seenThemeMigration = false;
        private HashSet<Guid> _excludedGameIds = new HashSet<Guid>();
        private HashSet<Guid> _excludedFromSummariesGameIds = new HashSet<Guid>();
        private Dictionary<Guid, string> _manualCapstones = new Dictionary<Guid, string>();
        private Dictionary<Guid, List<string>> _achievementOrderOverrides = new Dictionary<Guid, List<string>>();
        private Dictionary<Guid, Dictionary<string, string>> _achievementCategoryOverrides =
            new Dictionary<Guid, Dictionary<string, string>>();
        private Dictionary<Guid, Dictionary<string, string>> _achievementCategoryTypeOverrides =
            new Dictionary<Guid, Dictionary<string, string>>();
        private Dictionary<string, ThemeMigrationCacheEntry> _themeMigrationVersionCache =
            new Dictionary<string, ThemeMigrationCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private CompactListSortMode _compactListSortMode = CompactListSortMode.None;
        private bool _compactListSortDescending = false;
        private CompactListSortMode _compactUnlockedListSortMode = CompactListSortMode.None;
        private bool _compactUnlockedListSortDescending = false;
        private CompactListSortMode _compactLockedListSortMode = CompactListSortMode.None;
        private bool _compactLockedListSortDescending = false;
        private TaggingSettings _taggingSettings;
        private Dictionary<string, JObject> _providerSettings = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private bool _enableFriendsFeatures = true;
        private HashSet<string> _autoDiscoverFriendProviderKeys = CreateDefaultAutoDiscoverFriendProviderKeys();
        private bool _useExophaseForSteamFriendOwnership = false;
        private bool _includeUnownedFriendGames = false;
        private ObservableCollection<FriendSettingsEntry> _friends = new ObservableCollection<FriendSettingsEntry>();
        private ObservableCollection<FriendMergeGroup> _friendMergeGroups = new ObservableCollection<FriendMergeGroup>();

        #endregion

        #region Provider Settings Dictionary

        /// <summary>
        /// Dictionary of provider settings as JSON objects.
        /// Key is the provider key (e.g., "Steam", "Epic"), value is the settings as a JObject.
        /// </summary>
        public Dictionary<string, JObject> ProviderSettings
        {
            get => _providerSettings;
            set => SetValue(ref _providerSettings, value ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase));
        }

        #endregion

        #region Friend Settings

        public bool EnableFriendsFeatures
        {
            get => _enableFriendsFeatures;
            set => SetValue(ref _enableFriendsFeatures, value);
        }

        public HashSet<string> AutoDiscoverFriendProviderKeys
        {
            get => _autoDiscoverFriendProviderKeys ?? (_autoDiscoverFriendProviderKeys = CreateDefaultAutoDiscoverFriendProviderKeys());
            set => SetValue(ref _autoDiscoverFriendProviderKeys, NormalizeProviderKeySet(value));
        }

        public bool UseExophaseForSteamFriendOwnership
        {
            get => _useExophaseForSteamFriendOwnership;
            set => SetValue(ref _useExophaseForSteamFriendOwnership, value);
        }

        /// <summary>
        /// When true, friend refreshes may scan games the current user does not own (the Full
        /// scope). When false, Full-scope requests are clamped to Shared so no unowned friend
        /// games are ever scanned.
        /// </summary>
        public bool IncludeUnownedFriendGames
        {
            get => _includeUnownedFriendGames;
            set => SetValue(ref _includeUnownedFriendGames, value);
        }

        public ObservableCollection<FriendSettingsEntry> Friends
        {
            get => _friends ?? (_friends = new ObservableCollection<FriendSettingsEntry>());
            set
            {
                if (SetValueAndReturn(ref _friends, NormalizeFriendEntries(value)))
                {
                    FriendMergeGroups = FriendMergeGroups;
                }
            }
        }

        public ObservableCollection<FriendMergeGroup> FriendMergeGroups
        {
            get => _friendMergeGroups ?? (_friendMergeGroups = new ObservableCollection<FriendMergeGroup>());
            set => SetValue(ref _friendMergeGroups, NormalizeFriendMergeGroups(value, Friends));
        }

        public static HashSet<string> CreateDefaultAutoDiscoverFriendProviderKeys()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Steam",
                "RetroAchievements"
            };
        }

        public bool IsFriendAutoDiscoverEnabled(string providerKey)
        {
            return !string.IsNullOrWhiteSpace(providerKey) &&
                   AutoDiscoverFriendProviderKeys.Contains(providerKey.Trim());
        }

        public void SetFriendAutoDiscoverEnabled(string providerKey, bool enabled)
        {
            providerKey = NormalizeProviderKeyToken(providerKey);
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            var keys = NormalizeProviderKeySet(AutoDiscoverFriendProviderKeys);
            var changed = enabled ? keys.Add(providerKey) : keys.Remove(providerKey);
            if (changed)
            {
                AutoDiscoverFriendProviderKeys = keys;
            }
        }

        public FriendSettingsEntry AddOrUpdateFriend(
            FriendIdentity identity,
            FriendSettingsSource source = FriendSettingsSource.AutoDiscovered)
        {
            if (identity == null)
            {
                return null;
            }

            return AddOrUpdateFriend(
                identity.ProviderKey,
                identity.ExternalUserId,
                identity.DisplayName,
                identity.AvatarUrl,
                identity.AvatarPath,
                source,
                null,
                identity.LastRefreshedUtc,
                null,
                null);
        }

        public FriendSettingsEntry AddOrUpdateFriend(
            string providerKey,
            string externalUserId,
            string displayName,
            string avatarUrl,
            string avatarPath,
            FriendSettingsSource source,
            IEnumerable<string> selectedPlatforms = null,
            DateTime? lastRefreshedUtc = null,
            DateTime? lastProbedUtc = null,
            string lastError = null)
        {
            providerKey = NormalizeProviderKeyToken(providerKey);
            externalUserId = NormalizeProviderKeyToken(externalUserId);
            if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(externalUserId))
            {
                return null;
            }

            var entries = NormalizeFriendEntries(Friends);
            var existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.ExternalUserId, externalUserId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new FriendSettingsEntry
                {
                    ProviderKey = providerKey,
                    ExternalUserId = externalUserId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? externalUserId : displayName.Trim(),
                    Source = source,
                    SelectedPlatforms = FriendSettingsEntry.NormalizePlatformList(selectedPlatforms),
                    AddedUtc = DateTime.UtcNow
                };
                entries.Add(existing);
            }
            else
            {
                if (source == FriendSettingsSource.Manual)
                {
                    existing.Source = FriendSettingsSource.Manual;
                }

                if (selectedPlatforms != null)
                {
                    existing.SelectedPlatforms = FriendSettingsEntry.NormalizePlatformList(selectedPlatforms);
                }
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existing.DisplayName = displayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                existing.AvatarUrl = avatarUrl.Trim();
            }

            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                existing.AvatarPath = avatarPath.Trim();
            }

            if (lastRefreshedUtc.HasValue)
            {
                existing.LastRefreshedUtc = lastRefreshedUtc;
            }

            if (lastProbedUtc.HasValue)
            {
                existing.LastProbedUtc = lastProbedUtc;
            }

            if (lastError != null)
            {
                existing.LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError.Trim();
            }

            Friends = entries;
            return GetFriendSetting(providerKey, externalUserId);
        }

        public bool SetFriendNickname(string providerKey, string externalUserId, string nickname)
        {
            var entry = GetFriendSetting(providerKey, externalUserId);
            if (entry == null)
            {
                return false;
            }

            var normalized = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            if (string.Equals(entry.Nickname, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            entry.Nickname = normalized;
            Friends = Friends;
            return true;
        }

        public FriendSettingsEntry GetFriendSetting(string providerKey, string externalUserId)
        {
            providerKey = NormalizeProviderKeyToken(providerKey);
            externalUserId = NormalizeProviderKeyToken(externalUserId);
            if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(externalUserId))
            {
                return null;
            }

            return Friends.FirstOrDefault(entry =>
                string.Equals(entry?.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry?.ExternalUserId, externalUserId, StringComparison.OrdinalIgnoreCase));
        }

        public List<FriendSettingsEntry> GetFriendSettings(string providerKey = null, bool includeIgnored = true)
        {
            var normalizedProvider = NormalizeProviderKeyToken(providerKey);
            return Friends
                .Where(entry => entry != null &&
                                (string.IsNullOrWhiteSpace(normalizedProvider) ||
                                 string.Equals(entry.ProviderKey, normalizedProvider, StringComparison.OrdinalIgnoreCase)) &&
                                (includeIgnored || !entry.IsIgnored))
                .Select(entry => entry.Clone().Normalize())
                .ToList();
        }

        public List<FriendIdentity> GetActiveFriendIdentities(string providerKey = null)
        {
            return GetFriendSettings(providerKey, includeIgnored: false)
                .Select(entry => new FriendIdentity
                {
                    ProviderKey = entry.ProviderKey,
                    ExternalUserId = entry.ExternalUserId,
                    DisplayName = entry.DisplayName,
                    AvatarUrl = entry.AvatarUrl,
                    AvatarPath = entry.AvatarPath,
                    LastRefreshedUtc = entry.LastRefreshedUtc
                })
                .ToList();
        }

        public HashSet<string> GetIgnoredFriendIds(string providerKey)
        {
            return new HashSet<string>(
                GetFriendSettings(providerKey)
                    .Where(entry => entry.IsIgnored)
                    .Select(entry => entry.ExternalUserId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
        }

        public bool SetFriendIgnored(string providerKey, string externalUserId, bool ignored)
        {
            var entry = GetFriendSetting(providerKey, externalUserId);
            if (entry == null || entry.IsIgnored == ignored)
            {
                return false;
            }

            entry.IsIgnored = ignored;
            Friends = Friends;
            return true;
        }

        public bool RemoveFriendSetting(string providerKey, string externalUserId)
        {
            providerKey = NormalizeProviderKeyToken(providerKey);
            externalUserId = NormalizeProviderKeyToken(externalUserId);
            if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(externalUserId))
            {
                return false;
            }

            var entries = NormalizeFriendEntries(Friends);
            var removed = false;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (string.Equals(entry?.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry?.ExternalUserId, externalUserId, StringComparison.OrdinalIgnoreCase))
                {
                    entries.RemoveAt(i);
                    removed = true;
                }
            }

            if (!removed)
            {
                return false;
            }

            Friends = entries;
            PruneFriendMergeGroupsForRemovedAccount(providerKey, externalUserId);
            return true;
        }

        public List<FriendMergeGroup> GetFriendMergeGroups()
        {
            return FriendMergeGroups
                .Where(group => group?.IsValid == true)
                .Select(group => group.Clone().Normalize())
                .ToList();
        }

        public FriendMergeGroup GetFriendMergeGroup(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return null;
            }

            return FriendMergeGroups
                .FirstOrDefault(group => string.Equals(group?.Id, groupId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?.Clone()
                ?.Normalize();
        }

        public FriendMergeGroup GetFriendMergeGroupForAccount(string providerKey, string externalUserId)
        {
            return FriendMergeGroups
                .FirstOrDefault(group => group?.Contains(providerKey, externalUserId) == true)
                ?.Clone()
                ?.Normalize();
        }

        public FriendMergeGroup AddOrUpdateFriendMergeGroup(
            IEnumerable<FriendAccountRef> members,
            string nickname = null,
            FriendAccountRef avatarAccount = null,
            string existingGroupId = null)
        {
            var normalizedMembers = NormalizeFriendMergeMembers(members, Friends);
            if (normalizedMembers.Count < 2)
            {
                return null;
            }

            var groups = NormalizeFriendMergeGroups(FriendMergeGroups, Friends);
            var target = !string.IsNullOrWhiteSpace(existingGroupId)
                ? groups.FirstOrDefault(group => string.Equals(group.Id, existingGroupId.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            if (target == null)
            {
                target = new FriendMergeGroup
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CreatedUtc = DateTime.UtcNow
                };
                groups.Add(target);
            }

            var memberKeys = new HashSet<string>(
                normalizedMembers.Select(member => member.Key),
                StringComparer.OrdinalIgnoreCase);
            for (var i = groups.Count - 1; i >= 0; i--)
            {
                var group = groups[i];
                if (ReferenceEquals(group, target))
                {
                    continue;
                }

                group.Members = (group.Members ?? new List<FriendAccountRef>())
                    .Where(member => member != null && !memberKeys.Contains(member.Key))
                    .ToList();
                if (group.Members.Count < 2)
                {
                    groups.RemoveAt(i);
                }
            }

            target.Members = normalizedMembers;
            target.Nickname = string.IsNullOrWhiteSpace(nickname) ? target.Nickname : nickname.Trim();
            target.AvatarAccount = avatarAccount?.Clone()?.Normalize();
            target.Normalize();
            FriendMergeGroups = groups;
            return GetFriendMergeGroup(target.Id);
        }

        public bool RemoveFriendMergeGroup(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return false;
            }

            var groups = NormalizeFriendMergeGroups(FriendMergeGroups, Friends);
            var removed = false;
            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (string.Equals(groups[i]?.Id, groupId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    groups.RemoveAt(i);
                    removed = true;
                }
            }

            if (!removed)
            {
                return false;
            }

            FriendMergeGroups = new ObservableCollection<FriendMergeGroup>(groups);
            return true;
        }

        public bool SetFriendMergeGroupNickname(string groupId, string nickname)
        {
            var groups = NormalizeFriendMergeGroups(FriendMergeGroups, Friends);
            var group = groups.FirstOrDefault(item => string.Equals(item?.Id, groupId?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                return false;
            }

            var normalized = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            if (string.Equals(group.Nickname, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            group.Nickname = normalized;
            FriendMergeGroups = new ObservableCollection<FriendMergeGroup>(groups);
            return true;
        }

        public bool SetFriendMergeGroupAvatar(string groupId, FriendAccountRef avatarAccount)
        {
            var groups = NormalizeFriendMergeGroups(FriendMergeGroups, Friends);
            var group = groups.FirstOrDefault(item => string.Equals(item?.Id, groupId?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                return false;
            }

            var normalized = avatarAccount?.Clone()?.Normalize();
            if (normalized == null || !group.Contains(normalized.ProviderKey, normalized.ExternalUserId))
            {
                normalized = group.Members.FirstOrDefault()?.Clone();
            }

            if (string.Equals(group.AvatarAccount?.Key, normalized?.Key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            group.AvatarAccount = normalized;
            FriendMergeGroups = new ObservableCollection<FriendMergeGroup>(groups);
            return true;
        }

        public bool MigrateLegacyProviderFriends()
        {
            var changed = false;
            var entries = NormalizeFriendEntries(Friends);
            changed |= MigrateLegacySteamFriends(entries);
            changed |= MigrateLegacyExophaseFriends(entries);
            if (changed)
            {
                Friends = entries;
            }

            return changed;
        }

        #endregion

        #region Global Settings

        /// <summary>
        /// Global language for achievement text, used by all providers that support localization.
        /// </summary>
        public string GlobalLanguage
        {
            get => _globalLanguage;
            set => SetValue(ref _globalLanguage, value);
        }

        #endregion

        #region Update and Refresh Settings

        /// <summary>
        /// Enable the background periodic updates.
        /// </summary>
        public bool EnablePeriodicUpdates
        {
            get => _enablePeriodicUpdates;
            set => SetValue(ref _enablePeriodicUpdates, value);
        }

        /// <summary>
        /// When true, bulk refreshes include games marked hidden in Playnite.
        /// Explicit user-targeted refreshes ignore this setting.
        /// </summary>
        public bool IncludeHiddenGamesInBulkScans
        {
            get => _includeHiddenGamesInBulkScans;
            set => SetValue(ref _includeHiddenGamesInBulkScans, value);
        }

        /// <summary>
        /// Hours between periodic background updates.
        /// </summary>
        public int PeriodicUpdateHours
        {
            get => _periodicUpdateHours;
            set => SetValue(ref _periodicUpdateHours, Math.Max(1, value));
        }

        /// <summary>
        /// Enable the background periodic Recent friends refresh.
        /// </summary>
        public bool EnableFriendsPeriodicUpdates
        {
            get => _enableFriendsPeriodicUpdates;
            set => SetValue(ref _enableFriendsPeriodicUpdates, value);
        }

        /// <summary>
        /// Hours between periodic background Recent friends refreshes.
        /// </summary>
        public int FriendsPeriodicUpdateHours
        {
            get => _friendsPeriodicUpdateHours;
            set => SetValue(ref _friendsPeriodicUpdateHours, Math.Max(1, value));
        }

        public bool EnableInGamePolling
        {
            get => _enableInGamePolling;
            set => SetValue(ref _enableInGamePolling, value);
        }

        public int InGamePollIntervalSeconds
        {
            get => _inGamePollIntervalSeconds;
            set => SetValue(ref _inGamePollIntervalSeconds, Math.Max(10, value));
        }

        public bool InGamePollRefreshFriends
        {
            get => _inGamePollRefreshFriends;
            set => SetValue(ref _inGamePollRefreshFriends, value);
        }

        public int InGameFriendRefreshMultiplier
        {
            get => _inGameFriendRefreshMultiplier;
            set => SetValue(ref _inGameFriendRefreshMultiplier, Math.Max(1, value));
        }

        public int InGameFriendBatchSize
        {
            get => _inGameFriendBatchSize;
            set => SetValue(ref _inGameFriendBatchSize, Math.Max(0, value));
        }

        /// <summary>
        /// Maximum recent games to refresh when using Recent Refresh.
        /// </summary>
        public int RecentRefreshGamesCount
        {
            get => _recentRefreshGamesCount;
            set => SetValue(ref _recentRefreshGamesCount, Math.Max(1, value));
        }

        /// <summary>
        /// Refresh mode the Overview view's refresh dropdown defaults to on open.
        /// </summary>
        public RefreshModeType DefaultOverviewRefreshMode
        {
            get => _defaultOverviewRefreshMode;
            set => SetValue(ref _defaultOverviewRefreshMode, value);
        }

        /// <summary>
        /// Saved presets for Custom Refresh dialog.
        /// </summary>
        public List<CustomRefreshPreset> CustomRefreshPresets
        {
            get => _customRefreshPresets;
            set
            {
                var normalized = new List<CustomRefreshPreset>(
                    CustomRefreshPreset.NormalizePresets(value, CustomRefreshPreset.MaxPresetCount));
                SetValue(ref _customRefreshPresets, normalized);
            }
        }

        #endregion

        #region Hotkey Settings

        /// <summary>
        /// Enables keyboard shortcuts for achievement windows while Playnite is focused.
        /// </summary>
        public bool EnableAchievementHotkeys
        {
            get => _enableAchievementHotkeys;
            set => SetValue(ref _enableAchievementHotkeys, value);
        }

        /// <summary>
        /// Registers eligible achievement hotkeys with Windows so they work outside Playnite.
        /// </summary>
        public bool EnableGlobalAchievementHotkeys
        {
            get => _enableGlobalAchievementHotkeys;
            set => SetValue(ref _enableGlobalAchievementHotkeys, value);
        }

        /// <summary>
        /// Shortcut that opens, focuses, or toggles the View Achievements window.
        /// </summary>
        public string ViewAchievementsHotkey
        {
            get => _viewAchievementsHotkey;
            set => SetValue(ref _viewAchievementsHotkey, NormalizeHotkeyText(value));
        }

        /// <summary>
        /// Shortcut that opens, focuses, or toggles the Manage Achievements window.
        /// </summary>
        public string ManageAchievementsHotkey
        {
            get => _manageAchievementsHotkey;
            set => SetValue(ref _manageAchievementsHotkey, NormalizeHotkeyText(value));
        }

        /// <summary>
        /// Shortcut that opens, focuses, or toggles the Achievements Overview window.
        /// </summary>
        public string OverviewHotkey
        {
            get => _overviewHotkey;
            set => SetValue(ref _overviewHotkey, NormalizeHotkeyText(value));
        }

        /// <summary>
        /// Shortcut that opens the plugin settings window.
        /// </summary>
        public string OpenSettingsHotkey
        {
            get => _openSettingsHotkey;
            set => SetValue(ref _openSettingsHotkey, NormalizeHotkeyText(value));
        }

        /// <summary>
        /// Shortcut that flips category mode in a focused achievement grid, when the
        /// category toggle is available there.
        /// </summary>
        public string CategoryModeHotkey
        {
            get => _categoryModeHotkey;
            set => SetValue(ref _categoryModeHotkey, NormalizeHotkeyText(value));
        }

        #endregion

        #region Notification Settings

        /// <summary>
        /// Enable non-modal notifications (toasts) from the plugin.
        /// </summary>
        public bool EnableNotifications
        {
            get => _enableNotifications;
            set => SetValue(ref _enableNotifications, value);
        }

        /// <summary>
        /// Show lightweight toast when periodic background updates complete.
        /// </summary>
        public bool NotifyPeriodicUpdates
        {
            get => _notifyPeriodicUpdates;
            set => SetValue(ref _notifyPeriodicUpdates, value);
        }

        /// <summary>
        /// Show a toast when a manual or managed rebuild completes or fails.
        /// </summary>
        public bool NotifyOnRebuild
        {
            get => _notifyOnRebuild;
            set => SetValue(ref _notifyOnRebuild, value);
        }

        public bool EnableUnlockToasts
        {
            get => _enableUnlockToasts;
            set => SetValue(ref _enableUnlockToasts, value);
        }

        public bool EnableFriendUnlockToasts
        {
            get => _enableFriendUnlockToasts;
            set => SetValue(ref _enableFriendUnlockToasts, value);
        }

        public bool ToastShowHeader
        {
            get => _toastShowHeader;
            set => SetValue(ref _toastShowHeader, value);
        }

        public bool ToastShowName
        {
            get => _toastShowName;
            set => SetValue(ref _toastShowName, value);
        }

        public bool ToastShowRarityBadge
        {
            get => _toastShowRarityBadge;
            set => SetValue(ref _toastShowRarityBadge, value);
        }

        public bool ToastShowRarityGlow
        {
            get => _toastShowRarityGlow;
            set => SetValue(ref _toastShowRarityGlow, value);
        }

        public bool ToastRarityColoredName
        {
            get => _toastRarityColoredName;
            set => SetValue(ref _toastRarityColoredName, value);
        }

        public bool ToastShowRarityPercent
        {
            get => _toastShowRarityPercent;
            set => SetValue(ref _toastShowRarityPercent, value);
        }

        public bool ToastShowDescription
        {
            get => _toastShowDescription;
            set => SetValue(ref _toastShowDescription, value);
        }

        public bool ToastShowCategory
        {
            get => _toastShowCategory;
            set => SetValue(ref _toastShowCategory, value);
        }

        public bool ToastShowGameName
        {
            get => _toastShowGameName;
            set => SetValue(ref _toastShowGameName, value);
        }

        /// <summary>
        /// Shows the unlock datetime on the toast's header line. Off by default; the frame has
        /// its own always-on datetime row.
        /// </summary>
        public bool ToastShowUnlockTime
        {
            get => _toastShowUnlockTime;
            set => SetValue(ref _toastShowUnlockTime, value);
        }

        public int ToastDurationSeconds
        {
            get => _toastDurationSeconds;
            set => SetValue(ref _toastDurationSeconds, Math.Max(2, value));
        }

        public int MaxConcurrentToasts
        {
            get => _maxConcurrentToasts;
            set => SetValue(ref _maxConcurrentToasts, Math.Max(1, value));
        }

        public ToastScreenCorner ToastPosition
        {
            get => _toastPosition;
            set => SetValue(ref _toastPosition, value);
        }

        /// <summary>
        /// When true, a screenshot of the game's monitor is saved for each of your own unlock
        /// waves. Independent of unlock toasts (the with-toast variant is skipped when no toast
        /// shows). Opt-in since it writes files to disk.
        /// </summary>
        public bool EnableUnlockScreenshots
        {
            get => _enableUnlockScreenshots;
            set => SetValue(ref _enableUnlockScreenshots, value);
        }

        /// <summary>
        /// Save a screenshot captured before the toast window is shown (game only, no overlay).
        /// </summary>
        public bool UnlockScreenshotClean
        {
            get => _unlockScreenshotClean;
            set => SetValue(ref _unlockScreenshotClean, value);
        }

        /// <summary>
        /// Save a screenshot captured after the toast slides in (toast visible in frame).
        /// </summary>
        public bool UnlockScreenshotWithToast
        {
            get => _unlockScreenshotWithToast;
            set => SetValue(ref _unlockScreenshotWithToast, value);
        }

        /// <summary>
        /// Save a copy of the clean screenshot with the theme frame composited onto the image.
        /// The frame is never shown on screen.
        /// </summary>
        public bool UnlockScreenshotFramed
        {
            get => _unlockScreenshotFramed;
            set => SetValue(ref _unlockScreenshotFramed, value);
        }

        // Frame appearance toggles: which fields the screenshot frame renders. Independent of
        // the ToastShow* toggles so the saved image can differ from the on-screen toast.
        public bool FrameShowHeader
        {
            get => _frameShowHeader;
            set => SetValue(ref _frameShowHeader, value);
        }

        public bool FrameShowName
        {
            get => _frameShowName;
            set => SetValue(ref _frameShowName, value);
        }

        public bool FrameShowDescription
        {
            get => _frameShowDescription;
            set => SetValue(ref _frameShowDescription, value);
        }

        public bool FrameShowCategory
        {
            get => _frameShowCategory;
            set => SetValue(ref _frameShowCategory, value);
        }

        public bool FrameShowGameName
        {
            get => _frameShowGameName;
            set => SetValue(ref _frameShowGameName, value);
        }

        public bool FrameShowRarityBadge
        {
            get => _frameShowRarityBadge;
            set => SetValue(ref _frameShowRarityBadge, value);
        }

        public bool FrameShowRarityPercent
        {
            get => _frameShowRarityPercent;
            set => SetValue(ref _frameShowRarityPercent, value);
        }

        public bool FrameShowRarityGlow
        {
            get => _frameShowRarityGlow;
            set => SetValue(ref _frameShowRarityGlow, value);
        }

        public bool FrameRarityColoredName
        {
            get => _frameRarityColoredName;
            set => SetValue(ref _frameRarityColoredName, value);
        }

        /// <summary>
        /// Shows the localized unlock datetime on the frame's header line.
        /// </summary>
        public bool FrameShowUnlockTime
        {
            get => _frameShowUnlockTime;
            set => SetValue(ref _frameShowUnlockTime, value);
        }

        /// <summary>
        /// Base directory for unlock screenshots. Files are written to
        /// &lt;dir&gt;\Game\NNN_AchievementName_&lt;variant&gt;.png.
        /// </summary>
        public string UnlockScreenshotDirectory
        {
            get => _unlockScreenshotDirectory;
            set => SetValue(ref _unlockScreenshotDirectory, value);
        }

        /// <summary>
        /// When true, a video clip of the game's monitor is saved for each of your own unlocks
        /// while a game is running, via a rolling ffmpeg screen capture. Requires a valid
        /// <see cref="FfmpegPath"/>; the plugin never downloads ffmpeg.
        /// </summary>
        public bool EnableUnlockRecordings
        {
            get => _enableUnlockRecordings;
            set => SetValue(ref _enableUnlockRecordings, value);
        }

        /// <summary>
        /// Full path to a user-supplied ffmpeg.exe used for unlock recordings.
        /// </summary>
        public string FfmpegPath
        {
            get => _ffmpegPath;
            set => SetValue(ref _ffmpegPath, value);
        }

        /// <summary>
        /// Base directory for unlock recordings. Blank falls back to
        /// <see cref="UnlockScreenshotDirectory"/> at runtime. Files are written to
        /// &lt;dir&gt;\Game\NNN_AchievementName.mp4.
        /// </summary>
        public string UnlockRecordingDirectory
        {
            get => _unlockRecordingDirectory;
            set => SetValue(ref _unlockRecordingDirectory, value);
        }

        /// <summary>
        /// Seconds recorded before the unlock moment (pre-roll). The clip end is anchored past
        /// the toast's dismissal, so total length = pre-roll + detection gap + toast time.
        /// </summary>
        public int RecordingClipSeconds
        {
            get => _recordingClipSeconds;
            set => SetValue(ref _recordingClipSeconds, Math.Min(60, Math.Max(5, value)));
        }

        public int RecordingFps
        {
            get => _recordingFps;
            set => SetValue(ref _recordingFps, Math.Min(60, Math.Max(10, value)));
        }

        public RecordingResolution RecordingResolution
        {
            get => _recordingResolution;
            set => SetValue(ref _recordingResolution, value);
        }

        public RecordingEncoder RecordingEncoder
        {
            get => _recordingEncoder;
            set => SetValue(ref _recordingEncoder, value);
        }

        public RecordingCaptureBackend RecordingCaptureBackend
        {
            get => _recordingCaptureBackend;
            set => SetValue(ref _recordingCaptureBackend, value);
        }

        /// <summary>
        /// When true, unlock clips include system audio (everything the PC is playing) captured
        /// alongside the rolling screen capture. Off by default; audio capture is best-effort
        /// and never blocks the video pipeline.
        /// </summary>
        public bool RecordingIncludeAudio
        {
            get => _recordingIncludeAudio;
            set => SetValue(ref _recordingIncludeAudio, value);
        }

        /// <summary>
        /// Per-provider notification overrides keyed by provider key. Only deviating providers
        /// are stored; absent providers inherit the global notification defaults, so new
        /// providers pick up the globals automatically.
        /// </summary>
        public Dictionary<string, ProviderNotificationOverride> ProviderNotificationOverrides
        {
            get => _providerNotificationOverrides ??
                   (_providerNotificationOverrides =
                       new Dictionary<string, ProviderNotificationOverride>(StringComparer.OrdinalIgnoreCase));
            set => SetValue(ref _providerNotificationOverrides, NormalizeProviderNotificationOverrides(value));
        }

        /// <summary>
        /// The stored override for a provider, or null when the provider has no deviation and
        /// inherits the global notification defaults.
        /// </summary>
        public ProviderNotificationOverride GetProviderNotificationOverride(string providerKey)
        {
            providerKey = NormalizeProviderKeyToken(providerKey);
            return providerKey != null &&
                   ProviderNotificationOverrides.TryGetValue(providerKey, out var value)
                ? value
                : null;
        }

        /// <summary>
        /// Stores a clone of the override for a provider, removing the entry when the override
        /// is null or all-inherit. Reassigns the dictionary so PropertyChanged is raised.
        /// </summary>
        public void SetProviderNotificationOverride(string providerKey, ProviderNotificationOverride value)
        {
            providerKey = NormalizeProviderKeyToken(providerKey);
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            var overrides = new Dictionary<string, ProviderNotificationOverride>(
                ProviderNotificationOverrides,
                StringComparer.OrdinalIgnoreCase);
            if (value == null || value.IsAllInherit)
            {
                if (!overrides.Remove(providerKey))
                {
                    return;
                }
            }
            else
            {
                overrides[providerKey] = value.Clone();
            }

            ProviderNotificationOverrides = overrides;
        }

        #endregion

        #region Display Preferences

        /// <summary>
        /// When true, hidden achievement icons are shown before reveal.
        /// </summary>
        public bool ShowHiddenIcon
        {
            get => _showHiddenIcon;
            set => SetValue(ref _showHiddenIcon, value);
        }

        /// <summary>
        /// When true, hidden achievement titles are shown before reveal.
        /// </summary>
        public bool ShowHiddenTitle
        {
            get => _showHiddenTitle;
            set => SetValue(ref _showHiddenTitle, value);
        }

        /// <summary>
        /// When true, hidden achievement descriptions are shown before reveal.
        /// </summary>
        public bool ShowHiddenDescription
        {
            get => _showHiddenDescription;
            set => SetValue(ref _showHiddenDescription, value);
        }

        /// <summary>
        /// When true, hidden achievements show "(Hidden Achievement)" suffix after their title.
        /// </summary>
        public bool ShowHiddenSuffix
        {
            get => _showHiddenSuffix;
            set => SetValue(ref _showHiddenSuffix, value);
        }

        /// <summary>
        /// When true, locked achievement icons are shown.
        /// This uses a provider-supplied locked icon when available, otherwise a grayscaled unlocked fallback.
        /// When false, locked achievement icons are hidden with a placeholder until revealed.
        /// </summary>
        public bool ShowLockedIcon
        {
            get => _showLockedIcon;
            set => SetValue(ref _showLockedIcon, value);
        }

        /// <summary>
        /// When true, providers with distinct locked icons will cache and use them instead of grayscaling the unlocked icon.
        /// Changes apply on the next refresh for newly cached icons.
        /// </summary>
        public bool UseSeparateLockedIconsWhenAvailable
        {
            get => _useSeparateLockedIconsWhenAvailable;
            set => SetValue(ref _useSeparateLockedIconsWhenAvailable, value);
        }

        /// <summary>
        /// Game IDs that always use separate locked icons when available, regardless of the global default.
        /// When absent, the game falls back to the global UseSeparateLockedIconsWhenAvailable setting.
        /// </summary>
        [JsonIgnore]
        [DontSerialize]
        public HashSet<Guid> SeparateLockedIconEnabledGameIds
        {
            get => _separateLockedIconEnabledGameIds;
            set => SetValue(ref _separateLockedIconEnabledGameIds, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Resolves whether a game should use separate locked icons after applying the per-game override.
        /// </summary>
        public bool ShouldUseSeparateLockedIcons(Guid? playniteGameId)
        {
            if (UseSeparateLockedIconsWhenAvailable)
            {
                return true;
            }

            return playniteGameId.HasValue &&
                   playniteGameId.Value != Guid.Empty &&
                   SeparateLockedIconEnabledGameIds?.Contains(playniteGameId.Value) == true;
        }

        /// <summary>
        /// When true, the modern compact list (and the legacy SuccessStory-compatible lists, which
        /// follow it) shows rarity glow on unlocked icons.
        /// </summary>
        public bool ModernCompactListShowRarityGlow
        {
            get => _modernCompactListShowRarityGlow;
            set => SetValue(ref _modernCompactListShowRarityGlow, value);
        }

        /// <summary>
        /// When true, the modern unlocked list shows rarity glow on unlocked icons.
        /// </summary>
        public bool ModernUnlockedListShowRarityGlow
        {
            get => _modernUnlockedListShowRarityGlow;
            set => SetValue(ref _modernUnlockedListShowRarityGlow, value);
        }

        /// <summary>
        /// When true, all rarity badges use the hexagon shape while keeping rarity colors.
        /// </summary>
        public bool UseUniformRarityBadges
        {
            get => _useUniformRarityBadges;
            set => SetValue(ref _useUniformRarityBadges, value);
        }

        /// <summary>
        /// When true, rarity badges use the trophy geometry instead of the tier shapes while keeping rarity colors.
        /// </summary>
        public bool UseTrophiesForRarity
        {
            get => _useTrophiesForRarity;
            set => SetValue(ref _useTrophiesForRarity, value);
        }

        /// <summary>
        /// When true, rarity percentages display rounded to the nearest whole percent and values
        /// under 1% display as "&lt;1%". Display-only; stored percent values are unaffected.
        /// </summary>
        public bool RoundRarityPercentages
        {
            get => _roundRarityPercentages;
            set => SetValue(ref _roundRarityPercentages, value);
        }

        /// <summary>
        /// User-selected base colors for rarity, completed game, and trophy badges.
        /// </summary>
        public RarityColorSettings RarityColors
        {
            get => _rarityColors;
            set => SetValue(ref _rarityColors, value?.Clone() ?? RarityColorSettings.CreateDefault());
        }

        public bool IncludeUnplayedGames
        {
            get => _includeUnplayedGames;
            set => SetValue(ref _includeUnplayedGames, value);
        }

        /// <summary>
        /// When true, shows the collection score card in the overview header.
        /// </summary>
        public bool ShowOverviewCollectionScoreCard
        {
            get => _showOverviewCollectionScoreCard;
            set => SetValue(ref _showOverviewCollectionScoreCard, value);
        }

        /// <summary>
        /// When true, shows the prestige score card in the overview header.
        /// </summary>
        public bool ShowOverviewPrestigeScoreCard
        {
            get => _showOverviewPrestigeScoreCard;
            set => SetValue(ref _showOverviewPrestigeScoreCard, value);
        }

        /// <summary>
        /// Legacy aggregate toggle for overview pie charts.
        /// New builds use per-chart visibility settings, but this is preserved for migration.
        /// </summary>
        public bool ShowOverviewPieCharts
        {
            get => _showOverviewPieCharts;
            set
            {
                if (_showOverviewPieCharts == value)
                {
                    return;
                }

                SetValue(ref _showOverviewPieCharts, value);
                if (_overviewPieChartVisibilityInitializedFromIndividualSettings)
                {
                    return;
                }

                ShowOverviewGamesPieChart = value;
                ShowOverviewProviderPieChart = value;
                ShowOverviewRarityPieChart = value;
                ShowOverviewTrophyPieChart = value;
            }
        }

        /// <summary>
        /// When true, shows the completed-games pie chart in the overview.
        /// </summary>
        public bool ShowOverviewGamesPieChart
        {
            get => _showOverviewGamesPieChart;
            set
            {
                _overviewPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showOverviewGamesPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the platform/provider pie chart in the overview.
        /// </summary>
        public bool ShowOverviewProviderPieChart
        {
            get => _showOverviewProviderPieChart;
            set
            {
                _overviewPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showOverviewProviderPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the rarity pie chart in the overview.
        /// </summary>
        public bool ShowOverviewRarityPieChart
        {
            get => _showOverviewRarityPieChart;
            set
            {
                _overviewPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showOverviewRarityPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the trophy pie chart in the overview.
        /// </summary>
        public bool ShowOverviewTrophyPieChart
        {
            get => _showOverviewTrophyPieChart;
            set
            {
                _overviewPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showOverviewTrophyPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the center percentage text on overview pie charts.
        /// </summary>
        public bool ShowOverviewPiePercentages
        {
            get => _showOverviewPiePercentages;
            set => SetValue(ref _showOverviewPiePercentages, value);
        }

        /// <summary>
        /// Determines how overview pie charts handle slices below five percent.
        /// </summary>
        public OverviewPieSmallSliceMode OverviewPieSmallSliceMode
        {
            get => _overviewPieSmallSliceMode;
            set => SetValue(ref _overviewPieSmallSliceMode, value);
        }

        /// <summary>
        /// When true, shows the timeline bar chart at the bottom of the right overview.
        /// When false, the achievements list takes the full space.
        /// </summary>
        public bool ShowOverviewBarCharts
        {
            get => _showOverviewBarCharts;
            set => SetValue(ref _showOverviewBarCharts, value);
        }

        /// <summary>
        /// When true, shows the top menu bar button for opening the achievements window.
        /// </summary>
        public bool ShowTopMenuBarButton
        {
            get => _showTopMenuBarButton;
            set => SetValue(ref _showTopMenuBarButton, value);
        }

        /// <summary>
        /// When true, progress bars of fully completed games use the completed game colors
        /// instead of the normal fill.
        /// </summary>
        public bool ShowCompletedProgressColoring
        {
            get => _showCompletedProgressColoring;
            set => SetValue(ref _showCompletedProgressColoring, value);
        }

        /// <summary>
        /// When false, friend achievement rows for achievements the current user has not unlocked
        /// are obscured using the achievement visibility settings, as if locked for the user.
        /// Applies to all friend surfaces (overview, friends achievements window, recent unlocks, themes).
        /// </summary>
        public bool ShowFriendSpoilers
        {
            get => _showFriendSpoilers;
            set => SetValue(ref _showFriendSpoilers, value);
        }

        /// <summary>
        /// Maximum number of recent friend unlocks shown in Friends Overview.
        /// </summary>
        public int FriendsOverviewRecentUnlockLimit
        {
            get => _friendsOverviewRecentUnlockLimit;
            set => SetValue(ref _friendsOverviewRecentUnlockLimit, Math.Max(1, value));
        }

        /// <summary>
        /// When true, shows the rarity bar at the bottom of compact list achievement items.
        /// </summary>
        public bool ShowCompactListRarityBar
        {
            get => _showCompactListRarityBar;
            set => SetValue(ref _showCompactListRarityBar, value);
        }

        /// <summary>
        /// One-time bookkeeping flag: true once the Progress summary column has been seeded to Right
        /// alignment. Migration forces Right for existing configs where this is absent/false (so an
        /// updating user keeps the legacy footer layout) and then sets it true, after which a user's
        /// own alignment choice is respected. Defaults to true for fresh installs (seeded in the ctor).
        /// </summary>
        public bool ProgressColumnAlignmentDefaulted
        {
            get => _progressColumnAlignmentDefaulted;
            set => SetValue(ref _progressColumnAlignmentDefaulted, value);
        }

        /// <summary>
        /// One-time bookkeeping flag: true once the transparent inline-surface overrides
        /// (GridSurface, ControlSurface) have been seeded into the config. Migration seeds them for
        /// existing configs where this is absent/false (so updating users gain the transparent
        /// default) and then sets it true, after which a user's own later choice -- including
        /// switching a surface back to Follow Playnite (which removes the entry) -- is respected and
        /// never re-seeded. Defaults to true for fresh installs (which seed the overrides in the
        /// plugin-reference constructor).
        /// </summary>
        public bool InlineSurfaceTransparencySeeded
        {
            get => _inlineSurfaceTransparencySeeded;
            set => SetValue(ref _inlineSurfaceTransparencySeeded, value);
        }

        /// <summary>
        /// Horizontal alignment for text shown in DataGrid column headers.
        /// </summary>
        public GridAlignment GridColumnHeaderAlignment
        {
            get => _gridColumnHeaderAlignment;
            set => SetValue(ref _gridColumnHeaderAlignment, value);
        }

        /// <summary>
        /// Horizontal alignment for textual DataGrid cell content.
        /// </summary>
        public GridAlignment GridCellAlignment
        {
            get => _gridCellAlignment;
            set => SetValue(ref _gridCellAlignment, value);
        }

        /// <summary>
        /// Vertical alignment for DataGrid cell content.
        /// </summary>
        public GridVerticalAlignment GridCellVerticalAlignment
        {
            get => _gridCellVerticalAlignment;
            set => SetValue(ref _gridCellVerticalAlignment, value);
        }

        /// <summary>
        /// Date display mode for achievement unlock dates and friend last-unlock dates.
        /// </summary>
        public DateDisplayMode UnlockDateDisplayMode
        {
            get => _unlockDateDisplayMode;
            set => SetValue(ref _unlockDateDisplayMode, value);
        }

        /// <summary>
        /// Display mode for playtime text shown in summaries and metadata.
        /// </summary>
        public PlaytimeDisplayMode PlaytimeDisplayMode
        {
            get => _playtimeDisplayMode;
            set => SetValue(ref _playtimeDisplayMode, value);
        }

        /// <summary>
        /// When true, enables the modern compact list control.
        /// </summary>
        public bool EnableAchievementCompactListControl
        {
            get => _enableAchievementCompactListControl;
            set => SetValue(ref _enableAchievementCompactListControl, value);
        }

        /// <summary>
        /// When true, enables the modern achievement datagrid control.
        /// </summary>
        public bool EnableAchievementDataGridControl
        {
            get => _enableAchievementDataGridControl;
            set => SetValue(ref _enableAchievementDataGridControl, value);
        }

        /// <summary>
        /// When true, enables the modern compact unlocked list control.
        /// </summary>
        public bool EnableAchievementCompactUnlockedListControl
        {
            get => _enableAchievementCompactUnlockedListControl;
            set => SetValue(ref _enableAchievementCompactUnlockedListControl, value);
        }

        /// <summary>
        /// When true, enables the modern compact locked list control.
        /// </summary>
        public bool EnableAchievementCompactLockedListControl
        {
            get => _enableAchievementCompactLockedListControl;
            set => SetValue(ref _enableAchievementCompactLockedListControl, value);
        }

        /// <summary>
        /// When true, enables the modern progress bar control.
        /// </summary>
        public bool EnableAchievementProgressBarControl
        {
            get => _enableAchievementProgressBarControl;
            set => SetValue(ref _enableAchievementProgressBarControl, value);
        }

        /// <summary>
        /// When true, enables the modern stats control.
        /// </summary>
        public bool EnableAchievementStatsControl
        {
            get => _enableAchievementStatsControl;
            set => SetValue(ref _enableAchievementStatsControl, value);
        }

        /// <summary>
        /// When true, enables the modern button control.
        /// </summary>
        public bool EnableAchievementButtonControl
        {
            get => _enableAchievementButtonControl;
            set => SetValue(ref _enableAchievementButtonControl, value);
        }

        /// <summary>
        /// When true, enables the modern view item control.
        /// </summary>
        public bool EnableAchievementViewItemControl
        {
            get => _enableAchievementViewItemControl;
            set => SetValue(ref _enableAchievementViewItemControl, value);
        }

        /// <summary>
        /// When true, enables the modern pie chart control.
        /// </summary>
        public bool EnableAchievementPieChartControl
        {
            get => _enableAchievementPieChartControl;
            set => SetValue(ref _enableAchievementPieChartControl, value);
        }

        /// <summary>
        /// When true, enables the modern bar chart control.
        /// </summary>
        public bool EnableAchievementBarChartControl
        {
            get => _enableAchievementBarChartControl;
            set => SetValue(ref _enableAchievementBarChartControl, value);
        }

        /// <summary>
        /// Sort mode for the compact list (all achievements) control.
        /// None preserves provider order.
        /// </summary>
        public CompactListSortMode CompactListSortMode
        {
            get => _compactListSortMode;
            set => SetValue(ref _compactListSortMode, value);
        }

        /// <summary>
        /// When true, reverses the sort direction for the compact list (all achievements) control.
        /// </summary>
        public bool CompactListSortDescending
        {
            get => _compactListSortDescending;
            set => SetValue(ref _compactListSortDescending, value);
        }

        /// <summary>
        /// Sort mode for the compact unlocked list control.
        /// None preserves newest-first ordering.
        /// </summary>
        public CompactListSortMode CompactUnlockedListSortMode
        {
            get => _compactUnlockedListSortMode;
            set => SetValue(ref _compactUnlockedListSortMode, value);
        }

        /// <summary>
        /// When true, reverses the sort direction for the compact unlocked list control.
        /// </summary>
        public bool CompactUnlockedListSortDescending
        {
            get => _compactUnlockedListSortDescending;
            set => SetValue(ref _compactUnlockedListSortDescending, value);
        }

        /// <summary>
        /// Sort mode for the compact locked list control.
        /// None preserves provider order.
        /// </summary>
        public CompactListSortMode CompactLockedListSortMode
        {
            get => _compactLockedListSortMode;
            set => SetValue(ref _compactLockedListSortMode, value);
        }

        /// <summary>
        /// When true, reverses the sort direction for the compact locked list control.
        /// </summary>
        public bool CompactLockedListSortDescending
        {
            get => _compactLockedListSortDescending;
            set => SetValue(ref _compactLockedListSortDescending, value);
        }

        public StartPagePieWidgetSettings StartPagePieCharts
        {
            get => _startPagePieCharts ?? (_startPagePieCharts = AttachStartPageSettings(
                new StartPagePieWidgetSettings()));
            set => SetStartPagePieSettings(ref _startPagePieCharts, value, nameof(StartPagePieCharts));
        }

        public GridOptionsCatalog GridOptions
        {
            get => AttachGridOptionsBridge(_gridOptions ?? (_gridOptions = new GridOptionsCatalog()));
            set
            {
                if (SetValueAndReturn(ref _gridOptions, value?.Clone() ?? new GridOptionsCatalog()))
                {
                    AttachGridOptionsBridge(_gridOptions);
                    RebindStartPageGridSettings();
                }
            }
        }

        public GameActivityScope StartPageActivityScope
        {
            get => NormalizeStartPageActivityScope(_startPageActivityScope);
            set => SetValue(ref _startPageActivityScope, NormalizeStartPageActivityScope(value));
        }

        public GameProgressScope StartPageProgressScope
        {
            get => NormalizeStartPageProgressScope(_startPageProgressScope);
            set => SetValue(ref _startPageProgressScope, NormalizeStartPageProgressScope(value));
        }

        /// <summary>
        /// When true, providers execute concurrently during refresh runs.
        /// Disable to force deterministic sequential provider execution.
        /// </summary>
        public bool EnableParallelProviderRefresh
        {
            get => _enableParallelProviderRefresh;
            set => SetValue(ref _enableParallelProviderRefresh, value);
        }

        /// <summary>
        /// Base delay in milliseconds for retry/backoff after transient errors.
        /// Default is 200ms. Higher values are safer for strict APIs but slower after failures.
        /// Set to 0 for fastest retry behavior.
        /// </summary>
        public int ScanDelayMs
        {
            get => _scanDelayMs;
            set => SetValue(ref _scanDelayMs, Math.Max(0, value));
        }

        /// <summary>
        /// Maximum retry attempts when encountering rate limit or transient errors.
        /// Default is 3. Each retry uses exponential backoff with jitter.
        /// </summary>
        public int MaxRetryAttempts
        {
            get => _maxRetryAttempts;
            set => SetValue(ref _maxRetryAttempts, Math.Max(0, Math.Min(value, 10)));
        }

        #endregion

        #region Theme Integration Settings

        /// <summary>
        /// Optional overrides for plugin semantic resources such as PlayAch.Brush.Text.
        /// Missing entries follow the current Playnite theme resource mapped by the resolver.
        /// </summary>
        public Dictionary<string, ResourceOverrideSetting> ResourceOverrides
        {
            get => _resourceOverrides;
            set => SetValue(ref _resourceOverrides, NormalizeResourceOverrides(value));
        }

        #endregion

        #region UI Column Settings

        /// <summary>
        /// Persisted overview splitter position. Represents left column width
        /// as a ratio of the combined left and right overview columns.
        /// </summary>
        public double OverviewLeftColumnRatio
        {
            get => _overviewLeftColumnRatio;
            set
            {
                var normalized = double.IsNaN(value) || double.IsInfinity(value)
                    ? DefaultOverviewLeftColumnRatio
                    : Math.Max(MinOverviewLeftColumnRatio, Math.Min(MaxOverviewLeftColumnRatio, value));
                SetValue(ref _overviewLeftColumnRatio, normalized);
            }
        }

        public double FriendsOverviewFriendColumnRatio
        {
            get => _friendsOverviewFriendColumnRatio;
            set => SetValue(
                ref _friendsOverviewFriendColumnRatio,
                NormalizeFriendsOverviewColumnRatio(value, DefaultFriendsOverviewFriendColumnRatio));
        }

        public double FriendsOverviewGameColumnRatio
        {
            get => _friendsOverviewGameColumnRatio;
            set => SetValue(
                ref _friendsOverviewGameColumnRatio,
                NormalizeFriendsOverviewColumnRatio(value, DefaultFriendsOverviewGameColumnRatio));
        }

        /// <summary>
        /// Saved bounds for plugin-owned windows keyed by stable window name.
        /// </summary>
        public Dictionary<string, WindowPlacementState> WindowPlacements
        {
            get => _windowPlacements;
            set
            {
                var normalized = new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        var key = (pair.Key ?? string.Empty).Trim();
                        var placement = pair.Value;
                        if (!string.IsNullOrWhiteSpace(key) && placement?.IsValid() == true)
                        {
                            normalized[key] = placement.Clone();
                        }
                    }
                }

                SetValue(ref _windowPlacements, normalized);
            }
        }

        /// <summary>
        /// Last selected range for the overview achievements-over-time chart.
        /// </summary>
        public TimelineRange OverviewTimelineRange
        {
            get => _overviewTimelineRange;
            set => SetValue(ref _overviewTimelineRange, value);
        }

        /// <summary>
        /// Last selected range for the single-game achievements window timeline chart.
        /// </summary>
        public TimelineRange ViewAchievementsTimelineRange
        {
            get => _viewAchievementsTimelineRange;
            set => SetValue(ref _viewAchievementsTimelineRange, value);
        }

        /// <summary>
        /// Whether the single-game achievements window timeline chart is expanded.
        /// </summary>
        public bool ViewAchievementsTimelineVisible
        {
            get => _viewAchievementsTimelineVisible;
            set => SetValue(ref _viewAchievementsTimelineVisible, value);
        }

        #endregion

        #region General Settings

        /// <summary>
        /// Indicates whether the user has completed the first-time setup flow.
        /// When false, the overview shows a landing page guiding users through initial configuration.
        /// </summary>
        public bool FirstTimeSetupCompleted
        {
            get => _firstTimeSetupCompleted;
            set => SetValue(ref _firstTimeSetupCompleted, value);
        }

        /// <summary>
        /// Indicates whether the user has seen the theme migration landing page.
        /// When false, the overview always shows the landing page to promote theme migration.
        /// </summary>
        public bool SeenThemeMigration
        {
            get => _seenThemeMigration;
            set => SetValue(ref _seenThemeMigration, value);
        }

        /// <summary>
        /// Cache mapping ThemePath -> last migrated theme.yaml Version.
        /// Used to detect themes that have been upgraded since migration and may need re-migration.
        /// </summary>
        public Dictionary<string, ThemeMigrationCacheEntry> ThemeMigrationVersionCache
        {
            get => _themeMigrationVersionCache;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, ThemeMigrationCacheEntry>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, ThemeMigrationCacheEntry>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _themeMigrationVersionCache, normalized);
            }
        }

        #endregion

        #region User Preferences (Survive Cache Clear)

        /// <summary>
        /// Game IDs that the user has explicitly excluded from achievement tracking.
        /// These exclusions persist across cache clears.
        /// </summary>
        [JsonIgnore]
        [DontSerialize]
        public HashSet<Guid> ExcludedGameIds
        {
            get => _excludedGameIds;
            set => SetValue(ref _excludedGameIds, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Game IDs that are excluded from all summary surfaces.
        /// These exclusions persist across cache clears.
        /// </summary>
        [JsonIgnore]
        [DontSerialize]
        public HashSet<Guid> ExcludedFromSummariesGameIds
        {
            get => _excludedFromSummariesGameIds;
            set => SetValue(ref _excludedFromSummariesGameIds, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Manual capstone selections. Key = Playnite Game ID, Value = Achievement ApiName.
        /// These selections persist across cache clears.
        /// </summary>
        [JsonIgnore]
        [DontSerialize]
        public Dictionary<Guid, string> ManualCapstones
        {
            get => _manualCapstones;
            set => SetValue(ref _manualCapstones, value ?? new Dictionary<Guid, string>());
        }

        /// <summary>
        /// Manual achievement order per game.
        /// Key = Playnite Game ID, Value = full ordered list of achievement ApiName values.
        /// These overrides persist across cache clears.
        /// </summary>
        [JsonIgnore]
        [DontSerialize]
        public Dictionary<Guid, List<string>> AchievementOrderOverrides
        {
            get => _achievementOrderOverrides;
            set => SetValue(ref _achievementOrderOverrides, NormalizeAchievementOrderOverrides(value));
        }

        /// <summary>
        /// Manual achievement category overrides per game.
        /// Key = Playnite Game ID, Value = map of Achievement ApiName -> Category.
        /// These overrides persist across cache clears.
        /// </summary>
        [JsonIgnore]
        [DontSerialize]
        public Dictionary<Guid, Dictionary<string, string>> AchievementCategoryOverrides
        {
            get => _achievementCategoryOverrides;
            set => SetValue(ref _achievementCategoryOverrides, NormalizeAchievementCategoryOverrides(value));
        }

        /// <summary>
        /// Manual achievement category type overrides per game.
        /// Key = Playnite Game ID, Value = map of Achievement ApiName -> CategoryType.
        /// Allowed values: Base, DLC, Singleplayer, Multiplayer.
        /// These overrides persist across cache clears.
        /// </summary>
        [JsonIgnore]
        [DontSerialize]
        public Dictionary<Guid, Dictionary<string, string>> AchievementCategoryTypeOverrides
        {
            get => _achievementCategoryTypeOverrides;
            set => SetValue(ref _achievementCategoryTypeOverrides, NormalizeAchievementCategoryTypeOverrides(value));
        }

        #endregion

        #region Tagging Settings

        /// <summary>
        /// Settings for Playnite tag integration, allowing games to be tagged
        /// based on their achievement status for filtering and organization.
        /// </summary>
        public TaggingSettings TaggingSettings
        {
            get => _taggingSettings;
            set => SetValue(ref _taggingSettings, value ?? new TaggingSettings());
        }

        #endregion

        #region StartPage Settings Helpers

        private void SetStartPagePieSettings(
            ref StartPagePieWidgetSettings field,
            StartPagePieWidgetSettings value,
            string propertyName)
        {
            var normalized = value ?? new StartPagePieWidgetSettings();
            if (ReferenceEquals(field, normalized))
            {
                return;
            }

            DetachStartPageSettings(field);
            field = AttachStartPageSettings(normalized);
            OnPropertyChanged(propertyName);
        }

        private void AttachStartPageSettingsHandlers()
        {
            _startPageGameSummariesGrid = AttachStartPageSettings(
                _startPageGameSummariesGrid ?? new StartPageGameSummariesGridSettings(GameSummariesStartPage));
            _startPageRecentUnlocksGrid = AttachStartPageSettings(
                _startPageRecentUnlocksGrid ?? new StartPageRecentUnlocksGridSettings(AchievementStartPageRecent));
            _startPageFriendsRecentUnlocksGrid = AttachStartPageSettings(
                _startPageFriendsRecentUnlocksGrid ?? new StartPageFriendsRecentUnlocksGridSettings(AchievementStartPageFriendRecent));
            _startPagePieCharts = AttachStartPageSettings(
                _startPagePieCharts ?? new StartPagePieWidgetSettings());
        }

        private void RebindStartPageGridSettings()
        {
            _startPageGameSummariesGrid?.SetOptions(GameSummariesStartPage);
            _startPageRecentUnlocksGrid?.SetOptions(AchievementStartPageRecent);
            _startPageFriendsRecentUnlocksGrid?.SetOptions(AchievementStartPageFriendRecent);
        }

        [System.Runtime.Serialization.OnDeserialized]
        private void OnDeserialized(System.Runtime.Serialization.StreamingContext context)
        {
            AttachStartPageSettingsHandlers();
            RebindStartPageGridSettings();
        }

        private T AttachStartPageSettings<T>(T settings)
            where T : ObservableObject
        {
            if (settings != null)
            {
                settings.PropertyChanged -= StartPageSettings_PropertyChanged;
                settings.PropertyChanged += StartPageSettings_PropertyChanged;
            }

            return settings;
        }

        private void DetachStartPageSettings(ObservableObject settings)
        {
            if (settings != null)
            {
                settings.PropertyChanged -= StartPageSettings_PropertyChanged;
            }
        }

        private void StartPageSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var childPropertyName = e?.PropertyName;
            if (ReferenceEquals(sender, _startPageGameSummariesGrid))
            {
                RaiseStartPageSettingsChanged(nameof(StartPageGameSummariesGrid), childPropertyName);
                RaiseLegacyStartPageGridPropertyChanged(
                    childPropertyName,
                    nameof(StartPageGameSummariesGridRowHeight),
                    nameof(StartPageGameSummariesGridMaxRows));
                return;
            }

            if (ReferenceEquals(sender, _startPageRecentUnlocksGrid))
            {
                RaiseStartPageSettingsChanged(nameof(StartPageRecentUnlocksGrid), childPropertyName);
                RaiseLegacyStartPageGridPropertyChanged(
                    childPropertyName,
                    nameof(StartPageRecentAchievementsGridRowHeight),
                    nameof(StartPageRecentAchievementsGridMaxRows));
                return;
            }

            if (ReferenceEquals(sender, _startPageFriendsRecentUnlocksGrid))
            {
                RaiseStartPageSettingsChanged(nameof(StartPageFriendsRecentUnlocksGrid), childPropertyName);
                RaiseLegacyStartPageGridPropertyChanged(
                    childPropertyName,
                    nameof(StartPageFriendsRecentAchievementsGridRowHeight),
                    nameof(StartPageFriendsRecentAchievementsGridMaxRows));
                return;
            }

            if (ReferenceEquals(sender, _startPagePieCharts))
            {
                RaiseStartPageSettingsChanged(nameof(StartPagePieCharts), childPropertyName);
            }
        }

        private void RaiseStartPageSettingsChanged(string parentPropertyName, string childPropertyName)
        {
            if (string.IsNullOrWhiteSpace(parentPropertyName))
            {
                return;
            }

            OnPropertyChanged(parentPropertyName);
            if (!string.IsNullOrWhiteSpace(childPropertyName))
            {
                OnPropertyChanged($"{parentPropertyName}.{childPropertyName}");
            }
        }

        private void RaiseLegacyStartPageGridPropertyChanged(
            string childPropertyName,
            string rowHeightPropertyName,
            string maxRowsPropertyName)
        {
            if (string.Equals(childPropertyName, nameof(StartPageGameSummariesGridSettings.RowHeight), StringComparison.Ordinal))
            {
                OnPropertyChanged(rowHeightPropertyName);
            }
            else if (string.Equals(childPropertyName, nameof(StartPageGameSummariesGridSettings.MaxRows), StringComparison.Ordinal))
            {
                OnPropertyChanged(maxRowsPropertyName);
            }
        }

        #endregion

        #region Clone Method

        /// <summary>
        /// Creates a deep copy of this PersistedSettings instance.
        /// Provider-specific settings are cloned via the ProviderSettings dictionary.
        /// </summary>
        public PersistedSettings Clone()
        {
            var clonedProviderSettings = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (this.ProviderSettings != null)
            {
                foreach (var kvp in this.ProviderSettings)
                {
                    clonedProviderSettings[kvp.Key] = kvp.Value?.DeepClone() as JObject;
                }
            }

            return new PersistedSettings
            {
                // Provider Settings Dictionary (contains all provider-specific settings)
                ProviderSettings = clonedProviderSettings,
                EnableFriendsFeatures = this.EnableFriendsFeatures,
                AutoDiscoverFriendProviderKeys = this.AutoDiscoverFriendProviderKeys != null
                    ? new HashSet<string>(this.AutoDiscoverFriendProviderKeys, StringComparer.OrdinalIgnoreCase)
                    : CreateDefaultAutoDiscoverFriendProviderKeys(),
                UseExophaseForSteamFriendOwnership = this.UseExophaseForSteamFriendOwnership,
                IncludeUnownedFriendGames = this.IncludeUnownedFriendGames,
                Friends = new ObservableCollection<FriendSettingsEntry>(
                    (this.Friends ?? new ObservableCollection<FriendSettingsEntry>())
                    .Where(friend => friend != null)
                    .Select(friend => friend.Clone())),
                FriendMergeGroups = new ObservableCollection<FriendMergeGroup>(
                    (this.FriendMergeGroups ?? new ObservableCollection<FriendMergeGroup>())
                    .Where(group => group != null)
                    .Select(group => group.Clone())),

                // Global Settings
                GlobalLanguage = this.GlobalLanguage,

                // Update and Refresh Settings
                EnablePeriodicUpdates = this.EnablePeriodicUpdates,
                IncludeHiddenGamesInBulkScans = this.IncludeHiddenGamesInBulkScans,
                PeriodicUpdateHours = this.PeriodicUpdateHours,
                EnableFriendsPeriodicUpdates = this.EnableFriendsPeriodicUpdates,
                FriendsPeriodicUpdateHours = this.FriendsPeriodicUpdateHours,
                EnableInGamePolling = this.EnableInGamePolling,
                InGamePollIntervalSeconds = this.InGamePollIntervalSeconds,
                InGamePollRefreshFriends = this.InGamePollRefreshFriends,
                InGameFriendRefreshMultiplier = this.InGameFriendRefreshMultiplier,
                InGameFriendBatchSize = this.InGameFriendBatchSize,
                RecentRefreshGamesCount = this.RecentRefreshGamesCount,
                DefaultOverviewRefreshMode = this.DefaultOverviewRefreshMode,
                CustomRefreshPresets = this.CustomRefreshPresets != null
                    ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(this.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                    : new List<CustomRefreshPreset>(),

                // Hotkey Settings
                EnableAchievementHotkeys = this.EnableAchievementHotkeys,
                EnableGlobalAchievementHotkeys = this.EnableGlobalAchievementHotkeys,
                ViewAchievementsHotkey = this.ViewAchievementsHotkey,
                ManageAchievementsHotkey = this.ManageAchievementsHotkey,
                OverviewHotkey = this.OverviewHotkey,
                OpenSettingsHotkey = this.OpenSettingsHotkey,
                CategoryModeHotkey = this.CategoryModeHotkey,

                // Notification Settings
                EnableNotifications = this.EnableNotifications,
                NotifyPeriodicUpdates = this.NotifyPeriodicUpdates,
                NotifyOnRebuild = this.NotifyOnRebuild,
                EnableUnlockToasts = this.EnableUnlockToasts,
                EnableFriendUnlockToasts = this.EnableFriendUnlockToasts,
                ToastShowHeader = this.ToastShowHeader,
                ToastShowName = this.ToastShowName,
                ToastShowRarityBadge = this.ToastShowRarityBadge,
                ToastShowRarityGlow = this.ToastShowRarityGlow,
                ToastRarityColoredName = this.ToastRarityColoredName,
                ToastShowRarityPercent = this.ToastShowRarityPercent,
                ToastShowDescription = this.ToastShowDescription,
                ToastShowCategory = this.ToastShowCategory,
                ToastShowGameName = this.ToastShowGameName,
                ToastShowUnlockTime = this.ToastShowUnlockTime,
                ToastDurationSeconds = this.ToastDurationSeconds,
                MaxConcurrentToasts = this.MaxConcurrentToasts,
                ToastPosition = this.ToastPosition,
                EnableUnlockScreenshots = this.EnableUnlockScreenshots,
                UnlockScreenshotClean = this.UnlockScreenshotClean,
                UnlockScreenshotWithToast = this.UnlockScreenshotWithToast,
                UnlockScreenshotFramed = this.UnlockScreenshotFramed,
                FrameShowHeader = this.FrameShowHeader,
                FrameShowName = this.FrameShowName,
                FrameShowDescription = this.FrameShowDescription,
                FrameShowCategory = this.FrameShowCategory,
                FrameShowGameName = this.FrameShowGameName,
                FrameShowRarityBadge = this.FrameShowRarityBadge,
                FrameShowRarityPercent = this.FrameShowRarityPercent,
                FrameShowRarityGlow = this.FrameShowRarityGlow,
                FrameRarityColoredName = this.FrameRarityColoredName,
                FrameShowUnlockTime = this.FrameShowUnlockTime,
                UnlockScreenshotDirectory = this.UnlockScreenshotDirectory,
                EnableUnlockRecordings = this.EnableUnlockRecordings,
                FfmpegPath = this.FfmpegPath,
                UnlockRecordingDirectory = this.UnlockRecordingDirectory,
                RecordingClipSeconds = this.RecordingClipSeconds,
                RecordingFps = this.RecordingFps,
                RecordingResolution = this.RecordingResolution,
                RecordingEncoder = this.RecordingEncoder,
                RecordingCaptureBackend = this.RecordingCaptureBackend,
                RecordingIncludeAudio = this.RecordingIncludeAudio,
                ProviderNotificationOverrides = this.ProviderNotificationOverrides != null
                    ? this.ProviderNotificationOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value?.Clone(),
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, ProviderNotificationOverride>(StringComparer.OrdinalIgnoreCase),

                // Display Preferences
                ShowHiddenIcon = this.ShowHiddenIcon,
                ShowHiddenTitle = this.ShowHiddenTitle,
                ShowHiddenDescription = this.ShowHiddenDescription,
                ShowHiddenSuffix = this.ShowHiddenSuffix,
                ShowLockedIcon = this.ShowLockedIcon,
                UseSeparateLockedIconsWhenAvailable = this.UseSeparateLockedIconsWhenAvailable,
                ModernCompactListShowRarityGlow = this.ModernCompactListShowRarityGlow,
                ModernUnlockedListShowRarityGlow = this.ModernUnlockedListShowRarityGlow,
                UseUniformRarityBadges = this.UseUniformRarityBadges,
                UseTrophiesForRarity = this.UseTrophiesForRarity,
                RoundRarityPercentages = this.RoundRarityPercentages,
                RarityColors = this.RarityColors?.Clone() ?? RarityColorSettings.CreateDefault(),
                IncludeUnplayedGames = this.IncludeUnplayedGames,
                ShowOverviewCollectionScoreCard = this.ShowOverviewCollectionScoreCard,
                ShowOverviewPrestigeScoreCard = this.ShowOverviewPrestigeScoreCard,
                ShowOverviewPieCharts = this.ShowOverviewPieCharts,
                ShowOverviewGamesPieChart = this.ShowOverviewGamesPieChart,
                ShowOverviewProviderPieChart = this.ShowOverviewProviderPieChart,
                ShowOverviewRarityPieChart = this.ShowOverviewRarityPieChart,
                ShowOverviewTrophyPieChart = this.ShowOverviewTrophyPieChart,
                ShowOverviewPiePercentages = this.ShowOverviewPiePercentages,
                OverviewPieSmallSliceMode = this.OverviewPieSmallSliceMode,
                ShowOverviewBarCharts = this.ShowOverviewBarCharts,
                ShowTopMenuBarButton = this.ShowTopMenuBarButton,
                ShowCompletedProgressColoring = this.ShowCompletedProgressColoring,
                ShowFriendSpoilers = this.ShowFriendSpoilers,
                FriendsOverviewRecentUnlockLimit = this.FriendsOverviewRecentUnlockLimit,
                ShowCompactListRarityBar = this.ShowCompactListRarityBar,
                ProgressColumnAlignmentDefaulted = this.ProgressColumnAlignmentDefaulted,
                InlineSurfaceTransparencySeeded = this.InlineSurfaceTransparencySeeded,
                GridColumnHeaderAlignment = this.GridColumnHeaderAlignment,
                GridCellAlignment = this.GridCellAlignment,
                GridCellVerticalAlignment = this.GridCellVerticalAlignment,
                UnlockDateDisplayMode = this.UnlockDateDisplayMode,
                PlaytimeDisplayMode = this.PlaytimeDisplayMode,
                EnableAchievementCompactListControl = this.EnableAchievementCompactListControl,
                EnableAchievementDataGridControl = this.EnableAchievementDataGridControl,
                EnableAchievementCompactUnlockedListControl = this.EnableAchievementCompactUnlockedListControl,
                EnableAchievementCompactLockedListControl = this.EnableAchievementCompactLockedListControl,
                EnableAchievementProgressBarControl = this.EnableAchievementProgressBarControl,
                EnableAchievementStatsControl = this.EnableAchievementStatsControl,
                EnableAchievementButtonControl = this.EnableAchievementButtonControl,
                EnableAchievementViewItemControl = this.EnableAchievementViewItemControl,
                EnableAchievementPieChartControl = this.EnableAchievementPieChartControl,
                EnableAchievementBarChartControl = this.EnableAchievementBarChartControl,
                CompactListSortMode = this.CompactListSortMode,
                CompactListSortDescending = this.CompactListSortDescending,
                CompactUnlockedListSortMode = this.CompactUnlockedListSortMode,
                CompactUnlockedListSortDescending = this.CompactUnlockedListSortDescending,
                CompactLockedListSortMode = this.CompactLockedListSortMode,
                CompactLockedListSortDescending = this.CompactLockedListSortDescending,
                StartPagePieCharts = this.StartPagePieCharts?.Clone() ??
                    new StartPagePieWidgetSettings(),
                GridOptions = this.GridOptions?.Clone() ?? new GridOptionsCatalog(),
                StartPageActivityScope = this.StartPageActivityScope,
                StartPageProgressScope = this.StartPageProgressScope,
                EnableParallelProviderRefresh = this.EnableParallelProviderRefresh,
                ScanDelayMs = this.ScanDelayMs,
                MaxRetryAttempts = this.MaxRetryAttempts,
                ResourceOverrides = this.ResourceOverrides != null
                    ? this.ResourceOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value?.Clone(),
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase),

                // Layout State
                OverviewLeftColumnRatio = this.OverviewLeftColumnRatio,
                FriendsOverviewFriendColumnRatio = this.FriendsOverviewFriendColumnRatio,
                FriendsOverviewGameColumnRatio = this.FriendsOverviewGameColumnRatio,
                WindowPlacements = this.WindowPlacements != null
                    ? this.WindowPlacements.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value?.Clone(),
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase),
                OverviewTimelineRange = this.OverviewTimelineRange,
                ViewAchievementsTimelineRange = this.ViewAchievementsTimelineRange,
                ViewAchievementsTimelineVisible = this.ViewAchievementsTimelineVisible,

                // General Settings
                FirstTimeSetupCompleted = this.FirstTimeSetupCompleted,
                SeenThemeMigration = this.SeenThemeMigration,
                ThemeMigrationVersionCache = this.ThemeMigrationVersionCache != null
                    ? this.ThemeMigrationVersionCache.ToDictionary(
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
                ExcludedGameIds = this.ExcludedGameIds != null
                    ? new HashSet<Guid>(this.ExcludedGameIds)
                    : new HashSet<Guid>(),
                ExcludedFromSummariesGameIds = this.ExcludedFromSummariesGameIds != null
                    ? new HashSet<Guid>(this.ExcludedFromSummariesGameIds)
                    : new HashSet<Guid>(),
                SeparateLockedIconEnabledGameIds = this.SeparateLockedIconEnabledGameIds != null
                    ? new HashSet<Guid>(this.SeparateLockedIconEnabledGameIds)
                    : new HashSet<Guid>(),
                ManualCapstones = this.ManualCapstones != null
                    ? new Dictionary<Guid, string>(this.ManualCapstones)
                    : new Dictionary<Guid, string>(),
                AchievementOrderOverrides = this.AchievementOrderOverrides != null
                    ? this.AchievementOrderOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value != null
                            ? new List<string>(kvp.Value)
                            : new List<string>())
                    : new Dictionary<Guid, List<string>>(),
                AchievementCategoryOverrides = this.AchievementCategoryOverrides != null
                    ? this.AchievementCategoryOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value != null
                            ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                    : new Dictionary<Guid, Dictionary<string, string>>(),
                AchievementCategoryTypeOverrides = this.AchievementCategoryTypeOverrides != null
                    ? this.AchievementCategoryTypeOverrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value != null
                            ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                    : new Dictionary<Guid, Dictionary<string, string>>(),

                // Tagging Settings
                TaggingSettings = this.TaggingSettings?.Clone() ?? new TaggingSettings()
            };
        }

        public void ResetDisplaySettingsToDefaults()
        {
            var defaults = new PersistedSettings();

            ShowHiddenIcon = defaults.ShowHiddenIcon;
            ShowHiddenTitle = defaults.ShowHiddenTitle;
            ShowHiddenDescription = defaults.ShowHiddenDescription;
            ShowHiddenSuffix = defaults.ShowHiddenSuffix;
            ShowLockedIcon = defaults.ShowLockedIcon;
            ShowFriendSpoilers = defaults.ShowFriendSpoilers;
            UseSeparateLockedIconsWhenAvailable = defaults.UseSeparateLockedIconsWhenAvailable;
            SeparateLockedIconEnabledGameIds = new HashSet<Guid>();
            ModernCompactListShowRarityGlow = defaults.ModernCompactListShowRarityGlow;
            ModernUnlockedListShowRarityGlow = defaults.ModernUnlockedListShowRarityGlow;
            UseUniformRarityBadges = defaults.UseUniformRarityBadges;
            UseTrophiesForRarity = defaults.UseTrophiesForRarity;
            RoundRarityPercentages = defaults.RoundRarityPercentages;
            RarityColors = RarityColorSettings.CreateDefault();
            ResourceOverrides = CreateDefaultResourceOverrides();

            ShowOverviewCollectionScoreCard = defaults.ShowOverviewCollectionScoreCard;
            ShowOverviewPrestigeScoreCard = defaults.ShowOverviewPrestigeScoreCard;
            ShowOverviewPieCharts = defaults.ShowOverviewPieCharts;
            ShowOverviewGamesPieChart = defaults.ShowOverviewGamesPieChart;
            ShowOverviewProviderPieChart = defaults.ShowOverviewProviderPieChart;
            ShowOverviewRarityPieChart = defaults.ShowOverviewRarityPieChart;
            ShowOverviewTrophyPieChart = defaults.ShowOverviewTrophyPieChart;
            ShowOverviewPiePercentages = defaults.ShowOverviewPiePercentages;
            OverviewPieSmallSliceMode = defaults.OverviewPieSmallSliceMode;
            ShowOverviewBarCharts = defaults.ShowOverviewBarCharts;
            ShowTopMenuBarButton = defaults.ShowTopMenuBarButton;
            ShowCompletedProgressColoring = defaults.ShowCompletedProgressColoring;
            ShowCompactListRarityBar = defaults.ShowCompactListRarityBar;

            GridColumnHeaderAlignment = defaults.GridColumnHeaderAlignment;
            GridCellAlignment = defaults.GridCellAlignment;
            GridCellVerticalAlignment = defaults.GridCellVerticalAlignment;
            UnlockDateDisplayMode = defaults.UnlockDateDisplayMode;
            PlaytimeDisplayMode = defaults.PlaytimeDisplayMode;

            EnableAchievementCompactListControl = defaults.EnableAchievementCompactListControl;
            EnableAchievementDataGridControl = defaults.EnableAchievementDataGridControl;
            EnableAchievementCompactUnlockedListControl = defaults.EnableAchievementCompactUnlockedListControl;
            EnableAchievementCompactLockedListControl = defaults.EnableAchievementCompactLockedListControl;
            EnableAchievementProgressBarControl = defaults.EnableAchievementProgressBarControl;
            EnableAchievementStatsControl = defaults.EnableAchievementStatsControl;
            EnableAchievementButtonControl = defaults.EnableAchievementButtonControl;
            EnableAchievementViewItemControl = defaults.EnableAchievementViewItemControl;
            EnableAchievementPieChartControl = defaults.EnableAchievementPieChartControl;
            EnableAchievementBarChartControl = defaults.EnableAchievementBarChartControl;

            CompactListSortMode = defaults.CompactListSortMode;
            CompactListSortDescending = defaults.CompactListSortDescending;
            CompactUnlockedListSortMode = defaults.CompactUnlockedListSortMode;
            CompactUnlockedListSortDescending = defaults.CompactUnlockedListSortDescending;
            CompactLockedListSortMode = defaults.CompactLockedListSortMode;
            CompactLockedListSortDescending = defaults.CompactLockedListSortDescending;


            StartPagePieCharts = new StartPagePieWidgetSettings();
            GridOptions = new GridOptionsCatalog();
            StartPageActivityScope = defaults.StartPageActivityScope;
            StartPageProgressScope = defaults.StartPageProgressScope;


            OverviewLeftColumnRatio = defaults.OverviewLeftColumnRatio;
            FriendsOverviewFriendColumnRatio = defaults.FriendsOverviewFriendColumnRatio;
            FriendsOverviewGameColumnRatio = defaults.FriendsOverviewGameColumnRatio;
            ViewAchievementsTimelineRange = defaults.ViewAchievementsTimelineRange;
            ViewAchievementsTimelineVisible = defaults.ViewAchievementsTimelineVisible;
        }

        private static double NormalizeFriendsOverviewColumnRatio(double value, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return fallback;
            }

            return Math.Max(
                MinFriendsOverviewColumnRatio,
                Math.Min(MaxFriendsOverviewColumnRatio, value));
        }

        public static double? NormalizeGridRowHeight(double? value)
        {
            if (!value.HasValue ||
                double.IsNaN(value.Value) ||
                double.IsInfinity(value.Value) ||
                value.Value <= 0)
            {
                return null;
            }

            return Math.Max(MinimumGridRowHeight, value.Value);
        }

        public static int? NormalizeGridMaxRows(int? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return null;
            }

            return Math.Max(MinimumGridMaxRows, value.Value);
        }

        public static GameActivityScope NormalizeStartPageActivityScope(GameActivityScope value)
        {
            return value & GameActivityScope.All;
        }

        public static GameProgressScope NormalizeStartPageProgressScope(GameProgressScope value)
        {
            return value & GameProgressScope.All;
        }

        private static string NormalizePath(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string NormalizeHotkeyText(string value)
        {
            return AchievementHotkeyGesture.TryParse(value, out var gesture) && gesture != null
                ? gesture.ToString()
                : string.Empty;
        }

        // Content surfaces that ship transparent by default so embedded views blend into the host
        // theme. Popout windows back themselves with the opaque PopupSurface instead (see
        // PlayniteUiProvider.ApplyWindowThemeBrushes), so seeding WindowSurface transparent here does
        // not make standalone windows see-through. Seeded for fresh installs / display reset and,
        // once, for existing configs via AppearanceSettingsMigration.
        internal static Dictionary<string, ResourceOverrideSetting> CreateDefaultResourceOverrides()
        {
            var defaults = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[]
            {
                "PlayAch.Brush.GridSurface",
                "PlayAch.Brush.ControlSurface",
                "PlayAch.Brush.WindowSurface"
            })
            {
                defaults[key] = new ResourceOverrideSetting
                {
                    Mode = ResourceOverrideMode.Transparent,
                    CustomValue = ResourceOverrideSetting.TransparentValue
                };
            }

            return defaults;
        }

        private static Dictionary<string, ResourceOverrideSetting> NormalizeResourceOverrides(
            Dictionary<string, ResourceOverrideSetting> value)
        {
            var normalized = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
                {
                    continue;
                }

                var setting = pair.Value;
                switch (setting.Mode)
                {
                    case ResourceOverrideMode.FollowPlaynite:
                        continue;

                    case ResourceOverrideMode.Transparent:
                        normalized[key] = new ResourceOverrideSetting
                        {
                            Mode = ResourceOverrideMode.Transparent,
                            CustomValue = ResourceOverrideSetting.TransparentValue
                        };
                        break;

                    case ResourceOverrideMode.Custom:
                        var customValue = setting.CustomValue?.Trim();
                        if (string.IsNullOrWhiteSpace(customValue))
                        {
                            continue;
                        }

                        normalized[key] = new ResourceOverrideSetting
                        {
                            Mode = ResourceOverrideMode.Custom,
                            CustomValue = customValue
                        };
                        break;
                }
            }

            return normalized;
        }

        private static Dictionary<string, ProviderNotificationOverride> NormalizeProviderNotificationOverrides(
            IEnumerable<KeyValuePair<string, ProviderNotificationOverride>> value)
        {
            var normalized = new Dictionary<string, ProviderNotificationOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in value ?? Enumerable.Empty<KeyValuePair<string, ProviderNotificationOverride>>())
            {
                var key = NormalizeProviderKeyToken(pair.Key);
                if (key == null || pair.Value == null || pair.Value.IsAllInherit)
                {
                    continue;
                }

                normalized[key] = pair.Value;
            }

            return normalized;
        }

        private static HashSet<string> NormalizeProviderKeySet(IEnumerable<string> value)
        {
            if (value == null)
            {
                return CreateDefaultAutoDiscoverFriendProviderKeys();
            }

            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in value)
            {
                var token = NormalizeProviderKeyToken(key);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    normalized.Add(token);
                }
            }

            return normalized;
        }

        private static ObservableCollection<FriendSettingsEntry> NormalizeFriendEntries(
            IEnumerable<FriendSettingsEntry> value)
        {
            var normalized = new ObservableCollection<FriendSettingsEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in value ?? Enumerable.Empty<FriendSettingsEntry>())
            {
                var entry = friend?.Clone()?.Normalize();
                var key = FriendSettingsEntry.BuildKey(entry?.ProviderKey, entry?.ExternalUserId);
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                {
                    continue;
                }

                normalized.Add(entry);
            }

            return normalized;
        }

        private static ObservableCollection<FriendMergeGroup> NormalizeFriendMergeGroups(
            IEnumerable<FriendMergeGroup> value,
            IEnumerable<FriendSettingsEntry> friends)
        {
            var normalized = new ObservableCollection<FriendMergeGroup>();
            var friendKeys = new HashSet<string>(
                (friends ?? Enumerable.Empty<FriendSettingsEntry>())
                    .Select(friend => FriendAccountRef.BuildKey(friend?.ProviderKey, friend?.ExternalUserId))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            var claimedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in value ?? Enumerable.Empty<FriendMergeGroup>())
            {
                var group = source?.Clone()?.Normalize();
                if (group == null || string.IsNullOrWhiteSpace(group.Id) || !seenGroupIds.Add(group.Id))
                {
                    continue;
                }

                group.Members = NormalizeFriendMergeMembers(group.Members, friends)
                    .Where(member => !claimedAccounts.Contains(member.Key))
                    .ToList();
                if (friendKeys.Count > 0)
                {
                    group.Members = group.Members
                        .Where(member => friendKeys.Contains(member.Key))
                        .ToList();
                }

                if (group.Members.Count < 2)
                {
                    continue;
                }

                foreach (var member in group.Members)
                {
                    claimedAccounts.Add(member.Key);
                }

                if (group.AvatarAccount == null ||
                    !group.Members.Any(member => member.Matches(group.AvatarAccount.ProviderKey, group.AvatarAccount.ExternalUserId)))
                {
                    group.AvatarAccount = group.Members.FirstOrDefault()?.Clone();
                }

                normalized.Add(group.Normalize());
            }

            return normalized;
        }

        private static List<FriendAccountRef> NormalizeFriendMergeMembers(
            IEnumerable<FriendAccountRef> members,
            IEnumerable<FriendSettingsEntry> friends)
        {
            var friendKeys = new HashSet<string>(
                (friends ?? Enumerable.Empty<FriendSettingsEntry>())
                    .Select(friend => FriendAccountRef.BuildKey(friend?.ProviderKey, friend?.ExternalUserId))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            var seenAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<FriendAccountRef>();

            foreach (var member in members ?? Enumerable.Empty<FriendAccountRef>())
            {
                var account = member?.Clone()?.Normalize();
                var key = account?.Key;
                if (string.IsNullOrWhiteSpace(key) ||
                    !seenAccounts.Add(key) ||
                    !seenProviders.Add(account.ProviderKey) ||
                    (friendKeys.Count > 0 && !friendKeys.Contains(key)))
                {
                    continue;
                }

                normalized.Add(account);
            }

            return normalized
                .OrderBy(member => member.ProviderKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.ExternalUserId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void PruneFriendMergeGroupsForRemovedAccount(string providerKey, string externalUserId)
        {
            var removedKey = FriendAccountRef.BuildKey(providerKey, externalUserId);
            if (string.IsNullOrWhiteSpace(removedKey) || FriendMergeGroups.Count == 0)
            {
                return;
            }

            var groups = NormalizeFriendMergeGroups(FriendMergeGroups, Friends);
            foreach (var group in groups)
            {
                group.Members = (group.Members ?? new List<FriendAccountRef>())
                    .Where(member => !string.Equals(member?.Key, removedKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            FriendMergeGroups = groups;
        }

        private bool MigrateLegacySteamFriends(ObservableCollection<FriendSettingsEntry> entries)
        {
            var steam = GetProviderSettingsObject("Steam");
            if (steam == null)
            {
                return false;
            }

            var changed = false;
            foreach (var item in steam["IgnoredFriends"] as JArray ?? new JArray())
            {
                if (!(item is JObject row))
                {
                    continue;
                }

                var steamId = NormalizeProviderKeyToken(row["SteamId"]?.ToString());
                if (string.IsNullOrWhiteSpace(steamId))
                {
                    continue;
                }

                var existed = ContainsFriendEntry(entries, "Steam", steamId);
                var entry = EnsureFriendEntry(
                    entries,
                    "Steam",
                    steamId,
                    row["DisplayName"]?.ToString(),
                    row["AvatarUrl"]?.ToString(),
                    null,
                    FriendSettingsSource.AutoDiscovered);
                changed |= !existed;
                if (!entry.IsIgnored)
                {
                    entry.IsIgnored = true;
                    changed = true;
                }
            }

            foreach (var item in steam["FullLibraryFriends"] as JArray ?? new JArray())
            {
                if (!(item is JObject row))
                {
                    continue;
                }

                var steamId = NormalizeProviderKeyToken(row["SteamId"]?.ToString());
                if (string.IsNullOrWhiteSpace(steamId))
                {
                    continue;
                }

                var existed = ContainsFriendEntry(entries, "Steam", steamId);
                EnsureFriendEntry(
                    entries,
                    "Steam",
                    steamId,
                    row["DisplayName"]?.ToString(),
                    row["AvatarUrl"]?.ToString(),
                    null,
                    FriendSettingsSource.AutoDiscovered);
                changed |= !existed;
            }

            return changed;
        }

        private bool MigrateLegacyExophaseFriends(ObservableCollection<FriendSettingsEntry> entries)
        {
            var exophase = GetProviderSettingsObject("Exophase");
            var friends = exophase?["Friends"] as JArray;
            if (friends == null || friends.Count == 0)
            {
                return false;
            }

            var changed = false;
            foreach (var item in friends)
            {
                if (!(item is JObject row))
                {
                    continue;
                }

                var username = NormalizeProviderKeyToken(row["Username"]?.ToString());
                if (string.IsNullOrWhiteSpace(username))
                {
                    continue;
                }

                var existed = ContainsFriendEntry(entries, "Exophase", username);
                var entry = EnsureFriendEntry(
                    entries,
                    "Exophase",
                    username,
                    row["DisplayName"]?.ToString(),
                    row["AvatarUrl"]?.ToString(),
                    row["AvatarPath"]?.ToString(),
                    FriendSettingsSource.Manual);
                changed |= !existed;

                var nextPlatforms = FriendSettingsEntry.NormalizePlatformList(
                    (row["SelectedPlatforms"] as JArray)?.Select(token => token?.ToString()));

                var previousPlatformText = string.Join("\u001f", entry.SelectedPlatforms ?? new List<string>());
                var nextPlatformText = string.Join("\u001f", nextPlatforms);
                if (entry.Source != FriendSettingsSource.Manual)
                {
                    entry.Source = FriendSettingsSource.Manual;
                    changed = true;
                }

                if (!string.Equals(previousPlatformText, nextPlatformText, StringComparison.OrdinalIgnoreCase))
                {
                    entry.SelectedPlatforms = nextPlatforms;
                    changed = true;
                }

                entry.LastRefreshedUtc = ParseNullableUtc(row["LastRefreshedUtc"]) ?? entry.LastRefreshedUtc;
                entry.LastProbedUtc = ParseNullableUtc(row["LastProbedUtc"]) ?? entry.LastProbedUtc;
                entry.LastProbeStatus = NormalizeNullableString(row["LastProbeStatus"]?.ToString()) ?? entry.LastProbeStatus;
                entry.LastError = NormalizeNullableString(row["LastError"]?.ToString()) ?? entry.LastError;
            }

            return changed;
        }

        private JObject GetProviderSettingsObject(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey) ||
                ProviderSettings == null ||
                !ProviderSettings.TryGetValue(providerKey, out var settings))
            {
                return null;
            }

            return settings;
        }

        private static bool ContainsFriendEntry(
            IEnumerable<FriendSettingsEntry> entries,
            string providerKey,
            string externalUserId)
        {
            return (entries ?? Enumerable.Empty<FriendSettingsEntry>()).Any(entry =>
                string.Equals(entry?.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry?.ExternalUserId, externalUserId, StringComparison.OrdinalIgnoreCase));
        }

        private static FriendSettingsEntry EnsureFriendEntry(
            ObservableCollection<FriendSettingsEntry> entries,
            string providerKey,
            string externalUserId,
            string displayName,
            string avatarUrl,
            string avatarPath,
            FriendSettingsSource source)
        {
            providerKey = NormalizeProviderKeyToken(providerKey);
            externalUserId = NormalizeProviderKeyToken(externalUserId);
            if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(externalUserId))
            {
                return null;
            }

            var entry = entries.FirstOrDefault(item =>
                string.Equals(item?.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item?.ExternalUserId, externalUserId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new FriendSettingsEntry
                {
                    ProviderKey = providerKey,
                    ExternalUserId = externalUserId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? externalUserId : displayName.Trim(),
                    AvatarUrl = NormalizeNullableString(avatarUrl),
                    AvatarPath = NormalizeNullableString(avatarPath),
                    Source = source,
                    AddedUtc = DateTime.UtcNow
                };
                entries.Add(entry);
                return entry;
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                entry.DisplayName = displayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                entry.AvatarUrl = avatarUrl.Trim();
            }

            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                entry.AvatarPath = avatarPath.Trim();
            }

            return entry;
        }

        private static DateTime? ParseNullableUtc(JToken token)
        {
            if (token == null || string.IsNullOrWhiteSpace(token.ToString()))
            {
                return null;
            }

            if (!DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return null;
            }

            return parsed.Kind == DateTimeKind.Local ? parsed.ToUniversalTime() : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        private static string NormalizeProviderKeyToken(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string NormalizeNullableString(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static Dictionary<Guid, List<string>> NormalizeAchievementOrderOverrides(
            Dictionary<Guid, List<string>> value)
        {
            var normalized = new Dictionary<Guid, List<string>>();
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var order = Services.Achievements.AchievementOrderHelper.NormalizeApiNames(pair.Value);
                if (order.Count > 0)
                {
                    normalized[pair.Key] = order;
                }
            }

            return normalized;
        }

        private static Dictionary<Guid, Dictionary<string, string>> NormalizeAchievementCategoryOverrides(
            Dictionary<Guid, Dictionary<string, string>> value)
        {
            var normalized = new Dictionary<Guid, Dictionary<string, string>>();
            if (value == null)
            {
                return normalized;
            }

            foreach (var gamePair in value)
            {
                var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (gamePair.Value != null)
                {
                    foreach (var categoryPair in gamePair.Value)
                    {
                        var key = (categoryPair.Key ?? string.Empty).Trim();
                        var category = (categoryPair.Value ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(category))
                        {
                            continue;
                        }

                        categories[key] = category;
                    }
                }

                if (categories.Count > 0)
                {
                    normalized[gamePair.Key] = categories;
                }
            }

            return normalized;
        }

        private static Dictionary<Guid, Dictionary<string, string>> NormalizeAchievementCategoryTypeOverrides(
            Dictionary<Guid, Dictionary<string, string>> value)
        {
            var normalized = new Dictionary<Guid, Dictionary<string, string>>();
            if (value == null)
            {
                return normalized;
            }

            foreach (var gamePair in value)
            {
                var categoryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (gamePair.Value != null)
                {
                    foreach (var categoryTypePair in gamePair.Value)
                    {
                        var key = (categoryTypePair.Key ?? string.Empty).Trim();
                        var categoryType = Services.Achievements.AchievementCategoryTypeHelper.Normalize(categoryTypePair.Value);
                        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(categoryType))
                        {
                            continue;
                        }

                        categoryTypes[key] = categoryType;
                    }
                }

                if (categoryTypes.Count > 0)
                {
                    normalized[gamePair.Key] = categoryTypes;
                }
            }

            return normalized;
        }

        #endregion
    }
}
