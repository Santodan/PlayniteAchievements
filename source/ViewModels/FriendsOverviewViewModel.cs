using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    internal sealed class FriendsOverviewViewModel : ObservableObject, IDisposable, IOverviewRefreshHeaderViewModel
    {
        private readonly IFriendCacheManager _friendCache;
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly RefreshRuntime _refreshRuntime;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly Func<Guid?, string, FriendCustomRefreshOptions> _showCustomRefreshDialog;
        private readonly FriendsOverviewDataCoordinator _friendsOverviewDataCoordinator;
        private readonly bool _ownsFriendsOverviewDataCoordinator;
        private readonly SearchTextIndex<FriendSummaryItem> _friendSearchIndex =
            new SearchTextIndex<FriendSummaryItem>(item => SearchTextBuilder.FromValues(
                new[] { item?.DisplayName, item?.ProviderKey }.Concat(item?.MemberProviderKeys ?? Enumerable.Empty<string>())));
        private readonly SearchTextIndex<FriendGameSummaryItem> _gameSearchIndex =
            new SearchTextIndex<FriendGameSummaryItem>(item => SearchTextBuilder.FromValues(item?.GameName, item?.ProviderKey));
        private readonly SearchTextIndex<FriendAchievementDisplayItem> _achievementSearchIndex =
            new SearchTextIndex<FriendAchievementDisplayItem>(item => SearchTextBuilder.FromValues(
                item?.FriendName,
                item?.GameName,
                item?.DisplayName,
                item?.Description,
                item?.CategoryType,
                item?.CategoryLabel,
                item?.ProviderKey));

        private List<FriendSummaryItem> _allFriends = new List<FriendSummaryItem>();
        private List<FriendGameSummaryItem> _allGames = new List<FriendGameSummaryItem>();
        private List<FriendAchievementDisplayItem> _allRecentUnlocks = new List<FriendAchievementDisplayItem>();
        private List<FriendAchievementDisplayItem> _allAchievements = new List<FriendAchievementDisplayItem>();
        private List<FriendAchievementDisplayItem> _allUnlockedAchievements = new List<FriendAchievementDisplayItem>();

        // On-demand rows for the friend+game pair comparison. The overview snapshot carries
        // unlocked rows only, so the selected game's full definition rows (locked included,
        // all friends) are fetched once per game selection and swapped in when they land;
        // until then the pair view falls back to the snapshot's unlocked rows.
        private List<FriendAchievementDisplayItem> _pairGameAchievements;
        private string _pairGameKey;
        private string _pairGameKeyInFlight;
        private int _pairFetchVersion;
        internal Task PairAchievementsFetchTask { get; private set; }
        private List<FriendSummaryItem> _filteredFriendsList = new List<FriendSummaryItem>();
        private List<FriendGameSummaryItem> _filteredGamesList = new List<FriendGameSummaryItem>();
        private List<FriendAchievementDisplayItem> _filteredAchievementsList = new List<FriendAchievementDisplayItem>();
        private string _friendSortPath;
        private ListSortDirection _friendSortDirection;
        private string _gameSortPath;
        private ListSortDirection _gameSortDirection;
        private string _achievementSortPath;
        private ListSortDirection _achievementSortDirection;
        private FriendOverviewProjection _projection = new FriendOverviewProjection(null);
        private readonly HashSet<string> _selectedTypeFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedCategoryFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FriendSummaryItem _selectedFriend;
        private FriendGameSummaryItem _selectedGame;
        private bool _isApplyingFilters;
        private bool _isRefreshing;
        private bool _disposed;
        private int _loadVersion;
        private int _suppressCoordinatorInvalidationReload;
        private readonly object _loadQueueSync = new object();
        private Task _loadQueueTask;
        private bool _loadAgainRequested;
        private bool _isLoading;
        private string _statusText;
        private string _friendSearchText;
        private string _gameSearchText;
        private string _achievementSearchText;
        private string _selectedProviderKey;
        private string _selectedRefreshMode = RefreshModeType.FriendsRecent.GetKey();
        private readonly RefreshHeaderProgressTracker _progressTracker;
        private readonly TimeSpan _cacheInvalidationDebounceInterval;
        private readonly TimeSpan _activeRefreshInvalidationInterval;
        private DateTime _lastCacheReloadUtc = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer _cacheInvalidationDebounceTimer;

        public FriendsOverviewViewModel(
            IFriendCacheManager friendCache,
            RefreshEntryPoint refreshCoordinator,
            RefreshRuntime refreshRuntime,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            IPlayniteAPI playniteApi = null,
            Func<Guid?, string, FriendCustomRefreshOptions> showCustomRefreshDialog = null,
            TimeSpan? cacheInvalidationDebounceInterval = null,
            TimeSpan? activeRefreshInvalidationInterval = null,
            FriendsOverviewDataCoordinator friendsOverviewDataCoordinator = null)
        {
            _friendCache = friendCache;
            _refreshCoordinator = refreshCoordinator;
            _refreshRuntime = refreshRuntime;
            _settings = settings;
            _logger = logger;
            _playniteApi = playniteApi;
            _showCustomRefreshDialog = showCustomRefreshDialog;
            _ownsFriendsOverviewDataCoordinator = friendsOverviewDataCoordinator == null;
            _friendsOverviewDataCoordinator = friendsOverviewDataCoordinator ??
                new FriendsOverviewDataCoordinator(friendCache, () => _settings?.Persisted, logger);
            if (!_ownsFriendsOverviewDataCoordinator)
            {
                // A shared coordinator retains its snapshot only while views (or a
                // friend-consuming theme) hold it; an owned instance is disposed wholesale.
                _friendsOverviewDataCoordinator.AddViewConsumer();
            }
            _friendsOverviewDataCoordinator.SnapshotInvalidated += OnFriendsOverviewSnapshotInvalidated;
            _cacheInvalidationDebounceInterval = cacheInvalidationDebounceInterval ?? TimeSpan.FromMilliseconds(300);
            // Matches the FriendsOverviewDataCoordinator invalidation-event throttle: each reload
            // during an active friend refresh rebuilds the full friend display graph, so the two
            // cadences are kept aligned.
            _activeRefreshInvalidationInterval = activeRefreshInvalidationInterval ?? TimeSpan.FromSeconds(15);

            if (_cacheInvalidationDebounceInterval > TimeSpan.Zero ||
                _activeRefreshInvalidationInterval > TimeSpan.Zero)
            {
                _cacheInvalidationDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = _cacheInvalidationDebounceInterval > TimeSpan.Zero
                        ? _cacheInvalidationDebounceInterval
                        : _activeRefreshInvalidationInterval
                };
                _cacheInvalidationDebounceTimer.Tick += OnCacheInvalidationDebounceTimerTick;
            }

            FilteredFriends = new BulkObservableCollection<FriendSummaryItem>();
            FilteredGames = new BulkObservableCollection<FriendGameSummaryItem>();
            DisplayedAchievements = new BulkObservableCollection<FriendAchievementDisplayItem>();
            SelectedFriendGameAllAchievements = new BulkObservableCollection<FriendAchievementDisplayItem>();
            ProviderFilterOptions = new ObservableCollection<string>();
            TypeFilterOptions = new ObservableCollection<string>();
            CategoryFilterOptions = new ObservableCollection<string>();
            FriendSummariesControlBar = CreateFriendSummariesControlBar();
            GameSummariesControlBar = CreateGameSummariesControlBar();
            AchievementsControlBar = CreateAchievementsControlBar();
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
                _refreshRuntime.CacheInvalidated += OnCacheInvalidated;
                _refreshRuntime.FriendCacheInvalidated += OnFriendCacheInvalidated;
            }
            else if (_friendCache != null)
            {
                _friendCache.FriendCacheInvalidated += OnFriendCacheInvalidated;
            }

            if (_settings?.Persisted != null)
            {
                _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
            }

            PlayniteAchievementsPlugin.SettingsSaved += OnPluginSettingsSaved;
        }

        public BulkObservableCollection<FriendSummaryItem> FilteredFriends { get; }
        public BulkObservableCollection<FriendGameSummaryItem> FilteredGames { get; }
        public BulkObservableCollection<FriendAchievementDisplayItem> DisplayedAchievements { get; }

        // Unfiltered friend+game comparison rows in canonical definition order feeding the grid's
        // CategorySummarySource so category ordering does not follow the configured or live sort.
        public BulkObservableCollection<FriendAchievementDisplayItem> SelectedFriendGameAllAchievements { get; }

        public BulkObservableCollection<FriendSummaryItem> Friends => FilteredFriends;
        public BulkObservableCollection<FriendGameSummaryItem> Games => FilteredGames;
        public BulkObservableCollection<FriendAchievementDisplayItem> RecentUnlocks => DisplayedAchievements;

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

        public PlayniteAchievementsSettings Settings => _settings;

        public ObservableCollection<string> ProviderFilterOptions { get; }
        public ObservableCollection<string> TypeFilterOptions { get; }
        public ObservableCollection<string> CategoryFilterOptions { get; }
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
                    ApplyFilters();
                }
            }
        }

        public string SelectedProviderKey
        {
            get => _selectedProviderKey;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (SetValueAndReturn(ref _selectedProviderKey, normalized))
                {
                    OnPropertyChanged(nameof(SelectedProviderFilterText));
                    ApplyFilters();
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
            ? ResourceProvider.GetString("LOCPlayAch_Button_Configure")
            : ResourceProvider.GetString("LOCPlayAch_Button_Refresh");

        public string RefreshOrCancelButtonText => IsRefreshing
            ? ResourceProvider.GetString("LOCPlayAch_Button_Cancel")
            : RefreshActionButtonText;

        public string RefreshOrCancelButtonGlyph => IsRefreshing
            ? "\uEEE4"
            : string.Equals(SelectedRefreshMode, RefreshModeType.FriendsCustom.GetKey(), StringComparison.Ordinal)
                ? "\uEFE1"
                : "\uEFD1";

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set
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

        // True while the initial data load is in flight (before any data is shown), so the view
        // can display a loading indicator. Set only for empty-state loads, so routine reloads
        // over already-populated grids do not flash the overlay.
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetValue(ref _isLoading, value);
        }
        public bool HasData => _allFriends.Count > 0 || _allGames.Count > 0 || _allRecentUnlocks.Count > 0 || _allAchievements.Count > 0 || _allUnlockedAchievements.Count > 0;
        public bool IsProviderDisabled => false;

        public string SelectedProviderFilterText => string.IsNullOrWhiteSpace(SelectedProviderKey)
            ? ResourceProvider.GetString("LOCPlayAch_FriendsOverview_AllProviders")
            : SelectedProviderKey;

        public string SelectedTypeFilterText => GetSelectedFilterText(
            _selectedTypeFilters,
            TypeFilterOptions,
            ResourceProvider.GetString("LOCPlayAch_Common_Label_Type"));

        public string SelectedCategoryFilterText => GetSelectedFilterText(
            _selectedCategoryFilters,
            CategoryFilterOptions,
            ResourceProvider.GetString("LOCPlayAch_Common_Label_Category"));

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
                        ResourceProvider.GetString("LOCPlayAch_FriendsOverview_SelectedGameAchievements"),
                        SelectedGame.GameName);
                }

                return ResourceProvider.GetString("LOCPlayAch_RecentAchievements");
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
            return new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(FriendSearchText),
                    () => FriendSearchText,
                    value => FriendSearchText = value,
                    GridControlBarText.Get("LOCPlayAch_FriendsOverview_SearchFriends", "Search Friends"),
                    ClearFriendSearch)
            };
        }

        private GridControlBarViewModel CreateGameSummariesControlBar()
        {
            return new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(GameSearchText),
                    () => GameSearchText,
                    value => GameSearchText = value,
                    GridControlBarText.Get("LOCPlayAch_Filter_Games", "Search Games"),
                    ClearGameSearch)
            };
        }

        private GridControlBarViewModel CreateAchievementsControlBar()
        {
            var controlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(AchievementSearchText),
                    () => AchievementSearchText,
                    value => AchievementSearchText = value,
                    GridControlBarText.Get("LOCPlayAch_Filter_Achievements", "Search Achievements"),
                    ClearAchievementSearch)
            };
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedTypeFilterText),
                () => SelectedTypeFilterText,
                () => TypeFilterOptions,
                IsTypeFilterSelected,
                SetTypeFilterSelected)
            {
                Width = 118
            });
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCategoryFilterText),
                () => SelectedCategoryFilterText,
                () => CategoryFilterOptions,
                IsCategoryFilterSelected,
                SetCategoryFilterSelected)
            {
                Width = 132
            });
            return controlBar;
        }

        public Task LoadAsync()
        {
            return LoadFromCacheAsync();
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

        public bool IsProviderFilterSelected(string providerKey)
        {
            return string.Equals(SelectedProviderKey, providerKey, StringComparison.OrdinalIgnoreCase);
        }

        public void SetProviderFilter(string providerKey)
        {
            SelectedProviderKey = providerKey;
        }

        public bool IsTypeFilterSelected(string value)
        {
            return IsFilterSelected(_selectedTypeFilters, value);
        }

        public void SetTypeFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedTypeFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTypeFilterText));
            ApplyFilters();
        }

        public bool IsCategoryFilterSelected(string value)
        {
            return IsFilterSelected(_selectedCategoryFilters, value);
        }

        public void SetCategoryFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedCategoryFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCategoryFilterText));
            ApplyFilters();
        }

        public void ToggleFriendSelection(FriendSummaryItem friend)
        {
            SelectedFriend = ReferenceEquals(SelectedFriend, friend) || IsSameFriend(SelectedFriend, friend)
                ? null
                : friend;
        }

        public void ToggleGameSelection(FriendGameSummaryItem game)
        {
            SelectedGame = ReferenceEquals(SelectedGame, game) || IsSameGame(SelectedGame, game)
                ? null
                : game;
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
                await LoadFromCacheAsync().ConfigureAwait(true);
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

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            if (report?.Mode?.IsFriendRefreshMode() != true)
            {
                return;
            }

            // Progress display is handled by the shared tracker; here we only track the
            // friend-scoped refreshing state (button/command semantics) and refresh the
            // friends snapshot when a friend refresh completes.
            IsRefreshing = _refreshRuntime?.IsRebuilding == true;

            if (_refreshRuntime?.IsFinalProgressReport(report) == true)
            {
                InvalidateFriendsOverviewSnapshot(forceImmediate: true);
            }
        }

        private void OnCacheInvalidated(object sender, EventArgs e)
        {
            if (!ShouldReloadFromCacheInvalidation())
            {
                return;
            }

            InvalidateFriendsOverviewSnapshot(forceImmediate: false);
        }

        private void OnFriendCacheInvalidated(object sender, FriendCacheInvalidatedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            // Not forceImmediate: friend scans flush cache invalidations every ~2s, and each
            // immediate reload is a full multi-second snapshot rebuild. The scheduled path
            // applies the active-refresh interval during scans (and the short idle debounce
            // otherwise), so scan progress still surfaces on a bounded cadence. The change
            // scope is passed through so the coordinator can patch instead of fully reloading.
            InvalidateFriendsOverviewSnapshot(forceImmediate: false, e);
        }

        private void OnFriendsOverviewSnapshotInvalidated(object sender, EventArgs e)
        {
            if (_disposed || Volatile.Read(ref _suppressCoordinatorInvalidationReload) > 0)
            {
                return;
            }

            ScheduleCacheInvalidationReloadOnDispatcher(forceImmediate: false);
        }

        private void InvalidateFriendsOverviewSnapshot(bool forceImmediate, FriendCacheInvalidatedEventArgs args = null)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _suppressCoordinatorInvalidationReload);
            try
            {
                _friendsOverviewDataCoordinator?.Invalidate(args);
            }
            finally
            {
                Interlocked.Decrement(ref _suppressCoordinatorInvalidationReload);
            }

            ScheduleCacheInvalidationReloadOnDispatcher(forceImmediate);
        }

        private void ScheduleCacheInvalidationReloadOnDispatcher(bool forceImmediate)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.InvokeIfNeeded(() => ScheduleCacheInvalidationReload(forceImmediate));
            }
            else
            {
                ScheduleCacheInvalidationReload(forceImmediate);
            }
        }

        private bool ShouldReloadFromCacheInvalidation()
        {
            if (_disposed)
            {
                return false;
            }

            if (_refreshRuntime?.IsRebuilding == true)
            {
                return _refreshRuntime.GetLastRebuildProgress()?.Mode?.IsFriendRefreshMode() == true;
            }

            return true;
        }

        private void ScheduleCacheInvalidationReload(bool forceImmediate)
        {
            if (_disposed)
            {
                return;
            }

            var delay = forceImmediate
                ? TimeSpan.Zero
                : GetCacheInvalidationReloadDelay();

            if (delay <= TimeSpan.Zero ||
                _cacheInvalidationDebounceTimer == null)
            {
                _cacheInvalidationDebounceTimer?.Stop();
                _ = LoadFromCacheAsync();
                return;
            }

            _cacheInvalidationDebounceTimer.Stop();
            _cacheInvalidationDebounceTimer.Interval = delay;
            _cacheInvalidationDebounceTimer.Start();
        }

        private TimeSpan GetCacheInvalidationReloadDelay()
        {
            if (IsActiveFriendRefresh())
            {
                if (_activeRefreshInvalidationInterval <= TimeSpan.Zero)
                {
                    return TimeSpan.Zero;
                }

                var elapsed = DateTime.UtcNow - _lastCacheReloadUtc;
                return elapsed >= _activeRefreshInvalidationInterval
                    ? TimeSpan.Zero
                    : _activeRefreshInvalidationInterval - elapsed;
            }

            return _cacheInvalidationDebounceInterval;
        }

        private bool IsActiveFriendRefresh()
        {
            return _refreshRuntime?.IsRebuilding == true &&
                   _refreshRuntime.GetLastRebuildProgress()?.Mode?.IsFriendRefreshMode() == true;
        }

        private async void OnCacheInvalidationDebounceTimerTick(object sender, EventArgs e)
        {
            _cacheInvalidationDebounceTimer?.Stop();
            try
            {
                await LoadFromCacheAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reload friends overview after cache invalidation.");
            }
        }

        private Task LoadFromCacheAsync()
        {
            Task queueTask;
            bool startedNewLoad = false;
            lock (_loadQueueSync)
            {
                if (_disposed)
                {
                    return Task.CompletedTask;
                }

                if (_loadQueueTask == null || _loadQueueTask.IsCompleted)
                {
                    _loadAgainRequested = false;
                    _loadQueueTask = RunLoadFromCacheQueueAsync();
                    startedNewLoad = true;
                }
                else
                {
                    _loadAgainRequested = true;
                }

                queueTask = _loadQueueTask;
            }

            // Show the loading indicator only for an initial (empty) load, so it fills the blank
            // window while the first snapshot builds without covering already-populated grids on
            // routine reloads. It is cleared once the queue drains (RunLoadFromCacheQueueAsync).
            if (startedNewLoad && !HasData)
            {
                SetIsLoadingOnUiThread(true);
            }

            return queueTask;
        }

        private void SetIsLoadingOnUiThread(bool value)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => IsLoading = value));
                return;
            }

            IsLoading = value;
        }

        private async Task RunLoadFromCacheQueueAsync()
        {
            while (true)
            {
                lock (_loadQueueSync)
                {
                    _loadAgainRequested = false;
                }

                await LoadFromCacheOnceAsync().ConfigureAwait(false);

                lock (_loadQueueSync)
                {
                    if (!_loadAgainRequested || _disposed)
                    {
                        _loadQueueTask = null;
                        SetIsLoadingOnUiThread(false);
                        return;
                    }
                }
            }
        }

        private async Task LoadFromCacheOnceAsync()
        {
            // Latest-wins guard: a newer load supersedes any in-flight one so its
            // (possibly stale) results are discarded when they arrive on the UI thread.
            var version = Interlocked.Increment(ref _loadVersion);
            _lastCacheReloadUtc = DateTime.UtcNow;

            FriendsOverviewLoadResult result;
            try
            {
                var snapshot = await _friendsOverviewDataCoordinator
                    .GetSnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                if (snapshot == null)
                {
                    result = null;
                }
                else
                {
                    result = await Task
                        .Run(() =>
                        {
                            using (PerfScope.Start(_logger, "FriendsOverview.PrepareSnapshot", thresholdMs: 25))
                            {
                                return new FriendsOverviewLoadResult
                                {
                                    Projection = snapshot.Projection,
                                    Friends = snapshot.Friends,
                                    Games = snapshot.Games,
                                    RecentUnlocks = snapshot.RecentUnlocks,
                                    AllAchievements = snapshot.AllAchievements,
                                    AllUnlockedAchievements = snapshot.AllUnlockedAchievements,
                                    FriendSearchEntries = _friendSearchIndex.BuildEntries(snapshot.Friends),
                                    GameSearchEntries = _gameSearchIndex.BuildEntries(snapshot.Games),
                                    AchievementSearchEntries = _achievementSearchIndex.BuildEntries(
                                        snapshot.RecentUnlocks.Concat(snapshot.AllAchievements))
                                };
                            }
                        })
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load friends overview cache data.");
                result = null;
            }

            void Apply()
            {
                if (_disposed || version != Volatile.Read(ref _loadVersion))
                {
                    return;
                }

                if (result != null)
                {
                    _projection = result.Projection;
                    _allFriends = result.Friends;
                    _allGames = result.Games;
                    _allRecentUnlocks = result.RecentUnlocks;
                    _allAchievements = result.AllAchievements;
                    _allUnlockedAchievements = result.AllUnlockedAchievements;
                    ResetPairAchievements();

                    using (PerfScope.Start(_logger, "FriendsOverview.ApplyOnUiThread", thresholdMs: 15))
                    {
                        _friendSearchIndex.LoadEntries(result.FriendSearchEntries);
                        _gameSearchIndex.LoadEntries(result.GameSearchEntries);
                        _achievementSearchIndex.LoadEntries(result.AchievementSearchEntries);
                        UpdateFilterOptions();
                        ApplyFilters(preserveSelections: true);
                    }

                    StatusText = HasData
                        ? null
                        : ResourceProvider.GetString("LOCPlayAch_FriendsOverview_NoData");
                    OnPropertyChanged(nameof(HasData));
                    OnPropertyChanged(nameof(IsProviderDisabled));
                }
                else
                {
                    _allFriends = new List<FriendSummaryItem>();
                    _allGames = new List<FriendGameSummaryItem>();
                    _allRecentUnlocks = new List<FriendAchievementDisplayItem>();
                    _allAchievements = new List<FriendAchievementDisplayItem>();
                    _allUnlockedAchievements = new List<FriendAchievementDisplayItem>();
                    ResetPairAchievements();
                    _projection = new FriendOverviewProjection(null);
                    FilteredFriends.ReplaceAll(Array.Empty<FriendSummaryItem>());
                    FilteredGames.ReplaceAll(Array.Empty<FriendGameSummaryItem>());
                    SelectedFriendGameAllAchievements.ReplaceAll(Array.Empty<FriendAchievementDisplayItem>());
                    DisplayedAchievements.ReplaceAll(Array.Empty<FriendAchievementDisplayItem>());
                    StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsOverview_LoadFailed");
                    OnPropertyChanged(nameof(HasData));
                }
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.InvokeIfNeeded(Apply);
            }
            else
            {
                Apply();
            }
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
                var achievementQuery = SearchQuery.From(AchievementSearchText);

                var friends = _allFriends
                    .Where(MatchesProvider)
                    .Where(friend => _friendSearchIndex.Matches(friend, friendQuery));
                if (SelectedGame != null)
                {
                    friends = friends.Where(friend => HasFriendGamePairData(friend, SelectedGame));
                }

                var friendList = friends.ToList();

                var gameSource = SelectedFriend != null
                    ? GetSelectedFriendGames(SelectedFriend)
                    : _allGames;

                // No unlock gating here: the games list is ownership-driven and the cache only holds
                // ownership rows that should display (owned games unconditionally; provider-only games
                // once the friend has confirmed unlocks).
                var games = gameSource
                    .Where(game => MatchesProvider(game?.ProviderKey))
                    .Where(game => _gameSearchIndex.Matches(game, gameQuery));

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
                        if (preserveSelections)
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

                // Rescope the type/category dropdowns to the now-resolved friend/game selection.
                UpdateScopedFilterOptions();

                // Locked rows only make sense in the single friend + single game comparison view;
                // every aggregated view (friend-only, game-only) shows unlocked rows, and no
                // selection keeps the recent-unlocks feed. Pair rows (locked included) load on
                // demand per selected game.
                IReadOnlyList<FriendAchievementDisplayItem> achievementSource = HasFriendGameSelection
                    ? ResolvePairAchievementSource()
                    : HasAnySelection
                        ? _allUnlockedAchievements
                        : _allRecentUnlocks;

                var achievements = achievementSource
                    .Where(achievement => MatchesProvider(achievement?.ProviderKey))
                    .Where(achievement => _achievementSearchIndex.Matches(achievement, achievementQuery))
                    .Where(MatchesAchievementFilters);

                if (SelectedFriend != null)
                {
                    achievements = achievements.Where(achievement => IsSameFriend(achievement, SelectedFriend));
                }

                if (SelectedGame != null)
                {
                    achievements = achievements.Where(achievement => IsSameGame(achievement, SelectedGame));
                }

                var achievementList = achievements.ToList();

                _filteredFriendsList = friendList;
                _filteredGamesList = gameList;
                _filteredAchievementsList = achievementList;

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

                if (!string.IsNullOrEmpty(_achievementSortPath))
                {
                    var achievementSortDirection = (ListSortDirection?)_achievementSortDirection;
                    if (AchievementSortHelper.TrySortItems(
                            _filteredAchievementsList,
                            _achievementSortPath,
                            _achievementSortDirection,
                            AchievementSortScope.RecentAchievements,
                            ref _achievementSortPath,
                            ref achievementSortDirection) &&
                        achievementSortDirection.HasValue)
                    {
                        _achievementSortDirection = achievementSortDirection.Value;
                    }
                }
                else
                {
                    ApplyAchievementConfiguredDefaultSort();
                }

                var persisted = _settings?.Persisted;
                FilteredFriends.ReplaceAll(DisplayGridRowLimitHelper.Limit(
                    _filteredFriendsList,
                    persisted?.FriendsOverviewFriendSummariesGridMaxRows));
                FilteredGames.ReplaceAll(DisplayGridRowLimitHelper.Limit(
                    _filteredGamesList,
                    persisted?.FriendsOverviewGameSummariesGridMaxRows));
                // Keep the unfiltered category-summary source current; achievement filters and grid
                // sorts never touch it, so the category fallback order stays the definition-ordered
                // snapshot loaded from the cache. Replaced before DisplayedAchievements so the grid's
                // items-source reset rebuilds category rollups from the new selection's rows.
                SelectedFriendGameAllAchievements.ReplaceAll(HasFriendGameSelection
                    ? achievementSource.Where(achievement =>
                        IsSameFriend(achievement, SelectedFriend) &&
                        IsSameGame(achievement, SelectedGame))
                    : Enumerable.Empty<FriendAchievementDisplayItem>());
                DisplayedAchievements.ReplaceAll(DisplayGridRowLimitHelper.Limit(
                    _filteredAchievementsList,
                    persisted?.FriendsOverviewAchievementsGridMaxRows));
                OnPropertyChanged(nameof(AchievementCountText));
                OnPropertyChanged(nameof(HasData));
            }
            finally
            {
                _isApplyingFilters = false;
            }
        }

        // Returns the pair comparison's achievement source: the on-demand full rows when they
        // are loaded (or loaded-empty, falling back), otherwise the snapshot's unlocked rows
        // while a fetch for the selected game runs.
        private IReadOnlyList<FriendAchievementDisplayItem> ResolvePairAchievementSource()
        {
            var key = FriendOverviewProjection.GetGameScopeKey(SelectedGame);
            if (string.IsNullOrWhiteSpace(key) ||
                string.Equals(key, FriendOverviewProjection.AllScopeKey, StringComparison.OrdinalIgnoreCase))
            {
                return _allAchievements;
            }

            if (_pairGameAchievements != null &&
                string.Equals(_pairGameKey, key, StringComparison.OrdinalIgnoreCase))
            {
                // A loaded-but-empty result (no cached definitions for the game) falls back to
                // the unlocked rows instead of blanking the grid; it is cached either way so
                // filter churn does not re-query.
                return _pairGameAchievements.Count > 0 ? _pairGameAchievements : _allAchievements;
            }

            BeginPairAchievementsFetch(key, SelectedGame);
            return _allAchievements;
        }

        private void BeginPairAchievementsFetch(string key, FriendGameSummaryItem game)
        {
            if (_friendCache == null ||
                game == null ||
                string.Equals(_pairGameKeyInFlight, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _pairGameKeyInFlight = key;
            var version = Interlocked.Increment(ref _pairFetchVersion);
            var gameScope = FriendCacheChange.ForGameDefinition(
                game.ProviderKey,
                game.AppId,
                game.ProviderGameKey);
            PairAchievementsFetchTask = FetchPairAchievementsAsync(version, key, gameScope);
        }

        private async Task FetchPairAchievementsAsync(int version, string key, FriendCacheChange gameScope)
        {
            List<FriendAchievementDisplayItem> rows = null;
            try
            {
                rows = await Task
                    .Run(() => _friendCache.LoadFriendGameAchievementData(gameScope)?.AllAchievements)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load friend pair achievement rows.");
            }

            void Apply()
            {
                if (_disposed || version != Volatile.Read(ref _pairFetchVersion))
                {
                    return;
                }

                _pairGameKeyInFlight = null;
                _pairGameKey = key;
                _pairGameAchievements = rows ?? new List<FriendAchievementDisplayItem>();
                ApplyFilters(preserveSelections: true);
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.InvokeIfNeeded(Apply);
            }
            else
            {
                Apply();
            }
        }

        // Drops the on-demand pair rows and discards any in-flight fetch result; the next
        // pair-scoped ApplyFilters re-fetches against the fresh cache state.
        private void ResetPairAchievements()
        {
            Interlocked.Increment(ref _pairFetchVersion);
            _pairGameAchievements = null;
            _pairGameKey = null;
            _pairGameKeyInFlight = null;
        }

        private void ApplyAchievementConfiguredDefaultSort()
        {
            var configuredSort = AchievementSortHelper.GetConfiguredDefaultSort(
                _settings?.Persisted,
                AchievementSortSurface.FriendsOverviewRecentAchievements);
            if (configuredSort.PreservesSourceOrder)
            {
                return;
            }

            var comparison = AchievementSortHelper.GetComparison(
                configuredSort.SortMemberPath,
                configuredSort.Direction,
                AchievementSortScope.RecentAchievements);
            if (comparison == null)
            {
                return;
            }

            _filteredAchievementsList.Sort(comparison);
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
            else if (itemsSource == DisplayedAchievements)
            {
                SortAchievements(sortMemberPath, direction);
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

        private void SortAchievements(string sortMemberPath, ListSortDirection direction)
        {
            var achievementSortDirection = (ListSortDirection?)_achievementSortDirection;
            if (!AchievementSortHelper.TrySortItems(
                    _filteredAchievementsList,
                    sortMemberPath,
                    direction,
                    AchievementSortScope.RecentAchievements,
                    ref _achievementSortPath,
                    ref achievementSortDirection))
            {
                return;
            }

            if (achievementSortDirection.HasValue)
            {
                _achievementSortDirection = achievementSortDirection.Value;
            }

            SyncAchievementsDisplay();
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
            ApplyFilters(preserveSelections: true);
        }

        private void SyncFriendsDisplay()
        {
            FilteredFriends.ReplaceAll(DisplayGridRowLimitHelper.Limit(
                _filteredFriendsList,
                _settings?.Persisted?.FriendsOverviewFriendSummariesGridMaxRows));
        }

        private void SyncGamesDisplay()
        {
            FilteredGames.ReplaceAll(DisplayGridRowLimitHelper.Limit(
                _filteredGamesList,
                _settings?.Persisted?.FriendsOverviewGameSummariesGridMaxRows));
        }

        private void SyncAchievementsDisplay()
        {
            DisplayedAchievements.ReplaceAll(DisplayGridRowLimitHelper.Limit(
                _filteredAchievementsList,
                _settings?.Persisted?.FriendsOverviewAchievementsGridMaxRows));
        }

        private bool MatchesProvider(string providerKey)
        {
            return string.IsNullOrWhiteSpace(SelectedProviderKey) ||
                   string.Equals(providerKey, SelectedProviderKey, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesProvider(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                return false;
            }

            if (MatchesProvider(friend.ProviderKey))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(SelectedProviderKey) &&
                   (friend.MemberProviderKeys ?? new List<string>())
                   .Any(provider => string.Equals(provider, SelectedProviderKey, StringComparison.OrdinalIgnoreCase));
        }

        private bool MatchesAchievementFilters(FriendAchievementDisplayItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (_selectedTypeFilters.Count > 0 &&
                !_selectedTypeFilters.Contains(item.CategoryType ?? string.Empty))
            {
                return false;
            }

            if (_selectedCategoryFilters.Count > 0 &&
                !_selectedCategoryFilters.Contains(item.CategoryLabel ?? string.Empty))
            {
                return false;
            }

            return true;
        }

        private IReadOnlyList<FriendGameSummaryItem> GetSelectedFriendGames(FriendSummaryItem friend)
        {
            return _projection?.GetSelectedFriendGames(friend) ?? Array.Empty<FriendGameSummaryItem>();
        }

        private FriendGameSummaryItem GetSelectedFriendGameForHeader()
        {
            if (SelectedFriend == null || SelectedGame == null)
            {
                return SelectedGame;
            }

            return GetSelectedFriendGames(SelectedFriend)
                .FirstOrDefault(game => IsSameGame(game, SelectedGame)) ??
                   SelectedGame;
        }

        private bool HasFriendGamePairData(FriendSummaryItem friend, FriendGameSummaryItem game)
        {
            return _projection?.HasFriendGamePairData(friend, game) == true;
        }

        private static bool IsSameFriend(FriendSummaryItem left, FriendSummaryItem right)
        {
            return FriendOverviewProjection.IsSameFriend(left, right);
        }

        private static bool IsSameFriend(FriendAchievementDisplayItem achievement, FriendSummaryItem friend)
        {
            return FriendOverviewProjection.IsSameFriend(achievement, friend);
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

        private static bool IsSameGame(FriendAchievementDisplayItem achievement, FriendGameSummaryItem game)
        {
            return FriendOverviewProjection.IsSameGame(achievement, game);
        }

        private void UpdateFilterOptions()
        {
            ReplaceOptions(
                ProviderFilterOptions,
                _allFriends.SelectMany(GetProviderFilterKeys)
                    .Concat(_allGames.Select(game => game?.ProviderKey))
                    .Concat(_allAchievements.Select(achievement => achievement?.ProviderKey)));

            if (!string.IsNullOrWhiteSpace(SelectedProviderKey) &&
                !ProviderFilterOptions.Any(provider => string.Equals(provider, SelectedProviderKey, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedProviderKey = null;
                OnPropertyChanged(nameof(SelectedProviderKey));
                OnPropertyChanged(nameof(SelectedProviderFilterText));
            }

            UpdateScopedFilterOptions();
        }

        // Type and category options reflect only the achievements currently in scope. They only
        // make sense once a single game is selected (achievement types/categories are game-specific
        // vocabulary); with no game selected they're cleared, which auto-hides the dropdowns via
        // GridMultiSelectFilter.HasAvailableAction. Recomputed from ApplyFilters as selection changes.
        private void UpdateScopedFilterOptions()
        {
            if (SelectedGame == null)
            {
                ReplaceOptions(TypeFilterOptions, Enumerable.Empty<string>());
                ReplaceOptions(CategoryFilterOptions, Enumerable.Empty<string>());
                PruneFilterSelections(_selectedTypeFilters, TypeFilterOptions);
                PruneFilterSelections(_selectedCategoryFilters, CategoryFilterOptions);
                OnPropertyChanged(nameof(SelectedTypeFilterText));
                OnPropertyChanged(nameof(SelectedCategoryFilterText));
                AchievementsControlBar?.Refresh();
                return;
            }

            // Mirror the grid's source pick so the dropdowns only offer values the grid can show.
            var scoped = (HasFriendGameSelection ? _allAchievements : _allUnlockedAchievements)
                .Where(achievement => MatchesProvider(achievement?.ProviderKey))
                .Where(achievement => IsSameGame(achievement, SelectedGame));
            if (SelectedFriend != null)
            {
                scoped = scoped.Where(achievement => IsSameFriend(achievement, SelectedFriend));
            }

            var scopedList = scoped.ToList();

            ReplaceOptions(TypeFilterOptions, scopedList.Select(achievement => achievement?.CategoryType));
            ReplaceOptions(CategoryFilterOptions, scopedList.Select(achievement => achievement?.CategoryLabel));

            PruneFilterSelections(_selectedTypeFilters, TypeFilterOptions);
            PruneFilterSelections(_selectedCategoryFilters, CategoryFilterOptions);

            OnPropertyChanged(nameof(SelectedTypeFilterText));
            OnPropertyChanged(nameof(SelectedCategoryFilterText));
            AchievementsControlBar?.Refresh();
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
            ObservableCollection<string> options,
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
            OnPropertyChanged(nameof(AchievementSectionTitle));
            OnPropertyChanged(nameof(AchievementCountText));
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
                propertyName == nameof(PersistedSettings.ShowFriendSpoilers))
            {
                _friendsOverviewDataCoordinator?.Invalidate();
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
                ApplyFilters();
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

        private static List<FriendAchievementDisplayItem> ResolveAllAchievements(FriendsOverviewData data)
        {
            var all = data?.AllAchievements;
            if (all != null && all.Count > 0)
            {
                return all;
            }

            return data?.AllUnlockedAchievements ?? new List<FriendAchievementDisplayItem>();
        }

        public void Dispose()
        {
            // Guards the shared coordinator's view-consumer count against double-decrement.
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
                _refreshRuntime.CacheInvalidated -= OnCacheInvalidated;
                _refreshRuntime.FriendCacheInvalidated -= OnFriendCacheInvalidated;
            }
            else if (_friendCache != null)
            {
                _friendCache.FriendCacheInvalidated -= OnFriendCacheInvalidated;
            }

            if (_cacheInvalidationDebounceTimer != null)
            {
                _cacheInvalidationDebounceTimer.Stop();
                _cacheInvalidationDebounceTimer.Tick -= OnCacheInvalidationDebounceTimerTick;
                _cacheInvalidationDebounceTimer = null;
            }

            if (_settings?.Persisted != null)
            {
                _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
            }

            PlayniteAchievementsPlugin.SettingsSaved -= OnPluginSettingsSaved;
            _friendsOverviewDataCoordinator.SnapshotInvalidated -= OnFriendsOverviewSnapshotInvalidated;

            if (_ownsFriendsOverviewDataCoordinator)
            {
                _friendsOverviewDataCoordinator?.Dispose();
            }
            else
            {
                _friendsOverviewDataCoordinator?.RemoveViewConsumer();
            }
        }

        private void OnPluginSettingsSaved(object sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            InvalidateFriendsOverviewSnapshot(forceImmediate: true);
        }

        private sealed class FriendsOverviewLoadResult
        {
            public FriendOverviewProjection Projection { get; set; }

            public List<FriendSummaryItem> Friends { get; set; }

            public List<FriendGameSummaryItem> Games { get; set; }

            public List<FriendAchievementDisplayItem> RecentUnlocks { get; set; }

            public List<FriendAchievementDisplayItem> AllAchievements { get; set; }

            public List<FriendAchievementDisplayItem> AllUnlockedAchievements { get; set; }

            public Dictionary<FriendSummaryItem, string> FriendSearchEntries { get; set; }

            public Dictionary<FriendGameSummaryItem, string> GameSearchEntries { get; set; }

            public Dictionary<FriendAchievementDisplayItem, string> AchievementSearchEntries { get; set; }
        }
    }
}
