using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
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

        private string _steamUserId;
        private string _gogUserId;
        private string _exophaseUserId;
        private string _epicAccountId;
        private string _epicAccessToken;
        private string _epicRefreshToken;
        private string _epicTokenType;
        private DateTime? _epicTokenExpiryUtc;
        private DateTime? _epicRefreshTokenExpiryUtc;
        private string _steamApiKey;
        private string _psnNpsso = string.Empty;
        private string _globalLanguage = "english";
        private bool _steamEnabled = true;
        private bool _epicEnabled = true;
        private bool _gogEnabled = true;
        private bool _psnEnabled = true;
        private bool _retroAchievementsEnabled = true;
        private bool _xboxEnabled = true;
        private bool _xboxLowResIcons = false;
        private bool _shadPS4Enabled = true;
        private string _shadPS4GameDataPath = string.Empty;
        private bool _rpcs3Enabled = true;
        private string _rpcs3ExecutablePath = string.Empty;
        private bool _xeniaEnabled = true;
        private string _xeniaAccountPath = string.Empty;
        private string _legacyManualImportPath = string.Empty;
        private bool _manualTrackingOverrideEnabled = false;
        private bool _enablePeriodicUpdates = true;
        private bool _autoExcludeHiddenGames = false;
        private int _periodicUpdateHours = 6;
        private bool _manualEnabled = true;
        private bool _enableNotifications = true;
        private bool _notifyPeriodicUpdates = true;
        private bool _notifyOnRebuild = true;
        private int _recentRefreshGamesCount = 10;
        private bool _showHiddenIcon = false;
        private bool _showHiddenTitle = false;
        private bool _showHiddenDescription = false;
        private bool _showHiddenSuffix = true;
        private bool _showLockedIcon = true;
        private bool _showRarityGlow = true;
        private bool _useCoverImages = true;
        private bool _includeUnplayedGames = true;
        private bool _showSidebarPieCharts = true;
        private bool _showSidebarGamesPieChart = true;
        private bool _showSidebarProviderPieChart = true;
        private bool _showSidebarRarityPieChart = true;
        private bool _showSidebarTrophyPieChart = true;
        private bool _sidebarPieChartVisibilityInitializedFromIndividualSettings;
        private bool _showSidebarBarCharts = true;
        private bool _showGamesWithNoUnlocks = false;
        private bool _showUnplayedGames = false;
        private bool _showTopMenuBarButton = true;
        private bool _showCompactListRarityBar = true;
        private bool _enableCompactGridMode = false;
        private double? _achievementDataGridMaxHeight = null;
        private bool _enableParallelProviderRefresh = true;
        private int _scanDelayMs = 200;
        private int _maxRetryAttempts = 3;
        private List<CustomRefreshPreset> _customRefreshPresets = new List<CustomRefreshPreset>();

        private string _raUsername;
        private string _raWebApiKey;
        private string _raRarityStats = "casual";
        private string _raPointsMode = "points";
        private int _hashIndexMaxAgeDays = 30;
        private bool _enableArchiveScanning = true;
        private bool _enableDiscHashing = true;
        private bool _enableRaNameFallback = true;
        private Dictionary<Guid, int> _raGameIdOverrides = new Dictionary<Guid, int>();
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
        private Dictionary<Guid, string> _manualCapstones = new Dictionary<Guid, string>();
        private Dictionary<Guid, List<string>> _achievementOrderOverrides = new Dictionary<Guid, List<string>>();
        private Dictionary<Guid, Dictionary<string, string>> _achievementCategoryOverrides =
            new Dictionary<Guid, Dictionary<string, string>>();
        private Dictionary<Guid, Dictionary<string, string>> _achievementCategoryTypeOverrides =
            new Dictionary<Guid, Dictionary<string, string>>();
        private Dictionary<Guid, ManualAchievementLink> _manualAchievementLinks = new Dictionary<Guid, ManualAchievementLink>();
        private Dictionary<string, ThemeMigrationCacheEntry> _themeMigrationVersionCache =
            new Dictionary<string, ThemeMigrationCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private TaggingSettings _taggingSettings;
        private bool _exophaseEnabled = false;
        private HashSet<string> _exophaseManagedPlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<Guid> _exophaseIncludedGames = new HashSet<Guid>();
        private Dictionary<Guid, string> _exophaseSlugOverrides = new Dictionary<Guid, string>();

        #endregion

        #region Steam Settings

        public string SteamUserId
        {
            get => _steamUserId;
            set => SetValue(ref _steamUserId, value);
        }

        public string GogUserId
        {
            get => _gogUserId;
            set => SetValue(ref _gogUserId, value);
        }

        public string ExophaseUserId
        {
            get => _exophaseUserId;
            set => SetValue(ref _exophaseUserId, value);
        }

        public string EpicAccountId
        {
            get => _epicAccountId;
            set => SetValue(ref _epicAccountId, value);
        }

        public string EpicAccessToken
        {
            get => _epicAccessToken;
            set => SetValue(ref _epicAccessToken, value);
        }

        public string EpicRefreshToken
        {
            get => _epicRefreshToken;
            set => SetValue(ref _epicRefreshToken, value);
        }

        public string EpicTokenType
        {
            get => _epicTokenType;
            set => SetValue(ref _epicTokenType, value);
        }

        public DateTime? EpicTokenExpiryUtc
        {
            get => _epicTokenExpiryUtc;
            set => SetValue(ref _epicTokenExpiryUtc, value);
        }

        public DateTime? EpicRefreshTokenExpiryUtc
        {
            get => _epicRefreshTokenExpiryUtc;
            set => SetValue(ref _epicRefreshTokenExpiryUtc, value);
        }

        /// <summary>
        /// Optional Steam Web API key used for owned games, friends and player summaries.
        /// </summary>
        public string SteamApiKey
        {
            get => _steamApiKey;
            set => SetValue(ref _steamApiKey, value ?? string.Empty);
        }

        /// <summary>
        /// NPSSO cookie value for PlayStation Network authentication.
        /// Can be obtained from https://ca.account.sony.com/api/v1/ssocookie
        /// </summary>
        public string PsnNpsso
        {
            get => _psnNpsso;
            set => SetValue(ref _psnNpsso, value ?? string.Empty);
        }

        #endregion

        #region Provider Enable/Disable Settings

        /// <summary>
        /// Global language for achievement text, used by all providers that support localization.
        /// </summary>
        public string GlobalLanguage
        {
            get => _globalLanguage;
            set => SetValue(ref _globalLanguage, value);
        }

        /// <summary>
        /// Enable or disable Steam achievement scanning.
        /// </summary>
        public bool SteamEnabled
        {
            get => _steamEnabled;
            set => SetValue(ref _steamEnabled, value);
        }

        /// <summary>
        /// Enable or disable Epic Games achievement scanning.
        /// </summary>
        public bool EpicEnabled
        {
            get => _epicEnabled;
            set => SetValue(ref _epicEnabled, value);
        }

        /// <summary>
        /// Enable or disable GOG achievement scanning.
        /// </summary>
        public bool GogEnabled
        {
            get => _gogEnabled;
            set => SetValue(ref _gogEnabled, value);
        }

        /// <summary>
        /// Enable or disable PlayStation achievement scanning.
        /// </summary>
        public bool PsnEnabled
        {
            get => _psnEnabled;
            set => SetValue(ref _psnEnabled, value);
        }

        /// <summary>
        /// Enable or disable RetroAchievements scanning.
        /// </summary>
        public bool RetroAchievementsEnabled
        {
            get => _retroAchievementsEnabled;
            set => SetValue(ref _retroAchievementsEnabled, value);
        }

        /// <summary>
        /// Enable or disable Xbox achievement scanning.
        /// </summary>
        public bool XboxEnabled
        {
            get => _xboxEnabled;
            set => SetValue(ref _xboxEnabled, value);
        }

        /// <summary>
        /// When true, requests smaller 128px icons from Xbox CDN to improve download speed.
        /// Enable if Xbox icon downloads are slow.
        /// </summary>
        public bool XboxLowResIcons
        {
            get => _xboxLowResIcons;
            set => SetValue(ref _xboxLowResIcons, value);
        }

        /// <summary>
        /// Enable or disable ShadPS4 achievement scanning.
        /// </summary>
        public bool ShadPS4Enabled
        {
            get => _shadPS4Enabled;
            set => SetValue(ref _shadPS4Enabled, value);
        }

        /// <summary>
        /// Path to the ShadPS4 user/game_data folder containing trophy data.
        /// </summary>
        public string ShadPS4GameDataPath
        {
            get => _shadPS4GameDataPath;
            set => SetValue(ref _shadPS4GameDataPath, value ?? string.Empty);
        }

        /// <summary>
        /// Enable or disable RPCS3 trophy tracking.
        /// </summary>
        public bool Rpcs3Enabled
        {
            get => _rpcs3Enabled;
            set => SetValue(ref _rpcs3Enabled, value);
        }

        /// <summary>
        /// Enable or disable Xenia trophy tracking.
        /// </summary>
        public bool XeniaEnabled
        {
            get => _xeniaEnabled;
            set => SetValue(ref _xeniaEnabled, value);
        }

        /// <summary>
        /// Xenia Account Path.
        /// </summary>
        public string XeniaAccountPath
        {
            get => _xeniaAccountPath;
            set => SetValue(ref _xeniaAccountPath, value ?? string.Empty);
        }

        /// <summary>
        /// Enable or disable Manual achievement tracking.
        /// </summary>
        public bool ManualEnabled
        {
            get => _manualEnabled;
            set => SetValue(ref _manualEnabled, value);
        }

        /// <summary>
        /// Path to the RPCS3 executable (rpcs3.exe).
        /// The installation folder is derived from this path.
        /// </summary>
        public string Rpcs3ExecutablePath
        {
            get => _rpcs3ExecutablePath;
            set => SetValue(ref _rpcs3ExecutablePath, value ?? string.Empty);
        }

        /// <summary>
        /// Folder path containing legacy SuccessStory JSON files used for manual link import.
        /// </summary>
        public string LegacyManualImportPath
        {
            get => _legacyManualImportPath;
            set => SetValue(ref _legacyManualImportPath, NormalizePath(value));
        }

        /// <summary>
        /// Allow manual tracking for games that already have non-manual provider data.
        /// </summary>
        public bool ManualTrackingOverrideEnabled
        {
            get => _manualTrackingOverrideEnabled;
            set => SetValue(ref _manualTrackingOverrideEnabled, value);
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
        /// When true, locked achievement icons are shown in grayscale.
        /// When false, locked achievement icons are hidden with a placeholder until revealed.
        /// </summary>
        public bool ShowLockedIcon
        {
            get => _showLockedIcon;
            set => SetValue(ref _showLockedIcon, value);
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
        /// When true, shows the timeline bar chart at the bottom of the right sidebar.
        /// When false, the achievements list takes the full space.
        /// </summary>
        public bool ShowSidebarBarCharts
        {
            get => _showSidebarBarCharts;
            set => SetValue(ref _showSidebarBarCharts, value);
        }

        /// <summary>
        /// When true, shows games with no unlocked achievements in the sidebar game list.
        /// </summary>
        public bool ShowGamesWithNoUnlocks
        {
            get => _showGamesWithNoUnlocks;
            set => SetValue(ref _showGamesWithNoUnlocks, value);
        }

        /// <summary>
        /// When true, shows unplayed games in the sidebar game list.
        /// </summary>
        public bool ShowUnplayedGames
        {
            get => _showUnplayedGames;
            set => SetValue(ref _showUnplayedGames, value);
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
        /// When true, shared achievement DataGrid rows use a tighter compact layout.
        /// </summary>
        public bool EnableCompactGridMode
        {
            get => _enableCompactGridMode;
            set => SetValue(ref _enableCompactGridMode, value);
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

        #region RetroAchievements Settings

        public string RaUsername
        {
            get => _raUsername;
            set => SetValue(ref _raUsername, value ?? string.Empty);
        }

        public string RaWebApiKey
        {
            get => _raWebApiKey;
            set => SetValue(ref _raWebApiKey, value ?? string.Empty);
        }

        public string RaRarityStats
        {
            get => _raRarityStats;
            set
            {
                var mode = (value ?? string.Empty).Trim();
                if (string.Equals(mode, "hardcore", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "combined", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "casual", StringComparison.OrdinalIgnoreCase))
                {
                    SetValue(ref _raRarityStats, mode.ToLowerInvariant());
                }
                else
                {
                    SetValue(ref _raRarityStats, "casual");
                }
            }
        }

        /// <summary>
        /// Determines which points value to display for RetroAchievements:
        /// "points" for standard points, "scaled" for TrueRatio (weighted by rarity).
        /// </summary>
        public string RaPointsMode
        {
            get => _raPointsMode;
            set
            {
                var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
                SetValue(ref _raPointsMode,
                    (mode == "scaled" || mode == "points") ? mode : "points");
            }
        }

        public int HashIndexMaxAgeDays
        {
            get => _hashIndexMaxAgeDays;
            set => SetValue(ref _hashIndexMaxAgeDays, Math.Max(1, value));
        }

        public bool EnableArchiveScanning
        {
            get => _enableArchiveScanning;
            set => SetValue(ref _enableArchiveScanning, value);
        }

        public bool EnableDiscHashing
        {
            get => _enableDiscHashing;
            set => SetValue(ref _enableDiscHashing, value);
        }

        /// <summary>
        /// Enable name-based fallback for RetroAchievements when hash matching fails.
        /// </summary>
        public bool EnableRaNameFallback
        {
            get => _enableRaNameFallback;
            set => SetValue(ref _enableRaNameFallback, value);
        }

        /// <summary>
        /// Manual overrides for RetroAchievements game IDs.
        /// Key is Playnite Game ID, value is RetroAchievements game ID.
        /// Used when automatic hash-based or name-based matching fails.
        /// </summary>
        public Dictionary<Guid, int> RaGameIdOverrides
        {
            get => _raGameIdOverrides;
            set => SetValue(ref _raGameIdOverrides, value ?? new Dictionary<Guid, int>());
        }

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
        public HashSet<Guid> ExcludedGameIds
        {
            get => _excludedGameIds;
            set => SetValue(ref _excludedGameIds, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Game IDs that are excluded from all summary surfaces.
        /// These exclusions persist across cache clears.
        /// </summary>
        public HashSet<Guid> ExcludedFromSummariesGameIds
        {
            get => _excludedFromSummariesGameIds;
            set => SetValue(ref _excludedFromSummariesGameIds, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Manual capstone selections. Key = Playnite Game ID, Value = Achievement ApiName.
        /// These selections persist across cache clears.
        /// </summary>
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
        public Dictionary<Guid, Dictionary<string, string>> AchievementCategoryTypeOverrides
        {
            get => _achievementCategoryTypeOverrides;
            set => SetValue(ref _achievementCategoryTypeOverrides, NormalizeAchievementCategoryTypeOverrides(value));
        }

        /// <summary>
        /// Manual achievement links. Key = Playnite Game ID, Value = ManualAchievementLink.
        /// Links any Playnite game to achievements from a source (e.g., Steam).
        /// Unlock times are stored here and persist across cache clears.
        /// </summary>
        public Dictionary<Guid, ManualAchievementLink> ManualAchievementLinks
        {
            get => _manualAchievementLinks;
            set => SetValue(ref _manualAchievementLinks, value ?? new Dictionary<Guid, ManualAchievementLink>());
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

        #region Exophase Provider Settings

        /// <summary>
        /// Master toggle for the Exophase achievement provider.
        /// When false, ExophaseDataProvider will not claim any games.
        /// </summary>
        public bool ExophaseEnabled
        {
            get => _exophaseEnabled;
            set => SetValue(ref _exophaseEnabled, value);
        }

        /// <summary>
        /// Platform slugs that Exophase should automatically claim.
        /// Games on these platforms will use Exophase instead of modern providers.
        /// Valid values: "steam", "psn", "xbox", "gog", "epic", "ea", "blizzard", "nintendo", "retro"
        /// </summary>
        public HashSet<string> ExophaseManagedPlatforms
        {
            get => _exophaseManagedPlatforms;
            set => SetValue(ref _exophaseManagedPlatforms, value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Individual game IDs that should use Exophase even if their platform is not in ManagedPlatforms.
        /// Allows per-game override for platforms not globally enabled.
        /// </summary>
        public HashSet<Guid> ExophaseIncludedGames
        {
            get => _exophaseIncludedGames;
            set => SetValue(ref _exophaseIncludedGames, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Per-game Exophase slug overrides.
        /// Key is Playnite Game ID, value is the Exophase game slug (e.g., "game-name-gog").
        /// When set, this slug is used directly instead of auto-detection.
        /// </summary>
        public Dictionary<Guid, string> ExophaseSlugOverrides
        {
            get => _exophaseSlugOverrides;
            set => SetValue(ref _exophaseSlugOverrides, value ?? new Dictionary<Guid, string>());
        }

        #endregion

        #region Clone Method

        /// <summary>
        /// Creates a deep copy of this PersistedSettings instance.
        /// </summary>
        public PersistedSettings Clone()
        {
            return new PersistedSettings
            {
                SteamUserId = this.SteamUserId,
                GogUserId = this.GogUserId,
                EpicAccountId = this.EpicAccountId,
                EpicAccessToken = this.EpicAccessToken,
                EpicRefreshToken = this.EpicRefreshToken,
                EpicTokenType = this.EpicTokenType,
                EpicTokenExpiryUtc = this.EpicTokenExpiryUtc,
                EpicRefreshTokenExpiryUtc = this.EpicRefreshTokenExpiryUtc,
                SteamApiKey = this.SteamApiKey,
                PsnNpsso = this.PsnNpsso,
                GlobalLanguage = this.GlobalLanguage,
                SteamEnabled = this.SteamEnabled,
                EpicEnabled = this.EpicEnabled,
                GogEnabled = this.GogEnabled,
                PsnEnabled = this.PsnEnabled,
                RetroAchievementsEnabled = this.RetroAchievementsEnabled,
                XboxEnabled = this.XboxEnabled,
                XboxLowResIcons = this.XboxLowResIcons,
                ShadPS4Enabled = this.ShadPS4Enabled,
                ShadPS4GameDataPath = this.ShadPS4GameDataPath,
                Rpcs3Enabled = this.Rpcs3Enabled,
                Rpcs3ExecutablePath = this.Rpcs3ExecutablePath,
                XeniaEnabled = this.XeniaEnabled,
                XeniaAccountPath = this.XeniaAccountPath,
                LegacyManualImportPath = this.LegacyManualImportPath,
                ManualTrackingOverrideEnabled = this.ManualTrackingOverrideEnabled,
                EnablePeriodicUpdates = this.EnablePeriodicUpdates,
                AutoExcludeHiddenGames = this.AutoExcludeHiddenGames,
                PeriodicUpdateHours = this.PeriodicUpdateHours,
                EnableNotifications = this.EnableNotifications,
                NotifyPeriodicUpdates = this.NotifyPeriodicUpdates,
                NotifyOnRebuild = this.NotifyOnRebuild,
                RecentRefreshGamesCount = this.RecentRefreshGamesCount,
                CustomRefreshPresets = this.CustomRefreshPresets != null
                    ? new List<CustomRefreshPreset>(CustomRefreshPreset.NormalizePresets(this.CustomRefreshPresets, CustomRefreshPreset.MaxPresetCount))
                    : new List<CustomRefreshPreset>(),
                ShowHiddenIcon = this.ShowHiddenIcon,
                ShowHiddenTitle = this.ShowHiddenTitle,
                ShowHiddenDescription = this.ShowHiddenDescription,
                ShowHiddenSuffix = this.ShowHiddenSuffix,
                ShowRarityGlow = this.ShowRarityGlow,
                UseCoverImages = this.UseCoverImages,
                IncludeUnplayedGames = this.IncludeUnplayedGames,
                ShowSidebarPieCharts = this.ShowSidebarPieCharts,
                ShowSidebarGamesPieChart = this.ShowSidebarGamesPieChart,
                ShowSidebarProviderPieChart = this.ShowSidebarProviderPieChart,
                ShowSidebarRarityPieChart = this.ShowSidebarRarityPieChart,
                ShowSidebarTrophyPieChart = this.ShowSidebarTrophyPieChart,
                ShowSidebarBarCharts = this.ShowSidebarBarCharts,
                ShowGamesWithNoUnlocks = this.ShowGamesWithNoUnlocks,
                ShowUnplayedGames = this.ShowUnplayedGames,
                ShowTopMenuBarButton = this.ShowTopMenuBarButton,
                EnableCompactGridMode = this.EnableCompactGridMode,
                EnableParallelProviderRefresh = this.EnableParallelProviderRefresh,
                ScanDelayMs = this.ScanDelayMs,
                MaxRetryAttempts = this.MaxRetryAttempts,
                RaUsername = this.RaUsername,
                RaWebApiKey = this.RaWebApiKey,
                RaRarityStats = this.RaRarityStats,
                RaPointsMode = this.RaPointsMode,
                HashIndexMaxAgeDays = this.HashIndexMaxAgeDays,
                EnableArchiveScanning = this.EnableArchiveScanning,
                EnableDiscHashing = this.EnableDiscHashing,
                EnableRaNameFallback = this.EnableRaNameFallback,
                RaGameIdOverrides = this.RaGameIdOverrides != null
                    ? new Dictionary<Guid, int>(this.RaGameIdOverrides)
                    : new Dictionary<Guid, int>(),
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
                ExcludedGameIds = this.ExcludedGameIds != null
                    ? new HashSet<Guid>(this.ExcludedGameIds)
                    : new HashSet<Guid>(),
                ExcludedFromSummariesGameIds = this.ExcludedFromSummariesGameIds != null
                    ? new HashSet<Guid>(this.ExcludedFromSummariesGameIds)
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
                ManualAchievementLinks = this.ManualAchievementLinks != null
                    ? this.ManualAchievementLinks.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value?.Clone())
                    : new Dictionary<Guid, ManualAchievementLink>(),
                ManualEnabled = this.ManualEnabled,
                TaggingSettings = this.TaggingSettings?.Clone() ?? new TaggingSettings(),
                ExophaseEnabled = this.ExophaseEnabled,
                ExophaseManagedPlatforms = this.ExophaseManagedPlatforms != null
                    ? new HashSet<string>(this.ExophaseManagedPlatforms, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ExophaseIncludedGames = this.ExophaseIncludedGames != null
                    ? new HashSet<Guid>(this.ExophaseIncludedGames)
                    : new HashSet<Guid>(),
                ExophaseSlugOverrides = this.ExophaseSlugOverrides != null
                    ? new Dictionary<Guid, string>(this.ExophaseSlugOverrides)
                    : new Dictionary<Guid, string>()
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

