using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Models;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public partial class OverviewViewModel : ObservableObject, IDisposable, IOverviewRefreshHeaderViewModel, Common.IRetentionProbe
    {
        /// <summary>
        /// Returns true if unplayed games are included during refreshes.
        /// </summary>
        public bool IncludeUnplayedGames => _settings?.Persisted?.IncludeUnplayedGames ?? true;

        private readonly RefreshRuntime _refreshService;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementDataService _achievementDataService;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly AchievementSelectionPipeline _selectedGamePipeline;
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly OverviewLaunchContext _launchContext;
        private readonly Services.Captures.CaptureLibraryService _captureLibrary;

        private readonly OverviewDataBuilder _dataBuilder;
        private readonly ICacheReadOptimizations _cacheReads;
        private readonly IReadScheduler _scheduler;

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
        // refresh saving game after game must not drive a whole-library reload per save.
        private static readonly TimeSpan InteractiveReloadInterval = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan BulkReloadInterval = TimeSpan.FromMilliseconds(2500);
        private readonly Func<Services.Widgets.WidgetDataCoordinator> _widgetCoordinatorAccessor;
        private Services.Widgets.WidgetDataCoordinator _sharedSnapshotTarget;
        private DateTime _lastSharedPublishUtc;

        public OverviewDataSnapshot LatestSnapshot => _latestSnapshot;

        public event EventHandler SnapshotChanged;
        private bool _hasAppliedSnapshot;

        // Set on activation: the Compare dropdown's friend list is read once the first snapshot
        // has landed, so it does not hold the read connection while the window opens.
        private bool _compareFriendsStale;

        private readonly RefreshHeaderProgressTracker _progressTracker;
        private System.Windows.Threading.DispatcherTimer _reloadTimer;
        private bool _selectedGameLoadInProgress;
        private bool _selectedGameContentReady;
        private CancellationTokenSource _selectedGameLoadCts;

        // The Achievements grid: the rows in view, read through the query its filters, search,
        // sort and cap describe.
        private PagedRows<AchievementDisplayItem> _recent;
        private string _recentKey;
        private bool _recentDataChanged;

        // The summary rows of every listed game, for the Achievements grid while no game filter
        // narrows the Games grid; read again with each snapshot (the hidden games may change).
        private AchievementQuery _everyGameQuery;
        private int? _recentCap;
        private bool _recentDecorationPending;

        // The games of the unlocks the Achievements grid's filters keep, for the Games grid.
        private LiveQuery<HashSet<Guid>> _matchingGames;

        private List<AchievementDisplayItem> _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>();
        private readonly SearchTextIndex<GameSummaryItem> _gameSummarySearchIndex =
            new SearchTextIndex<GameSummaryItem>(item => SearchTextBuilder.ForGameSummary(item?.GameName));
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

        /// <summary>Compare for the achievements grid shown while no game is selected.</summary>
        public LibraryFriendCompareController RecentFriendCompare { get; }


        internal OverviewViewModel(
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            AchievementDataService achievementDataService,
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
            RecentFriendCompare = new LibraryFriendCompareController(friendCache, settings, logger);
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
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
                // A method group, not a lambda: the builder lives on in the kept snapshot's pool
                // reader, and a lambda here could share a closure that holds this view model.
                friendCache == null ? null : new Func<List<Models.Friends.FriendIdentity>>(friendCache.LoadCurrentUserIdentities));
            _selectedGamePipeline = new AchievementSelectionPipeline(_achievementDataService, _settings);
            _cacheReads = _achievementDataService.CacheReads;
            _scheduler = new DispatcherReadScheduler(
                Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);

            _reloadTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = InteractiveReloadInterval
            };
            _reloadTimer.Tick += OnReloadTimerTick;

            _progressTracker = new RefreshHeaderProgressTracker(_refreshService, _logger);
            _progressTracker.PropertyChanged += OnProgressTrackerChanged;

            // Initialize collections
            // Skeleton rows until the first snapshot lands (SyncGameSummariesDisplay keeps them).
            var gameSummaries = new BulkObservableCollection<GameSummaryItem>();
            gameSummaries.ReplaceAll(PlaceholderRows.Create(() => new GameSummaryItem { IsPlaceholder = true }));
            GameSummaries = gameSummaries;
            SelectedGameAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            SelectedGameAllAchievements = new BulkObservableCollection<AchievementDisplayItem>();
            CompletenessFilterOptions = new ObservableCollection<string>();

            // Like every other filter, the Progress dropdown starts with nothing picked, which
            // keeps every game.
            RestoreGameSummariesBarState();

            // Initialize refresh mode options from service (exclude LibrarySelected - context menu only)
            RefreshModes = new ObservableCollection<RefreshMode>(
                _refreshService.GetRefreshModes().Where(m => m.Type != RefreshModeType.LibrarySelected));

            // Seed the dropdown from the user's configured default, if it's a valid overview mode.
            var configuredDefault = _settings?.Persisted?.DefaultOverviewRefreshMode ?? RefreshModeType.Installed;
            if (RefreshModes.Any(m => m.Type == configuredDefault))
            {
                _selectedRefreshMode = configuredDefault.GetKey();
            }

            InitializeGridControlBars();
            Services.Showcase.ShowcaseControlBarStateStore.Instance.PersistUnlockToggles(
                _selectedGameControlBar,
                GridOptionKeys.Achievement.OverviewSelectedGame);
            // The achievements grid and the selected game's grid share one Compare pick: each
            // saves it to the same entry and hands it to the other.
            var stateStore = Services.Showcase.ShowcaseControlBarStateStore.Instance;
            FriendCompare.PersistSelection(
                stateStore.LoadCompareSelection(GridOptionKeys.Achievement.OverviewSelectedGame),
                state =>
                {
                    stateStore.SaveCompareSelection(GridOptionKeys.Achievement.OverviewSelectedGame, state);
                    RecentFriendCompare.AdoptSelection(state);
                });
            RecentFriendCompare.PersistSelection(
                stateStore.LoadCompareSelection(GridOptionKeys.Achievement.OverviewRecent),
                state =>
                {
                    stateStore.SaveCompareSelection(GridOptionKeys.Achievement.OverviewRecent, state);
                    FriendCompare.AdoptSelection(state);
                });
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

            if (_cacheReads?.Feed != null)
            {
                _cacheReads.Feed.AchievementDataChanged += OnAchievementDataChanged;
            }

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
                "rows={0}games selPipeline={1}games/{2}rows searchIdx={3} selRows={4}/{5} " +
                "display={6}games recent={7}loaded/{8}",
                _allGameSummaries?.Count ?? 0,
                pipelineGames,
                pipelineRows,
                _gameSummarySearchIndex.Count,
                _allSelectedGameAchievements?.Count ?? 0,
                _filteredSelectedGameAchievements?.Count ?? 0,
                GameSummaries?.Count ?? 0,
                _recent?.LoadedItems.Cast<object>().Count() ?? 0,
                _recent?.Count ?? 0);
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
                {
                    Id = ShowcaseControlBarItems.Search,
                    LabelKey = "LOCPlayAch_Filter_Games"
                }
            };
            GameSummariesControlBar.Items.Add(new GridProviderPlatformFilter(
                this,
                nameof(SelectedProviderFilterText),
                () => SelectedProviderFilterText,
                () => ProviderFilterGroups,
                CollapseUnselectedProviderFilters)
            {
                Id = ShowcaseControlBarItems.Platform,
                LabelKey = "LOCPlayAch_Common_Platform",
                Width = 170
            });
            GameSummariesControlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCompletenessFilterText),
                () => SelectedCompletenessFilterText,
                () => CompletenessFilterOptions,
                IsCompletenessFilterSelected,
                SetCompletenessFilterSelected,
                ProgressFilterChoices.Label)
            {
                Id = ShowcaseControlBarItems.Progress,
                LabelKey = "LOCPlayAch_Progress",
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
                Id = ShowcaseControlBarItems.Activity,
                LabelKey = "LOCPlayAch_Filter_ActivitySelectorPlaceholder",
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
                {
                    Id = ShowcaseControlBarItems.Search,
                    LabelKey = "LOCPlayAch_Filter_Achievements"
                }
            };
            // Compare: any friend, whose unlock state is drawn on the rows of the games they
            // have unlocks in.
            var recentCompareFilter = new GridMultiSelectFilter(
                this,
                null,
                () => RecentFriendCompare.CompareSelectionText,
                () => RecentFriendCompare.OptionKeys,
                RecentFriendCompare.IsKeySelected,
                RecentFriendCompare.SelectKey,
                RecentFriendCompare.GetDisplayNameForKey,
                () => RecentFriendCompare.IsCompareAvailable,
                RecentFriendCompare.IsKeyFavorite)
            {
                Id = ShowcaseControlBarItems.Compare,
                LabelKey = "LOCPlayAch_Filter_CompareSelectorPlaceholder",
                Width = 140,
                ToolTip = L("LOCPlayAch_Filter_CompareSelectorPlaceholder"),
                StaysOnBar = true
            };
            RecentFriendCompare.PropertyChanged += (_, __) => recentCompareFilter.Refresh();
            RecentAchievementsControlBar.Items.Add(recentCompareFilter);
            // The unlock date range; a timeline column or calendar day in the mini-showcase sets
            // it too, and the timeline and calendar mark whatever it holds.
            RecentAchievementsControlBar.Items.Add(new GridDateRangeFilter(
                this,
                nameof(UnlockSpanFilter),
                () => UnlockRangeFrom,
                () => UnlockRangeTo,
                SetUnlockRange,
                L("LOCPlayAch_Filter_AllTime"))
            {
                Id = ShowcaseControlBarItems.UnlockDate,
                LabelKey = "LOCPlayAch_Common_Unlocked",
                AutoHideWhenUnavailable = false,
                Width = 190
            });
            // Rarity (the tiers and Capstone) and trophy type, the widget bars' own filters; a
            // rarity or trophy pie slice in the mini-showcase toggles them too. The trophy filter
            // hides while no unlock has a trophy type.
            _tierFilters = new RarityTrophyControlBarFilters(ScheduleAchievementFilterApply);
            RecentAchievementsControlBar.Items.Add(_tierFilters.RarityItem);
            RecentAchievementsControlBar.Items.Add(_tierFilters.TrophyItem);
            // Achievement tags; a tag pie slice in the mini-showcase toggles them too. The grid
            // lists every game here, so it offers the global tags; a selected game's grid has its
            // own Tags filter with that game's tags.
            RecentAchievementsControlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedTagFilterText),
                () => SelectedTagFilterText,
                () => TagFilterOptions,
                IsTagFilterSelected,
                SetTagFilterSelected,
                key => Services.Achievements.AchievementTagRegistry.GetName(key))
            {
                Id = ShowcaseControlBarItems.Tag,
                LabelKey = "LOCPlayAch_Common_Tags",
                Width = 130,
                IconKeyOf = key => TagControlBarFilter.IconKey(key, null)
            });

            // The selected-game control bar is built and owned by _selectedGameControlBar.
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

        // Overview tab collections
        public ObservableCollection<GameSummaryItem> GameSummaries { get; }

        /// <summary>The Achievements grid's rows, loaded as the grid shows them; null until the first summary.</summary>
        // Skeleton rows while the grid has no read yet (a game filter waits for the snapshot).
        public System.Collections.IList RecentAchievements => (System.Collections.IList)_recent ?? _recentPlaceholders;

        private readonly List<AchievementDisplayItem> _recentPlaceholders =
            PlaceholderRows.Create(() => new AchievementDisplayItem { IsPlaceholder = true });
        public GridControlBarViewModel GameSummariesControlBar { get; private set; }
        public GridControlBarViewModel RecentAchievementsControlBar { get; private set; }
        public GridControlBarViewModel SelectedGameAchievementsControlBar => _selectedGameControlBar.ControlBar;

        private List<GameSummaryItem> _allGameSummaries = new List<GameSummaryItem>();
        private List<GameSummaryItem> _filteredGameSummaries = new List<GameSummaryItem>();

        private List<AchievementDisplayItem> _allSelectedGameAchievements = new List<AchievementDisplayItem>();
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
            // The shared pass merges a burst (clearing several groups raises once per group).
            ScheduleFilterApply();
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

        private ObservableCollection<string> _completenessFilterOptions;
        public ObservableCollection<string> CompletenessFilterOptions
        {
            get => _completenessFilterOptions;
            private set => SetValue(ref _completenessFilterOptions, value);
        }

        public string SelectedCompletenessFilterText
        {
            get
            {
                var picked = (CompletenessFilterOptions ?? new ObservableCollection<string>())
                    .Where(_selectedCompletenessFilters.Contains)
                    .Select(ProgressFilterChoices.Label)
                    .ToList();
                return picked.Count == 0 ? L("LOCPlayAch_Progress") : string.Join(", ", picked);
            }
        }

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
            SaveGameSummariesBarState();
            // Defer filter application to avoid interfering with menu click handling.
            ScheduleFilterApply();
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
            SaveGameSummariesBarState();
            // Defer filter application to avoid interfering with menu click handling.
            ScheduleFilterApply();
        }

        /// <summary>Applies a Completions pie slice click to the Progress filter: it toggles that slice's choice.</summary>
        /// <param name="key">The slice's <see cref="ProgressFilterChoices"/> key.</param>
        public void ToggleCompletenessFilterFromPieChart(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !CompletenessFilterOptions.Contains(key))
            {
                return;
            }

            if (!_selectedCompletenessFilters.Remove(key))
            {
                _selectedCompletenessFilters.Add(key);
            }

            OnPropertyChanged(nameof(SelectedCompletenessFilterText));
            SaveGameSummariesBarState();
            ScheduleFilterApply();
        }

        // The Activity choices in bar order; the saved picks are positions in this list, so they
        // survive a change of Playnite language.
        private static IList<string> PlayStatusChoices() => new[]
        {
            L("LOCPlayAch_Filter_Played"),
            L("LOCPlayAch_Filter_Unplayed")
        };

        /// <summary>
        /// Restores the game grid's saved Progress and Activity picks, the only bar choices kept
        /// across sessions so far.
        /// </summary>
        private void RestoreGameSummariesBarState()
        {
            var state = GridControlBarSurfaceState.Deserialize(
                Services.Showcase.ShowcaseControlBarStateStore.Instance.LoadSurface(GridOptionKeys.GameSummaries.Overview));
            if (state == null)
            {
                return;
            }

            if (state.ProgressKeys != null)
            {
                _selectedCompletenessFilters.Clear();
                foreach (var key in state.ProgressKeys)
                {
                    _selectedCompletenessFilters.Add(key);
                }
            }

            _selectedPlayStatusFilters.Clear();
            foreach (var choice in GridControlBarSurfaceState.RestoreActivity(state, PlayStatusChoices()))
            {
                _selectedPlayStatusFilters.Add(choice);
            }
        }

        private void SaveGameSummariesBarState()
        {
            var state = GridControlBarSurfaceState.Capture(
                _selectedCompletenessFilters.Count > 0 ? _selectedCompletenessFilters : null,
                PlayStatusChoices(),
                _selectedPlayStatusFilters);
            Services.Showcase.ShowcaseControlBarStateStore.Instance.SaveSurface(
                GridOptionKeys.GameSummaries.Overview,
                GridControlBarSurfaceState.Serialize(state));
        }

        // The games the Progress filter keeps, or null when it keeps every game.
        private Func<GameSummaryItem, bool> CompletenessMatcher() =>
            ProgressFilterChoices.Matcher(
                _selectedCompletenessFilters,
                CompletenessFilterOptions,
                ProgressFilterChoices.CurrentOrder());

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
            ? ResourceProvider.GetString("LOCPlayAch_Common_Configure")
            : ResourceProvider.GetString("LOCPlayAch_Button_Refresh");

        public string RefreshOrCancelButtonText => IsRefreshing
            ? ResourceProvider.GetString("LOCPlayAch_Common_Cancel")
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

        public bool ColorRarityColumnsByRarityRecentAchievements => _settings?.Persisted?.OverviewRecentAchievementsColorRarityColumnsByRarity ?? false;

        public bool ColorRarityColumnsByRaritySelectedGame => _settings?.Persisted?.OverviewSelectedGameColorRarityColumnsByRarity ?? false;

        public bool ShowOverviewScoreCard1 => _settings?.Persisted?.ShowOverviewScoreCard1 ?? true;

        public bool ShowOverviewScoreCard2 => _settings?.Persisted?.ShowOverviewScoreCard2 ?? true;

        public ScoreCardBadgePosition ScoreCard1BadgePosition =>
            ShowcaseWidgetOptions.GetScoreCardBadgePosition(_settings?.Persisted?.OverviewScoreCard1);

        public ScoreCardBadgePosition ScoreCard2BadgePosition =>
            ShowcaseWidgetOptions.GetScoreCardBadgePosition(_settings?.Persisted?.OverviewScoreCard2);

        public bool ShowOverviewScoreCards => ShowOverviewScoreCard1 || ShowOverviewScoreCard2;

        public bool ShowOverviewScoreCardDivider => ShowOverviewScoreCard1 && ShowOverviewScoreCard2;

        /// <summary>True until the first snapshot lands; the score cards show skeleton blocks meanwhile.</summary>
        public bool IsSnapshotPending => !_hasAppliedSnapshot;

        /// <summary>The header's first score card; its options follow <see cref="PersistedSettings.OverviewScoreCard1"/>.</summary>
        public ScoreCardViewModel ScoreCard1 { get; } = new ScoreCardViewModel(ScoreCardType.Collection);

        /// <summary>The header's second score card; its options follow <see cref="PersistedSettings.OverviewScoreCard2"/>.</summary>
        public ScoreCardViewModel ScoreCard2 { get; } = new ScoreCardViewModel(ScoreCardType.Prestige);

        // The header cards' filters, edited from each card's settings; a change reapplies both.
        private ScoreCardFilter _scoreCard1Filter;
        private ScoreCardFilter _scoreCard2Filter;

        private ScoreCardFilter ScoreCard1Filter => _scoreCard1Filter ?? (_scoreCard1Filter = new ScoreCardFilter(ApplyScoreCards));

        private ScoreCardFilter ScoreCard2Filter => _scoreCard2Filter ?? (_scoreCard2Filter = new ScoreCardFilter(ApplyScoreCards));

        public bool EnableFriendsFeatures => _settings?.Persisted?.EnableFriendsFeatures ?? true;

        /// <summary>The Year in Review button: around the turn of the year, unless turned off in its window.</summary>
        public bool ShowYearReviewButton =>
            Services.YearReview.YearReviewAvailability.IsSeason(DateTime.Today)
            && (_settings?.Persisted?.ShowYearReviewInOverview ?? true);

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
            SetValueAndReturn(ref _displayedSelectedGame, value, nameof(DisplayedSelectedGame));
        }

        private GameSummaryItem _selectedGame;
        public GameSummaryItem SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (value?.IsPlaceholder == true)
                {
                    // A skeleton row is not a game.
                    OnPropertyChanged(nameof(SelectedGame));
                    return;
                }

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
                // This cannot swallow a real data change. A per-game delta for the selected game
                // sets _selectedGameReloadRequested and updates its rows in place through
                // ReloadSelectedGameIfRequestedAsync, which does not come through here.
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
                        InvalidateLinkedSnapshots();
                    }

                    _selectedGameControlBar.ResetSelections();
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
            ResourceProvider.GetString(_launchContext == OverviewLaunchContext.Sidebar
                ? "LOCPlayAch_Common_BackToLibrary"
                : "LOCPlayAch_Common_Close");
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
                _reloadTimer?.Stop();
                _progressTracker.NotifyDeactivated();
                CancelPendingRefresh();
            }
            else
            {
                AttachSharedSnapshotPublisher();
                _progressTracker.SyncToCurrentState();
                _compareFriendsStale = true;
                // Shown again with no change while hidden: the rows it kept are current.
                if (_latestSnapshot != null && ReferenceEquals(_sharedSnapshotTarget?.CurrentSnapshot(), _latestSnapshot))
                {
                    RefreshCompareFriendsWhenIdle();
                    return;
                }

                // The snapshot read goes first on the read connection: the games grid, score
                // cards and widgets wait for it. The Achievements grid's read queues behind it.
                // A snapshot kept from an earlier open is used while no change has made it stale.
                // Shown again after a change, the rows it kept are read again with the snapshot.
                _ = RefreshViewAsync(opening: _latestSnapshot == null);
                ApplyRightFilters();
            }
        }

        private static string DescribeRefreshCaller()
        {
            // Skips this helper and RefreshViewAsync's own frame; the async state machine's
            // MoveNext and the builder's Start come next and say nothing, so they are dropped.
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames()
                ?? Array.Empty<System.Diagnostics.StackFrame>();
            return string.Join(
                " <- ",
                frames
                    .Select(frame => frame.GetMethod())
                    .Where(method => method != null &&
                                     method.Name != "MoveNext" &&
                                     method.Name != "Start" &&
                                     method.DeclaringType?.Namespace?.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal) != true)
                    .Take(8)
                    .Select(method => (method.DeclaringType?.Name ?? "?") + "." + method.Name));
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

        public Task RefreshViewAsync() => RefreshViewAsync(opening: false);

        /// <summary>
        /// Re-reads after a settings save only when the shown snapshot is no longer the current
        /// one. Most saves are view state (a Showcase page, a column ratio, a window position),
        /// and a change to the data or to a setting the snapshot reads marks it stale.
        /// </summary>
        public Task RefreshAfterSettingsSavedAsync()
        {
            var shown = _latestSnapshot;
            if (shown != null && ReferenceEquals(_sharedSnapshotTarget?.CurrentSnapshot(), shown))
            {
                return Task.CompletedTask;
            }

            return RefreshViewAsync(opening: false);
        }

        /// <param name="opening">
        /// The window just opened: a kept snapshot no change has made stale is used, and the
        /// grids' reads started at open are current, so the snapshot landing does not repeat them.
        /// </param>
        private async Task RefreshViewAsync(bool opening)
        {
            if (!_isActive)
            {
                return;
            }

            // A whole-library rebuild has eight callers and at least two public entry points, and
            // the log could not say which one ran it per edit. Taken before the first await, the
            // stack still names the synchronous chain that asked - down to the event that fired.
            if (PerfScope.PerfTracingEnabled)
            {
                _logger?.Debug("[Overview] RefreshViewAsync requested by: " + DescribeRefreshCaller());
            }

            // The build starts now, off the UI thread, ahead of whatever UI work follows the call.
            var version = Interlocked.Increment(ref _refreshVersion);

            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _refreshCts, newCts);
            try { oldCts?.Cancel(); } catch { }
            try { oldCts?.Dispose(); } catch { }

            var cancel = newCts.Token;

            try
            {
                await _refreshLock.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    // Spoiler reveals only affect the per-game grids (a separate per-game
                    // path); the overview snapshot carries no per-achievement display items,
                    // so it builds without reveal state.
                    var shared = opening ? _sharedSnapshotTarget : null;
                    var snapshot = await Task.Run(() => shared?.CurrentSnapshot() ?? _dataBuilder.Build(_settings, cancel), cancel).ConfigureAwait(false);

                    // Still off the UI thread: precompute the search-text map so ApplySnapshot
                    // only performs a cheap swap instead of tokenizing every game on the UI thread.
                    var gameEntries = _gameSummarySearchIndex.BuildEntries(snapshot?.GameSummaries);

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
                            ApplySnapshot(snapshot, gameEntries, readsCurrent: opening);
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
            Dictionary<GameSummaryItem, string> gameSummarySearchEntries = null,
            bool readsCurrent = false)
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

            // A canary on the OUTGOING game rows: a live count that grows per refresh means the
            // previous set (and the grid containers and bindings attached to it) is still rooted.
            Common.LeakWatch.Track("Row.replacedGameSummarySet", _allGameSummaries);

            _latestSnapshot = snapshot;
            _everyGameQuery = null;
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

            // The trophy options show while some game has trophies.
            if (_tierFilters.UpdateGames(_allGameSummaries))
            {
                RecentAchievementsControlBar?.Refresh();
            }

            UpdateProviderFilterOptions(_allGameSummaries);
            UpdateCompletenessFilterOptions();
            UpdatePlayStatusFilterOptions();

            // Initialize filtered lists
            _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();

            ApplyOverviewSummaryCore(snapshot);

            // The data changed, so the games the achievement filters keep and the grid's rows
            // are read again; at open they were read alongside this snapshot.
            if (!readsCurrent)
            {
                _matchingGames?.SetQuery(_matchingGames.Query);
                _recentDataChanged = true;
            }
            ApplyLeftFilters();

            // A full snapshot replaces every game's rows, so the selected game's own rows are
            // stale too. The selection restore in ApplyLeftFilters adopts a same-game instance
            // without reloading, so the reload is asked for explicitly.
            _selectedGameReloadRequested = SelectedGame?.PlayniteGameId != null;
            _ = ReloadSelectedGameIfRequestedAsync();

            RefreshSelectedGameHeaderCounts();
            RefreshCompareFriendsWhenIdle();
        }

        private void RefreshCompareFriendsWhenIdle()
        {
            if (!_compareFriendsStale)
            {
                return;
            }

            _compareFriendsStale = false;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed && _isActive)
                {
                    RecentFriendCompare.Refresh();
                }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
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

        private void ApplyOverviewSummaryCore(OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            NormalizeScoreSnapshot(snapshot);
            _latestSnapshot = snapshot;

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

            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            InvalidateLinkedSnapshots();
            PublishSharedSnapshot(snapshot);
        }

        private void MarkSnapshotApplied()
        {
            if (_hasAppliedSnapshot)
            {
                return;
            }

            _hasAppliedSnapshot = true;
            OnPropertyChanged(nameof(IsSnapshotPending));
        }

        private void ApplyScoreCards()
        {
            var useUniformRarityBadges = UseUniformRarityBadges;
            var snapshot = _latestSnapshot ?? new OverviewDataSnapshot
            {
                CollectorScore = CollectorScore,
                PrestigeScore = PrestigeScore
            };
            ApplyScoreCard(ScoreCard1, ScoreCard1Filter, _settings?.Persisted?.OverviewScoreCard1, snapshot, useUniformRarityBadges);
            ApplyScoreCard(ScoreCard2, ScoreCard2Filter, _settings?.Persisted?.OverviewScoreCard2, snapshot, useUniformRarityBadges);
        }

        // As the Scores widget applies its card: a points card narrows to the games its filters
        // keep, which also give it rarity badges and one game's art when its options ask.
        private static void ApplyScoreCard(
            ScoreCardViewModel card,
            ScoreCardFilter filter,
            ShowcaseWidgetInstanceSettings options,
            OverviewDataSnapshot snapshot,
            bool useUniformRarityBadges)
        {
            if (options == null)
            {
                return;
            }

            var type = ShowcaseWidgetOptions.GetScoreCardType(options);
            var narrowed = filter.Apply(snapshot, type, options, out var keptGames, out _);
            card.SetUseCustomColors(ShowcaseWidgetOptions.GetScoreCardUseCustomColors(options));
            card.SetShowTotal(ShowcaseWidgetOptions.GetScoreCardShowTotal(options));
            card.ApplyFor(type, narrowed, useUniformRarityBadges);
            card.SetRarityBadges(ScoreCardFilter.RarityBadgesFor(options, keptGames));
            card.SetGameArt(ScoreCardFilter.GameArtFor(options, keptGames));
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

            // The platform scores ride along so the header's platform cards hold their value too.
            var previous = _latestSnapshot;
            if (previous != null)
            {
                snapshot.ApplyPlatformScores(previous.PointsTotals);
            }
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
        }

        // The Progress choices follow the Achievement styles; picks a style no longer offers are
        // kept, unused, so turning it back on restores them.
        private void UpdateCompletenessFilterOptions()
        {
            // Only the finishes some game shows, so a choice never empties the grid.
            var options = ProgressFilterChoices.Keys(
                ProgressFilterChoices.CurrentOrder(),
                includeNoProgress: true,
                _allGameSummaries);
            if (CompletenessFilterOptions == null)
            {
                CompletenessFilterOptions = new ObservableCollection<string>(options);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(CompletenessFilterOptions, options);
            }

            OnPropertyChanged(nameof(SelectedCompletenessFilterText));
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
                OnPropertyChanged(nameof(ColorRarityColumnsByRarityRecentAchievements));
                OnPropertyChanged(nameof(ColorRarityColumnsByRaritySelectedGame));
                OnPropertyChanged(nameof(IncludeUnplayedGames));
                RaiseOverviewScoreCardVisibilityChanged();
                OnPropertyChanged(nameof(EnableFriendsFeatures));
                OnPropertyChanged(nameof(ShowYearReviewButton));
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
                _ = RefreshViewAsync();
                UpdateCompletenessFilterOptions();
                ApplyLeftFilters();
                return;
            }

            if (propertyName == nameof(PersistedSettings.ProgressDetails))
            {
                // The Progress choices and each finished game's badge follow the Achievement
                // styles; re-filtering also hands the linked widgets fresh snapshots.
                UpdateCompletenessFilterOptions();
                ApplyLeftFilters();
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
            else if (propertyName == nameof(PersistedSettings.ShowOverviewScoreCard1)
                || propertyName == nameof(PersistedSettings.ShowOverviewScoreCard2))
            {
                RaiseOverviewScoreCardVisibilityChanged();
            }
            else if (propertyName == nameof(PersistedSettings.OverviewScoreCard1)
                || propertyName == nameof(PersistedSettings.OverviewScoreCard2))
            {
                OnPropertyChanged(nameof(ScoreCard1BadgePosition));
                OnPropertyChanged(nameof(ScoreCard2BadgePosition));
                ApplyScoreCards();
            }
            else if (propertyName == nameof(PersistedSettings.EnableFriendsFeatures))
            {
                OnPropertyChanged(nameof(EnableFriendsFeatures));
            }
            else if (propertyName == nameof(PersistedSettings.ShowYearReviewInOverview))
            {
                OnPropertyChanged(nameof(ShowYearReviewButton));
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
                UpdateRecentQuery();
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

                ScoreCard1.RefreshProviderAppearance();
                ScoreCard2.RefreshProviderAppearance();

                // The mini-showcase's pies and timeline draw in platform colors.
                InvalidateLinkedSnapshots(rebuild: true);
            }
            else if (RarityAppearanceHelper.IsAppearanceSettingPropertyName(propertyName))
            {
                OnPropertyChanged(nameof(UseUniformRarityBadges));
                ApplyScoreCards();
                InvalidateLinkedSnapshots(rebuild: true);
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
            else if (AchievementSortHelper.IsConfiguredDefaultSortPropertyName(
                propertyName,
                AchievementSortSurface.OverviewRecentAchievements))
            {
                if (!IsGameSelected && string.IsNullOrEmpty(_recentSortPath))
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
            OnPropertyChanged(nameof(ShowOverviewScoreCard1));
            OnPropertyChanged(nameof(ShowOverviewScoreCard2));
            OnPropertyChanged(nameof(ShowOverviewScoreCards));
            OnPropertyChanged(nameof(ShowOverviewScoreCardDivider));
        }

        private void RevealAchievement(AchievementDisplayItem item)
        {
            if (item == null)
            {
                return;
            }

            var key = AchievementDisplayItem.MakeRevealKey(item.PlayniteGameId, item.ApiName, item.GameName);

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

        // A change of the data the summary reads: the projection's copy is stale too, even for a
        // write that raised no cache event of its own (an override or a capture).
        private void OnAchievementDataChanged(object sender, AchievementDataChange e)
        {
            if (!_isActive || _disposed || e == null || !e.Touches(AchievementQuery.CurrentUser))
            {
                return;
            }

            QueueReload();
        }

        private void OnCustomDataChanged(object sender, GameCustomDataChangedEventArgs e)
        {
            if (!_isActive || e == null || e.PlayniteGameId == Guid.Empty)
            {
                return;
            }

            // A reorder-only change (goals) is already applied to the rows in place by
            // ReapplyGoalOrder.
            if (!e.AffectsSummaryData)
            {
                return;
            }

            QueueReload();
        }

        /// <summary>
        /// Reloads the summary off the UI thread at the end of a window, which then reads the
        /// grids' rows again. A bulk refresh saves game after game, so the window widens while one
        /// runs; a repeat inside a window does not push it out.
        /// </summary>
        private void QueueReload()
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
            {
                if (!_isActive || _disposed)
                {
                    return;
                }

                var interval = _refreshService.IsRebuilding ? BulkReloadInterval : InteractiveReloadInterval;
                if (_reloadTimer.IsEnabled && _reloadTimer.Interval == interval)
                {
                    return;
                }

                _reloadTimer.Interval = interval;
                _reloadTimer.Stop();
                _reloadTimer.Start();
            });
        }

        /// <summary>
        /// Brings the selected game's rows up to date after a delta or snapshot touched that game,
        /// so its labels, icons, category filter and grouping follow the change. The rows are
        /// updated in place and the right pane's search text is kept, since the user is still on
        /// the same game.
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
                _selectedGameLoadCts?.Token ?? CancellationToken.None,
                inPlace: true);
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
                LoadedRecentRows(), _captureLibrary, gameFolderFilter);
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

        private async void OnReloadTimerTick(object sender, EventArgs e)
        {
            _reloadTimer.Stop();
            try
            {
                await RefreshViewAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reload the overview after a data change");
            }
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
            // The skeleton rows stay until the first snapshot lists the games.
            if (_latestSnapshot == null)
            {
                return;
            }

            var displayItems = DisplayGridRowLimitHelper.Limit(
                _filteredGameSummaries,
                _settings?.Persisted?.OverviewGameSummariesGridMaxRows);

            // Patched in place: the delta rebuilds one game's row and leaves every other row
            // the same instance, so this raises a notification or two instead of the Reset that
            // made the bound grid re-realize its viewport on a later dispatcher pass, per edit.
            CollectionHelper.Replace(GameSummaries, displayItems);
        }

        /// <summary>
        /// The Achievements grid's filters as a read: the unlocks they keep, under the summary
        /// rules, of <paramref name="source"/>'s rows.
        /// </summary>
        private AchievementQuery AchievementFilterQuery(AchievementQuery source = null)
        {
            var query = _tierFilters.ApplyTo((source ?? AchievementQuery.CurrentUser.WithSummaryRules()).WhereUnlocked(true));
            if (UnlockSpanFilter.HasValue)
            {
                query = query.WhereUnlockedBetween(_unlockRangeFrom, _unlockRangeTo);
            }

            return _selectedTagFilters.Count > 0 ? query.WhereClassification(_selectedTagFilters.ToList()) : query;
        }

        /// <summary>
        /// Points the Achievements grid at the unlocks its filters keep, of the games the Games
        /// grid lists, matching its search, in the column or configured order, up to the row cap.
        /// A query that is unchanged is left alone. With no game filter on, the games listed are
        /// every summary game, so the grid reads at open without waiting for the snapshot.
        /// </summary>
        private void UpdateRecentQuery()
        {
            if (_cacheReads == null || _disposed)
            {
                return;
            }

            AchievementQuery query;
            if (ListsEveryGame())
            {
                query = AchievementFilterQuery(_everyGameQuery ??= _achievementDataService.SummaryQuery());
            }
            else if (_latestSnapshot == null)
            {
                return;
            }
            else
            {
                query = AchievementFilterQuery().ForGames(_filteredGameSummaries
                    .Where(game => game?.PlayniteGameId != null)
                    .Select(game => game.PlayniteGameId.Value));
            }

            if (SearchQuery.From(RightSearchText).HasValue)
            {
                query = query.WhereSearch(RightSearchText);
            }

            query = ApplyRecentSort(query);
            var key = AchievementQuerySql.KeyOf(query);
            var dataChanged = _recentDataChanged;
            _recentDataChanged = false;
            var cap = PersistedSettings.NormalizeGridMaxRows(_settings?.Persisted?.OverviewRecentAchievementsGridMaxRows);
            if (_recent == null || cap != _recentCap)
            {
                _recent?.Dispose();
                _recentCap = cap;
                _recentKey = key;
                _recent = new PagedRows<AchievementDisplayItem>(
                    null,
                    query,
                    read => _cacheReads.Reads.SortedKeys(read),
                    LoadRecentRows,
                    () => new AchievementDisplayItem { IsPlaceholder = true },
                    _scheduler,
                    cap ?? 0,
                    onError: ex => _logger?.Error(ex, "Failed to load the overview's achievement rows."),
                    update: (kept, fresh) => kept.RefreshFrom(fresh),
                    pendingRows: PlaceholderRows.Count);
                _recent.CollectionChanged += OnRecentRowsChanged;
                _recent.PropertyChanged += OnRecentRowsChanged;
                OnPropertyChanged(nameof(RecentAchievements));
                return;
            }

            if (!string.Equals(key, _recentKey, StringComparison.Ordinal))
            {
                _recentKey = key;
                _recent.SetQuery(query);
            }
            else if (dataChanged)
            {
                _recent.Refresh();
            }
        }

        // The column sort, else the configured default, else newest first.
        private AchievementQuery ApplyRecentSort(AchievementQuery query)
        {
            if (!string.IsNullOrEmpty(_recentSortPath) && AchievementQuery.SortOfColumn(_recentSortPath) is AchievementSort column)
            {
                return query.OrderBy(column, _recentSortDirection == ListSortDirection.Descending);
            }

            var configured = AchievementSortHelper.GetConfiguredDefaultSort(
                _settings?.Persisted,
                AchievementSortSurface.OverviewRecentAchievements);
            return !configured.PreservesSourceOrder && AchievementQuery.SortOfColumn(configured.SortMemberPath) is AchievementSort configuredSort
                ? query.OrderBy(configuredSort, configured.Direction == ListSortDirection.Descending)
                : query.OrderBy(AchievementSort.UnlockTime, descending: true);
        }

        // Off the UI thread.
        private IReadOnlyList<AchievementDisplayItem> LoadRecentRows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys)
        {
            using (PerfScope.Start(_logger, "Overview.LoadRows", thresholdMs: 25, context: "rows=" + keys.Count))
            {
                return _dataBuilder.LoadRows(_settings, query, keys);
            }
        }

        private List<AchievementDisplayItem> LoadedRecentRows() =>
            _recent?.LoadedItems.Cast<AchievementDisplayItem>().ToList() ?? new List<AchievementDisplayItem>();

        // Rows arrive a page at a time, filled in place (an Item[] change) or added; the loaded
        // ones take the capture marks and the compare pick once per batch of changes.
        private void OnRecentRowsChanged(object sender, EventArgs e)
        {
            if (_recentDecorationPending)
            {
                return;
            }

            _recentDecorationPending = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                _recentDecorationPending = false;
                if (_disposed || !ReferenceEquals(sender, _recent))
                {
                    return;
                }

                var loaded = LoadedRecentRows();
                Services.Captures.CapturePresenceMarker.MarkAchievements(loaded, _captureLibrary);
                RecentFriendCompare.SetTargetItems(loaded);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void DisposeReads()
        {
            if (_recent != null)
            {
                _recent.CollectionChanged -= OnRecentRowsChanged;
                _recent.PropertyChanged -= OnRecentRowsChanged;
                _recent.Dispose();
                _recent = null;
                _recentKey = null;
                OnPropertyChanged(nameof(RecentAchievements));
            }

            _matchingGames?.Dispose();
            _matchingGames = null;
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

            // Re-run the full ordering from the natural order rather than re-partitioning in
            // place: partitioning alone is stable, so a removed goal would stay stranded at the top.
            OrderSelectedGameAchievements(useSourceOrder: false);
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
            using (PerfScope.Start(_logger, "Overview.ApplyLeftFilters", thresholdMs: 10))
            {
                ApplyLeftFiltersCore();
            }
        }

        private void ApplyLeftFiltersCore()
        {
            // Preserve selection across filter updates
            Guid? selectedGameId = SelectedGame?.PlayniteGameId;

            var filtered = ApplyAchievementFiltersToGames(ApplyGameFilters(_allGameSummaries));

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

            // The Achievements grid shows the achievements of the games listed here, so it follows.
            if (!IsGameSelected)
            {
                using (PerfScope.Start(_logger, "Overview.ApplyLeftFilters.Achievements", thresholdMs: 10))
                {
                    ApplyRightFilters();
                }
            }

            InvalidateLinkedSnapshots();
        }

        // The Games grid's own filters: search, platform, play status and completeness. Each one
        // passes its input through untouched while it is off.
        private IEnumerable<GameSummaryItem> ApplyGameFilters(IEnumerable<GameSummaryItem> games)
        {
            var filtered = games;
            var searchQuery = SearchQuery.From(LeftSearchText);
            if (searchQuery.HasValue)
            {
                filtered = filtered.Where(g => _gameSummarySearchIndex.Matches(g, searchQuery));
            }

            filtered = OverviewGameSummaryFilters.ApplyProviderPlatformFilter(filtered, ProviderFilterGroups);
            filtered = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                filtered,
                _selectedPlayStatusFilters,
                null,
                L("LOCPlayAch_Filter_Played"),
                L("LOCPlayAch_Filter_Unplayed"));
            var matchesProgress = CompletenessMatcher();
            return matchesProgress != null ? filtered.Where(matchesProgress) : filtered;
        }

        // True while no game filter is on, so the Games grid lists every game. The achievement
        // filters narrow the Achievements grid's rows themselves.
        private bool ListsEveryGame()
        {
            var probe = Array.Empty<GameSummaryItem>();
            return ReferenceEquals(ApplyGameFilters(probe), probe);
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
                L("LOCPlayAch_Common_Platform"));
        }

        private void ApplyRightFilters(bool skipDefaultSort = false)
        {
            // Contextually filter based on IsGameSelected
            if (IsGameSelected)
            {
                // The shared control bar owns the filter predicate (search + Unlocked/Locked/
                // Hidden + Type/Category); this VM keeps sorting, row limiting, and header counts.
                _filteredSelectedGameAchievements = _selectedGameControlBar
                    .Apply(_allSelectedGameAchievements)
                    .ToList();

                OrderSelectedGameAchievements(useSourceOrder: skipDefaultSort);
                SyncSelectedGameAchievementsDisplay();
            }
            else
            {
                UpdateRecentQuery();
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
        }

        private async Task<bool> LoadSelectedGameAchievementsAsync(
            Guid? targetGameId,
            CancellationToken cancellationToken,
            bool inPlace = false)
        {
            // Reset right search when selecting a game. A reload of the game already selected
            // keeps it, and keeps its rows: that is the data changing under the user, not the
            // user moving on.
            if (!inPlace)
            {
                RightSearchText = string.Empty;
            }

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

                // Back to the library's achievements, which may have moved while a game was shown.
                ApplyRightFilters();
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
                    context: $"items={items.Count} inPlace={inPlace}"))
                {
                    if (inPlace)
                    {
                        items = MergeIntoShownSelectedGameRows(gameId, items);
                    }

                    _allSelectedGameAchievements = items;
                    Services.Captures.CapturePresenceMarker.MarkAchievements(items, _captureLibrary);
                    // Snapshot the natural order before goals are pinned, so removing a goal can put
                    // the achievement back where it belongs instead of leaving it stranded on top.
                    _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>(items);
                    AchievementSortHelper.ApplyGoalsFirst(_allSelectedGameAchievements);
                    FriendCompare?.SetTargetItems(items);
                    UpdateSelectedGameAchievementFilterOptions(_allSelectedGameAchievements);
                    ApplyRightFilters();
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

        /// <summary>
        /// Brings the rows the pane already shows onto a fresh build of the same game, keeping each
        /// row's instance. The pane's collections then sync with only the achievements that came or
        /// went to move, so the grid keeps its containers, scroll position and the session flags
        /// the rows carry, where handing it all-new instances re-realized every row.
        /// </summary>
        /// <remarks>
        /// Keyed on the natural-order list, which holds exactly the rows the last load produced.
        /// A list for some other game - the selection moved while this load ran - is left alone
        /// and the fresh rows are used as they are.
        /// </remarks>
        private List<AchievementDisplayItem> MergeIntoShownSelectedGameRows(
            Guid gameId,
            List<AchievementDisplayItem> fresh)
        {
            var shown = _selectedGameDefaultOrderedAchievements;
            if (shown == null || shown.Count == 0 || shown[0]?.PlayniteGameId != gameId)
            {
                return fresh;
            }

            var iconOverrideKeys = ReadIconOverrideKeys(gameId);
            return CollectionHelper.MergeByKey(
                shown,
                fresh,
                row => row?.ApiName,
                (kept, source) =>
                {
                    kept.UpdateFrom(source);

                    // Replacing an override image reuses its managed path, so the update raises
                    // nothing for it. The icon properties read a cache-bust token when they are
                    // got, so re-raising them is what repaints.
                    if (!string.IsNullOrWhiteSpace(kept.ApiName) && iconOverrideKeys.Contains(kept.ApiName))
                    {
                        kept.RefreshIconDisplay();
                    }
                });
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

        // A column with no query order is ignored.
        private void SortRecentAchievements(string sortMemberPath, ListSortDirection direction)
        {
            if (AchievementQuery.SortOfColumn(sortMemberPath) == null)
            {
                return;
            }

            _recentSortPath = sortMemberPath;
            _recentSortDirection = direction;
            UpdateRecentQuery();
        }

        private void SortSelectedGameAchievements(string sortMemberPath, ListSortDirection direction)
        {
            if (string.IsNullOrWhiteSpace(sortMemberPath) ||
                AchievementSortHelper.GetComparison(sortMemberPath, direction, AchievementSortScope.GameAchievements) == null)
            {
                return;
            }

            _selectedGameSortPath = sortMemberPath;
            _selectedGameSortDirection = direction;
            OrderSelectedGameAchievements(useSourceOrder: false);
            SyncSelectedGameAchievementsDisplay();
        }

        /// <summary>
        /// Re-orders the selected game's live lists from the natural-order snapshot through the
        /// shared grid ordering: the active column sort, otherwise the configured default sort
        /// unless <paramref name="useSourceOrder"/>, then goals first.
        /// </summary>
        private void OrderSelectedGameAchievements(bool useSourceOrder)
        {
            RestoreSelectedGameNaturalOrder();
            foreach (var items in new[] { _allSelectedGameAchievements, _filteredSelectedGameAchievements })
            {
                AchievementSortHelper.OrderGameAchievementItems(
                    items,
                    _selectedGameSortPath,
                    SelectedGameSortDirection,
                    useSourceOrder,
                    _settings?.Persisted,
                    AchievementSortSurface.OverviewSelectedGame);
            }
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
            _reloadTimer?.Stop();
            CancelPendingRefresh();
            if (_progressTracker != null)
            {
                _progressTracker.PropertyChanged -= OnProgressTrackerChanged;
                _progressTracker.Dispose();
            }
            if (_cacheReads?.Feed != null)
            {
                _cacheReads.Feed.AchievementDataChanged -= OnAchievementDataChanged;
            }
            if (_captureLibrary != null)
            {
                _captureLibrary.CapturesChanged -= OnCapturesChanged;
            }
            if (_gameCustomDataStore != null)
            {
                _gameCustomDataStore.CustomDataChanged -= OnCustomDataChanged;
            }
            if (_settings != null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                }
            }
            if (_reloadTimer != null)
            {
                _reloadTimer.Tick -= OnReloadTimerTick;
            }

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
            DisposeReads();
            GameSummaries.Clear();
            SelectedGameAchievements.Clear();
            SelectedGameAllAchievements.Clear();

            _allGameSummaries = new List<GameSummaryItem>();
            _allSelectedGameAchievements = new List<AchievementDisplayItem>();
            _filteredGameSummaries = new List<GameSummaryItem>();
            _filteredSelectedGameAchievements = new List<AchievementDisplayItem>();
            _selectedGameDefaultOrderedAchievements = new List<AchievementDisplayItem>();

            _latestSnapshot = null;
            RecentFriendCompare.Release();

            _gameSummarySearchIndex.Clear();
        }
    }
}






