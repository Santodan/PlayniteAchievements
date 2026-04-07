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
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using Playnite.SDK;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public class SingleGameControlModel : ObservableObject, IDisposable
    {
        private readonly RefreshRuntime _refreshService;
        private readonly AchievementDataService _achievementDataService;
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
        private readonly HashSet<string> _selectedCategoryTypeFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedCategoryLabelFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public SingleGameControlModel(
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

            Timeline = new TimelineViewModel();
            OnPropertyChanged(nameof(Timeline));

            CategoryTypeFilterOptions = new ObservableCollection<string>();
            CategoryLabelFilterOptions = new ObservableCollection<string>();

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
                        await ExecuteSingleGameRefreshAsync();

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

            // Load data
            LoadGameData();
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

        public int Progression => AchievementCompletionPercentCalculator.ComputeRoundedPercent(UnlockedAchievements, TotalAchievements);

        public string ProgressionText => $"{Progression}%";

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

        public ObservableCollection<string> CategoryTypeFilterOptions { get; }

        public string SelectedCategoryTypeFilterText => GetSelectedFilterText(
            _selectedCategoryTypeFilters,
            CategoryTypeFilterOptions,
            L("LOCPlayAch_GameOptions_Category_TypeSelectorPlaceholder", "Type"));

        public bool IsCategoryTypeFilterSelected(string value)
        {
            return IsFilterSelected(_selectedCategoryTypeFilters, value);
        }

        public void SetCategoryTypeFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedCategoryTypeFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
            ApplySearchFilter();
        }

        public ObservableCollection<string> CategoryLabelFilterOptions { get; }

        public string SelectedCategoryLabelFilterText => GetSelectedFilterText(
            _selectedCategoryLabelFilters,
            CategoryLabelFilterOptions,
            L("LOCPlayAch_GameOptions_Category_Filter_LabelSelectorPlaceholder", "Category"));

        public bool IsCategoryLabelFilterSelected(string value)
        {
            return IsFilterSelected(_selectedCategoryLabelFilters, value);
        }

        public void SetCategoryLabelFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedCategoryLabelFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
            ApplySearchFilter();
        }

        #endregion

        #region Commands

        public ICommand RevealAchievementCommand { get; }
        public ICommand RefreshGameCommand { get; }
        public ICommand DismissStatusCommand { get; }

        #endregion

        #region Private Methods

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

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private void UpdateAchievementFilterOptions(IEnumerable<AchievementDisplayItem> source)
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

            CollectionHelper.SynchronizeCollection(CategoryTypeFilterOptions, typeOptions);
            CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, categoryOptions);

            if (PruneFilterSelections(_selectedCategoryTypeFilters, CategoryTypeFilterOptions))
            {
                OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
            }

            if (PruneFilterSelections(_selectedCategoryLabelFilters, CategoryLabelFilterOptions))
            {
                OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
            }
        }

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

                var gameData = _achievementDataService.GetGameAchievementData(_gameId);
                if (gameData == null || !gameData.HasAchievements)
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
                    UpdateAchievementFilterOptions(null);
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

                var achievements = gameData.Achievements ?? new List<AchievementDetail>();
                var hasCustomOrder = gameData.AchievementOrder != null && gameData.AchievementOrder.Count > 0;
                HasCustomAchievementOrder = hasCustomOrder;
                TotalAchievements = gameData.AchievementCount;
                UnlockedAchievements = gameData.UnlockedCount;
                IsCompleted = gameData.IsCompleted;

                if (achievements.Count == 0)
                {
                    CommonCount = 0;
                    UncommonCount = 0;
                    RareCount = 0;
                    UltraRareCount = 0;
                    _allAchievements = new List<AchievementDisplayItem>();
                    UpdateAchievementFilterOptions(null);
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
                    _allAchievements = AchievementGridSortHelper.CreateDefaultSortedList(
                        displayItems,
                        AchievementGridSortScope.GameAchievements);
                }

                UpdateAchievementFilterOptions(_allAchievements);
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
            var refreshStatus = _refreshService.GetRefreshStatusSnapshot(report);
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
            if (e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                if (_settings?.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                    _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
                }

                ApplyAppearanceSettingsToAchievements();
            }
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (AchievementProjectionService.IsAppearanceSettingPropertyName(e?.PropertyName))
            {
                ApplyAppearanceSettingsToAchievements();
            }
        }

        private void ApplyAppearanceSettingsToAchievements()
        {
            if (_settings?.Persisted == null)
            {
                return;
            }

            var options = AchievementProjectionService.CreateOptions(_settings, null);
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
                    AchievementProjectionService.ApplyAppearanceSettings(item, options);
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
            var items = Achievements.ToList();
            var currentSortDirection = (ListSortDirection?)_currentSortDirection;
            if (!AchievementGridSortHelper.TrySortItems(
                    items,
                    sortMemberPath,
                    direction,
                    AchievementGridSortScope.GameAchievements,
                    ref _currentSortPath,
                    ref currentSortDirection))
            {
                return;
            }

            if (currentSortDirection.HasValue)
            {
                _currentSortDirection = currentSortDirection.Value;
            }

            CollectionHelper.SynchronizeCollection(Achievements, items);
        }

        private void ApplySearchFilter()
        {
            IEnumerable<AchievementDisplayItem> filtered = _allAchievements;

            if (!ShowHidden)
            {
                filtered = filtered.Where(a => !(a.Hidden && !a.Unlocked));
            }

            filtered = filtered.Where(a => a.Unlocked ? ShowUnlocked : ShowLocked);

            if (_selectedCategoryTypeFilters.Count > 0)
            {
                var selectedTypeSet = new HashSet<string>(
                    _selectedCategoryTypeFilters
                        .Select(AchievementCategoryTypeHelper.NormalizeOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(a =>
                    AchievementCategoryTypeHelper.ParseValues(
                            AchievementCategoryTypeHelper.NormalizeOrDefault(a.CategoryType))
                        .Any(selectedTypeSet.Contains));
            }

            if (_selectedCategoryLabelFilters.Count > 0)
            {
                var selectedCategorySet = new HashSet<string>(
                    _selectedCategoryLabelFilters
                        .Select(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(a =>
                    selectedCategorySet.Contains(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.CategoryLabel)));
            }

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
                if (_settings.Persisted != null)
                {
                    _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
                }
            }
            _refreshService.GameCacheUpdated -= OnGameCacheUpdated;
            _refreshService.CacheDeltaUpdated -= OnCacheDeltaUpdated;
            _refreshService.RebuildProgress -= OnRebuildProgress;
        }

        #endregion
    }
}




