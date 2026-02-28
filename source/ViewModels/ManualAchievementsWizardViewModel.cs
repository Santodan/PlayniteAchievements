using System;
using System.Collections.Generic;
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

        private WizardStage _currentStage = WizardStage.Search;
        private double _progressPercent;
        private string _progressMessage = string.Empty;
        private bool _canCancelRefresh = true;
        private string _errorMessage = string.Empty;
        private ManualAchievementsEditViewModel _editVm;

        public event EventHandler RequestClose;

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

        public string PlayniteGameName => _playniteGame?.Name ?? string.Empty;

        public ManualAchievementsSearchViewModel SearchVm { get; }

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

        public ManualAchievementsEditViewModel EditVm
        {
            get => _editVm;
            private set
            {
                if (_editVm != value)
                {
                    _editVm = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasEditVm));
                }
            }
        }

        public bool HasEditVm => EditVm != null;

        public string WindowTitle =>
            ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Wizard_Title");

        public ICommand NextCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand CancelRefreshCommand { get; }
        public ICommand RetryCommand { get; }

        public bool? DialogResult { get; private set; }

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

            SearchVm = new ManualAchievementsSearchViewModel(
                source,
                playniteGame.Name,
                _language,
                logger,
                startAtEditingStage ? null : playniteGame.Name);

            NextCommand = new AsyncCommand(_ => TransitionToRefreshingAsync(), _ => CanTransitionToNext());
            SaveCommand = new RelayCommand(_ => Save(), _ => CanSave());
            CancelCommand = new RelayCommand(_ => CloseDialog(false));
            CancelRefreshCommand = new RelayCommand(_ => CancelRefresh());
            RetryCommand = new AsyncCommand(_ => TransitionToRefreshingAsync());

            if (_startAtEditingStage)
            {
                CurrentStage = WizardStage.Editing;
                LoadEditVmFromExistingLink();
            }
        }

        private bool CanTransitionToNext()
        {
            return CurrentStage == WizardStage.Search &&
                   SearchVm.SelectedResult != null &&
                   !SearchVm.IsSearching;
        }

        private bool CanSave()
        {
            return CurrentStage == WizardStage.Editing && EditVm != null;
        }

        private async Task TransitionToRefreshingAsync()
        {
            if (CurrentStage != WizardStage.Search || SearchVm.SelectedResult == null)
            {
                return;
            }

            var selectedResult = SearchVm.SelectedResult;
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

            EditVm = new ManualAchievementsEditViewModel(
                _source,
                cachedData.Achievements,
                link.SourceGameId,
                cachedData.GameName ?? link.SourceGameId,
                link,
                _playniteGame.Name,
                _language,
                _logger);

            CurrentStage = WizardStage.Editing;
        }

        private void LoadEditVmFromExistingLink()
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
                            EditVm = new ManualAchievementsEditViewModel(
                                _source,
                                achievements,
                                _existingLink.SourceGameId,
                                _existingLink.SourceGameId,
                                _existingLink,
                                _playniteGame.Name,
                                _language,
                                _logger);
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

            EditVm = new ManualAchievementsEditViewModel(
                _source,
                cachedData.Achievements,
                _existingLink.SourceGameId,
                cachedData.GameName ?? _existingLink.SourceGameId,
                _existingLink,
                _playniteGame.Name,
                _language,
                _logger);
        }

        private void CancelRefresh()
        {
            _refreshCts?.Cancel();
            CurrentStage = WizardStage.Search;
        }

        private void Save()
        {
            if (EditVm == null)
            {
                return;
            }

            try
            {
                var link = EditVm.BuildLink();
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

        public void CancelSearch()
        {
            SearchVm?.CancelSearch();
        }

        public void Cleanup()
        {
            SearchVm?.CancelSearch();
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;

            if (_achievementService != null)
            {
                _achievementService.RebuildProgress -= OnRebuildProgress;
            }
        }
    }
}
