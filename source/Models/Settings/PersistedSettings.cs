using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Tagging;

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
        public const GameActivityScope DefaultStartPageActivityScope = GameActivityScope.Played;
        public const GameProgressScope DefaultStartPageProgressScope =
            GameProgressScope.Completed | GameProgressScope.InProgress;
        public const string DefaultViewAchievementsHotkey = "Ctrl+Alt+V";
        public const string DefaultManageAchievementsHotkey = "Ctrl+Alt+M";
        public const string DefaultOverviewHotkey = "Ctrl+Alt+O";

        public PersistedSettings()
        {
            AttachStartPageSettingsHandlers();
        }

        #region Backing Fields

        private string _globalLanguage = "english";
        private bool _enablePeriodicUpdates = true;
        private bool _includeHiddenGamesInBulkScans = true;
        private int _periodicUpdateHours = 6;
        private bool _enableNotifications = true;
        private bool _notifyPeriodicUpdates = true;
        private bool _notifyOnRebuild = true;
        private int _recentRefreshGamesCount = 10;
        private bool _enableAchievementHotkeys = true;
        private bool _enableGlobalAchievementHotkeys = false;
        private string _viewAchievementsHotkey = DefaultViewAchievementsHotkey;
        private string _manageAchievementsHotkey = DefaultManageAchievementsHotkey;
        private string _overviewHotkey = DefaultOverviewHotkey;
        private bool _showHiddenIcon = false;
        private bool _showHiddenTitle = false;
        private bool _showHiddenDescription = false;
        private bool _showHiddenSuffix = true;
        private bool _showLockedIcon = true;
        private bool _preserveAchievementIconResolution = false;
        private bool _useSeparateLockedIconsWhenAvailable = false;
        private HashSet<Guid> _separateLockedIconEnabledGameIds = new HashSet<Guid>();
        private bool _showRarityGlow = true;
        private bool _useUniformRarityBadges = false;
        private bool _useCoverImages = true;
        private bool _includeUnplayedGames = true;
        private bool _showOverviewCollectionScoreCard = true;
        private bool _showOverviewPrestigeScoreCard = true;
        private bool _showOverviewPieCharts = true;
        private bool _showOverviewGamesPieChart = true;
        private bool _showOverviewProviderPieChart = true;
        private bool _showOverviewRarityPieChart = true;
        private bool _showOverviewTrophyPieChart = true;
        private bool _showOverviewPiePercentages = true;
        private OverviewPieSmallSliceMode _overviewPieSmallSliceMode = OverviewPieSmallSliceMode.Round;
        private bool _overviewPieChartVisibilityInitializedFromIndividualSettings;
        private bool _showOverviewBarCharts = true;
        private bool _showOverviewGameMetadata = true;
        private bool _showTopMenuBarButton = true;
        private bool _showCompactListRarityBar = true;
        private bool _showCompletionBorder = true;
        private bool _showGameSummariesGridColumnHeaders = true;
        private bool _showOverviewRecentAchievementsGridColumnHeaders = true;
        private bool _showOverviewSelectedGameGridColumnHeaders = true;
        private bool _showDesktopThemeAchievementGridColumnHeaders = true;
        private GridAlignment _gridColumnHeaderAlignment = GridAlignment.Center;
        private GridAlignment _gridCellAlignment = GridAlignment.Left;
        private GridVerticalAlignment _gridCellVerticalAlignment = GridVerticalAlignment.Center;
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
        private double? _achievementDataGridMaxHeight = DefaultAchievementDataGridMaxHeight;
        private double? _singleGameGridRowHeight;
        private double? _overviewGameSummariesGridRowHeight;
        private double? _overviewRecentAchievementsGridRowHeight;
        private double? _overviewSelectedGameGridRowHeight;
        private double? _desktopThemeAchievementGridRowHeight;
        private int? _singleGameGridMaxRows;
        private int? _overviewGameSummariesGridMaxRows;
        private int? _overviewRecentAchievementsGridMaxRows;
        private int? _overviewSelectedGameGridMaxRows;
        private int? _desktopThemeAchievementGridMaxRows;
        private StartPageGameSummariesGridSettings _startPageGameSummariesGrid =
            new StartPageGameSummariesGridSettings();
        private StartPageRecentUnlocksGridSettings _startPageRecentUnlocksGrid =
            new StartPageRecentUnlocksGridSettings();
        private StartPagePieWidgetSettings _startPagePieCharts =
            new StartPagePieWidgetSettings();
        private GameActivityScope _startPageActivityScope = DefaultStartPageActivityScope;
        private GameProgressScope _startPageProgressScope = DefaultStartPageProgressScope;
        private bool _enableParallelProviderRefresh = true;
        private int _scanDelayMs = 200;
        private int _maxRetryAttempts = 3;
        private List<CustomRefreshPreset> _customRefreshPresets = new List<CustomRefreshPreset>();
        private Dictionary<string, bool> _dataGridColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _dataGridColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _dataGridColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _overviewAchievementColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _overviewAchievementColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _overviewAchievementColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _overviewAchievementColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _overviewAchievementColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _overviewAchievementColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _overviewGameColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _overviewGameColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _overviewGameColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _overviewGameColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _overviewGameColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _overviewGameColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _singleGameColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _singleGameColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _singleGameColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _singleGameColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _singleGameColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _singleGameColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _desktopThemeColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _desktopThemeColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _desktopThemeColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _desktopThemeColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _desktopThemeColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _overviewGameSummariesColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _overviewGameSummariesColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _overviewGameSummariesColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _overviewGameSummariesColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _overviewGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _overviewGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _startPageAchievementColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _startPageAchievementColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _startPageAchievementColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _startPageAchievementColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _startPageAchievementColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _startPageAchievementColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _startPageGameSummariesColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _startPageGameSummariesColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _startPageGameSummariesColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _startPageGameSummariesColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _startPageGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _startPageGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private double _overviewLeftColumnRatio = DefaultOverviewLeftColumnRatio;
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
        private GameSummariesSortMode _overviewGameSummariesGridSortMode = GameSummariesSortMode.RecentUnlock;
        private bool _overviewGameSummariesGridSortDescending = true;
        private CompactListSortMode _overviewSelectedGameGridSortMode = CompactListSortMode.UnlockTime;
        private bool _overviewSelectedGameGridSortDescending = true;
        private CompactListSortMode _singleGameGridSortMode = CompactListSortMode.UnlockTime;
        private bool _singleGameGridSortDescending = true;
        private CompactListSortMode _achievementDataGridSortMode = CompactListSortMode.UnlockTime;
        private bool _achievementDataGridSortDescending = true;
        private TaggingSettings _taggingSettings;
        private Dictionary<string, JObject> _providerSettings = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

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
        /// Maximum recent games to refresh when using Recent Refresh.
        /// </summary>
        public int RecentRefreshGamesCount
        {
            get => _recentRefreshGamesCount;
            set => SetValue(ref _recentRefreshGamesCount, Math.Max(1, value));
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
        /// When true, achievement icons are cached at their original decoded size instead of the optimized 128px cache mode.
        /// Changes apply on the next refresh.
        /// </summary>
        public bool PreserveAchievementIconResolution
        {
            get => _preserveAchievementIconResolution;
            set => SetValue(ref _preserveAchievementIconResolution, value);
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
        /// When true, unlocked achievement icons display rarity-based glow effects.
        /// </summary>
        public bool ShowRarityGlow
        {
            get => _showRarityGlow;
            set => SetValue(ref _showRarityGlow, value);
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
        /// When true, use Playnite cover images instead of icons/logos in the games list.
        /// </summary>
        public bool UseCoverImages
        {
            get => _useCoverImages;
            set => SetValue(ref _useCoverImages, value);
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
        /// When true, shows platform/playtime/region metadata under game names in the overview game summaries.
        /// </summary>
        public bool ShowOverviewGameMetadata
        {
            get => _showOverviewGameMetadata;
            set => SetValue(ref _showOverviewGameMetadata, value);
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
        /// When true, shows the rarity bar at the bottom of compact list achievement items.
        /// </summary>
        public bool ShowCompactListRarityBar
        {
            get => _showCompactListRarityBar;
            set => SetValue(ref _showCompactListRarityBar, value);
        }

        /// <summary>
        /// When true, completed games display a blue border in the game summaries.
        /// </summary>
        public bool ShowCompletionBorder
        {
            get => _showCompletionBorder;
            set => SetValue(ref _showCompletionBorder, value);
        }

        /// <summary>
        /// When true, shows column headers in game summaries grids.
        /// </summary>
        public bool ShowOverviewGameSummariesGridColumnHeaders
        {
            get => _showGameSummariesGridColumnHeaders;
            set => SetValue(ref _showGameSummariesGridColumnHeaders, value);
        }

        /// <summary>
        /// When true, shows column headers in the overview recent achievements grid.
        /// </summary>
        public bool ShowOverviewRecentAchievementsGridColumnHeaders
        {
            get => _showOverviewRecentAchievementsGridColumnHeaders;
            set => SetValue(ref _showOverviewRecentAchievementsGridColumnHeaders, value);
        }

        /// <summary>
        /// When true, shows column headers in the overview selected game achievements grid.
        /// </summary>
        public bool ShowOverviewSelectedGameGridColumnHeaders
        {
            get => _showOverviewSelectedGameGridColumnHeaders;
            set => SetValue(ref _showOverviewSelectedGameGridColumnHeaders, value);
        }

        /// <summary>
        /// When true, shows column headers in desktop theme achievement grids.
        /// </summary>
        public bool ShowDesktopThemeAchievementGridColumnHeaders
        {
            get => _showDesktopThemeAchievementGridColumnHeaders;
            set => SetValue(ref _showDesktopThemeAchievementGridColumnHeaders, value);
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

        /// <summary>
        /// Sort mode for the overview game summaries grid.
        /// </summary>
        public GameSummariesSortMode OverviewGameSummariesGridSortMode
        {
            get => _overviewGameSummariesGridSortMode;
            set => SetValue(ref _overviewGameSummariesGridSortMode, value);
        }

        /// <summary>
        /// When true, reverses the configured sort direction for the overview game summaries grid.
        /// </summary>
        public bool OverviewGameSummariesGridSortDescending
        {
            get => _overviewGameSummariesGridSortDescending;
            set => SetValue(ref _overviewGameSummariesGridSortDescending, value);
        }

        /// <summary>
        /// Sort mode for the overview selected-game grid.
        /// None preserves custom order when configured, otherwise provider order.
        /// </summary>
        public CompactListSortMode OverviewSelectedGameGridSortMode
        {
            get => _overviewSelectedGameGridSortMode;
            set => SetValue(ref _overviewSelectedGameGridSortMode, value);
        }

        /// <summary>
        /// When true, reverses the configured sort direction for the overview selected-game grid.
        /// Ignored when OverviewSelectedGameGridSortMode is None.
        /// </summary>
        public bool OverviewSelectedGameGridSortDescending
        {
            get => _overviewSelectedGameGridSortDescending;
            set => SetValue(ref _overviewSelectedGameGridSortDescending, value);
        }

        /// <summary>
        /// Sort mode for the single-game achievement grid.
        /// None preserves custom order when configured, otherwise provider order.
        /// </summary>
        public CompactListSortMode SingleGameGridSortMode
        {
            get => _singleGameGridSortMode;
            set => SetValue(ref _singleGameGridSortMode, value);
        }

        /// <summary>
        /// When true, reverses the configured sort direction for the single-game grid.
        /// Ignored when SingleGameGridSortMode is None.
        /// </summary>
        public bool SingleGameGridSortDescending
        {
            get => _singleGameGridSortDescending;
            set => SetValue(ref _singleGameGridSortDescending, value);
        }

        /// <summary>
        /// Sort mode for the shared modern AchievementDataGrid control.
        /// None preserves custom order when configured, otherwise provider order.
        /// </summary>
        public CompactListSortMode AchievementDataGridSortMode
        {
            get => _achievementDataGridSortMode;
            set => SetValue(ref _achievementDataGridSortMode, value);
        }

        /// <summary>
        /// When true, reverses the configured sort direction for the shared modern AchievementDataGrid control.
        /// Ignored when AchievementDataGridSortMode is None.
        /// </summary>
        public bool AchievementDataGridSortDescending
        {
            get => _achievementDataGridSortDescending;
            set => SetValue(ref _achievementDataGridSortDescending, value);
        }

        /// <summary>
        /// Maximum height for AchievementDataGrid controls (null = unlimited).
        /// Defaults to 600 so theme-hosted grids do not expand indefinitely.
        /// </summary>
        public double? AchievementDataGridMaxHeight
        {
            get => _achievementDataGridMaxHeight;
            set => SetValue(ref _achievementDataGridMaxHeight, value);
        }

        /// <summary>
        /// Fixed row height for the single-game achievement grid (null = automatic).
        /// </summary>
        public double? SingleGameGridRowHeight
        {
            get => _singleGameGridRowHeight;
            set => SetValue(ref _singleGameGridRowHeight, NormalizeGridRowHeight(value));
        }

        /// <summary>
        /// Fixed row height for the overview game summaries grid (null = automatic).
        /// </summary>
        public double? OverviewGameSummariesGridRowHeight
        {
            get => _overviewGameSummariesGridRowHeight;
            set => SetValue(ref _overviewGameSummariesGridRowHeight, NormalizeGridRowHeight(value));
        }

        /// <summary>
        /// Fixed row height for the overview recent achievements grid (null = automatic).
        /// </summary>
        public double? OverviewRecentAchievementsGridRowHeight
        {
            get => _overviewRecentAchievementsGridRowHeight;
            set => SetValue(ref _overviewRecentAchievementsGridRowHeight, NormalizeGridRowHeight(value));
        }

        /// <summary>
        /// Fixed row height for the overview selected-game achievement grid (null = automatic).
        /// </summary>
        public double? OverviewSelectedGameGridRowHeight
        {
            get => _overviewSelectedGameGridRowHeight;
            set => SetValue(ref _overviewSelectedGameGridRowHeight, NormalizeGridRowHeight(value));
        }

        /// <summary>
        /// Fixed row height for the Start Page game summaries grid (null = automatic).
        /// </summary>
        public double? StartPageGameSummariesGridRowHeight
        {
            get => StartPageGameSummariesGrid.RowHeight;
            set => StartPageGameSummariesGrid.RowHeight = value;
        }

        /// <summary>
        /// Fixed row height for the Start Page recent unlocks grid (null = automatic).
        /// </summary>
        public double? StartPageRecentAchievementsGridRowHeight
        {
            get => StartPageRecentUnlocksGrid.RowHeight;
            set => StartPageRecentUnlocksGrid.RowHeight = value;
        }

        /// <summary>
        /// Fixed row height for the desktop theme achievement grid (null = automatic).
        /// </summary>
        public double? DesktopThemeAchievementGridRowHeight
        {
            get => _desktopThemeAchievementGridRowHeight;
            set => SetValue(ref _desktopThemeAchievementGridRowHeight, NormalizeGridRowHeight(value));
        }

        /// <summary>
        /// Maximum rendered rows for the single-game achievement grid (null = unlimited).
        /// </summary>
        public int? SingleGameGridMaxRows
        {
            get => _singleGameGridMaxRows;
            set => SetValue(ref _singleGameGridMaxRows, NormalizeGridMaxRows(value));
        }

        /// <summary>
        /// Maximum rendered rows for the overview game summaries grid (null = unlimited).
        /// </summary>
        public int? OverviewGameSummariesGridMaxRows
        {
            get => _overviewGameSummariesGridMaxRows;
            set => SetValue(ref _overviewGameSummariesGridMaxRows, NormalizeGridMaxRows(value));
        }

        /// <summary>
        /// Maximum rendered rows for the overview recent achievements grid (null = unlimited).
        /// </summary>
        public int? OverviewRecentAchievementsGridMaxRows
        {
            get => _overviewRecentAchievementsGridMaxRows;
            set => SetValue(ref _overviewRecentAchievementsGridMaxRows, NormalizeGridMaxRows(value));
        }

        /// <summary>
        /// Maximum rendered rows for the overview selected-game achievement grid (null = unlimited).
        /// </summary>
        public int? OverviewSelectedGameGridMaxRows
        {
            get => _overviewSelectedGameGridMaxRows;
            set => SetValue(ref _overviewSelectedGameGridMaxRows, NormalizeGridMaxRows(value));
        }

        /// <summary>
        /// Maximum rendered rows for the Start Page game summaries grid (null = unlimited).
        /// Defaults to 25 to preserve the previous Start Page behavior.
        /// </summary>
        public int? StartPageGameSummariesGridMaxRows
        {
            get => StartPageGameSummariesGrid.MaxRows;
            set => StartPageGameSummariesGrid.MaxRows = value;
        }

        /// <summary>
        /// Maximum rendered rows for the Start Page recent unlocks grid (null = unlimited).
        /// Defaults to 25 to preserve the previous Start Page behavior.
        /// </summary>
        public int? StartPageRecentAchievementsGridMaxRows
        {
            get => StartPageRecentUnlocksGrid.MaxRows;
            set => StartPageRecentUnlocksGrid.MaxRows = value;
        }

        /// <summary>
        /// Maximum rendered rows for the desktop theme achievement grid (null = unlimited).
        /// </summary>
        public int? DesktopThemeAchievementGridMaxRows
        {
            get => _desktopThemeAchievementGridMaxRows;
            set => SetValue(ref _desktopThemeAchievementGridMaxRows, NormalizeGridMaxRows(value));
        }

        public StartPageGameSummariesGridSettings StartPageGameSummariesGrid
        {
            get => _startPageGameSummariesGrid ?? (_startPageGameSummariesGrid = AttachStartPageSettings(
                new StartPageGameSummariesGridSettings()));
            set
            {
                var normalized = value ?? new StartPageGameSummariesGridSettings();
                if (ReferenceEquals(_startPageGameSummariesGrid, normalized))
                {
                    return;
                }

                DetachStartPageSettings(_startPageGameSummariesGrid);
                _startPageGameSummariesGrid = AttachStartPageSettings(normalized);
                OnPropertyChanged();
            }
        }

        public StartPageRecentUnlocksGridSettings StartPageRecentUnlocksGrid
        {
            get => _startPageRecentUnlocksGrid ?? (_startPageRecentUnlocksGrid = AttachStartPageSettings(
                new StartPageRecentUnlocksGridSettings()));
            set
            {
                var normalized = value ?? new StartPageRecentUnlocksGridSettings();
                if (ReferenceEquals(_startPageRecentUnlocksGrid, normalized))
                {
                    return;
                }

                DetachStartPageSettings(_startPageRecentUnlocksGrid);
                _startPageRecentUnlocksGrid = AttachStartPageSettings(normalized);
                OnPropertyChanged();
            }
        }

        public StartPagePieWidgetSettings StartPagePieCharts
        {
            get => _startPagePieCharts ?? (_startPagePieCharts = AttachStartPageSettings(
                new StartPagePieWidgetSettings()));
            set => SetStartPagePieSettings(ref _startPagePieCharts, value, nameof(StartPagePieCharts));
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

        #endregion

        #region UI Column Settings

        /// <summary>
        /// Persisted visibility state for shared achievement DataGrid columns.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> DataGridColumnVisibility
        {
            get => _dataGridColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _dataGridColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Legacy shared width storage for achievement DataGrid columns.
        /// New installs use scope-specific maps; this remains for backward compatibility fallback.
        /// </summary>
        public Dictionary<string, double> DataGridColumnWidths
        {
            get => _dataGridColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _dataGridColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Legacy shared order storage for achievement DataGrid columns.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> DataGridColumnOrder
        {
            get => _dataGridColumnOrder;
            set => SetValue(ref _dataGridColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted visibility state for overview recent achievement columns.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> OverviewRecentAchievementColumnVisibility
        {
            get => _overviewAchievementColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _overviewAchievementColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for overview recent achievement columns.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> OverviewRecentAchievementColumnWidths
        {
            get => _overviewAchievementColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _overviewAchievementColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted order for overview recent achievement columns.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> OverviewRecentAchievementColumnOrder
        {
            get => _overviewAchievementColumnOrder;
            set => SetValue(ref _overviewAchievementColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted cell text alignment overrides for overview recent achievement columns.
        /// Missing keys inherit the global GridCellAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> OverviewRecentAchievementColumnAlignments
        {
            get => _overviewAchievementColumnAlignments;
            set => SetValue(ref _overviewAchievementColumnAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted cell vertical alignment overrides for overview recent achievement columns.
        /// Missing keys inherit the global GridCellVerticalAlignment setting.
        /// </summary>
        public Dictionary<string, GridVerticalAlignment> OverviewRecentAchievementColumnVerticalAlignments
        {
            get => _overviewAchievementColumnVerticalAlignments;
            set => SetValue(ref _overviewAchievementColumnVerticalAlignments, NormalizeColumnVerticalAlignments(value));
        }

        /// <summary>
        /// Persisted header horizontal alignment overrides for overview recent achievement columns.
        /// Missing keys inherit the global GridColumnHeaderAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> OverviewRecentAchievementColumnHeaderAlignments
        {
            get => _overviewAchievementColumnHeaderAlignments;
            set => SetValue(ref _overviewAchievementColumnHeaderAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted visibility state for the overview selected game achievement columns.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> OverviewSelectedGameAchievementColumnVisibility
        {
            get => _overviewGameColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _overviewGameColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for the overview selected game achievement columns.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> OverviewSelectedGameAchievementColumnWidths
        {
            get => _overviewGameColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _overviewGameColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted order for overview selected game achievement columns.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> OverviewSelectedGameAchievementColumnOrder
        {
            get => _overviewGameColumnOrder;
            set => SetValue(ref _overviewGameColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted cell text alignment overrides for overview selected-game achievement columns.
        /// Missing keys inherit the global GridCellAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> OverviewSelectedGameAchievementColumnAlignments
        {
            get => _overviewGameColumnAlignments;
            set => SetValue(ref _overviewGameColumnAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted cell vertical alignment overrides for overview selected-game achievement columns.
        /// Missing keys inherit the global GridCellVerticalAlignment setting.
        /// </summary>
        public Dictionary<string, GridVerticalAlignment> OverviewSelectedGameAchievementColumnVerticalAlignments
        {
            get => _overviewGameColumnVerticalAlignments;
            set => SetValue(ref _overviewGameColumnVerticalAlignments, NormalizeColumnVerticalAlignments(value));
        }

        /// <summary>
        /// Persisted header horizontal alignment overrides for overview selected-game achievement columns.
        /// Missing keys inherit the global GridColumnHeaderAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> OverviewSelectedGameAchievementColumnHeaderAlignments
        {
            get => _overviewGameColumnHeaderAlignments;
            set => SetValue(ref _overviewGameColumnHeaderAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted visibility state for the single-game achievement columns.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> SingleGameColumnVisibility
        {
            get => _singleGameColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _singleGameColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for the single-game achievement columns.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> SingleGameColumnWidths
        {
            get => _singleGameColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _singleGameColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted order for the single-game achievement columns.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> SingleGameColumnOrder
        {
            get => _singleGameColumnOrder;
            set => SetValue(ref _singleGameColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted cell text alignment overrides for single-game achievement columns.
        /// Missing keys inherit the global GridCellAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> SingleGameColumnAlignments
        {
            get => _singleGameColumnAlignments;
            set => SetValue(ref _singleGameColumnAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted cell vertical alignment overrides for single-game achievement columns.
        /// Missing keys inherit the global GridCellVerticalAlignment setting.
        /// </summary>
        public Dictionary<string, GridVerticalAlignment> SingleGameColumnVerticalAlignments
        {
            get => _singleGameColumnVerticalAlignments;
            set => SetValue(ref _singleGameColumnVerticalAlignments, NormalizeColumnVerticalAlignments(value));
        }

        /// <summary>
        /// Persisted header horizontal alignment overrides for single-game achievement columns.
        /// Missing keys inherit the global GridColumnHeaderAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> SingleGameColumnHeaderAlignments
        {
            get => _singleGameColumnHeaderAlignments;
            set => SetValue(ref _singleGameColumnHeaderAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted widths for desktop theme integration achievement columns.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> DesktopThemeColumnWidths
        {
            get => _desktopThemeColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _desktopThemeColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted order for desktop theme integration achievement columns.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> DesktopThemeColumnOrder
        {
            get => _desktopThemeColumnOrder;
            set => SetValue(ref _desktopThemeColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted cell text alignment overrides for desktop theme integration achievement columns.
        /// Missing keys inherit the global GridCellAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> DesktopThemeColumnAlignments
        {
            get => _desktopThemeColumnAlignments;
            set => SetValue(ref _desktopThemeColumnAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted cell vertical alignment overrides for desktop theme integration achievement columns.
        /// Missing keys inherit the global GridCellVerticalAlignment setting.
        /// </summary>
        public Dictionary<string, GridVerticalAlignment> DesktopThemeColumnVerticalAlignments
        {
            get => _desktopThemeColumnVerticalAlignments;
            set => SetValue(ref _desktopThemeColumnVerticalAlignments, NormalizeColumnVerticalAlignments(value));
        }

        /// <summary>
        /// Persisted header horizontal alignment overrides for desktop theme integration achievement columns.
        /// Missing keys inherit the global GridColumnHeaderAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> DesktopThemeColumnHeaderAlignments
        {
            get => _desktopThemeColumnHeaderAlignments;
            set => SetValue(ref _desktopThemeColumnHeaderAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted visibility state for game summaries columns in the overview.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> OverviewGameSummariesColumnVisibility
        {
            get => _overviewGameSummariesColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _overviewGameSummariesColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for game summaries columns in the overview.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> OverviewGameSummariesColumnWidths
        {
            get => _overviewGameSummariesColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _overviewGameSummariesColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted order for game summaries columns in the overview.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> OverviewGameSummariesColumnOrder
        {
            get => _overviewGameSummariesColumnOrder;
            set => SetValue(ref _overviewGameSummariesColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted cell text alignment overrides for game summaries columns in the overview.
        /// Missing keys inherit the global GridCellAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> OverviewGameSummariesColumnAlignments
        {
            get => _overviewGameSummariesColumnAlignments;
            set => SetValue(ref _overviewGameSummariesColumnAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted cell vertical alignment overrides for game summaries columns.
        /// Missing keys inherit the global GridCellVerticalAlignment setting.
        /// </summary>
        public Dictionary<string, GridVerticalAlignment> OverviewGameSummariesColumnVerticalAlignments
        {
            get => _overviewGameSummariesColumnVerticalAlignments;
            set => SetValue(ref _overviewGameSummariesColumnVerticalAlignments, NormalizeColumnVerticalAlignments(value));
        }

        /// <summary>
        /// Persisted header horizontal alignment overrides for game summaries columns.
        /// Missing keys inherit the global GridColumnHeaderAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> OverviewGameSummariesColumnHeaderAlignments
        {
            get => _overviewGameSummariesColumnHeaderAlignments;
            set => SetValue(ref _overviewGameSummariesColumnHeaderAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted visibility state for StartPage achievement grid columns.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> StartPageAchievementColumnVisibility
        {
            get => _startPageAchievementColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _startPageAchievementColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for StartPage achievement grid columns.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> StartPageAchievementColumnWidths
        {
            get => _startPageAchievementColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _startPageAchievementColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted order for StartPage achievement grid columns.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> StartPageAchievementColumnOrder
        {
            get => _startPageAchievementColumnOrder;
            set => SetValue(ref _startPageAchievementColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted cell text alignment overrides for StartPage achievement grid columns.
        /// Missing keys inherit the global GridCellAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> StartPageAchievementColumnAlignments
        {
            get => _startPageAchievementColumnAlignments;
            set => SetValue(ref _startPageAchievementColumnAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted cell vertical alignment overrides for StartPage achievement grid columns.
        /// Missing keys inherit the global GridCellVerticalAlignment setting.
        /// </summary>
        public Dictionary<string, GridVerticalAlignment> StartPageAchievementColumnVerticalAlignments
        {
            get => _startPageAchievementColumnVerticalAlignments;
            set => SetValue(ref _startPageAchievementColumnVerticalAlignments, NormalizeColumnVerticalAlignments(value));
        }

        /// <summary>
        /// Persisted header horizontal alignment overrides for StartPage achievement grid columns.
        /// Missing keys inherit the global GridColumnHeaderAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> StartPageAchievementColumnHeaderAlignments
        {
            get => _startPageAchievementColumnHeaderAlignments;
            set => SetValue(ref _startPageAchievementColumnHeaderAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted visibility state for StartPage game summaries grid columns.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> StartPageGameSummariesColumnVisibility
        {
            get => _startPageGameSummariesColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _startPageGameSummariesColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for StartPage game summaries grid columns.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> StartPageGameSummariesColumnWidths
        {
            get => _startPageGameSummariesColumnWidths;
            set
            {
                var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (value != null)
                {
                    foreach (var pair in value)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            !double.IsNaN(pair.Value) &&
                            !double.IsInfinity(pair.Value) &&
                            pair.Value > 0)
                        {
                            normalized[pair.Key] = pair.Value;
                        }
                    }
                }

                SetValue(ref _startPageGameSummariesColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted order for StartPage game summaries grid columns.
        /// Key is a stable column identifier, value is DisplayIndex.
        /// </summary>
        public Dictionary<string, int> StartPageGameSummariesColumnOrder
        {
            get => _startPageGameSummariesColumnOrder;
            set => SetValue(ref _startPageGameSummariesColumnOrder, NormalizeColumnOrder(value));
        }

        /// <summary>
        /// Persisted cell text alignment overrides for StartPage game summaries grid columns.
        /// Missing keys inherit the global GridCellAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> StartPageGameSummariesColumnAlignments
        {
            get => _startPageGameSummariesColumnAlignments;
            set => SetValue(ref _startPageGameSummariesColumnAlignments, NormalizeColumnAlignments(value));
        }

        /// <summary>
        /// Persisted cell vertical alignment overrides for StartPage game summaries grid columns.
        /// Missing keys inherit the global GridCellVerticalAlignment setting.
        /// </summary>
        public Dictionary<string, GridVerticalAlignment> StartPageGameSummariesColumnVerticalAlignments
        {
            get => _startPageGameSummariesColumnVerticalAlignments;
            set => SetValue(ref _startPageGameSummariesColumnVerticalAlignments, NormalizeColumnVerticalAlignments(value));
        }

        /// <summary>
        /// Persisted header horizontal alignment overrides for StartPage game summaries grid columns.
        /// Missing keys inherit the global GridColumnHeaderAlignment setting.
        /// </summary>
        public Dictionary<string, GridAlignment> StartPageGameSummariesColumnHeaderAlignments
        {
            get => _startPageGameSummariesColumnHeaderAlignments;
            set => SetValue(ref _startPageGameSummariesColumnHeaderAlignments, NormalizeColumnAlignments(value));
        }

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
                _startPageGameSummariesGrid ?? new StartPageGameSummariesGridSettings());
            _startPageRecentUnlocksGrid = AttachStartPageSettings(
                _startPageRecentUnlocksGrid ?? new StartPageRecentUnlocksGridSettings());
            _startPagePieCharts = AttachStartPageSettings(
                _startPagePieCharts ?? new StartPagePieWidgetSettings());
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

                // Global Settings
                GlobalLanguage = this.GlobalLanguage,

                // Update and Refresh Settings
                EnablePeriodicUpdates = this.EnablePeriodicUpdates,
                IncludeHiddenGamesInBulkScans = this.IncludeHiddenGamesInBulkScans,
                PeriodicUpdateHours = this.PeriodicUpdateHours,
                RecentRefreshGamesCount = this.RecentRefreshGamesCount,
                CustomRefreshPresets = this.CustomRefreshPresets != null
                    ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(this.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                    : new List<CustomRefreshPreset>(),

                // Hotkey Settings
                EnableAchievementHotkeys = this.EnableAchievementHotkeys,
                EnableGlobalAchievementHotkeys = this.EnableGlobalAchievementHotkeys,
                ViewAchievementsHotkey = this.ViewAchievementsHotkey,
                ManageAchievementsHotkey = this.ManageAchievementsHotkey,
                OverviewHotkey = this.OverviewHotkey,

                // Notification Settings
                EnableNotifications = this.EnableNotifications,
                NotifyPeriodicUpdates = this.NotifyPeriodicUpdates,
                NotifyOnRebuild = this.NotifyOnRebuild,

                // Display Preferences
                ShowHiddenIcon = this.ShowHiddenIcon,
                ShowHiddenTitle = this.ShowHiddenTitle,
                ShowHiddenDescription = this.ShowHiddenDescription,
                ShowHiddenSuffix = this.ShowHiddenSuffix,
                ShowLockedIcon = this.ShowLockedIcon,
                PreserveAchievementIconResolution = this.PreserveAchievementIconResolution,
                UseSeparateLockedIconsWhenAvailable = this.UseSeparateLockedIconsWhenAvailable,
                ShowRarityGlow = this.ShowRarityGlow,
                UseUniformRarityBadges = this.UseUniformRarityBadges,
                UseCoverImages = this.UseCoverImages,
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
                ShowOverviewGameMetadata = this.ShowOverviewGameMetadata,
                ShowTopMenuBarButton = this.ShowTopMenuBarButton,
                ShowCompactListRarityBar = this.ShowCompactListRarityBar,
                ShowCompletionBorder = this.ShowCompletionBorder,
                ShowOverviewGameSummariesGridColumnHeaders = this.ShowOverviewGameSummariesGridColumnHeaders,
                ShowOverviewRecentAchievementsGridColumnHeaders = this.ShowOverviewRecentAchievementsGridColumnHeaders,
                ShowOverviewSelectedGameGridColumnHeaders = this.ShowOverviewSelectedGameGridColumnHeaders,
                ShowDesktopThemeAchievementGridColumnHeaders = this.ShowDesktopThemeAchievementGridColumnHeaders,
                GridColumnHeaderAlignment = this.GridColumnHeaderAlignment,
                GridCellAlignment = this.GridCellAlignment,
                GridCellVerticalAlignment = this.GridCellVerticalAlignment,
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
                OverviewGameSummariesGridSortMode = this.OverviewGameSummariesGridSortMode,
                OverviewGameSummariesGridSortDescending = this.OverviewGameSummariesGridSortDescending,
                OverviewSelectedGameGridSortMode = this.OverviewSelectedGameGridSortMode,
                OverviewSelectedGameGridSortDescending = this.OverviewSelectedGameGridSortDescending,
                SingleGameGridSortMode = this.SingleGameGridSortMode,
                SingleGameGridSortDescending = this.SingleGameGridSortDescending,
                AchievementDataGridSortMode = this.AchievementDataGridSortMode,
                AchievementDataGridSortDescending = this.AchievementDataGridSortDescending,
                AchievementDataGridMaxHeight = this.AchievementDataGridMaxHeight,
                SingleGameGridRowHeight = this.SingleGameGridRowHeight,
                OverviewGameSummariesGridRowHeight = this.OverviewGameSummariesGridRowHeight,
                OverviewRecentAchievementsGridRowHeight = this.OverviewRecentAchievementsGridRowHeight,
                OverviewSelectedGameGridRowHeight = this.OverviewSelectedGameGridRowHeight,
                StartPageGameSummariesGridRowHeight = this.StartPageGameSummariesGridRowHeight,
                StartPageRecentAchievementsGridRowHeight = this.StartPageRecentAchievementsGridRowHeight,
                DesktopThemeAchievementGridRowHeight = this.DesktopThemeAchievementGridRowHeight,
                SingleGameGridMaxRows = this.SingleGameGridMaxRows,
                OverviewGameSummariesGridMaxRows = this.OverviewGameSummariesGridMaxRows,
                OverviewRecentAchievementsGridMaxRows = this.OverviewRecentAchievementsGridMaxRows,
                OverviewSelectedGameGridMaxRows = this.OverviewSelectedGameGridMaxRows,
                StartPageGameSummariesGridMaxRows = this.StartPageGameSummariesGridMaxRows,
                StartPageRecentAchievementsGridMaxRows = this.StartPageRecentAchievementsGridMaxRows,
                DesktopThemeAchievementGridMaxRows = this.DesktopThemeAchievementGridMaxRows,
                StartPageGameSummariesGrid = this.StartPageGameSummariesGrid?.Clone() ??
                    new StartPageGameSummariesGridSettings(),
                StartPageRecentUnlocksGrid = this.StartPageRecentUnlocksGrid?.Clone() ??
                    new StartPageRecentUnlocksGridSettings(),
                StartPagePieCharts = this.StartPagePieCharts?.Clone() ??
                    new StartPagePieWidgetSettings(),
                StartPageActivityScope = this.StartPageActivityScope,
                StartPageProgressScope = this.StartPageProgressScope,
                EnableParallelProviderRefresh = this.EnableParallelProviderRefresh,
                ScanDelayMs = this.ScanDelayMs,
                MaxRetryAttempts = this.MaxRetryAttempts,

                // UI Column Settings
                DataGridColumnVisibility = this.DataGridColumnVisibility != null
                    ? new Dictionary<string, bool>(this.DataGridColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                DataGridColumnWidths = this.DataGridColumnWidths != null
                    ? new Dictionary<string, double>(this.DataGridColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                DataGridColumnOrder = this.DataGridColumnOrder != null
                    ? new Dictionary<string, int>(this.DataGridColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                OverviewRecentAchievementColumnVisibility = this.OverviewRecentAchievementColumnVisibility != null
                    ? new Dictionary<string, bool>(this.OverviewRecentAchievementColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                OverviewRecentAchievementColumnWidths = this.OverviewRecentAchievementColumnWidths != null
                    ? new Dictionary<string, double>(this.OverviewRecentAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                OverviewRecentAchievementColumnOrder = this.OverviewRecentAchievementColumnOrder != null
                    ? new Dictionary<string, int>(this.OverviewRecentAchievementColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                OverviewRecentAchievementColumnAlignments = this.OverviewRecentAchievementColumnAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.OverviewRecentAchievementColumnAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewRecentAchievementColumnVerticalAlignments = this.OverviewRecentAchievementColumnVerticalAlignments != null
                    ? new Dictionary<string, GridVerticalAlignment>(this.OverviewRecentAchievementColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewRecentAchievementColumnHeaderAlignments = this.OverviewRecentAchievementColumnHeaderAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.OverviewRecentAchievementColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewSelectedGameAchievementColumnVisibility = this.OverviewSelectedGameAchievementColumnVisibility != null
                    ? new Dictionary<string, bool>(this.OverviewSelectedGameAchievementColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                OverviewSelectedGameAchievementColumnWidths = this.OverviewSelectedGameAchievementColumnWidths != null
                    ? new Dictionary<string, double>(this.OverviewSelectedGameAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                OverviewSelectedGameAchievementColumnOrder = this.OverviewSelectedGameAchievementColumnOrder != null
                    ? new Dictionary<string, int>(this.OverviewSelectedGameAchievementColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                OverviewSelectedGameAchievementColumnAlignments = this.OverviewSelectedGameAchievementColumnAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.OverviewSelectedGameAchievementColumnAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewSelectedGameAchievementColumnVerticalAlignments = this.OverviewSelectedGameAchievementColumnVerticalAlignments != null
                    ? new Dictionary<string, GridVerticalAlignment>(this.OverviewSelectedGameAchievementColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewSelectedGameAchievementColumnHeaderAlignments = this.OverviewSelectedGameAchievementColumnHeaderAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.OverviewSelectedGameAchievementColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnVisibility = this.SingleGameColumnVisibility != null
                    ? new Dictionary<string, bool>(this.SingleGameColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnWidths = this.SingleGameColumnWidths != null
                    ? new Dictionary<string, double>(this.SingleGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnOrder = this.SingleGameColumnOrder != null
                    ? new Dictionary<string, int>(this.SingleGameColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnAlignments = this.SingleGameColumnAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.SingleGameColumnAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnVerticalAlignments = this.SingleGameColumnVerticalAlignments != null
                    ? new Dictionary<string, GridVerticalAlignment>(this.SingleGameColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnHeaderAlignments = this.SingleGameColumnHeaderAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.SingleGameColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                DesktopThemeColumnWidths = this.DesktopThemeColumnWidths != null
                    ? new Dictionary<string, double>(this.DesktopThemeColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                DesktopThemeColumnOrder = this.DesktopThemeColumnOrder != null
                    ? new Dictionary<string, int>(this.DesktopThemeColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                DesktopThemeColumnAlignments = this.DesktopThemeColumnAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.DesktopThemeColumnAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                DesktopThemeColumnVerticalAlignments = this.DesktopThemeColumnVerticalAlignments != null
                    ? new Dictionary<string, GridVerticalAlignment>(this.DesktopThemeColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase),
                DesktopThemeColumnHeaderAlignments = this.DesktopThemeColumnHeaderAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.DesktopThemeColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewGameSummariesColumnVisibility = this.OverviewGameSummariesColumnVisibility != null
                    ? new Dictionary<string, bool>(this.OverviewGameSummariesColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                OverviewGameSummariesColumnWidths = this.OverviewGameSummariesColumnWidths != null
                    ? new Dictionary<string, double>(this.OverviewGameSummariesColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                OverviewGameSummariesColumnOrder = this.OverviewGameSummariesColumnOrder != null
                    ? new Dictionary<string, int>(this.OverviewGameSummariesColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                OverviewGameSummariesColumnAlignments = this.OverviewGameSummariesColumnAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.OverviewGameSummariesColumnAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewGameSummariesColumnVerticalAlignments = this.OverviewGameSummariesColumnVerticalAlignments != null
                    ? new Dictionary<string, GridVerticalAlignment>(this.OverviewGameSummariesColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewGameSummariesColumnHeaderAlignments = this.OverviewGameSummariesColumnHeaderAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.OverviewGameSummariesColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                StartPageAchievementColumnVisibility = this.StartPageAchievementColumnVisibility != null
                    ? new Dictionary<string, bool>(this.StartPageAchievementColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                StartPageAchievementColumnWidths = this.StartPageAchievementColumnWidths != null
                    ? new Dictionary<string, double>(this.StartPageAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                StartPageAchievementColumnOrder = this.StartPageAchievementColumnOrder != null
                    ? new Dictionary<string, int>(this.StartPageAchievementColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                StartPageAchievementColumnAlignments = this.StartPageAchievementColumnAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.StartPageAchievementColumnAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                StartPageAchievementColumnVerticalAlignments = this.StartPageAchievementColumnVerticalAlignments != null
                    ? new Dictionary<string, GridVerticalAlignment>(this.StartPageAchievementColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase),
                StartPageAchievementColumnHeaderAlignments = this.StartPageAchievementColumnHeaderAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.StartPageAchievementColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                StartPageGameSummariesColumnVisibility = this.StartPageGameSummariesColumnVisibility != null
                    ? new Dictionary<string, bool>(this.StartPageGameSummariesColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                StartPageGameSummariesColumnWidths = this.StartPageGameSummariesColumnWidths != null
                    ? new Dictionary<string, double>(this.StartPageGameSummariesColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                StartPageGameSummariesColumnOrder = this.StartPageGameSummariesColumnOrder != null
                    ? new Dictionary<string, int>(this.StartPageGameSummariesColumnOrder, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                StartPageGameSummariesColumnAlignments = this.StartPageGameSummariesColumnAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.StartPageGameSummariesColumnAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                StartPageGameSummariesColumnVerticalAlignments = this.StartPageGameSummariesColumnVerticalAlignments != null
                    ? new Dictionary<string, GridVerticalAlignment>(this.StartPageGameSummariesColumnVerticalAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase),
                StartPageGameSummariesColumnHeaderAlignments = this.StartPageGameSummariesColumnHeaderAlignments != null
                    ? new Dictionary<string, GridAlignment>(this.StartPageGameSummariesColumnHeaderAlignments, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase),
                OverviewLeftColumnRatio = this.OverviewLeftColumnRatio,
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

                DisabledRealtimeNotificationGameIds = this.DisabledRealtimeNotificationGameIds != null
                    ? new HashSet<Guid>(this.DisabledRealtimeNotificationGameIds)
                    : new HashSet<Guid>(),
                PreferredProviderOverrides = this.PreferredProviderOverrides != null
                    ? new Dictionary<Guid, string>(this.PreferredProviderOverrides)
                    : new Dictionary<Guid, string>(),
                ExtraLocalPaths = this.ExtraLocalPaths,
                ExcludedLocalPaths = this.ExcludedLocalPaths,
                LastAllGamesCollectorScore = this.LastAllGamesCollectorScore,
                LastAllGamesCollectorLevel = this.LastAllGamesCollectorLevel,
                LastAllGamesCollectorLevelProgress = this.LastAllGamesCollectorLevelProgress,
                LastAllGamesCollectorRank = this.LastAllGamesCollectorRank,
                LastAllGamesPrestigeScore = this.LastAllGamesPrestigeScore,
                LastAllGamesPrestigeLevel = this.LastAllGamesPrestigeLevel,
                LastAllGamesPrestigeLevelProgress = this.LastAllGamesPrestigeLevelProgress,
                LastAllGamesPrestigeRank = this.LastAllGamesPrestigeRank,
                DefaultAchievementSortMode = this.DefaultAchievementSortMode,
                DefaultAchievementSortDescending = this.DefaultAchievementSortDescending,
                CustomSortPath = this.CustomSortPath,
                CustomSortDescending = this.CustomSortDescending,
                GamesOverviewCustomSortPath = this.GamesOverviewCustomSortPath,
                GamesOverviewCustomSortDescending = this.GamesOverviewCustomSortDescending,
                GamesOverviewCustomSecondarySorts = this.GamesOverviewCustomSecondarySorts,
                GamesOverviewCustomSortUsesSourceOrder = this.GamesOverviewCustomSortUsesSourceOrder,
                RecentAchievementsCustomSortPath = this.RecentAchievementsCustomSortPath,
                RecentAchievementsCustomSortDescending = this.RecentAchievementsCustomSortDescending,
                RecentAchievementsCustomSecondarySorts = this.RecentAchievementsCustomSecondarySorts,
                RecentAchievementsCustomSortUsesSourceOrder = this.RecentAchievementsCustomSortUsesSourceOrder,
                SidebarAllAchievementsCustomSortPath = this.SidebarAllAchievementsCustomSortPath,
                SidebarAllAchievementsCustomSortDescending = this.SidebarAllAchievementsCustomSortDescending,
                SidebarAllAchievementsCustomSecondarySorts = this.SidebarAllAchievementsCustomSecondarySorts,
                SidebarAllAchievementsCustomSortUsesSourceOrder = this.SidebarAllAchievementsCustomSortUsesSourceOrder,
                SidebarSelectedGameCustomSortPath = this.SidebarSelectedGameCustomSortPath,
                SidebarSelectedGameCustomSortDescending = this.SidebarSelectedGameCustomSortDescending,
                SidebarSelectedGameCustomSecondarySorts = this.SidebarSelectedGameCustomSecondarySorts,
                SidebarSelectedGameCustomSortUsesSourceOrder = this.SidebarSelectedGameCustomSortUsesSourceOrder,
                DefaultUnlockNotificationStyle = this.DefaultUnlockNotificationStyle,
                ProviderUnlockNotificationStyles = this.ProviderUnlockNotificationStyles != null
                    ? new Dictionary<string, string>(this.ProviderUnlockNotificationStyles, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                LastUpstreamReleaseNotificationVersion = this.LastUpstreamReleaseNotificationVersion,
                LastForkReleaseNotificationVersion = this.LastForkReleaseNotificationVersion,
                EnableGridTextWrapping = this.EnableGridTextWrapping,
                EnableCompactGridMode = this.EnableCompactGridMode,
                SidebarDefaultRefreshModeKey = this.SidebarDefaultRefreshModeKey,
                SidebarDefaultPlayStatusFilter = this.SidebarDefaultPlayStatusFilter,
                OverviewProviderFilterKeys = this.OverviewProviderFilterKeys,
                OverviewCompletenessFilterKeys = this.OverviewCompletenessFilterKeys,
                OverviewPlayStatusFilterKeys = this.OverviewPlayStatusFilterKeys,
                GamesOverviewGridSortMode = this.GamesOverviewGridSortMode,
                GamesOverviewGridSortDescending = this.GamesOverviewGridSortDescending,
                SidebarSelectedGameGridSortMode = this.SidebarSelectedGameGridSortMode,
                SidebarSelectedGameGridSortDescending = this.SidebarSelectedGameGridSortDescending,

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
            PreserveAchievementIconResolution = defaults.PreserveAchievementIconResolution;
            UseSeparateLockedIconsWhenAvailable = defaults.UseSeparateLockedIconsWhenAvailable;
            SeparateLockedIconEnabledGameIds = new HashSet<Guid>();
            ShowRarityGlow = defaults.ShowRarityGlow;
            UseUniformRarityBadges = defaults.UseUniformRarityBadges;
            UseCoverImages = defaults.UseCoverImages;

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
            ShowOverviewGameMetadata = defaults.ShowOverviewGameMetadata;
            ShowTopMenuBarButton = defaults.ShowTopMenuBarButton;
            ShowCompactListRarityBar = defaults.ShowCompactListRarityBar;
            ShowCompletionBorder = defaults.ShowCompletionBorder;

            ShowOverviewGameSummariesGridColumnHeaders = defaults.ShowOverviewGameSummariesGridColumnHeaders;
            ShowOverviewRecentAchievementsGridColumnHeaders = defaults.ShowOverviewRecentAchievementsGridColumnHeaders;
            ShowOverviewSelectedGameGridColumnHeaders = defaults.ShowOverviewSelectedGameGridColumnHeaders;
            ShowDesktopThemeAchievementGridColumnHeaders = defaults.ShowDesktopThemeAchievementGridColumnHeaders;
            GridColumnHeaderAlignment = defaults.GridColumnHeaderAlignment;
            GridCellAlignment = defaults.GridCellAlignment;
            GridCellVerticalAlignment = defaults.GridCellVerticalAlignment;

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
            OverviewGameSummariesGridSortMode = defaults.OverviewGameSummariesGridSortMode;
            OverviewGameSummariesGridSortDescending = defaults.OverviewGameSummariesGridSortDescending;
            OverviewSelectedGameGridSortMode = defaults.OverviewSelectedGameGridSortMode;
            OverviewSelectedGameGridSortDescending = defaults.OverviewSelectedGameGridSortDescending;
            SingleGameGridSortMode = defaults.SingleGameGridSortMode;
            SingleGameGridSortDescending = defaults.SingleGameGridSortDescending;
            AchievementDataGridSortMode = defaults.AchievementDataGridSortMode;
            AchievementDataGridSortDescending = defaults.AchievementDataGridSortDescending;

            AchievementDataGridMaxHeight = defaults.AchievementDataGridMaxHeight;
            SingleGameGridRowHeight = defaults.SingleGameGridRowHeight;
            OverviewGameSummariesGridRowHeight = defaults.OverviewGameSummariesGridRowHeight;
            OverviewRecentAchievementsGridRowHeight = defaults.OverviewRecentAchievementsGridRowHeight;
            OverviewSelectedGameGridRowHeight = defaults.OverviewSelectedGameGridRowHeight;
            StartPageGameSummariesGridRowHeight = defaults.StartPageGameSummariesGridRowHeight;
            StartPageRecentAchievementsGridRowHeight = defaults.StartPageRecentAchievementsGridRowHeight;
            DesktopThemeAchievementGridRowHeight = defaults.DesktopThemeAchievementGridRowHeight;
            SingleGameGridMaxRows = defaults.SingleGameGridMaxRows;
            OverviewGameSummariesGridMaxRows = defaults.OverviewGameSummariesGridMaxRows;
            OverviewRecentAchievementsGridMaxRows = defaults.OverviewRecentAchievementsGridMaxRows;
            OverviewSelectedGameGridMaxRows = defaults.OverviewSelectedGameGridMaxRows;
            StartPageGameSummariesGridMaxRows = defaults.StartPageGameSummariesGridMaxRows;
            StartPageRecentAchievementsGridMaxRows = defaults.StartPageRecentAchievementsGridMaxRows;
            DesktopThemeAchievementGridMaxRows = defaults.DesktopThemeAchievementGridMaxRows;

            StartPageGameSummariesGrid = new StartPageGameSummariesGridSettings();
            StartPageRecentUnlocksGrid = new StartPageRecentUnlocksGridSettings();
            StartPagePieCharts = new StartPagePieWidgetSettings();
            StartPageActivityScope = defaults.StartPageActivityScope;
            StartPageProgressScope = defaults.StartPageProgressScope;

            DataGridColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            DataGridColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            DataGridColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            OverviewRecentAchievementColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            OverviewRecentAchievementColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            OverviewRecentAchievementColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            OverviewRecentAchievementColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewRecentAchievementColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewRecentAchievementColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewSelectedGameAchievementColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            OverviewSelectedGameAchievementColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            OverviewSelectedGameAchievementColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            OverviewSelectedGameAchievementColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewSelectedGameAchievementColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewSelectedGameAchievementColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            SingleGameColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            SingleGameColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            SingleGameColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            SingleGameColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            SingleGameColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            SingleGameColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            DesktopThemeColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            DesktopThemeColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            DesktopThemeColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            DesktopThemeColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            DesktopThemeColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewGameSummariesColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            OverviewGameSummariesColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            OverviewGameSummariesColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            OverviewGameSummariesColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            OverviewGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            StartPageAchievementColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            StartPageAchievementColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            StartPageAchievementColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            StartPageAchievementColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            StartPageAchievementColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            StartPageAchievementColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            StartPageGameSummariesColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            StartPageGameSummariesColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            StartPageGameSummariesColumnOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            StartPageGameSummariesColumnAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            StartPageGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            StartPageGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);

            OverviewLeftColumnRatio = defaults.OverviewLeftColumnRatio;
            ViewAchievementsTimelineRange = defaults.ViewAchievementsTimelineRange;
            ViewAchievementsTimelineVisible = defaults.ViewAchievementsTimelineVisible;
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

        private static Dictionary<string, int> NormalizeColumnOrder(Dictionary<string, int> value)
        {
            var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && pair.Value >= 0)
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }

        private static Dictionary<string, GridAlignment> NormalizeColumnAlignments(Dictionary<string, GridAlignment> value)
        {
            var normalized = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) &&
                    Enum.IsDefined(typeof(GridAlignment), pair.Value))
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }

        private static Dictionary<string, GridVerticalAlignment> NormalizeColumnVerticalAlignments(
            Dictionary<string, GridVerticalAlignment> value)
        {
            var normalized = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) &&
                    Enum.IsDefined(typeof(GridVerticalAlignment), pair.Value))
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }

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
                var order = Services.AchievementOrderHelper.NormalizeApiNames(pair.Value);
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
                        var categoryType = Services.AchievementCategoryTypeHelper.Normalize(categoryTypePair.Value);
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


