using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using Playnite.SDK;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public class SingleGameControlModel : ObservableObject, IDisposable
    {
        private readonly AchievementService _achievementService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Guid _gameId;
        private Guid? _activeRefreshOperationId;

        // Sort state tracking for quick reverse
        private string _currentSortPath;
        private ListSortDirection _currentSortDirection;

        // Search and filter state
        private List<AchievementDisplayItem> _allAchievements = new List<AchievementDisplayItem>();
        private string _searchText = string.Empty;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _showHidden = true;
        private bool _hasCustomAchievementOrder;

        public SingleGameControlModel(
            Guid gameId,
            AchievementService achievementService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _gameId = gameId;
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;

            Timeline = new TimelineViewModel();
            OnPropertyChanged(nameof(Timeline));

            // Initialize commands
            RevealAchievementCommand = new RelayCommand(param => RevealAchievement(param as AchievementDisplayItem));
            DismissStatusCommand = new RelayCommand(_ => DismissStatus(), _ => CanDismissStatus);

            RefreshGameCommand = new RelayCommand(
                async (param) =>
                {
                    if (IsRefreshing) return;

                    IsRefreshing = true;
                    _activeRefreshOperationId = null;
                    RefreshStatusMessage = ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");
                    IsStatusMessageVisible = true;

                    try
                    {
                        await _achievementService.ExecuteRefreshAsync(RefreshModeType.Single, _gameId);

                        // Load updated data
                        LoadGameData();

                        // Simple success message
                        RefreshStatusMessage = ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to refresh game {_gameId}.");
                        RefreshStatusMessage = string.Format(
                            ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed"),
                            ex.Message);
                    }
                    finally
                    {
                        IsRefreshing = false;
                        _activeRefreshOperationId = null;
                        await Task.Delay(3000);
                        IsStatusMessageVisible = false;
                    }
                },
                _ => !_achievementService.IsRebuilding);

            // Subscribe to settings changes
            if (_settings != null)
            {
                _settings.PropertyChanged += OnSettingsChanged;
            }
            _achievementService.GameCacheUpdated += OnGameCacheUpdated;
            _achievementService.CacheDeltaUpdated += OnCacheDeltaUpdated;
            _achievementService.RebuildProgress += OnRebuildProgress;

            // Load data
            LoadGameData();
        }

        #region Properties

        private string _gameName;
        public string GameName
        {
            get => _gameName;
            private set => SetValue(ref _gameName, value);
        }

        private string _gameIconPath;
        public string GameIconPath
        {
            get => _gameIconPath;
            private set => SetValue(ref _gameIconPath, value);
        }

        private int _totalAchievements;
        public int TotalAchievements
        {
            get => _totalAchievements;
            private set
            {
                if (SetValueAndReturn(ref _totalAchievements, value))
                {
                    OnPropertyChanged(nameof(HasAchievements));
                }
            }
        }

        private int _unlockedAchievements;
        public int UnlockedAchievements
        {
            get => _unlockedAchievements;
            private set => SetValue(ref _unlockedAchievements, value);
        }

        private bool _isCompleted;
        public bool IsCompleted
        {
            get => _isCompleted;
            private set => SetValue(ref _isCompleted, value);
        }

        private int _commonCount;
        public int CommonCount
        {
            get => _commonCount;
            private set => SetValue(ref _commonCount, value);
        }

        private int _uncommonCount;
        public int UncommonCount
        {
            get => _uncommonCount;
            private set => SetValue(ref _uncommonCount, value);
        }

        private int _rareCount;
        public int RareCount
        {
            get => _rareCount;
            private set => SetValue(ref _rareCount, value);
        }

        private int _ultraRareCount;
        public int UltraRareCount
        {
            get => _ultraRareCount;
            private set => SetValue(ref _ultraRareCount, value);
        }

        // Trophy counts for PlayStation games
        private int _trophyPlatinumCount;
        public int TrophyPlatinumCount
        {
            get => _trophyPlatinumCount;
            private set => SetValue(ref _trophyPlatinumCount, value);
        }

        private int _trophyGoldCount;
        public int TrophyGoldCount
        {
            get => _trophyGoldCount;
            private set => SetValue(ref _trophyGoldCount, value);
        }

        private int _trophySilverCount;
        public int TrophySilverCount
        {
            get => _trophySilverCount;
            private set => SetValue(ref _trophySilverCount, value);
        }

        private int _trophyBronzeCount;
        public int TrophyBronzeCount
        {
            get => _trophyBronzeCount;
            private set => SetValue(ref _trophyBronzeCount, value);
        }

        /// <summary>
        /// True if this game has PlayStation trophy type data.
        /// </summary>
        public bool HasTrophyTypes => TrophyPlatinumCount > 0 || TrophyGoldCount > 0 || TrophySilverCount > 0 || TrophyBronzeCount > 0;

        public double Progression => TotalAchievements > 0
            ? (double)UnlockedAchievements / TotalAchievements * 100
            : 0;

        public string ProgressionText => string.Format(ResourceProvider.GetString("LOCPlayAch_Format_Percentage"), Progression);

        public TimelineViewModel Timeline { get; private set; }

        // Achievement list
        public ObservableCollection<AchievementDisplayItem> Achievements { get; } = new ObservableCollection<AchievementDisplayItem>();

        private bool _IsRefreshing;
        public bool IsRefreshing
        {
            get => _IsRefreshing;
            private set => SetValue(ref _IsRefreshing, value);
        }

        private string _RefreshStatusMessage;
        public string RefreshStatusMessage
        {
            get => _RefreshStatusMessage;
            private set => SetValue(ref _RefreshStatusMessage, value);
        }

        private bool _isStatusMessageVisible;
        public bool IsStatusMessageVisible
        {
            get => _isStatusMessageVisible;
            private set
            {
                if (SetValueAndReturn(ref _isStatusMessageVisible, value))
                {
                    OnPropertyChanged(nameof(CanDismissStatus));
                    (DismissStatusCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool CanDismissStatus => IsStatusMessageVisible;

        private bool _isTimelineVisible = false;
        public bool IsTimelineVisible
        {
            get => _isTimelineVisible;
            set => SetValue(ref _isTimelineVisible, value);
        }

        private bool _isStatsVisible = true;
        public bool IsStatsVisible
        {
            get => _isStatsVisible;
            set => SetValue(ref _isStatsVisible, value);
        }

        public bool HasAchievements => TotalAchievements > 0;

        public bool HasCustomAchievementOrder
        {
            get => _hasCustomAchievementOrder;
            private set => SetValue(ref _hasCustomAchievementOrder, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty))
                {
                    ApplySearchFilter();
                }
            }
        }

        public bool ShowUnlocked
        {
            get => _showUnlocked;
            set
            {
                if (SetValueAndReturn(ref _showUnlocked, value))
                {
                    ApplySearchFilter();
                }
            }
        }

        public bool ShowLocked
        {
            get => _showLocked;
            set
            {
                if (SetValueAndReturn(ref _showLocked, value))
                {
                    ApplySearchFilter();
                }
            }
        }

        public bool ShowHidden
        {
            get => _showHidden;
            set
            {
                if (SetValueAndReturn(ref _showHidden, value))
                {
                    ApplySearchFilter();
                }
            }
        }

        #endregion

        #region Commands

        public ICommand RevealAchievementCommand { get; }
        public ICommand RefreshGameCommand { get; }
        public ICommand DismissStatusCommand { get; }

        #endregion

        #region Private Methods

        private void LoadGameData()
        {
            try
            {
                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                if (game == null)
                {
                    _logger?.Warn($"Game not found: {_gameId}");
                    return;
                }

                GameName = game.Name;
                GameIconPath = (!string.IsNullOrEmpty(game.Icon)) ? _playniteApi.Database.GetFullFilePath(game.Icon) : null;

                var gameData = _achievementService.GetGameAchievementData(_gameId);
                if (gameData == null || !gameData.HasAchievements || gameData.Achievements == null)
                {
                    _logger?.Info($"No achievement data for game: {game.Name}");

                    TotalAchievements = 0;
                    UnlockedAchievements = 0;
                    IsCompleted = false;
                    CommonCount = 0;
                    UncommonCount = 0;
                    RareCount = 0;
                    UltraRareCount = 0;
                    _allAchievements = new List<AchievementDisplayItem>();
                    HasCustomAchievementOrder = false;

                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        Achievements.Clear();
                    });

                    OnPropertyChanged(nameof(Progression));
                    OnPropertyChanged(nameof(ProgressionText));

                    Timeline.SetCounts(null);
                    return;
                }

                var achievements = gameData.Achievements;
                var hasCustomOrder = gameData.AchievementOrder != null && gameData.AchievementOrder.Count > 0;
                HasCustomAchievementOrder = hasCustomOrder;
                TotalAchievements = achievements.Count;
                UnlockedAchievements = achievements.Count(a => a.Unlocked);
                IsCompleted = gameData.IsCompleted;

                // Calculate rarity counts
                int common = 0, uncommon = 0, rare = 0, ultraRare = 0;
                // Calculate trophy counts (for PlayStation games)
                int trophyPlatinum = 0, trophyGold = 0, trophySilver = 0, trophyBronze = 0;
                var projectionOptions = AchievementProjectionService.CreateOptions(_settings, gameData);
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
                    if (ach.Unlocked)
                    {
                        if (ach.UnlockTimeUtc.HasValue)
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

                        // Only count rarity if data is available (null means no rarity info for this provider)
                        AchievementProjectionService.AccumulateRarity(ach, ref common, ref uncommon, ref rare, ref ultraRare);
                        AchievementProjectionService.AccumulateTrophy(ach, ref trophyPlatinum, ref trophyGold, ref trophySilver, ref trophyBronze);
                    }

                    var item = AchievementProjectionService.CreateDisplayItem(gameData, ach, projectionOptions, _gameId);
                    if (item != null)
                    {
                        displayItems.Add(item);
                    }
                }

                CommonCount = common;
                UncommonCount = uncommon;
                RareCount = rare;
                UltraRareCount = ultraRare;

                // Set trophy counts
                TrophyPlatinumCount = trophyPlatinum;
                TrophyGoldCount = trophyGold;
                TrophySilverCount = trophySilver;
                TrophyBronzeCount = trophyBronze;

                if (hasCustomOrder)
                {
                    _allAchievements = AchievementOrderHelper.ApplyOrder(
                        displayItems,
                        a => a.ApiName,
                        gameData.AchievementOrder);
                }
                else
                {
                    // Default order: unlocked first by date desc, then locked by rarity.
                    _allAchievements = displayItems
                        .OrderByDescending(a => a.Unlocked)
                        .ThenByDescending(a => a.UnlockTimeUtc ?? DateTime.MinValue)
                        .ThenBy(a => a.GlobalPercentUnlocked ?? 100)
                        .ToList();
                }

                ApplySearchFilter();

                OnPropertyChanged(nameof(Progression));
                OnPropertyChanged(nameof(ProgressionText));

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
            item?.ToggleReveal();
        }

        private void DismissStatus()
        {
            IsStatusMessageVisible = false;
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
            var refreshStatus = _achievementService.GetRefreshStatusSnapshot(report);
            var statusMessage = refreshStatus.Message ?? ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");

            var isForOurGame = report.CurrentGameId.HasValue && report.CurrentGameId.Value == _gameId;
            if (isForOurGame && report.OperationId.HasValue)
            {
                _activeRefreshOperationId = report.OperationId;
            }

            var isTrackedOperation = _activeRefreshOperationId.HasValue &&
                                     report.OperationId.HasValue &&
                                     _activeRefreshOperationId.Value == report.OperationId.Value;

            if (isForOurGame || isTrackedOperation)
            {
                RefreshStatusMessage = statusMessage;
                OnPropertyChanged(nameof(RefreshStatusMessage));
            }
            else
            {
                return;
            }

            // Handle completion
            if (refreshStatus.IsCanceled)
            {
                IsRefreshing = false;
                _activeRefreshOperationId = null;
                RefreshStatusMessage = statusMessage;
                OnPropertyChanged(nameof(IsRefreshing));
                OnPropertyChanged(nameof(RefreshStatusMessage));
            }
            else if (refreshStatus.IsFinal)
            {
                IsRefreshing = false;
                _activeRefreshOperationId = null;
                RefreshStatusMessage = statusMessage;
                OnPropertyChanged(nameof(IsRefreshing));
                OnPropertyChanged(nameof(RefreshStatusMessage));
            }

            (RefreshGameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Persisted.ShowHiddenIcon"
                || e.PropertyName == "Persisted.ShowHiddenTitle"
                || e.PropertyName == "Persisted.ShowHiddenDescription"
                || e.PropertyName == "Persisted")
            {
                var showIcon = _settings.Persisted.ShowHiddenIcon;
                var showTitle = _settings.Persisted.ShowHiddenTitle;
                var showDescription = _settings.Persisted.ShowHiddenDescription;

                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    foreach (var item in Achievements)
                    {
                        item.ShowHiddenIcon = showIcon;
                        item.ShowHiddenTitle = showTitle;
                        item.ShowHiddenDescription = showDescription;
                    }
                });
            }
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
            // Quick reverse if same column
            if (_currentSortPath == sortMemberPath && _currentSortDirection == ListSortDirection.Ascending &&
                direction == ListSortDirection.Descending)
            {
                var items = Achievements.ToList();
                items.Reverse();
                _currentSortDirection = direction;
                CollectionHelper.SynchronizeCollection(Achievements, items);
                return;
            }

            _currentSortPath = sortMemberPath;
            _currentSortDirection = direction;

            Comparison<AchievementDisplayItem> comparison = sortMemberPath switch
            {
                "DisplayName" => (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase),
                "UnlockTime" => CompareAchievementsByUnlockColumn,
                "GlobalPercent" => (a, b) => (a.GlobalPercentUnlocked ?? 100).CompareTo(b.GlobalPercentUnlocked ?? 100),
                "Points" => (a, b) => a.Points.CompareTo(b.Points),
                "TrophyType" => CompareAchievementsByTrophyType,
                _ => null
            };

            if (comparison != null)
            {
                var items = Achievements.ToList();
                if (direction == ListSortDirection.Descending)
                {
                    items.Sort((a, b) => comparison(b, a));
                }
                else
                {
                    items.Sort(comparison);
                }
                CollectionHelper.SynchronizeCollection(Achievements, items);
            }
        }

        private static int CompareAchievementsByUnlockColumn(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var dateComparison = (a.UnlockTimeUtc ?? DateTime.MinValue).CompareTo(b.UnlockTimeUtc ?? DateTime.MinValue);
            if (dateComparison != 0)
            {
                return dateComparison;
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

        private static int CompareAchievementsByTrophyType(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            // Trophy type order: platinum (4) > gold (3) > silver (2) > bronze (1) > none (0)
            int GetTrophyRank(string trophyType)
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

            return GetTrophyRank(a.TrophyType).CompareTo(GetTrophyRank(b.TrophyType));
        }

        private void ApplySearchFilter()
        {
            IEnumerable<AchievementDisplayItem> filtered = _allAchievements;

            if (!ShowHidden)
            {
                filtered = filtered.Where(a => !(a.Hidden && !a.Unlocked));
            }

            filtered = filtered.Where(a => a.Unlocked ? ShowUnlocked : ShowLocked);

            if (!string.IsNullOrEmpty(_searchText))
            {
                filtered = filtered.Where(a =>
                    (a.DisplayName?.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Description?.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                CollectionHelper.SynchronizeCollection(Achievements, filtered.ToList());
            });
        }

        public void ClearSearch()
        {
            SearchText = string.Empty;
        }

        #region IDisposable

        public void Dispose()
        {
            if (_settings != null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
            }
            _achievementService.GameCacheUpdated -= OnGameCacheUpdated;
            _achievementService.CacheDeltaUpdated -= OnCacheDeltaUpdated;
            _achievementService.RebuildProgress -= OnRebuildProgress;
        }

        #endregion
    }
}



