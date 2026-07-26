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
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Models;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public class OverviewViewModel : ObservableObject, IDisposable, IOverviewRefreshHeaderViewModel
    {
        /// <summary>
        /// Returns true if unplayed games are included during refreshes.
        /// </summary>
        public bool IncludeUnplayedGames => _settings?.Persisted?.IncludeUnplayedGames ?? true;

        private readonly RefreshRuntime _refreshService;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementDataService _achievementDataService;
        private readonly LibraryProjectionService _libraryProjectionService;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly AchievementSelectionPipeline _selectedGamePipeline;
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly OverviewLaunchContext _launchContext;

        private readonly OverviewDataBuilder _dataBuilder;

        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _refreshCts;
        private volatile bool _isActive;
        private int _refreshVersion;
        private bool _disposed;

        private readonly HashSet<string> _revealedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private OverviewDataSnapshot _latestSnapshot;
        private bool _hasAppliedSnapshot;

        private readonly RefreshHeaderProgressTracker _progressTracker;
        private const int ContextualPieSeriesCount = 5;
        private System.Windows.Threading.DispatcherTimer _refreshDebounceTimer;
        private System.Windows.Threading.DispatcherTimer _deltaBatchTimer;
        private bool _isApplyingTimelineRange;
        private bool _selectedGameLoadInProgress;
        private bool _selectedGameContentReady;
        private CancellationTokenSource _selectedGameLoadCts;
        private readonly object _deltaSync = new object();
        private readonly HashSet<string> _pendingDeltaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _pendingFullResetFromDelta;

        private List<AchievementDisplayItem> _filteredRecentAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _allAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>();
        private readonly SearchTextIndex<AchievementDisplayItem> _globalAchievementSearchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForAchievementWithGame(item?.GameName, item?.DisplayName, item?.Description));
        private readonly SearchTextIndex<GameSummaryItem> _gameSummarySearchIndex =
            new SearchTextIndex<GameSummaryItem>(item => SearchTextBuilder.ForGameSummary(item?.GameName));
        private readonly SearchTextIndex<AchievementDisplayItem> _recentAchievementSearchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForRecentAchievement(item?.GameName, item?.Name));
        // Selected-game achievements grid: search box, Unlocked/Locked/Hidden toggles,
        // Type/Category filters, and the filter predicate all live in the shared adapter.
        private readonly AchievementGridControlBarAdapter _selectedGameControlBar = new AchievementGridControlBarAdapter();
        private List<string> _availableProviders = new List<string>();
        private readonly HashSet<string> _selectedCompletenessFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedPlayStatusFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Sort state tracking for quick reverse
        private string _overviewSortPath;
        private ListSortDirection _overviewSortDirection;
        private string _recentSortPath;
        private ListSortDirection _recentSortDirection;
        private string _selectedGameSortPath;
        private ListSortDirection _selectedGameSortDirection;


        internal OverviewViewModel(
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            AchievementDataService achievementDataService,
            LibraryProjectionService libraryProjectionService,
            GameCustomDataStore gameCustomDataStore,
            RefreshEntryPoint refreshEntryPoint,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            OverviewLaunchContext launchContext = OverviewLaunchContext.Sidebar)
        {
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _libraryProjectionService = libraryProjectionService;
            _gameCustomDataStore = gameCustomDataStore;
            _refreshCoordinator = refreshEntryPoint ?? throw new ArgumentNullException(nameof(refreshEntryPoint));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;
            _launchContext = launchContext;
            _dataBuilder = new OverviewDataBuilder(
                _achievementDataService,
                _refreshService.Providers,
                _playniteApi,
                _logger);
            _selectedGamePipeline = new AchievementSelectionPipeline(_achievementDataService, _settings);

            // Initialize debounce timer
            _refreshDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _refreshDebounceTimer.Tick += OnRefreshDebounceTimerTick;

            _progressTracker = new RefreshHeaderProgressTracker(_refreshService, _logger);
            _progressTracker.PropertyChanged += OnProgressTrackerChanged;

            _deltaBatchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _deltaBatchTimer.Tick += OnDeltaBatchTimerTick;

            // Initialize collections
            AllAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            GameSummaries = new BulkObservableCollection<GameSummaryItem>();
            RecentAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            SelectedGameAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            SelectedGameAllAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            CompletenessFilterOptions = new ObservableCollection<string>();

            // Default the progress dropdown to the full completed + incomplete scope.
            _selectedCompletenessFilters.Add(L("LOCPlayAch_Filter_Complete"));
            _selectedCompletenessFilters.Add(L("LOCPlayAch_Filter_InProgress"));

            // Initialize refresh mode options from service (exclude LibrarySelected - context menu only)
            RefreshModes = new ObservableCollection<RefreshMode>(
                _refreshService.GetRefreshModes().Where(m => m.Type != RefreshModeType.LibrarySelected));

            // Seed the dropdown from the user's configured default, if it's a valid overview mode.
            var configuredDefault = _settings?.Persisted?.DefaultOverviewRefreshMode ?? RefreshModeType.Installed;
            if (RefreshModes.Any(m => m.Type == configuredDefault))
            {
                _selectedRefreshMode = configuredDefault.GetKey();
            }

            GlobalTimeline = new TimelineViewModel();
            SelectedGameTimeline = new TimelineViewModel();
            InitializeTimelineRangePersistence();

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
            ApplyOverviewPieSmallSliceMode();

            // Set defaults: Unlocked Only, sorted by Unlock Date
            _showUnlockedOnly = true;
            _sortIndex = 2; // Unlock Date
            InitializeGridControlBars();
            _selectedGameControlBar.FilterChanged += OnSelectedGameControlBarFilterChanged;

            // Initialize commands
            RefreshViewCommand = new AsyncCommand(_ => RefreshViewAsync());
            RefreshCommand = new AsyncCommand(_ => ExecuteRefreshAsync(), _ => CanExecuteRefresh());
            CancelRefreshCommand = new RelayCommand(_ => CancelRefresh(), _ => IsRefreshing);
            RefreshOrCancelCommand = new RelayCommand(ExecuteRefreshOrCancel, _ => CanExecuteRefreshOrCancel());
            RevealAchievementCommand = new RelayCommand(param => RevealAchievement(param as AchievementDisplayItem));
            OpenGameInLibraryCommand = new RelayCommand(OpenGameInLibrary);
            OpenGameInOverviewCommand = new RelayCommand(OpenGameInOverview);
            RefreshSingleGameCommand = new AsyncCommand(ExecuteSingleGameRefreshAsync);
            CloseViewCommand = new RelayCommand(_ =>
            {
                try
                {
                    if (_playniteApi?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen)
                    {
                        CloseOverviewWindow();
                        return;
                    }

                    if (_launchContext == OverviewLaunchContext.Popout)
                    {
                        CloseOverviewWindow();
                        return;
                    }

                    PlayniteUiProvider.RestoreMainView();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to close overview view.");
                }
            });
            ClearGameSelectionCommand = new RelayCommand(_ => ClearGameSelection());
            NavigateToGameCommand = new RelayCommand(param => NavigateToGame(param as GameSummaryItem));

            // Subscribe to progress events
            _refreshService.CacheDeltaUpdated += OnCacheDeltaUpdated;
            _refreshService.CacheInvalidated += OnCacheInvalidated;
            if (_gameCustomDataStore != null)
            {
                _gameCustomDataStore.CustomDataChanged += OnCustomDataChanged;
            }
            if (_settings != null)
            {
                _settings.PropertyChanged += OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
                }
            }

        }

        private void InitializeTimelineRangePersistence()
        {
            ApplySavedTimelineRange();
            if (GlobalTimeline != null)
            {
                GlobalTimeline.PropertyChanged += Timeline_PropertyChanged;
            }

            if (SelectedGameTimeline != null)
            {
                SelectedGameTimeline.PropertyChanged += Timeline_PropertyChanged;
            }
        }

        private void InitializeGridControlBars()
        {
            GameSummariesControlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(LeftSearchText),
                    () => LeftSearchText,
                    value => LeftSearchText = value,
                    L("LOCPlayAch_Filter_Games"),
                    ClearLeftSearch)
            };
            GameSummariesControlBar.Items.Add(new GridProviderPlatformFilter(
                this,
                nameof(SelectedProviderFilterText),
                () => SelectedProviderFilterText,
                () => ProviderFilterGroups,
                CollapseUnselectedProviderFilters)
            {
                Width = 170
            });
            GameSummariesControlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCompletenessFilterText),
                () => SelectedCompletenessFilterText,
                () => CompletenessFilterOptions,
                IsCompletenessFilterSelected,
                SetCompletenessFilterSelected)
            {
                Width = 170
            });
            GameSummariesControlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedPlayStatusFilterText),
                () => SelectedPlayStatusFilterText,
                () => PlayStatusFilterOptions,
                IsPlayStatusFilterSelected,
                SetPlayStatusFilterSelected)
            {
                Width = 170
            });

            RecentAchievementsControlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(RightSearchText),
                    () => RightSearchText,
                    value => RightSearchText = value,
                    L("LOCPlayAch_Filter_Achievements"),
                    ClearRightSearch)
            };

            // The selected-game control bar is built and owned by _selectedGameControlBar.
        }

        private void Timeline_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isApplyingTimelineRange ||
                e?.PropertyName != nameof(TimelineViewModel.TimelineRange) ||
                !(sender is TimelineViewModel timeline))
            {
                return;
            }

            try
            {
                _isApplyingTimelineRange = true;
                if (!ReferenceEquals(timeline, GlobalTimeline) && GlobalTimeline != null)
                {
                    GlobalTimeline.TimelineRange = timeline.TimelineRange;
                }

                if (!ReferenceEquals(timeline, SelectedGameTimeline) && SelectedGameTimeline != null)
                {
                    SelectedGameTimeline.TimelineRange = timeline.TimelineRange;
                }

                if (_settings?.Persisted != null &&
                    _settings.Persisted.OverviewTimelineRange != timeline.TimelineRange)
                {
                    _settings.Persisted.OverviewTimelineRange = timeline.TimelineRange;
                    _persistSettingsForUi?.Invoke();
                }
            }
            finally
            {
                _isApplyingTimelineRange = false;
            }
        }

        private void ApplySavedTimelineRange()
        {
            var range = _settings?.Persisted?.OverviewTimelineRange ?? TimelineRange.OneYear;
            try
            {
                _isApplyingTimelineRange = true;
                if (GlobalTimeline != null && GlobalTimeline.TimelineRange != range)
                {
                    GlobalTimeline.TimelineRange = range;
                }

                if (SelectedGameTimeline != null && SelectedGameTimeline.TimelineRange != range)
                {
                    SelectedGameTimeline.TimelineRange = range;
                }
            }
            finally
            {
                _isApplyingTimelineRange = false;
            }
        }

        private void CloseOverviewWindow()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var overviewWindow = ResolveOverviewWindow();
                    if (overviewWindow == null)
                    {
                        _logger?.Debug("Overview close requested, but no overview window was found.");
                        return;
                    }

                    overviewWindow.Close();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to close overview window.");
                }
            }));
        }

        private static Window ResolveOverviewWindow()
        {
            var focusedOrActiveWindow = ResolveFocusedOrActiveWindow();
            if (IsOverviewWindow(focusedOrActiveWindow))
            {
                return focusedOrActiveWindow;
            }

            var application = Application.Current;
            return application?.Windows
                .OfType<Window>()
                .Where(window => !ReferenceEquals(window, application.MainWindow))
                .FirstOrDefault(IsOverviewWindow);
        }

        private static bool IsOverviewWindow(Window window)
        {
            if (window == null ||
                ReferenceEquals(window, Application.Current?.MainWindow))
            {
                return false;
            }

            var visited = new HashSet<DependencyObject>();
            return ContainsOverviewControl(window, visited) ||
                   ContainsOverviewControl(window.Content as DependencyObject, visited);
        }

        private static bool ContainsOverviewControl(DependencyObject root, ISet<DependencyObject> visited)
        {
            if (root == null || !visited.Add(root))
            {
                return false;
            }

            if (root is OverviewControl)
            {
                return true;
            }

            if (root is FullscreenOverlayContainer overlay &&
                ContainsOverviewControl(overlay.HostedContent, visited))
            {
                return true;
            }

            if (root is ContentControl contentControl &&
                ContainsOverviewControl(contentControl.Content as DependencyObject, visited))
            {
                return true;
            }

            foreach (var logicalChild in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (ContainsOverviewControl(logicalChild, visited))
                {
                    return true;
                }
            }

            if (!(root is Visual || root is Visual3D))
            {
                return false;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                if (ContainsOverviewControl(VisualTreeHelper.GetChild(root, i), visited))
                {
                    return true;
                }
            }

            return false;
        }

        private static Window ResolveFocusedOrActiveWindow()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            while (focused != null)
            {
                if (focused is Window focusedWindow)
                {
                    return focusedWindow;
                }

                var window = Window.GetWindow(focused);
                if (window != null)
                {
                    return window;
                }

                focused = GetDependencyObjectParent(focused);
            }

            var application = Application.Current;
            var activeWindow = application?.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive);

            return activeWindow ?? application?.MainWindow;
        }

        private static DependencyObject GetDependencyObjectParent(DependencyObject current)
        {
            if (current == null)
            {
                return null;
            }

            if (current is ContextMenu contextMenu && contextMenu.PlacementTarget != null)
            {
                return contextMenu.PlacementTarget;
            }

            if (current is System.Windows.Controls.Primitives.Popup popup && popup.PlacementTarget != null)
            {
                return popup.PlacementTarget;
            }

            if (current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(current);
                if (visualParent != null)
                {
                    return visualParent;
                }
            }

            if (current is FrameworkContentElement contentElement)
            {
                return contentElement.Parent ?? ContentOperations.GetParent(contentElement);
            }

            return LogicalTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.Parent;
        }

        #region Collections

        public ObservableCollection<AchievementDisplayItem> AllAchievements { get; }

        // Overview tab collections
        public ObservableCollection<GameSummaryItem> GameSummaries { get; }
        public ObservableCollection<AchievementDisplayItem> RecentAchievements { get; }
        public GridControlBarViewModel GameSummariesControlBar { get; private set; }
        public GridControlBarViewModel RecentAchievementsControlBar { get; private set; }
        public GridControlBarViewModel SelectedGameAchievementsControlBar => _selectedGameControlBar.ControlBar;

        private List<GameSummaryItem> _allGameSummaries = new List<GameSummaryItem>();
        private List<GameSummaryItem> _filteredGameSummaries = new List<GameSummaryItem>();

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

        // Shared by the selected-game and recent grids. The value lives in the adapter so the
        // selected-game control bar's search box and this property stay in sync.
        public string RightSearchText
        {
            get => _selectedGameControlBar.SearchText;
            set => _selectedGameControlBar.SearchText = value;
        }

        private void OnSelectedGameControlBarFilterChanged(object sender, EventArgs e)
        {
            // Keep the shared Recent search box in sync, then re-run the right-panel filters.
            OnPropertyChanged(nameof(RightSearchText));
            ApplyRightFilters();
        }

        private bool _selectedGameHasCustomAchievementOrder;
        public bool SelectedGameHasCustomAchievementOrder
        {
            get => _selectedGameHasCustomAchievementOrder;
            private set => SetValue(ref _selectedGameHasCustomAchievementOrder, value);
        }

        public string SelectedGameSortPath => _selectedGameSortPath;

        public ListSortDirection? SelectedGameSortDirection =>
            string.IsNullOrWhiteSpace(_selectedGameSortPath)
                ? (ListSortDirection?)null
                : _selectedGameSortDirection;

        public string OverviewSortPath => _overviewSortPath;

        public ListSortDirection? OverviewSortDirection =>
            string.IsNullOrWhiteSpace(_overviewSortPath)
                ? (ListSortDirection?)null
                : _overviewSortDirection;

        public string RecentSortPath => _recentSortPath;

        public ListSortDirection? RecentSortDirection =>
            string.IsNullOrWhiteSpace(_recentSortPath)
                ? (ListSortDirection?)null
                : _recentSortDirection;

        private ObservableCollection<ProviderFilterGroup> _providerFilterGroups
            = new ObservableCollection<ProviderFilterGroup>();
        public ObservableCollection<ProviderFilterGroup> ProviderFilterGroups
        {
            get => _providerFilterGroups;
            private set => SetValue(ref _providerFilterGroups, value);
        }

        public string SelectedProviderFilterText => GetSelectedProviderFilterText();

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

        /// <summary>
        /// Invoked by a provider group whenever its platform selection changes. Refreshes the box
        /// text and pie-chart highlight immediately and defers the grid filter to avoid interfering
        /// with the click that triggered it.
        /// </summary>
        private void OnProviderFilterSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            UpdateOverviewPieChartSelectionStates();
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(() => ApplyLeftFilters()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        public void ClearProviderFilters()
        {
            var groups = ProviderFilterGroups;
            if (groups == null)
            {
                return;
            }

            foreach (var group in groups.Where(g => g.HasAnySelected))
            {
                group.SetAll(false);
            }
        }

        /// <summary>
        /// Collapses provider sections that have no platform selected. Called when the dropdown
        /// closes so reopening it shows a tidy list with only the in-use sections expanded.
        /// </summary>
        public void CollapseUnselectedProviderFilters()
        {
            foreach (var group in ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
            {
                if (!group.HasAnySelected)
                {
                    group.IsExpanded = false;
                }
            }
        }

        /// <summary>
        /// Toggles all platforms for a provider when its pie slice is clicked.
        /// </summary>
        /// <param name="sliceLabel">The display label from the clicked slice</param>
        public void ToggleProviderFilterFromPieChart(string sliceLabel)
        {
            if (string.IsNullOrWhiteSpace(sliceLabel))
            {
                return;
            }

            // Check if "Locked" was clicked
            if (string.Equals(sliceLabel, L("LOCPlayAch_Common_Locked"), StringComparison.OrdinalIgnoreCase))
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

            var group = ProviderFilterGroups?.FirstOrDefault(
                g => string.Equals(g.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
            group?.ToggleAll();
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
            L("LOCPlayAch_Progress"));

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
                new Action(() => { ApplyLeftFilters(); UpdateAggregatePieCharts(); }),
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
            L("LOCPlayAch_Filter_ActivitySelectorPlaceholder"));

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
        /// Toggles progress filters when a games pie slice is clicked.
        /// The two-slice games pie maps Incomplete to both non-complete progress buckets.
        /// </summary>
        /// <param name="completenessLabel">The progress label from the clicked slice.</param>
        public void ToggleCompletenessFilterFromPieChart(string completenessLabel)
        {
            if (string.IsNullOrWhiteSpace(completenessLabel))
            {
                return;
            }

            var completeOption = L("LOCPlayAch_Filter_Complete");
            var inProgressOption = L("LOCPlayAch_Filter_InProgress");
            var noProgressOption = L("LOCPlayAch_Filter_NoProgress");
            var incompleteSliceLabel = L("LOCPlayAch_Overview_Incomplete");
            var targetFilters = new List<string>();

            if (string.Equals(completenessLabel, completeOption, StringComparison.OrdinalIgnoreCase))
            {
                targetFilters.Add(completeOption);
            }
            else if (string.Equals(completenessLabel, incompleteSliceLabel, StringComparison.OrdinalIgnoreCase))
            {
                targetFilters.Add(inProgressOption);
                targetFilters.Add(noProgressOption);
            }
            else
            {
                return;
            }

            var shouldSelect = targetFilters.Any(filter => !_selectedCompletenessFilters.Contains(filter));
            foreach (var filter in targetFilters)
            {
                if (shouldSelect)
                {
                    _selectedCompletenessFilters.Add(filter);
                }
                else
                {
                    _selectedCompletenessFilters.Remove(filter);
                }
            }

            OnPropertyChanged(nameof(SelectedCompletenessFilterText));
            UpdateOverviewPieChartSelectionStates();
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(() => { ApplyLeftFilters(); UpdateAggregatePieCharts(); }),
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
                    (RefreshOrCancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string RefreshModeSelectionText => RefreshModes?
            .FirstOrDefault(mode => string.Equals(mode?.Key, SelectedRefreshMode, StringComparison.Ordinal))?
            .ShortDisplayName
            ?? RefreshModes?.FirstOrDefault()?.ShortDisplayName
            ?? L("LOCPlayAch_Button_Refresh");

        public string RefreshActionButtonText => string.Equals(
            SelectedRefreshMode,
            RefreshModeType.Custom.GetKey(),
            StringComparison.Ordinal)
            ? ResourceProvider.GetString("LOCPlayAch_Button_Configure")
            : ResourceProvider.GetString("LOCPlayAch_Button_Refresh");

        public string RefreshOrCancelButtonText => IsRefreshing
            ? ResourceProvider.GetString("LOCPlayAch_Button_Cancel")
            : RefreshActionButtonText;

        public string RefreshOrCancelButtonGlyph => IsRefreshing
            ? "\uEEE4"
            : string.Equals(SelectedRefreshMode, RefreshModeType.Custom.GetKey(), StringComparison.Ordinal)
                ? "\uEFE1"
                : "\uEFD1";

        public bool UseCoverImagesGameSummaries => _settings?.Persisted?.OverviewGameSummariesUseCoverImages ?? true;

        public bool UseCoverImagesRecentAchievements => _settings?.Persisted?.OverviewRecentAchievementsUseCoverImages ?? true;

        public bool ShowRarityGlowRecentAchievements => _settings?.Persisted?.OverviewRecentAchievementsShowRarityGlow ?? true;

        public bool ShowRarityGlowSelectedGame => _settings?.Persisted?.OverviewSelectedGameShowRarityGlow ?? true;

        public bool ColorNamesByRarityRecentAchievements => _settings?.Persisted?.OverviewRecentAchievementsColorNamesByRarity ?? false;

        public bool ColorNamesByRaritySelectedGame => _settings?.Persisted?.OverviewSelectedGameColorNamesByRarity ?? false;

        public bool ColorRarityColumnsByRarityRecentAchievements => _settings?.Persisted?.OverviewRecentAchievementsColorRarityColumnsByRarity ?? false;

        public bool ColorRarityColumnsByRaritySelectedGame => _settings?.Persisted?.OverviewSelectedGameColorRarityColumnsByRarity ?? false;

        public bool ShowOverviewCollectionScoreCard => _settings?.Persisted?.ShowOverviewCollectionScoreCard ?? true;

        public bool ShowOverviewPrestigeScoreCard => _settings?.Persisted?.ShowOverviewPrestigeScoreCard ?? true;

        public bool ShowOverviewScoreCards => _hasAppliedSnapshot && (ShowOverviewCollectionScoreCard || ShowOverviewPrestigeScoreCard);

        public bool ShowOverviewScoreCardDivider => _hasAppliedSnapshot && ShowOverviewCollectionScoreCard && ShowOverviewPrestigeScoreCard;

        public ScoreCardViewModel CollectionScoreCard { get; } = new ScoreCardViewModel(ScoreCardType.Collection);

        public ScoreCardViewModel PrestigeScoreCard { get; } = new ScoreCardViewModel(ScoreCardType.Prestige);

        public bool ShowOverviewPieCharts =>
            ShowOverviewGamesPieChart ||
            ShowOverviewProviderPieChart ||
            ShowOverviewRarityPieChart ||
            ShowOverviewTrophyPieChart;

        public bool ShowOverviewGamesPieChart => _settings?.Persisted?.ShowOverviewGamesPieChart ?? true;

        public bool ShowOverviewProviderPieChart => _settings?.Persisted?.ShowOverviewProviderPieChart ?? true;

        public bool ShowOverviewRarityPieChart => _settings?.Persisted?.ShowOverviewRarityPieChart ?? true;

        public bool ShowOverviewTrophyPieChart => _settings?.Persisted?.ShowOverviewTrophyPieChart ?? true;

        public bool ShowOverviewPiePercentages => _settings?.Persisted?.ShowOverviewPiePercentages ?? true;

        public bool ShowOverviewBarCharts => _settings?.Persisted?.ShowOverviewBarCharts ?? true;

        public bool EnableFriendsFeatures => _settings?.Persisted?.EnableFriendsFeatures ?? true;

        public bool ShowOverviewGameMetadataPlatform => _settings?.Persisted?.ShowOverviewGameMetadataPlatform ?? true;

        public bool ShowOverviewGameMetadataPlaytime => _settings?.Persisted?.ShowOverviewGameMetadataPlaytime ?? true;

        public bool ShowOverviewGameMetadataRegion => _settings?.Persisted?.ShowOverviewGameMetadataRegion ?? true;

        public bool ShowCompletionGlow => _settings?.Persisted?.ShowCompletionGlow ?? true;

        public bool ShowOverviewGameSummariesGridColumnHeaders => _settings?.Persisted?.ShowOverviewGameSummariesGridColumnHeaders ?? true;

        public bool ShowOverviewRecentAchievementsGridColumnHeaders => _settings?.Persisted?.ShowOverviewRecentAchievementsGridColumnHeaders ?? true;

        public bool ShowOverviewSelectedGameGridColumnHeaders => _settings?.Persisted?.ShowOverviewSelectedGameGridColumnHeaders ?? true;

        public bool OverviewSelectedGameAchievementsHideCategorySummaryRow => _settings?.Persisted?.OverviewSelectedGameAchievementsHideCategorySummaryRow ?? false;

        public bool ShowOverviewSelectedGameCategorySummariesGridColumnHeaders => _settings?.Persisted?.ShowOverviewSelectedGameCategorySummariesGridColumnHeaders ?? true;

        public double? OverviewSelectedGameCategorySummariesGridRowHeight => _settings?.Persisted?.OverviewSelectedGameCategorySummariesGridRowHeight;

        public bool OverviewSelectedGameCategorySummariesUseCoverImages => _settings?.Persisted?.OverviewSelectedGameCategorySummariesUseCoverImages ?? false;

        public bool OverviewSelectedGameCategorySummariesShowCompletionGlow => _settings?.Persisted?.OverviewSelectedGameCategorySummariesShowCompletionGlow ?? true;

        public bool ShowOverviewGameSummariesGridControlBar => _settings?.Persisted?.ShowOverviewGameSummariesGridControlBar ?? true;

        public bool ShowOverviewRecentAchievementsGridControlBar => _settings?.Persisted?.ShowOverviewRecentAchievementsGridControlBar ?? true;

        public bool ShowOverviewSelectedGameGridControlBar => _settings?.Persisted?.ShowOverviewSelectedGameGridControlBar ?? true;

        public double? OverviewGameSummariesGridRowHeight => _settings?.Persisted?.OverviewGameSummariesGridRowHeight;

        public double? OverviewRecentAchievementsGridRowHeight => _settings?.Persisted?.OverviewRecentAchievementsGridRowHeight;

        public double? OverviewSelectedGameGridRowHeight => _settings?.Persisted?.OverviewSelectedGameGridRowHeight;

        public bool UseUniformRarityBadges => _settings?.Persisted?.UseUniformRarityBadges ?? false;

        private int _totalGameSummaries;
        public int TotalGameSummaries
        {
            get => _totalGameSummaries;
            private set => SetValue(ref _totalGameSummaries, value);
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

        private int _collectorScore;
        public int CollectorScore
        {
            get => _collectorScore;
            private set => SetValue(ref _collectorScore, value);
        }

        private int _collectorLevel;
        public int CollectorLevel
        {
            get => _collectorLevel;
            private set => SetValue(ref _collectorLevel, value);
        }

        private double _collectorLevelProgress;
        public double CollectorLevelProgress
        {
            get => _collectorLevelProgress;
            private set => SetValue(ref _collectorLevelProgress, value);
        }

        private string _collectorRank = "Bronze5";
        public string CollectorRank
        {
            get => _collectorRank;
            private set => SetValue(ref _collectorRank, value ?? "Bronze5");
        }

        private int _prestigeScore;
        public int PrestigeScore
        {
            get => _prestigeScore;
            private set => SetValue(ref _prestigeScore, value);
        }

        private int _prestigeLevel;
        public int PrestigeLevel
        {
            get => _prestigeLevel;
            private set => SetValue(ref _prestigeLevel, value);
        }

        private double _prestigeLevelProgress;
        public double PrestigeLevelProgress
        {
            get => _prestigeLevelProgress;
            private set => SetValue(ref _prestigeLevelProgress, value);
        }

        private string _prestigeRank = "Bronze5";
        public string PrestigeRank
        {
            get => _prestigeRank;
            private set => SetValue(ref _prestigeRank, value ?? "Bronze5");
        }

        private GameSummaryItem _displayedSelectedGame;
        public GameSummaryItem DisplayedSelectedGame => _displayedSelectedGame;

        private void SetDisplayedSelectedGame(GameSummaryItem value)
        {
            if (SetValueAndReturn(ref _displayedSelectedGame, value, nameof(DisplayedSelectedGame)))
            {
                OnPropertyChanged(nameof(TimelineSectionTitle));
            }
        }

        private GameSummaryItem _selectedGame;
        public GameSummaryItem SelectedGame
        {
            get => _selectedGame;
            set
            {
                var previousGameId = _selectedGame?.PlayniteGameId;
                var newGameId = value?.PlayniteGameId;
                var keepDisplayedContent = newGameId.HasValue && IsSelectedGameContentReady;

                if (SetValueAndReturn(ref _selectedGame, value))
                {
                    if (previousGameId != newGameId)
                    {
                        ResetSelectedGameSortToDefault();
                    }

                    _selectedGameControlBar.ResetFilters();
                    RefreshSelectedGameHeaderCounts();
                    (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                    (RefreshOrCancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    _selectedGameContentReady = keepDisplayedContent;
                    if (!newGameId.HasValue)
                    {
                        SetDisplayedSelectedGame(null);
                    }

                    _selectedGameLoadInProgress = newGameId.HasValue;
                    NotifySelectedGameViewStateChanged();
                    OnPropertyChanged(nameof(TimelineSectionTitle));
                    CancelSelectedGameLoad();
                    _selectedGameLoadCts = new CancellationTokenSource();

                    // Defer visibility/data notifications until after data loads to prevent flash
                    _ = LoadSelectedGameAchievementsAndNotifyAsync(newGameId, _selectedGameLoadCts.Token);
                }
            }
        }

        public bool IsGameSelected => SelectedGame != null;
        public bool IsSelectedGameContentReady => DisplayedSelectedGame != null && _selectedGameContentReady;
        public bool ShowRecentAchievementsPanel =>
            SelectedGame == null || (!IsSelectedGameContentReady && DisplayedSelectedGame == null);

        private void NotifySelectedGameViewStateChanged()
        {
            OnPropertyChanged(nameof(IsGameSelected));
            OnPropertyChanged(nameof(IsSelectedGameContentReady));
            OnPropertyChanged(nameof(ShowRecentAchievementsPanel));
        }

        private string _selectedGameHeaderText;
        public string SelectedGameHeaderText
        {
            get => _selectedGameHeaderText;
            private set => SetValue(ref _selectedGameHeaderText, value);
        }

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

        // Full, unfiltered selected-game achievements feeding the category-summaries source so its
        // rollups stay stable when the Unlocked/Locked/Hidden filters are applied within a drill.
        public ObservableCollection<AchievementDisplayItem> SelectedGameAllAchievements { get; }

        // The category the selected-game grid is currently drilled into (null when not drilled),
        // pushed up from AchievementDataGridControl so the header count can scope to it.
        private string _selectedGameDrilledCategory;
        public string SelectedGameDrilledCategory
        {
            get => _selectedGameDrilledCategory;
            set
            {
                if (SetValueAndReturn(ref _selectedGameDrilledCategory, value))
                {
                    OnPropertyChanged(nameof(IsSelectedGameDrilledIntoCategory));
                    RefreshSelectedGameHeaderCounts();
                }
            }
        }

        // Drives the breadcrumb's "> CategoryName" segment and the clickable game-name affordance.
        public bool IsSelectedGameDrilledIntoCategory => !string.IsNullOrEmpty(SelectedGameDrilledCategory);

        public ObservableCollection<ChartDataPoint> SelectedGameDailyUnlocks { get; } = new ObservableCollection<ChartDataPoint>();

        #endregion

        #region Timeline Properties

        public TimelineViewModel GlobalTimeline { get; private set; }
        public TimelineViewModel SelectedGameTimeline { get; private set; }
        public string TimelineSectionTitle
        {
            get
            {
                var title = L("LOCPlayAch_Section_Timeline");
                var selectedGameName = IsSelectedGameContentReady ? DisplayedSelectedGame?.GameName : null;
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
                ? L("LOCPlayAch_Overview_RarityPieChart")
                : _rarityPieChartTitle;
            private set => SetValue(ref _rarityPieChartTitle, value);
        }

        private string _trophyPieChartTitle;
        public string TrophyPieChartTitle
        {
            get => string.IsNullOrWhiteSpace(_trophyPieChartTitle)
                ? L("LOCPlayAch_Overview_TrophyPieChart")
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

        public bool IsRefreshing => _progressTracker.IsRefreshing;

        public double ProgressPercent => _progressTracker.ProgressPercent;

        public string ProgressMessage => _progressTracker.ProgressMessage;

        public bool ShowProgress => _progressTracker.ShowProgress;

        private void OnProgressTrackerChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(RefreshHeaderProgressTracker.ProgressPercent):
                    OnPropertyChanged(nameof(ProgressPercent));
                    break;
                case nameof(RefreshHeaderProgressTracker.ProgressMessage):
                    OnPropertyChanged(nameof(ProgressMessage));
                    break;
                case nameof(RefreshHeaderProgressTracker.IsRefreshing):
                    OnPropertyChanged(nameof(IsRefreshing));
                    RaiseCommandsChanged();
                    break;
                case nameof(RefreshHeaderProgressTracker.ShowProgress):
                    OnPropertyChanged(nameof(ShowProgress));
                    break;
            }
        }

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
        public ICommand RefreshOrCancelCommand { get; }
        public ICommand RevealAchievementCommand { get; }
        public ICommand OpenGameInLibraryCommand { get; }
        public ICommand OpenGameInOverviewCommand { get; }
        public ICommand RefreshSingleGameCommand { get; }
        public ICommand CloseViewCommand { get; }
        public string CloseViewToolTip =>
            _launchContext == OverviewLaunchContext.Sidebar ? "Back to Library" : "Close";
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
                _progressTracker.NotifyDeactivated();
                CancelPendingRefresh();
            }
            else
            {
                _progressTracker.SyncToCurrentState();
                // Refresh data when overview becomes active to ensure cached changes are visible
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
                    // Spoiler reveals only affect the per-game grids (a separate per-game
                    // path); the overview snapshot carries no per-achievement display items,
                    // so it builds without reveal state.
                    OverviewDataSnapshot snapshot;
                    snapshot = await Task.Run(
                        () => _libraryProjectionService != null
                            ? _libraryProjectionService.GetOverviewSnapshot(_settings, cancel)
                            : _dataBuilder.Build(_settings, cancel),
                        cancel).ConfigureAwait(false);

                    // Still off the UI thread: precompute the search-text maps so ApplySnapshot
                    // only performs a cheap swap instead of tokenizing every item on the UI thread.
                    Dictionary<AchievementDisplayItem, string> globalEntries;
                    Dictionary<GameSummaryItem, string> gameEntries;
                    Dictionary<AchievementDisplayItem, string> recentEntries;
                    using (PerfScope.Start(_logger, "Overview.BuildSearchEntries", thresholdMs: 15))
                    {
                        globalEntries = _globalAchievementSearchIndex.BuildEntries(snapshot?.Achievements);
                        gameEntries = _gameSummarySearchIndex.BuildEntries(snapshot?.GameSummaries);
                        recentEntries = _recentAchievementSearchIndex.BuildEntries(snapshot?.RecentAchievements);
                    }

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

                        using (PerfScope.Start(_logger, "Overview.ApplySnapshot", thresholdMs: 15))
                        {
                            ApplySnapshot(snapshot, globalEntries, gameEntries, recentEntries);
                        }
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
                _logger?.Error(ex, "Failed to refresh overview achievements");
                StatusText = string.Format(ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed"), ex.Message);
            }
        }

        public void CancelRefresh()
        {
            _refreshService.CancelCurrentRebuild();
        }

        private bool CanExecuteRefreshOrCancel()
        {
            if (IsRefreshing)
            {
                return true;
            }

            return CanExecuteRefresh();
        }

        private void ExecuteRefreshOrCancel(object parameter)
        {
            if (IsRefreshing)
            {
                CancelRefresh();
                return;
            }

            if (CanExecuteRefresh())
            {
                _ = ExecuteRefreshAsync();
            }
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

                _progressTracker.NotifyRefreshStarting();

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
                    _progressTracker.SyncToCurrentState();
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
                    Options = RefreshOptions.FromCustom(customOptions)
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
                    StatusText = ResourceProvider.GetString("LOCPlayAch_Overview_NoGameSelected");
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
            OnPropertyChanged(nameof(RefreshOrCancelButtonText));
            OnPropertyChanged(nameof(RefreshOrCancelButtonGlyph));
            OnPropertyChanged(nameof(RefreshModeSelectionText));
        }

        private void NavigateToGame(GameSummaryItem game)
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
                _progressTracker.NotifyRefreshStarting();

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
                _progressTracker.SyncToCurrentState();
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

        private void OpenGameInOverview(object parameter)
        {
            if (!TryGetPlayniteGameId(parameter, out var gameId))
            {
                return;
            }

            try
            {
                var targetGame =
                    GameSummaries.FirstOrDefault(g => g?.PlayniteGameId == gameId) ??
                    _allGameSummaries.FirstOrDefault(g => g?.PlayniteGameId == gameId);

                if (targetGame != null)
                {
                    SelectedGame = targetGame;
                }
                else
                {
                    _logger?.Warn($"Overview game view target not found for game ID {gameId}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to open game in overview view: {gameId}");
            }
        }

        private static bool TryGetPlayniteGameId(object parameter, out Guid gameId)
        {
            switch (parameter)
            {
                case GameSummaryItem game when game.PlayniteGameId.HasValue:
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

        private void ApplySnapshot(
            OverviewDataSnapshot snapshot,
            Dictionary<AchievementDisplayItem, string> globalSearchEntries = null,
            Dictionary<GameSummaryItem, string> gameSummarySearchEntries = null,
            Dictionary<AchievementDisplayItem, string> recentSearchEntries = null)
        {
            if (_disposed)
            {
                return;
            }

            if (IsTransientRebuildSnapshot(snapshot))
            {
                return;
            }

            _selectedGamePipeline.InvalidateAll();

            _latestSnapshot = snapshot;
            _allAchievements = snapshot.Achievements ?? new List<AchievementDisplayItem>();
            if (globalSearchEntries != null)
            {
                _globalAchievementSearchIndex.LoadEntries(globalSearchEntries);
            }
            else
            {
                _globalAchievementSearchIndex.Rebuild(_allAchievements);
            }

            CollectionHelper.Replace(AllAchievements, _allAchievements);

            _allGameSummaries = snapshot.GameSummaries ?? new List<GameSummaryItem>();
            if (gameSummarySearchEntries != null)
            {
                _gameSummarySearchIndex.LoadEntries(gameSummarySearchEntries);
            }
            else
            {
                _gameSummarySearchIndex.Rebuild(_allGameSummaries);
            }

            SetRecentAchievementsSource(
                snapshot.RecentAchievements,
                recentSearchEntries);

            UpdateProviderFilterOptions(_allGameSummaries);
            UpdateCompletenessFilterOptions();
            UpdatePlayStatusFilterOptions();

            // Initialize filtered lists
            _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();

            ApplyOverviewSummaryCore(snapshot, updateProviderFilterOptions: false);

            RefreshFilter();
            ApplyLeftFilters();
            using (PerfScope.Start(_logger, "Overview.UpdateAggregatePieCharts", thresholdMs: 15))
            {
                UpdateAggregatePieCharts();
            }

            SyncRecentAchievementsDisplay();

            RefreshSelectedGameHeaderCounts();
            UpdateFilteredStatus();
        }

        private bool IsTransientRebuildSnapshot(OverviewDataSnapshot snapshot)
        {
            if (!_refreshService.IsRebuilding || snapshot?.GameSummaries == null)
            {
                return false;
            }

            var currentCount = _allGameSummaries?.Count ?? 0;
            return currentCount > 0 && snapshot.GameSummaries.Count < currentCount;
        }

        private void SetRecentAchievementsSource(
            List<AchievementDisplayItem> recentAchievements,
            Dictionary<AchievementDisplayItem, string> recentSearchEntries = null)
        {
            _allRecentAchievements = recentAchievements ?? new List<AchievementDisplayItem>();
            _filteredRecentAchievements = new List<AchievementDisplayItem>(_allRecentAchievements);
            if (recentSearchEntries != null)
            {
                _recentAchievementSearchIndex.LoadEntries(recentSearchEntries);
            }
            else
            {
                _recentAchievementSearchIndex.Rebuild(_allRecentAchievements);
            }
        }

        private bool ApplyFragmentDelta(string key, OverviewGameFragment fragment)
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
                    _logger?.Warn($"Incremental overview delta ignored because key is not a valid game id: {key}");
                    return false;
                }
            }

            if (fragment == null)
            {
                if (_refreshService.IsRebuilding &&
                    _allGameSummaries.Any(g => g?.PlayniteGameId == gameId))
                {
                    return true;
                }

                _allAchievements.RemoveAll(a => a?.PlayniteGameId == gameId);
                _allGameSummaries.RemoveAll(g => g?.PlayniteGameId == gameId);
                _allRecentAchievements.RemoveAll(r => r?.PlayniteGameId == gameId);
                _selectedGamePipeline.Invalidate(gameId);

                if (SelectedGame?.PlayniteGameId == gameId)
                {
                    SelectedGame = null;
                }

                return true;
            }

            _allAchievements.RemoveAll(a => a?.PlayniteGameId == gameId);
            _allGameSummaries.RemoveAll(g => g?.PlayniteGameId == gameId);
            _allRecentAchievements.RemoveAll(r => r?.PlayniteGameId == gameId);
            _selectedGamePipeline.Invalidate(gameId);

            if (fragment.Achievements != null && fragment.Achievements.Count > 0)
            {
                _allAchievements.AddRange(fragment.Achievements);
            }

            if (fragment.GameSummary != null)
            {
                _allGameSummaries.Add(fragment.GameSummary);
            }

            if (fragment.RecentAchievements != null && fragment.RecentAchievements.Count > 0)
            {
                _allRecentAchievements.AddRange(fragment.RecentAchievements);
            }

            return true;
        }

        private OverviewDataSnapshot BuildSnapshotFromSourceLists()
        {
            var snapshot = new OverviewDataSnapshot
            {
                Achievements = _allAchievements ?? new List<AchievementDisplayItem>(),
                GameSummaries = _allGameSummaries ?? new List<GameSummaryItem>(),
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

                if (!item.UnlockTimeUtc.HasValue)
                {
                    continue;
                }

                var date = DateTimeUtilities.AsUtcKind(item.UnlockTimeUtc.Value).Date;
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

            snapshot.TotalGames = snapshot.GameSummaries.Count;
            snapshot.TotalAchievements = snapshot.GameSummaries.Sum(g => g?.TotalAchievements ?? 0);
            snapshot.TotalUnlocked = snapshot.GameSummaries.Sum(g => g?.UnlockedAchievements ?? 0);
            snapshot.TotalCommon = snapshot.GameSummaries.Sum(g => g?.CommonCount ?? 0);
            snapshot.TotalUncommon = snapshot.GameSummaries.Sum(g => g?.UncommonCount ?? 0);
            snapshot.TotalRare = snapshot.GameSummaries.Sum(g => g?.RareCount ?? 0);
            snapshot.TotalUltraRare = snapshot.GameSummaries.Sum(g => g?.UltraRareCount ?? 0);
            snapshot.CompletedGames = snapshot.GameSummaries.Count(g => g?.IsCompleted == true);
            snapshot.TotalLocked = Math.Max(0, snapshot.TotalAchievements - snapshot.TotalUnlocked);
            snapshot.GlobalProgressionPercent = snapshot.TotalAchievements > 0
                ? (double)snapshot.TotalUnlocked / snapshot.TotalAchievements * 100
                : 0;

            snapshot.TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < snapshot.GameSummaries.Count; i++)
            {
                var game = snapshot.GameSummaries[i];
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
                snapshot.CollectorScore = AddClamped(snapshot.CollectorScore, game.CollectionScore);
                snapshot.PrestigeScore = AddClamped(snapshot.PrestigeScore, game.PrestigeScore);
            }

            // Aggregate rarity "possible" totals from GameSummaries
            snapshot.TotalCommonPossible = snapshot.GameSummaries.Sum(g => g?.TotalCommonPossible ?? 0);
            snapshot.TotalUncommonPossible = snapshot.GameSummaries.Sum(g => g?.TotalUncommonPossible ?? 0);
            snapshot.TotalRarePossible = snapshot.GameSummaries.Sum(g => g?.TotalRarePossible ?? 0);
            snapshot.TotalUltraRarePossible = snapshot.GameSummaries.Sum(g => g?.TotalUltraRarePossible ?? 0);
            ApplyScoreSnapshotFromValues(snapshot, snapshot.CollectorScore, snapshot.PrestigeScore);

            return snapshot;
        }

        private static void ApplyScoreSnapshotFromValues(
            OverviewDataSnapshot snapshot,
            int collectionScore,
            int prestigeScore)
        {
            if (snapshot == null)
            {
                return;
            }

            var scoreSnapshot = AchievementScoreCalculator.CreateModernScoreSnapshot(
                collectionScore,
                prestigeScore);

            snapshot.CollectorScore = scoreSnapshot.CollectorScore;
            snapshot.CollectorLevel = GetDisplayLevel(scoreSnapshot.CollectorLevel);
            snapshot.CollectorLevelProgress = scoreSnapshot.CollectorLevel?.LevelProgress ?? 0;
            snapshot.CollectorRank = scoreSnapshot.CollectorLevel?.Rank ?? "Bronze5";

            snapshot.PrestigeScore = scoreSnapshot.PrestigeScore;
            snapshot.PrestigeLevel = GetDisplayLevel(scoreSnapshot.PrestigeLevel);
            snapshot.PrestigeLevelProgress = scoreSnapshot.PrestigeLevel?.LevelProgress ?? 0;
            snapshot.PrestigeRank = scoreSnapshot.PrestigeLevel?.Rank ?? "Bronze5";
        }

        private static int GetDisplayLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

            return snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level;
        }

        private void ApplyOverviewSummaryFromSnapshot(OverviewDataSnapshot snapshot)
        {
            ApplyOverviewSummaryCore(snapshot, updateProviderFilterOptions: true);
        }

        private void ApplyOverviewSummaryCore(OverviewDataSnapshot snapshot, bool updateProviderFilterOptions)
        {
            if (snapshot == null)
            {
                return;
            }

            NormalizeScoreSnapshot(snapshot);
            _latestSnapshot = snapshot;
            if (updateProviderFilterOptions)
            {
                UpdateProviderFilterOptions(snapshot.GameSummaries ?? new List<GameSummaryItem>());
                UpdateCompletenessFilterOptions();
            }

            _totalCount = snapshot.TotalAchievements;
            _unlockedCount = snapshot.TotalUnlocked;
            _gamesCount = snapshot.TotalGames;

            TotalGameSummaries = snapshot.TotalGames;
            TotalAchievementsOverview = snapshot.TotalAchievements;
            TotalUnlockedOverview = snapshot.TotalUnlocked;
            TotalCommon = snapshot.TotalCommon;
            TotalUncommon = snapshot.TotalUncommon;
            TotalRare = snapshot.TotalRare;
            TotalUltraRare = snapshot.TotalUltraRare;
            CompletedGames = snapshot.CompletedGames;
            GlobalProgression = snapshot.GlobalProgressionPercent;
            CollectorScore = snapshot.CollectorScore;
            CollectorLevel = snapshot.CollectorLevel;
            CollectorLevelProgress = snapshot.CollectorLevelProgress;
            CollectorRank = snapshot.CollectorRank;
            PrestigeScore = snapshot.PrestigeScore;
            PrestigeLevel = snapshot.PrestigeLevel;
            PrestigeLevelProgress = snapshot.PrestigeLevelProgress;
            PrestigeRank = snapshot.PrestigeRank;
            ApplyScoreCards();
            MarkSnapshotApplied();

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

        private void MarkSnapshotApplied()
        {
            if (_hasAppliedSnapshot)
            {
                return;
            }

            _hasAppliedSnapshot = true;
            OnPropertyChanged(nameof(ShowOverviewScoreCards));
            OnPropertyChanged(nameof(ShowOverviewScoreCardDivider));
        }

        private void ApplyScoreCards()
        {
            var useUniformRarityBadges = UseUniformRarityBadges;
            CollectionScoreCard.Apply(
                CollectorScore,
                CollectorLevel,
                CollectorLevelProgress,
                CollectorRank,
                useUniformRarityBadges);
            PrestigeScoreCard.Apply(
                PrestigeScore,
                PrestigeLevel,
                PrestigeLevelProgress,
                PrestigeRank,
                useUniformRarityBadges);
        }

        private void NormalizeScoreSnapshot(OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (snapshot.CollectorScore > 0 || snapshot.PrestigeScore > 0)
            {
                ApplyScoreSnapshotFromValues(snapshot, snapshot.CollectorScore, snapshot.PrestigeScore);
                return;
            }

            if (!IsRefreshing || (CollectorScore <= 0 && PrestigeScore <= 0))
            {
                return;
            }

            snapshot.CollectorScore = CollectorScore;
            snapshot.CollectorLevel = CollectorLevel;
            snapshot.CollectorLevelProgress = CollectorLevelProgress;
            snapshot.CollectorRank = CollectorRank;
            snapshot.PrestigeScore = PrestigeScore;
            snapshot.PrestigeLevel = PrestigeLevel;
            snapshot.PrestigeLevelProgress = PrestigeLevelProgress;
            snapshot.PrestigeRank = PrestigeRank;
        }

        private static int AddClamped(int current, int value)
        {
            if (value <= 0)
            {
                return current;
            }

            if (current > int.MaxValue - value)
            {
                return int.MaxValue;
            }

            return current + value;
        }

        private void UpdateProviderFilterOptions(List<GameSummaryItem> games)
        {
            var gameList = games ?? new List<GameSummaryItem>();

            // Snapshot prior selections and expansion so they survive the rebuild.
            var priorSelections = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var priorExpanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var priorSelectedCount = 0;
            foreach (var existing in ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
            {
                var selected = existing.SelectedPlatformNames.ToList();
                if (selected.Count > 0)
                {
                    priorSelections[existing.ProviderKey] =
                        new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                    priorSelectedCount += selected.Count;
                }

                if (existing.IsExpanded)
                {
                    priorExpanded.Add(existing.ProviderKey);
                }
            }

            // Group games by provider, collecting the distinct platform names each provider has.
            var platformsByProvider = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in gameList)
            {
                var providerKey = game?.ProviderKey;
                if (string.IsNullOrWhiteSpace(providerKey))
                {
                    continue;
                }

                if (!platformsByProvider.TryGetValue(providerKey, out var platforms))
                {
                    platforms = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                    platformsByProvider[providerKey] = platforms;
                }

                foreach (var platform in game.Platforms ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(platform))
                    {
                        platforms.Add(platform.Trim());
                    }
                }
            }

            var groups = new List<ProviderFilterGroup>();
            var newSelectedCount = 0;
            foreach (var providerKey in platformsByProvider.Keys
                .OrderBy(GetProviderFilterDisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var platformNames = platformsByProvider[providerKey].ToList();
                if (platformNames.Count == 0)
                {
                    // Provider with no platform metadata: a single synthetic option lets the parent
                    // checkbox select/clear the provider as a whole.
                    platformNames.Add(GetProviderFilterDisplayName(providerKey));
                }

                priorSelections.TryGetValue(providerKey, out var selectedSet);
                var group = new ProviderFilterGroup(
                    providerKey,
                    GetProviderFilterDisplayName(providerKey),
                    platformNames,
                    name => selectedSet != null && selectedSet.Contains(name),
                    OnProviderFilterSelectionChanged)
                {
                    IsExpanded = priorExpanded.Contains(providerKey)
                };
                groups.Add(group);
                newSelectedCount += group.SelectedPlatformNames.Count();
            }

            ProviderFilterGroups = new ObservableCollection<ProviderFilterGroup>(groups);

            // A drop in the selected count means a previously-selected platform/provider disappeared,
            // so the visible game set may have changed and the grid filter must be reapplied.
            if (newSelectedCount != priorSelectedCount)
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
                L("LOCPlayAch_Filter_Complete"),
                L("LOCPlayAch_Filter_InProgress"),
                L("LOCPlayAch_Filter_NoProgress")
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
                L("LOCPlayAch_Filter_Played"),
                L("LOCPlayAch_Filter_Unplayed")
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
                OnPropertyChanged(nameof(UseCoverImagesGameSummaries));
                OnPropertyChanged(nameof(UseCoverImagesRecentAchievements));
                OnPropertyChanged(nameof(ShowRarityGlowRecentAchievements));
                OnPropertyChanged(nameof(ShowRarityGlowSelectedGame));
                OnPropertyChanged(nameof(ColorNamesByRarityRecentAchievements));
                OnPropertyChanged(nameof(ColorNamesByRaritySelectedGame));
                OnPropertyChanged(nameof(ColorRarityColumnsByRarityRecentAchievements));
                OnPropertyChanged(nameof(ColorRarityColumnsByRaritySelectedGame));
                OnPropertyChanged(nameof(IncludeUnplayedGames));
                RaiseOverviewScoreCardVisibilityChanged();
                ApplyOverviewPieSmallSliceMode();
                RaiseOverviewPieChartVisibilityChanged();
                OnPropertyChanged(nameof(ShowOverviewPiePercentages));
                OnPropertyChanged(nameof(ShowOverviewBarCharts));
                OnPropertyChanged(nameof(EnableFriendsFeatures));
                OnPropertyChanged(nameof(ShowOverviewGameMetadataPlatform));
                OnPropertyChanged(nameof(ShowOverviewGameMetadataPlaytime));
                OnPropertyChanged(nameof(ShowOverviewGameMetadataRegion));
                OnPropertyChanged(nameof(ShowCompletionGlow));
                OnPropertyChanged(nameof(ShowOverviewGameSummariesGridColumnHeaders));
                OnPropertyChanged(nameof(ShowOverviewRecentAchievementsGridColumnHeaders));
                OnPropertyChanged(nameof(ShowOverviewSelectedGameGridColumnHeaders));
                OnPropertyChanged(nameof(OverviewSelectedGameAchievementsHideCategorySummaryRow));
                OnPropertyChanged(nameof(ShowOverviewSelectedGameCategorySummariesGridColumnHeaders));
                OnPropertyChanged(nameof(OverviewSelectedGameCategorySummariesGridRowHeight));
                OnPropertyChanged(nameof(OverviewSelectedGameCategorySummariesUseCoverImages));
                OnPropertyChanged(nameof(OverviewSelectedGameCategorySummariesShowCompletionGlow));
                OnPropertyChanged(nameof(ShowOverviewGameSummariesGridControlBar));
                OnPropertyChanged(nameof(ShowOverviewRecentAchievementsGridControlBar));
                OnPropertyChanged(nameof(ShowOverviewSelectedGameGridControlBar));
                OnPropertyChanged(nameof(OverviewGameSummariesGridRowHeight));
                OnPropertyChanged(nameof(OverviewRecentAchievementsGridRowHeight));
                OnPropertyChanged(nameof(OverviewSelectedGameGridRowHeight));
                OnPropertyChanged(nameof(UseUniformRarityBadges));
                ApplyScoreCards();
                ApplySavedTimelineRange();
                _ = RefreshViewAsync();
                ApplyLeftFilters();
                UpdateAggregatePieCharts();
                return;
            }

            if (propertyName == nameof(PersistedSettings.OverviewGameSummariesUseCoverImages))
            {
                OnPropertyChanged(nameof(UseCoverImagesGameSummaries));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewRecentAchievementsUseCoverImages))
            {
                OnPropertyChanged(nameof(UseCoverImagesRecentAchievements));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewRecentAchievementsShowRarityGlow))
            {
                OnPropertyChanged(nameof(ShowRarityGlowRecentAchievements));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameShowRarityGlow))
            {
                OnPropertyChanged(nameof(ShowRarityGlowSelectedGame));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewRecentAchievementsColorNamesByRarity))
            {
                OnPropertyChanged(nameof(ColorNamesByRarityRecentAchievements));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameColorNamesByRarity))
            {
                OnPropertyChanged(nameof(ColorNamesByRaritySelectedGame));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewRecentAchievementsColorRarityColumnsByRarity))
            {
                OnPropertyChanged(nameof(ColorRarityColumnsByRarityRecentAchievements));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameColorRarityColumnsByRarity))
            {
                OnPropertyChanged(nameof(ColorRarityColumnsByRaritySelectedGame));
            }
            else if (propertyName == nameof(PersistedSettings.IncludeUnplayedGames))
            {
                OnPropertyChanged(nameof(IncludeUnplayedGames));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewCollectionScoreCard)
                || propertyName == nameof(PersistedSettings.ShowOverviewPrestigeScoreCard))
            {
                RaiseOverviewScoreCardVisibilityChanged();
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewPieCharts)
                || propertyName == nameof(PersistedSettings.ShowOverviewGamesPieChart)
                || propertyName == nameof(PersistedSettings.ShowOverviewProviderPieChart)
                || propertyName == nameof(PersistedSettings.ShowOverviewRarityPieChart)
                || propertyName == nameof(PersistedSettings.ShowOverviewTrophyPieChart))
            {
                RaiseOverviewPieChartVisibilityChanged();
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewPiePercentages))
            {
                OnPropertyChanged(nameof(ShowOverviewPiePercentages));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewBarCharts))
            {
                OnPropertyChanged(nameof(ShowOverviewBarCharts));
            }
            else if (propertyName == nameof(PersistedSettings.EnableFriendsFeatures))
            {
                OnPropertyChanged(nameof(EnableFriendsFeatures));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewGameMetadataPlatform))
            {
                OnPropertyChanged(nameof(ShowOverviewGameMetadataPlatform));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewGameMetadataPlaytime))
            {
                OnPropertyChanged(nameof(ShowOverviewGameMetadataPlaytime));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewGameMetadataRegion))
            {
                OnPropertyChanged(nameof(ShowOverviewGameMetadataRegion));
            }
            else if (propertyName == nameof(PersistedSettings.ShowCompletionGlow))
            {
                OnPropertyChanged(nameof(ShowCompletionGlow));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewGameSummariesGridColumnHeaders))
            {
                OnPropertyChanged(nameof(ShowOverviewGameSummariesGridColumnHeaders));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewRecentAchievementsGridColumnHeaders))
            {
                OnPropertyChanged(nameof(ShowOverviewRecentAchievementsGridColumnHeaders));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewSelectedGameGridColumnHeaders))
            {
                OnPropertyChanged(nameof(ShowOverviewSelectedGameGridColumnHeaders));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameAchievementsHideCategorySummaryRow))
            {
                OnPropertyChanged(nameof(OverviewSelectedGameAchievementsHideCategorySummaryRow));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewSelectedGameCategorySummariesGridColumnHeaders))
            {
                OnPropertyChanged(nameof(ShowOverviewSelectedGameCategorySummariesGridColumnHeaders));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameCategorySummariesGridRowHeight))
            {
                OnPropertyChanged(nameof(OverviewSelectedGameCategorySummariesGridRowHeight));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameCategorySummariesUseCoverImages))
            {
                OnPropertyChanged(nameof(OverviewSelectedGameCategorySummariesUseCoverImages));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameCategorySummariesShowCompletionGlow))
            {
                OnPropertyChanged(nameof(OverviewSelectedGameCategorySummariesShowCompletionGlow));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewGameSummariesGridControlBar))
            {
                OnPropertyChanged(nameof(ShowOverviewGameSummariesGridControlBar));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewRecentAchievementsGridControlBar))
            {
                OnPropertyChanged(nameof(ShowOverviewRecentAchievementsGridControlBar));
            }
            else if (propertyName == nameof(PersistedSettings.ShowOverviewSelectedGameGridControlBar))
            {
                OnPropertyChanged(nameof(ShowOverviewSelectedGameGridControlBar));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewGameSummariesGridRowHeight))
            {
                OnPropertyChanged(nameof(OverviewGameSummariesGridRowHeight));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewRecentAchievementsGridRowHeight))
            {
                OnPropertyChanged(nameof(OverviewRecentAchievementsGridRowHeight));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameGridRowHeight))
            {
                OnPropertyChanged(nameof(OverviewSelectedGameGridRowHeight));
            }
            else if (propertyName == nameof(PersistedSettings.OverviewGameSummariesGridMaxRows))
            {
                SyncGameSummariesDisplay();
            }
            else if (propertyName == nameof(PersistedSettings.OverviewRecentAchievementsGridMaxRows))
            {
                SyncRecentAchievementsDisplay();
            }
            else if (propertyName == nameof(PersistedSettings.OverviewSelectedGameGridMaxRows))
            {
                SyncSelectedGameAchievementsDisplay();
            }
            else if (RarityAppearanceHelper.IsAppearanceSettingPropertyName(propertyName))
            {
                OnPropertyChanged(nameof(UseUniformRarityBadges));
                ApplyScoreCards();
                UpdateAggregatePieCharts();
            }
            else if (propertyName == nameof(PersistedSettings.OverviewPieSmallSliceMode))
            {
                ApplyOverviewPieSmallSliceMode();
                UpdateAggregatePieCharts();
            }
            else if (propertyName == nameof(PersistedSettings.OverviewTimelineRange))
            {
                ApplySavedTimelineRange();
            }
            else if (GameSummariesSortHelper.IsConfiguredDefaultSortPropertyName(propertyName))
            {
                if (string.IsNullOrWhiteSpace(_overviewSortPath))
                {
                    ApplyLeftFilters();
                    OnPropertyChanged(nameof(OverviewSortPath));
                    OnPropertyChanged(nameof(OverviewSortDirection));
                }
            }
            else if (AchievementSortHelper.IsConfiguredDefaultSortPropertyName(
                propertyName,
                AchievementSortSurface.OverviewSelectedGame))
            {
                if (IsGameSelected && !SelectedGameSortDirection.HasValue)
                {
                    ApplyRightFilters();
                }
            }
            else if (AchievementDisplayItem.IsAppearanceSettingPropertyName(propertyName))
            {
                _ = RefreshViewAsync();
            }
        }

        private void RaiseOverviewScoreCardVisibilityChanged()
        {
            OnPropertyChanged(nameof(ShowOverviewCollectionScoreCard));
            OnPropertyChanged(nameof(ShowOverviewPrestigeScoreCard));
            OnPropertyChanged(nameof(ShowOverviewScoreCards));
            OnPropertyChanged(nameof(ShowOverviewScoreCardDivider));
        }

        private void RaiseOverviewPieChartVisibilityChanged()
        {
            OnPropertyChanged(nameof(ShowOverviewPieCharts));
            OnPropertyChanged(nameof(ShowOverviewGamesPieChart));
            OnPropertyChanged(nameof(ShowOverviewProviderPieChart));
            OnPropertyChanged(nameof(ShowOverviewRarityPieChart));
            OnPropertyChanged(nameof(ShowOverviewTrophyPieChart));
        }

        private void RevealAchievement(AchievementDisplayItem item)
        {
            if (item == null)
            {
                return;
            }

            var key = AchievementDisplayItem.MakeRevealKey(item.PlayniteGameId, item.ApiName, item.GameName);

            item.ToggleReveal();
            _globalAchievementSearchIndex.Invalidate(item);
            _recentAchievementSearchIndex.Invalidate(item);

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

        private void OnCacheDeltaUpdated(object sender, CacheDeltaEventArgs e)
        {
            if (!_isActive || e == null)
            {
                return;
            }

            QueueOverviewDelta(e.IsFullReset, e.Key);
        }

        private void OnCustomDataChanged(object sender, GameCustomDataChangedEventArgs e)
        {
            if (!_isActive || e == null || e.PlayniteGameId == Guid.Empty)
            {
                return;
            }

            QueueOverviewDelta(isFullReset: false, key: e.PlayniteGameId.ToString("D"));
        }

        private void QueueOverviewDelta(bool isFullReset, string key)
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
            {
                lock (_deltaSync)
                {
                    if (isFullReset)
                    {
                        _pendingFullResetFromDelta = true;
                        _pendingDeltaKeys.Clear();
                    }
                    else if (!string.IsNullOrWhiteSpace(key))
                    {
                        _pendingDeltaKeys.Add(key.Trim());
                    }
                }

                _deltaBatchTimer.Stop();
                _deltaBatchTimer.Start();
            });
        }

        private void OnCacheInvalidated(object sender, CacheInvalidatedEventArgs e)
        {
            if (!_isActive || _disposed || _refreshService.IsRebuilding)
            {
                return;
            }

            // Scoped invalidations (a poller tick names exactly one game) route through the
            // existing per-game fragment path instead of the full projection + search index
            // rebuild; only unscoped invalidations pay for the full refresh.
            if (e != null && !e.IsFull)
            {
                foreach (var gameId in e.ChangedGameIds)
                {
                    if (gameId != Guid.Empty)
                    {
                        QueueOverviewDelta(isFullReset: false, key: gameId.ToString("D"));
                    }
                }

                return;
            }

            System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
            {
                if (!_isActive || _disposed)
                {
                    return;
                }

                _refreshDebounceTimer.Stop();
                _refreshDebounceTimer.Start();
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

            var revealedCopy = GetRevealedKeysSnapshotIfNeeded();

            var fragments = await Task.Run(() =>
            {
                var dict = new Dictionary<string, OverviewGameFragment>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var gameData = Guid.TryParse(key, out var parsedGameId)
                        ? _achievementDataService.GetGameAchievementDataForOverview(parsedGameId)
                        : _achievementDataService.GetVisibleGameAchievementData(key);
                    dict[key] = gameData == null
                        ? null
                        : _dataBuilder.BuildGameFragment(
                            _settings,
                            revealedCopy,
                            gameData,
                            includeAchievementItems: false);
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
                GameSummariesSortHelper.SortByConfiguredDefault(_allGameSummaries, _settings?.Persisted);
            }

            if (string.IsNullOrEmpty(_recentSortPath))
            {
                _allRecentAchievements = AchievementSortHelper.CreateDefaultSortedList(
                    _allRecentAchievements,
                    AchievementSortScope.RecentAchievements);
            }

            RefreshOverviewSearchIndexes();

            var snapshot = BuildSnapshotFromSourceLists();
            ApplyOverviewSummaryFromSnapshot(snapshot);
            RefreshFilter();
            ApplyLeftFilters();
            UpdateAggregatePieCharts();
            ApplyRightFilters();
            UpdateFilteredStatus();
        }

        private void RefreshOverviewSearchIndexes()
        {
            _globalAchievementSearchIndex.Rebuild(_allAchievements);
            _gameSummarySearchIndex.Rebuild(_allGameSummaries);
            _recentAchievementSearchIndex.Rebuild(_allRecentAchievements);
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
                _logger?.Error(ex, "Failed to auto-refresh overview on cache change");
            }
        }

        private bool FilterAchievement(AchievementDisplayItem item, SearchQuery searchQuery)
        {
            if (item == null) return false;

            // Search filter
            if (searchQuery.HasValue && !_globalAchievementSearchIndex.Matches(item, searchQuery))
            {
                return false;
            }

            // Unlocked/Locked filters
            if (ShowUnlockedOnly && !item.Unlocked) return false;
            if (ShowLockedOnly && item.Unlocked) return false;

            return true;
        }

        public void RefreshFilter()
        {
            var source = _allAchievements ?? new List<AchievementDisplayItem>();
            var searchQuery = SearchQuery.From(SearchText);
            var filtered = ApplySort(source.Where(item => FilterAchievement(item, searchQuery))).ToList();
            CollectionHelper.Replace(AllAchievements, filtered);
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
            else if (HasMaterializedGlobalAchievementItems() && AllAchievements.Count < _totalCount)
            {
                StatusText = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Status_FilteredCounts"),
                    AllAchievements.Count.ToString("N0", FormattingCulture.Current),
                    _totalCount.ToString("N0", FormattingCulture.Current),
                    _unlockedCount.ToString("N0", FormattingCulture.Current),
                    _gamesCount.ToString("N0", FormattingCulture.Current));
            }
            else
            {
                StatusText = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Status_TotalCounts"),
                    _totalCount.ToString("N0", FormattingCulture.Current),
                    _unlockedCount.ToString("N0", FormattingCulture.Current),
                    _gamesCount.ToString("N0", FormattingCulture.Current));
            }
        }

        private bool HasMaterializedGlobalAchievementItems()
        {
            return (_allAchievements?.Count ?? 0) > 0 || (AllAchievements?.Count ?? 0) > 0;
        }

        private void RecalculateOverviewStats()
        {
            // This now calculates from the filtered view
            var sourceList = _filteredGameSummaries;

            TotalGameSummaries = sourceList.Count;
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
            (RefreshOrCancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshSingleGameCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (OpenGameInLibraryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenGameInOverviewCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(RefreshOrCancelButtonText));
            OnPropertyChanged(nameof(RefreshOrCancelButtonGlyph));
        }

        #endregion

        #region Overview Methods

        // LoadOverviewData removed: overview and recent lists are built via OverviewDataBuilder snapshots.

        private void SyncGameSummariesDisplay()
        {
            var displayItems = DisplayGridRowLimitHelper.Limit(
                _filteredGameSummaries,
                _settings?.Persisted?.OverviewGameSummariesGridMaxRows);

            CollectionHelper.Replace(GameSummaries, displayItems);
        }

        private void SyncRecentAchievementsDisplay()
        {
            var displayItems = DisplayGridRowLimitHelper.Limit(
                _filteredRecentAchievements,
                _settings?.Persisted?.OverviewRecentAchievementsGridMaxRows);

            CollectionHelper.Replace(RecentAchievements, displayItems);
        }

        private void SyncSelectedGameAchievementsDisplay()
        {
            // Keep the unfiltered category-summary source current; the achievement filters do not
            // touch it, so category rollups stay stable while drilled. Sync from the canonical
            // definition/custom-order snapshot so category ordering does not follow the live grid sort.
            CollectionHelper.Replace(
                SelectedGameAllAchievements,
                _selectedGameDefaultOrderedAchievements ?? new List<AchievementDisplayItem>());

            var displayItems = DisplayGridRowLimitHelper.Limit(
                _filteredSelectedGameAchievements,
                _settings?.Persisted?.OverviewSelectedGameGridMaxRows);

            CollectionHelper.Replace(SelectedGameAchievements, displayItems);
        }

        private void ApplyLeftFilters()
        {
            // Preserve selection across filter updates
            Guid? selectedGameId = SelectedGame?.PlayniteGameId;

            var filtered = _allGameSummaries.AsEnumerable();
            var searchQuery = SearchQuery.From(LeftSearchText);

            // Search filter
            if (searchQuery.HasValue)
            {
                filtered = filtered.Where(g => _gameSummarySearchIndex.Matches(g, searchQuery));
            }

            // Provider + platform filter
            filtered = OverviewGameSummaryFilters.ApplyProviderPlatformFilter(filtered, ProviderFilterGroups);

            filtered = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                filtered,
                _selectedPlayStatusFilters,
                _selectedCompletenessFilters,
                L("LOCPlayAch_Filter_Played"),
                L("LOCPlayAch_Filter_Unplayed"),
                L("LOCPlayAch_Filter_Complete"),
                L("LOCPlayAch_Filter_InProgress"),
                L("LOCPlayAch_Filter_NoProgress"));

            _filteredGameSummaries = filtered.ToList();
            if (!string.IsNullOrEmpty(_overviewSortPath))
            {
                SortGameSummaries(_overviewSortPath, _overviewSortDirection);
            }
            else
            {
                GameSummariesSortHelper.SortByConfiguredDefault(_filteredGameSummaries, _settings?.Persisted);
                SyncGameSummariesDisplay();
            }
            RecalculateOverviewStats();

            // Restore selection by finding the game with matching PlayniteGameId
            if (selectedGameId.HasValue)
            {
                var restored = GameSummaries.FirstOrDefault(g => g.PlayniteGameId == selectedGameId.Value);
                if (restored != null)
                {
                    SelectedGame = restored;
                }
            }
        }

        private void UpdateOverviewPieChartSelectionStates()
        {
            ProviderPieChart?.SetSelectedLabels(
                (ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
                    .Where(group => group.HasAnySelected)
                    .Select(group => group.DisplayName)
                    .Where(label => !string.IsNullOrWhiteSpace(label)));
            GamesPieChart?.SetSelectedLabels(GetGamesPieChartSelectedLabels());
        }

        private IEnumerable<string> GetGamesPieChartSelectedLabels()
        {
            var visibleLabels = new HashSet<string>(
                (GamesPieChart?.LegendItems ?? Enumerable.Empty<LegendItem>())
                    .Select(item => item?.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label)),
                StringComparer.OrdinalIgnoreCase);
            if (visibleLabels.Count <= 1 || _selectedCompletenessFilters.Count == 0)
            {
                return Enumerable.Empty<string>();
            }

            var labels = new List<string>();
            var completeLabel = L("LOCPlayAch_Filter_Complete");
            if (_selectedCompletenessFilters.Contains(completeLabel))
            {
                labels.Add(completeLabel);
            }

            if (_selectedCompletenessFilters.Contains(L("LOCPlayAch_Filter_InProgress")) ||
                _selectedCompletenessFilters.Contains(L("LOCPlayAch_Filter_NoProgress")))
            {
                labels.Add(L("LOCPlayAch_Overview_Incomplete"));
            }

            var selectedVisibleLabels = labels
                .Where(label => visibleLabels.Contains(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return selectedVisibleLabels.Count == 1
                ? selectedVisibleLabels
                : Enumerable.Empty<string>();
        }

        private void ApplyOverviewPieSmallSliceMode()
        {
            var mode = _settings?.Persisted?.OverviewPieSmallSliceMode ?? OverviewPieSmallSliceMode.Round;
            GamesPieChart.SmallSliceMode = mode;
            ProviderPieChart.SmallSliceMode = mode;
            RarityPieChart.SmallSliceMode = mode;
            TrophyPieChart.SmallSliceMode = mode;
        }

        private void UpdateAggregatePieCharts()
        {
            var snapshot = BuildPieChartSnapshotFromCurrentState();
            var gamesPieSnapshot = BuildPieChartSnapshotFromCurrentState(useCompletedGamesPieProgressScope: true);

            var completedLabel = ResourceProvider.GetString("LOCPlayAch_Filter_Complete");
            var incompleteLabel = ResourceProvider.GetString("LOCPlayAch_Overview_Incomplete");
            var lockedLabel = ResourceProvider.GetString("LOCPlayAch_Common_Locked");
            var commonLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Common");
            var uncommonLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Uncommon");
            var rareLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_Rare");
            var ultraRareLabel = ResourceProvider.GetString("LOCPlayAch_Rarity_UltraRare");
            var trophyPlatinumLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Platinum");
            var trophyGoldLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Gold");
            var trophySilverLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Silver");
            var trophyBronzeLabel = ResourceProvider.GetString("LOCPlayAch_Trophy_Bronze");

            GamesPieChart?.SetGameData(gamesPieSnapshot.TotalGames, gamesPieSnapshot.CompletedGames, completedLabel, incompleteLabel);

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
            if (SelectedGame?.PlayniteGameId.HasValue == true && _selectedGameLoadInProgress)
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

        private OverviewDataSnapshot BuildPieChartSnapshotFromCurrentState(bool useCompletedGamesPieProgressScope = false)
        {
            var gamesList = (useCompletedGamesPieProgressScope
                ? GetCompletedGamesPieChartGames()
                : GetPieChartGames()).ToList();
            var snapshot = new OverviewDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem>(),
                GameSummaries = gamesList,
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

        private IEnumerable<GameSummaryItem> GetPieChartGames()
        {
            var filteredGames = (_allGameSummaries ?? new List<GameSummaryItem>()).Where(game => game != null);
            return OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                filteredGames,
                _selectedPlayStatusFilters,
                _selectedCompletenessFilters,
                L("LOCPlayAch_Filter_Played"),
                L("LOCPlayAch_Filter_Unplayed"),
                L("LOCPlayAch_Filter_Complete"),
                L("LOCPlayAch_Filter_InProgress"),
                L("LOCPlayAch_Filter_NoProgress"));
        }

        private IEnumerable<GameSummaryItem> GetCompletedGamesPieChartGames()
        {
            var filteredGames = (_allGameSummaries ?? new List<GameSummaryItem>()).Where(game => game != null);
            return OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                filteredGames,
                _selectedPlayStatusFilters,
                GetCompletedGamesPieProgressFilters(),
                L("LOCPlayAch_Filter_Played"),
                L("LOCPlayAch_Filter_Unplayed"),
                L("LOCPlayAch_Filter_Complete"),
                L("LOCPlayAch_Filter_InProgress"),
                L("LOCPlayAch_Filter_NoProgress"));
        }

        private ISet<string> GetCompletedGamesPieProgressFilters()
        {
            if (_selectedCompletenessFilters == null || _selectedCompletenessFilters.Count == 0)
            {
                return null;
            }

            var noProgressLabel = L("LOCPlayAch_Filter_NoProgress");
            if (_selectedCompletenessFilters.Contains(noProgressLabel))
            {
                return null;
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                L("LOCPlayAch_Filter_Complete"),
                L("LOCPlayAch_Filter_InProgress")
            };
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
            OverviewDataSnapshot snapshot,
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
            commonLabel ??= L("LOCPlayAch_Rarity_Common");
            uncommonLabel ??= L("LOCPlayAch_Rarity_Uncommon");
            rareLabel ??= L("LOCPlayAch_Rarity_Rare");
            ultraRareLabel ??= L("LOCPlayAch_Rarity_UltraRare");
            trophyPlatinumLabel ??= L("LOCPlayAch_Trophy_Platinum");
            trophyGoldLabel ??= L("LOCPlayAch_Trophy_Gold");
            trophySilverLabel ??= L("LOCPlayAch_Trophy_Silver");
            trophyBronzeLabel ??= L("LOCPlayAch_Trophy_Bronze");
            lockedLabel ??= L("LOCPlayAch_Common_Locked");

            var selectedGame = ResolveSelectedGameForChartContext(snapshot);
            var useSelectedRarity = selectedGame?.HasRarityPieChartData == true;
            var useSelectedTrophy = selectedGame?.HasTrophyPieChartData == true;
            var useUniformRarityBadges = _settings?.Persisted?.UseUniformRarityBadges ?? false;

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
                    lockedLabel,
                    useUniformRarityBadges);
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
                    lockedLabel,
                    useUniformRarityBadges);
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
                var trophySummary = BuildTrophySummaryFromGames(snapshot?.GameSummaries);
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

            var rarityTitle = L("LOCPlayAch_Overview_RarityPieChart");
            var trophyTitle = L("LOCPlayAch_Overview_TrophyPieChart");
            RarityPieChartTitle = BuildContextualPieChartTitle(rarityTitle, useSelectedRarity ? selectedGame?.GameName : null);
            TrophyPieChartTitle = BuildContextualPieChartTitle(trophyTitle, useSelectedTrophy ? selectedGame?.GameName : null);
        }

        private GameSummaryItem ResolveSelectedGameForChartContext(OverviewDataSnapshot snapshot)
        {
            if (SelectedGame?.PlayniteGameId.HasValue != true)
            {
                return SelectedGame;
            }

            var selectedGameId = SelectedGame.PlayniteGameId.Value;
            return snapshot?.GameSummaries?.FirstOrDefault(game => game?.PlayniteGameId == selectedGameId)
                ?? SelectedGame;
        }

        private static int GetSelectedGameLockedAchievementCount(GameSummaryItem game)
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
            int BronzeTotal) BuildTrophySummaryFromGames(IEnumerable<GameSummaryItem> games)
        {
            int platinumUnlocked = 0;
            int goldUnlocked = 0;
            int silverUnlocked = 0;
            int bronzeUnlocked = 0;
            int platinumTotal = 0;
            int goldTotal = 0;
            int silverTotal = 0;
            int bronzeTotal = 0;

            if (games == null)
            {
                return (0, 0, 0, 0, 0, 0, 0, 0);
            }

            foreach (var game in games)
            {
                if (game == null)
                {
                    continue;
                }

                platinumUnlocked += game.TrophyPlatinumCount;
                goldUnlocked += game.TrophyGoldCount;
                silverUnlocked += game.TrophySilverCount;
                bronzeUnlocked += game.TrophyBronzeCount;
                platinumTotal += game.TrophyPlatinumTotal;
                goldTotal += game.TrophyGoldTotal;
                silverTotal += game.TrophySilverTotal;
                bronzeTotal += game.TrophyBronzeTotal;
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
            _selectedGameControlBar.UpdateOptions(source);
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
            return OverviewGameSummaryFilters.BuildProviderFilterText(
                ProviderFilterGroups,
                L("LOCPlayAch_Common_Label_Platform"));
        }

        private void ApplyRightFilters(bool skipDefaultSort = false)
        {
            var searchQuery = SearchQuery.From(RightSearchText);

            // Contextually filter based on IsGameSelected
            if (IsGameSelected)
            {
                // The shared control bar owns the filter predicate (search + Unlocked/Locked/
                // Hidden + Type/Category); this VM keeps sorting, row limiting, and header counts.
                _filteredSelectedGameAchievements = _selectedGameControlBar
                    .Apply(_allSelectedGameAchievements)
                    .ToList();

                if (!string.IsNullOrEmpty(_selectedGameSortPath))
                {
                    SortSelectedGameAchievements(_selectedGameSortPath, _selectedGameSortDirection);
                }
                else if (!skipDefaultSort)
                {
                    AchievementSortHelper.ApplyConfiguredDefaultSort(
                        _filteredSelectedGameAchievements,
                        _settings?.Persisted,
                        AchievementSortSurface.OverviewSelectedGame,
                        AchievementSortScope.GameAchievements,
                        stableOrder: AchievementSortHelper.CreateStableOrderMap(_filteredSelectedGameAchievements));

                    SyncSelectedGameAchievementsDisplay();
                }
                else
                {
                    SyncSelectedGameAchievementsDisplay();
                }
            }
            else
            {
                _filteredRecentAchievements = OverviewAchievementFilters.FilterRecentAchievements(
                    _allRecentAchievements,
                    string.Empty);

                if (searchQuery.HasValue)
                {
                    _filteredRecentAchievements = _filteredRecentAchievements
                        .Where(item => _recentAchievementSearchIndex.Matches(item, searchQuery))
                        .ToList();
                }

                if (!string.IsNullOrEmpty(_recentSortPath))
                {
                    SortRecentAchievements(_recentSortPath, _recentSortDirection);
                }
                else
                {
                    SyncRecentAchievementsDisplay();
                }
            }

            RefreshSelectedGameHeaderCounts();
        }

        private bool HasSelectedGameAchievementFiltersApplied()
        {
            return IsGameSelected && _selectedGameControlBar.HasActiveFilters;
        }

        private void RefreshSelectedGameHeaderCounts()
        {
            var drilledCategory = SelectedGameDrilledCategory;
            var isDrilled = !string.IsNullOrEmpty(drilledCategory);
            // A drilled category is presented like a filtered view ("x/y Selected").
            var isFiltered = isDrilled || HasSelectedGameAchievementFiltersApplied();
            var unlocked = 0;
            var total = 0;

            if (IsSelectedGameContentReady)
            {
                if (isDrilled)
                {
                    // Scope to the drilled category, respecting any active filter applied within it.
                    var scoped = (_filteredSelectedGameAchievements ?? new List<AchievementDisplayItem>())
                        .Where(item => string.Equals(
                            AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item?.CategoryLabel),
                            drilledCategory,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    total = scoped.Count;
                    unlocked = scoped.Count(item => item?.Unlocked == true);
                }
                else if (isFiltered)
                {
                    var filtered = _filteredSelectedGameAchievements ?? new List<AchievementDisplayItem>();
                    total = filtered.Count;
                    unlocked = filtered.Count(item => item?.Unlocked == true);
                }
                else
                {
                    total = DisplayedSelectedGame?.TotalAchievements ?? 0;
                    unlocked = DisplayedSelectedGame?.UnlockedAchievements ?? 0;
                }
            }

            SelectedGameHeaderText = $"({unlocked.ToString("N0", FormattingCulture.Current)}/{total.ToString("N0", FormattingCulture.Current)} {(isFiltered ? L("LOCPlayAch_RefreshModeShort_Selected") : L("LOCPlayAch_Achievements"))})";
        }

        private void ResetSelectedGameSortToDefault()
        {
            _allSelectedGameAchievements = _selectedGameDefaultOrderedAchievements != null
                ? new List<AchievementDisplayItem>(_selectedGameDefaultOrderedAchievements)
                : new List<AchievementDisplayItem>();
            _selectedGameSortPath = null;
            _selectedGameSortDirection = AchievementSortHelper.GetConfiguredDefaultSort(
                _settings?.Persisted,
                AchievementSortSurface.OverviewSelectedGame).Direction;
        }

        private void ResetOverviewSortToDefault()
        {
            GameSummariesSortHelper.SortByConfiguredDefault(_allGameSummaries, _settings?.Persisted);
            _overviewSortPath = null;
            _overviewSortDirection = GameSummariesSortHelper.GetConfiguredDefaultSort(_settings?.Persisted).Direction;
        }

        private void ResetRecentSortToDefault()
        {
            _allRecentAchievements = AchievementSortHelper.CreateDefaultSortedList(
                _allRecentAchievements,
                AchievementSortScope.RecentAchievements);
            _recentSortPath = null;
            _recentSortDirection = AchievementSortHelper.GetConfiguredDefaultSort(
                _settings?.Persisted,
                AchievementSortSurface.OverviewRecentAchievements).Direction;
        }

        public void ApplyDefaultSelectedGameSort()
        {
            ResetSelectedGameSortToDefault();

            if (IsGameSelected)
            {
                ApplyRightFilters(skipDefaultSort: true);
            }
        }

        public void ApplyDefaultOverviewSort()
        {
            ResetOverviewSortToDefault();
            ApplyLeftFilters();
        }

        public void ApplyDefaultRecentSort()
        {
            ResetRecentSortToDefault();

            if (!IsGameSelected)
            {
                ApplyRightFilters();
            }
        }

        /// <summary>
        /// Loads game achievements and fires visibility notifications after data is ready.
        /// This prevents flash by ensuring data is loaded before the grid becomes visible.
        /// </summary>
        private async Task LoadSelectedGameAchievementsAndNotifyAsync(Guid? targetGameId, CancellationToken cancellationToken)
        {
            var loadApplied = await LoadSelectedGameAchievementsAsync(targetGameId, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _selectedGameLoadInProgress = false;
            var isCurrentLoad = IsSelectedGameLoadCurrent(targetGameId, cancellationToken);
            _selectedGameContentReady = loadApplied && targetGameId.HasValue && isCurrentLoad;

            if (!loadApplied && !isCurrentLoad)
            {
                return;
            }

            if (_selectedGameContentReady)
            {
                SetDisplayedSelectedGame(SelectedGame);
            }
            else if (!targetGameId.HasValue && isCurrentLoad)
            {
                SetDisplayedSelectedGame(null);
            }

            RefreshSelectedGameHeaderCounts();

            // Fire visibility notifications after the current selection load has settled so the
            // selected-game grid is not realized with empty rows during game-to-game switches.
            NotifySelectedGameViewStateChanged();
            OnPropertyChanged(nameof(TimelineSectionTitle));

            if (!loadApplied)
            {
                return;
            }

            UpdateContextualPieCharts(BuildPieChartSnapshotFromCurrentState());
        }

        private async Task<bool> LoadSelectedGameAchievementsAsync(Guid? targetGameId, CancellationToken cancellationToken)
        {
            // Reset right search when selecting a game
            RightSearchText = string.Empty;

            if (targetGameId == null)
            {
                if (!IsSelectedGameLoadCurrent(targetGameId, cancellationToken))
                {
                    return false;
                }

                _allSelectedGameAchievements = new List<AchievementDisplayItem>();
                _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>();
                _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();
                UpdateSelectedGameAchievementFilterOptions(null);
                SelectedGameHasCustomAchievementOrder = false;
                SyncSelectedGameAchievementsDisplay();
                // Restore global timeline to show all games
                GlobalTimeline.SetCounts(_latestSnapshot?.GlobalUnlockCountsByDate);
                RefreshSelectedGameHeaderCounts();
                return true;
            }

            try
            {
                if (!IsSelectedGameLoadCurrent(targetGameId, cancellationToken))
                {
                    return false;
                }

                var gameId = targetGameId.Value;

                var revealedCopy = GetRevealedKeysSnapshotIfNeeded();

                var loadResult = await _selectedGamePipeline
                    .LoadAsync(gameId, revealedCopy, cancellationToken)
                    .ConfigureAwait(true);

                if (!IsSelectedGameLoadCurrent(targetGameId, cancellationToken))
                {
                    return false;
                }

                var items = loadResult.Items ?? new List<AchievementDisplayItem>();
                var hasCustomOrder = loadResult.HasCustomOrder;
                SelectedGameHasCustomAchievementOrder = hasCustomOrder;

                _allSelectedGameAchievements = items;
                _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>(items);
                UpdateSelectedGameAchievementFilterOptions(_allSelectedGameAchievements);
                ApplyRightFilters();

                var selectedTimelineCounts = GetSelectedGameTimelineCounts(gameId);
                GlobalTimeline.SetCounts(selectedTimelineCounts);
                SelectedGameTimeline.SetCounts(selectedTimelineCounts);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                if (!IsSelectedGameLoadCurrent(targetGameId, cancellationToken))
                {
                    return false;
                }

                UpdateSelectedGameAchievementFilterOptions(null);
                SelectedGameHasCustomAchievementOrder = false;
                _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>();
                _logger?.Warn(ex, $"Failed to load achievements for game {SelectedGame?.AppId}");
                RefreshSelectedGameHeaderCounts();
                return false;
            }
        }

        private bool IsSelectedGameLoadCurrent(Guid? targetGameId, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return SelectedGame?.PlayniteGameId == targetGameId;
        }

        private ISet<string> GetRevealedKeysSnapshotIfNeeded()
        {
            var showIcon = _settings?.Persisted?.ShowHiddenIcon ?? false;
            var showTitle = _settings?.Persisted?.ShowHiddenTitle ?? false;
            var showDescription = _settings?.Persisted?.ShowHiddenDescription ?? false;
            var anyHidingEnabled = !showIcon || !showTitle || !showDescription;
            if (!anyHidingEnabled)
            {
                return null;
            }

            lock (_revealedKeys)
            {
                if (_revealedKeys.Count == 0)
                {
                    return null;
                }

                return new HashSet<string>(_revealedKeys, StringComparer.OrdinalIgnoreCase);
            }
        }

        private IDictionary<DateTime, int> GetSelectedGameTimelineCounts(Guid gameId)
        {
            if (_latestSnapshot?.UnlockCountsByDateByGame != null &&
                _latestSnapshot.UnlockCountsByDateByGame.TryGetValue(gameId, out var counts))
            {
                return counts;
            }

            return new Dictionary<DateTime, int>();
        }

        private void CancelSelectedGameLoad()
        {
            var cts = Interlocked.Exchange(ref _selectedGameLoadCts, null);
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            try
            {
                cts.Dispose();
            }
            catch
            {
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

            if (itemsSource == GameSummaries)
            {
                SortGameSummaries(sortMemberPath, direction);
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

        private void SortGameSummaries(string sortMemberPath, ListSortDirection direction)
        {
            if (!GameSummariesSortHelper.TrySortItems(
                    _filteredGameSummaries,
                    sortMemberPath,
                    direction,
                    ref _overviewSortPath,
                    ref _overviewSortDirection))
            {
                return;
            }

            SyncGameSummariesDisplay();
        }

        private void SortRecentAchievements(string sortMemberPath, ListSortDirection direction)
        {
            var recentSortDirection = (ListSortDirection?)_recentSortDirection;
            if (!AchievementSortHelper.TrySortItems(
                    _filteredRecentAchievements,
                    sortMemberPath,
                    direction,
                    AchievementSortScope.RecentAchievements,
                    ref _recentSortPath,
                    ref recentSortDirection))
            {
                return;
            }

            if (recentSortDirection.HasValue)
            {
                _recentSortDirection = recentSortDirection.Value;
            }

            SyncRecentAchievementsDisplay();
        }

        private void SortSelectedGameAchievements(string sortMemberPath, ListSortDirection direction)
        {
            var existingAllOrder = _allSelectedGameAchievements
                .Select((item, index) => new { item, index })
                .ToDictionary(x => x.item, x => x.index);

            var selectedSortDirection = (ListSortDirection?)_selectedGameSortDirection;
            if (!AchievementSortHelper.TrySortItems(
                    _allSelectedGameAchievements,
                    sortMemberPath,
                    direction,
                    AchievementSortScope.GameAchievements,
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
            AchievementSortHelper.TrySortItems(
                _filteredSelectedGameAchievements,
                sortMemberPath,
                direction,
                AchievementSortScope.GameAchievements,
                ref _selectedGameSortPath,
                ref selectedSortDirection,
                sortedAllOrder);

            SyncSelectedGameAchievementsDisplay();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SetActive(false);
            CancelSelectedGameLoad();
            _refreshDebounceTimer?.Stop();
            _deltaBatchTimer?.Stop();
            CancelPendingRefresh();
            if (_progressTracker != null)
            {
                _progressTracker.PropertyChanged -= OnProgressTrackerChanged;
                _progressTracker.Dispose();
            }
            if (_refreshService != null)
            {
                _refreshService.CacheDeltaUpdated -= OnCacheDeltaUpdated;
                _refreshService.CacheInvalidated -= OnCacheInvalidated;
            }
            if (_gameCustomDataStore != null)
            {
                _gameCustomDataStore.CustomDataChanged -= OnCustomDataChanged;
            }
            if (GlobalTimeline != null)
            {
                GlobalTimeline.PropertyChanged -= Timeline_PropertyChanged;
            }
            if (SelectedGameTimeline != null)
            {
                SelectedGameTimeline.PropertyChanged -= Timeline_PropertyChanged;
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
            if (_deltaBatchTimer != null)
            {
                _deltaBatchTimer.Tick -= OnDeltaBatchTimerTick;
            }
        }
    }
}






