using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public class ViewAchievementsViewModel : ObservableObject, IDisposable
    {
        private readonly RefreshRuntime _refreshService;
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly GameSummaryItemBuilder _summaryBuilder;
        private readonly Guid _gameId;
        private Guid? _activeRefreshOperationId;
        private bool _isApplyingTimelineState;

        // Standard refresh-progress UI state (mirrors OverviewViewModel). The progress bar stays
        // visible while a refresh runs, lingers at 100% briefly on completion, then auto-hides.
        private static readonly TimeSpan ProgressHideDelay = TimeSpan.FromSeconds(3);
        private DispatcherTimer _progressHideTimer;
        private bool _refreshInitiated;
        private bool _showCompletedProgress;

        // Sort state tracking for quick reverse
        private string _currentSortPath;
        private ListSortDirection _currentSortDirection;

        // Search and filter state. The control bar (search box, Unlocked/Locked/Hidden
        // toggles, Type/Category filters) and its filter predicate live in the shared adapter.
        private readonly AchievementGridControlBarAdapter _controlBar = new AchievementGridControlBarAdapter();
        private List<AchievementDisplayItem> _allAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _orderedAchievements = new List<AchievementDisplayItem>();
        private List<AchievementDisplayItem> _filteredAchievements = new List<AchievementDisplayItem>();
        private bool _hasCustomAchievementOrder;

        // In-memory sort/filter state for the most recently viewed game. Restored when the
        // window reopens for the same game, overwritten on every close, and reset when a
        // different game is opened. Process-lifetime only; clears on Playnite restart.
        private sealed class GridStateSnapshot
        {
            public Guid GameId;
            public string SortPath;
            public ListSortDirection SortDirection;
            public GridControlBarFilterState Filters;
        }

        private static GridStateSnapshot _lastGridState;

        public ViewAchievementsViewModel(
            Guid gameId,
            RefreshRuntime refreshRuntime,
            AchievementDataService achievementDataService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _gameId = gameId;
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;
            _summaryBuilder = new GameSummaryItemBuilder(_refreshService.Providers, _playniteApi, _logger);

            Timeline = new TimelineViewModel();
            ApplySavedTimelineState();
            Timeline.PropertyChanged += Timeline_PropertyChanged;
            OnPropertyChanged(nameof(Timeline));

            _controlBar.FilterChanged += (_, __) => ApplySearchFilter();

            // Initialize commands
            RevealAchievementCommand = new RelayCommand(param => RevealAchievement(param as AchievementDisplayItem));
            OpenGameInLibraryCommand = new RelayCommand(_ => OpenGameInLibrary());

            _progressHideTimer = new DispatcherTimer { Interval = ProgressHideDelay };
            _progressHideTimer.Tick += OnProgressHideTimerTick;

            RefreshGameCommand = new RelayCommand(
                async (param) =>
                {
                    if (IsRefreshing) return;

                    IsRefreshing = true;
                    _refreshInitiated = true;
                    _activeRefreshOperationId = null;
                    CancelProgressHideTimer(clearCompletedProgress: false);
                    _showCompletedProgress = false;
                    ProgressPercent = 0;
                    ProgressMessage = ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");
                    OnPropertyChanged(nameof(ShowProgress));

                    try
                    {
                        await ExecuteSingleGameRefreshAsync();

                        // Load updated data
                        LoadGameData();

                        // Surface a final snapshot so the bar reaches 100% before auto-hiding.
                        ProgressPercent = 100;
                        ProgressMessage = ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to refresh game {_gameId}.");
                        ProgressMessage = string.Format(
                            ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed"),
                            ex.Message);
                    }
                    finally
                    {
                        IsRefreshing = false;
                        _activeRefreshOperationId = null;

                        // Linger at the final state, then auto-hide (matches Overview behavior).
                        if (_refreshInitiated)
                        {
                            _showCompletedProgress = true;
                            StartProgressHideTimer();
                        }
                        OnPropertyChanged(nameof(ShowProgress));
                    }
                },
                _ => !_refreshService.IsRebuilding);

            // Subscribe to settings changes
            if (_settings != null)
            {
                _settings.PropertyChanged += OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
                }
            }
            _refreshService.GameCacheUpdated += OnGameCacheUpdated;
            _refreshService.CacheDeltaUpdated += OnCacheDeltaUpdated;
            _refreshService.RebuildProgress += OnRebuildProgress;

            // Restore the previous session's sort/filter state for this game (if any) before
            // the initial load so the first display reflects it.
            RestoreGridStateIfMatching();

            // Resolve the game name synchronously so the window title is correct at
            // creation, then load achievement data in the background so the window
            // can render immediately instead of blocking the UI thread on the load.
            GameName = _playniteApi?.Database?.Games?.Get(_gameId)?.Name;
            IsLoading = true;
            Task.Run(() =>
            {
                try
                {
                    LoadGameData();
                }
                finally
                {
                    IsLoading = false;
                }
            });
        }

        private void RestoreGridStateIfMatching()
        {
            var snapshot = _lastGridState;
            if (snapshot == null || snapshot.GameId != _gameId)
            {
                return;
            }

            // Restore silently to avoid triggering ApplySearchFilter repeatedly;
            // LoadGameData applies the combined state once.
            _currentSortPath = snapshot.SortPath;
            _currentSortDirection = snapshot.SortDirection;
            _controlBar.RestoreState(snapshot.Filters, raiseChanged: false);
        }

        private void SaveGridState()
        {
            _lastGridState = new GridStateSnapshot
            {
                GameId = _gameId,
                SortPath = _currentSortPath,
                SortDirection = _currentSortDirection,
                Filters = _controlBar.CaptureState(),
            };
        }

        private async Task ExecuteSingleGameRefreshAsync()
        {
            var coordinator = PlayniteAchievementsPlugin.Instance?.RefreshEntryPoint;
            if (coordinator == null)
            {
                throw new InvalidOperationException("RefreshEntryPoint is not available.");
            }

            await coordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = _gameId
                },
                new RefreshExecutionPolicy
                {
                    ValidateAuthentication = true,
                    UseProgressWindow = false,
                    SwallowExceptions = false
                });
        }

        #region Properties

        private string _gameName;
        public string GameName
        {
            get => _gameName;
            private set => SetValue(ref _gameName, value);
        }

        // Category the achievement grid is currently drilled into (null when not drilled), pushed
        // up from AchievementDataGridControl so a breadcrumb header can be shown above the grid.
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

        private int _totalAchievements;
        public int TotalAchievements
        {
            get => _totalAchievements;
            private set
            {
                if (SetValueAndReturn(ref _totalAchievements, value))
                {
                    OnPropertyChanged(nameof(HasAchievements));
                    OnPropertyChanged(nameof(ShowNoAchievementsPlaceholder));
                }
            }
        }

        public TimelineViewModel Timeline { get; private set; }

        // Achievement list
        public ObservableCollection<AchievementDisplayItem> Achievements { get; } = new BulkObservableCollection<AchievementDisplayItem>();

        // Unfiltered achievements in canonical definition/custom order; feeds the grid's
        // CategorySummarySource so category ordering does not follow the configured or live sort.
        public ObservableCollection<AchievementDisplayItem> AllAchievements { get; } = new BulkObservableCollection<AchievementDisplayItem>();

        // Single-row game summary grid (standardized header surface).
        public ObservableCollection<GameSummaryItem> SummaryItems { get; } = new ObservableCollection<GameSummaryItem>();

        public bool SummaryUseCoverImages => _settings?.Persisted?.ViewAchievementsGameSummariesUseCoverImages ?? false;

        public bool SummaryShowMetadataPlatform => _settings?.Persisted?.ViewAchievementsGameSummariesShowMetadataPlatform ?? true;

        public bool SummaryShowMetadataPlaytime => _settings?.Persisted?.ViewAchievementsGameSummariesShowMetadataPlaytime ?? true;

        public bool SummaryShowMetadataRegion => _settings?.Persisted?.ViewAchievementsGameSummariesShowMetadataRegion ?? true;

        public bool SummaryShowCompletionGlow => _settings?.Persisted?.ViewAchievementsGameSummariesShowCompletionGlow ?? true;

        public bool SummaryShowColumnHeaders => _settings?.Persisted?.ShowViewAchievementsGameSummariesGridColumnHeaders ?? true;

        public double? SummaryGridRowHeight => _settings?.Persisted?.ViewAchievementsGameSummariesGridRowHeight;

        private bool _IsRefreshing;
        public bool IsRefreshing
        {
            get => _IsRefreshing;
            private set => SetValue(ref _IsRefreshing, value);
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            private set => SetValue(ref _progressPercent, value);
        }

        private string _progressMessage;
        public string ProgressMessage
        {
            get => _progressMessage;
            private set => SetValue(ref _progressMessage, value);
        }

        public bool ShowProgress => _refreshInitiated || IsRefreshing || _showCompletedProgress;

        private bool _isTimelineVisible = false;
        public bool IsTimelineVisible
        {
            get => _isTimelineVisible;
            set
            {
                if (SetValueAndReturn(ref _isTimelineVisible, value))
                {
                    PersistTimelineVisibility(value);
                }
            }
        }

        public bool HasAchievements => TotalAchievements > 0;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetValueAndReturn(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(ShowNoAchievementsPlaceholder));
                }
            }
        }

        // Keeps the "no achievements" placeholder from flashing while the initial
        // background load is still running.
        public bool ShowNoAchievementsPlaceholder => !HasAchievements && !IsLoading;

        public bool HasCustomAchievementOrder
        {
            get => _hasCustomAchievementOrder;
            private set => SetValue(ref _hasCustomAchievementOrder, value);
        }

        public string CurrentSortPath => _currentSortPath;

        public ListSortDirection? CurrentSortDirection =>
            string.IsNullOrWhiteSpace(_currentSortPath)
                ? (ListSortDirection?)null
                : _currentSortDirection;

        public GridControlBarViewModel AchievementsControlBar => _controlBar.ControlBar;

        public bool ShowAchievementGridControlBar => _settings?.Persisted?.ShowViewAchievementsAchievementGridControlBar ?? true;

        public bool HideCategorySummaryRow => _settings?.Persisted?.ViewAchievementsAchievementGridHideCategorySummaryRow ?? false;

        public bool CategorySummariesShowColumnHeaders => _settings?.Persisted?.ShowViewAchievementsCategorySummariesGridColumnHeaders ?? true;

        public double? CategorySummariesGridRowHeight => _settings?.Persisted?.ViewAchievementsCategorySummariesGridRowHeight;

        public bool CategorySummariesUseCoverImages => _settings?.Persisted?.ViewAchievementsCategorySummariesUseCoverImages ?? false;

        public bool CategorySummariesShowCompletionGlow => _settings?.Persisted?.ViewAchievementsCategorySummariesShowCompletionGlow ?? true;

        public double? SingleGameGridRowHeight => _settings?.Persisted?.SingleGameGridRowHeight;

        // The Manage Achievements window follows the Overview "Selected Game Achievements" glow setting.
        public bool ShowRarityGlow => _settings?.Persisted?.OverviewSelectedGameShowRarityGlow ?? true;

        // The Manage Achievements window follows the Overview "Selected Game Achievements" name-color setting.
        public bool ColorNamesByRarity => _settings?.Persisted?.OverviewSelectedGameColorNamesByRarity ?? false;

        public bool ColorRarityColumnsByRarity => _settings?.Persisted?.ViewAchievementsAchievementGridColorRarityColumnsByRarity ?? false;

        #endregion

        #region Commands

        public ICommand RevealAchievementCommand { get; }
        public ICommand RefreshGameCommand { get; }
        public ICommand OpenGameInLibraryCommand { get; }

        #endregion

        #region Private Methods

        private void Timeline_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isApplyingTimelineState ||
                e?.PropertyName != nameof(TimelineViewModel.TimelineRange))
            {
                return;
            }

            PersistTimelineRange();
        }

        private void ApplySavedTimelineState()
        {
            var persisted = _settings?.Persisted;
            var range = persisted?.ViewAchievementsTimelineRange ?? TimelineRange.OneYear;
            var isVisible = persisted?.ViewAchievementsTimelineVisible ?? false;

            try
            {
                _isApplyingTimelineState = true;

                if (_isTimelineVisible != isVisible)
                {
                    _isTimelineVisible = isVisible;
                    OnPropertyChanged(nameof(IsTimelineVisible));
                }

                if (Timeline != null && Timeline.TimelineRange != range)
                {
                    Timeline.TimelineRange = range;
                }
            }
            finally
            {
                _isApplyingTimelineState = false;
            }
        }

        private void PersistTimelineRange()
        {
            if (_isApplyingTimelineState || _settings?.Persisted == null || Timeline == null)
            {
                return;
            }

            if (_settings.Persisted.ViewAchievementsTimelineRange == Timeline.TimelineRange)
            {
                return;
            }

            _settings.Persisted.ViewAchievementsTimelineRange = Timeline.TimelineRange;
            PersistSettingsForUi();
        }

        private void PersistTimelineVisibility(bool isVisible)
        {
            if (_isApplyingTimelineState || _settings?.Persisted == null)
            {
                return;
            }

            if (_settings.Persisted.ViewAchievementsTimelineVisible == isVisible)
            {
                return;
            }

            _settings.Persisted.ViewAchievementsTimelineVisible = isVisible;
            PersistSettingsForUi();
        }

        private void PersistSettingsForUi()
        {
            try
            {
                PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to persist view achievements timeline settings.");
            }
        }

        private void LoadGameData()
        {
            using (PerfScope.Start(_logger, "ViewAchievements.LoadGameData", thresholdMs: 100))
            {
                LoadGameDataCore();
            }
        }

        private void LoadGameDataCore()
        {
            try
            {
                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                if (game == null)
                {
                    _logger?.Warn($"Game not found: {_gameId}");
                    UpdateSummaryItem(null, null);
                    return;
                }

                GameName = game.Name;

                var gameData = _achievementDataService.GetVisibleGameAchievementData(_gameId);
                UpdateSummaryItem(game, gameData);
                if (gameData == null || !gameData.HasAchievements || gameData.Achievements == null)
                {
                    _logger?.Info($"No achievement data for game: {game.Name}");

                    TotalAchievements = 0;
                    _allAchievements = new List<AchievementDisplayItem>();
                    _orderedAchievements = new List<AchievementDisplayItem>();
                    _filteredAchievements = new List<AchievementDisplayItem>();
                    HasCustomAchievementOrder = false;

                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        // The control bar's filter option collections are UI-bound.
                        _controlBar.Clear();
                        Achievements.Clear();
                        AllAchievements.Clear();
                    });

                    Timeline.SetCounts(null);
                    return;
                }

                var achievements = gameData.Achievements;
                var hasCustomOrder = gameData.AchievementOrder != null && gameData.AchievementOrder.Count > 0;
                HasCustomAchievementOrder = hasCustomOrder;
                TotalAchievements = achievements.Count;

                var displayItems = new List<AchievementDisplayItem>();
                var unlockCounts = new Dictionary<DateTime, int>();

                IEnumerable<AchievementDetail> projectionOrder = achievements;
                if (hasCustomOrder)
                {
                    projectionOrder = AchievementOrderHelper.ApplyOrder(
                        achievements,
                        a => a.ApiName,
                        gameData.AchievementOrder);
                }

                foreach (var ach in projectionOrder)
                {
                    if (ach.Unlocked && ach.UnlockTimeUtc.HasValue)
                    {
                        var date = DateTimeUtilities.AsUtcKind(ach.UnlockTimeUtc.Value).Date;
                        if (unlockCounts.TryGetValue(date, out var existing))
                        {
                            unlockCounts[date] = existing + 1;
                        }
                        else
                        {
                            unlockCounts[date] = 1;
                        }
                    }

                    var item = AchievementDisplayItem.Create(gameData, ach, _settings, playniteGameIdOverride: _gameId);
                    if (item != null)
                    {
                        displayItems.Add(item);
                    }
                }

                _allAchievements = displayItems;
                RefreshOrderedAchievements(skipDefaultSort: false);

                // The control bar's filter option collections are UI-bound.
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _controlBar.UpdateOptions(_allAchievements);
                    CollectionHelper.Replace(AllAchievements, _allAchievements);
                });
                ApplySearchFilter();

                Timeline.SetCounts(unlockCounts);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to load game data for {_gameId}");
                HasCustomAchievementOrder = false;
            }
        }

        private void RevealAchievement(AchievementDisplayItem item)
        {
            if (item == null)
            {
                return;
            }

            item.ToggleReveal();
        }

        private void OpenGameInLibrary()
        {
            try
            {
                PlayniteUiProvider.RestoreMainView();
                _playniteApi?.MainView?.SelectGame(_gameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to open game in Playnite library: {_gameId}");
            }
        }

        // Builds the single-row game summary that replaces the legacy header/stats cards.
        private void UpdateSummaryItem(Playnite.SDK.Models.Game game, GameAchievementData gameData)
        {
            GameSummaryItem item = null;

            if (gameData != null)
            {
                if (gameData.Game == null)
                {
                    gameData.Game = game;
                }

                item = _summaryBuilder.Build(gameData, _settings, allowEmpty: true);
            }
            else if (game != null)
            {
                var stub = new GameAchievementData
                {
                    GameName = game.Name,
                    PlayniteGameId = _gameId,
                    Game = game,
                    HasAchievements = false,
                    Achievements = new List<AchievementDetail>()
                };
                item = _summaryBuilder.Build(stub, _settings, allowEmpty: true);
            }

            var items = item != null
                ? new List<GameSummaryItem> { item }
                : new List<GameSummaryItem>();

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                CollectionHelper.SynchronizeCollection(SummaryItems, items));
        }

        private void RaiseSummaryAppearanceProperties()
        {
            OnPropertyChanged(nameof(SummaryUseCoverImages));
            OnPropertyChanged(nameof(SummaryShowMetadataPlatform));
            OnPropertyChanged(nameof(SummaryShowMetadataPlaytime));
            OnPropertyChanged(nameof(SummaryShowMetadataRegion));
            OnPropertyChanged(nameof(SummaryShowCompletionGlow));
            OnPropertyChanged(nameof(SummaryShowColumnHeaders));
            OnPropertyChanged(nameof(SummaryGridRowHeight));
        }

        private void OnGameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            if (e.GameId == _gameId.ToString())
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(LoadGameData);
            }
        }

        private void OnCacheDeltaUpdated(object sender, CacheDeltaEventArgs e)
        {
            if (e?.IsFullReset != true)
            {
                return;
            }

            System.Windows.Application.Current?.Dispatcher?.Invoke(LoadGameData);
        }

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            if (report == null) return;

            var isForOurGame = report.CurrentGameId.HasValue && report.CurrentGameId.Value == _gameId;
            if (isForOurGame && report.OperationId.HasValue)
            {
                _activeRefreshOperationId = report.OperationId;
            }

            var isTrackedOperation = _activeRefreshOperationId.HasValue &&
                                     report.OperationId.HasValue &&
                                     _activeRefreshOperationId.Value == report.OperationId.Value;

            // Only this game's refresh drives the progress bar; ignore unrelated reports.
            if (!isForOurGame && !isTrackedOperation)
            {
                return;
            }

            var refreshStatus = _refreshService.GetRefreshStatusSnapshot(report);
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                try
                {
                    ApplyRefreshStatus(refreshStatus);
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Progress UI update error: {ex.Message}");
                }
            }));
        }

        private void ApplyRefreshStatus(RefreshStatusSnapshot status)
        {
            if (status == null)
            {
                return;
            }

            ProgressPercent = status.ProgressPercent;
            ProgressMessage = status.Message ?? ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");

            var isComplete = status.IsCanceled || status.IsFinal || !status.IsRefreshing;
            if (!isComplete)
            {
                IsRefreshing = true;
                _refreshInitiated = true;
                CancelProgressHideTimer(clearCompletedProgress: false);
                _showCompletedProgress = false;
            }
            else if (_refreshInitiated)
            {
                IsRefreshing = false;
                _activeRefreshOperationId = null;
                _showCompletedProgress = true;
                StartProgressHideTimer();
            }
            else
            {
                IsRefreshing = false;
                _showCompletedProgress = false;
            }

            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(ShowProgress));
            (RefreshGameCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
                _refreshInitiated = false;
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
            _refreshInitiated = false;
            if (_showCompletedProgress)
            {
                _showCompletedProgress = false;
                OnPropertyChanged(nameof(ShowProgress));
            }
        }

        private void OnSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                if (_settings?.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                    _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
                }

                ApplyAppearanceSettingsToAchievements();
                OnPropertyChanged(nameof(SingleGameGridRowHeight));
                OnPropertyChanged(nameof(ShowAchievementGridControlBar));
                OnPropertyChanged(nameof(HideCategorySummaryRow));
                OnPropertyChanged(nameof(CategorySummariesShowColumnHeaders));
                OnPropertyChanged(nameof(CategorySummariesGridRowHeight));
                OnPropertyChanged(nameof(CategorySummariesUseCoverImages));
                OnPropertyChanged(nameof(CategorySummariesShowCompletionGlow));
                OnPropertyChanged(nameof(ColorRarityColumnsByRarity));
                RaiseSummaryAppearanceProperties();
                ApplySavedTimelineState();
                ApplySearchFilter(skipDefaultSort: CurrentSortDirection.HasValue, refreshOrder: true);
            }
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (AchievementDisplayItem.IsAppearanceSettingPropertyName(e?.PropertyName))
            {
                ApplyAppearanceSettingsToAchievements();
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.SingleGameGridRowHeight))
            {
                OnPropertyChanged(nameof(SingleGameGridRowHeight));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ShowViewAchievementsAchievementGridControlBar))
            {
                OnPropertyChanged(nameof(ShowAchievementGridControlBar));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ViewAchievementsAchievementGridHideCategorySummaryRow))
            {
                OnPropertyChanged(nameof(HideCategorySummaryRow));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ShowViewAchievementsCategorySummariesGridColumnHeaders))
            {
                OnPropertyChanged(nameof(CategorySummariesShowColumnHeaders));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ViewAchievementsCategorySummariesGridRowHeight))
            {
                OnPropertyChanged(nameof(CategorySummariesGridRowHeight));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ViewAchievementsCategorySummariesUseCoverImages))
            {
                OnPropertyChanged(nameof(CategorySummariesUseCoverImages));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ViewAchievementsCategorySummariesShowCompletionGlow))
            {
                OnPropertyChanged(nameof(CategorySummariesShowCompletionGlow));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.OverviewSelectedGameShowRarityGlow))
            {
                OnPropertyChanged(nameof(ShowRarityGlow));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.OverviewSelectedGameColorNamesByRarity))
            {
                OnPropertyChanged(nameof(ColorNamesByRarity));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ViewAchievementsAchievementGridColorRarityColumnsByRarity))
            {
                OnPropertyChanged(nameof(ColorRarityColumnsByRarity));
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.SingleGameGridMaxRows))
            {
                SyncAchievementsDisplay();
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ViewAchievementsGameSummariesUseCoverImages) ||
                e?.PropertyName == nameof(PersistedSettings.ViewAchievementsGameSummariesShowMetadataPlatform) ||
                e?.PropertyName == nameof(PersistedSettings.ViewAchievementsGameSummariesShowMetadataPlaytime) ||
                e?.PropertyName == nameof(PersistedSettings.ViewAchievementsGameSummariesShowMetadataRegion) ||
                e?.PropertyName == nameof(PersistedSettings.ViewAchievementsGameSummariesShowCompletionGlow) ||
                e?.PropertyName == nameof(PersistedSettings.ShowViewAchievementsGameSummariesGridColumnHeaders) ||
                e?.PropertyName == nameof(PersistedSettings.ViewAchievementsGameSummariesGridRowHeight))
            {
                RaiseSummaryAppearanceProperties();
                return;
            }

            if (e?.PropertyName == nameof(PersistedSettings.ViewAchievementsTimelineRange) ||
                e?.PropertyName == nameof(PersistedSettings.ViewAchievementsTimelineVisible))
            {
                ApplySavedTimelineState();
                return;
            }

            if (!CurrentSortDirection.HasValue &&
                AchievementSortHelper.IsConfiguredDefaultSortPropertyName(
                    e?.PropertyName,
                    AchievementSortSurface.SingleGame))
            {
                ApplySearchFilter(refreshOrder: true);
            }
        }

        private void ApplyAppearanceSettingsToAchievements()
        {
            if (_settings?.Persisted == null)
            {
                return;
            }

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                var items = new HashSet<AchievementDisplayItem>();
                foreach (var item in _allAchievements)
                {
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }

                foreach (var item in Achievements)
                {
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }

                foreach (var item in items)
                {
                    item.ApplyAppearanceSettings(_settings);
                }
            });
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Refreshes the game data display. Called when settings are saved or when cache is updated.
        /// </summary>
        public void RefreshView()
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(LoadGameData);
        }

        #endregion

        public void SortDataGrid(string sortMemberPath, ListSortDirection direction)
        {
            var items = _orderedAchievements.Count == _allAchievements.Count
                ? _orderedAchievements.ToList()
                : _allAchievements.ToList();
            var currentSortDirection = (ListSortDirection?)_currentSortDirection;
            if (!AchievementSortHelper.TrySortItems(
                    items,
                    sortMemberPath,
                    direction,
                    AchievementSortScope.GameAchievements,
                    ref _currentSortPath,
                    ref currentSortDirection))
            {
                return;
            }

            if (currentSortDirection.HasValue)
            {
                _currentSortDirection = currentSortDirection.Value;
            }

            _orderedAchievements = items;
            ApplySearchFilter();
        }

        private void RefreshOrderedAchievements(bool skipDefaultSort)
        {
            var items = _allAchievements.ToList();

            if (CurrentSortDirection.HasValue)
            {
                var currentSortDirection = CurrentSortDirection;
                AchievementSortHelper.TrySortItems(
                    items,
                    _currentSortPath,
                    currentSortDirection.Value,
                    AchievementSortScope.GameAchievements,
                    ref _currentSortPath,
                    ref currentSortDirection);
            }
            else if (!skipDefaultSort)
            {
                AchievementSortHelper.ApplyConfiguredDefaultSort(
                    items,
                    _settings?.Persisted,
                    AchievementSortSurface.SingleGame,
                    AchievementSortScope.GameAchievements,
                    stableOrder: AchievementSortHelper.CreateStableOrderMap(items));
            }

            _orderedAchievements = items;
        }

        private void ApplySearchFilter(bool skipDefaultSort = false, bool refreshOrder = false)
        {
            if (refreshOrder || _orderedAchievements.Count != _allAchievements.Count)
            {
                RefreshOrderedAchievements(skipDefaultSort);
            }

            // The shared control bar owns the filter predicate (search, Unlocked/Locked/Hidden,
            // Type/Category). This VM keeps ordering, row limiting, and display sync.
            var filtered = _controlBar.Apply(_orderedAchievements);

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                _filteredAchievements = filtered.ToList();
                SyncAchievementsDisplay();
            });
        }

        public void ResetSortToDefault()
        {
            _currentSortPath = null;
            _currentSortDirection = AchievementSortHelper.GetConfiguredDefaultSort(
                _settings?.Persisted,
                AchievementSortSurface.SingleGame).Direction;
            ApplySearchFilter(skipDefaultSort: true, refreshOrder: true);
        }

        private void SyncAchievementsDisplay()
        {
            var displayItems = DisplayGridRowLimitHelper.Limit(
                _filteredAchievements,
                _settings?.Persisted?.SingleGameGridMaxRows);
            CollectionHelper.Replace(Achievements, displayItems);
        }

        #region IDisposable

        public void Dispose()
        {
            SaveGridState();

            if (_settings != null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                }
            }
            _refreshService.GameCacheUpdated -= OnGameCacheUpdated;
            _refreshService.CacheDeltaUpdated -= OnCacheDeltaUpdated;
            _refreshService.RebuildProgress -= OnRebuildProgress;
            if (_progressHideTimer != null)
            {
                _progressHideTimer.Stop();
                _progressHideTimer.Tick -= OnProgressHideTimerTick;
                _progressHideTimer = null;
            }
            if (Timeline != null)
            {
                Timeline.PropertyChanged -= Timeline_PropertyChanged;
            }
        }

        #endregion
    }
}





