using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Sidebar;
using PlayniteAchievements.Services;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Models;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public class SidebarViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// Returns true if unplayed games are included during refreshes.
        /// </summary>
        public bool IncludeUnplayedGames => _settings?.Persisted?.IncludeUnplayedGames ?? true;

        private readonly RefreshRuntime _refreshService;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementDataService _achievementDataService;
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;

        private readonly SidebarDataBuilder _dataBuilder;

        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _refreshCts;
        private volatile bool _isActive;
        private int _refreshVersion;
        private bool _disposed;

        private readonly HashSet<string> _revealedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private SidebarDataSnapshot _latestSnapshot;

        private readonly object _progressLock = new object();
        private DateTime _lastProgressUpdate = DateTime.MinValue;
        private static readonly TimeSpan ProgressMinInterval = TimeSpan.FromMilliseconds(50);
        private const int ContextualPieSeriesCount = 5;
        private System.Windows.Threading.DispatcherTimer _refreshDebounceTimer;
        private System.Windows.Threading.DispatcherTimer _progressHideTimer;
        private System.Windows.Threading.DispatcherTimer _deltaBatchTimer;
        private bool _showCompletedProgress;
        private bool _refreshAttemptInProgress;
        private int _selectedGameLoadVersion;
        private bool _selectedGameLoadInProgress;
        private static readonly TimeSpan ProgressHideDelay = TimeSpan.FromSeconds(3);
        private readonly object _deltaSync = new object();
        private readonly HashSet<string> _pendingDeltaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _pendingFullResetFromDelta;

        private List<AchievementDisplayItem> _filteredRecentAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _allAchievements = new List<AchievementDisplayItem>();
        private List<string> _availableProviders = new List<string>();
        private readonly HashSet<string> _selectedProviderFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedCompletenessFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedPlayStatusFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedGameTypeFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedGameCategoryFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Sort state tracking for quick reverse
        private string _overviewSortPath;
        private ListSortDirection _overviewSortDirection;
        private string _recentSortPath;
        private ListSortDirection _recentSortDirection;
        private string _selectedGameSortPath;
        private ListSortDirection _selectedGameSortDirection;


        public SidebarViewModel(
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            AchievementDataService achievementDataService,
            RefreshEntryPoint refreshEntryPoint,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _refreshCoordinator = refreshEntryPoint ?? throw new ArgumentNullException(nameof(refreshEntryPoint));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;
            _dataBuilder = new SidebarDataBuilder(_achievementDataService, _refreshService.Providers, _playniteApi, _logger);

            // Initialize debounce timer
            _refreshDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _refreshDebounceTimer.Tick += OnRefreshDebounceTimerTick;

            _progressHideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = ProgressHideDelay
            };
            _progressHideTimer.Tick += OnProgressHideTimerTick;

            _deltaBatchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _deltaBatchTimer.Tick += OnDeltaBatchTimerTick;

            // Initialize collections
            AllAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            GamesOverview = new BulkObservableCollection<GameOverviewItem>();
            RecentAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            SelectedGameAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            SelectedGameTypeFilterOptions = new ObservableCollection<string>();
            SelectedGameCategoryFilterOptions = new ObservableCollection<string>();
            CompletenessFilterOptions = new ObservableCollection<string>();

            // Initialize refresh mode options from service (exclude LibrarySelected - context menu only)
            RefreshModes = new ObservableCollection<RefreshMode>(
                _refreshService.GetRefreshModes().Where(m => m.Type != RefreshModeType.LibrarySelected));

            GlobalTimeline = new TimelineViewModel();
            SelectedGameTimeline = new TimelineViewModel();

            GamesPieChart = new PieChartViewModel
            {
                AlwaysShowSmallSliceIcons = true
            };
            RarityPieChart = new PieChartViewModel
            {
                MinimumSeriesCount = ContextualPieSeriesCount
            };
            ProviderPieChart = new PieChartViewModel();
            TrophyPieChart = new PieChartViewModel
            {
                MinimumSeriesCount = ContextualPieSeriesCount
            };
            ApplySidebarPieSmallSliceMode();

            // Set defaults: Unlocked Only, sorted by Unlock Date
            _showUnlockedOnly = true;
            _sortIndex = 2; // Unlock Date

            // Initialize commands
            RefreshViewCommand = new AsyncCommand(_ => RefreshViewAsync());
            RefreshCommand = new AsyncCommand(_ => ExecuteRefreshAsync(), _ => CanExecuteRefresh());
            CancelRefreshCommand = new RelayCommand(_ => CancelRefresh(), _ => IsRefreshing);
            RevealAchievementCommand = new RelayCommand(param => RevealAchievement(param as AchievementDisplayItem));
            OpenGameInLibraryCommand = new RelayCommand(OpenGameInLibrary);
            OpenGameInSidebarCommand = new RelayCommand(OpenGameInSidebar);
            RefreshSingleGameCommand = new AsyncCommand(ExecuteSingleGameRefreshAsync);
            CloseViewCommand = new RelayCommand(_ => PlayniteUiProvider.RestoreMainView());
            ClearGameSelectionCommand = new RelayCommand(_ => ClearGameSelection());
            NavigateToGameCommand = new RelayCommand(param => NavigateToGame(param as GameOverviewItem));

            // Subscribe to progress events
            _refreshService.RebuildProgress += OnRebuildProgress;
            _refreshService.CacheDeltaUpdated += OnCacheDeltaUpdated;
            if (_settings != null)
            {
                _settings.PropertyChanged += OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
                }
            }

        }

        #region Collections

        public ObservableCollection<AchievementDisplayItem> AllAchievements { get; }

        // Overview tab collections
        public ObservableCollection<GameOverviewItem> GamesOverview { get; }
        public ObservableCollection<AchievementDisplayItem> RecentAchievements { get; }

        private List<GameOverviewItem> _allGamesOverview = new List<GameOverviewItem>();
        private List<GameOverviewItem> _filteredGamesOverview = new List<GameOverviewItem>();

        private List<AchievementDisplayItem> _allRecentAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _allSelectedGameAchievements = new List<AchievementDisplayItem>();

        #endregion

        #region Overview Tab Properties

        private string _leftSearchText = string.Empty;
        public string LeftSearchText
        {
            get => _leftSearchText;
            set
            {
                if (SetValueAndReturn(ref _leftSearchText, value ?? string.Empty))
                {
                    ApplyLeftFilters();
                }
            }
        }

        private string _rightSearchText = string.Empty;
        public string RightSearchText
        {
            get => _rightSearchText;
            set
            {
                if (SetValueAndReturn(ref _rightSearchText, value ?? string.Empty))
                {
                    ApplyRightFilters();
                }
            }
        }

        private ObservableCollection<string> _selectedGameTypeFilterOptions;
        public ObservableCollection<string> SelectedGameTypeFilterOptions
        {
            get => _selectedGameTypeFilterOptions;
            private set => SetValue(ref _selectedGameTypeFilterOptions, value);
        }

        public string SelectedGameTypeFilterText => GetSelectedFilterText(
            _selectedGameTypeFilters,
            SelectedGameTypeFilterOptions,
            L("LOCPlayAch_GameOptions_Category_TypeSelectorPlaceholder", "Type"));

        public bool IsSelectedGameTypeFilterSelected(string value)
        {
            return IsFilterSelected(_selectedGameTypeFilters, value);
        }

        public void SetSelectedGameTypeFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedGameTypeFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedGameTypeFilterText));
            ApplyRightFilters();
        }

        private ObservableCollection<string> _selectedGameCategoryFilterOptions;
        public ObservableCollection<string> SelectedGameCategoryFilterOptions
        {
            get => _selectedGameCategoryFilterOptions;
            private set => SetValue(ref _selectedGameCategoryFilterOptions, value);
        }

        public string SelectedGameCategoryFilterText => GetSelectedFilterText(
            _selectedGameCategoryFilters,
            SelectedGameCategoryFilterOptions,
            L("LOCPlayAch_GameOptions_Category_Filter_LabelSelectorPlaceholder", "Category"));

        public bool IsSelectedGameCategoryFilterSelected(string value)
        {
            return IsFilterSelected(_selectedGameCategoryFilters, value);
        }

        public void SetSelectedGameCategoryFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedGameCategoryFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedGameCategoryFilterText));
            ApplyRightFilters();
        }

        private bool _showSelectedGameUnlocked = true;
        public bool ShowSelectedGameUnlocked
        {
            get => _showSelectedGameUnlocked;
            set
            {
                if (SetValueAndReturn(ref _showSelectedGameUnlocked, value))
                {
                    ApplyRightFilters();
                }
            }
        }

        private bool _showSelectedGameLocked = true;
        public bool ShowSelectedGameLocked
        {
            get => _showSelectedGameLocked;
            set
            {
                if (SetValueAndReturn(ref _showSelectedGameLocked, value))
                {
                    ApplyRightFilters();
                }
            }
        }

        private bool _showSelectedGameHidden = true;
        public bool ShowSelectedGameHidden
        {
            get => _showSelectedGameHidden;
            set
            {
                if (SetValueAndReturn(ref _showSelectedGameHidden, value))
                {
                    ApplyRightFilters();
                }
            }
        }

        private bool _selectedGameHasCustomAchievementOrder;
        public bool SelectedGameHasCustomAchievementOrder
        {
            get => _selectedGameHasCustomAchievementOrder;
            private set => SetValue(ref _selectedGameHasCustomAchievementOrder, value);
        }

        private ObservableCollection<string> _providerFilterOptions;
        public ObservableCollection<string> ProviderFilterOptions
        {
            get => _providerFilterOptions;
            private set => SetValue(ref _providerFilterOptions, value);
        }

        public string SelectedProviderFilterText => GetSelectedProviderFilterText();

        public bool IsProviderFilterSelected(string providerKey)
        {
            return IsFilterSelected(_selectedProviderFilters, providerKey);
        }

        public void SetProviderFilterSelected(string providerKey, bool isSelected)
        {
            if (!SetFilterSelection(_selectedProviderFilters, providerKey, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedProviderFilterText));
            UpdateOverviewPieChartSelectionStates();
            // Defer filter application to avoid interfering with menu click handling.
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(() => ApplyLeftFilters()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        public string GetProviderFilterDisplayName(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return string.Empty;
            }

            var normalized = providerKey.Trim();
            var localized = ProviderRegistry.GetLocalizedName(normalized);
            return string.IsNullOrWhiteSpace(localized) ? normalized : localized;
        }

        public void ClearProviderFilters()
        {
            if (_selectedProviderFilters.Count == 0)
            {
                return;
            }

            _selectedProviderFilters.Clear();
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            UpdateOverviewPieChartSelectionStates();
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(() => ApplyLeftFilters()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        /// <summary>
        /// Toggles a provider filter when a pie slice is clicked.
        /// </summary>
        /// <param name="sliceLabel">The display label from the clicked slice</param>
        public void ToggleProviderFilterFromPieChart(string sliceLabel)
        {
            if (string.IsNullOrWhiteSpace(sliceLabel))
            {
                return;
            }

            // Check if "Locked" was clicked
            if (string.Equals(sliceLabel, L("LOCPlayAch_Sidebar_Locked", "Locked"), StringComparison.OrdinalIgnoreCase))
            {
                ClearProviderFilters();
                return;
            }

            // Get the provider key from the pie chart's label mapping
            var providerKey = ProviderPieChart.GetProviderKeyFromLabel(sliceLabel);
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            if (ProviderFilterOptions == null ||
                !ProviderFilterOptions.Any(p => string.Equals(p, providerKey, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var currentlySelected = IsProviderFilterSelected(providerKey);
            SetProviderFilterSelected(providerKey, !currentlySelected);
        }

        private ObservableCollection<string> _completenessFilterOptions;
        public ObservableCollection<string> CompletenessFilterOptions
        {
            get => _completenessFilterOptions;
            private set => SetValue(ref _completenessFilterOptions, value);
        }

        public string SelectedCompletenessFilterText => GetSelectedFilterText(
            _selectedCompletenessFilters,
            CompletenessFilterOptions,
            L("LOCPlayAch_Filter_CompletenessSelectorPlaceholder", "Completeness"));

        public bool IsCompletenessFilterSelected(string value)
        {
            return IsFilterSelected(_selectedCompletenessFilters, value);
        }

        public void SetCompletenessFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedCompletenessFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCompletenessFilterText));
            UpdateOverviewPieChartSelectionStates();
            // Defer filter application to avoid interfering with menu click handling.
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(() => ApplyLeftFilters()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private ObservableCollection<string> _playStatusFilterOptions;
        public ObservableCollection<string> PlayStatusFilterOptions
        {
            get => _playStatusFilterOptions;
            private set => SetValue(ref _playStatusFilterOptions, value);
        }

        public string SelectedPlayStatusFilterText => GetSelectedFilterText(
            _selectedPlayStatusFilters,
            PlayStatusFilterOptions,
            L("LOCPlayAch_Filter_PlayStatusSelectorPlaceholder", "Play Status"));

        public bool IsPlayStatusFilterSelected(string value)
        {
            return IsFilterSelected(_selectedPlayStatusFilters, value);
        }

        public void SetPlayStatusFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedPlayStatusFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedPlayStatusFilterText));
            // Defer filter application to avoid interfering with menu click handling.
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(() => { ApplyLeftFilters(); UpdateAggregatePieCharts(); }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        /// <summary>
        /// Selects exactly one completeness filter when a games pie slice is clicked.
        /// Pie chart clicks are binary: Complete or Incomplete, never both.
        /// </summary>
        /// <param name="completenessLabel">The completeness label from the clicked slice.</param>
        public void ToggleCompletenessFilterFromPieChart(string completenessLabel)
        {
            if (string.IsNullOrWhiteSpace(completenessLabel) ||
                CompletenessFilterOptions == null ||
                !CompletenessFilterOptions.Contains(completenessLabel))
            {
                return;
            }

            var isOnlySelected =
                _selectedCompletenessFilters.Count == 1 &&
                _selectedCompletenessFilters.Contains(completenessLabel);

            if (isOnlySelected)
            {
                _selectedCompletenessFilters.Clear();
            }
            else
            {
                _selectedCompletenessFilters.Clear();
                _selectedCompletenessFilters.Add(completenessLabel);
            }

            OnPropertyChanged(nameof(SelectedCompletenessFilterText));
            UpdateOverviewPieChartSelectionStates();
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(() => ApplyLeftFilters()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        public ObservableCollection<RefreshMode> RefreshModes { get; }

        private string _selectedRefreshMode = RefreshModeType.Installed.GetKey();
        public string SelectedRefreshMode
        {
            get => _selectedRefreshMode;
            set
            {
                if (SetValueAndReturn(ref _selectedRefreshMode, value))
                {
                    HandleRefreshModeSelectionChanged();
                    (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string RefreshModeSelectionText => RefreshModes?
            .FirstOrDefault(mode => string.Equals(mode?.Key, SelectedRefreshMode, StringComparison.Ordinal))?
            .ShortDisplayName
            ?? RefreshModes?.FirstOrDefault()?.ShortDisplayName
            ?? L("LOCPlayAch_Button_Refresh", "Refresh");

        public string RefreshActionButtonText => string.Equals(
            SelectedRefreshMode,
            RefreshModeType.Custom.GetKey(),
            StringComparison.Ordinal)
            ? ResourceProvider.GetString("LOCPlayAch_Button_Configure")
            : ResourceProvider.GetString("LOCPlayAch_Button_Refresh");

        public bool UseCoverImages => _settings?.Persisted?.UseCoverImages ?? false;

        public bool EnableCompactGridMode => _settings?.Persisted?.EnableCompactGridMode ?? false;

        public bool ShowSidebarPieCharts =>
            ShowSidebarGamesPieChart ||
            ShowSidebarProviderPieChart ||
            ShowSidebarRarityPieChart ||
            ShowSidebarTrophyPieChart;

        public bool ShowSidebarGamesPieChart => _settings?.Persisted?.ShowSidebarGamesPieChart ?? true;

        public bool ShowSidebarProviderPieChart => _settings?.Persisted?.ShowSidebarProviderPieChart ?? true;

        public bool ShowSidebarRarityPieChart => _settings?.Persisted?.ShowSidebarRarityPieChart ?? true;

        public bool ShowSidebarTrophyPieChart => _settings?.Persisted?.ShowSidebarTrophyPieChart ?? true;

        public bool ShowSidebarPiePercentages => _settings?.Persisted?.ShowSidebarPiePercentages ?? true;

        public bool ShowSidebarBarCharts => _settings?.Persisted?.ShowSidebarBarCharts ?? true;

        private int _totalGamesOverview;
        public int TotalGamesOverview
        {
            get => _totalGamesOverview;
            private set => SetValue(ref _totalGamesOverview, value);
        }

        private int _totalAchievementsOverview;
        public int TotalAchievementsOverview
        {
            get => _totalAchievementsOverview;
            private set => SetValue(ref _totalAchievementsOverview, value);
        }

        private int _totalUnlockedOverview;
        public int TotalUnlockedOverview
        {
            get => _totalUnlockedOverview;
            private set => SetValue(ref _totalUnlockedOverview, value);
        }

        private int _completedGames;
        public int CompletedGames
        {
            get => _completedGames;
            private set => SetValue(ref _completedGames, value);
        }

        private int _totalCommon;
        public int TotalCommon
        {
            get => _totalCommon;
            private set => SetValue(ref _totalCommon, value);
        }

        private int _totalUncommon;
        public int TotalUncommon
        {
            get => _totalUncommon;
            private set => SetValue(ref _totalUncommon, value);
        }

        private int _totalRare;
        public int TotalRare
        {
            get => _totalRare;
            private set => SetValue(ref _totalRare, value);
        }

        private int _totalUltraRare;
        public int TotalUltraRare
        {
            get => _totalUltraRare;
            private set => SetValue(ref _totalUltraRare, value);
        }

        private double _globalProgression;
        public double GlobalProgression
        {
            get => _globalProgression;
            private set => SetValue(ref _globalProgression, value);
        }

        private GameOverviewItem _selectedGame;
        public GameOverviewItem SelectedGame
        {
            get => _selectedGame;
            set
            {
                var previousGameId = _selectedGame?.PlayniteGameId;
                var newGameId = value?.PlayniteGameId;

                if (SetValueAndReturn(ref _selectedGame, value))
                {
                    if (previousGameId != newGameId)
                    {
                        ResetSelectedGameSortToDefault();
                    }

                    ResetSelectedGameAchievementVisibilityFilters();
                    (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                    _selectedGameLoadInProgress = true;

                    // Defer visibility/data notifications until after data loads to prevent flash
                    var loadVersion = Interlocked.Increment(ref _selectedGameLoadVersion);
                    _ = LoadSelectedGameAchievementsAndNotifyAsync(loadVersion, newGameId);
                }
            }
        }

        public bool IsGameSelected => SelectedGame != null;

        /// <summary>
        /// Determines whether the refresh command can execute.
        /// Refresh is disabled if refreshing, or if refresh mode is Single and no game is selected.
        /// </summary>
        private bool CanExecuteRefresh()
        {
            if (IsRefreshing)
            {
                return false;
            }

            // If refresh mode is Single, require a game to be selected
            if (SelectedRefreshMode == RefreshModeType.Single.GetKey())
            {
                return SelectedGame != null;
            }

            return true;
        }

        public ObservableCollection<AchievementDisplayItem> SelectedGameAchievements { get; }

        public ObservableCollection<ChartDataPoint> SelectedGameDailyUnlocks { get; } = new ObservableCollection<ChartDataPoint>();

        #endregion

        #region Timeline Properties

        public TimelineViewModel GlobalTimeline { get; private set; }
        public TimelineViewModel SelectedGameTimeline { get; private set; }
        public string TimelineSectionTitle
        {
            get
            {
                var title = L("LOCPlayAch_Section_Timeline", "Achievements Over Time");
                var selectedGameName = SelectedGame?.GameName;
                return string.IsNullOrWhiteSpace(selectedGameName)
                    ? title
                    : $"{title} ({selectedGameName})";
            }
        }

        public PieChartViewModel GamesPieChart { get; private set; }
        public PieChartViewModel RarityPieChart { get; private set; }
        public PieChartViewModel ProviderPieChart { get; private set; }
        public PieChartViewModel TrophyPieChart { get; private set; }

        private string _rarityPieChartTitle;
        public string RarityPieChartTitle
        {
            get => string.IsNullOrWhiteSpace(_rarityPieChartTitle)
                ? L("LOCPlayAch_Sidebar_RarityPieChart", "Achievements by Rarity")
                : _rarityPieChartTitle;
            private set => SetValue(ref _rarityPieChartTitle, value);
        }

        private string _trophyPieChartTitle;
        public string TrophyPieChartTitle
        {
            get => string.IsNullOrWhiteSpace(_trophyPieChartTitle)
                ? L("LOCPlayAch_Sidebar_TrophyPieChart", "Achievements by Trophy")
                : _trophyPieChartTitle;
            private set => SetValue(ref _trophyPieChartTitle, value);
        }

        // Rarity percentage properties for distribution bars
        public double CommonPercentage => TotalUnlockedOverview > 0
            ? (double)TotalCommon / TotalUnlockedOverview * 100 : 0;

        public double UncommonPercentage => TotalUnlockedOverview > 0
            ? (double)TotalUncommon / TotalUnlockedOverview * 100 : 0;

        public double RarePercentage => TotalUnlockedOverview > 0
            ? (double)TotalRare / TotalUnlockedOverview * 100 : 0;

        public double UltraRarePercentage => TotalUnlockedOverview > 0
            ? (double)TotalUltraRare / TotalUnlockedOverview * 100 : 0;

        #endregion

        #region Progress Properties

        public bool IsRefreshing => _refreshService.IsRebuilding;

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetValue(ref _progressPercent, value);
        }

        private string _progressMessage;
        public string ProgressMessage
        {
            get => _progressMessage;
            set => SetValue(ref _progressMessage, value);
        }

        public bool ShowProgress => IsRefreshing || _showCompletedProgress;

        #endregion

        #region Filter Properties

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty))
                {
                    RefreshFilter();
                }
            }
        }

        private bool _showUnlockedOnly;
        public bool ShowUnlockedOnly
        {
            get => _showUnlockedOnly;
            set
            {
                if (SetValueAndReturn(ref _showUnlockedOnly, value))
                {
                    if (value) _showLockedOnly = false;
                    OnPropertyChanged(nameof(ShowLockedOnly));
                    RefreshFilter();
                }
            }
        }

        private bool _showLockedOnly;
        public bool ShowLockedOnly
        {
            get => _showLockedOnly;
            set
            {
                if (SetValueAndReturn(ref _showLockedOnly, value))
                {
                    if (value) _showUnlockedOnly = false;
                    OnPropertyChanged(nameof(ShowUnlockedOnly));
                    RefreshFilter();
                }
            }
        }

        private int _sortIndex = 2; // Default to Unlock Date
        public int SortIndex
        {
            get => _sortIndex;
            set
            {
                if (SetValueAndReturn(ref _sortIndex, value))
                {
                    RefreshFilter();
                }
            }
        }

        #endregion

        #region Status Properties

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetValue(ref _statusText, value);
        }

        private int _totalCount;
        private int _unlockedCount;
        private int _gamesCount;

        #endregion

        #region Commands

        public ICommand RefreshViewCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand CancelRefreshCommand { get; }
        public ICommand RevealAchievementCommand { get; }
        public ICommand OpenGameInLibraryCommand { get; }
        public ICommand OpenGameInSidebarCommand { get; }
        public ICommand RefreshSingleGameCommand { get; }
        public ICommand CloseViewCommand { get; }
        public ICommand ClearGameSelectionCommand { get; }
        public ICommand NavigateToGameCommand { get; }

        #endregion

        #region Public Methods

        public void SetActive(bool isActive)
        {
            _isActive = isActive;
            if (!isActive)
            {
                _deltaBatchTimer?.Stop();
                lock (_deltaSync)
                {
                    _pendingDeltaKeys.Clear();
                    _pendingFullResetFromDelta = false;
                }
                CancelProgressHideTimer(clearCompletedProgress: true);
                CancelPendingRefresh();
            }
            else
            {
                ApplyRefreshStatus(_refreshService.GetRefreshStatusSnapshot());
                // Refresh data when sidebar becomes active to ensure cached changes are visible
                _ = RefreshViewAsync();
            }
        }

        public async Task RefreshViewAsync()
        {
            if (!_isActive)
            {
                return;
            }

            // Ensure the UI gets a chance to paint before we begin heavy work.
            await Task.Yield();

            var version = Interlocked.Increment(ref _refreshVersion);

            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _refreshCts, newCts);
            try { oldCts?.Cancel(); } catch { }
            try { oldCts?.Dispose(); } catch { }

            var cancel = newCts.Token;

            try
            {
                StatusText = ResourceProvider.GetString("LOCPlayAch_Status_LoadingAchievements");

                await _refreshLock.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    var showIcon = _settings?.Persisted?.ShowHiddenIcon ?? false;
                    var showTitle = _settings?.Persisted?.ShowHiddenTitle ?? false;
                    var showDescription = _settings?.Persisted?.ShowHiddenDescription ?? false;
                    var anyHidingEnabled = !showIcon || !showTitle || !showDescription;
                    HashSet<string> revealedCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (anyHidingEnabled)
                    {
                        lock (_revealedKeys)
                        {
                            if (_revealedKeys.Count > 0)
                            {
                                revealedCopy = new HashSet<string>(_revealedKeys, StringComparer.OrdinalIgnoreCase);
                            }
                        }
                    }
                    SidebarDataSnapshot snapshot;
                    snapshot = await Task.Run(
                        () => _dataBuilder.Build(_settings, revealedCopy, cancel),
                        cancel).ConfigureAwait(false);

                    System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
                    {
                        if (_disposed || !_isActive)
                        {
                            return;
                        }

                        if (version != _refreshVersion)
                        {
                            return;
                        }

                        ApplySnapshot(snapshot);
                    });
                }
                finally
                {
                    _refreshLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when new refresh starts.
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to refresh sidebar achievements");
                StatusText = string.Format(ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed"), ex.Message);
            }
        }

        public void CancelRefresh()
        {
            _refreshService.CancelCurrentRebuild();
        }

        public void ClearSearch()
        {
            SearchText = string.Empty;
        }

        public void ClearLeftSearch()
        {
            LeftSearchText = string.Empty;
        }

        public void ClearRightSearch()
        {
            RightSearchText = string.Empty;
        }

        public async Task ExecuteRefreshAsync()
        {
            if (IsRefreshing) return;

            RefreshRequest refreshRequest = null;
            try
            {
                refreshRequest = BuildRefreshRequest();
                if (refreshRequest == null)
                {
                    return;
                }

                CancelProgressHideTimer(clearCompletedProgress: false);
                ApplyRefreshStatus(_refreshService.GetStartingRefreshStatusSnapshot());
                _refreshAttemptInProgress = true;

                await _refreshCoordinator.ExecuteAsync(
                    refreshRequest,
                    new RefreshExecutionPolicy
                    {
                        ValidateAuthentication = true,
                        UseProgressWindow = false,
                        SwallowExceptions = false
                    });
                await RefreshViewAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"{SelectedRefreshMode} refresh failed");
                StatusText = string.Format(ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed"), ex.Message);
            }
            finally
            {
                // Ensure command/button state always reflects centralized manager state,
                // even if the final progress event was not delivered.
                if (refreshRequest != null)
                {
                    ApplyRefreshStatus(_refreshService.GetRefreshStatusSnapshot());
                }
            }
        }

        private RefreshRequest BuildRefreshRequest()
        {
            if (string.Equals(SelectedRefreshMode, RefreshModeType.Custom.GetKey(), StringComparison.Ordinal))
            {
                if (!CustomRefreshControl.TryShowDialog(
                    _playniteApi,
                    _refreshService,
                    _persistSettingsForUi,
                    _settings,
                    _logger,
                    out var customOptions))
                {
                    return null;
                }

                return new RefreshRequest
                {
                    Mode = RefreshModeType.Custom,
                    CustomOptions = customOptions?.Clone()
                };
            }

            Guid? singleGameId = null;
            if (string.Equals(SelectedRefreshMode, RefreshModeType.Single.GetKey(), StringComparison.Ordinal))
            {
                if (SelectedGame?.PlayniteGameId.HasValue == true)
                {
                    singleGameId = SelectedGame.PlayniteGameId.Value;
                }
                else
                {
                    StatusText = "No game selected in the overview.";
                    return null;
                }
            }

            return new RefreshRequest
            {
                ModeKey = SelectedRefreshMode,
                SingleGameId = singleGameId
            };
        }

        private void HandleRefreshModeSelectionChanged()
        {
            OnPropertyChanged(nameof(RefreshActionButtonText));
            OnPropertyChanged(nameof(RefreshModeSelectionText));
        }

        private void NavigateToGame(GameOverviewItem game)
        {
            OpenGameInLibrary(game);
        }

        private async Task ExecuteSingleGameRefreshAsync(object parameter)
        {
            if (!TryGetPlayniteGameId(parameter, out var gameId) || IsRefreshing)
            {
                return;
            }

            try
            {
                CancelProgressHideTimer(clearCompletedProgress: false);
                ApplyRefreshStatus(_refreshService.GetStartingRefreshStatusSnapshot());
                _refreshAttemptInProgress = true;

                await _refreshCoordinator.ExecuteAsync(
                    new RefreshRequest
                    {
                        Mode = RefreshModeType.Single,
                        SingleGameId = gameId
                    },
                    new RefreshExecutionPolicy
                    {
                        ValidateAuthentication = true,
                        UseProgressWindow = false,
                        SwallowExceptions = false
                    });
                await RefreshViewAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Single game refresh failed for game ID {gameId}");
                StatusText = string.Format(ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed"), ex.Message);
            }
            finally
            {
                ApplyRefreshStatus(_refreshService.GetRefreshStatusSnapshot());
            }
        }

        private void OpenGameInLibrary(object parameter)
        {
            if (!TryGetPlayniteGameId(parameter, out var gameId))
            {
                return;
            }

            try
            {
                PlayniteUiProvider.RestoreMainView();
                _playniteApi?.MainView?.SelectGame(gameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to open game in Playnite library: {gameId}");
            }
        }

        private void OpenGameInSidebar(object parameter)
        {
            if (!TryGetPlayniteGameId(parameter, out var gameId))
            {
                return;
            }

            try
            {
                var targetGame =
                    GamesOverview.FirstOrDefault(g => g?.PlayniteGameId == gameId) ??
                    _allGamesOverview.FirstOrDefault(g => g?.PlayniteGameId == gameId);

                if (targetGame != null)
                {
                    SelectedGame = targetGame;
                }
                else
                {
                    _logger?.Warn($"Sidebar game view target not found for game ID {gameId}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to open game in sidebar view: {gameId}");
            }
        }

        private static bool TryGetPlayniteGameId(object parameter, out Guid gameId)
        {
            switch (parameter)
            {
                case GameOverviewItem game when game.PlayniteGameId.HasValue:
                    gameId = game.PlayniteGameId.Value;
                    return true;
                case AchievementDisplayItem achievement when achievement.PlayniteGameId.HasValue:
                    gameId = achievement.PlayniteGameId.Value;
                    return true;
                case Guid id when id != Guid.Empty:
                    gameId = id;
                    return true;
                case string text when Guid.TryParse(text, out var parsed):
                    gameId = parsed;
                    return true;
                default:
                    gameId = Guid.Empty;
                    return false;
            }
        }

        #endregion

        #region Private Methods

        private void CancelPendingRefresh()
        {
            var cts = Interlocked.Exchange(ref _refreshCts, null);
            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }
        }

        private void ApplySnapshot(SidebarDataSnapshot snapshot)
        {
            if (_disposed)
            {
                return;
            }

            _latestSnapshot = snapshot;
            _allAchievements = snapshot.Achievements ?? new List<AchievementDisplayItem>();

            if (AllAchievements is BulkObservableCollection<AchievementDisplayItem> bulkAll)
            {
                bulkAll.ReplaceAll(_allAchievements);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(AllAchievements, _allAchievements);
            }

            _allGamesOverview = snapshot.GamesOverview ?? new List<GameOverviewItem>();
            _allRecentAchievements = snapshot.RecentAchievements ?? new List<AchievementDisplayItem>();
            _filteredRecentAchievements = new List<AchievementDisplayItem>(_allRecentAchievements);

            UpdateProviderFilterOptions(_allGamesOverview);
            UpdateCompletenessFilterOptions();
            UpdatePlayStatusFilterOptions();

            // Default: select "Played" to match prior behavior (hiding unplayed/no-progress games)
            var playedOption = L("LOCPlayAch_Filter_Played", "Played");
            if (_selectedPlayStatusFilters.Count == 0 && PlayStatusFilterOptions?.Contains(playedOption) == true)
            {
                _selectedPlayStatusFilters.Add(playedOption);
                OnPropertyChanged(nameof(SelectedPlayStatusFilterText));
            }

            // Initialize filtered lists
            _filteredRecentAchievements = new List<AchievementDisplayItem>(_allRecentAchievements);
            _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();

            ApplyOverviewSummaryCore(snapshot, updateProviderFilterOptions: false);

            RefreshFilter();
            ApplyLeftFilters();
            UpdateAggregatePieCharts();

            if (RecentAchievements is BulkObservableCollection<AchievementDisplayItem> bulk)
            {
                bulk.ReplaceAll(_filteredRecentAchievements);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(RecentAchievements, _filteredRecentAchievements);
            }
            UpdateFilteredStatus();
        }

        private bool ApplyFragmentDelta(string key, SidebarGameFragment fragment)
        {
            Guid gameId;
            if (!Guid.TryParse(key, out gameId))
            {
                if (fragment?.PlayniteGameId.HasValue == true)
                {
                    gameId = fragment.PlayniteGameId.Value;
                }
                else
                {
                    _logger?.Warn($"Incremental sidebar delta ignored because key is not a valid game id: {key}");
                    return false;
                }
            }

            _allAchievements.RemoveAll(a => a?.PlayniteGameId == gameId);
            _allGamesOverview.RemoveAll(g => g?.PlayniteGameId == gameId);
            _allRecentAchievements.RemoveAll(r => r?.PlayniteGameId == gameId);

            if (fragment == null)
            {
                if (SelectedGame?.PlayniteGameId == gameId)
                {
                    SelectedGame = null;
                }

                return true;
            }

            if (fragment.Achievements != null && fragment.Achievements.Count > 0)
            {
                _allAchievements.AddRange(fragment.Achievements);
            }

            if (fragment.GameOverview != null)
            {
                _allGamesOverview.Add(fragment.GameOverview);
            }

            if (fragment.RecentAchievements != null && fragment.RecentAchievements.Count > 0)
            {
                _allRecentAchievements.AddRange(fragment.RecentAchievements);
            }

            return true;
        }

        private SidebarDataSnapshot BuildSnapshotFromSourceLists()
        {
            var snapshot = new SidebarDataSnapshot
            {
                Achievements = _allAchievements ?? new List<AchievementDisplayItem>(),
                GamesOverview = _allGamesOverview ?? new List<GameOverviewItem>(),
                RecentAchievements = _allRecentAchievements ?? new List<AchievementDisplayItem>(),
                GlobalUnlockCountsByDate = new Dictionary<DateTime, int>(),
                UnlockCountsByDateByGame = new Dictionary<Guid, Dictionary<DateTime, int>>(),
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };

            for (var i = 0; i < snapshot.RecentAchievements.Count; i++)
            {
                var item = snapshot.RecentAchievements[i];
                if (item == null)
                {
                    continue;
                }

                var date = item.UnlockTime.Date;
                if (snapshot.GlobalUnlockCountsByDate.TryGetValue(date, out var existing))
                {
                    snapshot.GlobalUnlockCountsByDate[date] = existing + 1;
                }
                else
                {
                    snapshot.GlobalUnlockCountsByDate[date] = 1;
                }

                if (item.PlayniteGameId.HasValue)
                {
                    var gameId = item.PlayniteGameId.Value;
                    if (!snapshot.UnlockCountsByDateByGame.TryGetValue(gameId, out var gameCounts))
                    {
                        gameCounts = new Dictionary<DateTime, int>();
                        snapshot.UnlockCountsByDateByGame[gameId] = gameCounts;
                    }

                    if (gameCounts.TryGetValue(date, out var gameExisting))
                    {
                        gameCounts[date] = gameExisting + 1;
                    }
                    else
                    {
                        gameCounts[date] = 1;
                    }
                }
            }

            snapshot.TotalGames = snapshot.GamesOverview.Count;
            snapshot.TotalAchievements = snapshot.GamesOverview.Sum(g => g?.TotalAchievements ?? 0);
            snapshot.TotalUnlocked = snapshot.GamesOverview.Sum(g => g?.UnlockedAchievements ?? 0);
            snapshot.TotalCommon = snapshot.GamesOverview.Sum(g => g?.CommonCount ?? 0);
            snapshot.TotalUncommon = snapshot.GamesOverview.Sum(g => g?.UncommonCount ?? 0);
            snapshot.TotalRare = snapshot.GamesOverview.Sum(g => g?.RareCount ?? 0);
            snapshot.TotalUltraRare = snapshot.GamesOverview.Sum(g => g?.UltraRareCount ?? 0);
            snapshot.CompletedGames = snapshot.GamesOverview.Count(g => g?.IsCompleted == true);
            snapshot.TotalLocked = Math.Max(0, snapshot.TotalAchievements - snapshot.TotalUnlocked);
            snapshot.GlobalProgressionPercent = snapshot.TotalAchievements > 0
                ? (double)snapshot.TotalUnlocked / snapshot.TotalAchievements * 100
                : 0;

            snapshot.TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < snapshot.GamesOverview.Count; i++)
            {
                var game = snapshot.GamesOverview[i];
                if (game == null)
                {
                    continue;
                }

                var provider = string.IsNullOrWhiteSpace(game.ProviderKey) ? "Unknown" : game.ProviderKey;
                if (!snapshot.UnlockedByProvider.ContainsKey(provider))
                {
                    snapshot.UnlockedByProvider[provider] = 0;
                }

                if (!snapshot.TotalByProvider.ContainsKey(provider))
                {
                    snapshot.TotalByProvider[provider] = 0;
                }

                snapshot.UnlockedByProvider[provider] += game.UnlockedAchievements;
                snapshot.TotalByProvider[provider] += game.TotalAchievements;
            }

            // Aggregate rarity "possible" totals from GamesOverview
            snapshot.TotalCommonPossible = snapshot.GamesOverview.Sum(g => g?.TotalCommonPossible ?? 0);
            snapshot.TotalUncommonPossible = snapshot.GamesOverview.Sum(g => g?.TotalUncommonPossible ?? 0);
            snapshot.TotalRarePossible = snapshot.GamesOverview.Sum(g => g?.TotalRarePossible ?? 0);
            snapshot.TotalUltraRarePossible = snapshot.GamesOverview.Sum(g => g?.TotalUltraRarePossible ?? 0);

            return snapshot;
        }

        private void ApplyOverviewSummaryFromSnapshot(SidebarDataSnapshot snapshot)
        {
            ApplyOverviewSummaryCore(snapshot, updateProviderFilterOptions: true);
        }

        private void ApplyOverviewSummaryCore(SidebarDataSnapshot snapshot, bool updateProviderFilterOptions)
        {
            if (snapshot == null)
            {
                return;
            }

            _latestSnapshot = snapshot;
            if (updateProviderFilterOptions)
            {
                UpdateProviderFilterOptions(snapshot.GamesOverview ?? new List<GameOverviewItem>());
                UpdateCompletenessFilterOptions();
            }

            _totalCount = snapshot.TotalAchievements;
            _unlockedCount = snapshot.TotalUnlocked;
            _gamesCount = snapshot.TotalGames;

            TotalGamesOverview = snapshot.TotalGames;
            TotalAchievementsOverview = snapshot.TotalAchievements;
            TotalUnlockedOverview = snapshot.TotalUnlocked;
            TotalCommon = snapshot.TotalCommon;
            TotalUncommon = snapshot.TotalUncommon;
            TotalRare = snapshot.TotalRare;
            TotalUltraRare = snapshot.TotalUltraRare;
            CompletedGames = snapshot.CompletedGames;
            GlobalProgression = snapshot.GlobalProgressionPercent;

            OnPropertyChanged(nameof(CommonPercentage));
            OnPropertyChanged(nameof(UncommonPercentage));
            OnPropertyChanged(nameof(RarePercentage));
            OnPropertyChanged(nameof(UltraRarePercentage));

            IDictionary<DateTime, int> selectedTimelineCounts = null;
            IDictionary<DateTime, int> timelineCountsToShow = snapshot.GlobalUnlockCountsByDate;

            if (SelectedGame?.PlayniteGameId.HasValue == true)
            {
                if (snapshot.UnlockCountsByDateByGame != null &&
                    snapshot.UnlockCountsByDateByGame.TryGetValue(SelectedGame.PlayniteGameId.Value, out var selectedCounts))
                {
                    selectedTimelineCounts = selectedCounts;
                }
                else
                {
                    selectedTimelineCounts = new Dictionary<DateTime, int>();
                }

                timelineCountsToShow = selectedTimelineCounts;
            }

            GlobalTimeline.SetCounts(timelineCountsToShow);
            SelectedGameTimeline.SetCounts(selectedTimelineCounts);
        }

        private void UpdateProviderFilterOptions(List<GameOverviewItem> games)
        {
            var providers = (games ?? new List<GameOverviewItem>())
                .Select(g => g?.ProviderKey)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var providerOptions = new List<string>(providers);

            if (ProviderFilterOptions == null)
            {
                ProviderFilterOptions = new ObservableCollection<string>(providerOptions);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(ProviderFilterOptions, providerOptions);
            }

            if (PruneFilterSelections(_selectedProviderFilters, ProviderFilterOptions))
            {
                ApplyLeftFilters();
            }

            OnPropertyChanged(nameof(SelectedProviderFilterText));
            UpdateOverviewPieChartSelectionStates();
        }

        private void UpdateCompletenessFilterOptions()
        {
            var options = new List<string>
            {
                L("LOCPlayAch_Filter_Complete", "Complete"),
                L("LOCPlayAch_Sidebar_Incomplete", "Incomplete")
            };

            if (CompletenessFilterOptions == null)
            {
                CompletenessFilterOptions = new ObservableCollection<string>(options);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(CompletenessFilterOptions, options);
            }

            if (PruneFilterSelections(_selectedCompletenessFilters, CompletenessFilterOptions))
            {
                ApplyLeftFilters();
            }

            OnPropertyChanged(nameof(SelectedCompletenessFilterText));
            UpdateOverviewPieChartSelectionStates();
        }

        private void UpdatePlayStatusFilterOptions()
        {
            var options = new List<string>
            {
                L("LOCPlayAch_Filter_Played", "Played"),
                L("LOCPlayAch_Filter_Unplayed", "Unplayed"),
                L("LOCPlayAch_Filter_NoProgress", "No Progress")
            };

            if (PlayStatusFilterOptions == null)
            {
                PlayStatusFilterOptions = new ObservableCollection<string>(options);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(PlayStatusFilterOptions, options);
            }

            if (PruneFilterSelections(_selectedPlayStatusFilters, PlayStatusFilterOptions))
            {
                ApplyLeftFilters();
            }

            OnPropertyChanged(nameof(SelectedPlayStatusFilterText));
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                if (_settings?.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                    _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
                }

                HandlePersistedSettingsChanged(propertyName: null);
            }
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            HandlePersistedSettingsChanged(e?.PropertyName);
        }

        private void HandlePersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                OnPropertyChanged(nameof(UseCoverImages));
                OnPropertyChanged(nameof(EnableCompactGridMode));
                OnPropertyChanged(nameof(IncludeUnplayedGames));
                ApplySidebarPieSmallSliceMode();
                RaiseSidebarPieChartVisibilityChanged();
                OnPropertyChanged(nameof(ShowSidebarPiePercentages));
                OnPropertyChanged(nameof(ShowSidebarBarCharts));
                _ = RefreshViewAsync();
                ApplyLeftFilters();
                UpdateAggregatePieCharts();
                return;
            }

            if (propertyName == nameof(PersistedSettings.UseCoverImages))
            {
                OnPropertyChanged(nameof(UseCoverImages));
            }
            else if (propertyName == nameof(PersistedSettings.EnableCompactGridMode))
            {
                OnPropertyChanged(nameof(EnableCompactGridMode));
            }
            else if (propertyName == nameof(PersistedSettings.IncludeUnplayedGames))
            {
                OnPropertyChanged(nameof(IncludeUnplayedGames));
            }
            else if (propertyName == nameof(PersistedSettings.ShowSidebarPieCharts)
                || propertyName == nameof(PersistedSettings.ShowSidebarGamesPieChart)
                || propertyName == nameof(PersistedSettings.ShowSidebarProviderPieChart)
                || propertyName == nameof(PersistedSettings.ShowSidebarRarityPieChart)
                || propertyName == nameof(PersistedSettings.ShowSidebarTrophyPieChart))
            {
                RaiseSidebarPieChartVisibilityChanged();
            }
            else if (propertyName == nameof(PersistedSettings.ShowSidebarPiePercentages))
            {
                OnPropertyChanged(nameof(ShowSidebarPiePercentages));
            }
            else if (propertyName == nameof(PersistedSettings.ShowSidebarBarCharts))
            {
                OnPropertyChanged(nameof(ShowSidebarBarCharts));
            }
            else if (propertyName == nameof(PersistedSettings.SidebarPieSmallSliceMode))
            {
                ApplySidebarPieSmallSliceMode();
                UpdateAggregatePieCharts();
            }
            else if (AchievementProjectionService.IsAppearanceSettingPropertyName(propertyName))
            {
                _ = RefreshViewAsync();
            }
        }

        private void RaiseSidebarPieChartVisibilityChanged()
        {
            OnPropertyChanged(nameof(ShowSidebarPieCharts));
            OnPropertyChanged(nameof(ShowSidebarGamesPieChart));
            OnPropertyChanged(nameof(ShowSidebarProviderPieChart));
            OnPropertyChanged(nameof(ShowSidebarRarityPieChart));
            OnPropertyChanged(nameof(ShowSidebarTrophyPieChart));
        }

        private void RevealAchievement(AchievementDisplayItem item)
        {
            if (item == null)
            {
                return;
            }

            var key = AchievementProjectionService.MakeRevealKey(item.PlayniteGameId, item.ApiName, item.GameName);

            item.ToggleReveal();

            lock (_revealedKeys)
            {
                if (item.IsRevealed)
                {
                    _revealedKeys.Add(key);
                }
                else
                {
                    _revealedKeys.Remove(key);
                }
            }
        }

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            if (report == null) return;

            var now = DateTime.UtcNow;

            // Centralized progress/status state from RefreshRuntime.
            var status = _refreshService.GetRefreshStatusSnapshot(report);

            lock (_progressLock)
            {
                if (!status.IsFinal)
                {
                    // Only throttle non-final updates
                    if ((now - _lastProgressUpdate) < ProgressMinInterval)
                    {
                        return;
                    }
                }

                _lastProgressUpdate = now;
            }

            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                try
                {
                    ApplyRefreshStatus(status);
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Progress UI update error: {ex.Message}");
                }
            }));
        }

        private void OnCacheDeltaUpdated(object sender, CacheDeltaEventArgs e)
        {
            if (!_isActive || e == null)
            {
                return;
            }

            System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
            {
                lock (_deltaSync)
                {
                    if (e.IsFullReset)
                    {
                        _pendingFullResetFromDelta = true;
                        _pendingDeltaKeys.Clear();
                    }
                    else if (!string.IsNullOrWhiteSpace(e.Key))
                    {
                        _pendingDeltaKeys.Add(e.Key.Trim());
                    }
                }

                _deltaBatchTimer.Stop();
                _deltaBatchTimer.Start();
            });
        }

        private async void OnDeltaBatchTimerTick(object sender, EventArgs e)
        {
            _deltaBatchTimer.Stop();
            try
            {
                await ApplyPendingDeltasAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed applying incremental cache deltas.");
            }
        }

        private async Task ApplyPendingDeltasAsync()
        {
            bool fullReset;
            List<string> keys;
            lock (_deltaSync)
            {
                fullReset = _pendingFullResetFromDelta;
                keys = _pendingDeltaKeys.ToList();
                _pendingDeltaKeys.Clear();

                if (fullReset)
                {
                    _pendingFullResetFromDelta = false;
                }
            }

            if (fullReset)
            {
                await RefreshViewAsync();
                return;
            }

            if (keys.Count == 0)
            {
                return;
            }

            var showIcon = _settings?.Persisted?.ShowHiddenIcon ?? false;
            var showTitle = _settings?.Persisted?.ShowHiddenTitle ?? false;
            var showDescription = _settings?.Persisted?.ShowHiddenDescription ?? false;
            var anyHidingEnabled = !showIcon || !showTitle || !showDescription;
            var revealedCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (anyHidingEnabled)
            {
                lock (_revealedKeys)
                {
                    if (_revealedKeys.Count > 0)
                    {
                        revealedCopy = new HashSet<string>(_revealedKeys, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }

            var fragments = await Task.Run(() =>
            {
                var dict = new Dictionary<string, SidebarGameFragment>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var gameData = _achievementDataService.GetGameAchievementData(key);
                    dict[key] = gameData == null
                        ? null
                        : _dataBuilder.BuildGameFragment(_settings, revealedCopy, gameData);
                }

                return dict;
            }).ConfigureAwait(true);

            if (!_isActive || _disposed)
            {
                return;
            }

            var requiresFallbackRefresh = false;
            foreach (var key in keys)
            {
                if (!ApplyFragmentDelta(key, fragments.TryGetValue(key, out var fragment) ? fragment : null))
                {
                    requiresFallbackRefresh = true;
                    break;
                }
            }

            if (requiresFallbackRefresh)
            {
                await RefreshViewAsync();
                return;
            }

            if (string.IsNullOrEmpty(_overviewSortPath))
            {
                _allGamesOverview = _allGamesOverview
                    .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                    .ToList();
            }

            if (string.IsNullOrEmpty(_recentSortPath))
            {
                _allRecentAchievements = AchievementGridSortHelper.CreateDefaultSortedList(
                    _allRecentAchievements,
                    AchievementGridSortScope.RecentAchievements);
            }

            var snapshot = BuildSnapshotFromSourceLists();
            ApplyOverviewSummaryFromSnapshot(snapshot);
            RefreshFilter();
            ApplyLeftFilters();
            UpdateAggregatePieCharts();
            ApplyRightFilters();
            UpdateFilteredStatus();
        }

        private async void OnRefreshDebounceTimerTick(object sender, EventArgs e)
        {
            _refreshDebounceTimer.Stop();
            try
            {
                await RefreshViewAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to auto-refresh sidebar on cache change");
            }
        }

        private void StartProgressHideTimer()
        {
            if (_progressHideTimer == null)
            {
                return;
            }

            _progressHideTimer.Stop();
            _progressHideTimer.Start();
        }

        private void CancelProgressHideTimer(bool clearCompletedProgress)
        {
            _progressHideTimer?.Stop();

            if (clearCompletedProgress)
            {
                _refreshAttemptInProgress = false;
                if (_showCompletedProgress)
                {
                    _showCompletedProgress = false;
                    OnPropertyChanged(nameof(ShowProgress));
                }
            }
        }

        private void OnProgressHideTimerTick(object sender, EventArgs e)
        {
            _progressHideTimer?.Stop();
            _refreshAttemptInProgress = false;
            if (_showCompletedProgress)
            {
                _showCompletedProgress = false;
                OnPropertyChanged(nameof(ShowProgress));
            }
        }

        private void ApplyRefreshStatus(RefreshStatusSnapshot status)
        {
            if (status == null)
            {
                return;
            }

            ProgressPercent = status.ProgressPercent;
            ProgressMessage = status.Message ?? string.Empty;

            if (status.IsRefreshing)
            {
                _refreshAttemptInProgress = true;
                CancelProgressHideTimer(clearCompletedProgress: false);
                _showCompletedProgress = false;
            }
            else if (_refreshAttemptInProgress)
            {
                _showCompletedProgress = true;
                StartProgressHideTimer();
            }
            else
            {
                _showCompletedProgress = false;
            }

            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(ShowProgress));
            RaiseCommandsChanged();
        }

        private bool FilterAchievement(AchievementDisplayItem item)
        {
            if (item == null) return false;

            // Search filter
            if (!string.IsNullOrEmpty(SearchText))
            {
                var matchesSearch =
                    (item.GameName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (item.DisplayName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (item.Description?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!matchesSearch) return false;
            }

            // Unlocked/Locked filters
            if (ShowUnlockedOnly && !item.Unlocked) return false;
            if (ShowLockedOnly && item.Unlocked) return false;

            return true;
        }

        public void RefreshFilter()
        {
            var source = _allAchievements ?? new List<AchievementDisplayItem>();
            var filtered = ApplySort(source.Where(FilterAchievement)).ToList();
            if (AllAchievements is BulkObservableCollection<AchievementDisplayItem> bulk)
            {
                bulk.ReplaceAll(filtered);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(AllAchievements, filtered);
            }
            UpdateFilteredStatus();
        }

        private IEnumerable<AchievementDisplayItem> ApplySort(IEnumerable<AchievementDisplayItem> items)
        {
            switch (SortIndex)
            {
                case 0: // Game Name
                    return items.OrderBy(a => a.SortingName).ThenBy(a => a.DisplayName);
                case 1: // Achievement Name
                    return items.OrderBy(a => a.DisplayName);
                case 2: // Unlock Date (most recent first)
                    return items.OrderByDescending(a => a.UnlockTimeUtc ?? DateTime.MinValue);
                case 3: // Rarity (rarest first)
                    return items.OrderBy(a => a.RaritySortValue).ThenByDescending(a => a.Points);
                default:
                    return items;
            }
        }

        private void UpdateStats()
        {
            var source = _allAchievements ?? new List<AchievementDisplayItem>();
            _totalCount = source.Count;
            _unlockedCount = source.Count(a => a.Unlocked);
            _gamesCount = source.Select(a => a.GameName).Distinct().Count();

            UpdateFilteredStatus();
        }

        private void UpdateFilteredStatus()
        {
            if (_totalCount == 0)
            {
                StatusText = ResourceProvider.GetString("LOCPlayAch_Status_NoAchievementsCached");
            }
            else if (AllAchievements.Count < _totalCount)
            {
                StatusText = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_FilteredCounts"), AllAchievements.Count, _totalCount, _unlockedCount, _gamesCount);
            }
            else
            {
                StatusText = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_TotalCounts"), _totalCount, _unlockedCount, _gamesCount);
            }
        }

        private void RecalculateOverviewStats()
        {
            // This now calculates from the filtered view
            var sourceList = _filteredGamesOverview;

            TotalGamesOverview = sourceList.Count;
            TotalAchievementsOverview = sourceList.Sum(g => g.TotalAchievements);
            TotalUnlockedOverview = sourceList.Sum(g => g.UnlockedAchievements);
            TotalCommon = sourceList.Sum(g => g.CommonCount);
            TotalUncommon = sourceList.Sum(g => g.UncommonCount);
            TotalRare = sourceList.Sum(g => g.RareCount);
            TotalUltraRare = sourceList.Sum(g => g.UltraRareCount);
            CompletedGames = sourceList.Count(g => g.IsCompleted);

            GlobalProgression = TotalAchievementsOverview > 0 ? (double)TotalUnlockedOverview / TotalAchievementsOverview * 100 : 0;
        }

        private void RaiseCommandsChanged()
        {
            (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (CancelRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshSingleGameCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (OpenGameInLibraryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenGameInSidebarCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        #endregion

        #region Overview Methods

        // LoadOverviewData removed: overview and recent lists are built via SidebarDataBuilder snapshots.

        private void ApplyLeftFilters()
        {
            // Preserve selection across filter updates
            Guid? selectedGameId = SelectedGame?.PlayniteGameId;

            var filtered = _allGamesOverview.AsEnumerable();

            // Search filter
            if (!string.IsNullOrEmpty(LeftSearchText))
            {
                filtered = filtered.Where(g =>
                    g.GameName?.IndexOf(LeftSearchText, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // Provider filter
            if (_selectedProviderFilters.Count > 0)
            {
                filtered = filtered.Where(g =>
                    !string.IsNullOrWhiteSpace(g.ProviderKey) &&
                    _selectedProviderFilters.Contains(g.ProviderKey));
            }

            // Completeness filter
            if (_selectedCompletenessFilters.Count > 0)
            {
                var completeOption = L("LOCPlayAch_Filter_Complete", "Complete");
                var incompleteOption = L("LOCPlayAch_Sidebar_Incomplete", "Incomplete");
                var includeComplete = _selectedCompletenessFilters.Contains(completeOption);
                var includeIncomplete = _selectedCompletenessFilters.Contains(incompleteOption);

                if (includeComplete && !includeIncomplete)
                {
                    filtered = filtered.Where(g => g.IsCompleted);
                }
                else if (!includeComplete && includeIncomplete)
                {
                    filtered = filtered.Where(g => !g.IsCompleted);
                }
            }

            // Play Status filter
            if (_selectedPlayStatusFilters.Count > 0)
            {
                var playedOption = L("LOCPlayAch_Filter_Played", "Played");
                var unplayedOption = L("LOCPlayAch_Filter_Unplayed", "Unplayed");
                var noProgressOption = L("LOCPlayAch_Filter_NoProgress", "No Progress");

                var includePlayed = _selectedPlayStatusFilters.Contains(playedOption);
                var includeUnplayed = _selectedPlayStatusFilters.Contains(unplayedOption);
                var includeNoProgress = _selectedPlayStatusFilters.Contains(noProgressOption);

                filtered = filtered.Where(g =>
                {
                    if (includePlayed && (g.LastPlayed.HasValue || g.UnlockedAchievements > 0))
                        return true;
                    if (includeUnplayed && !g.LastPlayed.HasValue && g.UnlockedAchievements == 0)
                        return true;
                    if (includeNoProgress && g.UnlockedAchievements == 0)
                        return true;
                    return false;
                });
            }

            _filteredGamesOverview = filtered.ToList();
            if (!string.IsNullOrEmpty(_overviewSortPath))
            {
                SortGamesOverview(_overviewSortPath, _overviewSortDirection);
            }
            else
            {
                if (GamesOverview is BulkObservableCollection<GameOverviewItem> bulk)
                {
                    bulk.ReplaceAll(_filteredGamesOverview);
                }
                else
                {
                    CollectionHelper.SynchronizeCollection(GamesOverview, _filteredGamesOverview);
                }
            }
            RecalculateOverviewStats();

            // Restore selection by finding the game with matching PlayniteGameId
            if (selectedGameId.HasValue)
            {
                var restored = GamesOverview.FirstOrDefault(g => g.PlayniteGameId == selectedGameId.Value);
                if (restored != null)
                {
                    SelectedGame = restored;
                }
            }
        }

        private void UpdateOverviewPieChartSelectionStates()
        {
            ProviderPieChart?.SetSelectedLabels(
                _selectedProviderFilters
                    .Select(GetProviderFilterDisplayName)
                    .Where(label => !string.IsNullOrWhiteSpace(label)));
            GamesPieChart?.SetSelectedLabels(_selectedCompletenessFilters);
        }

        private void ApplySidebarPieSmallSliceMode()
        {
            var mode = _settings?.Persisted?.SidebarPieSmallSliceMode ?? SidebarPieSmallSliceMode.Round;
            GamesPieChart.SmallSliceMode = mode;
            ProviderPieChart.SmallSliceMode = mode;
            RarityPieChart.SmallSliceMode = mode;
            TrophyPieChart.SmallSliceMode = mode;
        }

        private void UpdateAggregatePieCharts()
        {
            var snapshot = BuildPieChartSnapshotFromCurrentState();

            var completedLabel = ResourceProvider.GetString("LOCPlayAch_Filter_Complete");
            var incompleteLabel = ResourceProvider.GetString("LOCPlayAch_Sidebar_Incomplete");
            var lockedLabel = ResourceProvider.GetString("LOCPlayAch_Sidebar_Locked");
            var commonLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Common");
            var uncommonLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Uncommon");
            var rareLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Rare");
            var ultraRareLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_UltraRare");
            var trophyPlatinumLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Platinum");
            var trophyGoldLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Gold");
            var trophySilverLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Silver");
            var trophyBronzeLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Bronze");

            GamesPieChart?.SetGameData(snapshot.TotalGames, snapshot.CompletedGames, completedLabel, incompleteLabel);

            var providerLookup = BuildProviderLookup();
            var providerDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var providerKey in snapshot.UnlockedByProvider.Keys)
            {
                providerDisplayNames[providerKey] = GetProviderFilterDisplayName(providerKey);
            }
            ProviderPieChart?.SetProviderData(
                snapshot.UnlockedByProvider,
                snapshot.TotalByProvider,
                snapshot.TotalLocked,
                lockedLabel,
                providerLookup,
                providerDisplayNames);

            UpdateOverviewPieChartSelectionStates();
            if (_selectedGameLoadInProgress && SelectedGame?.PlayniteGameId.HasValue == true)
            {
                return;
            }

            UpdateContextualPieCharts(
                snapshot,
                commonLabel,
                uncommonLabel,
                rareLabel,
                ultraRareLabel,
                trophyPlatinumLabel,
                trophyGoldLabel,
                trophySilverLabel,
                trophyBronzeLabel,
                lockedLabel);
        }

        private SidebarDataSnapshot BuildPieChartSnapshotFromCurrentState()
        {
            var gamesList = GetPieChartGames().ToList();
            var gameIds = new HashSet<Guid>(
                gamesList
                    .Where(game => game?.PlayniteGameId.HasValue == true)
                    .Select(game => game.PlayniteGameId.Value));
            var achievements = (_allAchievements ?? new List<AchievementDisplayItem>())
                .Where(achievement =>
                    achievement != null &&
                    achievement.PlayniteGameId.HasValue &&
                    gameIds.Contains(achievement.PlayniteGameId.Value))
                .ToList();

            var snapshot = new SidebarDataSnapshot
            {
                Achievements = achievements,
                GamesOverview = gamesList,
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };

            snapshot.TotalGames = gamesList.Count;
            snapshot.CompletedGames = gamesList.Count(game => game?.IsCompleted == true);
            snapshot.TotalAchievements = gamesList.Sum(game => game?.TotalAchievements ?? 0);
            snapshot.TotalUnlocked = gamesList.Sum(game => game?.UnlockedAchievements ?? 0);
            snapshot.TotalLocked = Math.Max(0, snapshot.TotalAchievements - snapshot.TotalUnlocked);
            snapshot.TotalCommon = gamesList.Sum(game => game?.CommonCount ?? 0);
            snapshot.TotalUncommon = gamesList.Sum(game => game?.UncommonCount ?? 0);
            snapshot.TotalRare = gamesList.Sum(game => game?.RareCount ?? 0);
            snapshot.TotalUltraRare = gamesList.Sum(game => game?.UltraRareCount ?? 0);
            snapshot.TotalCommonPossible = gamesList.Sum(game => game?.TotalCommonPossible ?? 0);
            snapshot.TotalUncommonPossible = gamesList.Sum(game => game?.TotalUncommonPossible ?? 0);
            snapshot.TotalRarePossible = gamesList.Sum(game => game?.TotalRarePossible ?? 0);
            snapshot.TotalUltraRarePossible = gamesList.Sum(game => game?.TotalUltraRarePossible ?? 0);

            foreach (var game in gamesList)
            {
                var provider = string.IsNullOrWhiteSpace(game?.ProviderKey) ? "Unknown" : game.ProviderKey;
                if (!snapshot.UnlockedByProvider.ContainsKey(provider))
                {
                    snapshot.UnlockedByProvider[provider] = 0;
                    snapshot.TotalByProvider[provider] = 0;
                }

                snapshot.UnlockedByProvider[provider] += game?.UnlockedAchievements ?? 0;
                snapshot.TotalByProvider[provider] += game?.TotalAchievements ?? 0;
            }

            return snapshot;
        }

        private IEnumerable<GameOverviewItem> GetPieChartGames()
        {
            var filteredGames = (_allGamesOverview ?? new List<GameOverviewItem>()).Where(game => game != null);
            if (_selectedPlayStatusFilters.Count > 0)
            {
                var playedOption = L("LOCPlayAch_Filter_Played", "Played");
                var unplayedOption = L("LOCPlayAch_Filter_Unplayed", "Unplayed");
                var noProgressOption = L("LOCPlayAch_Filter_NoProgress", "No Progress");

                var includePlayed = _selectedPlayStatusFilters.Contains(playedOption);
                var includeUnplayed = _selectedPlayStatusFilters.Contains(unplayedOption);
                var includeNoProgress = _selectedPlayStatusFilters.Contains(noProgressOption);

                filteredGames = filteredGames.Where(g =>
                {
                    if (includePlayed && (g.LastPlayed.HasValue || g.UnlockedAchievements > 0))
                        return true;
                    if (includeUnplayed && !g.LastPlayed.HasValue && g.UnlockedAchievements == 0)
                        return true;
                    if (includeNoProgress && g.UnlockedAchievements == 0)
                        return true;
                    return false;
                });
            }

            return filteredGames;
        }

        private Dictionary<string, (string iconKey, string colorHex)> BuildProviderLookup()
        {
            var providerLookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _refreshService.Providers)
            {
                providerLookup[provider.ProviderKey] = (provider.ProviderIconKey, provider.ProviderColorHex);
            }
            return providerLookup;
        }

        private void UpdateContextualPieCharts(
            SidebarDataSnapshot snapshot,
            string commonLabel = null,
            string uncommonLabel = null,
            string rareLabel = null,
            string ultraRareLabel = null,
            string trophyPlatinumLabel = null,
            string trophyGoldLabel = null,
            string trophySilverLabel = null,
            string trophyBronzeLabel = null,
            string lockedLabel = null)
        {
            commonLabel ??= L("LOCPlayAch_Rarity_Common", "Common");
            uncommonLabel ??= L("LOCPlayAch_Rarity_Uncommon", "Uncommon");
            rareLabel ??= L("LOCPlayAch_Rarity_Rare", "Rare");
            ultraRareLabel ??= L("LOCPlayAch_Rarity_UltraRare", "Ultra Rare");
            trophyPlatinumLabel ??= L("LOCPlayAch_Trophy_Platinum", "Platinum");
            trophyGoldLabel ??= L("LOCPlayAch_Trophy_Gold", "Gold");
            trophySilverLabel ??= L("LOCPlayAch_Trophy_Silver", "Silver");
            trophyBronzeLabel ??= L("LOCPlayAch_Trophy_Bronze", "Bronze");
            lockedLabel ??= L("LOCPlayAch_Sidebar_Locked", "Locked");

            var selectedGame = ResolveSelectedGameForChartContext(snapshot);
            var useSelectedRarity = selectedGame?.HasRarityPieChartData == true;
            var useSelectedTrophy = selectedGame?.HasTrophyPieChartData == true;

            if (useSelectedRarity)
            {
                RarityPieChart.SetRarityData(
                    selectedGame.CommonCount,
                    selectedGame.UncommonCount,
                    selectedGame.RareCount,
                    selectedGame.UltraRareCount,
                    GetSelectedGameLockedAchievementCount(selectedGame),
                    selectedGame.TotalCommonPossible,
                    selectedGame.TotalUncommonPossible,
                    selectedGame.TotalRarePossible,
                    selectedGame.TotalUltraRarePossible,
                    commonLabel,
                    uncommonLabel,
                    rareLabel,
                    ultraRareLabel,
                    lockedLabel);
            }
            else
            {
                RarityPieChart.SetRarityData(
                    snapshot?.TotalCommon ?? 0,
                    snapshot?.TotalUncommon ?? 0,
                    snapshot?.TotalRare ?? 0,
                    snapshot?.TotalUltraRare ?? 0,
                    snapshot?.TotalLocked ?? 0,
                    snapshot?.TotalCommonPossible ?? 0,
                    snapshot?.TotalUncommonPossible ?? 0,
                    snapshot?.TotalRarePossible ?? 0,
                    snapshot?.TotalUltraRarePossible ?? 0,
                    commonLabel,
                    uncommonLabel,
                    rareLabel,
                    ultraRareLabel,
                    lockedLabel);
            }

            if (useSelectedTrophy)
            {
                TrophyPieChart.SetTrophyData(
                    selectedGame.TrophyPlatinumCount,
                    selectedGame.TrophyGoldCount,
                    selectedGame.TrophySilverCount,
                    selectedGame.TrophyBronzeCount,
                    selectedGame.TrophyPlatinumTotal,
                    selectedGame.TrophyGoldTotal,
                    selectedGame.TrophySilverTotal,
                    selectedGame.TrophyBronzeTotal,
                    trophyPlatinumLabel,
                    trophyGoldLabel,
                    trophySilverLabel,
                    trophyBronzeLabel,
                    lockedLabel);
            }
            else
            {
                var trophySummary = BuildTrophySummary(snapshot?.Achievements);
                TrophyPieChart.SetTrophyData(
                    trophySummary.PlatinumUnlocked,
                    trophySummary.GoldUnlocked,
                    trophySummary.SilverUnlocked,
                    trophySummary.BronzeUnlocked,
                    trophySummary.PlatinumTotal,
                    trophySummary.GoldTotal,
                    trophySummary.SilverTotal,
                    trophySummary.BronzeTotal,
                    trophyPlatinumLabel,
                    trophyGoldLabel,
                    trophySilverLabel,
                    trophyBronzeLabel,
                    lockedLabel);
            }

            var rarityTitle = L("LOCPlayAch_Sidebar_RarityPieChart", "Achievements by Rarity");
            var trophyTitle = L("LOCPlayAch_Sidebar_TrophyPieChart", "Achievements by Trophy");
            RarityPieChartTitle = BuildContextualPieChartTitle(rarityTitle, useSelectedRarity ? selectedGame?.GameName : null);
            TrophyPieChartTitle = BuildContextualPieChartTitle(trophyTitle, useSelectedTrophy ? selectedGame?.GameName : null);
        }

        private GameOverviewItem ResolveSelectedGameForChartContext(SidebarDataSnapshot snapshot)
        {
            if (SelectedGame?.PlayniteGameId.HasValue != true)
            {
                return SelectedGame;
            }

            var selectedGameId = SelectedGame.PlayniteGameId.Value;
            return snapshot?.GamesOverview?.FirstOrDefault(game => game?.PlayniteGameId == selectedGameId)
                ?? SelectedGame;
        }

        private static int GetSelectedGameLockedAchievementCount(GameOverviewItem game)
        {
            if (game == null)
            {
                return 0;
            }

            return Math.Max(0, game.TotalAchievements - game.UnlockedAchievements);
        }

        private static string BuildContextualPieChartTitle(string baseTitle, string gameName)
        {
            return string.IsNullOrWhiteSpace(gameName)
                ? baseTitle
                : $"{baseTitle} ({gameName})";
        }

        private static (
            int PlatinumUnlocked,
            int GoldUnlocked,
            int SilverUnlocked,
            int BronzeUnlocked,
            int PlatinumTotal,
            int GoldTotal,
            int SilverTotal,
            int BronzeTotal) BuildTrophySummary(IEnumerable<AchievementDisplayItem> achievements)
        {
            int platinumUnlocked = 0;
            int goldUnlocked = 0;
            int silverUnlocked = 0;
            int bronzeUnlocked = 0;
            int platinumTotal = 0;
            int goldTotal = 0;
            int silverTotal = 0;
            int bronzeTotal = 0;

            if (achievements == null)
            {
                return (0, 0, 0, 0, 0, 0, 0, 0);
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                AchievementProjectionService.AccumulateTrophy(
                    achievement.TrophyType,
                    ref platinumTotal,
                    ref goldTotal,
                    ref silverTotal,
                    ref bronzeTotal);

                if (!achievement.Unlocked)
                {
                    continue;
                }

                AchievementProjectionService.AccumulateTrophy(
                    achievement.TrophyType,
                    ref platinumUnlocked,
                    ref goldUnlocked,
                    ref silverUnlocked,
                    ref bronzeUnlocked);
            }

            return (
                platinumUnlocked,
                goldUnlocked,
                silverUnlocked,
                bronzeUnlocked,
                platinumTotal,
                goldTotal,
                silverTotal,
                bronzeTotal);
        }

        private void UpdateSelectedGameAchievementFilterOptions(IEnumerable<AchievementDisplayItem> source)
        {
            var typeValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var categoryValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (source != null)
            {
                foreach (var item in source)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    var parsedTypes = AchievementCategoryTypeHelper.ParseValues(
                        AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType));
                    foreach (var parsedType in parsedTypes)
                    {
                        if (!string.IsNullOrWhiteSpace(parsedType))
                        {
                            typeValues.Add(parsedType);
                        }
                    }

                    var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel);
                    if (!string.IsNullOrWhiteSpace(normalizedCategory))
                    {
                        categoryValues.Add(normalizedCategory);
                    }
                }
            }

            var typeOptions = AchievementCategoryTypeHelper.AllowedCategoryTypes
                .Where(typeValues.Contains)
                .ToList();

            var categoryOptions = categoryValues
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (SelectedGameTypeFilterOptions == null)
            {
                SelectedGameTypeFilterOptions = new ObservableCollection<string>(typeOptions);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(SelectedGameTypeFilterOptions, typeOptions);
            }

            if (SelectedGameCategoryFilterOptions == null)
            {
                SelectedGameCategoryFilterOptions = new ObservableCollection<string>(categoryOptions);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(SelectedGameCategoryFilterOptions, categoryOptions);
            }

            if (PruneFilterSelections(_selectedGameTypeFilters, SelectedGameTypeFilterOptions))
            {
                OnPropertyChanged(nameof(SelectedGameTypeFilterText));
            }

            if (PruneFilterSelections(_selectedGameCategoryFilters, SelectedGameCategoryFilterOptions))
            {
                OnPropertyChanged(nameof(SelectedGameCategoryFilterText));
            }
        }

        private static bool IsFilterSelected(HashSet<string> selectedValues, string value)
        {
            if (selectedValues == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return selectedValues.Contains(value.Trim());
        }

        private static bool SetFilterSelection(HashSet<string> selectedValues, string value, bool isSelected)
        {
            if (selectedValues == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            return isSelected
                ? selectedValues.Add(normalized)
                : selectedValues.Remove(normalized);
        }

        private static bool PruneFilterSelections(HashSet<string> selectedValues, IEnumerable<string> options)
        {
            if (selectedValues == null)
            {
                return false;
            }

            var optionSet = new HashSet<string>(
                (options ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return selectedValues.RemoveWhere(value => !optionSet.Contains(value)) > 0;
        }

        private static string GetSelectedFilterText(
            HashSet<string> selectedValues,
            IEnumerable<string> options,
            string placeholder)
        {
            if (selectedValues == null || selectedValues.Count == 0)
            {
                return placeholder;
            }

            var ordered = new List<string>();
            foreach (var option in options ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(option) && selectedValues.Contains(option))
                {
                    ordered.Add(option);
                }
            }

            if (ordered.Count == 0)
            {
                ordered.AddRange(selectedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            }

            return string.Join(", ", ordered);
        }

        private string GetSelectedProviderFilterText()
        {
            if (_selectedProviderFilters == null || _selectedProviderFilters.Count == 0)
            {
                return L("LOCPlayAch_Filter_ProviderSelectorPlaceholder", "Platform");
            }

            var orderedDisplayNames = new List<string>();
            foreach (var option in ProviderFilterOptions ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(option) && _selectedProviderFilters.Contains(option))
                {
                    orderedDisplayNames.Add(GetProviderFilterDisplayName(option));
                }
            }

            if (orderedDisplayNames.Count == 0)
            {
                orderedDisplayNames.AddRange(_selectedProviderFilters
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(GetProviderFilterDisplayName));
            }

            return string.Join(", ", orderedDisplayNames);
        }

        private void ApplyRightFilters()
        {
            // Contextually filter based on IsGameSelected
            if (IsGameSelected)
            {
                // Filter SelectedGameAchievements
                var filtered = _allSelectedGameAchievements.AsEnumerable();

                if (!ShowSelectedGameHidden)
                {
                    filtered = filtered.Where(a => !(a.Hidden && !a.Unlocked));
                }

                filtered = filtered.Where(a => a.Unlocked ? ShowSelectedGameUnlocked : ShowSelectedGameLocked);

                if (_selectedGameTypeFilters.Count > 0)
                {
                    var selectedTypeSet = new HashSet<string>(
                        _selectedGameTypeFilters
                            .Select(AchievementCategoryTypeHelper.NormalizeOrDefault)
                            .Where(value => !string.IsNullOrWhiteSpace(value)),
                        StringComparer.OrdinalIgnoreCase);
                    filtered = filtered.Where(a =>
                        AchievementCategoryTypeHelper.ParseValues(
                                AchievementCategoryTypeHelper.NormalizeOrDefault(a.CategoryType))
                            .Any(selectedTypeSet.Contains));
                }

                if (_selectedGameCategoryFilters.Count > 0)
                {
                    var selectedCategorySet = new HashSet<string>(
                        _selectedGameCategoryFilters
                            .Select(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault)
                            .Where(value => !string.IsNullOrWhiteSpace(value)),
                        StringComparer.OrdinalIgnoreCase);
                    filtered = filtered.Where(a =>
                        selectedCategorySet.Contains(
                            AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.CategoryLabel)));
                }

                if (!string.IsNullOrEmpty(RightSearchText))
                {
                    filtered = filtered.Where(a =>
                        (a.DisplayName?.IndexOf(RightSearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (a.Description?.IndexOf(RightSearchText, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                _filteredSelectedGameAchievements = filtered.ToList();
                if (!string.IsNullOrEmpty(_selectedGameSortPath))
                {
                    SortSelectedGameAchievements(_selectedGameSortPath, _selectedGameSortDirection);
                }
                else
                {
                    if (SelectedGameAchievements is BulkObservableCollection<AchievementDisplayItem> bulk)
                    {
                        bulk.ReplaceAll(_filteredSelectedGameAchievements);
                    }
                    else
                    {
                        CollectionHelper.SynchronizeCollection(SelectedGameAchievements, _filteredSelectedGameAchievements);
                    }
                }
            }
            else
            {
                // Filter RecentAchievements
                var filtered = _allRecentAchievements.AsEnumerable();
                if (!string.IsNullOrEmpty(RightSearchText))
                {
                    filtered = filtered.Where(r =>
                        (r.GameName?.IndexOf(RightSearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (r.Name?.IndexOf(RightSearchText, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                _filteredRecentAchievements = filtered.ToList();
                if (!string.IsNullOrEmpty(_recentSortPath))
                {
                    SortRecentAchievements(_recentSortPath, _recentSortDirection);
                }
                else
                {
                    if (RecentAchievements is BulkObservableCollection<AchievementDisplayItem> bulk)
                    {
                        bulk.ReplaceAll(_filteredRecentAchievements);
                    }
                    else
                    {
                        CollectionHelper.SynchronizeCollection(RecentAchievements, _filteredRecentAchievements);
                    }
                }
            }
        }

        private void ResetSelectedGameAchievementVisibilityFilters()
        {
            ShowSelectedGameUnlocked = true;
            ShowSelectedGameLocked = true;
            ShowSelectedGameHidden = true;
            ResetSelectedGameAchievementCategoryFilters();
        }

        private void ResetSelectedGameAchievementCategoryFilters()
        {
            var typeChanged = _selectedGameTypeFilters.Count > 0;
            var categoryChanged = _selectedGameCategoryFilters.Count > 0;

            _selectedGameTypeFilters.Clear();
            _selectedGameCategoryFilters.Clear();

            if (typeChanged)
            {
                OnPropertyChanged(nameof(SelectedGameTypeFilterText));
            }

            if (categoryChanged)
            {
                OnPropertyChanged(nameof(SelectedGameCategoryFilterText));
            }
        }

        private void ResetSelectedGameSortToDefault()
        {
            _selectedGameSortPath = nameof(AchievementDisplayItem.UnlockTime);
            _selectedGameSortDirection = ListSortDirection.Descending;
            SelectedGameHasCustomAchievementOrder = false;
        }

        /// <summary>
        /// Loads game achievements and fires visibility notifications after data is ready.
        /// This prevents flash by ensuring data is loaded before the grid becomes visible.
        /// </summary>
        private async Task LoadSelectedGameAchievementsAndNotifyAsync(int loadVersion, Guid? targetGameId)
        {
            var loadApplied = await LoadSelectedGameAchievementsAsync(loadVersion, targetGameId).ConfigureAwait(true);
            if (loadVersion == _selectedGameLoadVersion)
            {
                _selectedGameLoadInProgress = false;
            }

            if (!loadApplied)
            {
                return;
            }

            // Fire notifications after data is ready
            OnPropertyChanged(nameof(IsGameSelected));
            OnPropertyChanged(nameof(TimelineSectionTitle));
            UpdateContextualPieCharts(BuildPieChartSnapshotFromCurrentState());
        }

        private async Task<bool> LoadSelectedGameAchievementsAsync(int loadVersion, Guid? targetGameId)
        {
            // Reset right search when selecting a game
            RightSearchText = string.Empty;

            if (targetGameId == null)
            {
                if (!IsSelectedGameLoadCurrent(loadVersion, targetGameId))
                {
                    return false;
                }

                _allSelectedGameAchievements = new List<AchievementDisplayItem>();
                _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();
                UpdateSelectedGameAchievementFilterOptions(null);
                SelectedGameHasCustomAchievementOrder = false;
                if (SelectedGameAchievements is BulkObservableCollection<AchievementDisplayItem> bulk)
                {
                    bulk.ReplaceAll(_filteredSelectedGameAchievements);
                }
                else
                {
                    CollectionHelper.SynchronizeCollection(SelectedGameAchievements, _filteredSelectedGameAchievements);
                }
                // Restore global timeline to show all games
                GlobalTimeline.SetCounts(_latestSnapshot?.GlobalUnlockCountsByDate);
                return true;
            }

            try
            {
                if (!IsSelectedGameLoadCurrent(loadVersion, targetGameId))
                {
                    return false;
                }

                var gameId = targetGameId.Value;
                var showIcon = _settings?.Persisted?.ShowHiddenIcon ?? false;
                var showTitle = _settings?.Persisted?.ShowHiddenTitle ?? false;
                var showDescription = _settings?.Persisted?.ShowHiddenDescription ?? false;
                var anyHidingEnabled = !showIcon || !showTitle || !showDescription;
                ISet<string> revealedCopy = null;
                if (anyHidingEnabled)
                {
                    lock (_revealedKeys)
                    {
                        if (_revealedKeys.Count > 0)
                        {
                            revealedCopy = new HashSet<string>(_revealedKeys, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }

                var loadResult = await Task.Run(() =>
                {
                    var gameData = _achievementDataService.GetGameAchievementData(gameId);
                    if (gameData == null || gameData.Achievements == null)
                    {
                        return Tuple.Create(new List<AchievementDisplayItem>(), false);
                    }

                    var options = AchievementProjectionService.CreateOptions(_settings, gameData, revealedCopy);
                    var achievements = new List<AchievementDisplayItem>(gameData.Achievements.Count);
                    foreach (var ach in gameData.Achievements)
                    {
                        var item = AchievementProjectionService.CreateDisplayItem(gameData, ach, options, gameId);
                        if (item != null)
                        {
                            achievements.Add(item);
                        }
                    }

                    var hasCustomOrder = gameData.AchievementOrder != null && gameData.AchievementOrder.Count > 0;
                    if (hasCustomOrder)
                    {
                        return Tuple.Create(
                            AchievementOrderHelper.ApplyOrder(
                                achievements,
                                a => a.ApiName,
                                gameData.AchievementOrder),
                            true);
                    }

                    return Tuple.Create(
                        AchievementGridSortHelper.CreateDefaultSortedList(
                            achievements,
                            AchievementGridSortScope.GameAchievements),
                        false);
                }).ConfigureAwait(true);

                if (!IsSelectedGameLoadCurrent(loadVersion, targetGameId))
                {
                    return false;
                }

                var items = loadResult.Item1;
                var hasCustomOrder = loadResult.Item2;
                SelectedGameHasCustomAchievementOrder = hasCustomOrder;

                if (hasCustomOrder &&
                    string.Equals(_selectedGameSortPath, nameof(AchievementDisplayItem.UnlockTime), StringComparison.Ordinal))
                {
                    _selectedGameSortPath = null;
                }
                else if (!hasCustomOrder && string.IsNullOrEmpty(_selectedGameSortPath))
                {
                    _selectedGameSortPath = nameof(AchievementDisplayItem.UnlockTime);
                    _selectedGameSortDirection = ListSortDirection.Descending;
                }

                _allSelectedGameAchievements = items;
                UpdateSelectedGameAchievementFilterOptions(_allSelectedGameAchievements);
                ApplyRightFilters();

                // Update timeline to show selected game's unlock history
                IDictionary<DateTime, int> selectedTimelineCounts = null;
                if (_latestSnapshot?.UnlockCountsByDateByGame != null &&
                    _latestSnapshot.UnlockCountsByDateByGame.TryGetValue(gameId, out var counts))
                {
                    selectedTimelineCounts = counts;
                }
                else
                {
                    selectedTimelineCounts = new Dictionary<DateTime, int>();
                }

                GlobalTimeline.SetCounts(selectedTimelineCounts);
                SelectedGameTimeline.SetCounts(selectedTimelineCounts);
                return true;
            }
            catch (Exception ex)
            {
                if (!IsSelectedGameLoadCurrent(loadVersion, targetGameId))
                {
                    return false;
                }

                UpdateSelectedGameAchievementFilterOptions(null);
                SelectedGameHasCustomAchievementOrder = false;
                _logger?.Warn(ex, $"Failed to load achievements for game {SelectedGame?.AppId}");
                return false;
            }
        }

        private bool IsSelectedGameLoadCurrent(int loadVersion, Guid? targetGameId)
        {
            if (loadVersion != _selectedGameLoadVersion)
            {
                return false;
            }

            return SelectedGame?.PlayniteGameId == targetGameId;
        }

        public void ClearGameSelection()
        {
            // Reset right search when clearing selection
            RightSearchText = string.Empty;
            SelectedGame = null;
        }

        #endregion

        public void SortDataGrid(DataGrid dataGrid, string sortMemberPath, ListSortDirection direction)
        {
            if (dataGrid == null || string.IsNullOrEmpty(sortMemberPath)) return;

            // Identify which DataGrid is being sorted by checking ItemsSource
            var itemsSource = dataGrid.ItemsSource;

            if (itemsSource == GamesOverview)
            {
                SortGamesOverview(sortMemberPath, direction);
            }
            else if (itemsSource == RecentAchievements)
            {
                SortRecentAchievements(sortMemberPath, direction);
            }
            else if (itemsSource == SelectedGameAchievements)
            {
                SortSelectedGameAchievements(sortMemberPath, direction);
            }
        }

        private void SortGamesOverview(string sortMemberPath, ListSortDirection direction)
        {
            // Quick reverse if same column
            if (_overviewSortPath == sortMemberPath && _overviewSortDirection == ListSortDirection.Ascending &&
                direction == ListSortDirection.Descending)
            {
                _filteredGamesOverview.Reverse();
                _overviewSortDirection = direction;
                if (GamesOverview is BulkObservableCollection<GameOverviewItem> bulkOverview)
                {
                    bulkOverview.ReplaceAll(_filteredGamesOverview);
                }
                else
                {
                    CollectionHelper.SynchronizeCollection(GamesOverview, _filteredGamesOverview);
                }
                return;
            }

            _overviewSortPath = sortMemberPath;
            _overviewSortDirection = direction;

            Comparison<GameOverviewItem> comparison = sortMemberPath switch
            {
                "SortingName" => (a, b) => string.Compare(a.SortingName, b.SortingName, StringComparison.OrdinalIgnoreCase),
                nameof(GameOverviewItem.GameName) => (a, b) => string.Compare(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase),
                nameof(GameOverviewItem.LastPlayed) => (a, b) => (a.LastPlayed ?? DateTime.MinValue).CompareTo(b.LastPlayed ?? DateTime.MinValue),
                nameof(GameOverviewItem.Progression) => (a, b) => a.Progression.CompareTo(b.Progression),
                nameof(GameOverviewItem.TotalAchievements) => (a, b) => a.TotalAchievements.CompareTo(b.TotalAchievements),
                nameof(GameOverviewItem.UnlockedAchievements) => (a, b) => a.UnlockedAchievements.CompareTo(b.UnlockedAchievements),
                "TrophyType" => CompareGameOverviewByTrophyType,
                _ => null
            };

            if (comparison != null)
            {
                if (direction == ListSortDirection.Descending)
                {
                    _filteredGamesOverview.Sort((a, b) => comparison(b, a));
                }
                else
                {
                    _filteredGamesOverview.Sort(comparison);
                }
            }

            if (GamesOverview is BulkObservableCollection<GameOverviewItem> bulkOverview2)
            {
                bulkOverview2.ReplaceAll(_filteredGamesOverview);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(GamesOverview, _filteredGamesOverview);
            }
        }

        private void SortRecentAchievements(string sortMemberPath, ListSortDirection direction)
        {
            var recentSortDirection = (ListSortDirection?)_recentSortDirection;
            if (!AchievementGridSortHelper.TrySortItems(
                    _filteredRecentAchievements,
                    sortMemberPath,
                    direction,
                    AchievementGridSortScope.RecentAchievements,
                    ref _recentSortPath,
                    ref recentSortDirection))
            {
                return;
            }

            if (recentSortDirection.HasValue)
            {
                _recentSortDirection = recentSortDirection.Value;
            }

            if (RecentAchievements is BulkObservableCollection<AchievementDisplayItem> bulkRecent2)
            {
                bulkRecent2.ReplaceAll(_filteredRecentAchievements);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(RecentAchievements, _filteredRecentAchievements);
            }
        }

        private void SortSelectedGameAchievements(string sortMemberPath, ListSortDirection direction)
        {
            var existingAllOrder = _allSelectedGameAchievements
                .Select((item, index) => new { item, index })
                .ToDictionary(x => x.item, x => x.index);

            var selectedSortDirection = (ListSortDirection?)_selectedGameSortDirection;
            if (!AchievementGridSortHelper.TrySortItems(
                    _allSelectedGameAchievements,
                    sortMemberPath,
                    direction,
                    AchievementGridSortScope.GameAchievements,
                    ref _selectedGameSortPath,
                    ref selectedSortDirection,
                    existingAllOrder))
            {
                return;
            }

            if (selectedSortDirection.HasValue)
            {
                _selectedGameSortDirection = selectedSortDirection.Value;
            }

            var sortedAllOrder = _allSelectedGameAchievements
                .Select((item, index) => new { item, index })
                .ToDictionary(x => x.item, x => x.index);

            selectedSortDirection = _selectedGameSortDirection;
            AchievementGridSortHelper.TrySortItems(
                _filteredSelectedGameAchievements,
                sortMemberPath,
                direction,
                AchievementGridSortScope.GameAchievements,
                ref _selectedGameSortPath,
                ref selectedSortDirection,
                sortedAllOrder);

            if (SelectedGameAchievements is BulkObservableCollection<AchievementDisplayItem> bulkSelected2)
            {
                bulkSelected2.ReplaceAll(_filteredSelectedGameAchievements);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(SelectedGameAchievements, _filteredSelectedGameAchievements);
            }
        }

        private static int GetTrophyRank(string trophyType)
        {
            return AchievementGridSortHelper.GetTrophyRank(trophyType);
        }

        private static int CompareGameOverviewByTrophyType(GameOverviewItem a, GameOverviewItem b)
        {
            return GetTrophyRank(a.TrophyPlatinumCount > 0 ? "platinum" :
                                  a.TrophyGoldCount > 0 ? "gold" :
                                  a.TrophySilverCount > 0 ? "silver" :
                                  a.TrophyBronzeCount > 0 ? "bronze" : null)
                   .CompareTo(GetTrophyRank(b.TrophyPlatinumCount > 0 ? "platinum" :
                                            b.TrophyGoldCount > 0 ? "gold" :
                                            b.TrophySilverCount > 0 ? "silver" :
                                            b.TrophyBronzeCount > 0 ? "bronze" : null));
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        public void Dispose()
        {
            _disposed = true;
            SetActive(false);
            _refreshDebounceTimer?.Stop();
            _progressHideTimer?.Stop();
            _deltaBatchTimer?.Stop();
            CancelPendingRefresh();
            if (_refreshService != null)
            {
                _refreshService.RebuildProgress -= OnRebuildProgress;
                _refreshService.CacheDeltaUpdated -= OnCacheDeltaUpdated;
            }
            if (_settings != null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                }
            }
            if (_refreshDebounceTimer != null)
            {
                _refreshDebounceTimer.Tick -= OnRefreshDebounceTimerTick;
            }
            if (_progressHideTimer != null)
            {
                _progressHideTimer.Tick -= OnProgressHideTimerTick;
            }
            if (_deltaBatchTimer != null)
            {
                _deltaBatchTimer.Tick -= OnDeltaBatchTimerTick;
            }
        }
    }
}




