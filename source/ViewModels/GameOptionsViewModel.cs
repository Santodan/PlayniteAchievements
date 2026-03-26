using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Services;
using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public sealed class GameOptionsViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly RefreshRuntime _refreshService;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private GameOptionsTab _selectedTab;
        private bool _hasGame;
        private string _gameName;
        private string _gameImagePath;
        private bool _hasCachedData;
        private string _providerName;
        private string _librarySourceName;
        private string _lastUpdatedLocalText;
        private string _lastUpdatedUtcText;
        private int _totalAchievements;
        private int _unlockedAchievements;
        private bool _isCompleted;
        private string _currentCapstoneName;
        private bool _isExcluded;
        private bool _isExcludedFromSummaries;
        private bool _isRaCapable;
        private bool _hasRaOverride;
        private string _raOverrideValue;
        private string _raOverrideInput;
        private bool _isXeniaCapable;
        private bool _hasManualTrackingLink;
        private string _manualTrackingSummary;
        private bool _hasCapstoneData;
        private string _capstoneEmptyMessage;
        private bool _isRefreshing;
        private string _cachedProviderKey;
        private bool _cachedHasAchievements;
        private string _manualTrackingWarningAcceptedForProvider;
        private bool _showManualTrackingTab = true;
        private bool _showExophaseToggle;
        private bool _useExophaseForGame;
        private bool _isExophaseManagedByPlatform;
        private string _exophaseAutoSlug;
        private string _exophaseSlugOverrideValue;
        private string _exophaseSlugInput;
        private bool _hasExophaseSlugOverride;

        public RelayCommand OpenAchievementsCommand { get; }
        public RelayCommand ToggleExclusionCommand { get; }
        public RelayCommand ToggleSummaryExclusionCommand { get; }
        public RelayCommand ApplyRaOverrideCommand { get; }
        public RelayCommand ClearRaOverrideCommand { get; }
        public RelayCommand ApplyExophaseSlugOverrideCommand { get; }
        public RelayCommand ClearExophaseSlugOverrideCommand { get; }
        public RelayCommand UnlinkManualTrackingCommand { get; }
        public RelayCommand RefreshStateCommand { get; }
        public AsyncCommand RefreshGameCommand { get; }
        public RelayCommand ClearGameDataCommand { get; }

        public GameOptionsViewModel(
            Guid gameId,
            GameOptionsTab initialTab,
            PlayniteAchievementsPlugin plugin,
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            IPlayniteAPI playniteApi,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _selectedTab = initialTab;
            _plugin = plugin;
            _refreshService = refreshRuntime;
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementOverridesService = achievementOverridesService;
            _playniteApi = playniteApi;
            _settings = settings;
            _logger = logger;

            OpenAchievementsCommand = new RelayCommand(_ => OpenAchievements(), _ => HasGame);
            ToggleExclusionCommand = new RelayCommand(_ => ToggleExclusion(), _ => HasGame);
            ToggleSummaryExclusionCommand = new RelayCommand(_ => ToggleSummaryExclusion(), _ => HasGame);
            ApplyRaOverrideCommand = new RelayCommand(_ => ApplyRaOverride(), _ => HasGame && IsRaCapable);
            ClearRaOverrideCommand = new RelayCommand(_ => ClearRaOverride(), _ => HasGame && IsRaCapable && HasRaOverride);
            ApplyExophaseSlugOverrideCommand = new RelayCommand(_ => ApplyExophaseSlugOverride(), _ => HasGame && ShowExophaseToggle);
            ClearExophaseSlugOverrideCommand = new RelayCommand(_ => ClearExophaseSlugOverride(), _ => HasGame && ShowExophaseToggle && HasExophaseSlugOverride);
            UnlinkManualTrackingCommand = new RelayCommand(_ => UnlinkManualTracking(), _ => HasGame && HasManualTrackingLink);
            RefreshStateCommand = new RelayCommand(_ => Reload());
            RefreshGameCommand = new AsyncCommand(_ => RefreshGameAsync(), _ => HasGame && !IsRefreshing && !(_refreshService?.IsRebuilding ?? false));
            ClearGameDataCommand = new RelayCommand(_ => ClearGameData(), _ => HasGame);

            Reload();
        }

        public Guid GameId => _gameId;

        public string GameIdText => _gameId.ToString();

        public GameOptionsTab SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (_selectedTab == value)
                {
                    return;
                }

                if (value == GameOptionsTab.ManualTracking && !ShowManualTrackingTab)
                {
                    return;
                }

                if (!HasCapstoneData &&
                    (value == GameOptionsTab.Capstones ||
                     value == GameOptionsTab.AchievementOrder ||
                     value == GameOptionsTab.Category))
                {
                    return;
                }

                if (value == GameOptionsTab.ManualTracking &&
                    ShouldWarnAboutManualTrackingOverride(out var existingProviderKey) &&
                    !string.Equals(_manualTrackingWarningAcceptedForProvider, existingProviderKey, StringComparison.OrdinalIgnoreCase))
                {
                    var displayName = ProviderRegistry.GetLocalizedName(existingProviderKey);
                    var message = string.Format(
                        L(
                            "LOCPlayAch_GameOptions_Manual_ReplaceProviderWarning",
                            "Manual tracking can replace cached achievement data from {0}. Continue?"),
                        displayName);

                    var result = _playniteApi?.Dialogs?.ShowMessage(
                        message,
                        L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning) ?? MessageBoxResult.None;

                    if (result != MessageBoxResult.OK)
                    {
                        return;
                    }

                    _manualTrackingWarningAcceptedForProvider = existingProviderKey;
                }

                SetValue(ref _selectedTab, value);
            }
        }

        public bool ShowManualTrackingTab
        {
            get => _showManualTrackingTab;
            private set => SetValue(ref _showManualTrackingTab, value);
        }

        public bool ShowExophaseToggle
        {
            get => _showExophaseToggle;
            private set => SetValue(ref _showExophaseToggle, value);
        }

        public bool UseExophaseForGame
        {
            get => _useExophaseForGame;
            set
            {
                if (SetValueAndReturn(ref _useExophaseForGame, value))
                {
                    if (ExophaseDataProvider.SetIncludedGame(_gameId, GameName, value, _persistSettingsForUi, _logger))
                    {
                        TriggerRefresh();
                    }
                    else
                    {
                        Reload();
                    }
                }
            }
        }

        public bool IsExophaseManagedByPlatform
        {
            get => _isExophaseManagedByPlatform;
            private set => SetValue(ref _isExophaseManagedByPlatform, value);
        }

        /// <summary>
        /// The auto-detected Exophase slug for this game (shown as placeholder).
        /// </summary>
        public string ExophaseAutoSlug
        {
            get => _exophaseAutoSlug;
            private set => SetValue(ref _exophaseAutoSlug, value);
        }

        /// <summary>
        /// The current slug override value (or null if not overridden).
        /// </summary>
        public string ExophaseSlugOverrideValue
        {
            get => _exophaseSlugOverrideValue;
            private set => SetValue(ref _exophaseSlugOverrideValue, value);
        }

        /// <summary>
        /// Input field for the slug override.
        /// </summary>
        public string ExophaseSlugInput
        {
            get => _exophaseSlugInput;
            set
            {
                if (SetValueAndReturn(ref _exophaseSlugInput, value ?? string.Empty))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>
        /// Whether this game has an active slug override.
        /// </summary>
        public bool HasExophaseSlugOverride
        {
            get => _hasExophaseSlugOverride;
            private set => SetValue(ref _hasExophaseSlugOverride, value);
        }

        public bool HasGame
        {
            get => _hasGame;
            private set
            {
                if (SetValueAndReturn(ref _hasGame, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public string GameName
        {
            get => _gameName;
            private set => SetValue(ref _gameName, value);
        }

        public string GameImagePath
        {
            get => _gameImagePath;
            private set => SetValue(ref _gameImagePath, value);
        }

        public bool HasCachedData
        {
            get => _hasCachedData;
            private set => SetValue(ref _hasCachedData, value);
        }

        public string ProviderName
        {
            get => _providerName;
            private set => SetValue(ref _providerName, value);
        }

        public string LibrarySourceName
        {
            get => _librarySourceName;
            private set => SetValue(ref _librarySourceName, value);
        }

        public string LastUpdatedLocalText
        {
            get => _lastUpdatedLocalText;
            set => SetValue(ref _lastUpdatedLocalText, value);
        }

        public string LastUpdatedUtcText
        {
            get => _lastUpdatedUtcText;
            set => SetValue(ref _lastUpdatedUtcText, value);
        }

        public int TotalAchievements
        {
            get => _totalAchievements;
            private set
            {
                if (SetValueAndReturn(ref _totalAchievements, value))
                {
                    OnPropertyChanged(nameof(CompletionSummary));
                    OnPropertyChanged(nameof(CompletionPercentValue));
                }
            }
        }

        public int UnlockedAchievements
        {
            get => _unlockedAchievements;
            private set
            {
                if (SetValueAndReturn(ref _unlockedAchievements, value))
                {
                    OnPropertyChanged(nameof(CompletionSummary));
                    OnPropertyChanged(nameof(CompletionPercentValue));
                }
            }
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            private set => SetValue(ref _isCompleted, value);
        }

        public string CompletionSummary
        {
            get
            {
                if (TotalAchievements <= 0)
                {
                    return "0 / 0 (0%)";
                }

                var percent = (double)UnlockedAchievements / TotalAchievements * 100.0;
                return $"{UnlockedAchievements} / {TotalAchievements} ({percent:F0}%)";
            }
        }

        public double CompletionPercentValue
        {
            get
            {
                if (TotalAchievements <= 0)
                {
                    return 0;
                }

                var percent = (double)UnlockedAchievements / TotalAchievements * 100.0;
                return Math.Max(0, Math.Min(100, percent));
            }
        }

        public string CurrentCapstoneName
        {
            get => _currentCapstoneName;
            private set => SetValue(ref _currentCapstoneName, value);
        }

        public bool IsExcluded
        {
            get => _isExcluded;
            private set
            {
                if (SetValueAndReturn(ref _isExcluded, value))
                {
                    OnPropertyChanged(nameof(ExclusionStatusText));
                    OnPropertyChanged(nameof(ExclusionActionText));
                }
            }
        }

        public string ExclusionStatusText => IsExcluded
            ? L("LOCPlayAch_GameOptions_Status_ExcludedFromRefreshes", "Excluded from Refreshes")
            : L("LOCPlayAch_GameOptions_Status_IncludedFromRefreshes", "Included from Refreshes");

        public string ExclusionActionText => IsExcluded
            ? L("LOCPlayAch_Menu_IncludeGame", "Include this Game")
            : L("LOCPlayAch_Menu_ExcludeGame", "Exclude this Game and Clear Data");

        public bool IsExcludedFromSummaries
        {
            get => _isExcludedFromSummaries;
            private set
            {
                if (SetValueAndReturn(ref _isExcludedFromSummaries, value))
                {
                    OnPropertyChanged(nameof(SummaryExclusionStatusText));
                    OnPropertyChanged(nameof(SummaryExclusionActionText));
                }
            }
        }

        public string SummaryExclusionStatusText => IsExcludedFromSummaries
            ? L("LOCPlayAch_GameOptions_Status_ExcludedFromSummaries", "Excluded from Summaries")
            : L("LOCPlayAch_GameOptions_Status_IncludedFromSummaries", "Included in Summaries");

        public string SummaryExclusionActionText => IsExcludedFromSummaries
            ? L("LOCPlayAch_GameOptions_Action_IncludeInSummaries", "Include in Summaries")
            : L("LOCPlayAch_GameOptions_Action_ExcludeFromSummaries", "Exclude from Summaries");

        public bool IsRaCapable
        {
            get => _isRaCapable;
            private set
            {
                if (SetValueAndReturn(ref _isRaCapable, value))
                {
                    OnPropertyChanged(nameof(RaStatusText));
                    RaiseCommandStates();
                }
            }
        }

        public bool HasRaOverride
        {
            get => _hasRaOverride;
            private set
            {
                if (SetValueAndReturn(ref _hasRaOverride, value))
                {
                    OnPropertyChanged(nameof(RaStatusText));
                    RaiseCommandStates();
                }
            }
        }

        public string RaOverrideValue
        {
            get => _raOverrideValue;
            private set => SetValue(ref _raOverrideValue, value);
        }

        public string RaOverrideInput
        {
            get => _raOverrideInput;
            set
            {
                if (SetValueAndReturn(ref _raOverrideInput, value ?? string.Empty))
                {
                    RaiseCommandStates();
                }
            }
        }

        public string RaStatusText
        {
            get
            {
                if (!IsRaCapable)
                {
                    return L("LOCPlayAch_GameOptions_Overrides_RaNotCapable", "RetroAchievements override is not available for this game.");
                }

                if (!HasRaOverride)
                {
                    return L("LOCPlayAch_GameOptions_Status_RaOverrideNone", "No override set");
                }

                return string.Format(
                    L("LOCPlayAch_GameOptions_Status_RaOverrideValue", "Override set: {0}"),
                    RaOverrideValue);
            }
        }

        public bool IsXeniaCapable
        {
            get => _isXeniaCapable;
            private set
            {
                if (SetValueAndReturn(ref _isXeniaCapable, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasManualTrackingLink
        {
            get => _hasManualTrackingLink;
            private set
            {
                if (SetValueAndReturn(ref _hasManualTrackingLink, value))
                {
                    OnPropertyChanged(nameof(ManualTrackingStatusText));
                    RaiseCommandStates();
                }
            }
        }

        public string ManualTrackingSummary
        {
            get => _manualTrackingSummary;
            private set => SetValue(ref _manualTrackingSummary, value);
        }

        public string ManualTrackingStatusText => HasManualTrackingLink
            ? L("LOCPlayAch_GameOptions_Status_ManualLinked", "Linked")
            : L("LOCPlayAch_GameOptions_Status_ManualUnlinked", "Not linked");

        public bool HasCapstoneData
        {
            get => _hasCapstoneData;
            private set => SetValue(ref _hasCapstoneData, value);
        }

        public string CapstoneEmptyMessage
        {
            get => _capstoneEmptyMessage;
            private set => SetValue(ref _capstoneEmptyMessage, value);
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set
            {
                if (SetValueAndReturn(ref _isRefreshing, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public void Reload()
        {
            try
            {
                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                HasGame = game != null;
                GameName = game?.Name ?? L("LOCPlayAch_Text_UnknownGame", "Unknown Game");

                var imagePath = string.Empty;
                if (game != null)
                {
                    if (!string.IsNullOrWhiteSpace(game.CoverImage))
                    {
                        imagePath = _playniteApi?.Database?.GetFullFilePath(game.CoverImage);
                    }

                    if (string.IsNullOrWhiteSpace(imagePath) && !string.IsNullOrWhiteSpace(game.Icon))
                    {
                        imagePath = _playniteApi?.Database?.GetFullFilePath(game.Icon);
                    }
                }

                GameImagePath = imagePath;

                var gameData = _plugin?.AchievementDataService?.GetGameAchievementData(_gameId);
                HasCachedData = gameData != null;
                _cachedProviderKey = gameData?.ProviderKey?.Trim();
                _cachedHasAchievements = gameData?.HasAchievements ?? false;
                var allowManualOverride = ManualAchievementsProvider.IsTrackingOverrideEnabled();
                var isExcluded = _plugin?.IsGameExcluded(_gameId) ?? false;
                var hasNonManualProviderData = ShouldWarnAboutManualTrackingOverride(out _);
                ShowManualTrackingTab = allowManualOverride ||
                    (!isExcluded && (!_cachedHasAchievements || !hasNonManualProviderData));
                ProviderName = ResolveProviderDisplayName(gameData);
                LibrarySourceName = ResolveLibrarySourceDisplayName(game, gameData?.LibrarySourceName);

                if (gameData?.LastUpdatedUtc > DateTime.MinValue)
                {
                    LastUpdatedUtcText = gameData.LastUpdatedUtc.ToString("u");
                    LastUpdatedLocalText = gameData.LastUpdatedUtc.ToLocalTime().ToString("g");
                }
                else
                {
                    LastUpdatedUtcText = L("LOCPlayAch_GameOptions_Value_NotAvailable", "N/A");
                    LastUpdatedLocalText = L("LOCPlayAch_GameOptions_Value_NotAvailable", "N/A");
                }

                var achievements = gameData?.Achievements ?? Enumerable.Empty<AchievementDetail>();
                var list = achievements.Where(a => a != null).ToList();
                TotalAchievements = list.Count;
                UnlockedAchievements = list.Count(a => a.Unlocked);
                IsCompleted = gameData?.IsCompleted ?? false;

                var capstone = list.FirstOrDefault(a => a.IsCapstone);
                CurrentCapstoneName = !string.IsNullOrWhiteSpace(capstone?.DisplayName)
                    ? capstone.DisplayName.Trim()
                    : !string.IsNullOrWhiteSpace(capstone?.ApiName)
                        ? capstone.ApiName.Trim()
                        : L("LOCPlayAch_Capstone_Current_None", "None");

                HasCapstoneData = (gameData?.HasAchievements ?? false) && list.Count > 0;
                CapstoneEmptyMessage = string.Format(
                    L("LOCPlayAch_Capstone_NoCachedData", "No cached achievements are available for \"{0}\". Refresh this game first."),
                    GameName);

                IsExcluded = isExcluded;
                IsExcludedFromSummaries = _settings?.Persisted?.ExcludedFromSummariesGameIds?.Contains(_gameId) ?? false;

                var raProvider = _refreshService?.Providers
                    ?.FirstOrDefault(p => p.ProviderKey == "RetroAchievements");
                IsRaCapable = raProvider?.IsCapable(game) == true;

                var xeniaProvider = _refreshService?.Providers
                    ?.FirstOrDefault(p => p.ProviderKey == "Xenia");
                IsXeniaCapable = xeniaProvider?.IsCapable(game) == true;

                if (RetroAchievementsDataProvider.TryGetGameIdOverride(_gameId, out var raId))
                {
                    HasRaOverride = true;
                    RaOverrideValue = raId.ToString();
                    RaOverrideInput = RaOverrideValue;
                }
                else
                {
                    HasRaOverride = false;
                    RaOverrideValue = string.Empty;
                    RaOverrideInput = string.Empty;
                }

                ManualAchievementLink manualLink;
                var hasManualLink = ManualAchievementsProvider.TryGetManualLink(_gameId, out manualLink);
                HasManualTrackingLink = hasManualLink;
                ManualTrackingSummary = ManualAchievementsProvider.GetGameOptionsLinkSummary(manualLink);

                ExophaseDataProvider.GetGameOptionsState(
                    game,
                    _gameId,
                    out var showExophaseToggle,
                    out var isExophaseManagedByPlatform,
                    out var useExophaseForGame,
                    out var exophaseAutoSlug,
                    out var hasExophaseSlugOverride,
                    out var exophaseSlugOverrideValue);

                ShowExophaseToggle = showExophaseToggle;
                IsExophaseManagedByPlatform = isExophaseManagedByPlatform;
                SetValue(ref _useExophaseForGame, useExophaseForGame);
                ExophaseAutoSlug = exophaseAutoSlug;
                HasExophaseSlugOverride = hasExophaseSlugOverride;
                ExophaseSlugOverrideValue = exophaseSlugOverrideValue;
                ExophaseSlugInput = hasExophaseSlugOverride ? exophaseSlugOverrideValue : string.Empty;

                if (!ShowManualTrackingTab && SelectedTab == GameOptionsTab.ManualTracking)
                {
                    SelectedTab = GameOptionsTab.Overview;
                }

                if (!HasCapstoneData &&
                    (SelectedTab == GameOptionsTab.Capstones ||
                     SelectedTab == GameOptionsTab.AchievementOrder ||
                     SelectedTab == GameOptionsTab.Category))
                {
                    SelectedTab = GameOptionsTab.Overview;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to load Game Options state for gameId={_gameId}");
            }
            finally
            {
                RaiseCommandStates();
            }
        }

        private void OpenAchievements()
        {
            _plugin?.OpenSingleGameAchievementsView(_gameId);
        }

        private void ToggleExclusion()
        {
            _plugin?.ToggleGameExclusion(_gameId);
            Reload();
        }

        private void ToggleSummaryExclusion()
        {
            _achievementOverridesService?.SetExcludedFromSummaries(_gameId, !IsExcludedFromSummaries);
            Reload();
        }

        private void ApplyRaOverride()
        {
            if (!IsRaCapable)
            {
                return;
            }

            var text = (RaOverrideInput ?? string.Empty).Trim();
            if (!int.TryParse(text, out var newId) || newId <= 0)
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_Menu_RaGameId_InvalidId", "Please enter a valid positive integer game ID."),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (TrySetRaOverride(newId))
            {
                Reload();
            }
        }

        private void ClearRaOverride()
        {
            if (TryClearRaOverride())
            {
                Reload();
            }
        }

        private bool TrySetRaOverride(int newId)
        {
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null || newId <= 0)
            {
                return false;
            }

            if (!RetroAchievementsDataProvider.TrySetGameIdOverride(_gameId, newId, game.Name, _persistSettingsForUi, _logger))
            {
                return false;
            }

            TriggerRefresh();
            return true;
        }

        private bool TryClearRaOverride()
        {
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null)
            {
                return false;
            }

            if (!RetroAchievementsDataProvider.TryClearGameIdOverride(_gameId, game.Name, _persistSettingsForUi, _logger))
            {
                return false;
            }

            TriggerRefresh();
            return true;
        }

        private void ApplyExophaseSlugOverride()
        {
            var text = (ExophaseSlugInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_Menu_ExophaseSlug_Empty", "Please enter an Exophase game slug."),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (TrySetExophaseSlugOverride(text))
            {
                Reload();
            }
        }

        private void ClearExophaseSlugOverride()
        {
            if (TryClearExophaseSlugOverride())
            {
                Reload();
            }
        }

        private bool TrySetExophaseSlugOverride(string slug)
        {
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null || string.IsNullOrWhiteSpace(slug))
            {
                return false;
            }

            if (!ExophaseDataProvider.TrySetSlugOverride(_gameId, game.Name, slug, _persistSettingsForUi, _logger))
            {
                return false;
            }

            TriggerRefresh();
            return true;
        }

        private bool TryClearExophaseSlugOverride()
        {
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null)
            {
                return false;
            }

            if (!ExophaseDataProvider.TryClearSlugOverride(_gameId, game.Name, _persistSettingsForUi, _logger))
            {
                return false;
            }

            TriggerRefresh();
            return true;
        }

        private void TriggerRefresh()
        {
            _ = _plugin?.RefreshEntryPoint?.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = _gameId
                },
                RefreshExecutionPolicy.ProgressWindow(_gameId));
        }

        private void UnlinkManualTracking()
        {
            if (TryUnlinkManualTracking())
            {
                Reload();
            }
        }

        private bool TryUnlinkManualTracking()
        {
            if (!HasGame)
            {
                return false;
            }

            return ManualAchievementsProvider.TryUnlinkGameOptionsLink(
                _gameId,
                GameName,
                _playniteApi,
                _achievementOverridesService);
        }

        private async Task RefreshGameAsync()
        {
            if (!HasGame || IsRefreshing)
            {
                return;
            }

            try
            {
                IsRefreshing = true;
                if (_plugin?.RefreshEntryPoint != null)
                {
                    await _plugin.RefreshEntryPoint.ExecuteAsync(
                        new RefreshRequest
                        {
                            Mode = RefreshModeType.Single,
                            SingleGameId = _gameId
                        },
                        RefreshExecutionPolicy.ProgressWindow(_gameId)).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException("RefreshEntryPoint is not available.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to refresh game options data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_Error_RefreshFailed", "Refresh failed: {0}"),
                        ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsRefreshing = false;
                Reload();
            }
        }

        private void ClearGameData()
        {
            if (!HasGame)
            {
                return;
            }

            var result = _playniteApi?.Dialogs?.ShowMessage(
                string.Format(
                    L("LOCPlayAch_Menu_ClearData_ConfirmSingle", "Clear cached data for \"{0}\"?\n\nThis removes cached achievements and icons for this game. Refresh again to fetch fresh data."),
                    GameName),
                L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) ?? System.Windows.MessageBoxResult.None;

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                if (_achievementOverridesService != null)
                {
                    _achievementOverridesService.ClearGameData(_gameId, GameName);
                }
                else
                {
                    _refreshService?.Cache?.RemoveGameCache(_gameId);
                }

                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_Menu_ClearData_SuccessSingle", "Cleared cached data for \"{0}\"."),
                        GameName),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear cached data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_Menu_ClearData_Failed", "Failed to clear cached data: {0}"),
                        ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                Reload();
            }
        }

        private void RaiseCommandStates()
        {
            OpenAchievementsCommand?.RaiseCanExecuteChanged();
            ToggleExclusionCommand?.RaiseCanExecuteChanged();
            ToggleSummaryExclusionCommand?.RaiseCanExecuteChanged();
            ApplyRaOverrideCommand?.RaiseCanExecuteChanged();
            ClearRaOverrideCommand?.RaiseCanExecuteChanged();
            ApplyExophaseSlugOverrideCommand?.RaiseCanExecuteChanged();
            ClearExophaseSlugOverrideCommand?.RaiseCanExecuteChanged();
            UnlinkManualTrackingCommand?.RaiseCanExecuteChanged();
            RefreshStateCommand?.RaiseCanExecuteChanged();
            RefreshGameCommand?.RaiseCanExecuteChanged();
            ClearGameDataCommand?.RaiseCanExecuteChanged();
        }

        private bool ShouldWarnAboutManualTrackingOverride(out string providerKey)
        {
            providerKey = (_cachedProviderKey ?? string.Empty).Trim();
            if (!HasCachedData || !_cachedHasAchievements || string.IsNullOrWhiteSpace(providerKey))
            {
                return false;
            }

            return !string.Equals(providerKey, "Manual", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveProviderDisplayName(GameAchievementData gameData)
        {
            var providerKey = gameData?.EffectiveProviderKey;
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return L("LOCPlayAch_GameOptions_Value_NotAvailable", "N/A");
            }

            var displayName = ProviderRegistry.GetLocalizedName(providerKey);
            return string.IsNullOrWhiteSpace(displayName)
                ? L("LOCPlayAch_GameOptions_Value_NotAvailable", "N/A")
                : displayName;
        }

        private string ResolveLibrarySourceDisplayName(Playnite.SDK.Models.Game game, string cachedLibrarySource)
        {
            var fallback = L("LOCPlayAch_GameOptions_Value_NotAvailable", "N/A");

            if (!string.IsNullOrWhiteSpace(game?.Source?.Name))
            {
                return game.Source.Name.Trim();
            }

            var candidate = (cachedLibrarySource ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return fallback;
            }

            if (Guid.TryParse(candidate, out var sourceId))
            {
                var sourceName = _playniteApi?.Database?.Sources?
                    .FirstOrDefault(s => s != null && s.Id == sourceId)?.Name;
                if (!string.IsNullOrWhiteSpace(sourceName))
                {
                    return sourceName.Trim();
                }

                if (game != null && game.PluginId == sourceId && !string.IsNullOrWhiteSpace(ProviderName))
                {
                    return ProviderName;
                }
            }

            return candidate;
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}





