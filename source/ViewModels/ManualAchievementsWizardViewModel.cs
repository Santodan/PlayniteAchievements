using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services;
using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Stages of the manual achievements wizard flow.
    /// </summary>
    public enum WizardStage
    {
        Search,
        Refreshing,
        Editing,
        Completed
    }

    /// <summary>
    /// Unified ViewModel for the manual achievements wizard.
    /// Manages the flow through Search -> Refreshing -> Editing stages.
    /// All functionality is consolidated directly into this ViewModel.
    /// </summary>
    public sealed class ManualAchievementsWizardViewModel : Common.ObservableObject
    {
        private readonly Game _playniteGame;
        private readonly AchievementService _achievementService;
        private readonly IManualSource _source;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action<PlayniteAchievementsSettings> _saveSettings;
        private readonly ILogger _logger;
        private readonly string _language;
        private readonly bool _startAtEditingStage;
        private readonly ManualAchievementLink _existingLink;
        private CancellationTokenSource _refreshCts;
        private CancellationTokenSource _searchCts;

        private WizardStage _currentStage = WizardStage.Search;
        private double _progressPercent;
        private string _progressMessage = string.Empty;
        private bool _canCancelRefresh = true;
        private string _errorMessage = string.Empty;

        // Search stage properties
        private string _searchText = string.Empty;
        private bool _isSearching;
        private ManualGameSearchResult _selectedResult;
        private string _searchStatusMessage = string.Empty;

        // Edit stage properties
        private string _editSearchFilter = string.Empty;
        private string _sourceGameName = string.Empty;

        public event EventHandler RequestClose;

        #region Stage Properties

        public WizardStage CurrentStage
        {
            get => _currentStage;
            private set
            {
                if (_currentStage != value)
                {
                    _currentStage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSearchStage));
                    OnPropertyChanged(nameof(IsRefreshingStage));
                    OnPropertyChanged(nameof(IsEditingStage));
                    OnPropertyChanged(nameof(IsCompletedStage));
                }
            }
        }

        public bool IsSearchStage => CurrentStage == WizardStage.Search;
        public bool IsRefreshingStage => CurrentStage == WizardStage.Refreshing;
        public bool IsEditingStage => CurrentStage == WizardStage.Editing;
        public bool IsCompletedStage => CurrentStage == WizardStage.Completed;

        #endregion

        #region Common Properties

        public string PlayniteGameName => _playniteGame?.Name ?? string.Empty;

        public string WindowTitle =>
            ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Wizard_Title");

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_errorMessage != value)
                {
                    _errorMessage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public bool? DialogResult { get; private set; }

        #endregion

        #region Search Stage Properties

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value ?? string.Empty;
                    OnPropertyChanged();
                    SearchCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsSearching
        {
            get => _isSearching;
            private set
            {
                if (_isSearching != value)
                {
                    _isSearching = value;
                    OnPropertyChanged();
                    SearchCommand.RaiseCanExecuteChanged();
                    NextCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public ObservableCollection<ManualGameSearchResult> SearchResults { get; } =
            new ObservableCollection<ManualGameSearchResult>();

        public ManualGameSearchResult SelectedResult
        {
            get => _selectedResult;
            set
            {
                if (_selectedResult != value)
                {
                    _selectedResult = value;
                    OnPropertyChanged();
                    NextCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string SearchStatusMessage
        {
            get => _searchStatusMessage;
            private set
            {
                if (_searchStatusMessage != value)
                {
                    _searchStatusMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        #region Refresh Stage Properties

        public double ProgressPercent
        {
            get => _progressPercent;
            private set => SetValue(ref _progressPercent, value);
        }

        public string ProgressMessage
        {
            get => _progressMessage;
            private set => SetValue(ref _progressMessage, value);
        }

        public bool CanCancelRefresh
        {
            get => _canCancelRefresh;
            private set => SetValue(ref _canCancelRefresh, value);
        }

        #endregion

        #region Edit Stage Properties

        public string SourceGameName
        {
            get => _sourceGameName;
            private set => SetValue(ref _sourceGameName, value);
        }

        public string SourceGameId { get; private set; }

        public string EditSearchFilter
        {
            get => _editSearchFilter;
            set
            {
                if (_editSearchFilter != value)
                {
                    _editSearchFilter = value ?? string.Empty;
                    OnPropertyChanged();
                    FilterAchievements();
                }
            }
        }

        public ObservableCollection<ManualAchievementEditItem> AllAchievements { get; } =
            new ObservableCollection<ManualAchievementEditItem>();

        public ObservableCollection<ManualAchievementEditItem> FilteredAchievements { get; } =
            new ObservableCollection<ManualAchievementEditItem>();

        public int TotalCount => AllAchievements.Count;

        public int UnlockedCount => AllAchievements.Count(a => a.IsUnlocked);

        public double CompletionPercent =>
            AllAchievements.Count > 0
                ? (double)UnlockedCount / TotalCount * 100.0
                : 0;

        #endregion

        #region Commands

        public RelayCommand SearchCommand { get; }
        public AsyncCommand NextCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand CancelRefreshCommand { get; }
        public ICommand RetryCommand { get; }
        public RelayCommand UnlockAllCommand { get; }
        public RelayCommand LockAllCommand { get; }

        #endregion

        public ManualAchievementsWizardViewModel(
            Game playniteGame,
            AchievementService achievementService,
            IManualSource source,
            PlayniteAchievementsSettings settings,
            Action<PlayniteAchievementsSettings> saveSettings,
            ILogger logger,
            bool startAtEditingStage = false)
        {
            _playniteGame = playniteGame ?? throw new ArgumentNullException(nameof(playniteGame));
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            _logger = logger;
            _language = settings.Persisted.GlobalLanguage ?? "english";
            _startAtEditingStage = startAtEditingStage;

            if (startAtEditingStage)
            {
                if (!settings.Persisted.ManualAchievementLinks.TryGetValue(playniteGame.Id, out _existingLink) || _existingLink == null)
                {
                    throw new ArgumentException("Cannot start at editing stage: no existing link found for game.");
                }
            }

            // Initialize search text with game name for auto-search
            _searchText = startAtEditingStage ? string.Empty : playniteGame.Name;

            // Search commands
            SearchCommand = new RelayCommand(
                async _ => await ExecuteSearchAsync(),
                _ => !IsSearching && !string.IsNullOrWhiteSpace(SearchText));

            // Navigation commands
            NextCommand = new AsyncCommand(_ => TransitionToRefreshingAsync(), _ => CanTransitionToNext());
            SaveCommand = new RelayCommand(_ => Save(), _ => CanSave());
            CancelCommand = new RelayCommand(_ => CloseDialog(false));
            CancelRefreshCommand = new RelayCommand(_ => CancelRefresh());
            RetryCommand = new AsyncCommand(_ => TransitionToRefreshingAsync());

            // Edit commands
            UnlockAllCommand = new RelayCommand(_ => SetAllUnlocked(true));
            LockAllCommand = new RelayCommand(_ => SetAllUnlocked(false));

            if (_startAtEditingStage)
            {
                CurrentStage = WizardStage.Editing;
                LoadEditFromExistingLink();
            }
        }

        #region Search Logic

        private bool CanTransitionToNext()
        {
            return CurrentStage == WizardStage.Search &&
                   SelectedResult != null &&
                   !IsSearching;
        }

        public async Task ExecuteSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchText) || IsSearching)
            {
                return;
            }

            // Cancel any previous search
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();

            var ct = _searchCts.Token;
            IsSearching = true;
            SearchStatusMessage = ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");
            SearchResults.Clear();
            SelectedResult = null;

            try
            {
                var results = await _source.SearchGamesAsync(SearchText.Trim(), _language, ct);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (results == null || results.Count == 0)
                {
                    SearchStatusMessage = ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Search_NoResults");
                    return;
                }

                foreach (var result in results)
                {
                    if (result != null)
                    {
                        SearchResults.Add(result);
                    }
                }

                SearchStatusMessage = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Search_ResultsFormat"),
                    SearchResults.Count);

                // Auto-select first result
                if (SearchResults.Count > 0)
                {
                    SelectedResult = SearchResults[0];
                }
            }
            catch (OperationCanceledException)
            {
                // Search was cancelled
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Manual achievement search failed");
                SearchStatusMessage = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Search_Error"),
                    ex.Message);
            }
            finally
            {
                IsSearching = false;
            }
        }

        public void CancelSearch()
        {
            _searchCts?.Cancel();
        }

        #endregion

        #region Refresh Logic

        private async Task TransitionToRefreshingAsync()
        {
            if (CurrentStage != WizardStage.Search || SelectedResult == null)
            {
                return;
            }

            var selectedResult = SelectedResult;
            CurrentStage = WizardStage.Refreshing;
            ErrorMessage = string.Empty;
            ProgressMessage = ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");
            ProgressPercent = 0;
            CanCancelRefresh = true;

            var link = new ManualAchievementLink
            {
                SourceKey = _source.SourceKey,
                SourceGameId = selectedResult.SourceGameId,
                UnlockTimes = new Dictionary<string, DateTime?>(),
                CreatedUtc = DateTime.UtcNow,
                LastModifiedUtc = DateTime.UtcNow
            };

            SaveLink(link);

            _refreshCts = new CancellationTokenSource();

            try
            {
                _achievementService.RebuildProgress += OnRebuildProgress;

                var request = new RefreshRequest
                {
                    CustomOptions = new CustomRefreshOptions
                    {
                        ProviderKeys = new List<string> { "Manual" },
                        Scope = CustomGameScope.Explicit,
                        IncludeGameIds = new List<Guid> { _playniteGame.Id }
                    }
                };

                await _achievementService.ExecuteRefreshAsync(request);

                if (_refreshCts.Token.IsCancellationRequested)
                {
                    CurrentStage = WizardStage.Search;
                    return;
                }

                TransitionToEditing(link);
            }
            catch (OperationCanceledException)
            {
                CurrentStage = WizardStage.Search;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Manual achievement refresh failed");
                ErrorMessage = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Schema_FetchFailed"),
                    ex.Message);
                CanCancelRefresh = false;
            }
            finally
            {
                _achievementService.RebuildProgress -= OnRebuildProgress;
                _refreshCts?.Dispose();
                _refreshCts = null;
            }
        }

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            if (report.CurrentGameId != _playniteGame.Id)
            {
                return;
            }

            ProgressPercent = report.PercentComplete;
            if (!string.IsNullOrWhiteSpace(report.Message))
            {
                ProgressMessage = report.Message;
            }
        }

        private void CancelRefresh()
        {
            _refreshCts?.Cancel();
            CurrentStage = WizardStage.Search;
        }

        #endregion

        #region Edit Logic

        private void TransitionToEditing(ManualAchievementLink link)
        {
            var cachedData = _achievementService.Cache.LoadGameData(_playniteGame.Id.ToString());
            if (cachedData?.Achievements == null || cachedData.Achievements.Count == 0)
            {
                ErrorMessage = ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Schema_NoAchievements");
                CanCancelRefresh = false;
                CurrentStage = WizardStage.Search;
                return;
            }

            PopulateAchievements(cachedData.Achievements, link);
            SourceGameId = link.SourceGameId;
            SourceGameName = cachedData.GameName ?? link.SourceGameId;

            CurrentStage = WizardStage.Editing;
        }

        private void LoadEditFromExistingLink()
        {
            if (_existingLink == null)
            {
                return;
            }

            var cachedData = _achievementService.Cache.LoadGameData(_playniteGame.Id.ToString());
            if (cachedData?.Achievements == null || cachedData.Achievements.Count == 0)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var achievements = await _source.GetAchievementsAsync(
                            _existingLink.SourceGameId,
                            _language,
                            CancellationToken.None);

                        if (achievements == null || achievements.Count == 0)
                        {
                            ErrorMessage = ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Schema_NoAchievements");
                            return;
                        }

                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            PopulateAchievements(achievements, _existingLink);
                            SourceGameId = _existingLink.SourceGameId;
                            SourceGameName = _existingLink.SourceGameId;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, "Failed to load achievements for existing link");
                        ErrorMessage = string.Format(
                            ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Schema_FetchFailed"),
                            ex.Message);
                    }
                });

                return;
            }

            PopulateAchievements(cachedData.Achievements, _existingLink);
            SourceGameId = _existingLink.SourceGameId;
            SourceGameName = cachedData.GameName ?? _existingLink.SourceGameId;
        }

        private void PopulateAchievements(List<AchievementDetail> achievements, ManualAchievementLink link)
        {
            AllAchievements.Clear();
            FilteredAchievements.Clear();

            if (achievements != null)
            {
                foreach (var detail in achievements)
                {
                    if (detail == null || string.IsNullOrWhiteSpace(detail.ApiName))
                    {
                        continue;
                    }

                    // Get existing unlock state
                    var isUnlocked = false;
                    DateTime? unlockTime = null;

                    if (link?.UnlockTimes != null &&
                        link.UnlockTimes.TryGetValue(detail.ApiName, out var existingTime))
                    {
                        unlockTime = existingTime;
                        isUnlocked = existingTime.HasValue;
                    }

                    var item = new ManualAchievementEditItem(detail, isUnlocked, unlockTime);
                    item.PropertyChanged += OnAchievementChanged;
                    AllAchievements.Add(item);
                }
            }

            FilterAchievements();
            UpdateCounts();
        }

        private void OnAchievementChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ManualAchievementEditItem.IsUnlocked))
            {
                UpdateCounts();
            }
        }

        private void SetAllUnlocked(bool unlocked)
        {
            foreach (var item in AllAchievements)
            {
                if (unlocked)
                {
                    // Set to now if not already unlocked
                    if (!item.IsUnlocked)
                    {
                        item.UnlockTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    item.UnlockTime = null;
                    item.IsUnlocked = false;
                }
            }
        }

        private void FilterAchievements()
        {
            FilteredAchievements.Clear();

            var filter = EditSearchFilter?.Trim().ToLowerInvariant() ?? string.Empty;
            var hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var item in AllAchievements)
            {
                if (!hasFilter ||
                    item.DisplayName?.ToLowerInvariant().Contains(filter) == true ||
                    item.Description?.ToLowerInvariant().Contains(filter) == true ||
                    item.ApiName?.ToLowerInvariant().Contains(filter) == true)
                {
                    FilteredAchievements.Add(item);
                }
            }
        }

        private void UpdateCounts()
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(UnlockedCount));
            OnPropertyChanged(nameof(CompletionPercent));
        }

        private bool CanSave()
        {
            return CurrentStage == WizardStage.Editing && AllAchievements.Count > 0;
        }

        private ManualAchievementLink BuildLink()
        {
            var now = DateTime.UtcNow;

            var link = new ManualAchievementLink
            {
                SourceKey = _source.SourceKey,
                SourceGameId = SourceGameId,
                UnlockTimes = new Dictionary<string, DateTime?>(),
                CreatedUtc = _existingLink?.CreatedUtc ?? now,
                LastModifiedUtc = now
            };

            foreach (var item in AllAchievements)
            {
                link.UnlockTimes[item.ApiName] = item.IsUnlocked ? item.UnlockTime : null;
            }

            return link;
        }

        #endregion

        #region Save/Close Logic

        private void Save()
        {
            if (CurrentStage != WizardStage.Editing)
            {
                return;
            }

            try
            {
                var link = BuildLink();
                SaveLink(link);

                var cachedData = _achievementService.Cache.LoadGameData(_playniteGame.Id.ToString());
                if (cachedData?.Achievements != null)
                {
                    foreach (var achievement in cachedData.Achievements)
                    {
                        if (link.UnlockTimes.TryGetValue(achievement.ApiName, out var unlockTime))
                        {
                            achievement.Unlocked = unlockTime.HasValue;
                            achievement.UnlockTimeUtc = unlockTime;
                        }
                    }
                    _achievementService.Cache.SaveGameData(_playniteGame.Id.ToString(), cachedData);
                    _achievementService.Cache.NotifyCacheInvalidated();
                }

                _logger?.Info($"Saved manual achievement link for '{_playniteGame.Name}' (source={link.SourceKey}, gameId={link.SourceGameId})");

                CurrentStage = WizardStage.Completed;
                CloseDialog(true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to save manual achievements for '{_playniteGame.Name}'");
                ErrorMessage = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Edit_SaveFailed"),
                    ex.Message);
            }
        }

        private void SaveLink(ManualAchievementLink link)
        {
            _settings.Persisted.ManualAchievementLinks[_playniteGame.Id] = link;
            _saveSettings(_settings);
        }

        private void CloseDialog(bool result)
        {
            DialogResult = result;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region Cleanup

        public void Cleanup()
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;

            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;

            if (_achievementService != null)
            {
                _achievementService.RebuildProgress -= OnRebuildProgress;
            }

            // Unsubscribe from achievement item events
            foreach (var item in AllAchievements)
            {
                item.PropertyChanged -= OnAchievementChanged;
            }
        }

        #endregion
    }
}
