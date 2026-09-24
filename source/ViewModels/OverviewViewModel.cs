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
using PlayniteAchievements.Services.Friends;
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
    public class OverviewViewModel : ObservableObject, IDisposable, IOverviewRefreshHeaderViewModel, Common.IRetentionProbe
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
        private readonly Services.Captures.CaptureLibraryService _captureLibrary;

        private readonly OverviewDataBuilder _dataBuilder;

        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _refreshCts;
        private volatile bool _isActive;
        private int _refreshVersion;
        private bool _disposed;

        private readonly HashSet<string> _revealedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private OverviewDataSnapshot _latestSnapshot;

        // Shared-snapshot publishing: while this view model is active it feeds every applied
        // snapshot to the process-wide widget coordinator so start-page widgets share the
        // instance retained here instead of building their own full-library copy.
        private static readonly TimeSpan SharedPublishMinInterval = TimeSpan.FromSeconds(2);

        // Single-game changes (an in-game unlock, a row edit) should surface promptly; a bulk
        // refresh saving game after game must not drive a whole-library recompute per save.
        private static readonly TimeSpan InteractiveDeltaBatchInterval = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan BulkDeltaBatchInterval = TimeSpan.FromMilliseconds(2500);
        private readonly Func<Services.Widgets.WidgetDataCoordinator> _widgetCoordinatorAccessor;
        private Services.Widgets.WidgetDataCoordinator _sharedSnapshotTarget;
        private DateTime _lastSharedPublishUtc;

        public OverviewDataSnapshot LatestSnapshot => _latestSnapshot;

        public event EventHandler SnapshotChanged;
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

        // Compare-friend selection for the selected-game grid: enriches the self rows with a
        // friend's unlock state. Selection clears whenever the selected game changes.
        public FriendCompareController FriendCompare { get; }


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
            OverviewLaunchContext launchContext = OverviewLaunchContext.Sidebar,
            IFriendCacheManager friendCache = null,
            Func<Services.Widgets.WidgetDataCoordinator> widgetCoordinatorAccessor = null)
        {
            _widgetCoordinatorAccessor = widgetCoordinatorAccessor;
            FriendCompare = new FriendCompareController(friendCache, settings, logger);
            _selectedGameControlBar.AttachFriendCompare(FriendCompare);
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
                _logger,
                () => friendCache?.LoadCurrentUserIdentities());
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
                Interval = InteractiveDeltaBatchInterval
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
            ApplyOverviewPieIncludeLocked();

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
            _captureLibrary = PlayniteAchievementsPlugin.Instance?.CaptureLibraryService;
            if (_captureLibrary != null)
            {
                _captureLibrary.CapturesChanged += OnCapturesChanged;
            }
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

            // The retention report cannot reach in here, and the managed growth in a reported
            // session correlated with this surface being open, so the surface reports itself.
            // The registration is weak, so it does not root this instance.
            Common.RetentionProbes.Register(this);
        }

        public string RetentionProbeName => "overview";

        /// <summary>
        /// What this surface is holding. Everything here was invisible to the retention report:
        /// a session grew the managed heap from 122 MB to 912 MB with every tracked count flat,
        /// and these are the collections in the path that correlated with it.
        /// </summary>
        public string DescribeRetention()
        {
            _selectedGamePipeline.GetRetentionStats(out var pipelineGames, out var pipelineRows);

            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "rows={0}ach/{1}games/{2}recent selPipeline={3}games/{4}rows " +
                "searchIdx={5}/{6}/{7} selRows={8}/{9} display={10}ach/{11}games",
                _allAchievements?.Count ?? 0,
                _allGameSummaries?.Count ?? 0,
                _allRecentAchievements?.Count ?? 0,
                pipelineGames,
                pipelineRows,
                _globalAchievementSearchIndex.Count,
                _gameSummarySearchIndex.Count,
                _recentAchievementSearchIndex.Count,
                _allSelectedGameAchievements?.Count ?? 0,
                _filteredSelectedGameAchievements?.Count ?? 0,
                AllAchievements?.Count ?? 0,
                GameSummaries?.Count ?? 0);
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
        private readonly HashSet<string> _selectedGameIconOverrideKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _selectedGameReloadRequested;

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

                // A rebuild mints a fresh GameSummaryItem per game, and the selection restore
                // after a filter or sort pass re-assigns the same game as a different instance.
                // GameSummaryItem carries no value equality, so SetValueAndReturn read that as a
                // selection change and ran the whole load again: a reported session logged 276
                // loads of one unchanged game, and each one also cleared the user's per-game
                // filters through ResetFilters below. Adopt the new instance so the bindings and
                // the header see its updated counts, but skip the selection work.
                //
                // This cannot swallow a real data change. A per-game delta updates the selected
                // game's own rows through ApplySelectedGameIconOverrides and, when that cannot
                // re-stamp them in place, sets _selectedGameReloadRequested and reloads through
                // ReloadSelectedGameIfRequestedAsync -- neither of which comes through here.
                if (!ReferenceEquals(_selectedGame, value)
                    && previousGameId.HasValue
                    && newGameId.HasValue
                    && previousGameId == newGameId)
                {
                    _selectedGame = value;
                    OnPropertyChanged(nameof(SelectedGame));
                    if (_displayedSelectedGame != null)
                    {
                        SetDisplayedSelectedGame(value);
                    }

                    RefreshSelectedGameHeaderCounts();
                    return;
                }

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

        // Storage form of the same drill, for matching against achievement labels.
        // SelectedGameDrilledCategory is the display form and will not compare equal to one.
        private string _selectedGameDrilledCategoryPath;
        public string SelectedGameDrilledCategoryPath
        {
            get => _selectedGameDrilledCategoryPath;
            set
            {
                if (SetValueAndReturn(ref _selectedGameDrilledCategoryPath, value))
                {
                    RefreshSelectedGameHeaderCounts();
                }
            }
        }

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
                DetachSharedSnapshotPublisher();
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
                AttachSharedSnapshotPublisher();
                _progressTracker.SyncToCurrentState();
                // Refresh data when overview becomes active to ensure cached changes are visible
                _ = RefreshViewAsync();
            }
        }

        private void AttachSharedSnapshotPublisher()
        {
            if (_sharedSnapshotTarget != null)
            {
                return;
            }

            _sharedSnapshotTarget = _widgetCoordinatorAccessor?.Invoke();
            _sharedSnapshotTarget?.AttachPublisher();
        }

        private void DetachSharedSnapshotPublisher()
        {
            var target = _sharedSnapshotTarget;
            _sharedSnapshotTarget = null;
            target?.DetachPublisher();
        }

        // Mid-run the delta pipeline applies a snapshot roughly per refreshed game; publishing
        // each would fan a full start-page widget re-projection out per game, so publishes are
        // throttled while a refresh run is active. The trailing state still lands: the end-of-run
        // scoped CacheInvalidated re-queues deltas whose batch tick runs after IsRebuilding is
        // false, and full-view refreshes publish unconditionally.
        private void PublishSharedSnapshot(OverviewDataSnapshot snapshot)
        {
            var target = _sharedSnapshotTarget;
            if (target == null || snapshot == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (_refreshService.IsRebuilding && now - _lastSharedPublishUtc < SharedPublishMinInterval)
            {
                return;
            }

            _lastSharedPublishUtc = now;
            target.Publish(snapshot);
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
                // No explicit view refresh here: the pipeline's end-of-run CacheInvalidated
                // already reconciles this view model - scoped runs re-queue a delta per
                // refreshed game, larger/full runs collapse to a full invalidation that
                // lands in OnRefreshDebounceTimerTick's RefreshViewAsync. A second full
                // projection rebuild on top of that doubled the post-run allocation.
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
                SingleGameId = singleGameId,
                // Only the single-game mode refreshes a selected game; bulk modes fail silently.
                SurfaceUserNotices = singleGameId.HasValue
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
                        SingleGameId = gameId,
                        SurfaceUserNotices = true
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

            // Canaries on the OUTGOING full row sets. A full rebuild replaces every row at once,
            // unlike the per-game delta swap, so retention here is invisible to the delta
            // canaries. A live count that grows per refresh means the previous library-wide set
            // (and the grid containers and bindings attached to it) is still rooted.
            //
            // The list objects, not a sampled row: the previous form took _allAchievements
            // .FirstOrDefault(), and Track ignores null, so on the summary-only path -- where
            // _allAchievements is empty -- this canary reported nothing at all. It stayed silent
            // through a reported session that grew the managed heap from 122 MB to 912 MB, which
            // is the one thing it existed to catch. Tracking the container also answers the
            // sharper question: if the outgoing list is still rooted, so is every row in it.
            Common.LeakWatch.Track("Row.replacedFullSet", _allAchievements);
            Common.LeakWatch.Track("Row.replacedGameSummarySet", _allGameSummaries);
            Common.LeakWatch.Track("Row.replacedRecentSet", _allRecentAchievements);

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
            Services.Captures.CapturePresenceMarker.MarkSummaries(_allGameSummaries, _captureLibrary);
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

            // A full snapshot replaces every game's rows, so the selected game's own rows are
            // stale too. This used to happen by accident: the selection restore in
            // ApplyLeftFilters re-assigned a freshly built GameSummaryItem, and the setter read
            // the new instance as a selection change and reloaded. The setter now adopts a
            // same-game instance without reloading, so the reload this path genuinely needs is
            // asked for explicitly, through the same flag the per-game delta path uses.
            _selectedGameReloadRequested = SelectedGame?.PlayniteGameId != null;
            _ = ReloadSelectedGameIfRequestedAsync();

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
            Services.Captures.CapturePresenceMarker.MarkAchievements(_allRecentAchievements, _captureLibrary);
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

                RemoveGameAchievementRows(gameId);
                RemoveGameRows(_allGameSummaries, gameId, _gameSummarySearchIndex);
                RemoveGameRows(_allRecentAchievements, gameId, _recentAchievementSearchIndex);
                _selectedGamePipeline.Invalidate(gameId);

                if (SelectedGame?.PlayniteGameId == gameId)
                {
                    SelectedGame = null;
                }

                return true;
            }

            // Canaries on the rows this delta discards. Nothing should reference them once the
            // swap completes, so a rising live count localizes retention to whoever still holds
            // replaced rows (grid, chart, projection) rather than to a growing cache.
            //
            // Every discarded row, not the first one: a single sample answers "did this row
            // survive", which reports nothing when the set is empty and says nothing about rate.
            // LeakWatch caps each kind at 256 entries and drops collected ones first, so a set
            // that is being released stays near zero live while one that is leaking saturates --
            // and the denominator is a true creation count, so the rate stays readable.
            Common.LeakWatch.TrackAll(
                "Row.discardedAchievement",
                _allAchievements.Where(a => a?.PlayniteGameId == gameId));
            Common.LeakWatch.TrackAll(
                "Row.discardedGameSummary",
                _allGameSummaries.Where(g => g?.PlayniteGameId == gameId));

            RemoveGameAchievementRows(gameId);
            RemoveGameRows(_allGameSummaries, gameId, _gameSummarySearchIndex);
            RemoveGameRows(_allRecentAchievements, gameId, _recentAchievementSearchIndex);
            _selectedGamePipeline.Invalidate(gameId);

            // The delta replaces the library rows but not the selected game's, which are their own
            // instances built by the pipeline. Without this an icon override set from the Manage
            // window sat in the store until something re-assigned SelectedGame -- in practice, a
            // refresh -- and the grid kept showing the provider's art.
            if (SelectedGame?.PlayniteGameId == gameId && !ApplySelectedGameIconOverrides(gameId))
            {
                _selectedGameReloadRequested = true;
            }

            if (fragment.Achievements != null && fragment.Achievements.Count > 0)
            {
                _allAchievements.AddRange(fragment.Achievements);
                _filteredGlobalAchievementCount += CountFilteredRows(fragment.Achievements);
            }

            if (fragment.GameSummary != null)
            {
                _allGameSummaries.Add(fragment.GameSummary);
            }

            if (fragment.RecentAchievements != null && fragment.RecentAchievements.Count > 0)
            {
                _allRecentAchievements.AddRange(fragment.RecentAchievements);
            }

            // Only the rows just swapped in need stamping: they are freshly built, so their
            // session-only HasCaptures defaults to false, while every other row in the library
            // still carries the mark it was given. Re-stamping all three library lists per delta
            // instead copied them and re-ran the capture scan over the whole library for one
            // changed game. _allSelectedGameAchievements is not stamped here because
            // LoadSelectedGameAchievementsAsync marks its own rows, and a custom-data edit cannot
            // move a capture.
            if (fragment.GameSummary != null)
            {
                Services.Captures.CapturePresenceMarker.MarkSummaries(
                    new[] { fragment.GameSummary }, _captureLibrary);
            }

            if (fragment.Achievements != null && fragment.Achievements.Count > 0)
            {
                Services.Captures.CapturePresenceMarker.MarkAchievements(
                    fragment.Achievements, _captureLibrary);
            }

            if (fragment.RecentAchievements != null && fragment.RecentAchievements.Count > 0)
            {
                Services.Captures.CapturePresenceMarker.MarkAchievements(
                    fragment.RecentAchievements, _captureLibrary);
            }

            return true;
        }

        /// <summary>
        /// Re-stamps the selected game's rows from the stored icon overrides, the way
        /// <see cref="ApplyCapstone"/> re-stamps capstones, so an icon set elsewhere shows without
        /// rebuilding the grid and losing the right pane's search and scroll.
        /// Returns false when the rows have to be rebuilt instead.
        /// </summary>
        private bool ApplySelectedGameIconOverrides(Guid gameId)
        {
            var rows = _allSelectedGameAchievements;
            if (rows == null || rows.Count == 0)
            {
                return true;
            }

            var keys = ReadIconOverrideKeys(gameId);
            if (keys.Count == 0 && _selectedGameIconOverrideKeys.Count == 0)
            {
                return true;
            }

            // Removing an override cannot be undone in place: the applier writes only the entries
            // the store still holds, so a row would keep custom art that no longer has a record.
            // Only a rebuild reads the provider's own path back out of the cache.
            if (!_selectedGameIconOverrideKeys.IsSubsetOf(keys))
            {
                return false;
            }

            AchievementIconOverrideHelper.ApplyOverrides(
                gameId,
                rows,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                item => item.ApiName,
                (item, path) => item.UnlockedIconPath = path,
                (item, path) => item.LockedIconPath = path);

            // Replacing an image reuses its managed path, so the writes above are no-ops for the
            // row even though the file changed. The icon properties carry a cache-bust token read
            // when they are got, so re-raising them is what repaints.
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i]?.RefreshIconDisplay();
            }

            CaptureIconOverrideKeys(keys);
            return true;
        }

        /// <summary>
        /// Records which achievements the selected game's rows were built with an icon override
        /// for, so a later delta can tell a changed override from a removed one.
        /// </summary>
        private void CaptureIconOverrideKeys(HashSet<string> keys)
        {
            _selectedGameIconOverrideKeys.Clear();
            if (keys != null)
            {
                _selectedGameIconOverrideKeys.UnionWith(keys);
            }
        }

        private static HashSet<string> ReadIconOverrideKeys(Guid gameId)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unlocked = GameCustomDataLookup.GetAchievementUnlockedIconOverrides(gameId);
            if (unlocked != null)
            {
                keys.UnionWith(unlocked.Keys);
            }

            var locked = GameCustomDataLookup.GetAchievementLockedIconOverrides(gameId);
            if (locked != null)
            {
                keys.UnionWith(locked.Keys);
            }

            return keys;
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
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                // Deltas never touch identities; carry the last full build's forward so
                // profile consumers of a delta-built snapshot keep the resolved user.
                CurrentUserIdentities = _latestSnapshot?.CurrentUserIdentities
                    ?? new List<Models.Friends.FriendIdentity>(),
                // Same for the Unlock Next pool: a delta rebuilds from the unlocked source lists,
                // which never held the locked candidates, so carrying the last full build's pool
                // forward keeps the mosaic populated between full rebuilds.
                UnlockNextCandidates = _latestSnapshot?.UnlockNextCandidates
                    ?? new List<AchievementDisplayItem>(),
                UnlockNextPoolBuilt = _latestSnapshot?.UnlockNextPoolBuilt ?? false
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

            Common.LeakWatch.Track("OverviewSnapshot.delta", snapshot);
            snapshot.TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            snapshot.ApplyGameSummaryTotals(snapshot.GameSummaries, AddClamped);
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
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            PublishSharedSnapshot(snapshot);
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

        /// <summary>
        /// The provider/platform set the current <see cref="ProviderFilterGroups"/> were built
        /// from, so a tick that did not change it can leave them alone.
        /// </summary>
        private string _providerFilterOptionsSignature;

        private static string BuildProviderFilterOptionsSignature(
            Dictionary<string, SortedSet<string>> platformsByProvider)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var providerKey in platformsByProvider.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(providerKey).Append('␟');
                foreach (var platform in platformsByProvider[providerKey])
                {
                    builder.Append(platform).Append('␞');
                }

                builder.Append('␝');
            }

            return builder.ToString();
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

            // Nothing to rebuild when the provider/platform set is the one the current groups
            // were built from. The overview calls this from every delta tick, so a custom-data
            // edit - which almost never adds or removes a provider or a platform - otherwise
            // allocated a fresh group per provider and a fresh collection, and drove the
            // property cascade below, for an identical result. Selections and expansion are
            // carried on the existing groups, so keeping them is also what preserves them.
            var optionsSignature = BuildProviderFilterOptionsSignature(platformsByProvider);
            if (ProviderFilterGroups != null &&
                ProviderFilterGroups.Count > 0 &&
                string.Equals(optionsSignature, _providerFilterOptionsSignature, StringComparison.Ordinal))
            {
                return;
            }

            _providerFilterOptionsSignature = optionsSignature;

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
                ApplyOverviewPieIncludeLocked();
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
            else if (propertyName == nameof(PersistedSettings.ProviderColorOverrides))
            {
                foreach (var item in _allGameSummaries)
                {
                    item?.RefreshProviderAppearance();
                }

                UpdateAggregatePieCharts();
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
            else if (propertyName == nameof(PersistedSettings.OverviewPieIncludeLocked) ||
                propertyName == nameof(PersistedSettings.ShowOverviewPiePercentages))
            {
                ApplyOverviewPieIncludeLocked();
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

            // A reorder-only change (goals) is already applied to the rows in place by
            // ReapplyGoalOrder. Queuing a delta would rebuild the selected game and re-run the
            // control bar, discarding the filters the user currently has applied.
            if (!e.AffectsSummaryData)
            {
                return;
            }

            QueueOverviewDelta(isFullReset: false, key: e.PlayniteGameId.ToString("D"));
        }

        private void QueueOverviewDelta(bool isFullReset, string key)
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
            {
                bool addedWork;
                lock (_deltaSync)
                {
                    if (isFullReset)
                    {
                        _pendingFullResetFromDelta = true;
                        _pendingDeltaKeys.Clear();
                        addedWork = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(key))
                    {
                        // Add reports whether this key was not already queued.
                        addedWork = _pendingDeltaKeys.Add(key.Trim());
                    }
                    else
                    {
                        addedWork = false;
                    }
                }

                // Each tick recomputes library-wide state (sorts, snapshot rollups, charts,
                // filters), so its cost is independent of how many games changed. A bulk
                // refresh saves games steadily, which at the interactive interval produces
                // dozens of whole-library passes per run; widening the window while a run is
                // active collapses those into a handful without changing the end state, since
                // the run's final invalidation queues one last pass.
                var interval = _refreshService.IsRebuilding
                    ? BulkDeltaBatchInterval
                    : InteractiveDeltaBatchInterval;

                // A repeat signal for a game already queued must not push the deadline out.
                // One edit reaches here more than once -- the store's synchronous
                // CustomDataChanged, then the editor's own scoped cache invalidation behind its
                // 250ms debounce -- and restarting the timer each time turned a single edit into
                // two whole-library ticks (measured at 363ms and 334ms on a 500-game library,
                // for keys=1 both times). The tick rebuilds each key's fragment from the store
                // when it runs, so a later signal naming the same game is already covered by the
                // pass that is scheduled. Draining and queuing both happen on this thread, so a
                // signal that arrives after a drain still opens a new window rather than being
                // lost.
                if (!addedWork && _deltaBatchTimer.IsEnabled && _deltaBatchTimer.Interval == interval)
                {
                    return;
                }

                _deltaBatchTimer.Interval = interval;
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

            // Diagnostics for the per-edit hitch. This tick is queued by every custom-data edit
            // and runs 300ms later, and most of what follows the fragment build is sized to the
            // whole library rather than to the games that actually changed -- so the context
            // below deliberately carries both numbers. Nothing here was instrumented, which is
            // why a ~1s hitch could not be seen in a log at all.
            using (var tickScope = Common.PerfScope.Start(_logger, "Overview.DeltaTick", thresholdMs: 10))
            {
                tickScope?.SetContext(
                    "keys=" + keys.Count +
                    " games=" + (_allGameSummaries?.Count ?? 0) +
                    " recent=" + (_allRecentAchievements?.Count ?? 0) +
                    " ach=" + (_allAchievements?.Count ?? 0));

                await ApplyPendingDeltasCoreAsync(keys).ConfigureAwait(true);
            }
        }

        private async Task ApplyPendingDeltasCoreAsync(List<string> keys)
        {
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

                    // The other big per-refresh allocation: one full per-game payload read from
                    // the cache per delta key. Only the bounded in-memory game cache should keep
                    // these alive after the fragment is built.
                    Common.LeakWatch.Track("OverviewGameData", gameData);
                    // Fragments carry the game's achievement rows (unlocked + pinned): the
                    // delta swap removes the game's old rows from _allAchievements, so a
                    // fragment without rows would silently drop the refreshed game from the
                    // all-achievements surfaces until the next full rebuild.
                    dict[key] = gameData == null
                        ? null
                        : _dataBuilder.BuildGameFragment(
                            _settings,
                            revealedCopy,
                            gameData,
                            includeAchievementItems: true);
                }

                return dict;
            }).ConfigureAwait(true);

            if (!_isActive || _disposed)
            {
                return;
            }

            var requiresFallbackRefresh = false;
            using (var scope = Common.PerfScope.Start(_logger, "Overview.DeltaTick.ApplyFragments", thresholdMs: 5))
            {
                scope?.SetContext("keys=" + keys.Count);
                foreach (var key in keys)
                {
                    if (!ApplyFragmentDelta(key, fragments.TryGetValue(key, out var fragment) ? fragment : null))
                    {
                        requiresFallbackRefresh = true;
                        break;
                    }
                }
            }

            if (requiresFallbackRefresh)
            {
                // The full refresh re-assigns SelectedGame, which rebuilds the selected game's rows
                // on its own, so the request raised above is already answered.
                _selectedGameReloadRequested = false;
                await RefreshViewAsync();
                return;
            }

            // Whole-library sorts, run per delta tick regardless of how many games changed.
            using (var scope = Common.PerfScope.Start(_logger, "Overview.DeltaTick.Sort", thresholdMs: 5))
            {
                scope?.SetContext(
                    "games=" + (_allGameSummaries?.Count ?? 0) +
                    " recent=" + (_allRecentAchievements?.Count ?? 0));

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
            }

            // No capture re-stamp here. ApplyFragmentDelta now marks the rows it swapped in, which
            // are the only ones whose session-only HasCaptures was reset; doing it library-wide
            // per tick copied all three lists and re-ran the capture scan over every game.

            // No search-index rebuild here. ApplyFragmentDelta already dropped the entries for
            // the rows it replaced, and the index fills lazily for the new ones, so rebuilding
            // all three indexes would re-normalize the entire library on every delta batch.

            // Rebuilds the whole-library snapshot -- including the global unlock-count map and one
            // sub-dictionary per game -- then re-runs every filter and chart over it.
            using (Common.PerfScope.Start(_logger, "Overview.DeltaTick.Snapshot", thresholdMs: 5))
            {
                var snapshot = BuildSnapshotFromSourceLists();
                ApplyOverviewSummaryFromSnapshot(snapshot);
            }

            using (Common.PerfScope.Start(_logger, "Overview.DeltaTick.Filters", thresholdMs: 5))
            {
                // No RefreshFilter here. ApplyFragmentDelta has already adjusted
                // _filteredGlobalAchievementCount by the changed game's contribution, which is
                // the only thing the status line reads of that set; running the full pass
                // re-filtered and re-sorted every achievement in the library and raised a
                // collection Reset, per edit. Every user-driven filter and sort change still
                // calls RefreshFilter, which rebuilds the collection and re-seeds the count.
                ApplyLeftFilters();
            }

            using (Common.PerfScope.Start(_logger, "Overview.DeltaTick.Charts", thresholdMs: 5))
            {
                UpdateAggregatePieCharts();
            }

            using (var scope = Common.PerfScope.Start(_logger, "Overview.DeltaTick.RightFilters", thresholdMs: 5))
            {
                // With a game selected the right pane shows only that game's rows, so a delta
                // that never touched it cannot change what the pane holds -- and re-running the
                // control bar, the sort and the row limit for it was pure cost. With no game
                // selected the pane shows the library's recent achievements, which the delta did
                // move, so it still runs.
                var selectedGameId = SelectedGame?.PlayniteGameId;
                var selectedGameChanged = !IsGameSelected ||
                    !selectedGameId.HasValue ||
                    keys.Contains(selectedGameId.Value.ToString("D"), StringComparer.OrdinalIgnoreCase);

                scope?.SetContext("applied=" + selectedGameChanged);
                if (selectedGameChanged)
                {
                    ApplyRightFilters();
                }

                UpdateFilteredStatus();
            }

            using (Common.PerfScope.Start(_logger, "Overview.DeltaTick.SelectedGame", thresholdMs: 5))
            {
                await ReloadSelectedGameIfRequestedAsync();
            }
        }

        /// <summary>
        /// Rebuilds the selected game's rows for the deltas the in-place re-stamp could not cover.
        /// This is the path that clears a removed icon override, and it costs the right pane's
        /// search text, so it runs only when the re-stamp refused.
        /// </summary>
        private async Task ReloadSelectedGameIfRequestedAsync()
        {
            if (!_selectedGameReloadRequested)
            {
                return;
            }

            _selectedGameReloadRequested = false;
            var gameId = SelectedGame?.PlayniteGameId;
            if (!gameId.HasValue)
            {
                return;
            }

            await LoadSelectedGameAchievementsAsync(
                gameId,
                _selectedGameLoadCts?.Token ?? CancellationToken.None);
        }

        /// <summary>
        /// Re-stamps the Captures flag on the row collections that own the rendered instances. The
        /// display collections are CollectionHelper.Replace'd with subsets of these same backing
        /// lists, so marking the backing lists reaches the grids. Pass a sanitized capture folder to
        /// touch only one game's rows.
        /// </summary>
        private void RemarkCapturePresence(string gameFolderFilter = null)
        {
            Services.Captures.CapturePresenceMarker.MarkSummaries(
                _allGameSummaries, _captureLibrary, gameFolderFilter);
            Services.Captures.CapturePresenceMarker.MarkAchievements(
                _allRecentAchievements, _captureLibrary, gameFolderFilter);
            Services.Captures.CapturePresenceMarker.MarkAchievements(
                _allSelectedGameAchievements, _captureLibrary, gameFolderFilter);
        }

        private void OnCapturesChanged(object sender, Services.Captures.CapturesChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            RemarkCapturePresence(e?.FolderName);
        }

        // Drops a game's rows and their search-index entries together. The index is keyed by row
        // instance, so an entry left behind would both root a replaced row and answer for a row
        // that is no longer in the list. Replacement rows are not indexed here: the index fills
        // lazily on first lookup, which is what makes a per-delta whole-library rebuild
        // unnecessary.
        // One pass, not two: RemoveAll calls its predicate exactly once per element, so the
        // index entry can be dropped there rather than in a separate walk beforehand. A delta
        // does this for three library lists per changed game.
        private static void RemoveGameRows(
            List<AchievementDisplayItem> rows,
            Guid gameId,
            SearchTextIndex<AchievementDisplayItem> index)
        {
            rows?.RemoveAll(row =>
            {
                if (row?.PlayniteGameId != gameId)
                {
                    return false;
                }

                index?.Invalidate(row);
                return true;
            });
        }

        private static void RemoveGameRows(
            List<GameSummaryItem> rows,
            Guid gameId,
            SearchTextIndex<GameSummaryItem> index)
        {
            rows?.RemoveAll(row =>
            {
                if (row?.PlayniteGameId != gameId)
                {
                    return false;
                }

                index?.Invalidate(row);
                return true;
            });
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
            _filteredGlobalAchievementCount = filtered.Count;
            UpdateFilteredStatus();
        }

        /// <summary>
        /// Size of the filtered global achievement set, which is all the status line reads of it.
        /// </summary>
        /// <remarks>
        /// Maintained across delta ticks instead of recomputed. <see cref="RefreshFilter"/> re-ran
        /// the filter predicate over every achievement in the library, re-sorted the result and
        /// raised a collection Reset -- per edit, for one changed game -- and the only thing that
        /// came of it was this number: <see cref="AllAchievements"/> is not bound by any view.
        /// (OverviewControl binds SelectedGameAllAchievements; the AllAchievements binding in
        /// ViewAchievementsControl belongs to ViewAchievementsViewModel.) So a delta adjusts the
        /// count by the one game's contribution and leaves the collection to the next full
        /// RefreshFilter, which every user-driven filter and sort change still runs.
        /// </remarks>
        private int _filteredGlobalAchievementCount;

        /// <summary>
        /// Drops a game's achievement rows and their search-index entries, and subtracts what
        /// they contributed to <see cref="_filteredGlobalAchievementCount"/> — all in the single
        /// pass <see cref="RemoveGameRows"/> was already making. The filter predicate runs only
        /// on that game's rows.
        /// </summary>
        private void RemoveGameAchievementRows(Guid gameId)
        {
            if (_allAchievements == null || _allAchievements.Count == 0)
            {
                return;
            }

            var searchQuery = SearchQuery.From(SearchText);
            var removedFiltered = 0;
            _allAchievements.RemoveAll(row =>
            {
                if (row?.PlayniteGameId != gameId)
                {
                    return false;
                }

                if (FilterAchievement(row, searchQuery))
                {
                    removedFiltered++;
                }

                _globalAchievementSearchIndex?.Invalidate(row);
                return true;
            });

            _filteredGlobalAchievementCount = Math.Max(0, _filteredGlobalAchievementCount - removedFiltered);
        }

        private int CountFilteredRows(IReadOnlyList<AchievementDisplayItem> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            var searchQuery = SearchQuery.From(SearchText);
            var count = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row != null && FilterAchievement(row, searchQuery))
                {
                    count++;
                }
            }

            return count;
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
            else if (HasMaterializedGlobalAchievementItems() &&
                     _filteredGlobalAchievementCount < _totalCount)
            {
                StatusText = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Status_FilteredCounts"),
                    _filteredGlobalAchievementCount.ToString("N0", FormattingCulture.Current),
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
            return (_allAchievements?.Count ?? 0) > 0 || _filteredGlobalAchievementCount > 0;
        }

        private void RecalculateOverviewStats()
        {
            // This now calculates from the filtered view.
            //
            // One pass, not eight. This runs from ApplyLeftFilters, which the overview calls on
            // every delta tick, so each of these separate LINQ walks was another trip over the
            // filtered library for one changed game.
            var sourceList = _filteredGameSummaries;

            var totalAchievements = 0;
            var totalUnlocked = 0;
            var common = 0;
            var uncommon = 0;
            var rare = 0;
            var ultraRare = 0;
            var completed = 0;

            for (var i = 0; i < sourceList.Count; i++)
            {
                var game = sourceList[i];
                if (game == null)
                {
                    continue;
                }

                totalAchievements += game.TotalAchievements;
                totalUnlocked += game.UnlockedAchievements;
                common += game.CommonCount;
                uncommon += game.UncommonCount;
                rare += game.RareCount;
                ultraRare += game.UltraRareCount;
                if (game.IsCompleted)
                {
                    completed++;
                }
            }

            TotalGameSummaries = sourceList.Count;
            TotalAchievementsOverview = totalAchievements;
            TotalUnlockedOverview = totalUnlocked;
            TotalCommon = common;
            TotalUncommon = uncommon;
            TotalRare = rare;
            TotalUltraRare = ultraRare;
            CompletedGames = completed;

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

            // Patched in place: the delta rebuilds one game's row and leaves every other row
            // the same instance, so this raises a notification or two instead of the Reset that
            // made the bound grid re-realize its viewport on a later dispatcher pass, per edit.
            CollectionHelper.Replace(GameSummaries, displayItems);
        }

        private void SyncRecentAchievementsDisplay()
        {
            var displayItems = DisplayGridRowLimitHelper.Limit(
                _filteredRecentAchievements,
                _settings?.Persisted?.OverviewRecentAchievementsGridMaxRows);

            CollectionHelper.Replace(RecentAchievements, displayItems);
        }

        /// <summary>
        /// Re-stamps the capstone flag on the selected game's rows. Valid only when a capstone is
        /// being set, where every other row becomes a non-capstone.
        /// </summary>
        /// <summary>
        /// Re-stamps the capstone flags on the rows already in memory from the game's stored
        /// set, so the click that changed a capstone is the one that shows it.
        /// </summary>
        public bool ApplyCapstone(string capstoneApiName)
        {
            var gameId = SelectedGame?.PlayniteGameId;
            if (_allSelectedGameAchievements == null ||
                _allSelectedGameAchievements.Count == 0 ||
                gameId == null)
            {
                return false;
            }

            return PlayniteAchievementsPlugin.Instance?.AchievementMarkerToggle?
                .TryRestampCapstones(gameId.Value, _allSelectedGameAchievements) == true;
        }

        /// <summary>
        /// Re-sorts the selected game's rows after a goal toggle. Goal state only affects
        /// ordering, so this avoids the full selected-game rebuild.
        /// </summary>
        public bool ReapplyGoalOrder()
        {
            if (_allSelectedGameAchievements == null || _allSelectedGameAchievements.Count == 0)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(_selectedGameSortPath))
            {
                // Re-runs the active column sort and re-partitions goals, then syncs.
                SortSelectedGameAchievements(_selectedGameSortPath, _selectedGameSortDirection);
                return true;
            }

            // No column sort, so re-run exactly what the load path does: restore the natural order
            // as the base (the configured sort may preserve source order), apply the configured
            // default sort, then pin goals. Partitioning alone is stable, so without this a
            // removed goal would stay stranded at the top.
            RestoreSelectedGameNaturalOrder();
            AchievementSortHelper.ApplyConfiguredDefaultSort(
                _filteredSelectedGameAchievements,
                _settings?.Persisted,
                AchievementSortSurface.OverviewSelectedGame,
                AchievementSortScope.GameAchievements,
                stableOrder: AchievementSortHelper.CreateStableOrderMap(_filteredSelectedGameAchievements));
            AchievementSortHelper.ApplyGoalsFirst(_allSelectedGameAchievements);
            AchievementSortHelper.ApplyGoalsFirst(_filteredSelectedGameAchievements);
            SyncSelectedGameAchievementsDisplay();
            return true;
        }

        /// <summary>
        /// Re-orders the selected game's live lists to follow the natural-order snapshot taken
        /// when the game loaded. Items missing from the snapshot keep their relative position at
        /// the end.
        /// </summary>
        private void RestoreSelectedGameNaturalOrder()
        {
            var natural = _selectedGameDefaultOrderedAchievements;
            if (natural == null || natural.Count == 0)
            {
                return;
            }

            var indexByItem = new Dictionary<AchievementDisplayItem, int>();
            for (var i = 0; i < natural.Count; i++)
            {
                var item = natural[i];
                if (item != null && !indexByItem.ContainsKey(item))
                {
                    indexByItem[item] = i;
                }
            }

            SortByNaturalOrder(_allSelectedGameAchievements, indexByItem);
            SortByNaturalOrder(_filteredSelectedGameAchievements, indexByItem);
        }

        private static void SortByNaturalOrder(
            List<AchievementDisplayItem> items,
            IReadOnlyDictionary<AchievementDisplayItem, int> indexByItem)
        {
            if (items == null || items.Count < 2)
            {
                return;
            }

            // OrderBy is stable, so anything absent from the snapshot keeps its relative order.
            var ordered = items
                .OrderBy(item => item != null && indexByItem.TryGetValue(item, out var index)
                    ? index
                    : int.MaxValue)
                .ToList();

            items.Clear();
            items.AddRange(ordered);
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

        // The completions pie never hides its trailing slice, so it keeps the requested
        // percentage regardless; the other three follow the setting.
        private void ApplyOverviewPieIncludeLocked()
        {
            var includeLocked = _settings?.Persisted?.OverviewPieIncludeLocked ?? true;
            var showPercentages = _settings?.Persisted?.ShowOverviewPiePercentages ?? true;
            GamesPieChart.ShowCenterPercentageRequested = showPercentages;
            ProviderPieChart.ShowCenterPercentageRequested = showPercentages;
            RarityPieChart.ShowCenterPercentageRequested = showPercentages;
            TrophyPieChart.ShowCenterPercentageRequested = showPercentages;
            ProviderPieChart.IncludeLocked = includeLocked;
            RarityPieChart.IncludeLocked = includeLocked;
            TrophyPieChart.IncludeLocked = includeLocked;
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

            // Finishes, not finished games: a game with several capstones offers several, so the
            // pie partitions every finish the library holds rather than every game.
            GamesPieChart?.SetGameData(
                gamesPieSnapshot.PossibleCompletions,
                gamesPieSnapshot.Completions,
                completedLabel,
                incompleteLabel);

            var providerLookup = BuildProviderLookup(snapshot.UnlockedByProvider.Keys);
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

            // No score accumulation: the pies do not read CollectorScore or PrestigeScore, and
            // passing no adder leaves both at zero, which is what this builder always produced.
            snapshot.ApplyGameSummaryTotals(gamesList, addClamped: null);

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

        /// <param name="displayProviderKeys">
        /// Provider keys present in the data being charted. Custom-only games display as keys no
        /// registered provider owns (Custom and Custom:&lt;id&gt;), so they resolve here by key rather
        /// than through the provider list.
        /// </param>
        private Dictionary<string, (string iconKey, string colorHex)> BuildProviderLookup(
            IEnumerable<string> displayProviderKeys = null)
        {
            var providerLookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            var keys = _refreshService.Providers
                .Select(provider => provider?.ProviderKey)
                .Concat(displayProviderKeys ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key));
            foreach (var providerKey in keys)
            {
                if (providerLookup.ContainsKey(providerKey))
                {
                    continue;
                }

                if (ProviderRegistry.TryResolveProviderVisuals(
                    providerKey,
                    out var iconKey,
                    out var colorHex))
                {
                    providerLookup[providerKey] = (iconKey, colorHex);
                }
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
                TrophyPieChart.SetTrophyData(
                    snapshot?.TotalPlatinum ?? 0,
                    snapshot?.TotalGold ?? 0,
                    snapshot?.TotalSilver ?? 0,
                    snapshot?.TotalBronze ?? 0,
                    snapshot?.TotalPlatinumPossible ?? 0,
                    snapshot?.TotalGoldPossible ?? 0,
                    snapshot?.TotalSilverPossible ?? 0,
                    snapshot?.TotalBronzePossible ?? 0,
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

                    AchievementSortHelper.ApplyGoalsFirst(_filteredSelectedGameAchievements);
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
                    // Scope to the drilled category itself, not its subtree: the header counts what
                    // the grid below is showing, and that grid holds this node's own achievements
                    // only. Matching is on the storage path - the display form spells its separators
                    // out and never equals a stored label.
                    var drilledPath = SelectedGameDrilledCategoryPath;
                    var scoped = (_filteredSelectedGameAchievements ?? new List<AchievementDisplayItem>())
                        .Where(item => CategoryPathHelper.IsSame(item?.CategoryLabel, drilledPath))
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
            // Kick the compare-friend rows load immediately so the control bar's Compare
            // dropdown is available together with the rest of the bar instead of trailing
            // the achievements load; the loaded items are retargeted onto it below.
            FriendCompare?.SetGame(targetGameId, null);

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

                (List<AchievementDisplayItem> Items, bool HasCustomOrder) loadResult;
                using (PerfScope.Start(_logger, "Overview.SelectedGameLoad", thresholdMs: 25,
                    context: $"game={gameId}"))
                {
                    loadResult = await _selectedGamePipeline
                        .LoadAsync(gameId, revealedCopy, cancellationToken)
                        .ConfigureAwait(true);
                }

                if (!IsSelectedGameLoadCurrent(targetGameId, cancellationToken))
                {
                    return false;
                }

                var items = loadResult.Items ?? new List<AchievementDisplayItem>();
                var hasCustomOrder = loadResult.HasCustomOrder;
                SelectedGameHasCustomAchievementOrder = hasCustomOrder;

                using (PerfScope.Start(_logger, "Overview.SelectedGameApply", thresholdMs: 25,
                    context: $"items={items.Count}"))
                {
                    _allSelectedGameAchievements = items;
                    CaptureIconOverrideKeys(ReadIconOverrideKeys(gameId));
                    Services.Captures.CapturePresenceMarker.MarkAchievements(items, _captureLibrary);
                    // Snapshot the natural order before goals are pinned, so removing a goal can put
                    // the achievement back where it belongs instead of leaving it stranded on top.
                    _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>(items);
                    AchievementSortHelper.ApplyGoalsFirst(_allSelectedGameAchievements);
                    FriendCompare?.SetTargetItems(items);
                    UpdateSelectedGameAchievementFilterOptions(_allSelectedGameAchievements);
                    ApplyRightFilters();

                    var selectedTimelineCounts = GetSelectedGameTimelineCounts(gameId);
                    GlobalTimeline.SetCounts(selectedTimelineCounts);
                    SelectedGameTimeline.SetCounts(selectedTimelineCounts);
                }

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

            AchievementSortHelper.ApplyGoalsFirst(_allSelectedGameAchievements);
            AchievementSortHelper.ApplyGoalsFirst(_filteredSelectedGameAchievements);
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
            Common.RetentionProbes.Unregister(this);
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
            if (_captureLibrary != null)
            {
                _captureLibrary.CapturesChanged -= OnCapturesChanged;
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

            // Each chart view model is subscribed to the process-lifetime appearance event, so
            // without this every overview open strands four of them (and their series data)
            // for the rest of the session.
            GamesPieChart?.Dispose();
            RarityPieChart?.Dispose();
            ProviderPieChart?.Dispose();
            TrophyPieChart?.Dispose();

            ReleaseRetainedData();
        }

        // Eagerly drop the large per-open data set instead of waiting for GC. Playnite is a 32-bit
        // process, so under rapid open/close of the overview several full VM graphs (thousands of
        // GameSummaryItem/AchievementDisplayItem plus the search indexes) could coexist awaiting
        // collection and exhaust the address space. Releasing here bounds the retained set to one
        // live overview. Runs after _disposed and CancelPendingRefresh, so no in-flight apply can
        // repopulate these (ApplySnapshot early-returns on _disposed).
        // The _all* fields are REASSIGNED, not cleared in place: the list instances are embedded
        // in snapshots published to the widget coordinator, which retains the last one after this
        // view model dies. An in-place Clear() would gut that shared snapshot.
        private void ReleaseRetainedData()
        {
            AllAchievements.Clear();
            _filteredGlobalAchievementCount = 0;
            GameSummaries.Clear();
            RecentAchievements.Clear();
            SelectedGameAchievements.Clear();
            SelectedGameAllAchievements.Clear();

            _allAchievements = new List<AchievementDisplayItem>();
            _allGameSummaries = new List<GameSummaryItem>();
            _allRecentAchievements = new List<AchievementDisplayItem>();
            _allSelectedGameAchievements = new List<AchievementDisplayItem>();
            _filteredGameSummaries = new List<GameSummaryItem>();
            _filteredRecentAchievements = new List<AchievementDisplayItem>();
            _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();
            _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>();

            _latestSnapshot = null;

            _globalAchievementSearchIndex.Clear();
            _gameSummarySearchIndex.Clear();
            _recentAchievementSearchIndex.Clear();
        }
    }
}






