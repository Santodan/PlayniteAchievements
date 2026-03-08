using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    public sealed class ManualAchievementsViewModel : Common.ObservableObject
    {
        private readonly Game _playniteGame;
        private readonly AchievementService _achievementService;
        private IManualSource _source;
        private readonly IReadOnlyList<IManualSource> _availableSources;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action<PlayniteAchievementsSettings> _saveSettings;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _language;
        private readonly bool _startAtEditingStage;
        private readonly ManualAchievementLink _existingLink;
        private CancellationTokenSource _refreshCts;
        private CancellationTokenSource _searchCts;
        private ManualAchievementLink _lastSavedLink;
        private List<InheritedUnlockEntry> _pendingInheritedUnlocks;

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
        private IManualSource _selectedSource;

        // Edit stage properties
        private string _editSearchFilter = string.Empty;
        private string _sourceGameName = string.Empty;
        private string _manualSourceName = string.Empty;
        private string _saveStatusMessage = string.Empty;

        private sealed class InheritedUnlockEntry
        {
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public bool IsUnlocked { get; set; }
            public DateTime? UnlockTimeUtc { get; set; }
        }

        public event EventHandler ManualLinkSaved;

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

        /// <summary>
        /// Gets the list of available manual sources for selection.
        /// </summary>
        public IReadOnlyList<IManualSource> AvailableSources => _availableSources;

        /// <summary>
        /// Gets or sets the currently selected manual source.
        /// Changing this updates the active source for search operations and triggers a new search.
        /// </summary>
        public IManualSource SelectedSource
        {
            get => _selectedSource;
            set
            {
                if (_selectedSource != value)
                {
                    _logger?.Debug($"[ManualTracking] SelectedSource changing from {_selectedSource?.SourceKey} to {value?.SourceKey}");
                    _selectedSource = value;
                    _source = value;
                    ManualSourceName = ResolveSourceName(_source?.SourceKey);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedSourceKey));
                    OnPropertyChanged(nameof(ManualSourceName));
                    OnPropertyChanged(nameof(ShowPlatformColumn));

                    // Clear search results and trigger new search when source changes
                    SearchResults.Clear();
                    SelectedResult = null;

                    // Auto-search if there's search text
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
                        _ = ExecuteSearchAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Gets or sets the selected source by key. Used for reliable ComboBox binding.
        /// </summary>
        public string SelectedSourceKey
        {
            get => _source?.SourceKey;
            set
            {
                if (_source?.SourceKey != value)
                {
                    var newSource = _availableSources?.FirstOrDefault(s => s?.SourceKey == value);
                    if (newSource != null)
                    {
                        SelectedSource = newSource;
                    }
                }
            }
        }

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

        public string SaveStatusMessage
        {
            get => _saveStatusMessage;
            private set
            {
                if (_saveStatusMessage != value)
                {
                    _saveStatusMessage = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSaveStatus));
                }
            }
        }

        public bool HasSaveStatus => !string.IsNullOrWhiteSpace(SaveStatusMessage);

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

        public string ManualSourceName
        {
            get => _manualSourceName;
            private set => SetValue(ref _manualSourceName, value);
        }

        /// <summary>
        /// Gets whether the platform column should be visible in search results.
        /// Only Exophase provides platform information.
        /// </summary>
        public bool ShowPlatformColumn => _source?.SourceKey == "Exophase";

        private string _sourceGameId;

        public string SourceGameId
        {
            get => _sourceGameId;
            private set
            {
                if (_sourceGameId != value)
                {
                    _sourceGameId = value;
                    OnPropertyChanged();
                }
            }
        }

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
        public RelayCommand RevealAchievementCommand { get; }

        #endregion

        public ManualAchievementsViewModel(
            Game playniteGame,
            AchievementService achievementService,
            IManualSource source,
            PlayniteAchievementsSettings settings,
            Action<PlayniteAchievementsSettings> saveSettings,
            ILogger logger,
            IPlayniteAPI playniteApi,
            bool startAtEditingStage = false)
            : this(playniteGame, achievementService, new[] { source }, source, settings, saveSettings, logger, playniteApi, startAtEditingStage)
        {
        }

        public ManualAchievementsViewModel(
            Game playniteGame,
            AchievementService achievementService,
            IEnumerable<IManualSource> availableSources,
            IManualSource initialSource,
            PlayniteAchievementsSettings settings,
            Action<PlayniteAchievementsSettings> saveSettings,
            ILogger logger,
            IPlayniteAPI playniteApi,
            bool startAtEditingStage = false)
        {
            _playniteGame = playniteGame ?? throw new ArgumentNullException(nameof(playniteGame));
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _availableSources = availableSources?.ToList().AsReadOnly() ?? throw new ArgumentNullException(nameof(availableSources));
            _source = initialSource ?? throw new ArgumentNullException(nameof(initialSource));
            _selectedSource = _source;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            _logger = logger;
            _playniteApi = playniteApi;
            _language = settings.Persisted.GlobalLanguage ?? "english";
            _startAtEditingStage = startAtEditingStage;
            ManualSourceName = ResolveSourceName(_source?.SourceKey);

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
            CancelCommand = new RelayCommand(_ => CancelOrClose());
            CancelRefreshCommand = new RelayCommand(_ => CancelRefresh());
            RetryCommand = new AsyncCommand(_ => TransitionToRefreshingAsync());

            // Edit commands
            UnlockAllCommand = new RelayCommand(_ => SetAllUnlocked(true));
            LockAllCommand = new RelayCommand(_ => SetAllUnlocked(false));
            RevealAchievementCommand = new RelayCommand(param => RevealAchievement(param as ManualAchievementEditItem));

            if (_startAtEditingStage)
            {
                // Start at refreshing stage to ensure cache is populated via provider
                CurrentStage = WizardStage.Refreshing;
                _ = RefreshExistingAndTransitionToEditAsync();
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
            EnsureManualProviderEnabledForLinking();

            var precheckHasAchievements = await SelectedResultHasAchievementsAsync(selectedResult);

            _pendingInheritedUnlocks = CaptureInheritedUnlocksFromCurrentProvider();

            var link = new ManualAchievementLink
            {
                SourceKey = _source.SourceKey,
                SourceGameId = selectedResult.SourceGameId,
                UnlockTimes = new Dictionary<string, DateTime?>(),
                UnlockStates = new Dictionary<string, bool>(),
                CreatedUtc = DateTime.UtcNow,
                LastModifiedUtc = DateTime.UtcNow
            };
            SeedLinkUnlocksFromInheritedSnapshot(link, _pendingInheritedUnlocks);

            ManualAchievementLink existingLink = null;
            var hadExistingLink = _settings?.Persisted?.ManualAchievementLinks != null &&
                                  _settings.Persisted.ManualAchievementLinks.TryGetValue(_playniteGame.Id, out existingLink);
            var rollbackLink = existingLink?.Clone();
            var rollbackCacheData = _achievementService?.Cache?.LoadGameData(_playniteGame.Id.ToString());
            var rollbackPending = true;

            SetLinkInMemory(link);

            _refreshCts = new CancellationTokenSource();

            try
            {
                _achievementService.RebuildProgress += OnRebuildProgress;

                var request = new RefreshRequest
                {
                    GameIds = new List<Guid> { _playniteGame.Id }
                };

                await _achievementService.ExecuteRefreshAsync(request);

                if (_refreshCts.Token.IsCancellationRequested)
                {
                    RollbackTransientLink(hadExistingLink, rollbackLink, rollbackCacheData, persist: false);
                    rollbackPending = false;
                    ResetToSearchStage();
                    return;
                }

                if (!TransitionToEditing(link, requireManualProviderData: true))
                {
                    RollbackTransientLink(hadExistingLink, rollbackLink, rollbackCacheData, persist: false);
                    rollbackPending = false;
                    await HandleRefreshFailureAsync(
                        precheckHasAchievements == false
                            ? (ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Schema_NoAchievements") ??
                               "The selected game has no achievements.")
                            : null);
                    return;
                }

                // Persist the link only after we have confirmed editable manual schema data.
                SaveLink(link);
                rollbackPending = false;
            }
            catch (OperationCanceledException)
            {
                if (rollbackPending)
                {
                    RollbackTransientLink(hadExistingLink, rollbackLink, rollbackCacheData, persist: false);
                    rollbackPending = false;
                }

                ResetToSearchStage();
            }
            catch (Exception ex)
            {
                if (rollbackPending)
                {
                    RollbackTransientLink(hadExistingLink, rollbackLink, rollbackCacheData, persist: false);
                    rollbackPending = false;
                }

                _logger?.Error(ex, "Manual achievement refresh failed");
                await HandleRefreshFailureAsync();
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
            if (report == null)
            {
                return;
            }

            if (report.CurrentGameId.HasValue && report.CurrentGameId.Value != _playniteGame.Id)
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
            ResetToSearchStage();
        }

        /// <summary>
        /// Refreshes cache for an existing link and transitions to editing.
        /// Called when opening the wizard for an existing manual link.
        /// </summary>
        private async Task RefreshExistingAndTransitionToEditAsync()
        {
            if (_existingLink == null)
            {
                await HandleRefreshFailureAsync();
                return;
            }

            // Existing links should open directly when we already have cached/hydrated
            // achievements for this game (for example after legacy import).
            if (TransitionToEditing(_existingLink))
            {
                return;
            }

            ErrorMessage = string.Empty;
            ProgressMessage = ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");
            ProgressPercent = 0;
            CanCancelRefresh = true;

            _refreshCts = new CancellationTokenSource();

            try
            {
                _achievementService.RebuildProgress += OnRebuildProgress;

                var request = new RefreshRequest
                {
                    GameIds = new List<Guid> { _playniteGame.Id }
                };

                await _achievementService.ExecuteRefreshAsync(request);

                if (_refreshCts.Token.IsCancellationRequested)
                {
                    ResetToSearchStage();
                    return;
                }

                if (!TransitionToEditing(_existingLink))
                {
                    await HandleRefreshFailureAsync();
                }
            }
            catch (OperationCanceledException)
            {
                ResetToSearchStage();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Manual achievement refresh failed during edit flow");
                await HandleRefreshFailureAsync();
            }
            finally
            {
                _achievementService.RebuildProgress -= OnRebuildProgress;
                _refreshCts?.Dispose();
                _refreshCts = null;
            }
        }

        #endregion

        #region Edit Logic

        private bool TransitionToEditing(ManualAchievementLink link, bool requireManualProviderData = false)
        {
            var cachedData = _achievementService.Cache.LoadGameData(_playniteGame.Id.ToString());
            var hydratedData = _achievementService.GetGameAchievementData(_playniteGame.Id);
            string providerKey = cachedData?.ProviderKey;
            var achievements = cachedData?.Achievements?
                .Where(a => a != null)
                .ToList();

            if (achievements == null || achievements.Count == 0)
            {
                providerKey = hydratedData?.ProviderKey;
                achievements = hydratedData?.Achievements?
                    .Where(a => a != null)
                    .ToList();
            }

            if (achievements == null || achievements.Count == 0)
            {
                return false;
            }

            if (requireManualProviderData)
            {
                if (string.IsNullOrWhiteSpace(providerKey) ||
                    !string.Equals(providerKey, "Manual", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (hydratedData?.AchievementOrder != null && hydratedData.AchievementOrder.Count > 0)
            {
                achievements = AchievementOrderHelper.ApplyOrder(
                    achievements,
                    a => a?.ApiName,
                    hydratedData.AchievementOrder);
            }

            var inheritedFallbackApplied = ApplyInheritedUnlockFallback(link, achievements, _pendingInheritedUnlocks);
            if (inheritedFallbackApplied > 0)
            {
                link.LastModifiedUtc = DateTime.UtcNow;
                SaveLink(link);
            }
            _pendingInheritedUnlocks = null;

            PopulateAchievements(achievements, link);
            SourceGameId = link.SourceGameId;
            SourceGameName = !string.IsNullOrWhiteSpace(cachedData?.GameName)
                ? cachedData.GameName
                : (!string.IsNullOrWhiteSpace(hydratedData?.GameName) ? hydratedData.GameName : link.SourceGameId);
            ManualSourceName = ResolveSourceName(link?.SourceKey);
            _lastSavedLink = link?.Clone();
            SaveStatusMessage = string.Empty;

            CurrentStage = WizardStage.Editing;
            return true;
        }

        private void ResetToSearchStage(IManualSource sourceToPreserve = null)
        {
            // Use provided source or preserve current source
            var preservedSource = sourceToPreserve ?? _source;
            _logger?.Debug($"[ManualTracking] ResetToSearchStage: preserving source={preservedSource?.SourceKey}");

            CurrentStage = WizardStage.Search;
            ErrorMessage = string.Empty;
            CanCancelRefresh = false;
            _pendingInheritedUnlocks = null;
            SearchResults.Clear();
            SelectedResult = null;

            // Set the preserved source
            _source = preservedSource;
            _selectedSource = preservedSource;

            // Ensure source name is updated
            ManualSourceName = ResolveSourceName(_source?.SourceKey);

            // Notify all source-related properties
            OnPropertyChanged(nameof(SelectedSourceKey));
            OnPropertyChanged(nameof(SelectedSource));
            OnPropertyChanged(nameof(ManualSourceName));
            OnPropertyChanged(nameof(ShowPlatformColumn));

            if (string.IsNullOrWhiteSpace(SearchText))
            {
                SearchText = _playniteGame?.Name ?? string.Empty;
            }
        }

        private async Task HandleRefreshFailureAsync(string dialogMessage = null)
        {
            _logger?.Debug($"[ManualTracking] HandleRefreshFailureAsync: _source={_source?.SourceKey}");

            // Capture the current source before showing dialog
            var currentSource = _source;

            // Show dialog while still on Refreshing stage
            // User explicitly clicks OK to transition back to search
            if (!string.IsNullOrWhiteSpace(dialogMessage))
            {
                var result = _playniteApi?.Dialogs?.ShowMessage(
                    dialogMessage,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                // Transition only happens when user explicitly dismisses the dialog
                if (result == MessageBoxResult.OK)
                {
                    // Transition back to search with preserved source
                    ResetToSearchStage(currentSource);

                    // Re-search with the current source to populate results
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
                        await ExecuteSearchAsync();
                    }
                }
            }
            else
            {
                // No dialog message - just transition back with current source
                ResetToSearchStage(currentSource);

                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    await ExecuteSearchAsync();
                }
            }
        }

        private async Task<bool?> SelectedResultHasAchievementsAsync(ManualGameSearchResult selectedResult)
        {
            if (selectedResult == null || string.IsNullOrWhiteSpace(selectedResult.SourceGameId))
            {
                return false;
            }

            try
            {
                var achievements = await _source.GetAchievementsAsync(
                    selectedResult.SourceGameId,
                    _language,
                    CancellationToken.None);

                var hasAchievements = achievements != null && achievements.Count > 0;
                selectedResult.HasAchievements = hasAchievements;
                return hasAchievements;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Manual pre-check failed for source game '{selectedResult.SourceGameId}'");
                return null;
            }
        }

        private List<InheritedUnlockEntry> CaptureInheritedUnlocksFromCurrentProvider()
        {
            try
            {
                var gameData = _achievementService?.GetGameAchievementData(_playniteGame.Id);
                var achievements = gameData?.Achievements;
                if (achievements == null || achievements.Count == 0)
                {
                    return null;
                }

                return achievements
                    .Where(a => a != null)
                    .Select(a => new InheritedUnlockEntry
                    {
                        ApiName = a.ApiName?.Trim(),
                        DisplayName = a.DisplayName?.Trim(),
                        IsUnlocked = a.Unlocked,
                        UnlockTimeUtc = a.Unlocked ? a.UnlockTimeUtc : null
                    })
                    .Where(a => a.IsUnlocked || a.UnlockTimeUtc.HasValue)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to capture provider unlock snapshot for manual transition.");
                return null;
            }
        }

        private static void SeedLinkUnlocksFromInheritedSnapshot(
            ManualAchievementLink link,
            IReadOnlyCollection<InheritedUnlockEntry> inheritedUnlocks)
        {
            if (link == null || inheritedUnlocks == null || inheritedUnlocks.Count == 0)
            {
                return;
            }

            if (link.UnlockTimes == null)
            {
                link.UnlockTimes = new Dictionary<string, DateTime?>();
            }

            if (link.UnlockStates == null)
            {
                link.UnlockStates = new Dictionary<string, bool>();
            }

            foreach (var entry in inheritedUnlocks)
            {
                var apiName = entry?.ApiName?.Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var isUnlocked = entry.IsUnlocked || entry.UnlockTimeUtc.HasValue;
                if (!isUnlocked)
                {
                    continue;
                }

                link.UnlockStates[apiName] = true;
                if (entry.UnlockTimeUtc.HasValue)
                {
                    link.UnlockTimes[apiName] = entry.UnlockTimeUtc;
                }
            }
        }

        private static int ApplyInheritedUnlockFallback(
            ManualAchievementLink link,
            IReadOnlyCollection<AchievementDetail> manualAchievements,
            IReadOnlyCollection<InheritedUnlockEntry> inheritedUnlocks)
        {
            if (link == null || manualAchievements == null || inheritedUnlocks == null || inheritedUnlocks.Count == 0)
            {
                return 0;
            }

            if (link.UnlockTimes == null)
            {
                link.UnlockTimes = new Dictionary<string, DateTime?>();
            }

            if (link.UnlockStates == null)
            {
                link.UnlockStates = new Dictionary<string, bool>();
            }

            var inheritedByDisplayName = inheritedUnlocks
                .Where(entry => !string.IsNullOrWhiteSpace(entry?.DisplayName))
                .GroupBy(entry => entry.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.FirstOrDefault(entry => entry.IsUnlocked || entry.UnlockTimeUtc.HasValue),
                    StringComparer.OrdinalIgnoreCase);

            var applied = 0;
            foreach (var achievement in manualAchievements)
            {
                var apiName = achievement?.ApiName?.Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var alreadyHasState = link.UnlockStates.TryGetValue(apiName, out var existingUnlocked) && existingUnlocked;
                var alreadyHasTime = link.UnlockTimes.TryGetValue(apiName, out var existingTime) && existingTime.HasValue;
                if (alreadyHasState || alreadyHasTime)
                {
                    continue;
                }

                var displayName = achievement.DisplayName?.Trim();
                if (string.IsNullOrWhiteSpace(displayName) ||
                    !inheritedByDisplayName.TryGetValue(displayName, out var inherited) ||
                    inherited == null)
                {
                    continue;
                }

                var isUnlocked = inherited.IsUnlocked || inherited.UnlockTimeUtc.HasValue;
                if (!isUnlocked)
                {
                    continue;
                }

                link.UnlockStates[apiName] = true;
                if (inherited.UnlockTimeUtc.HasValue)
                {
                    link.UnlockTimes[apiName] = inherited.UnlockTimeUtc;
                }
                applied++;
            }

            return applied;
        }

        private string ResolveSourceName(string sourceKey)
        {
            if (!string.IsNullOrWhiteSpace(sourceKey) &&
                string.Equals(sourceKey, _source.SourceKey, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(_source.SourceName) ? _source.SourceKey : _source.SourceName;
            }

            if (!string.IsNullOrWhiteSpace(sourceKey))
            {
                return sourceKey;
            }

            return string.IsNullOrWhiteSpace(_source.SourceName) ? _source.SourceKey : _source.SourceName;
        }

        private void EnsureManualProviderEnabledForLinking()
        {
            try
            {
                if (_settings?.Persisted == null || _settings.Persisted.ManualEnabled)
                {
                    return;
                }

                _settings.Persisted.ManualEnabled = true;
                _saveSettings(_settings);

                PlayniteAchievementsPlugin.Instance?.ProviderRegistry?.SyncFromSettings(_settings.Persisted);
                PlayniteAchievementsPlugin.NotifySettingsSaved();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to auto-enable Manual provider for manual linking flow.");
            }
        }

        private void RollbackTransientLink(
            bool hadExistingLink,
            ManualAchievementLink previousLink,
            GameAchievementData previousCacheData,
            bool persist = true)
        {
            try
            {
                if (_settings?.Persisted?.ManualAchievementLinks == null)
                {
                    return;
                }

                if (hadExistingLink && previousLink != null)
                {
                    _settings.Persisted.ManualAchievementLinks[_playniteGame.Id] = previousLink;
                }
                else
                {
                    _settings.Persisted.ManualAchievementLinks.Remove(_playniteGame.Id);
                }

                if (persist)
                {
                    _saveSettings(_settings);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to rollback transient manual link after refresh did not complete.");
            }

            try
            {
                var cache = _achievementService?.Cache;
                if (cache == null)
                {
                    return;
                }

                if (previousCacheData != null)
                {
                    cache.SaveGameData(_playniteGame.Id.ToString(), previousCacheData);
                }
                else
                {
                    cache.RemoveGameData(_playniteGame.Id);
                }

                cache.NotifyCacheInvalidated();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to rollback transient manual cache state after refresh did not complete.");
            }
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

                    if (link?.UnlockStates != null &&
                        link.UnlockStates.TryGetValue(detail.ApiName, out var existingUnlocked))
                    {
                        isUnlocked = existingUnlocked;
                    }

                    if (link?.UnlockTimes != null &&
                        link.UnlockTimes.TryGetValue(detail.ApiName, out var existingTime))
                    {
                        unlockTime = existingTime;

                        // Backward compatibility: before UnlockStates existed,
                        // unlock state was inferred from unlock time value.
                        if (link?.UnlockStates == null || link.UnlockStates.Count == 0)
                        {
                            isUnlocked = existingTime.HasValue;
                        }
                    }

                    if (!isUnlocked)
                    {
                        unlockTime = null;
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
            if (e.PropertyName == nameof(ManualAchievementEditItem.IsUnlocked) ||
                e.PropertyName == nameof(ManualAchievementEditItem.HasUnlockTime) ||
                e.PropertyName == nameof(ManualAchievementEditItem.UnlockTime) ||
                e.PropertyName == nameof(ManualAchievementEditItem.IsValidTime))
            {
                SaveStatusMessage = string.Empty;
                ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
            }

            if (e.PropertyName == nameof(ManualAchievementEditItem.IsUnlocked))
            {
                UpdateCounts();
            }

        }

        private void SetAllUnlocked(bool unlocked)
        {
            foreach (var item in FilteredAchievements)
            {
                if (unlocked)
                {
                    item.IsUnlocked = true;
                    item.HasUnlockTime = true;
                }
                else
                {
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

        private void RevealAchievement(ManualAchievementEditItem item)
        {
            if (item == null)
            {
                return;
            }

            item.ToggleReveal();
        }

        private bool CanSave()
        {
            if (CurrentStage != WizardStage.Editing || AllAchievements.Count == 0)
            {
                return false;
            }

            // Disable save if any achievement with an unlock timestamp has invalid time input
            foreach (var item in AllAchievements)
            {
                if (item.IsUnlocked && item.HasUnlockTime && !item.IsValidTime)
                {
                    return false;
                }
            }

            return true;
        }

        private ManualAchievementLink BuildLink()
        {
            var now = DateTime.UtcNow;

            var link = new ManualAchievementLink
            {
                SourceKey = _source.SourceKey,
                SourceGameId = SourceGameId,
                UnlockTimes = new Dictionary<string, DateTime?>(),
                UnlockStates = new Dictionary<string, bool>(),
                CreatedUtc = _existingLink?.CreatedUtc ?? now,
                LastModifiedUtc = now
            };

            foreach (var item in AllAchievements)
            {
                if (item == null || !item.IsUnlocked || string.IsNullOrWhiteSpace(item.ApiName))
                {
                    continue;
                }

                var apiName = item.ApiName.Trim();
                link.UnlockStates[apiName] = true;

                if (item.UnlockTime.HasValue)
                {
                    link.UnlockTimes[apiName] = item.UnlockTime;
                }
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
                    var nowUtc = DateTime.UtcNow;

                    foreach (var achievement in cachedData.Achievements)
                    {
                        if (string.IsNullOrWhiteSpace(achievement?.ApiName))
                        {
                            continue;
                        }

                        var unlockedState = false;
                        var hasState = link.UnlockStates != null &&
                                       link.UnlockStates.TryGetValue(achievement.ApiName, out unlockedState);
                        DateTime? unlockTime = null;
                        var hasTime = link.UnlockTimes != null &&
                                      link.UnlockTimes.TryGetValue(achievement.ApiName, out unlockTime);

                        var isUnlocked = (hasState && unlockedState) || (hasTime && unlockTime.HasValue);
                        achievement.Unlocked = isUnlocked;
                        achievement.UnlockTimeUtc = isUnlocked && hasTime && unlockTime.HasValue
                            ? unlockTime
                            : null;
                    }

                    // Force a new snapshot version so theme update coalescing does not skip this save.
                    cachedData.LastUpdatedUtc = nowUtc;

                    _achievementService.Cache.SaveGameData(_playniteGame.Id.ToString(), cachedData);
                    _achievementService.Cache.NotifyCacheInvalidated();

                    // Ensure immediate theme refresh for this game after manual edits.
                    if (_settings?.SelectedGame?.Id == _playniteGame.Id)
                    {
                        PlayniteAchievementsPlugin.Instance?.ThemeUpdateService?.RequestUpdate(_playniteGame.Id);
                    }
                }

                _logger?.Info($"Saved manual achievement link for '{_playniteGame.Name}' (source={link.SourceKey}, gameId={link.SourceGameId})");

                _lastSavedLink = link.Clone();
                SaveStatusMessage = ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Edit_SaveSuccess");
                ManualLinkSaved?.Invoke(this, EventArgs.Empty);
                CurrentStage = WizardStage.Editing;
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
            SetLinkInMemory(CompactLinkForPersistence(link));
            _saveSettings(_settings);
        }

        private void SetLinkInMemory(ManualAchievementLink link)
        {
            if (link == null || _settings?.Persisted?.ManualAchievementLinks == null)
            {
                return;
            }

            _settings.Persisted.ManualAchievementLinks[_playniteGame.Id] = link;
        }

        private static ManualAchievementLink CompactLinkForPersistence(ManualAchievementLink link)
        {
            if (link == null)
            {
                return null;
            }

            var compactStates = new Dictionary<string, bool>();
            if (link.UnlockStates != null)
            {
                foreach (var pair in link.UnlockStates)
                {
                    var apiName = pair.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(apiName) || !pair.Value)
                    {
                        continue;
                    }

                    compactStates[apiName] = true;
                }
            }

            var compactTimes = new Dictionary<string, DateTime?>();
            if (link.UnlockTimes != null)
            {
                foreach (var pair in link.UnlockTimes)
                {
                    var apiName = pair.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(apiName) || !pair.Value.HasValue)
                    {
                        continue;
                    }

                    compactTimes[apiName] = pair.Value;
                    compactStates[apiName] = true;
                }
            }

            link.UnlockStates = compactStates;
            link.UnlockTimes = compactTimes;
            return link;
        }

        private void CancelOrClose()
        {
            if (CurrentStage == WizardStage.Editing)
            {
                RevertEditingToLastSavedState();
                SaveStatusMessage = string.Empty;
                ErrorMessage = string.Empty;
            }
            else if (CurrentStage == WizardStage.Search)
            {
                SaveStatusMessage = string.Empty;
                ErrorMessage = string.Empty;
            }
        }

        private void RevertEditingToLastSavedState()
        {
            if (CurrentStage != WizardStage.Editing || AllAchievements.Count == 0)
            {
                return;
            }

            var baseline = _lastSavedLink ?? _existingLink;
            var unlockTimes = baseline?.UnlockTimes ?? new Dictionary<string, DateTime?>();
            var unlockStates = baseline?.UnlockStates ?? new Dictionary<string, bool>();

            foreach (var item in AllAchievements)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ApiName))
                {
                    continue;
                }

                var hasState = unlockStates.TryGetValue(item.ApiName, out var unlockedState);
                var hasTime = unlockTimes.TryGetValue(item.ApiName, out var unlockTime);
                var isUnlocked = hasState
                    ? unlockedState
                    : (hasTime && unlockTime.HasValue);

                if (isUnlocked)
                {
                    item.IsUnlocked = true;
                    item.UnlockTime = hasTime && unlockTime.HasValue ? unlockTime.Value : (DateTime?)null;
                }
                else
                {
                    item.IsUnlocked = false;
                }
            }

            if (baseline != null)
            {
                SourceGameId = baseline.SourceGameId;
                ManualSourceName = ResolveSourceName(baseline.SourceKey);
            }

            UpdateCounts();
            FilterAchievements();
            ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
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
