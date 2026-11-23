using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The Friends Overview window. Its friend and game grids come from one lists read with the
    /// unlocked totals per friend and game; its achievements grid is a <see cref="PagedRows{T}"/>
    /// over the query of the current selection, filters, search, sort and compare pick. Every
    /// read reruns from the change feed.
    /// </summary>
    internal sealed class FriendsOverviewViewModel :
        ObservableObject, IDisposable, IOverviewRefreshHeaderViewModel, IFriendSummariesFilterSource
    {
        private readonly IFriendCacheManager _friendCache;
        private readonly IFriendsOverviewReads _reads;
        private readonly IReadScheduler _scheduler;
        private readonly Services.Cache.FeedGate _gate = new Services.Cache.FeedGate();
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly RefreshRuntime _refreshRuntime;
        private readonly PlayniteAchievementsSettings _settings;
        private PersistedSettingsSubscription _persistedSubscription;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly Func<Guid?, string, FriendCustomRefreshOptions> _showCustomRefreshDialog;
        private readonly SearchTextIndex<FriendSummaryItem> _friendSearchIndex =
            new SearchTextIndex<FriendSummaryItem>(item => SearchTextBuilder.FromValues(
                new[] { item?.DisplayName, item?.ProviderKey }.Concat(item?.MemberProviderKeys ?? Enumerable.Empty<string>())));
        private readonly SearchTextIndex<FriendGameSummaryItem> _gameSearchIndex =
            new SearchTextIndex<FriendGameSummaryItem>(item => SearchTextBuilder.FromValues(item?.GameName, item?.ProviderKey));

        // The reads: the lists with friend scores, the favorites' games, the bar's options for a
        // selected game, the pair's whole row list for category rollups, and the grid's rows.
        private LiveQuery<FriendsOverviewLists> _lists;
        private FriendsOverviewLists _current = FriendsOverviewLists.Empty;
        private LiveQuery<IReadOnlyList<AchievementTotalsRecord>> _favoriteGames;
        private string _favoriteGamesKey;
        private LiveQuery<AchievementBarOptions> _barOptions;
        private string _barOptionsKey;
        private LiveQuery<IReadOnlyList<FriendAchievementDisplayItem>> _pairRows;
        private string _pairRowsKey;
        private PagedRows<FriendAchievementDisplayItem> _achievements;
        private string _achievementsKey;
        private string _achievementsScopeKey;
        private int? _achievementsCap;
        private volatile RowDecoration _rowDecoration;

        private List<FriendSummaryItem> _filteredFriendsList = new List<FriendSummaryItem>();
        private List<FriendGameSummaryItem> _filteredGamesList = new List<FriendGameSummaryItem>();
        private string _friendSortPath;
        private ListSortDirection _friendSortDirection;
        private string _gameSortPath;
        private ListSortDirection _gameSortDirection;
        private string _achievementSortPath;
        private ListSortDirection _achievementSortDirection;
        private readonly AchievementGridControlBarAdapter _achievementsBar = new AchievementGridControlBarAdapter();
        // Compare selection for the achievement grid in each of its four views. It is dropped
        // whenever the selected friend or game changes, and the pick remembered for the view the
        // grid is in is selected again when it is on offer.
        private readonly FriendVsFriendCompareController _friendCompare;
        private FriendSummaryItem _compareScopeFriend;
        private FriendGameSummaryItem _compareScopeGame;
        private Func<string, string> _loadCompareSelection;
        private Action<string, string> _saveCompareSelection;
        private string _compareSurfaceKey;
        private readonly HashSet<string> _selectedOwnershipFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedFriendProviderFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Session-only, like every other filter on these bars: off each time the window opens.
        private bool _favoritesOnly;
        private ObservableCollection<ProviderFilterGroup> _gamePlatformFilterGroups =
            new ObservableCollection<ProviderFilterGroup>();
        private FriendSummaryItem _gameFilterOptionsFriend;
        private bool _gameFilterOptionsBuilt;
        private FriendSummaryItem _selectedFriend;
        private FriendGameSummaryItem _selectedGame;
        private bool _isApplyingFilters;
        private bool _notifyingSelection;
        private bool _barOptionsOffered = true;
        private bool _isRefreshing;
        private bool _disposed;

        // The friend entries and merge groups as of the last read; see OnPluginSettingsSaved.
        private string _readFriendSettingsKey;
        private string _statusText;
        private string _friendSearchText;
        private string _gameSearchText;
        private string _achievementSearchText;
        private string _selectedRefreshMode = RefreshModeType.FriendsRecent.GetKey();
        private readonly RefreshHeaderProgressTracker _progressTracker;
        private int _searchVersion;

        private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

        // How long after a change the reads rerun while friends refresh: a refresh writes every
        // few hundred milliseconds.
        private static readonly TimeSpan RefreshingWindow = TimeSpan.FromSeconds(3);

        /// <param name="reads">The window's reads; null shows no data.</param>
        /// <param name="scheduler">Where reads run and apply; the application dispatcher by default.</param>
        public FriendsOverviewViewModel(
            IFriendCacheManager friendCache,
            IFriendsOverviewReads reads,
            RefreshEntryPoint refreshCoordinator,
            RefreshRuntime refreshRuntime,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            IPlayniteAPI playniteApi = null,
            Func<Guid?, string, FriendCustomRefreshOptions> showCustomRefreshDialog = null,
            IReadScheduler scheduler = null)
        {
            _friendCache = friendCache;
            _reads = reads;
            _scheduler = scheduler ?? new DispatcherReadScheduler(
                System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
            _refreshCoordinator = refreshCoordinator;
            _refreshRuntime = refreshRuntime;
            _settings = settings;
            _logger = logger;
            _playniteApi = playniteApi;
            _showCustomRefreshDialog = showCustomRefreshDialog;

            // Skeleton rows in every grid until the lists land (the display syncs keep them).
            FilteredFriends = new BulkObservableCollection<FriendSummaryItem>();
            FilteredFriends.ReplaceAll(PlaceholderRows.Create(() => new FriendSummaryItem { IsPlaceholder = true }));
            FilteredGames = new BulkObservableCollection<FriendGameSummaryItem>();
            FilteredGames.ReplaceAll(PlaceholderRows.Create(() => new FriendGameSummaryItem { IsPlaceholder = true }));
            _achievementPlaceholders = PlaceholderRows.Create(() => new FriendAchievementDisplayItem { IsPlaceholder = true });
            SelectedFriendGameAllAchievements = new BulkObservableCollection<FriendAchievementDisplayItem>();
            OwnershipFilterOptions = new ObservableCollection<string>();
            FriendProviderFilterOptions = new ObservableCollection<string>();
            FriendSummariesControlBar = CreateFriendSummariesControlBar();
            GameSummariesControlBar = CreateGameSummariesControlBar();
            _friendCompare = new FriendVsFriendCompareController(
                () => SelectedFriend,
                GetCompareFriendOptions,
                loadCurrentUserIdentities: () => _friendCache?.LoadCurrentUserIdentities(),
                logger: logger,
                hasPairData: HasCompareGameData,
                scopeProviderKey: () => SelectedGame?.ProviderKey ??
                    (SelectedFriend?.IsMergedFriend == false ? SelectedFriend.ProviderKey : null));
            // A pick in the Compare dropdown, or the signed-in accounts arriving for the self
            // entry, rereads the rows; a selection change rereads them once it has settled. The
            // accounts arrive off the UI thread.
            _friendCompare.PropertyChanged += (_, __) =>
            {
                if (!_notifyingSelection && !_isApplyingFilters)
                {
                    _scheduler.Post(UpdateAchievementQueries);
                }
            };
            _achievementsBar.FilterChanged += (_, __) => UpdateAchievementQueries();
            _achievementsBar.AttachFriendCompare(_friendCompare);
            // The overview keeps its own achievement search over friend, game, achievement,
            // category and provider fields, run in the query; the adapter's search stays empty.
            _achievementsBar.ControlBar.Search = new GridSearchControl(
                this,
                nameof(AchievementSearchText),
                () => AchievementSearchText,
                value => AchievementSearchText = value,
                GridControlBarText.Get("LOCPlayAch_Filter_Achievements", "Search Achievements"),
                ClearAchievementSearch);
            AchievementsControlBar = _achievementsBar.ControlBar;
            FriendRefreshModes = new ObservableCollection<RefreshMode>(CreateFriendRefreshModes());

            RefreshCommand = new AsyncCommand(async _ => await RefreshSelectedModeAsync().ConfigureAwait(true), _ => CanRefresh());
            RefreshRecentCommand = new AsyncCommand(async _ => await RefreshFriendsAsync(RefreshModeType.FriendsRecent).ConfigureAwait(true), _ => CanRefresh());
            RefreshFullCommand = new AsyncCommand(async _ => await RefreshFriendsAsync(RefreshModeType.FriendsFull).ConfigureAwait(true), _ => CanRefresh());
            RefreshSharedCommand = new AsyncCommand(async _ => await RefreshFriendsAsync(RefreshModeType.FriendsShared).ConfigureAwait(true), _ => CanRefresh());
            RefreshInstalledCommand = new AsyncCommand(async _ => await RefreshFriendsAsync(RefreshModeType.FriendsInstalled).ConfigureAwait(true), _ => CanRefresh());
            RefreshFriendSelectedGameCommand = new AsyncCommand(ExecuteSelectedFriendTargetRefreshAsync, parameter => CanRefreshSelectedFriendTarget(parameter));
            OpenGameInLibraryCommand = new RelayCommand(OpenGameInLibrary);
            RefreshOrCancelCommand = new RelayCommand(ExecuteRefreshOrCancel, _ => CanExecuteRefreshOrCancel());
            ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
            ClearFriendSelectionCommand = new RelayCommand(_ => ClearFriendSelection());
            ClearGameSelectionCommand = new RelayCommand(_ => ClearGameSelection());

            if (_refreshRuntime != null)
            {
                _progressTracker = new RefreshHeaderProgressTracker(_refreshRuntime, logger);
                _progressTracker.PropertyChanged += OnProgressTrackerChanged;
                _refreshRuntime.RebuildProgress += OnRebuildProgress;
            }

            if (_settings != null)
            {
                // Tracks the current Persisted instance: CancelEdit replaces it, and a
                // direct subscription would leave this view bound to the orphan. The swap
                // re-derives everything, the same as a settings change with no name.
                _persistedSubscription = new PersistedSettingsSubscription(
                    _settings,
                    OnPersistedSettingsChanged,
                    () => OnPersistedSettingsChanged(this, new PropertyChangedEventArgs(null)));
            }

            _readFriendSettingsKey = FriendSettingsKey();
            PlayniteAchievementsPlugin.SettingsSaved += OnPluginSettingsSaved;
        }

        public BulkObservableCollection<FriendSummaryItem> FilteredFriends { get; }
        public BulkObservableCollection<FriendGameSummaryItem> FilteredGames { get; }

        /// <summary>
        /// The achievements grid's rows, loaded page by page; replaced when the row cap changes.
        /// Null until <see cref="LoadAsync"/>.
        /// </summary>
        public PagedRows<FriendAchievementDisplayItem> DisplayedAchievements => _achievements;

        /// <summary>What the achievements grid shows: its rows, or skeleton rows until the lists land.</summary>
        public System.Collections.IList AchievementRows => (System.Collections.IList)_achievements ?? _achievementPlaceholders;

        private List<FriendAchievementDisplayItem> _achievementPlaceholders;

        // The selected friend and game's every row in definition order, feeding the grid's
        // CategorySummarySource so category rollups follow neither the filters nor the sort.
        public BulkObservableCollection<FriendAchievementDisplayItem> SelectedFriendGameAllAchievements { get; }

        public BulkObservableCollection<FriendSummaryItem> Friends => FilteredFriends;
        public BulkObservableCollection<FriendGameSummaryItem> Games => FilteredGames;

        public string FriendSortPath => _friendSortPath;

        public ListSortDirection? FriendSortDirection =>
            string.IsNullOrWhiteSpace(_friendSortPath) ? (ListSortDirection?)null : _friendSortDirection;

        public string GameSortPath => _gameSortPath;

        public ListSortDirection? GameSortDirection =>
            string.IsNullOrWhiteSpace(_gameSortPath) ? (ListSortDirection?)null : _gameSortDirection;

        public string AchievementSortPath => _achievementSortPath;

        public ListSortDirection? AchievementSortDirection =>
            string.IsNullOrWhiteSpace(_achievementSortPath) ? (ListSortDirection?)null : _achievementSortDirection;
        public GridControlBarViewModel FriendSummariesControlBar { get; }
        public GridControlBarViewModel GameSummariesControlBar { get; }
        public GridControlBarViewModel AchievementsControlBar { get; }

        /// <summary>
        /// Restores the achievement bar's Unlocked, Locked and Hidden toggles from
        /// <paramref name="saved"/> and hands <paramref name="save"/> each later change. The view
        /// calls this before the first load.
        /// </summary>
        public void PersistAchievementUnlockToggles(string saved, Action<string> save) =>
            _achievementsBar.PersistUnlockToggles(saved, save);

        /// <summary>
        /// Remembers the achievement grid's Compare pick per view. <paramref name="load"/> returns
        /// the pick saved for a grid surface key and <paramref name="save"/> stores one; the view
        /// calls this before the first load.
        /// </summary>
        public void PersistCompareSelections(Func<string, string> load, Action<string, string> save)
        {
            _loadCompareSelection = load;
            _saveCompareSelection = save;
            _compareSurfaceKey = null;
        }

        private string CompareSurfaceKey => HasFriendGameSelection
            ? GridOptionKeys.Achievement.FriendsOverviewSelectedFriendGame
            : HasFriendSelection
                ? GridOptionKeys.Achievement.FriendsOverviewSelectedFriend
                : HasGameSelection
                    ? GridOptionKeys.Achievement.FriendsOverviewSelectedGame
                    : GridOptionKeys.Achievement.FriendsOverviewRecent;

        public PlayniteAchievementsSettings Settings => _settings;

        public ObservableCollection<string> OwnershipFilterOptions { get; }
        public ObservableCollection<string> FriendProviderFilterOptions { get; }

        public ObservableCollection<ProviderFilterGroup> GamePlatformFilterGroups
        {
            get => _gamePlatformFilterGroups;
            private set => SetValue(ref _gamePlatformFilterGroups, value ?? new ObservableCollection<ProviderFilterGroup>());
        }
        public ObservableCollection<RefreshMode> FriendRefreshModes { get; }
        public ObservableCollection<RefreshMode> RefreshModes => FriendRefreshModes;

        public ICommand RefreshCommand { get; }
        public ICommand RefreshRecentCommand { get; }
        public ICommand RefreshFullCommand { get; }
        public ICommand RefreshSharedCommand { get; }
        public ICommand RefreshInstalledCommand { get; }
        public ICommand RefreshFriendSelectedGameCommand { get; }
        public ICommand OpenGameInLibraryCommand { get; }
        public ICommand RefreshOrCancelCommand { get; }
        public ICommand ClearSelectionCommand { get; }
        public ICommand ClearFriendSelectionCommand { get; }
        public ICommand ClearGameSelectionCommand { get; }

        public FriendSummaryItem SelectedFriend
        {
            get => _selectedFriend;
            set
            {
                if (value?.IsPlaceholder == true)
                {
                    // A skeleton row is not a friend.
                    OnPropertyChanged(nameof(SelectedFriend));
                    return;
                }

                if (SetValueAndReturn(ref _selectedFriend, value))
                {
                    NotifySelectionStateChanged();
                    if (!_isApplyingFilters)
                    {
                        ApplyFilters();
                    }
                }
            }
        }

        public FriendGameSummaryItem SelectedGame
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

                if (SetValueAndReturn(ref _selectedGame, value))
                {
                    NotifySelectionStateChanged();
                    if (!_isApplyingFilters)
                    {
                        ApplyFilters();
                    }
                }
            }
        }

        public bool HasFriendSelection => SelectedFriend != null;
        public bool HasGameSelection => SelectedGame != null;
        public bool HasAnySelection => SelectedFriend != null || SelectedGame != null;
        public bool HasFriendGameSelection => SelectedFriend != null && SelectedGame != null;

        // Friends other than the selected one, those with data for the selected game first when
        // there is one. The controller caches this, so it is walked once per selection or reload.
        private IReadOnlyList<FriendSummaryItem> GetCompareFriendOptions()
        {
            return FriendSurfaceControlBars.GetCompareFriendOptions(
                _current.Friends,
                SelectedFriend,
                HasCompareGameData);
        }

        // With no game selected the rows span every game, so every friend counts.
        private bool HasCompareGameData(FriendSummaryItem friend)
        {
            return SelectedGame == null || HasFriendGamePairData(friend, SelectedGame);
        }

        public string AchievementColumnSettingsKey
        {
            get
            {
                if (HasFriendGameSelection)
                {
                    return "FriendsOverviewSelectedFriendGameAchievements";
                }

                if (HasFriendSelection)
                {
                    return "FriendsOverviewSelectedFriendAchievements";
                }

                if (HasGameSelection)
                {
                    return "FriendsOverviewSelectedGameAchievements";
                }

                return "FriendsOverviewRecentAchievements";
            }
        }

        // Category the achievement grid is currently drilled into (null when not drilled), pushed
        // up from AchievementDataGridControl so a breadcrumb segment can follow the section title.
        private string _selectedCategoryName;
        public string SelectedCategoryName
        {
            get => _selectedCategoryName;
            set
            {
                if (SetValueAndReturn(ref _selectedCategoryName, value))
                {
                    OnPropertyChanged(nameof(IsCategorySelected));
                }
            }
        }

        public bool IsCategorySelected => !string.IsNullOrEmpty(SelectedCategoryName);

        public string FriendSearchText
        {
            get => _friendSearchText;
            set
            {
                if (SetValueAndReturn(ref _friendSearchText, value))
                {
                    ApplyFilters();
                }
            }
        }

        public string GameSearchText
        {
            get => _gameSearchText;
            set
            {
                if (SetValueAndReturn(ref _gameSearchText, value))
                {
                    ApplyFilters();
                }
            }
        }

        public string AchievementSearchText
        {
            get => _achievementSearchText;
            set
            {
                if (SetValueAndReturn(ref _achievementSearchText, value))
                {
                    // The rows are read again once typing pauses.
                    var version = ++_searchVersion;
                    _scheduler.PostAfter(SearchDelay, () =>
                    {
                        if (version == _searchVersion)
                        {
                            UpdateAchievementQueries();
                        }
                    });
                }
            }
        }

        public string SelectedRefreshMode
        {
            get => _selectedRefreshMode;
            set
            {
                if (SetValueAndReturn(ref _selectedRefreshMode, value))
                {
                    OnPropertyChanged(nameof(RefreshModeSelectionText));
                    OnPropertyChanged(nameof(RefreshActionButtonText));
                    OnPropertyChanged(nameof(RefreshOrCancelButtonText));
                    OnPropertyChanged(nameof(RefreshOrCancelButtonGlyph));
                }
            }
        }

        public string RefreshModeSelectionText => FriendRefreshModes?
            .FirstOrDefault(mode => string.Equals(mode?.Key, SelectedRefreshMode, StringComparison.Ordinal))?
            .ShortDisplayName
            ?? ResourceProvider.GetString("LOCPlayAch_Button_Refresh")
            ?? "Refresh";

        public string RefreshActionButtonText => string.Equals(
            SelectedRefreshMode,
            RefreshModeType.FriendsCustom.GetKey(),
            StringComparison.Ordinal)
            ? ResourceProvider.GetString("LOCPlayAch_Common_Configure")
            : ResourceProvider.GetString("LOCPlayAch_Button_Refresh");

        public string RefreshOrCancelButtonText => IsRefreshing
            ? ResourceProvider.GetString("LOCPlayAch_Common_Cancel")
            : RefreshActionButtonText;

        public string RefreshOrCancelButtonGlyph => IsRefreshing
            ? ""
            : string.Equals(SelectedRefreshMode, RefreshModeType.FriendsCustom.GetKey(), StringComparison.Ordinal)
                ? ""
                : "";

        public bool IsRefreshing
        {
            get => _isRefreshing;
            internal set
            {
                if (SetValueAndReturn(ref _isRefreshing, value))
                {
                    RaiseRefreshCanExecuteChanged();
                    OnPropertyChanged(nameof(RefreshOrCancelButtonText));
                    OnPropertyChanged(nameof(RefreshOrCancelButtonGlyph));
                    OnPropertyChanged(nameof(ShowProgress));
                }
            }
        }

        public double ProgressPercent => _progressTracker?.ProgressPercent ?? 0;

        public string ProgressMessage => _progressTracker?.ProgressMessage;

        public bool ShowProgress => _progressTracker?.ShowProgress ?? false;

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
                case nameof(RefreshHeaderProgressTracker.ShowProgress):
                    OnPropertyChanged(nameof(ShowProgress));
                    break;
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetValueAndReturn(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatusText));
                }
            }
        }

        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        public bool HasData => _current.HasData || (_achievements != null && !_achievements.IsPending && _achievements.Count > 0);
        public bool IsProviderDisabled => false;

        // Single selection shows the localized provider name (options carry raw provider keys so
        // merged-friend membership checks stay key-based).
        public string SelectedFriendProviderFilterText =>
            _selectedFriendProviderFilters.Count == 1
                ? PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName(
                    _selectedFriendProviderFilters.First())
                : GetSelectedFilterText(
                    _selectedFriendProviderFilters,
                    ResourceProvider.GetString("LOCPlayAch_Common_Platform"));

        public string SelectedGamePlatformFilterText => OverviewGameSummaryFilters.BuildProviderFilterText(
            GamePlatformFilterGroups,
            ResourceProvider.GetString("LOCPlayAch_Common_Platform"));

        public string SelectedOwnershipFilterText => GetSelectedFilterText(
            _selectedOwnershipFilters,
            ResourceProvider.GetString("LOCPlayAch_Filter_OwnershipSelectorPlaceholder"));

        private static string OwnedFilterLabel => ResourceProvider.GetString("LOCPlayAch_Column_Owned");

        private static string UnownedFilterLabel => ResourceProvider.GetString("LOCPlayAch_Filter_Unowned");

        // Each side grid's title names the filter imposed by the other grid's selection; the
        // ✕ that follows it removes that filter (friends grid ✕ clears the game, games grid ✕
        // clears the friend).
        public string FriendSectionTitle => SelectedGame != null
            ? SelectedGame.GameName
            : ResourceProvider.GetString("LOCPlayAch_FriendsOverview_FriendSummaries");

        public string GameSectionTitle => SelectedFriend != null
            ? SelectedFriend.DisplayName
            : ResourceProvider.GetString("LOCPlayAch_Overview_GameSummaries");

        public string AchievementSectionTitle
        {
            get
            {
                if (SelectedFriend != null && SelectedGame != null)
                {
                    return string.Format(
                        ResourceProvider.GetString("LOCPlayAch_FriendsOverview_SelectedFriendGameAchievements"),
                        SelectedFriend.DisplayName,
                        SelectedGame.GameName);
                }

                if (SelectedFriend != null)
                {
                    return SelectedFriend.DisplayName;
                }

                if (SelectedGame != null)
                {
                    return string.Format(
                        ResourceProvider.GetString("LOCPlayAch_FriendsOverview_SelectedFriendAchievements"),
                        SelectedGame.GameName);
                }

                return ResourceProvider.GetString("LOCPlayAch_Achievements");
            }
        }

        public string AchievementCountText
        {
            get
            {
                if (!HasFriendGameSelection)
                {
                    return string.Empty;
                }

                var game = GetSelectedFriendGameForHeader();
                var unlocked = Math.Max(0, game?.UniqueFriendUnlockedAchievementsCount ?? 0);
                var total = Math.Max(0, game?.TotalAchievements ?? 0);
                var format = GetResourceFormatOrFallback(
                    "LOCPlayAch_FriendsOverview_SelectedGameAchievementCount",
                    "({0}/{1} {2})",
                    "{0}");

                return string.Format(
                    format,
                    unlocked.ToString("N0", FormattingCulture.Current),
                    total.ToString("N0", FormattingCulture.Current),
                    ResourceProvider.GetString("LOCPlayAch_Achievements"));
            }
        }

        private GridControlBarViewModel CreateFriendSummariesControlBar()
        {
            return FriendSurfaceControlBars.CreateFriendSummariesControlBar(
                this,
                new GridMultiSelectFilter(
                    this,
                    nameof(SelectedFriendProviderFilterText),
                    () => SelectedFriendProviderFilterText,
                    () => FriendProviderFilterOptions,
                    IsFriendProviderFilterSelected,
                    SetFriendProviderFilterSelected,
                    PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName)
                {
                    Id = PlayniteAchievements.Models.ShowcaseControlBarItems.Platform,
                    LabelKey = "LOCPlayAch_Common_Platform",
                    Width = 118
                });
        }

        private GridControlBarViewModel CreateGameSummariesControlBar()
        {
            var controlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(GameSearchText),
                    () => GameSearchText,
                    value => GameSearchText = value,
                    GridControlBarText.Get("LOCPlayAch_Filter_Games", "Search Games"),
                    ClearGameSearch)
                {
                    Id = PlayniteAchievements.Models.ShowcaseControlBarItems.Search,
                    LabelKey = "LOCPlayAch_Filter_Games"
                }
            };
            controlBar.Items.Add(new GridProviderPlatformFilter(
                this,
                nameof(SelectedGamePlatformFilterText),
                () => SelectedGamePlatformFilterText,
                () => GamePlatformFilterGroups,
                CollapseUnselectedGamePlatformFilters)
            {
                Id = PlayniteAchievements.Models.ShowcaseControlBarItems.Platform,
                LabelKey = "LOCPlayAch_Common_Platform",
                Width = 170
            });
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedOwnershipFilterText),
                () => SelectedOwnershipFilterText,
                () => OwnershipFilterOptions,
                IsOwnershipFilterSelected,
                SetOwnershipFilterSelected)
            {
                Id = PlayniteAchievements.Models.ShowcaseControlBarItems.Ownership,
                LabelKey = "LOCPlayAch_Filter_OwnershipSelectorPlaceholder",
                Width = 118
            });
            return controlBar;
        }

        /// <summary>
        /// Starts the window's reads on the first call; reruns every read on later calls, for
        /// writes that do not reach the change feed (settings the rows read).
        /// </summary>
        public Task LoadAsync()
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_lists == null)
            {
                Start();
            }
            else
            {
                Reload();
            }

            return Task.CompletedTask;
        }

        private void Start()
        {
            if (_reads == null)
            {
                FilteredFriends.Clear();
                FilteredGames.Clear();
                _achievementPlaceholders = new List<FriendAchievementDisplayItem>();
                OnPropertyChanged(nameof(AchievementRows));
                StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsOverview_NoData");
                return;
            }

            UpdateRowDecoration();
            _lists = new LiveQuery<FriendsOverviewLists>(
                _reads.Feed,
                AchievementQuery.AllFriends,
                LoadLists,
                _scheduler,
                ReadWindow,
                ex => OnReadFailed(ex, "Failed to load friends overview lists."),
                gate: _gate);
            _lists.PropertyChanged += OnListsChanged;
        }

        /// <summary>
        /// Whether the view is shown. While it is not, a data change only marks its reads stale,
        /// and showing it again reads each stale one once. UI thread.
        /// </summary>
        public void SetActive(bool active) => _gate.IsOpen = active;

        // Every read again, with the same queries.
        private void Reload()
        {
            _readFriendSettingsKey = FriendSettingsKey();
            UpdateRowDecoration();
            _lists?.SetQuery(_lists.Query);
            _favoriteGames?.SetQuery(_favoriteGames.Query);
            _barOptions?.SetQuery(_barOptions.Query);
            _pairRows?.SetQuery(_pairRows.Query);
            _achievements?.SetQuery(_achievements.Query);
        }

        private FriendsOverviewLists LoadLists(AchievementQuery query)
        {
            using (PerfScope.Start(_logger, "FriendsOverview.LoadLists", thresholdMs: 25))
            {
                return FriendsOverviewLists.Load(_reads, _settings?.Persisted);
            }
        }

        private TimeSpan ReadWindow() => IsRefreshing ? RefreshingWindow : LiveQuery<object>.DefaultWindow;

        private void OnListsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed || e.PropertyName != nameof(LiveQuery<FriendsOverviewLists>.Result))
            {
                return;
            }

            _current = _lists.Result ?? FriendsOverviewLists.Empty;
            using (PerfScope.Start(_logger, "FriendsOverview.ApplyLists", thresholdMs: 15))
            {
                _friendSearchIndex.Rebuild(_current.Friends);
                _gameSearchIndex.Rebuild(_current.Games);
                UpdateRowDecoration();
                UpdateFilterOptions();
                ApplyFilters(preserveSelections: true);
            }

            StatusText = HasData ? null : ResourceProvider.GetString("LOCPlayAch_FriendsOverview_NoData");
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(IsProviderDisabled));
        }

        private void OnReadFailed(Exception ex, string message)
        {
            _logger?.Error(ex, message);
            _scheduler.Post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (!HasData)
                {
                    StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsOverview_LoadFailed");
                }
            });
        }

        public void ClearFriendSearch()
        {
            FriendSearchText = string.Empty;
        }

        public void ClearGameSearch()
        {
            GameSearchText = string.Empty;
        }

        public void ClearAchievementSearch()
        {
            AchievementSearchText = string.Empty;
        }

        public bool IsFriendProviderFilterSelected(string value)
        {
            return IsFilterSelected(_selectedFriendProviderFilters, value);
        }

        public void SetFriendProviderFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedFriendProviderFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedFriendProviderFilterText));
            ApplyFilters();
        }

        // Restricts the friends grid to favorites and, with no friend selected, scopes the games
        // and achievements grids to those favorites too. A selected friend already scopes both.
        public bool FavoritesOnly
        {
            get => _favoritesOnly;
            set
            {
                if (SetValueAndReturn(ref _favoritesOnly, value))
                {
                    ApplyFilters();
                }
            }
        }

        public bool IsOwnershipFilterSelected(string value)
        {
            return IsFilterSelected(_selectedOwnershipFilters, value);
        }

        public void SetOwnershipFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedOwnershipFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedOwnershipFilterText));
            ApplyFilters();
        }

        /// <summary>
        /// Invoked by a platform group whenever its selection changes. Refreshes the box text
        /// immediately and defers the grid filter to avoid interfering with the click that
        /// triggered it, matching the Overview's platform filter.
        /// </summary>
        private void OnGamePlatformFilterSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedGamePlatformFilterText));
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke(
                    new Action(() => ApplyFilters()),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            else
            {
                ApplyFilters();
            }
        }

        /// <summary>
        /// Collapses platform sections that have no platform selected. Called when the dropdown
        /// closes so reopening it shows only the in-use sections expanded.
        /// </summary>
        public void CollapseUnselectedGamePlatformFilters()
        {
            foreach (var group in GamePlatformFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
            {
                if (!group.HasAnySelected)
                {
                    group.IsExpanded = false;
                }
            }
        }

        public void ClearSelection()
        {
            var changed = false;
            if (_selectedFriend != null)
            {
                _selectedFriend = null;
                OnPropertyChanged(nameof(SelectedFriend));
                changed = true;
            }

            if (_selectedGame != null)
            {
                _selectedGame = null;
                OnPropertyChanged(nameof(SelectedGame));
                changed = true;
            }

            if (changed)
            {
                NotifySelectionStateChanged();
                ApplyFilters();
            }
        }

        public void ClearFriendSelection()
        {
            if (_selectedFriend == null)
            {
                return;
            }

            _selectedFriend = null;
            OnPropertyChanged(nameof(SelectedFriend));
            NotifySelectionStateChanged();
            ApplyFilters();
        }

        public void ClearGameSelection()
        {
            if (_selectedGame == null)
            {
                return;
            }

            _selectedGame = null;
            OnPropertyChanged(nameof(SelectedGame));
            NotifySelectionStateChanged();
            ApplyFilters();
        }

        private bool CanRefresh()
        {
            return !IsRefreshing;
        }

        public void CancelRefresh()
        {
            _refreshRuntime?.CancelCurrentRebuild();
        }

        private bool CanExecuteRefreshOrCancel()
        {
            return IsRefreshing || CanRefresh();
        }

        private void ExecuteRefreshOrCancel(object parameter)
        {
            if (IsRefreshing)
            {
                CancelRefresh();
                return;
            }

            if (CanRefresh())
            {
                _ = RefreshSelectedModeAsync();
            }
        }

        private async Task RefreshSelectedModeAsync()
        {
            var selected = FriendRefreshModes.FirstOrDefault(mode =>
                string.Equals(mode?.Key, SelectedRefreshMode, StringComparison.Ordinal));
            if (selected?.Type == RefreshModeType.FriendsSelectedGame)
            {
                await ExecuteSelectedFriendTargetRefreshAsync(null).ConfigureAwait(true);
                return;
            }

            if (selected?.Type == RefreshModeType.FriendsCustom)
            {
                if (_showCustomRefreshDialog == null)
                {
                    StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsOverview_NotAvailable");
                    return;
                }

                var customOptions = _showCustomRefreshDialog(
                    SelectedGame?.PlayniteGameId,
                    SelectedGame?.GameName);
                if (customOptions == null)
                {
                    return;
                }

                await ExecuteFriendRefreshRequestAsync(
                    new RefreshRequest
                    {
                        Mode = RefreshModeType.FriendsCustom,
                        Options = RefreshOptions.FromFriend(customOptions)
                    },
                    "Custom friends refresh failed.").ConfigureAwait(true);
                return;
            }

            await RefreshFriendsAsync(selected?.Type ?? RefreshModeType.FriendsRecent).ConfigureAwait(true);
        }

        private async Task RefreshFriendsAsync(RefreshModeType mode)
        {
            await ExecuteFriendRefreshRequestAsync(
                new RefreshRequest { Mode = mode },
                "Friend refresh failed.").ConfigureAwait(true);
        }

        // The refresh's writes reach the grids through the change feed.
        private async Task ExecuteFriendRefreshRequestAsync(RefreshRequest request, string errorLogMessage)
        {
            if (_refreshCoordinator == null)
            {
                StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsOverview_NotAvailable");
                return;
            }

            try
            {
                IsRefreshing = true;
                StatusText = null;
                _progressTracker?.NotifyRefreshStarting();
                await _refreshCoordinator.ExecuteAsync(
                    request,
                    new RefreshExecutionPolicy
                    {
                        ValidateAuthentication = true,
                        UseProgressWindow = false,
                        SwallowExceptions = false,
                        ErrorLogMessage = errorLogMessage ?? "Friend refresh failed."
                    }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to refresh friends overview.");
                StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsOverview_RefreshFailed");
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        private async Task ExecuteSelectedFriendTargetRefreshAsync(object parameter)
        {
            if (!CanRefresh())
            {
                return;
            }

            if (TryBuildSelectedFriendRefreshRequest(parameter, SelectedFriend, SelectedGame, out var request))
            {
                await ExecuteFriendRefreshRequestAsync(
                    request,
                    "Friend selected refresh failed.").ConfigureAwait(true);
                return;
            }

            StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsOverview_SelectTargetForRefresh");
        }

        private bool CanRefreshSelectedFriendTarget(object parameter)
        {
            return CanRefresh() &&
                   TryBuildSelectedFriendRefreshRequest(parameter, SelectedFriend, SelectedGame, out _);
        }

        internal static bool TryBuildSelectedFriendRefreshRequest(
            object parameter,
            FriendSummaryItem selectedFriend,
            FriendGameSummaryItem selectedGame,
            out RefreshRequest request)
        {
            request = null;

            var parameterIsFriend = TryGetFriendTarget(parameter, out var parameterFriend);
            var parameterIsGame = TryGetGameTarget(parameter, out var parameterGame);

            var friend = parameterIsFriend
                ? parameterFriend
                : selectedFriend;
            var game = parameterIsFriend
                ? null
                : (parameterIsGame ? parameterGame : selectedGame);

            if (friend != null && game == null)
            {
                var friendAccounts = GetRefreshAccounts(friend).ToList();
                if (friendAccounts.Count == 0)
                {
                    return false;
                }

                request = new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = friendAccounts
                            .Select(account => account.ProviderKey)
                            .Where(provider => !string.IsNullOrWhiteSpace(provider))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        Scope = FriendRefreshScope.Full,
                        FriendAccounts = friendAccounts.ToArray(),
                        FriendExternalUserIds = friendAccounts
                            .Select(account => account.ExternalUserId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    })
                };
                return true;
            }

            if (friend != null && game != null)
            {
                return TryBuildSelectedFriendGameRefreshRequest(friend, game, out request);
            }

            if (game != null)
            {
                return TryBuildSelectedGameRefreshRequest(game, out request);
            }

            return false;
        }

        private static bool TryBuildSelectedFriendGameRefreshRequest(
            FriendSummaryItem friend,
            object game,
            out RefreshRequest request)
        {
            request = null;
            if (!TryGetFriendTarget(friend, out friend))
            {
                return false;
            }

            if (TryGetPlayniteGameId(game, out var gameId))
            {
                var friendAccounts = GetRefreshAccounts(friend).ToList();
                if (friendAccounts.Count == 0)
                {
                    return false;
                }

                request = new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = friendAccounts
                            .Select(account => account.ProviderKey)
                            .Where(provider => !string.IsNullOrWhiteSpace(provider))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        Scope = FriendRefreshScope.SelectedGame,
                        PlayniteGameIds = new[] { gameId },
                        FriendAccounts = friendAccounts.ToArray(),
                        FriendExternalUserIds = friendAccounts
                            .Select(account => account.ExternalUserId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        ForceDefinitionRefresh = true
                    })
                };
                return true;
            }

            if (TryGetProviderAppTarget(game, out var providerKey, out var appId, out var providerGameKey))
            {
                var friendAccounts = GetRefreshAccounts(friend)
                    .Where(account => string.Equals(account.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (friendAccounts.Count == 0)
                {
                    return false;
                }

                request = new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = new[] { providerKey },
                        Scope = FriendRefreshScope.SelectedGame,
                        ProviderAppIds = appId > 0 ? new[] { appId } : null,
                        ProviderGameKeys = !string.IsNullOrWhiteSpace(providerGameKey) ? new[] { providerGameKey } : null,
                        FriendAccounts = friendAccounts.ToArray(),
                        FriendExternalUserIds = friendAccounts
                            .Select(account => account.ExternalUserId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        ForceDefinitionRefresh = true
                    })
                };
                return true;
            }

            return false;
        }

        private static bool TryBuildSelectedGameRefreshRequest(object game, out RefreshRequest request)
        {
            request = null;
            if (TryGetPlayniteGameId(game, out var gameId))
            {
                request = new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsSelectedGame,
                    SingleGameId = gameId
                };
                return true;
            }

            if (TryGetProviderAppTarget(game, out var providerKey, out var appId, out var providerGameKey))
            {
                request = new RefreshRequest
                {
                    Mode = RefreshModeType.FriendsCustom,
                    Options = RefreshOptions.FromFriend(new FriendCustomRefreshOptions
                    {
                        ProviderKeys = new[] { providerKey },
                        Scope = FriendRefreshScope.SelectedGame,
                        ProviderAppIds = appId > 0 ? new[] { appId } : null,
                        ProviderGameKeys = !string.IsNullOrWhiteSpace(providerGameKey) ? new[] { providerGameKey } : null,
                        ForceDefinitionRefresh = true
                    })
                };
                return true;
            }

            return false;
        }

        private static bool TryGetFriendTarget(object parameter, out FriendSummaryItem friend)
        {
            friend = parameter as FriendSummaryItem;
            return friend != null &&
                   (!string.IsNullOrWhiteSpace(friend.MergedFriendId) ||
                    (!string.IsNullOrWhiteSpace(friend.ProviderKey) &&
                     !string.IsNullOrWhiteSpace(friend.ExternalUserId)));
        }

        private static IEnumerable<FriendAccountRef> GetRefreshAccounts(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                yield break;
            }

            var accounts = friend.MemberAccounts?
                .Where(account =>
                    account != null &&
                    !string.IsNullOrWhiteSpace(account.ProviderKey) &&
                    !string.IsNullOrWhiteSpace(account.ExternalUserId))
                .ToList();
            if (accounts != null && accounts.Count > 0)
            {
                foreach (var account in accounts)
                {
                    yield return account;
                }

                yield break;
            }

            if (!string.IsNullOrWhiteSpace(friend.ProviderKey) &&
                !string.IsNullOrWhiteSpace(friend.ExternalUserId))
            {
                yield return FriendAccountRef.From(friend.ProviderKey, friend.ExternalUserId);
            }
        }

        private static bool TryGetGameTarget(object parameter, out object game)
        {
            game = null;
            if (parameter == null)
            {
                return false;
            }

            if (TryGetPlayniteGameId(parameter, out _) ||
                TryGetProviderAppTarget(parameter, out _, out _, out _))
            {
                game = parameter;
                return true;
            }

            return false;
        }

        // Progress display is handled by the shared tracker; here only the friend-scoped
        // refreshing state (button and command semantics) is kept.
        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            if (report?.Mode?.IsFriendRefreshMode() != true)
            {
                return;
            }

            IsRefreshing = _refreshRuntime?.IsRebuilding == true;
        }

        private void ApplyFilters(bool preserveSelections = false)
        {
            if (_isApplyingFilters)
            {
                return;
            }

            try
            {
                _isApplyingFilters = true;

                if (!preserveSelections &&
                    SelectedFriend != null &&
                    SelectedGame != null &&
                    !HasFriendGamePairData(SelectedFriend, SelectedGame))
                {
                    _selectedGame = null;
                    OnPropertyChanged(nameof(SelectedGame));
                    NotifySelectionStateChanged();
                }

                var friendQuery = SearchQuery.From(FriendSearchText);
                var gameQuery = SearchQuery.From(GameSearchText);

                var friends = _current.Friends
                    .Where(friend => friend != null)
                    .Where(friend => _friendSearchIndex.Matches(friend, friendQuery));
                if (_selectedFriendProviderFilters.Count > 0)
                {
                    // A merged friend passes when any member account's provider is selected.
                    friends = friends.Where(friend =>
                        GetProviderFilterKeys(friend).Any(_selectedFriendProviderFilters.Contains));
                }
                if (FavoritesOnly)
                {
                    friends = friends.Where(friend => friend?.IsFavorite == true);
                }
                if (SelectedGame != null)
                {
                    friends = friends.Where(friend => HasFriendGamePairData(friend, SelectedGame));
                }

                var friendList = friends.ToList();

                // Rescope the platform/ownership dropdowns when the game source changed (data
                // reload or a different selected friend); a no-op on plain filter passes so an
                // open dropdown menu keeps its live groups.
                UpdateGameFilterOptions(force: false);

                // With no friend selected the favorites view is its own aggregate: the games at
                // least one favorite has data for, with the friend columns computed over them.
                // Its read may still be on its way; the selection waits for it.
                var gamesPending = false;
                var gameSource = SelectedFriend != null
                    ? _current.GamesOf(SelectedFriend)
                    : FavoritesOnly
                        ? GetFavoriteGames(out gamesPending)
                        : _current.Games;

                // No unlock gating here: the games list is ownership-driven and the cache only holds
                // ownership rows that should display (owned games unconditionally; provider-only games
                // once the friend has confirmed unlocks).
                var games = gameSource
                    .Where(game => _gameSearchIndex.Matches(game, gameQuery));

                games = OverviewGameSummaryFilters.ApplyProviderPlatformFilter(games, GamePlatformFilterGroups);

                if (_selectedOwnershipFilters.Count > 0)
                {
                    var includeOwned = _selectedOwnershipFilters.Contains(OwnedFilterLabel);
                    var includeUnowned = _selectedOwnershipFilters.Contains(UnownedFilterLabel);
                    if (includeOwned != includeUnowned)
                    {
                        games = games.Where(game => includeOwned ? game.Owned : !game.Owned);
                    }
                }

                var gameList = games.ToList();

                var selectionChanged = false;
                if (SelectedFriend != null)
                {
                    var matchingFriend = friendList.FirstOrDefault(friend => IsSameFriend(friend, SelectedFriend));
                    if (matchingFriend != null && !ReferenceEquals(_selectedFriend, matchingFriend))
                    {
                        _selectedFriend = matchingFriend;
                        OnPropertyChanged(nameof(SelectedFriend));
                        NotifySelectionStateChanged();
                    }
                    else if (matchingFriend == null)
                    {
                        if (preserveSelections)
                        {
                            friendList.Insert(0, SelectedFriend);
                        }
                        else
                        {
                            _selectedFriend = null;
                            OnPropertyChanged(nameof(SelectedFriend));
                            NotifySelectionStateChanged();
                            selectionChanged = true;
                        }
                    }
                }

                if (SelectedGame != null)
                {
                    var matchingGame = gameList.FirstOrDefault(game => IsSameGame(game, SelectedGame));
                    if (matchingGame == null)
                    {
                        if (preserveSelections || gamesPending)
                        {
                            gameList.Insert(0, SelectedGame);
                        }
                        else
                        {
                            _selectedGame = null;
                            OnPropertyChanged(nameof(SelectedGame));
                            NotifySelectionStateChanged();
                            selectionChanged = true;
                        }
                    }
                    else if (!ReferenceEquals(_selectedGame, matchingGame))
                    {
                        _selectedGame = matchingGame;
                        OnPropertyChanged(nameof(SelectedGame));
                        NotifySelectionStateChanged();
                    }
                }

                if (selectionChanged)
                {
                    _isApplyingFilters = false;
                    ApplyFilters();
                    return;
                }

                _filteredFriendsList = friendList;
                _filteredGamesList = gameList;

                if (!string.IsNullOrEmpty(_friendSortPath))
                {
                    FriendSummarySortHelper.TrySortItems(
                        _filteredFriendsList,
                        _friendSortPath,
                        _friendSortDirection,
                        ref _friendSortPath,
                        ref _friendSortDirection);
                }
                else
                {
                    FriendSummarySortHelper.SortByConfiguredDefault(_filteredFriendsList, _settings?.Persisted);
                }

                if (!string.IsNullOrEmpty(_gameSortPath))
                {
                    GameSummariesSortHelper.TrySortItems(
                        _filteredGamesList,
                        _gameSortPath,
                        _gameSortDirection,
                        ref _gameSortPath,
                        ref _gameSortDirection);
                }
                else
                {
                    GameSummariesSortHelper.SortByConfiguredDefault(
                        _filteredGamesList,
                        _settings?.Persisted,
                        GameSummariesSortSurface.FriendsOverview);
                }

                SyncFriendsDisplay();
                SyncGamesDisplay();
                UpdateCompareScope();
                UpdateAchievementQueries();
                OnPropertyChanged(nameof(AchievementCountText));
                OnPropertyChanged(nameof(HasData));
            }
            finally
            {
                _isApplyingFilters = false;
            }
        }

        // The favorites' games, from their read; empty with pending set while it runs.
        private IReadOnlyList<FriendGameSummaryItem> GetFavoriteGames(out bool pending)
        {
            var totals = _favoriteGames?.Result;
            pending = totals == null;
            return totals == null
                ? Array.Empty<FriendGameSummaryItem>()
                : _current.GamesOf(_current.FavoriteAccounts(), totals);
        }

        /// <summary>
        /// The friends and game the achievements grid shows, and whether its rows are unlocked
        /// only: every friend's recent unlocks, a friend's or a game's unlocks, or a friend and
        /// game's every achievement. Favorites stand in for every friend.
        /// </summary>
        internal AchievementQuery BuildScopeQuery()
        {
            var query = SelectedFriend == null && FavoritesOnly
                ? AchievementQuery.AllFriends.ForFriendAccounts(_current.FavoriteAccounts())
                : AchievementQuery.AllFriends;
            query = FriendsOverviewLists.Scope(query, SelectedFriend, SelectedGame);

            // Locked rows show only for one friend and one game; the recent feed lists the
            // unlocks that have a time.
            return HasFriendGameSelection
                ? query
                : HasAnySelection
                    ? query.WhereUnlocked(true)
                    : query.WhereUnlocked(true).WhereUnlockTimeKnown();
        }

        /// <summary>The grid's query: the scope, the bar's filters, search, sort and compare.</summary>
        internal AchievementQuery BuildAchievementQuery(AchievementQuery scope)
        {
            var query = _achievementsBar.ApplyTo(scope).WithFriendNames(_current.FriendNames);
            if (SearchQuery.From(AchievementSearchText).HasValue)
            {
                query = query.WhereSearch(AchievementSearchText);
            }

            var compareFriend = _friendCompare.IsSelfSelected ? null : _friendCompare.CompareFriend;
            if (compareFriend != null)
            {
                query = query.CompareWith(FriendsOverviewLists.AccountsOf(compareFriend));
            }

            return ApplyAchievementSort(query, scope);
        }

        // The column sort, else the configured default, else the scope's own order: newest
        // unlocks first for the recent feed, friend, game and definition order otherwise.
        private AchievementQuery ApplyAchievementSort(AchievementQuery query, AchievementQuery scope)
        {
            if (!string.IsNullOrEmpty(_achievementSortPath) && AchievementQuery.SortOfColumn(_achievementSortPath) is AchievementSort column)
            {
                return query.OrderBy(column, _achievementSortDirection == ListSortDirection.Descending);
            }

            var configured = AchievementSortHelper.GetConfiguredDefaultSort(
                _settings?.Persisted,
                AchievementSortSurface.FriendsOverviewRecentAchievements);
            if (!configured.PreservesSourceOrder && AchievementQuery.SortOfColumn(configured.SortMemberPath) is AchievementSort configuredSort)
            {
                return query.OrderBy(configuredSort, configured.Direction == ListSortDirection.Descending);
            }

            return scope.RequireUnlockTime
                ? query.OrderBy(AchievementSort.UnlockTime, descending: true)
                : query.OrderBy(AchievementSort.Display);
        }

        /// <summary>
        /// Points every read at the current selection, filters, search, sort and compare pick;
        /// a read whose query is unchanged is left alone. Nothing reads before the lists land:
        /// the queries name the friends and favorites the lists bring.
        /// </summary>
        private void UpdateAchievementQueries()
        {
            if (_disposed || _lists?.Result == null)
            {
                return;
            }

            var scope = BuildScopeQuery();
            UpdateFavoriteGamesRead();
            UpdateBarOptionsRead(scope);
            UpdatePairRowsRead(scope);
            UpdateRowDecoration();

            var query = BuildAchievementQuery(scope);
            var key = KeyOf(query) + "|" + _rowDecoration.Key;
            var scopeKey = KeyOf(scope);
            var cap = PersistedSettings.NormalizeGridMaxRows(_settings?.Persisted?.FriendsOverviewAchievementsGridMaxRows);
            if (_achievements == null || cap != _achievementsCap)
            {
                _achievements?.Dispose();
                _achievementsCap = cap;
                _achievementsKey = key;
                _achievementsScopeKey = scopeKey;
                _achievements = new PagedRows<FriendAchievementDisplayItem>(
                    _reads.Feed,
                    query,
                    LoadKeys,
                    LoadRows,
                    () => new FriendAchievementDisplayItem { IsPlaceholder = true },
                    _scheduler,
                    cap ?? 0,
                    ReadWindow,
                    ex => OnReadFailed(ex, "Failed to load friend achievement rows."),
                    (kept, fresh) => kept.RefreshFrom(fresh),
                    PlaceholderRows.Count,
                    gate: _gate);
                OnPropertyChanged(nameof(DisplayedAchievements));
                OnPropertyChanged(nameof(AchievementRows));
                return;
            }

            if (!string.Equals(key, _achievementsKey, StringComparison.Ordinal))
            {
                // A new selection lists other rows from their top; a filter, search or sort keeps
                // the place the grid is at.
                var fromStart = !string.Equals(scopeKey, _achievementsScopeKey, StringComparison.Ordinal);
                _achievementsKey = key;
                _achievementsScopeKey = scopeKey;
                _achievements.SetQuery(query, fromStart);
            }
        }

        private IReadOnlyList<AchievementQuerySql.RowKey> LoadKeys(AchievementQuery query)
        {
            using (PerfScope.Start(_logger, "FriendsOverview.LoadKeys", thresholdMs: 25))
            {
                return _reads.SortedKeys(query);
            }
        }

        private IReadOnlyList<FriendAchievementDisplayItem> LoadRows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys)
        {
            using (PerfScope.Start(_logger, "FriendsOverview.LoadRows", thresholdMs: 25, context: "rows=" + keys.Count))
            {
                var rows = _reads.Rows(query, keys) ?? Array.Empty<FriendAchievementDisplayItem>();
                _rowDecoration.Apply(rows);
                return rows;
            }
        }

        // With no friend selected, the favorites' games read; dropped otherwise.
        private void UpdateFavoriteGamesRead()
        {
            if (SelectedFriend != null || !FavoritesOnly)
            {
                _favoriteGames?.Dispose();
                _favoriteGames = null;
                _favoriteGamesKey = null;
                return;
            }

            var query = AchievementQuery.AllFriends
                .ForFriendAccounts(_current.FavoriteAccounts())
                .WhereUnlocked(true)
                .CountDistinctAchievements();
            var key = KeyOf(query);
            if (_favoriteGames != null && string.Equals(key, _favoriteGamesKey, StringComparison.Ordinal))
            {
                return;
            }

            _favoriteGamesKey = key;
            if (_favoriteGames != null)
            {
                _favoriteGames.SetQuery(query);
                return;
            }

            _favoriteGames = new LiveQuery<IReadOnlyList<AchievementTotalsRecord>>(
                _reads.Feed,
                query,
                read => _reads.Totals(read, AchievementGroup.ProviderGame),
                _scheduler,
                ReadWindow,
                ex => OnReadFailed(ex, "Failed to load favorite friends' games."),
                gate: _gate);
            _favoriteGames.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LiveQuery<object>.Result) && !_disposed)
                {
                    ApplyFilters(preserveSelections: true);
                }
            };
        }

        // Type, category, unlock-state and trophy options are a game's vocabulary: offered while
        // a game is selected, from totals of the grid's scope.
        private void UpdateBarOptionsRead(AchievementQuery scope)
        {
            if (SelectedGame == null)
            {
                _barOptions?.Dispose();
                _barOptions = null;
                _barOptionsKey = null;
                if (_barOptionsOffered)
                {
                    _barOptionsOffered = false;
                    _achievementsBar.UpdateOptions((AchievementBarOptions)null);
                }

                return;
            }

            _barOptionsOffered = true;

            var key = KeyOf(scope);
            if (_barOptions != null && string.Equals(key, _barOptionsKey, StringComparison.Ordinal))
            {
                return;
            }

            _barOptionsKey = key;
            if (_barOptions != null)
            {
                _barOptions.SetQuery(scope);
                return;
            }

            _barOptions = new LiveQuery<AchievementBarOptions>(
                _reads.Feed,
                scope,
                LoadBarOptions,
                _scheduler,
                ReadWindow,
                ex => OnReadFailed(ex, "Failed to load friend achievement filter options."),
                gate: _gate);
            _barOptions.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LiveQuery<object>.Result) && !_disposed)
                {
                    _achievementsBar.UpdateOptions(_barOptions?.Result);
                    UpdateAchievementQueries();
                }
            };
        }

        private AchievementBarOptions LoadBarOptions(AchievementQuery scope)
        {
            var categories = _reads.Totals(scope, AchievementGroup.Category);
            var tags = _reads.Totals(scope, AchievementGroup.Tag);
            long count = 0, unlocked = 0, hiddenLocked = 0, trophies = 0;
            var orders = new Dictionary<string, long>(StringComparer.Ordinal);
            var firstRows = new Dictionary<string, string>(StringComparer.Ordinal);
            var labels = new List<string>();
            foreach (var total in categories)
            {
                count += total.Count;
                unlocked += total.UnlockedCount;
                hiddenLocked += total.HiddenLockedCount;
                trophies += total.PlatinumCount + total.GoldCount + total.SilverCount + total.BronzeCount;
                var label = AchievementCategoryHelper.NormalizeCategoryOrDefault(total.Key);
                if (!orders.TryGetValue(label, out var order))
                {
                    labels.Add(label);
                    orders[label] = total.MinCategoryOrder ?? long.MaxValue;
                    firstRows[label] = total.FirstDisplayOrder ?? string.Empty;
                    continue;
                }

                orders[label] = Math.Min(order, total.MinCategoryOrder ?? long.MaxValue);
                if (string.CompareOrdinal(total.FirstDisplayOrder ?? string.Empty, firstRows[label]) < 0)
                {
                    firstRows[label] = total.FirstDisplayOrder;
                }
            }

            return new AchievementBarOptions
            {
                HasUnlocked = unlocked > 0,
                HasLocked = count > unlocked,
                HasHiddenLocked = hiddenLocked > 0,
                HasTrophies = trophies > 0,
                GameId = SelectedGameIdOf(scope),
                Tags = tags.Where(total => total.Key != null).Select(total => total.Key).ToList(),
                // The custom category order first, then the rest in definition order.
                CategoryLabels = labels
                    .OrderBy(label => orders[label])
                    .ThenBy(label => firstRows[label], StringComparer.Ordinal)
                    .ToList()
            };
        }

        private static Guid? SelectedGameIdOf(AchievementQuery scope) =>
            scope.GameIds?.Count == 1 ? scope.GameIds[0] : (Guid?)null;

        // The friend and game's every row, unfiltered, in definition order, for the category
        // rollups; only while both are selected.
        private void UpdatePairRowsRead(AchievementQuery scope)
        {
            if (!HasFriendGameSelection)
            {
                _pairRows?.Dispose();
                _pairRows = null;
                _pairRowsKey = null;
                CollectionHelper.Replace(SelectedFriendGameAllAchievements, Array.Empty<FriendAchievementDisplayItem>());
                return;
            }

            var query = scope.OrderBy(AchievementSort.Display);
            var key = KeyOf(query);
            if (_pairRows != null && string.Equals(key, _pairRowsKey, StringComparison.Ordinal))
            {
                return;
            }

            _pairRowsKey = key;
            if (_pairRows != null)
            {
                _pairRows.SetQuery(query);
                return;
            }

            _pairRows = new LiveQuery<IReadOnlyList<FriendAchievementDisplayItem>>(
                _reads.Feed,
                query,
                read =>
                {
                    var rows = _reads.Rows(read) ?? Array.Empty<FriendAchievementDisplayItem>();
                    _rowDecoration.Lists.StampIdentity(rows, _rowDecoration.Settings);
                    return rows;
                },
                _scheduler,
                ReadWindow,
                ex => OnReadFailed(ex, "Failed to load friend pair rows."),
                gate: _gate);
            _pairRows.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LiveQuery<object>.Result) && !_disposed)
                {
                    CollectionHelper.Replace(
                        SelectedFriendGameAllAchievements,
                        _pairRows?.Result ?? Array.Empty<FriendAchievementDisplayItem>());
                }
            };
        }

        private static string KeyOf(AchievementQuery query) => AchievementQuerySql.KeyOf(query);

        // What loaded rows are given beyond their columns: the friend identity and the compare
        // side's names. Replaced, never changed, so a read off the UI thread sees one whole.
        private void UpdateRowDecoration()
        {
            var compareSelf = _friendCompare.IsSelfSelected;
            _rowDecoration = new RowDecoration(
                _current,
                _settings?.Persisted,
                compareSelf ? null : _friendCompare.CompareFriend,
                compareSelf ? _friendCompare : null,
                SelectedFriend);
        }

        private sealed class RowDecoration
        {
            private readonly FriendSummaryItem _compareFriend;
            private readonly FriendVsFriendCompareController _self;
            private readonly FriendSummaryItem _owner;

            public RowDecoration(
                FriendsOverviewLists lists,
                PersistedSettings settings,
                FriendSummaryItem compareFriend,
                FriendVsFriendCompareController self,
                FriendSummaryItem owner)
            {
                Lists = lists ?? FriendsOverviewLists.Empty;
                Settings = settings;
                _compareFriend = compareFriend;
                _self = self;
                _owner = owner;
                Key = (compareFriend != null ? "friend:" + FriendOverviewProjection.GetFriendScopeKey(compareFriend) + "|" + compareFriend.DisplayName : string.Empty) +
                      (self != null ? "self:" + self.IdentityVersion : string.Empty) +
                      (compareFriend != null || self != null ? "|owner:" + owner?.DisplayName : string.Empty);
            }

            public FriendsOverviewLists Lists { get; }

            public PersistedSettings Settings { get; }

            /// <summary>Which names and comparison the rows are given; a change reloads them.</summary>
            public string Key { get; }

            public void Apply(IReadOnlyList<FriendAchievementDisplayItem> rows)
            {
                Lists.StampIdentity(rows, Settings);
                foreach (var row in rows)
                {
                    if (_self != null)
                    {
                        // The self identity follows each row's provider; a game the user has no
                        // progress for reads N/A, not Locked.
                        row.ApplyComparison(
                            _self.SelfLabel(row.ProviderKey),
                            _self.SelfAvatarPath(row.ProviderKey),
                            row.SelfUnlockTimeUtc,
                            row.UnlockedBySelf,
                            _owner?.DisplayName ?? row.FriendName,
                            _owner?.AvatarPath ?? row.FriendAvatarPath,
                            row.SelfHasGameData);
                    }
                    else if (_compareFriend != null)
                    {
                        // A friend is not compared with themself: with no friend selected the
                        // compare friend's own rows are listed too, and stay as they are.
                        if (FriendOverviewProjection.IsSameFriend(row, _compareFriend))
                        {
                            row.ClearComparison();
                            continue;
                        }

                        row.ApplyComparison(
                            _compareFriend.DisplayName,
                            _compareFriend.AvatarPath,
                            row.ComparisonUnlockTimeUtc,
                            row.ComparisonUnlocked,
                            _owner?.DisplayName ?? row.FriendName,
                            _owner?.AvatarPath ?? row.FriendAvatarPath,
                            row.ComparisonApplicable);
                    }
                }
            }
        }

        public void SortDataGrid(DataGrid dataGrid, string sortMemberPath, ListSortDirection direction)
        {
            if (dataGrid == null || string.IsNullOrEmpty(sortMemberPath))
            {
                return;
            }

            var itemsSource = dataGrid.ItemsSource;
            if (itemsSource == FilteredFriends)
            {
                SortFriends(sortMemberPath, direction);
            }
            else if (itemsSource == FilteredGames)
            {
                SortGames(sortMemberPath, direction);
            }
        }

        private void SortFriends(string sortMemberPath, ListSortDirection direction)
        {
            if (!FriendSummarySortHelper.TrySortItems(
                    _filteredFriendsList,
                    sortMemberPath,
                    direction,
                    ref _friendSortPath,
                    ref _friendSortDirection))
            {
                return;
            }

            SyncFriendsDisplay();
        }

        private void SortGames(string sortMemberPath, ListSortDirection direction)
        {
            if (!GameSummariesSortHelper.TrySortItems(
                    _filteredGamesList,
                    sortMemberPath,
                    direction,
                    ref _gameSortPath,
                    ref _gameSortDirection))
            {
                return;
            }

            SyncGamesDisplay();
        }

        /// <summary>Sorts the achievements grid by a column; a column with no query order is ignored.</summary>
        public void SortAchievements(string sortMemberPath, ListSortDirection direction)
        {
            if (AchievementQuery.SortOfColumn(sortMemberPath) == null)
            {
                return;
            }

            _achievementSortPath = sortMemberPath;
            _achievementSortDirection = direction;
            UpdateAchievementQueries();
        }

        public void ApplyDefaultFriendSort()
        {
            _friendSortPath = null;
            ApplyFilters(preserveSelections: true);
        }

        public void ApplyDefaultGameSort()
        {
            _gameSortPath = null;
            ApplyFilters(preserveSelections: true);
        }

        public void ApplyDefaultAchievementSort()
        {
            _achievementSortPath = null;
            UpdateAchievementQueries();
        }

        // The skeleton rows stay until the lists land.
        private void SyncFriendsDisplay()
        {
            if (_lists?.Result == null)
            {
                return;
            }

            CollectionHelper.Replace(FilteredFriends, DisplayGridRowLimitHelper.Limit(
                _filteredFriendsList,
                _settings?.Persisted?.FriendsOverviewFriendSummariesGridMaxRows));
        }

        private void SyncGamesDisplay()
        {
            if (_lists?.Result == null)
            {
                return;
            }

            CollectionHelper.Replace(FilteredGames, DisplayGridRowLimitHelper.Limit(
                _filteredGamesList,
                _settings?.Persisted?.FriendsOverviewGameSummariesGridMaxRows));
        }

        private FriendGameSummaryItem GetSelectedFriendGameForHeader()
        {
            if (SelectedFriend == null || SelectedGame == null)
            {
                return SelectedGame;
            }

            return _current.GamesOf(SelectedFriend)
                .FirstOrDefault(game => IsSameGame(game, SelectedGame)) ??
                   SelectedGame;
        }

        private bool HasFriendGamePairData(FriendSummaryItem friend, FriendGameSummaryItem game)
        {
            return _current.HasPairData(friend, game);
        }

        private static bool IsSameFriend(FriendSummaryItem left, FriendSummaryItem right)
        {
            return FriendOverviewProjection.IsSameFriend(left, right);
        }

        private static bool IsSameGame(FriendGameSummaryItem left, FriendGameSummaryItem right)
        {
            return FriendOverviewProjection.IsSameGame(left, right);
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

        private static bool TryGetPlayniteGameId(object parameter, out Guid gameId)
        {
            switch (parameter)
            {
                case GameSummaryItem game when game.PlayniteGameId.HasValue && game.PlayniteGameId.Value != Guid.Empty:
                    gameId = game.PlayniteGameId.Value;
                    return true;
                case AchievementDisplayItem achievement when achievement.PlayniteGameId.HasValue && achievement.PlayniteGameId.Value != Guid.Empty:
                    gameId = achievement.PlayniteGameId.Value;
                    return true;
                case Guid id when id != Guid.Empty:
                    gameId = id;
                    return true;
                default:
                    gameId = Guid.Empty;
                    return false;
            }
        }

        private static bool TryGetProviderAppTarget(object parameter, out string providerKey, out int appId, out string providerGameKey)
        {
            switch (parameter)
            {
                case FriendGameSummaryItem game
                    when !game.PlayniteGameId.HasValue &&
                         !string.IsNullOrWhiteSpace(game.ProviderKey) &&
                         (game.AppId > 0 || !string.IsNullOrWhiteSpace(game.ProviderGameKey)):
                    providerKey = game.ProviderKey.Trim();
                    appId = game.AppId;
                    providerGameKey = game.ProviderGameKey?.Trim();
                    return true;
                case FriendAchievementDisplayItem achievement
                    when !achievement.PlayniteGameId.HasValue &&
                         !string.IsNullOrWhiteSpace(achievement.ProviderKey) &&
                         (achievement.AppId > 0 || !string.IsNullOrWhiteSpace(achievement.ProviderGameKey)):
                    providerKey = achievement.ProviderKey.Trim();
                    appId = achievement.AppId;
                    providerGameKey = achievement.ProviderGameKey?.Trim();
                    return true;
                default:
                    providerKey = null;
                    appId = 0;
                    providerGameKey = null;
                    return false;
            }
        }

        private void UpdateFilterOptions()
        {
            ReplaceOptions(
                FriendProviderFilterOptions,
                _current.Friends.SelectMany(GetProviderFilterKeys));
            PruneFilterSelections(_selectedFriendProviderFilters, FriendProviderFilterOptions);
            OnPropertyChanged(nameof(SelectedFriendProviderFilterText));
            FriendSummariesControlBar?.Refresh();

            UpdateGameFilterOptions(force: true);
        }

        // Platform and ownership options reflect the game grid's unfiltered source (the selected
        // friend's games, or all games). Rebuilt only when that source changes: forced on data
        // reloads, and detected via the selected-friend reference on filter passes. Not rebuilt on
        // plain filter passes because the platform dropdown's open context menu captures the group
        // collection at open time and would detach from a replacement.
        private void UpdateGameFilterOptions(bool force)
        {
            if (!force && _gameFilterOptionsBuilt && ReferenceEquals(_gameFilterOptionsFriend, SelectedFriend))
            {
                return;
            }

            _gameFilterOptionsBuilt = true;
            _gameFilterOptionsFriend = SelectedFriend;

            var source = SelectedFriend != null
                ? _current.GamesOf(SelectedFriend)
                : _current.Games;

            GamePlatformFilterGroups = ProviderFilterGroupBuilder.Rebuild(
                source,
                GamePlatformFilterGroups,
                OnGamePlatformFilterSelectionChanged);
            OnPropertyChanged(nameof(SelectedGamePlatformFilterText));

            var hasOwned = false;
            var hasUnowned = false;
            foreach (var game in source)
            {
                if (game == null)
                {
                    continue;
                }

                if (game.Owned)
                {
                    hasOwned = true;
                }
                else
                {
                    hasUnowned = true;
                }

                if (hasOwned && hasUnowned)
                {
                    break;
                }
            }

            // Offer the ownership filter only when the source mixes owned and unowned games; an
            // empty option list auto-hides the dropdown via GridMultiSelectFilter.HasAvailableAction.
            ReplaceOptions(
                OwnershipFilterOptions,
                hasOwned && hasUnowned
                    ? new[] { OwnedFilterLabel, UnownedFilterLabel }
                    : Enumerable.Empty<string>());
            PruneFilterSelections(_selectedOwnershipFilters, OwnershipFilterOptions);
            OnPropertyChanged(nameof(SelectedOwnershipFilterText));
            GameSummariesControlBar?.Refresh();
        }

        // Drops the compare selection when the selected friend or game changes and switches to
        // the pick remembered for the view the grid is now in. The comparison itself is part of
        // the grid's query.
        private void UpdateCompareScope()
        {
            var scopeChanged =
                (SelectedFriend == null
                    ? _compareScopeFriend != null
                    : !IsSameFriend(_compareScopeFriend, SelectedFriend)) ||
                (SelectedGame == null
                    ? _compareScopeGame != null
                    : !IsSameGame(_compareScopeGame, SelectedGame));
            _compareScopeFriend = SelectedFriend;
            _compareScopeGame = SelectedGame;
            if (scopeChanged)
            {
                _friendCompare.ClearSelection();
            }

            var surfaceKey = CompareSurfaceKey;
            if (_loadCompareSelection != null &&
                !string.Equals(_compareSurfaceKey, surfaceKey, StringComparison.Ordinal))
            {
                _compareSurfaceKey = surfaceKey;
                _friendCompare.ClearSelection();
                _friendCompare.PersistSelection(
                    _loadCompareSelection(surfaceKey),
                    state => _saveCompareSelection?.Invoke(surfaceKey, state));
            }

            _friendCompare.UpdateRows(null, null);
        }

        private static void ReplaceOptions(ObservableCollection<string> target, IEnumerable<string> values)
        {
            var next = (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            target.Clear();
            foreach (var value in next)
            {
                target.Add(value);
            }
        }

        private static bool IsFilterSelected(HashSet<string> selectedValues, string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   selectedValues != null &&
                   selectedValues.Contains(value);
        }

        private static bool SetFilterSelection(HashSet<string> selectedValues, string value, bool isSelected)
        {
            if (selectedValues == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return isSelected
                ? selectedValues.Add(value)
                : selectedValues.Remove(value);
        }

        private static void PruneFilterSelections(HashSet<string> selectedValues, IEnumerable<string> options)
        {
            if (selectedValues == null)
            {
                return;
            }

            var available = new HashSet<string>(options ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            selectedValues.RemoveWhere(value => !available.Contains(value));
        }

        private static string GetSelectedFilterText(
            HashSet<string> selectedValues,
            string placeholder)
        {
            var selectedCount = selectedValues?.Count ?? 0;
            if (selectedCount <= 0)
            {
                return placeholder;
            }

            if (selectedCount == 1)
            {
                return selectedValues.First();
            }

            var format = GetResourceFormatOrFallback("LOCPlayAch_Common_SelectedCountFormat", "{0:N0} selected", "{0");
            return string.Format(FormattingCulture.Current, format, selectedCount);
        }

        internal static string GetResourceFormatOrFallback(string resourceKey, string fallback, params string[] requiredPlaceholders)
        {
            var value = ResourceProvider.GetString(resourceKey);
            if (string.IsNullOrWhiteSpace(value) ||
                value.StartsWith("<!", StringComparison.Ordinal) ||
                requiredPlaceholders?.Any(placeholder => value.IndexOf(placeholder, StringComparison.Ordinal) < 0) == true)
            {
                return fallback;
            }

            return value;
        }

        private void NotifySelectionStateChanged()
        {
            OnPropertyChanged(nameof(HasFriendSelection));
            OnPropertyChanged(nameof(HasGameSelection));
            OnPropertyChanged(nameof(HasAnySelection));
            OnPropertyChanged(nameof(HasFriendGameSelection));
            OnPropertyChanged(nameof(AchievementColumnSettingsKey));
            OnPropertyChanged(nameof(FriendSectionTitle));
            OnPropertyChanged(nameof(GameSectionTitle));
            OnPropertyChanged(nameof(AchievementSectionTitle));
            OnPropertyChanged(nameof(AchievementCountText));
            // Both the option list and its availability follow the selection.
            _notifyingSelection = true;
            try
            {
                _friendCompare.Refresh();
            }
            finally
            {
                _notifyingSelection = false;
            }
        }

        private void RaiseRefreshCanExecuteChanged()
        {
            (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (RefreshRecentCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (RefreshFullCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (RefreshSharedCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (RefreshInstalledCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (RefreshFriendSelectedGameCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (RefreshOrCancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            if (string.IsNullOrWhiteSpace(propertyName) ||
                propertyName == nameof(PersistedSettings.ProviderColorOverrides))
            {
                foreach (var friend in _current.Friends)
                {
                    friend?.RefreshProviderAppearance();
                }

                foreach (var game in _current.Games)
                {
                    game?.RefreshProviderAppearance();
                }
            }

            // The rows read spoilers and name display when they are mapped.
            if (string.IsNullOrWhiteSpace(propertyName) ||
                propertyName == nameof(PersistedSettings.ShowFriendSpoilers) ||
                propertyName == nameof(PersistedSettings.FriendNameDisplayMode))
            {
                Reload();
            }

            if (string.IsNullOrWhiteSpace(propertyName) ||
                propertyName == nameof(PersistedSettings.IncludeUnownedFriendGames))
            {
                RebuildFriendRefreshModes();
            }

            if (string.IsNullOrWhiteSpace(propertyName) ||
                propertyName == nameof(PersistedSettings.FriendsOverviewFriendSummariesGridMaxRows) ||
                propertyName == nameof(PersistedSettings.FriendsOverviewGameSummariesGridMaxRows) ||
                propertyName == nameof(PersistedSettings.FriendsOverviewAchievementsGridMaxRows))
            {
                ApplyFilters();
            }

            if (string.IsNullOrWhiteSpace(_friendSortPath) &&
                (string.IsNullOrWhiteSpace(propertyName) ||
                 FriendSummarySortHelper.IsConfiguredDefaultSortPropertyName(propertyName)))
            {
                ApplyFilters();
                OnPropertyChanged(nameof(FriendSortPath));
                OnPropertyChanged(nameof(FriendSortDirection));
            }

            if (string.IsNullOrWhiteSpace(_gameSortPath) &&
                (string.IsNullOrWhiteSpace(propertyName) ||
                 GameSummariesSortHelper.IsConfiguredDefaultSortPropertyName(propertyName, GameSummariesSortSurface.FriendsOverview)))
            {
                ApplyFilters();
                OnPropertyChanged(nameof(GameSortPath));
                OnPropertyChanged(nameof(GameSortDirection));
            }

            if (string.IsNullOrWhiteSpace(_achievementSortPath) &&
                (string.IsNullOrWhiteSpace(propertyName) ||
                 AchievementSortHelper.IsConfiguredDefaultSortPropertyName(propertyName, AchievementSortSurface.FriendsOverviewRecentAchievements)))
            {
                UpdateAchievementQueries();
                OnPropertyChanged(nameof(AchievementSortPath));
                OnPropertyChanged(nameof(AchievementSortDirection));
            }
        }

        private IEnumerable<RefreshMode> CreateFriendRefreshModes()
        {
            yield return CreateRefreshMode(RefreshModeType.FriendsRecent);
            if (_settings?.Persisted?.IncludeUnownedFriendGames == true)
            {
                // Full is the only mode that scans unowned friend games; hide it entirely when
                // the global toggle excludes unowned games (the planner also clamps any Full
                // request to Shared as a backstop).
                yield return CreateRefreshMode(RefreshModeType.FriendsFull);
            }

            yield return CreateRefreshMode(RefreshModeType.FriendsShared);
            yield return CreateRefreshMode(RefreshModeType.FriendsInstalled);
            yield return CreateRefreshMode(RefreshModeType.FriendsSelectedGame);
            yield return CreateRefreshMode(RefreshModeType.FriendsCustom);
        }

        private void RebuildFriendRefreshModes()
        {
            FriendRefreshModes.Clear();
            foreach (var mode in CreateFriendRefreshModes())
            {
                FriendRefreshModes.Add(mode);
            }

            if (!FriendRefreshModes.Any(mode => string.Equals(mode?.Key, SelectedRefreshMode, StringComparison.Ordinal)))
            {
                SelectedRefreshMode = RefreshModeType.FriendsRecent.GetKey();
            }
        }

        private static RefreshMode CreateRefreshMode(RefreshModeType type)
        {
            return new RefreshMode(type, type.GetResourceKey(), type.GetShortResourceKey())
            {
                DisplayName = ResourceProvider.GetString(type.GetResourceKey()) ?? type.GetKey(),
                ShortDisplayName = ResourceProvider.GetString(type.GetShortResourceKey()) ?? type.GetKey()
            };
        }

        private static IEnumerable<string> GetProviderFilterKeys(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(friend.ProviderKey) &&
                !string.Equals(friend.ProviderKey, FriendOverviewProjection.MergedProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                yield return friend.ProviderKey;
            }

            foreach (var provider in friend.MemberProviderKeys ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    yield return provider;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_progressTracker != null)
            {
                _progressTracker.PropertyChanged -= OnProgressTrackerChanged;
                _progressTracker.Dispose();
            }

            if (_refreshRuntime != null)
            {
                _refreshRuntime.RebuildProgress -= OnRebuildProgress;
            }

            _persistedSubscription?.Dispose();
            _persistedSubscription = null;

            PlayniteAchievementsPlugin.SettingsSaved -= OnPluginSettingsSaved;

            if (_lists != null)
            {
                _lists.PropertyChanged -= OnListsChanged;
                _lists.Dispose();
            }

            _favoriteGames?.Dispose();
            _barOptions?.Dispose();
            _pairRows?.Dispose();
            _achievements?.Dispose();
        }

        // Merge groups, nicknames and favorites live in the settings the rows are stamped from.
        // Most saves are view state (a Showcase page, a window position, a column ratio), and
        // re-reading every list for them stalled each page switch. A settings window save may
        // change anything the rows are mapped with; any other save re-reads only when the friend
        // entries or merge groups differ from the last read, since those change inside their
        // collections without a property change.
        private void OnPluginSettingsSaved(object sender, EventArgs e)
        {
            if (_disposed ||
                (!ReferenceEquals(e, PlayniteAchievementsPlugin.SettingsWindowSave) &&
                 FriendSettingsKey() == _readFriendSettingsKey))
            {
                return;
            }

            _scheduler.Post(Reload);
        }

        private string FriendSettingsKey()
        {
            var persisted = _settings?.Persisted;
            return persisted == null
                ? null
                : Newtonsoft.Json.JsonConvert.SerializeObject(new { persisted.Friends, persisted.FriendMergeGroups });
        }
    }
}
