using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public sealed class ViewFriendsAchievementsViewModel : ObservableObject, IDisposable
    {
        private readonly Guid _gameId;
        private readonly FriendGameAchievementsDataCoordinator _dataCoordinator;
        private readonly RefreshRuntime _refreshRuntime;
        private readonly RefreshEntryPoint _refreshEntryPoint;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly AchievementGridControlBarAdapter _achievementControlBar =
            new AchievementGridControlBarAdapter();
        private readonly SearchTextIndex<FriendSummaryItem> _friendSearchIndex =
            new SearchTextIndex<FriendSummaryItem>(friend => SearchTextBuilder.FromValues(
                friend?.DisplayName,
                friend?.ProviderKey,
                friend?.ExternalUserId));
        private readonly List<FriendSummaryItem> _allFriends = new List<FriendSummaryItem>();
        private readonly List<FriendAchievementDisplayItem> _allAchievements = new List<FriendAchievementDisplayItem>();
        private FriendGameSummaryItem _summaryItem;
        private FriendOverviewProjection _projection;
        private FriendSummaryItem _selectedFriend;
        private string _selectedCategoryName;
        private string _friendSearchText = string.Empty;
        private string _gameName;
        private bool _isLoading;
        private bool _isRefreshing;
        private double _progressPercent;
        private string _progressMessage;
        private string _statusText;
        private CancellationTokenSource _loadCts;
        private bool _disposed;

        internal ViewFriendsAchievementsViewModel(
            Guid gameId,
            FriendGameAchievementsDataCoordinator dataCoordinator,
            RefreshRuntime refreshRuntime,
            RefreshEntryPoint refreshEntryPoint,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _gameId = gameId;
            _dataCoordinator = dataCoordinator ?? throw new ArgumentNullException(nameof(dataCoordinator));
            _refreshRuntime = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _refreshEntryPoint = refreshEntryPoint ?? throw new ArgumentNullException(nameof(refreshEntryPoint));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;
            GameName = ResolveInitialGameName();

            FriendSummariesControlBar = CreateFriendSummariesControlBar();
            AchievementsControlBar = _achievementControlBar.ControlBar;
            _achievementControlBar.FilterChanged += (_, __) => ApplyFilters();

            RefreshCommand = new RelayCommand(async _ => await RefreshAllFriendsForGameAsync(), _ => !IsRefreshing);
            RefreshSelectedFriendCommand = new RelayCommand(
                async parameter => await RefreshSelectedFriendForGameAsync(parameter as FriendSummaryItem),
                parameter => !IsRefreshing && parameter is FriendSummaryItem);
            ClearFriendSelectionCommand = new RelayCommand(_ => SelectedFriend = null);
            OpenGameInLibraryCommand = new RelayCommand(_ => OpenGameInLibrary());

            if (_settings != null)
            {
                _settings.PropertyChanged += OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
                }
            }

            _refreshRuntime.RebuildProgress += OnRebuildProgress;
            _dataCoordinator.SnapshotInvalidated += OnSnapshotInvalidated;
            _ = LoadAsync();
        }

        public BulkObservableCollection<FriendSummaryItem> Friends { get; } =
            new BulkObservableCollection<FriendSummaryItem>();

        public BulkObservableCollection<FriendAchievementDisplayItem> Achievements { get; } =
            new BulkObservableCollection<FriendAchievementDisplayItem>();

        // Unfiltered selected-friend comparison rows in canonical definition order feeding the
        // grid's CategorySummarySource so category ordering does not follow the configured or
        // live sort.
        public BulkObservableCollection<FriendAchievementDisplayItem> SelectedFriendAllAchievements { get; } =
            new BulkObservableCollection<FriendAchievementDisplayItem>();

        public BulkObservableCollection<FriendGameSummaryItem> SummaryItems { get; } =
            new BulkObservableCollection<FriendGameSummaryItem>();

        public GridControlBarViewModel FriendSummariesControlBar { get; }

        public GridControlBarViewModel AchievementsControlBar { get; }

        public ICommand RefreshCommand { get; }

        public ICommand RefreshSelectedFriendCommand { get; }

        public ICommand ClearFriendSelectionCommand { get; }

        public ICommand OpenGameInLibraryCommand { get; }

        public PlayniteAchievementsSettings Settings => _settings;

        public string GameName
        {
            get => _gameName;
            private set => SetValue(ref _gameName, value);
        }

        public FriendSummaryItem SelectedFriend
        {
            get => _selectedFriend;
            set
            {
                if (SetValueAndReturn(ref _selectedFriend, value))
                {
                    OnPropertyChanged(nameof(HasFriendSelection));
                    OnPropertyChanged(nameof(AchievementColumnSettingsKey));
                    OnPropertyChanged(nameof(AchievementSectionTitle));
                    OnPropertyChanged(nameof(AchievementCountText));
                    UpdateSummaryItems();
                    ApplyFilters();
                }
            }
        }

        public bool HasFriendSelection => SelectedFriend != null;
        public string AchievementColumnSettingsKey => HasFriendSelection
            ? "ViewFriendsAchievementsSelectedFriendAchievements"
            : "ViewFriendsAchievements";

        // Category the achievement grid is currently drilled into (null when not drilled), pushed
        // up from AchievementDataGridControl so a breadcrumb header can be shown above the grid.
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
                var normalized = value ?? string.Empty;
                if (SetValueAndReturn(ref _friendSearchText, normalized))
                {
                    ApplyFilters();
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetValueAndReturn(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(ShowProgress));
                }
            }
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set
            {
                if (SetValueAndReturn(ref _isRefreshing, value))
                {
                    (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RefreshSelectedFriendCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(ShowProgress));
                }
            }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            private set => SetValue(ref _progressPercent, value);
        }

        public string ProgressMessage
        {
            get => _progressMessage;
            private set
            {
                if (SetValueAndReturn(ref _progressMessage, value))
                {
                    OnPropertyChanged(nameof(ShowProgress));
                }
            }
        }

        public bool ShowProgress => (IsLoading || IsRefreshing) && !string.IsNullOrWhiteSpace(ProgressMessage);

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

        public bool HasAchievements => _allAchievements.Count > 0;

        public string AchievementSectionTitle => SelectedFriend != null
            ? string.Format(
                L("LOCPlayAch_FriendsOverview_SelectedFriendAchievements"),
                SelectedFriend.DisplayName)
            : L("LOCPlayAch_ViewFriendsAchievements_TitleFallback");

        public string AchievementCountText
        {
            get
            {
                if (SelectedFriend == null)
                {
                    return string.Empty;
                }

                var game = GetSelectedFriendGameSummary() ?? _summaryItem;
                var unlocked = Math.Max(0, game?.UniqueFriendUnlockedAchievementsCount ?? 0);
                var total = Math.Max(0, game?.TotalAchievements ?? 0);
                var format = FriendsOverviewViewModel.GetResourceFormatOrFallback(
                    "LOCPlayAch_FriendsOverview_SelectedGameAchievementCount",
                    "({0}/{1} {2})",
                    "{0}");

                return string.Format(
                    format,
                    unlocked.ToString("N0", FormattingCulture.Current),
                    total.ToString("N0", FormattingCulture.Current),
                    L("LOCPlayAch_Achievements"));
            }
        }

        public bool SummaryUseCoverImages => GameSummaryOptions?.UseCoverImages ?? false;

        public bool SummaryShowMetadataPlatform => GameSummaryOptions?.ShowMetadataPlatform ?? true;

        public bool SummaryShowMetadataPlaytime => GameSummaryOptions?.ShowMetadataPlaytime ?? true;

        public bool SummaryShowMetadataRegion => GameSummaryOptions?.ShowMetadataRegion ?? true;

        public bool SummaryShowCompletionGlow => GameSummaryOptions?.ShowCompletionGlow ?? true;

        public bool SummaryShowColumnHeaders => GameSummaryOptions?.ShowColumnHeaders ?? true;

        public double? SummaryGridRowHeight => GameSummaryOptions?.RowHeight;

        public double? FriendSummariesGridRowHeight =>
            _settings?.Persisted?.GridOptions?.GetFriendSummaries(GridOptionKeys.FriendSummaries.ViewFriendsAchievements)?.RowHeight;

        public bool ShowFriendSummariesGridColumnHeaders =>
            _settings?.Persisted?.GridOptions?.GetFriendSummaries(GridOptionKeys.FriendSummaries.ViewFriendsAchievements)?.ShowColumnHeaders ?? true;

        public bool ShowFriendSummariesGridControlBar =>
            _settings?.Persisted?.GridOptions?.GetFriendSummaries(GridOptionKeys.FriendSummaries.ViewFriendsAchievements)?.ShowControlBar ?? true;

        public double? AchievementsGridRowHeight =>
            AchievementGridOptions?.RowHeight;

        public bool ShowAchievementsGridColumnHeaders =>
            AchievementGridOptions?.ShowColumnHeaders ?? true;

        public bool ShowAchievementsGridControlBar =>
            AchievementGridOptions?.ShowControlBar ?? true;

        public bool UseCoverImages =>
            AchievementGridOptions?.UseCoverImages ?? false;

        public bool ShowRarityGlow =>
            AchievementGridOptions?.ShowRarityGlow ?? true;

        public bool ColorNamesByRarity =>
            AchievementGridOptions?.ColorNamesByRarity ?? false;

        public bool ColorRarityColumnsByRarity =>
            AchievementGridOptions?.ColorRarityColumnsByRarity ?? false;

        public bool HideCategorySummaryRow =>
            AchievementGridOptions?.HideCategorySummaryRow ?? false;

        public bool CategorySummariesShowColumnHeaders =>
            CategorySummaryOptions?.ShowColumnHeaders ?? true;

        public double? CategorySummariesGridRowHeight =>
            CategorySummaryOptions?.RowHeight;

        public bool CategorySummariesUseCoverImages =>
            CategorySummaryOptions?.UseCoverImages ?? false;

        public bool CategorySummariesShowCompletionGlow =>
            CategorySummaryOptions?.ShowCompletionGlow ?? true;

        private AchievementGridOptions AchievementGridOptions =>
            _settings?.Persisted?.GridOptions?.GetAchievement(GridOptionKeys.Achievement.ViewFriendsAchievements);

        private GameSummaryGridOptions GameSummaryOptions =>
            _settings?.Persisted?.GridOptions?.GetGameSummaries(GridOptionKeys.GameSummaries.ViewFriendsAchievements);

        private CategorySummaryGridOptions CategorySummaryOptions =>
            _settings?.Persisted?.GridOptions?.GetCategorySummaries(GridOptionKeys.CategorySummaries.ViewFriendsAchievements);

        public async Task LoadAsync()
        {
            if (_disposed)
            {
                return;
            }

            var previous = _loadCts;
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            previous?.Cancel();
            previous?.Dispose();

            IsLoading = true;
            ProgressPercent = 0;
            ProgressMessage = L("LOCPlayAch_Status_LoadingAchievements");

            try
            {
                var snapshot = await _dataCoordinator.GetSnapshotAsync(_gameId, cts.Token);
                if (cts.IsCancellationRequested || _disposed)
                {
                    return;
                }

                ApplySnapshot(snapshot);
                ProgressPercent = 100;
                ProgressMessage = string.Empty;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to load friend achievements for {_gameId}.");
                StatusText = L("LOCPlayAch_FriendsOverview_LoadFailed");
            }
            finally
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    IsLoading = false;
                }
            }
        }

        public void RefreshView()
        {
            // The window's grid-option surfaces have no flat-name change notifications, so a
            // settings save must re-read the option-backed properties explicitly.
            NotifySettingProperties();
            _dataCoordinator.Invalidate();
            _ = LoadAsync();
        }

        public void ClearFriendSearch()
        {
            FriendSearchText = string.Empty;
        }

        public void Dispose()
        {
            _disposed = true;
            _dataCoordinator.SnapshotInvalidated -= OnSnapshotInvalidated;
            _refreshRuntime.RebuildProgress -= OnRebuildProgress;
            if (_settings != null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                }
            }

            _loadCts?.Cancel();
            _loadCts?.Dispose();
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

        private void ApplySnapshot(FriendsOverviewSnapshot snapshot)
        {
            _allFriends.Clear();
            _allFriends.AddRange((snapshot?.Friends ?? new List<FriendSummaryItem>())
                .Where(friend => friend != null)
                .OrderByDescending(friend => friend.LastUnlockUtc ?? DateTime.MinValue)
                .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase));

            _allAchievements.Clear();
            _allAchievements.AddRange((snapshot?.AllAchievements ?? new List<FriendAchievementDisplayItem>())
                .Where(achievement => achievement?.PlayniteGameId == _gameId));

            _projection = snapshot?.Projection;
            _summaryItem = (snapshot?.Games ?? new List<FriendGameSummaryItem>())
                .FirstOrDefault(game => game?.PlayniteGameId == _gameId);

            if (_summaryItem != null && !string.IsNullOrWhiteSpace(_summaryItem.GameName))
            {
                GameName = _summaryItem.GameName;
            }

            if (SelectedFriend != null)
            {
                SelectedFriend = _allFriends.FirstOrDefault(friend =>
                    FriendOverviewProjection.IsSameFriend(friend, SelectedFriend));
            }

            UpdateSummaryItems();

            _friendSearchIndex.Rebuild(_allFriends);
            _achievementControlBar.UpdateOptions(_allAchievements);
            OnPropertyChanged(nameof(HasAchievements));
            ApplyFilters();
        }

        // Mirrors the Friends Overview game-summary pair: the aggregate all-friends row when no
        // friend is selected, the selected friend's row for this game otherwise.
        private void UpdateSummaryItems()
        {
            var item = GetSelectedFriendGameSummary() ?? _summaryItem;
            SummaryItems.ReplaceAll(item != null
                ? new[] { item }
                : Array.Empty<FriendGameSummaryItem>());
        }

        private FriendGameSummaryItem GetSelectedFriendGameSummary()
        {
            if (SelectedFriend == null || _projection == null)
            {
                return null;
            }

            // The warm-path snapshot reuses the full overview projection, so the per-friend
            // rows must be filtered back down to this window's game.
            return _projection.GetSelectedFriendGames(SelectedFriend)
                .FirstOrDefault(game => game?.PlayniteGameId == _gameId);
        }

        private void ApplyFilters()
        {
            var friendQuery = SearchQuery.From(FriendSearchText);
            var friends = _allFriends
                .Where(friend => _friendSearchIndex.Matches(friend, friendQuery))
                .ToList();
            Friends.ReplaceAll(friends);

            // The game is fixed here, so a selected friend forms a single friend+game pair: show the
            // full comparison list including the friend's locked achievements. The all-friends view is
            // aggregated and shows unlocked rows only.
            IEnumerable<FriendAchievementDisplayItem> source = SelectedFriend != null
                ? _allAchievements.Where(achievement =>
                    FriendOverviewProjection.IsSameFriend(achievement, SelectedFriend))
                : _allAchievements.Where(achievement => achievement?.Unlocked == true);

            var filtered = _achievementControlBar
                .Apply(source)
                .OfType<FriendAchievementDisplayItem>()
                .ToList();
            SortAchievements(filtered);

            var maxRows = AchievementGridOptions?.MaxRows;

            // Keep the unfiltered category-summary source current; achievement filters and grid
            // sorts never touch it, so the category fallback order stays the definition-ordered
            // snapshot loaded from the cache. Replaced before Achievements so the grid's
            // items-source reset rebuilds category rollups from the new selection's rows.
            SelectedFriendAllAchievements.ReplaceAll(SelectedFriend != null
                ? _allAchievements.Where(achievement =>
                    FriendOverviewProjection.IsSameFriend(achievement, SelectedFriend))
                : Enumerable.Empty<FriendAchievementDisplayItem>());
            Achievements.ReplaceAll(DisplayGridRowLimitHelper.Limit(filtered, maxRows));
            StatusText = _allAchievements.Count == 0
                ? L("LOCPlayAch_ViewFriendsAchievements_NoData")
                : string.Empty;
            OnPropertyChanged(nameof(AchievementSectionTitle));
            OnPropertyChanged(nameof(AchievementCountText));
            OnPropertyChanged(nameof(HasStatusText));
        }

        private void SortAchievements(List<FriendAchievementDisplayItem> items)
        {
            if (items == null || items.Count <= 1)
            {
                return;
            }

            var options = AchievementGridOptions;
            var sort = new AchievementSortSpec(
                options?.SortMode ?? CompactListSortMode.UnlockTime,
                (options?.SortDescending ?? true)
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);
            if (!sort.PreservesSourceOrder)
            {
                var stableOrder = AchievementSortHelper.CreateStableOrderMap(items);
                var comparison = AchievementSortHelper.GetComparison(
                    sort.SortMemberPath,
                    sort.Direction,
                    AchievementSortScope.RecentAchievements);
                if (comparison != null)
                {
                    var ordered = items.Cast<AchievementDisplayItem>().ToList();
                    ordered.Sort(AchievementSortHelper.WithStableOrder(comparison, stableOrder));
                    items.Clear();
                    items.AddRange(ordered.OfType<FriendAchievementDisplayItem>());
                    return;
                }
            }

            items.Sort((left, right) =>
            {
                var result = Nullable.Compare(right?.UnlockTimeUtc, left?.UnlockTimeUtc);
                if (result != 0)
                {
                    return result;
                }

                result = string.Compare(left?.FriendName, right?.FriendName, StringComparison.CurrentCultureIgnoreCase);
                return result != 0
                    ? result
                    : string.Compare(left?.DisplayName, right?.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private async Task RefreshAllFriendsForGameAsync()
        {
            await ExecuteRefreshAsync(new RefreshRequest
            {
                Mode = RefreshModeType.FriendsSelectedGame,
                SingleGameId = _gameId
            });
        }

        private async Task RefreshSelectedFriendForGameAsync(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                return;
            }

            if (!FriendsOverviewViewModel.TryBuildSelectedFriendRefreshRequest(
                    null,
                    friend,
                    GetRefreshGameTarget(),
                    out var request))
            {
                return;
            }

            await ExecuteRefreshAsync(request);
        }

        private FriendGameSummaryItem GetRefreshGameTarget()
        {
            return _summaryItem ?? new FriendGameSummaryItem
            {
                PlayniteGameId = _gameId,
                GameName = GameName
            };
        }

        private async Task ExecuteRefreshAsync(RefreshRequest request)
        {
            if (request == null || IsRefreshing)
            {
                return;
            }

            IsRefreshing = true;
            ProgressPercent = 0;
            ProgressMessage = L("LOCPlayAch_FriendsOverview_Refreshing");
            try
            {
                await _refreshEntryPoint.ExecuteAsync(
                    request,
                    new RefreshExecutionPolicy
                    {
                        ValidateAuthentication = true,
                        UseProgressWindow = false,
                        SwallowExceptions = false,
                        ProgressSingleGameId = _gameId
                    });
                _dataCoordinator.Invalidate();
                await LoadAsync();
                ProgressPercent = 100;
                ProgressMessage = L("LOCPlayAch_Status_RefreshComplete");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to refresh friend achievements for {_gameId}.");
                ProgressMessage = string.Format(
                    L("LOCPlayAch_Error_RefreshFailed"),
                    ex.Message);
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            if (report?.Mode?.IsFriendRefreshMode() != true)
            {
                return;
            }

            if (report.CurrentGameId.HasValue &&
                report.CurrentGameId.Value != Guid.Empty &&
                report.CurrentGameId.Value != _gameId)
            {
                return;
            }

            IsRefreshing = _refreshRuntime.IsRebuilding;
            ProgressPercent = Math.Max(0, Math.Min(100, report.PercentComplete));
            if (!string.IsNullOrWhiteSpace(report.Message))
            {
                ProgressMessage = report.Message;
            }

            if (_refreshRuntime.IsFinalProgressReport(report))
            {
                _dataCoordinator.Invalidate();
                _ = LoadAsync();
            }
        }

        private void OnSnapshotInvalidated(object sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => _ = LoadAsync()));
                return;
            }

            _ = LoadAsync();
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            NotifySettingProperties();
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            NotifySettingProperties();
            if (e?.PropertyName == nameof(PersistedSettings.ShowFriendSpoilers) ||
                e?.PropertyName == nameof(PersistedSettings.Friends) ||
                e?.PropertyName == nameof(PersistedSettings.FriendMergeGroups))
            {
                RefreshView();
                return;
            }

            ApplyFilters();
        }

        private void NotifySettingProperties()
        {
            OnPropertyChanged(nameof(FriendSummariesGridRowHeight));
            OnPropertyChanged(nameof(ShowFriendSummariesGridColumnHeaders));
            OnPropertyChanged(nameof(ShowFriendSummariesGridControlBar));
            OnPropertyChanged(nameof(AchievementsGridRowHeight));
            OnPropertyChanged(nameof(ShowAchievementsGridColumnHeaders));
            OnPropertyChanged(nameof(ShowAchievementsGridControlBar));
            OnPropertyChanged(nameof(UseCoverImages));
            OnPropertyChanged(nameof(ShowRarityGlow));
            OnPropertyChanged(nameof(ColorNamesByRarity));
            OnPropertyChanged(nameof(ColorRarityColumnsByRarity));
            OnPropertyChanged(nameof(SummaryUseCoverImages));
            OnPropertyChanged(nameof(SummaryShowCompletionGlow));
            OnPropertyChanged(nameof(SummaryShowMetadataPlatform));
            OnPropertyChanged(nameof(SummaryShowMetadataPlaytime));
            OnPropertyChanged(nameof(SummaryShowMetadataRegion));
            OnPropertyChanged(nameof(SummaryShowColumnHeaders));
            OnPropertyChanged(nameof(SummaryGridRowHeight));
            OnPropertyChanged(nameof(HideCategorySummaryRow));
            OnPropertyChanged(nameof(CategorySummariesShowColumnHeaders));
            OnPropertyChanged(nameof(CategorySummariesGridRowHeight));
            OnPropertyChanged(nameof(CategorySummariesUseCoverImages));
            OnPropertyChanged(nameof(CategorySummariesShowCompletionGlow));
        }

        private void OpenGameInLibrary()
        {
            try
            {
                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                if (game != null)
                {
                    PlayniteUiProvider.RestoreMainView();
                    _playniteApi?.MainView?.SelectGame(_gameId);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to select Playnite game {_gameId}.");
            }
        }

        private string ResolveInitialGameName()
        {
            try
            {
                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                if (!string.IsNullOrWhiteSpace(game?.Name))
                {
                    return game.Name;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to resolve game name for {_gameId}.");
            }

            return L("LOCPlayAch_ViewFriendsAchievements_TitleFallback");
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
