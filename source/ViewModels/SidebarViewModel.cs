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

        private readonly AchievementService _achievementService;
        private readonly RefreshCoordinator _refreshCoordinator;
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
        private System.Windows.Threading.DispatcherTimer _refreshDebounceTimer;
        private System.Windows.Threading.DispatcherTimer _progressHideTimer;
        private System.Windows.Threading.DispatcherTimer _deltaBatchTimer;
        private bool _showCompletedProgress;
        private bool _refreshAttemptInProgress;
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
            AchievementService achievementService,
            RefreshCoordinator refreshCoordinator,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;
            _dataBuilder = new SidebarDataBuilder(_achievementService, _playniteApi, _logger);

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
                _achievementService.GetRefreshModes().Where(m => m.Type != RefreshModeType.LibrarySelected));

            GlobalTimeline = new TimelineViewModel();
            SelectedGameTimeline = new TimelineViewModel();

            GamesPieChart = new PieChartViewModel();
            RarityPieChart = new PieChartViewModel();
            ProviderPieChart = new PieChartViewModel();
            TrophyPieChart = new PieChartViewModel();

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
            _achievementService.RebuildProgress += OnRebuildProgress;
            _achievementService.CacheDeltaUpdated += OnCacheDeltaUpdated;
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

        public string SelectedProviderFilterText => GetSelectedFilterText(
            _selectedProviderFilters,
            ProviderFilterOptions,
            L("LOCPlayAch_Filter_ProviderSelectorPlaceholder", "Platform"));

        public bool IsProviderFilterSelected(string providerName)
        {
            return IsFilterSelected(_selectedProviderFilters, providerName);
        }

        public void SetProviderFilterSelected(string providerName, bool isSelected)
        {
            if (!SetFilterSelection(_selectedProviderFilters, providerName, isSelected))
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

            // Get the canonical display name from ProviderRegistry
            var canonicalName = ProviderRegistry.GetLocalizedName(providerKey);

            // Use the canonical name for filtering
            if (ProviderFilterOptions == null || !ProviderFilterOptions.Any(p => string.Equals(p, canonicalName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var currentlySelected = IsProviderFilterSelected(canonicalName);
            SetProviderFilterSelected(canonicalName, !currentlySelected);
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

        public bool ShowGamesWithNoUnlocks => _settings?.Persisted?.ShowGamesWithNoUnlocks ?? false;

        public bool ShowUnplayedGames => _settings?.Persisted?.ShowUnplayedGames ?? false;

        public bool UseCoverImages => _settings?.Persisted?.UseCoverImages ?? false;

        public bool ShowSidebarPieCharts =>
            ShowSidebarGamesPieChart ||
            ShowSidebarProviderPieChart ||
            ShowSidebarRarityPieChart ||
            ShowSidebarTrophyPieChart;

        public bool ShowSidebarGamesPieChart => _settings?.Persisted?.ShowSidebarGamesPieChart ?? true;

        public bool ShowSidebarProviderPieChart => _settings?.Persisted?.ShowSidebarProviderPieChart ?? true;

        public bool ShowSidebarRarityPieChart => _settings?.Persisted?.ShowSidebarRarityPieChart ?? true;

        public bool ShowSidebarTrophyPieChart => _settings?.Persisted?.ShowSidebarTrophyPieChart ?? true;

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

                    // Defer visibility/data notifications until after data loads to prevent flash
                    _ = LoadSelectedGameAchievementsAndNotifyAsync();
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

        public bool IsRefreshing => _achievementService.IsRebuilding;

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
                ApplyRefreshStatus(_achievementService.GetRefreshStatusSnapshot());
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
            _achievementService.CancelCurrentRebuild();
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
                ApplyRefreshStatus(_achievementService.GetStartingRefreshStatusSnapshot());
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
                    ApplyRefreshStatus(_achievementService.GetRefreshStatusSnapshot());
                }
            }
        }

        private RefreshRequest BuildRefreshRequest()
        {
            if (string.Equals(SelectedRefreshMode, RefreshModeType.Custom.GetKey(), StringComparison.Ordinal))
            {
                if (!CustomRefreshControl.TryShowDialog(
                    _playniteApi,
                    _achievementService,
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
                ApplyRefreshStatus(_achievementService.GetStartingRefreshStatusSnapshot());
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
                ApplyRefreshStatus(_achievementService.GetRefreshStatusSnapshot());
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

            var providerLookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            var providerDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _achievementService.GetProviders())
            {
                providerLookup[provider.ProviderKey] = (provider.ProviderIconKey, provider.ProviderColorHex);
                providerDisplayNames[provider.ProviderKey] = provider.ProviderName;
            }

            var completedLabel = ResourceProvider.GetString("LOCPlayAch_Filter_Complete");
            var incompleteLabel = ResourceProvider.GetString("LOCPlayAch_Sidebar_Incomplete");
            var commonLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Common");
            var uncommonLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Uncommon");
            var rareLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Rare");
            var ultraRareLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_UltraRare");
            var trophyPlatinumLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Platinum");
            var trophyGoldLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Gold");
            var trophySilverLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Silver");
            var trophyBronzeLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Bronze");
            var lockedLabel = ResourceProvider.GetString("LOCPlayAch_Sidebar_Locked");

            ProviderPieChart.SetProviderData(snapshot.UnlockedByProvider, snapshot.TotalByProvider, snapshot.TotalLocked, lockedLabel, providerLookup, providerDisplayNames);

            GamesPieChart.SetGameData(snapshot.TotalGames, snapshot.CompletedGames, completedLabel, incompleteLabel);
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
            UpdateOverviewPieChartSelectionStates();

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
                .Select(g => g?.Provider)
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

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Persisted.UseCoverImages")
            {
                OnPropertyChanged(nameof(UseCoverImages));
            }
            else if (e.PropertyName == "Persisted.IncludeUnplayedGames")
            {
                OnPropertyChanged(nameof(IncludeUnplayedGames));
            }
            else if (e.PropertyName == "Persisted.ShowSidebarPieCharts"
                || e.PropertyName == "Persisted.ShowSidebarGamesPieChart"
                || e.PropertyName == "Persisted.ShowSidebarProviderPieChart"
                || e.PropertyName == "Persisted.ShowSidebarRarityPieChart"
                || e.PropertyName == "Persisted.ShowSidebarTrophyPieChart")
            {
                RaiseSidebarPieChartVisibilityChanged();
            }
            else if (e.PropertyName == "Persisted.ShowSidebarBarCharts")
            {
                OnPropertyChanged(nameof(ShowSidebarBarCharts));
            }
            else if (e.PropertyName == "Persisted.ShowHiddenIcon"
                || e.PropertyName == "Persisted.ShowHiddenTitle"
                || e.PropertyName == "Persisted.ShowHiddenDescription"
                || e.PropertyName == "Persisted.ShowRarityGlow")
            {
                // Refresh view when any hide or display setting changes
                _ = RefreshViewAsync();
            }
            else if (e.PropertyName == "Persisted.ShowGamesWithNoUnlocks")
            {
                OnPropertyChanged(nameof(ShowGamesWithNoUnlocks));
                ApplyLeftFilters();
                UpdateAggregatePieCharts();
            }
            else if (e.PropertyName == "Persisted.ShowUnplayedGames")
            {
                OnPropertyChanged(nameof(ShowUnplayedGames));
                ApplyLeftFilters();
                UpdateAggregatePieCharts();
            }
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.ShowSidebarPieCharts)
                || e.PropertyName == nameof(PersistedSettings.ShowSidebarGamesPieChart)
                || e.PropertyName == nameof(PersistedSettings.ShowSidebarProviderPieChart)
                || e.PropertyName == nameof(PersistedSettings.ShowSidebarRarityPieChart)
                || e.PropertyName == nameof(PersistedSettings.ShowSidebarTrophyPieChart))
            {
                RaiseSidebarPieChartVisibilityChanged();
            }
            else if (e.PropertyName == nameof(PersistedSettings.ShowSidebarBarCharts))
            {
                OnPropertyChanged(nameof(ShowSidebarBarCharts));
            }
            else if (e.PropertyName == nameof(PersistedSettings.ShowGamesWithNoUnlocks))
            {
                OnPropertyChanged(nameof(ShowGamesWithNoUnlocks));
                ApplyLeftFilters();
                UpdateAggregatePieCharts();
            }
            else if (e.PropertyName == nameof(PersistedSettings.ShowUnplayedGames))
            {
                OnPropertyChanged(nameof(ShowUnplayedGames));
                ApplyLeftFilters();
                UpdateAggregatePieCharts();
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

            // Centralized progress/status state from AchievementService.
            var status = _achievementService.GetRefreshStatusSnapshot(report);

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

                    var gameData = _achievementService.GetGameAchievementData(key);
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
                _allRecentAchievements = _allRecentAchievements
                    .OrderByDescending(r => r.UnlockTime)
                    .ToList();
            }

            var snapshot = BuildSnapshotFromSourceLists();
            ApplyOverviewSummaryFromSnapshot(snapshot);
            RefreshFilter();
            ApplyLeftFilters();
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
                    return items.OrderBy(a => a.GlobalPercentUnlocked ?? 100);
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
                    !string.IsNullOrWhiteSpace(g.Provider) &&
                    _selectedProviderFilters.Contains(g.Provider));
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

            // Hide games with no unlocked
            if (!ShowGamesWithNoUnlocks)
            {
                filtered = filtered.Where(g => g.UnlockedAchievements > 0);
            }

            // Hide unplayed games, but show them if they have achievements
            if (!ShowUnplayedGames)
            {
                filtered = filtered.Where(g => g.LastPlayed.HasValue || g.UnlockedAchievements > 0);
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
            ProviderPieChart?.SetSelectedLabels(_selectedProviderFilters);
            GamesPieChart?.SetSelectedLabels(_selectedCompletenessFilters);
        }

        private void UpdateAggregatePieCharts()
        {
            // Filter games based only on ShowGamesWithNoUnlocks and ShowUnplayedGames settings
            var filteredGames = _allGamesOverview.AsEnumerable();
            if (!ShowGamesWithNoUnlocks)
            {
                filteredGames = filteredGames.Where(g => g.UnlockedAchievements > 0);
            }
            if (!ShowUnplayedGames)
            {
                filteredGames = filteredGames.Where(g => g.LastPlayed.HasValue || g.UnlockedAchievements > 0);
            }
            var gamesList = filteredGames.ToList();

            if (gamesList.Count == 0)
            {
                GamesPieChart?.SetGameData(0, 0, string.Empty, string.Empty);
                ProviderPieChart?.SetProviderData(new Dictionary<string, int>(), new Dictionary<string, int>(), 0, string.Empty, null, null);
                RarityPieChart?.SetRarityData(0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
                TrophyPieChart?.SetTrophyData(0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
                return;
            }

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

            // Games pie chart
            var totalGames = gamesList.Count;
            var completedGames = gamesList.Count(g => g.IsCompleted);
            GamesPieChart?.SetGameData(totalGames, completedGames, completedLabel, incompleteLabel);

            // Provider pie chart
            var unlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in gamesList)
            {
                var provider = string.IsNullOrWhiteSpace(game.ProviderKey) ? "Unknown" : game.ProviderKey;
                if (!unlockedByProvider.ContainsKey(provider))
                {
                    unlockedByProvider[provider] = 0;
                    totalByProvider[provider] = 0;
                }
                unlockedByProvider[provider] += game.UnlockedAchievements;
                totalByProvider[provider] += game.TotalAchievements;
            }
            var totalLocked = gamesList.Sum(g => g.TotalAchievements) - gamesList.Sum(g => g.UnlockedAchievements);
            var providerLookup = BuildProviderLookup();
            ProviderPieChart?.SetProviderData(unlockedByProvider, totalByProvider, totalLocked, lockedLabel, providerLookup, null);

            // Rarity pie chart
            var totalCommon = gamesList.Sum(g => g.CommonCount);
            var totalUncommon = gamesList.Sum(g => g.UncommonCount);
            var totalRare = gamesList.Sum(g => g.RareCount);
            var totalUltraRare = gamesList.Sum(g => g.UltraRareCount);
            var totalCommonPossible = gamesList.Sum(g => g.TotalCommonPossible);
            var totalUncommonPossible = gamesList.Sum(g => g.TotalUncommonPossible);
            var totalRarePossible = gamesList.Sum(g => g.TotalRarePossible);
            var totalUltraRarePossible = gamesList.Sum(g => g.TotalUltraRarePossible);
            RarityPieChart?.SetRarityData(
                totalCommon, totalUncommon, totalRare, totalUltraRare, totalLocked,
                totalCommonPossible, totalUncommonPossible, totalRarePossible, totalUltraRarePossible,
                commonLabel, uncommonLabel, rareLabel, ultraRareLabel, lockedLabel);

            // Trophy pie chart - filter achievements by games in filtered list
            var filteredGameIds = new HashSet<Guid>(gamesList.Where(g => g.PlayniteGameId.HasValue).Select(g => g.PlayniteGameId.Value));
            var filteredAchievements = (_allAchievements ?? new List<AchievementDisplayItem>())
                .Where(a => a != null && a.PlayniteGameId.HasValue && filteredGameIds.Contains(a.PlayniteGameId.Value));
            var trophySummary = BuildTrophySummary(filteredAchievements);
            TrophyPieChart?.SetTrophyData(
                trophySummary.PlatinumUnlocked, trophySummary.GoldUnlocked, trophySummary.SilverUnlocked, trophySummary.BronzeUnlocked,
                trophySummary.PlatinumTotal, trophySummary.GoldTotal, trophySummary.SilverTotal, trophySummary.BronzeTotal,
                trophyPlatinumLabel, trophyGoldLabel, trophySilverLabel, trophyBronzeLabel, lockedLabel);

            // Reset contextual pie chart titles to base titles (no game name suffix)
            RarityPieChartTitle = null;
            TrophyPieChartTitle = null;

            // Update pie chart selection states
            UpdateOverviewPieChartSelectionStates();
        }

        private Dictionary<string, (string iconKey, string colorHex)> BuildProviderLookup()
        {
            var providerLookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _achievementService.GetProviders())
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
        private async Task LoadSelectedGameAchievementsAndNotifyAsync()
        {
            await LoadSelectedGameAchievementsAsync().ConfigureAwait(true);

            // Fire notifications after data is ready
            OnPropertyChanged(nameof(IsGameSelected));
            OnPropertyChanged(nameof(TimelineSectionTitle));
            UpdateContextualPieCharts(_latestSnapshot);
        }

        private async Task LoadSelectedGameAchievementsAsync()
        {
            // Reset right search when selecting a game
            RightSearchText = string.Empty;

            if (SelectedGame == null)
            {
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
                return;
            }

            try
            {
                if (!SelectedGame.PlayniteGameId.HasValue)
                    return;

                var gameId = SelectedGame.PlayniteGameId.Value;
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
                    var gameData = _achievementService.GetGameAchievementData(gameId);
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
                        achievements
                            .OrderByDescending(a => a.Unlocked)
                            .ThenByDescending(a => a.UnlockTimeUtc ?? DateTime.MinValue)
                            .ThenBy(a => a.GlobalPercentUnlocked ?? 100)
                            .ToList(),
                        false);
                }).ConfigureAwait(true);

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
            }
            catch (Exception ex)
            {
                UpdateSelectedGameAchievementFilterOptions(null);
                SelectedGameHasCustomAchievementOrder = false;
                _logger?.Warn(ex, $"Failed to load achievements for game {SelectedGame?.AppId}");
            }
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
            // Quick reverse if same column
            if (_recentSortPath == sortMemberPath && _recentSortDirection == ListSortDirection.Ascending &&
                direction == ListSortDirection.Descending)
            {
                _filteredRecentAchievements.Reverse();
                _recentSortDirection = direction;
                if (RecentAchievements is BulkObservableCollection<AchievementDisplayItem> bulkRecent)
                {
                    bulkRecent.ReplaceAll(_filteredRecentAchievements);
                }
                else
                {
                    CollectionHelper.SynchronizeCollection(RecentAchievements, _filteredRecentAchievements);
                }
                return;
            }

            _recentSortPath = sortMemberPath;
            _recentSortDirection = direction;

            Comparison<AchievementDisplayItem> comparison = sortMemberPath switch
            {
                "DisplayName" => (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase),
                "SortingName" => (a, b) => string.Compare(a.SortingName ?? a.GameName, b.SortingName ?? b.GameName, StringComparison.OrdinalIgnoreCase),
                "CategoryType" => CompareRecentAchievementsByCategoryTypeThenUnlock,
                "CategoryLabel" => CompareRecentAchievementsByCategoryLabelThenUnlock,
                "UnlockTime" => CompareRecentAchievementsByUnlockColumn,
                "GlobalPercent" => (a, b) => (a.GlobalPercentUnlocked ?? 100).CompareTo(b.GlobalPercentUnlocked ?? 100),
                "Points" => (a, b) => a.Points.CompareTo(b.Points),
                "TrophyType" => CompareRecentAchievementsByTrophyType,
                _ => null
            };

            if (comparison != null)
            {
                if (direction == ListSortDirection.Descending)
                {
                    _filteredRecentAchievements.Sort((a, b) => comparison(b, a));
                }
                else
                {
                    _filteredRecentAchievements.Sort(comparison);
                }
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
            _selectedGameSortPath = sortMemberPath;
            _selectedGameSortDirection = direction;

            Comparison<AchievementDisplayItem> comparison = sortMemberPath switch
            {
                "DisplayName" => (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase),
                "UnlockTime" => CompareSelectedGameAchievementsByUnlockColumn,
                "CategoryType" => CompareSelectedGameAchievementsByCategoryTypeThenUnlock,
                "CategoryLabel" => CompareSelectedGameAchievementsByCategoryLabelThenUnlock,
                "GlobalPercent" => (a, b) => (a.GlobalPercentUnlocked ?? 100).CompareTo(b.GlobalPercentUnlocked ?? 100),
                "Points" => (a, b) => a.Points.CompareTo(b.Points),
                "TrophyType" => CompareSelectedGameAchievementsByTrophyType,
                _ => null
            };

            if (comparison != null)
            {
                // Keep tie ordering stable so refiltering doesn't appear to "lose" sort.
                var existingAllOrder = _allSelectedGameAchievements
                    .Select((item, index) => new { item, index })
                    .ToDictionary(x => x.item, x => x.index);

                _allSelectedGameAchievements.Sort((a, b) =>
                {
                    var result = direction == ListSortDirection.Descending ? comparison(b, a) : comparison(a, b);
                    if (result != 0)
                    {
                        return result;
                    }

                    if (existingAllOrder.TryGetValue(a, out var aIndex) && existingAllOrder.TryGetValue(b, out var bIndex))
                    {
                        return aIndex.CompareTo(bIndex);
                    }

                    return 0;
                });

                var sortedAllOrder = _allSelectedGameAchievements
                    .Select((item, index) => new { item, index })
                    .ToDictionary(x => x.item, x => x.index);

                _filteredSelectedGameAchievements.Sort((a, b) =>
                {
                    var result = direction == ListSortDirection.Descending ? comparison(b, a) : comparison(a, b);
                    if (result != 0)
                    {
                        return result;
                    }

                    if (sortedAllOrder.TryGetValue(a, out var aIndex) && sortedAllOrder.TryGetValue(b, out var bIndex))
                    {
                        return aIndex.CompareTo(bIndex);
                    }

                    return 0;
                });
            }

            if (SelectedGameAchievements is BulkObservableCollection<AchievementDisplayItem> bulkSelected2)
            {
                bulkSelected2.ReplaceAll(_filteredSelectedGameAchievements);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(SelectedGameAchievements, _filteredSelectedGameAchievements);
            }
        }

        private static int CompareRecentAchievementsByUnlockColumn(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var dateComparison = a.UnlockTime.CompareTo(b.UnlockTime);
            if (dateComparison != 0)
            {
                return dateComparison;
            }

            // Platinum > Gold > Silver > Bronze when unlock times are equal
            var trophyComparison = GetTrophyRank(b.TrophyType).CompareTo(GetTrophyRank(a.TrophyType));
            if (trophyComparison != 0)
            {
                return trophyComparison;
            }

            var progressComparison = CompareProgressFraction(a.ProgressNum, a.ProgressDenom, b.ProgressNum, b.ProgressDenom);
            if (progressComparison != 0)
            {
                return progressComparison;
            }

            var pointsComparison = a.Points.CompareTo(b.Points);
            if (pointsComparison != 0)
            {
                return pointsComparison;
            }

            var rarityComparison = a.GlobalPercent.CompareTo(b.GlobalPercent);
            if (rarityComparison != 0)
            {
                return rarityComparison;
            }

            var gameComparison = string.Compare(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase);
            if (gameComparison != 0)
            {
                return gameComparison;
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareRecentAchievementsByCategoryTypeThenUnlock(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var typeComparison = string.Compare(
                AchievementCategoryTypeHelper.ToDisplayText(a.CategoryType),
                AchievementCategoryTypeHelper.ToDisplayText(b.CategoryType),
                StringComparison.OrdinalIgnoreCase);
            if (typeComparison != 0)
            {
                return typeComparison;
            }

            var unlockComparison = a.UnlockTime.CompareTo(b.UnlockTime);
            if (unlockComparison != 0)
            {
                return unlockComparison;
            }

            var gameComparison = string.Compare(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase);
            if (gameComparison != 0)
            {
                return gameComparison;
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareRecentAchievementsByCategoryLabelThenUnlock(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var labelComparison = string.Compare(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.CategoryLabel),
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(b.CategoryLabel),
                StringComparison.OrdinalIgnoreCase);
            if (labelComparison != 0)
            {
                return labelComparison;
            }

            var unlockComparison = a.UnlockTime.CompareTo(b.UnlockTime);
            if (unlockComparison != 0)
            {
                return unlockComparison;
            }

            var gameComparison = string.Compare(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase);
            if (gameComparison != 0)
            {
                return gameComparison;
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareSelectedGameAchievementsByUnlockColumn(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var dateComparison = (a.UnlockTimeUtc ?? DateTime.MinValue).CompareTo(b.UnlockTimeUtc ?? DateTime.MinValue);
            if (dateComparison != 0)
            {
                return dateComparison;
            }

            // Platinum > Gold > Silver > Bronze when unlock times are equal
            var trophyComparison = GetTrophyRank(b.TrophyType).CompareTo(GetTrophyRank(a.TrophyType));
            if (trophyComparison != 0)
            {
                return trophyComparison;
            }

            var progressComparison = CompareProgressFraction(a.ProgressNum, a.ProgressDenom, b.ProgressNum, b.ProgressDenom);
            if (progressComparison != 0)
            {
                return progressComparison;
            }

            var unlockedComparison = a.Unlocked.CompareTo(b.Unlocked);
            if (unlockedComparison != 0)
            {
                return unlockedComparison;
            }

            var pointsComparison = a.Points.CompareTo(b.Points);
            if (pointsComparison != 0)
            {
                return pointsComparison;
            }

            var rarityComparison = a.GlobalPercent.CompareTo(b.GlobalPercent);
            if (rarityComparison != 0)
            {
                return rarityComparison;
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareSelectedGameAchievementsByCategoryTypeThenUnlock(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var typeComparison = string.Compare(
                AchievementCategoryTypeHelper.ToDisplayText(a.CategoryType),
                AchievementCategoryTypeHelper.ToDisplayText(b.CategoryType),
                StringComparison.OrdinalIgnoreCase);
            if (typeComparison != 0)
            {
                return typeComparison;
            }

            var unlockComparison = (a.UnlockTimeUtc ?? DateTime.MinValue).CompareTo(b.UnlockTimeUtc ?? DateTime.MinValue);
            if (unlockComparison != 0)
            {
                return unlockComparison;
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareSelectedGameAchievementsByCategoryLabelThenUnlock(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var labelComparison = string.Compare(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.CategoryLabel),
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(b.CategoryLabel),
                StringComparison.OrdinalIgnoreCase);
            if (labelComparison != 0)
            {
                return labelComparison;
            }

            var unlockComparison = (a.UnlockTimeUtc ?? DateTime.MinValue).CompareTo(b.UnlockTimeUtc ?? DateTime.MinValue);
            if (unlockComparison != 0)
            {
                return unlockComparison;
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareProgressFraction(int? aNum, int? aDenom, int? bNum, int? bDenom)
        {
            var aHasProgress = aNum.HasValue && aDenom.HasValue && aDenom.Value > 0;
            var bHasProgress = bNum.HasValue && bDenom.HasValue && bDenom.Value > 0;

            if (aHasProgress && bHasProgress)
            {
                var aFraction = (double)aNum.Value / aDenom.Value;
                var bFraction = (double)bNum.Value / bDenom.Value;
                var fractionComparison = aFraction.CompareTo(bFraction);
                if (fractionComparison != 0)
                {
                    return fractionComparison;
                }
            }

            if (aHasProgress != bHasProgress)
            {
                return aHasProgress ? 1 : -1;
            }

            return 0;
        }

        private static int GetTrophyRank(string trophyType)
        {
            if (string.IsNullOrWhiteSpace(trophyType)) return 0;
            return trophyType.ToLowerInvariant() switch
            {
                "platinum" => 4,
                "gold" => 3,
                "silver" => 2,
                "bronze" => 1,
                _ => 0
            };
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

        private static int CompareRecentAchievementsByTrophyType(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            return GetTrophyRank(a.TrophyType).CompareTo(GetTrophyRank(b.TrophyType));
        }

        private static int CompareSelectedGameAchievementsByTrophyType(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            return GetTrophyRank(a.TrophyType).CompareTo(GetTrophyRank(b.TrophyType));
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
            if (_achievementService != null)
            {
                _achievementService.RebuildProgress -= OnRebuildProgress;
                _achievementService.CacheDeltaUpdated -= OnCacheDeltaUpdated;
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



