using System;
using System.Collections.Generic;
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
    public class PersistedSettings : ObservableObject
    {
        public PersistedSettings()
        {
        }

        #region Backing Fields

        private string _globalLanguage = "english";
        private bool _enablePeriodicUpdates = true;
        private bool _autoExcludeHiddenGames = false;
        private int _periodicUpdateHours = 6;
        private bool _enableNotifications = true;
        private bool _notifyPeriodicUpdates = true;
        private bool _notifyOnRebuild = true;
        private string _lastUpstreamReleaseNotificationVersion = string.Empty;
        private int _recentRefreshGamesCount = 10;
        private bool _showHiddenIcon = false;
        private bool _showHiddenTitle = false;
        private bool _showHiddenDescription = false;
        private bool _showHiddenSuffix = true;
        private bool _showLockedIcon = true;
        private bool _preserveAchievementIconResolution = false;
        private bool _useSeparateLockedIconsWhenAvailable = false;
        private HashSet<Guid> _separateLockedIconEnabledGameIds = new HashSet<Guid>();
        private bool _showRarityGlow = true;
        private bool _useCoverImages = true;
        private bool _includeUnplayedGames = true;
        private bool _showSidebarPieCharts = true;
        private bool _showSidebarGamesPieChart = true;
        private bool _showSidebarProviderPieChart = true;
        private bool _showSidebarRarityPieChart = true;
        private bool _showSidebarTrophyPieChart = true;
        private bool _showSidebarPiePercentages = true;
        private SidebarPieSmallSliceMode _sidebarPieSmallSliceMode = SidebarPieSmallSliceMode.Round;
        private bool _sidebarPieChartVisibilityInitializedFromIndividualSettings;
        private bool _showSidebarBarCharts = true;
        private bool _showSidebarGameMetadata = true;
        private bool _showTopMenuBarButton = true;
        private bool _showCompactListRarityBar = true;
        private bool _showCompletionBorder = true;
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
        private bool _enableCompactGridMode = false;
        private double? _achievementDataGridMaxHeight = null;
        private bool _enableParallelProviderRefresh = true;
        private int _scanDelayMs = 200;
        private int _maxRetryAttempts = 3;
        private List<CustomRefreshPreset> _customRefreshPresets = new List<CustomRefreshPreset>();
        private Dictionary<string, bool> _dataGridColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _dataGridColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _sidebarAchievementColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _sidebarGameColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _singleGameColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _desktopThemeColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _gamesOverviewColumnVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _gamesOverviewColumnWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private bool _firstTimeSetupCompleted = false;
        private bool _seenThemeMigration = false;
        private HashSet<Guid> _excludedGameIds = new HashSet<Guid>();
        private HashSet<Guid> _excludedFromSummariesGameIds = new HashSet<Guid>();
        private Dictionary<Guid, string> _preferredProviderOverrides = new Dictionary<Guid, string>();
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
        private string _extraLocalPaths = "";

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
        /// <summary>
        /// Semicolon-separated list of extra folders to scan for local achievements.json files.
        /// </summary>
        [JsonProperty("ExtraLocalPaths")]
        public string ExtraLocalPaths
        {
            get => _extraLocalPaths;
            set => SetValue(ref _extraLocalPaths, value ?? "");
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
        /// When true, Playnite hide/unhide actions automatically exclude/include games from tracking.
        /// Hiding also clears cached data for the game.
        /// </summary>
        public bool AutoExcludeHiddenGames
        {
            get => _autoExcludeHiddenGames;
            set => SetValue(ref _autoExcludeHiddenGames, value);
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

        /// <summary>
        /// Last upstream release version already shown to the user.
        /// </summary>
        public string LastUpstreamReleaseNotificationVersion
        {
            get => _lastUpstreamReleaseNotificationVersion;
            set => SetValue(ref _lastUpstreamReleaseNotificationVersion, value ?? string.Empty);
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
        /// Legacy aggregate toggle for sidebar pie charts.
        /// New builds use per-chart visibility settings, but this is preserved for migration.
        /// </summary>
        public bool ShowSidebarPieCharts
        {
            get => _showSidebarPieCharts;
            set
            {
                if (_showSidebarPieCharts == value)
                {
                    return;
                }

                SetValue(ref _showSidebarPieCharts, value);
                if (_sidebarPieChartVisibilityInitializedFromIndividualSettings)
                {
                    return;
                }

                ShowSidebarGamesPieChart = value;
                ShowSidebarProviderPieChart = value;
                ShowSidebarRarityPieChart = value;
                ShowSidebarTrophyPieChart = value;
            }
        }

        /// <summary>
        /// When true, shows the completed-games pie chart in the sidebar.
        /// </summary>
        public bool ShowSidebarGamesPieChart
        {
            get => _showSidebarGamesPieChart;
            set
            {
                _sidebarPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showSidebarGamesPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the platform/provider pie chart in the sidebar.
        /// </summary>
        public bool ShowSidebarProviderPieChart
        {
            get => _showSidebarProviderPieChart;
            set
            {
                _sidebarPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showSidebarProviderPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the rarity pie chart in the sidebar.
        /// </summary>
        public bool ShowSidebarRarityPieChart
        {
            get => _showSidebarRarityPieChart;
            set
            {
                _sidebarPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showSidebarRarityPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the trophy pie chart in the sidebar.
        /// </summary>
        public bool ShowSidebarTrophyPieChart
        {
            get => _showSidebarTrophyPieChart;
            set
            {
                _sidebarPieChartVisibilityInitializedFromIndividualSettings = true;
                SetValue(ref _showSidebarTrophyPieChart, value);
            }
        }

        /// <summary>
        /// When true, shows the center percentage text on sidebar pie charts.
        /// </summary>
        public bool ShowSidebarPiePercentages
        {
            get => _showSidebarPiePercentages;
            set => SetValue(ref _showSidebarPiePercentages, value);
        }

        /// <summary>
        /// Determines how sidebar pie charts handle slices below five percent.
        /// </summary>
        public SidebarPieSmallSliceMode SidebarPieSmallSliceMode
        {
            get => _sidebarPieSmallSliceMode;
            set => SetValue(ref _sidebarPieSmallSliceMode, value);
        }

        /// <summary>
        /// When true, shows the timeline bar chart at the bottom of the right sidebar.
        /// When false, the achievements list takes the full space.
        /// </summary>
        public bool ShowSidebarBarCharts
        {
            get => _showSidebarBarCharts;
            set => SetValue(ref _showSidebarBarCharts, value);
        }

        /// <summary>
        /// When true, shows platform/playtime/region metadata under game names in the sidebar games overview.
        /// </summary>
        public bool ShowSidebarGameMetadata
        {
            get => _showSidebarGameMetadata;
            set => SetValue(ref _showSidebarGameMetadata, value);
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
        /// When true, completed games display a blue border in the games overview.
        /// </summary>
        public bool ShowCompletionBorder
        {
            get => _showCompletionBorder;
            set => SetValue(ref _showCompletionBorder, value);
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
        /// When true, shared achievement DataGrid rows use a tighter compact layout.
        /// </summary>
        public bool EnableCompactGridMode
        {
            get => _enableCompactGridMode;
            set => SetValue(ref _enableCompactGridMode, value);
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
        /// Maximum height for AchievementDataGrid controls (null = unlimited).
        /// </summary>
        public double? AchievementDataGridMaxHeight
        {
            get => _achievementDataGridMaxHeight;
            set => SetValue(ref _achievementDataGridMaxHeight, value);
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
        /// Persisted widths for sidebar achievement columns (recent + selected game panels).
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> SidebarAchievementColumnWidths
        {
            get => _sidebarAchievementColumnWidths;
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

                SetValue(ref _sidebarAchievementColumnWidths, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for the sidebar selected game achievement columns.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> SidebarGameColumnWidths
        {
            get => _sidebarGameColumnWidths;
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

                SetValue(ref _sidebarGameColumnWidths, normalized);
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
        /// Persisted visibility state for games overview columns in the sidebar.
        /// Key is a stable column identifier, value indicates whether the column is visible.
        /// </summary>
        public Dictionary<string, bool> GamesOverviewColumnVisibility
        {
            get => _gamesOverviewColumnVisibility;
            set
            {
                var normalized = value != null
                    ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                SetValue(ref _gamesOverviewColumnVisibility, normalized);
            }
        }

        /// <summary>
        /// Persisted widths for games overview columns in the sidebar.
        /// Key is a stable column identifier, value is pixel width.
        /// </summary>
        public Dictionary<string, double> GamesOverviewColumnWidths
        {
            get => _gamesOverviewColumnWidths;
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

                SetValue(ref _gamesOverviewColumnWidths, normalized);
            }
        }

        #endregion

        #region General Settings

        /// <summary>
        /// Indicates whether the user has completed the first-time setup flow.
        /// When false, the sidebar shows a landing page guiding users through initial configuration.
        /// </summary>
        public bool FirstTimeSetupCompleted
        {
            get => _firstTimeSetupCompleted;
            set => SetValue(ref _firstTimeSetupCompleted, value);
        }

        /// <summary>
        /// Indicates whether the user has seen the theme migration landing page.
        /// When false, the sidebar always shows the landing page to promote theme migration.
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
        /// Preferred provider override per game.
        /// Key = Playnite Game ID, Value = ProviderKey.
        /// These overrides persist across cache clears.
        /// </summary>
        public Dictionary<Guid, string> PreferredProviderOverrides
        {
            get => _preferredProviderOverrides;
            set => SetValue(ref _preferredProviderOverrides, NormalizePreferredProviderOverrides(value));
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
                AutoExcludeHiddenGames = this.AutoExcludeHiddenGames,
                PeriodicUpdateHours = this.PeriodicUpdateHours,
                RecentRefreshGamesCount = this.RecentRefreshGamesCount,
                CustomRefreshPresets = this.CustomRefreshPresets != null
                    ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(this.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                    : new List<CustomRefreshPreset>(),

                // Notification Settings
                EnableNotifications = this.EnableNotifications,
                NotifyPeriodicUpdates = this.NotifyPeriodicUpdates,
                NotifyOnRebuild = this.NotifyOnRebuild,
                LastUpstreamReleaseNotificationVersion = this.LastUpstreamReleaseNotificationVersion,

                // Display Preferences
                ShowHiddenIcon = this.ShowHiddenIcon,
                ShowHiddenTitle = this.ShowHiddenTitle,
                ShowHiddenDescription = this.ShowHiddenDescription,
                ShowHiddenSuffix = this.ShowHiddenSuffix,
                ShowLockedIcon = this.ShowLockedIcon,
                PreserveAchievementIconResolution = this.PreserveAchievementIconResolution,
                UseSeparateLockedIconsWhenAvailable = this.UseSeparateLockedIconsWhenAvailable,
                ShowRarityGlow = this.ShowRarityGlow,
                UseCoverImages = this.UseCoverImages,
                IncludeUnplayedGames = this.IncludeUnplayedGames,
                ShowSidebarPieCharts = this.ShowSidebarPieCharts,
                ShowSidebarGamesPieChart = this.ShowSidebarGamesPieChart,
                ShowSidebarProviderPieChart = this.ShowSidebarProviderPieChart,
                ShowSidebarRarityPieChart = this.ShowSidebarRarityPieChart,
                ShowSidebarTrophyPieChart = this.ShowSidebarTrophyPieChart,
                ShowSidebarPiePercentages = this.ShowSidebarPiePercentages,
                SidebarPieSmallSliceMode = this.SidebarPieSmallSliceMode,
                ShowSidebarBarCharts = this.ShowSidebarBarCharts,
                ShowSidebarGameMetadata = this.ShowSidebarGameMetadata,
                ShowTopMenuBarButton = this.ShowTopMenuBarButton,
                ShowCompactListRarityBar = this.ShowCompactListRarityBar,
                ShowCompletionBorder = this.ShowCompletionBorder,
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
                EnableCompactGridMode = this.EnableCompactGridMode,
                CompactListSortMode = this.CompactListSortMode,
                CompactListSortDescending = this.CompactListSortDescending,
                CompactUnlockedListSortMode = this.CompactUnlockedListSortMode,
                CompactUnlockedListSortDescending = this.CompactUnlockedListSortDescending,
                CompactLockedListSortMode = this.CompactLockedListSortMode,
                CompactLockedListSortDescending = this.CompactLockedListSortDescending,
                AchievementDataGridMaxHeight = this.AchievementDataGridMaxHeight,
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
                SidebarAchievementColumnWidths = this.SidebarAchievementColumnWidths != null
                    ? new Dictionary<string, double>(this.SidebarAchievementColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SidebarGameColumnWidths = this.SidebarGameColumnWidths != null
                    ? new Dictionary<string, double>(this.SidebarGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SingleGameColumnWidths = this.SingleGameColumnWidths != null
                    ? new Dictionary<string, double>(this.SingleGameColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                DesktopThemeColumnWidths = this.DesktopThemeColumnWidths != null
                    ? new Dictionary<string, double>(this.DesktopThemeColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                GamesOverviewColumnVisibility = this.GamesOverviewColumnVisibility != null
                    ? new Dictionary<string, bool>(this.GamesOverviewColumnVisibility, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                GamesOverviewColumnWidths = this.GamesOverviewColumnWidths != null
                    ? new Dictionary<string, double>(this.GamesOverviewColumnWidths, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),

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
                PreferredProviderOverrides = this.PreferredProviderOverrides != null
                    ? new Dictionary<Guid, string>(this.PreferredProviderOverrides)
                    : new Dictionary<Guid, string>(),
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

        private static string NormalizePath(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
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

        private static Dictionary<Guid, string> NormalizePreferredProviderOverrides(
            Dictionary<Guid, string> value)
        {
            var normalized = new Dictionary<Guid, string>();
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var providerKey = (pair.Value ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(providerKey))
                {
                    normalized[pair.Key] = providerKey;
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

