using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
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
    public sealed class GameOptionsViewModel : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Guid _gameId;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly RefreshRuntime _refreshService;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly GameOptionsDataSnapshotProvider _gameDataSnapshotProvider;
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
        private bool _useSeparateLockedIconsOverride;
        private bool _useExophaseForGame;
        private bool _isExophaseManagedByPlatform;
        private string _exophaseAutoSlug;
        private string _exophaseSlugOverrideValue;
        private string _exophaseSlugInput;
        private bool _hasExophaseSlugOverride;
        private bool _canExportCustomJson;
        private bool _canClearCustomData;
        private int _customDataRevision;

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
        public RelayCommand ExportCustomJsonCommand { get; }
        public RelayCommand ExportCustomPackageCommand { get; }
        public RelayCommand ImportCustomJsonCommand { get; }
        public RelayCommand ClearCustomDataCommand { get; }

        public GameOptionsViewModel(
            Guid gameId,
            GameOptionsTab initialTab,
            PlayniteAchievementsPlugin plugin,
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            GameOptionsDataSnapshotProvider gameDataSnapshotProvider,
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
            _gameDataSnapshotProvider = gameDataSnapshotProvider;
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
            ExportCustomJsonCommand = new RelayCommand(_ => ExportCustomJson(), _ => HasGame && CanExportCustomJson);
            ExportCustomPackageCommand = new RelayCommand(_ => ExportCustomPackage(), _ => HasGame && CanExportCustomJson);
            ImportCustomJsonCommand = new RelayCommand(_ => ImportCustomJson(), _ => HasGame);
            ClearCustomDataCommand = new RelayCommand(_ => ClearCustomData(), _ => HasGame && CanClearCustomData);

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
                     value == GameOptionsTab.Category ||
                     value == GameOptionsTab.CustomIcons))
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

        public bool UseSeparateLockedIconsOverride
        {
            get => _useSeparateLockedIconsOverride;
            set
            {
                if (!HasGame)
                {
                    return;
                }

                if (SetValueAndReturn(ref _useSeparateLockedIconsOverride, value))
                {
                    _achievementOverridesService?.SetSeparateLockedIconOverride(_gameId, value);
                    RefreshCustomDataState();
                    OnPropertyChanged(nameof(SeparateLockedIconsStatusText));
                }
            }
        }

        public string SeparateLockedIconsStatusText
        {
            get
            {
                if (UseSeparateLockedIconsOverride)
                {
                    return L(
                        "LOCPlayAch_GameOptions_Overrides_LockedIcons_StatusOverride",
                        "Enabled via override");
                }

                if (GameCustomDataLookup.ShouldUseSeparateLockedIcons(_gameId, _settings?.Persisted))
                {
                    return L(
                        "LOCPlayAch_GameOptions_Overrides_LockedIcons_StatusSettings",
                        "Enabled via settings");
                }

                return L(
                    "LOCPlayAch_GameOptions_Overrides_LockedIcons_StatusDisabled",
                    "Disabled");
            }
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
                        RefreshCustomDataState();
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

                var percent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(UnlockedAchievements, TotalAchievements);
                return $"{UnlockedAchievements} / {TotalAchievements} ({percent}%)";
            }
        }

        public int CompletionPercentValue => AchievementCompletionPercentCalculator.ComputeRoundedPercent(UnlockedAchievements, TotalAchievements);

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

        public bool CanExportCustomJson
        {
            get => _canExportCustomJson;
            private set
            {
                if (SetValueAndReturn(ref _canExportCustomJson, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool CanClearCustomData
        {
            get => _canClearCustomData;
            private set
            {
                if (SetValueAndReturn(ref _canClearCustomData, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public int CustomDataRevision
        {
            get => _customDataRevision;
            private set => SetValue(ref _customDataRevision, value);
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

                var gameData = _gameDataSnapshotProvider?.GetHydratedGameData() ??
                    _plugin?.AchievementDataService?.GetGameAchievementData(_gameId);
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

                var currentCustomData = TryLoadStoredCustomData(_plugin?.GameCustomDataStore);
                IsExcluded = isExcluded;
                IsExcludedFromSummaries = GameCustomDataLookup.IsExcludedFromSummaries(_gameId, _settings?.Persisted);
                SetValue(
                    ref _useSeparateLockedIconsOverride,
                    currentCustomData?.UseSeparateLockedIconsOverride == true);
                OnPropertyChanged(nameof(SeparateLockedIconsStatusText));

                var raProvider = _refreshService?.Providers
                    ?.FirstOrDefault(p => p.ProviderKey == "RetroAchievements");
                IsRaCapable = raProvider?.IsCapable(game) == true ||
                              RetroAchievementsDataProvider.CanSetOverride(game);
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
                RefreshCustomDataState();

                if (!ShowManualTrackingTab && SelectedTab == GameOptionsTab.ManualTracking)
                {
                    SelectedTab = GameOptionsTab.Overview;
                }

                if (!HasCapstoneData &&
                    (SelectedTab == GameOptionsTab.Capstones ||
                     SelectedTab == GameOptionsTab.AchievementOrder ||
                     SelectedTab == GameOptionsTab.Category ||
                     SelectedTab == GameOptionsTab.CustomIcons))
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

        private void ExportCustomJson()
        {
            if (!HasGame)
            {
                return;
            }

            try
            {
                var store = _plugin?.GameCustomDataStore;
                if (store == null)
                {
                    throw new InvalidOperationException("Game custom data store is not available.");
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "Playnite Achievements Portable (*.pa)|*.pa",
                    AddExtension = true,
                    DefaultExt = GameCustomDataStore.PortableFileExtension,
                    FileName = BuildDefaultPortablePaFileName()
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var destinationPath = NormalizePortableExportPath(
                    dialog.FileName,
                    GameCustomDataStore.PortableFileExtension);
                var result = store.ExportPortablePa(_gameId, destinationPath);
                var successMessage = string.Format(
                    L("LOCPlayAch_GameOptions_Overrides_ExportSuccess", "Exported custom game data to:\n{0}"),
                    result.DestinationPath);
                if (result.HasOmittedLocalIconOverrides)
                {
                    successMessage += "\n\n" + string.Format(
                        L(
                            "LOCPlayAch_GameOptions_Overrides_ExportPaOmittedLocalIcons",
                            ".PA export omitted {0} local image override(s). Use .PA.ZIP to export full image sets."),
                        result.OmittedLocalIconOverrideCount);
                }

                _playniteApi?.Dialogs?.ShowMessage(
                    successMessage,
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed exporting custom game data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_GameOptions_Overrides_ExportFailed", "Failed to export custom game data: {0}"),
                        ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Reload();
            }
        }

        private void ExportCustomPackage()
        {
            if (!HasGame)
            {
                return;
            }

            try
            {
                var store = _plugin?.GameCustomDataStore;
                if (store == null)
                {
                    throw new InvalidOperationException("Game custom data store is not available.");
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "Playnite Achievements Package (*.pa.zip)|*.pa.zip",
                    AddExtension = true,
                    DefaultExt = ".zip",
                    FileName = BuildDefaultPortablePackageFileName()
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var destinationPath = NormalizePortableExportPath(
                    dialog.FileName,
                    GameCustomDataStore.PortablePackageFileExtension);
                store.ExportPortablePackage(_gameId, destinationPath);
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_GameOptions_Overrides_ExportSuccess", "Exported custom game data to:\n{0}"),
                        destinationPath),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed exporting custom game package for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_GameOptions_Overrides_ExportFailed", "Failed to export custom game data: {0}"),
                        ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Reload();
            }
        }

        private void ImportCustomJson()
        {
            if (!HasGame)
            {
                return;
            }

            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Playnite Achievements Files (*.pa;*.pa.zip)|*.pa;*.pa.zip|Playnite Achievements Portable (*.pa)|*.pa|Playnite Achievements Package (*.pa.zip)|*.pa.zip",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var store = _plugin?.GameCustomDataStore;
                if (store == null)
                {
                    throw new InvalidOperationException("Game custom data store is not available.");
                }

                var previousData = TryLoadStoredCustomData(store);
                var importResult = store.ImportReplacePortable(_gameId, dialog.FileName);
                var currentData = importResult?.ImportedData;
                if (currentData == null)
                {
                    throw new InvalidOperationException("Imported custom game data was empty.");
                }

                var transitionEffects = AnalyzeCustomDataTransition(previousData, currentData);
                NotifyCustomDataChanged(transitionEffects.RequiresRefresh, transitionEffects.ForceIconRefresh);

                var successMessage = L(
                    "LOCPlayAch_GameOptions_Overrides_ImportSuccess",
                    "Imported custom game data for this game.");
                if (importResult.HasIgnoredPackageImages)
                {
                    successMessage += "\n\n" + string.Format(
                        L(
                            "LOCPlayAch_GameOptions_Overrides_ImportIgnoredPackageImages",
                            "Ignored {0} image file(s) because their file names did not match this game's achievement API names."),
                        importResult.IgnoredPackageImageCount);
                }

                _playniteApi?.Dialogs?.ShowMessage(
                    successMessage,
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    importResult.HasIgnoredPackageImages
                        ? MessageBoxImage.Warning
                        : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed importing custom game data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_GameOptions_Overrides_ImportFailed", "Failed to import custom game data: {0}"),
                        ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ClearCustomData()
        {
            if (!HasGame)
            {
                return;
            }

            var store = _plugin?.GameCustomDataStore;
            if (store == null || !store.TryLoad(_gameId, out var currentData) || currentData == null)
            {
                return;
            }

            var result = _playniteApi?.Dialogs?.ShowMessage(
                string.Format(
                    L(
                        "LOCPlayAch_GameOptions_Overrides_ClearCustomDataConfirm",
                        "Clear all custom data for \"{0}\"?\n\nThis removes per-game exclusions, manual links, capstones, order/category changes, and provider overrides stored by Playnite Achievements. Cached achievement data is not removed."),
                    GameName),
                L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                store.Delete(_gameId);
                var transitionEffects = AnalyzeCustomDataTransition(currentData, null);
                NotifyCustomDataChanged(transitionEffects.RequiresRefresh, transitionEffects.ForceIconRefresh);

                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L(
                            "LOCPlayAch_GameOptions_Overrides_ClearCustomDataSuccess",
                            "Cleared custom data for \"{0}\"."),
                        GameName),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed clearing custom data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L(
                            "LOCPlayAch_GameOptions_Overrides_ClearCustomDataFailed",
                            "Failed to clear custom data: {0}"),
                        ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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

        private void TriggerRefresh(bool forceIconRefresh = false)
        {
            _ = _plugin?.RefreshEntryPoint?.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = _gameId,
                    ForceIconRefresh = forceIconRefresh
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
            ExportCustomJsonCommand?.RaiseCanExecuteChanged();
            ExportCustomPackageCommand?.RaiseCanExecuteChanged();
            ImportCustomJsonCommand?.RaiseCanExecuteChanged();
            ClearCustomDataCommand?.RaiseCanExecuteChanged();
        }

        private void RefreshCustomDataState()
        {
            var store = _plugin?.GameCustomDataStore;
            GameCustomDataFile currentData = null;
            var hasStoredData = HasGame && store != null && store.TryLoad(_gameId, out currentData) && currentData != null;
            CanClearCustomData = hasStoredData;
            CanExportCustomJson = hasStoredData && GameCustomDataNormalizer.HasPortableData(currentData);
        }

        internal void NotifyCustomDataChanged(
            bool requiresRefresh,
            bool forceIconRefresh = false,
            bool syncTags = true)
        {
            _gameDataSnapshotProvider?.Invalidate();
            _refreshService?.Cache?.NotifyCacheInvalidated();
            if (syncTags)
            {
                _plugin?.TagSyncService?.SyncTagsForGames(new List<Guid> { _gameId });
            }

            if (_settings?.SelectedGame?.Id == _gameId)
            {
                _plugin?.ThemeUpdateService?.RequestUpdate(_gameId);
            }

            Reload();
            CustomDataRevision = unchecked(CustomDataRevision + 1);

            if (requiresRefresh)
            {
                TriggerRefresh(forceIconRefresh);
            }
        }

        internal void NotifyIconOverridesChanged()
        {
            _gameDataSnapshotProvider?.Invalidate();
            _refreshService?.Cache?.NotifyCacheInvalidated();
            if (_settings?.SelectedGame?.Id == _gameId)
            {
                _plugin?.ThemeUpdateService?.RequestUpdate(_gameId);
            }

            RefreshCustomDataState();
            TriggerRefresh(forceIconRefresh: true);
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

        private string BuildDefaultPortablePaFileName()
        {
            return BuildDefaultPortableFileBaseName() + GameCustomDataStore.PortableFileExtension;
        }

        private string BuildDefaultPortablePackageFileName()
        {
            return BuildDefaultPortableFileBaseName() + GameCustomDataStore.PortablePackageFileExtension;
        }

        private string BuildDefaultPortableFileBaseName()
        {
            var preferredName = SanitizePortableFileNamePart(
                string.IsNullOrWhiteSpace(GameName) ? _gameId.ToString("D") : GameName);
            var providerStub = SanitizePortableFileNamePart(
                _gameDataSnapshotProvider?.GetHydratedGameData()?.EffectiveProviderKey ??
                _plugin?.AchievementDataService?.GetGameAchievementData(_gameId)?.EffectiveProviderKey ??
                _cachedProviderKey);

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                parts.Add(preferredName);
            }

            if (!string.IsNullOrWhiteSpace(providerStub))
            {
                parts.Add(providerStub);
            }

            if (parts.Count == 0)
            {
                parts.Add(_gameId.ToString("D"));
            }

            return string.Join("_", parts);
        }

        private static string SanitizePortableFileNamePart(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(
                normalized.Select(ch => invalidChars.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray());

            while (sanitized.Contains("__"))
            {
                sanitized = sanitized.Replace("__", "_");
            }

            sanitized = sanitized.Trim('_', '.');
            return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
        }

        private static string NormalizePortableExportPath(string path, string extension)
        {
            var normalized = (path ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            foreach (var suffix in new[]
            {
                GameCustomDataStore.PortablePackageFileExtension,
                GameCustomDataStore.PortableFileExtension,
                ".json",
                ".zip"
            })
            {
                if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - suffix.Length);
                    break;
                }
            }

            return normalized + extension;
        }

        private static bool StoredDataRequiresRefresh(GameCustomDataFile data)
        {
            return data?.ManualLink != null ||
                   data?.RetroAchievementsGameIdOverride.HasValue == true ||
                   data?.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data?.ExophaseSlugOverride);
        }

        private static CustomDataTransitionEffects AnalyzeCustomDataTransition(
            GameCustomDataFile previousData,
            GameCustomDataFile currentData)
        {
            var forceIconRefresh = HaveIconOverridesChanged(previousData, currentData);
            return new CustomDataTransitionEffects(
                StoredDataRequiresRefresh(previousData) ||
                StoredDataRequiresRefresh(currentData) ||
                forceIconRefresh,
                forceIconRefresh);
        }

        private GameCustomDataFile TryLoadStoredCustomData(GameCustomDataStore store)
        {
            if (store == null)
            {
                return null;
            }

            return store.TryLoad(_gameId, out var currentData)
                ? currentData
                : null;
        }

        private static bool HaveIconOverridesChanged(
            GameCustomDataFile previousData,
            GameCustomDataFile currentData)
        {
            return !AreStringMapsEqual(
                       previousData?.AchievementUnlockedIconOverrides,
                       currentData?.AchievementUnlockedIconOverrides) ||
                   !AreStringMapsEqual(
                       previousData?.AchievementLockedIconOverrides,
                       currentData?.AchievementLockedIconOverrides);
        }

        private static bool AreStringMapsEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
        {
            var normalizedLeft = NormalizeStringMap(left);
            var normalizedRight = NormalizeStringMap(right);
            if (normalizedLeft.Count != normalizedRight.Count)
            {
                return false;
            }

            foreach (var pair in normalizedLeft)
            {
                if (!normalizedRight.TryGetValue(pair.Key, out var value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, string> NormalizeStringMap(IReadOnlyDictionary<string, string> source)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return normalized;
            }

            foreach (var pair in source)
            {
                var key = NormalizeOverrideValue(pair.Key);
                var value = NormalizeOverrideValue(pair.Value);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                normalized[key] = value;
            }

            return normalized;
        }

        private static string NormalizeOverrideValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private readonly struct CustomDataTransitionEffects
        {
            public CustomDataTransitionEffects(bool requiresRefresh, bool forceIconRefresh)
            {
                RequiresRefresh = requiresRefresh;
                ForceIconRefresh = forceIconRefresh;
            }

            public bool RequiresRefresh { get; }
            public bool ForceIconRefresh { get; }
        }
    }
}





