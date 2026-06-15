using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Services;
using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public sealed class ManageAchievementsViewModel : PlayniteAchievements.Common.ObservableObject
    {
        private const string ProviderOverrideNoneKey = "None";

        public sealed class ProviderOverrideOption
        {
            public string ProviderKey { get; set; }

            public string DisplayName { get; set; }
        }

        private readonly Guid _gameId;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly RefreshRuntime _refreshService;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly AchievementPageLinkResolver _achievementPageLinkResolver;

        private ManageAchievementsTab _selectedTab;
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
        private bool _hasManualTrackingLink;
        private string _manualTrackingSummary;
        private bool _hasCapstoneData;
        private bool _hasAchievementPageLink;
        private string _capstoneEmptyMessage;
        private bool _isRefreshing;
        private string _cachedProviderKey;
        private bool _cachedHasAchievements;
        private string _manualTrackingWarningAcceptedForProvider;
        private bool _showManualTrackingTab = true;
        private bool _useSeparateLockedIconsOverride;
        private bool _isLoadingProviderOverride;
        private string _selectedProviderOverrideKey = ProviderOverrideNoneKey;
        private string _providerOverrideInput;
        private bool _hasProviderOverride;
        private string _providerOverrideKey = ProviderOverrideNoneKey;
        private string _providerOverrideValue;
        private bool _canExportCustomJson;
        private bool _canClearCustomData;
        private int _customDataRevision;

        public IReadOnlyList<ProviderOverrideOption> ProviderOverrideOptions { get; }

        public RelayCommand OpenAchievementsCommand { get; }
        public AsyncCommand OpenAchievementPageCommand { get; }
        public RelayCommand ToggleExclusionCommand { get; }
        public RelayCommand ToggleSummaryExclusionCommand { get; }
        public RelayCommand ApplyProviderOverrideCommand { get; }
        public RelayCommand ClearProviderOverrideCommand { get; }
        public RelayCommand UnlinkManualTrackingCommand { get; }
        public RelayCommand RefreshStateCommand { get; }
        public AsyncCommand RefreshGameCommand { get; }
        public RelayCommand ClearGameDataCommand { get; }
        public RelayCommand ExportCustomJsonCommand { get; }
        public RelayCommand ExportCustomPackageCommand { get; }
        public RelayCommand ImportCustomJsonCommand { get; }
        public RelayCommand ClearCustomDataCommand { get; }

        public ManageAchievementsViewModel(
            Guid gameId,
            ManageAchievementsTab initialTab,
            PlayniteAchievementsPlugin plugin,
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
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
            _achievementPageLinkResolver = new AchievementPageLinkResolver(_refreshService?.Providers);
            ProviderOverrideOptions = BuildProviderOverrideOptions();

            OpenAchievementsCommand = new RelayCommand(_ => OpenAchievements(), _ => HasGame);
            OpenAchievementPageCommand = new AsyncCommand(_ => OpenAchievementPageAsync(), _ => HasGame && HasAchievementPageLink);
            ToggleExclusionCommand = new RelayCommand(_ => ToggleExclusion(), _ => HasGame);
            ToggleSummaryExclusionCommand = new RelayCommand(_ => ToggleSummaryExclusion(), _ => HasGame);
            ApplyProviderOverrideCommand = new RelayCommand(_ => ApplyProviderOverride(), _ => HasGame);
            ClearProviderOverrideCommand = new RelayCommand(_ => ClearProviderOverride(), _ => HasGame && HasProviderOverride);
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

        public ManageAchievementsTab SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (_selectedTab == value)
                {
                    return;
                }

                if (value == ManageAchievementsTab.ManualTracking && !ShowManualTrackingTab)
                {
                    return;
                }

                if (!HasCapstoneData &&
                    (value == ManageAchievementsTab.Capstones ||
                     value == ManageAchievementsTab.AchievementOrder ||
                     value == ManageAchievementsTab.Category ||
                     value == ManageAchievementsTab.Filters ||
                     value == ManageAchievementsTab.Notes ||
                     value == ManageAchievementsTab.CustomIcons))
                {
                    return;
                }

                if (value == ManageAchievementsTab.ManualTracking &&
                    ShouldWarnAboutManualTrackingOverride(out var existingProviderKey) &&
                    !string.Equals(_manualTrackingWarningAcceptedForProvider, existingProviderKey, StringComparison.OrdinalIgnoreCase))
                {
                    var displayName = ProviderRegistry.GetLocalizedName(existingProviderKey);
                    var message = string.Format(
                        L(
                            "LOCPlayAch_ManageAchievements_Manual_ReplaceProviderWarning",
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
                        "LOCPlayAch_ManageAchievements_Overrides_LockedIcons_StatusOverride",
                        "Enabled via override");
                }

                if (GameCustomDataLookup.ShouldUseSeparateLockedIcons(_gameId, _settings?.Persisted))
                {
                    return L(
                        "LOCPlayAch_ManageAchievements_Overrides_LockedIcons_StatusSettings",
                        "Enabled via settings");
                }

                return L(
                    "LOCPlayAch_Common_Status_Disabled",
                    "Disabled");
            }
        }

        public string SelectedProviderOverrideKey
        {
            get => _selectedProviderOverrideKey;
            set
            {
                var normalized = NormalizeProviderOverrideSelection(value);
                if (SetValueAndReturn(ref _selectedProviderOverrideKey, normalized))
                {
                    if (!_isLoadingProviderOverride)
                    {
                        ProviderOverrideInput = string.Empty;
                    }

                    OnProviderOverrideSelectionChanged();
                }
            }
        }

        public string ProviderOverrideInput
        {
            get => _providerOverrideInput;
            set
            {
                if (SetValueAndReturn(ref _providerOverrideInput, value ?? string.Empty))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasProviderOverride
        {
            get => _hasProviderOverride;
            private set
            {
                if (SetValueAndReturn(ref _hasProviderOverride, value))
                {
                    OnPropertyChanged(nameof(ProviderOverrideStatusText));
                    OnPropertyChanged(nameof(ProviderOverrideSummaryText));
                    RaiseCommandStates();
                }
            }
        }

        public string ProviderOverrideValue
        {
            get => _providerOverrideValue;
            private set
            {
                if (SetValueAndReturn(ref _providerOverrideValue, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(ProviderOverrideStatusText));
                    OnPropertyChanged(nameof(ProviderOverrideSummaryText));
                }
            }
        }

        public bool IsProviderOverrideProviderSelected =>
            !string.Equals(SelectedProviderOverrideKey, ProviderOverrideNoneKey, StringComparison.OrdinalIgnoreCase);

        public string ProviderOverrideInputLabel => GetProviderOverrideInputLabel(SelectedProviderOverrideKey);

        public string ProviderOverrideStatusText
        {
            get
            {
                if (!HasProviderOverride)
                {
                    return L("LOCPlayAch_Common_Status_NoOverrideSet", "No override set");
                }

                var providerName = GetProviderOverrideDisplayName(_providerOverrideKey);
                if (string.Equals(_providerOverrideKey, "Exophase", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(ProviderOverrideValue))
                {
                    return string.Format(
                        L("LOCPlayAch_ManageAchievements_Overrides_ProviderStatusAuto", "Override set: {0} (auto-detect)"),
                        providerName);
                }

                return string.Format(
                    L("LOCPlayAch_ManageAchievements_Overrides_ProviderStatusValue", "Override set: {0} - {1}"),
                    providerName,
                    ProviderOverrideValue);
            }
        }

        public string ProviderOverrideSummaryText => ProviderOverrideStatusText;

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

        public bool HasAchievementPageLink
        {
            get => _hasAchievementPageLink;
            private set
            {
                if (SetValueAndReturn(ref _hasAchievementPageLink, value))
                {
                    RaiseCommandStates();
                }
            }
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
            ? L("LOCPlayAch_ManageAchievements_Status_ExcludedFromRefreshes", "Excluded from Refreshes")
            : L("LOCPlayAch_ManageAchievements_Status_IncludedFromRefreshes", "Included from Refreshes");

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
            ? L("LOCPlayAch_ManageAchievements_Status_ExcludedFromSummaries", "Excluded from Summaries")
            : L("LOCPlayAch_ManageAchievements_Status_IncludedFromSummaries", "Included in Summaries");

        public string SummaryExclusionActionText => IsExcludedFromSummaries
            ? L("LOCPlayAch_Common_Action_IncludeInSummaries", "Include in Summaries")
            : L("LOCPlayAch_Common_Action_ExcludeFromSummaries", "Exclude from Summaries");

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
            ? L("LOCPlayAch_Common_Status_Linked", "Linked")
            : L("LOCPlayAch_Common_Status_NotLinked", "Not linked");

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

        private GameAchievementData GetHydratedGameData()
        {
            return _gameDataSnapshotProvider?.GetHydratedGameData() ??
                   _plugin?.AchievementDataService?.GetGameAchievementData(_gameId);
        }

        private GameAchievementData GetRawGameData()
        {
            return _gameDataSnapshotProvider?.GetRawGameData() ??
                   _plugin?.AchievementDataService?.GetRawGameAchievementData(_gameId);
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

                var gameData = GetHydratedGameData();
                var rawGameData = GetRawGameData();
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
                    LastUpdatedUtcText = L("LOCPlayAch_ManageAchievements_Value_NotAvailable", "N/A");
                    LastUpdatedLocalText = L("LOCPlayAch_ManageAchievements_Value_NotAvailable", "N/A");
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
                        : L("LOCPlayAch_CustomRefresh_None", "None");

                HasCapstoneData = (gameData?.HasAchievements ?? false) && list.Count > 0;
                CapstoneEmptyMessage = L(
                    "LOCPlayAch_Common_NoCachedAchievementsForGame",
                    "No cached achievements are available for this game.");

                var currentCustomData = TryLoadStoredCustomData(_plugin?.GameCustomDataStore);
                IsExcluded = isExcluded;
                IsExcludedFromSummaries = GameCustomDataLookup.IsExcludedFromSummaries(_gameId, _settings?.Persisted);
                SetValue(
                    ref _useSeparateLockedIconsOverride,
                    currentCustomData?.UseSeparateLockedIconsOverride == true);
                OnPropertyChanged(nameof(SeparateLockedIconsStatusText));
                ReloadProviderOverrideState(currentCustomData);

                ManualAchievementLink manualLink;
                var hasManualLink = ManualAchievementsProvider.TryGetManualLink(_gameId, out manualLink);
                HasManualTrackingLink = hasManualLink;
                ManualTrackingSummary = ManualAchievementsProvider.GetManageAchievementsLinkSummary(manualLink);
                HasAchievementPageLink = HasGame && _achievementPageLinkResolver.CanResolve(
                    new AchievementPageLinkContext(game, gameData, rawGameData, manualLink));

                RefreshCustomDataState();

                if (!ShowManualTrackingTab && SelectedTab == ManageAchievementsTab.ManualTracking)
                {
                    SelectedTab = ManageAchievementsTab.Overview;
                }

                if (!HasCapstoneData &&
                    (SelectedTab == ManageAchievementsTab.Capstones ||
                     SelectedTab == ManageAchievementsTab.AchievementOrder ||
                     SelectedTab == ManageAchievementsTab.Category ||
                     SelectedTab == ManageAchievementsTab.Filters ||
                     SelectedTab == ManageAchievementsTab.CustomIcons))
                {
                    SelectedTab = ManageAchievementsTab.Overview;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to load Manage Achievements state for gameId={_gameId}");
            }
            finally
            {
                RaiseCommandStates();
            }
        }

        private void OpenAchievements()
        {
            _plugin?.OpenViewAchievementsWindow(_gameId);
        }

        private async Task OpenAchievementPageAsync()
        {
            try
            {
                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                ManualAchievementLink manualLink;
                ManualAchievementsProvider.TryGetManualLink(_gameId, out manualLink);

                var url = await _achievementPageLinkResolver.ResolveUrlAsync(
                    new AchievementPageLinkContext(game, GetHydratedGameData(), GetRawGameData(), manualLink),
                    CancellationToken.None);

                if (string.IsNullOrWhiteSpace(url))
                {
                    ShowAchievementPageUnavailable();
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed opening achievement page for gameId={_gameId}.");
                ShowAchievementPageUnavailable();
            }
        }

        private void ShowAchievementPageUnavailable()
        {
            _playniteApi?.Dialogs?.ShowMessage(
                L(
                    "LOCPlayAch_ManageAchievements_Overview_AchievementPageUnavailable",
                    "No achievement page link is available for this game."),
                L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

        private void ApplyProviderOverride()
        {
            var providerKey = NormalizeProviderOverrideSelection(SelectedProviderOverrideKey);
            if (string.Equals(providerKey, ProviderOverrideNoneKey, StringComparison.OrdinalIgnoreCase))
            {
                if (TryClearProviderOverride())
                {
                    Reload();
                }

                return;
            }

            if (!TryCreateProviderOverride(providerKey, ProviderOverrideInput, out var providerOverride, out var validationMessageKey, out var validationMessageFallback))
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    L(validationMessageKey, validationMessageFallback),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (TrySetProviderOverride(providerOverride))
            {
                Reload();
            }
        }

        private void ClearProviderOverride()
        {
            if (TryClearProviderOverride())
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
                var successMessage = L("LOCPlayAch_Status_Succeeded", "Success!") + "\n" + result.DestinationPath;
                if (result.HasOmittedLocalIconOverrides)
                {
                    successMessage += "\n\n" + string.Format(
                        L(
                            "LOCPlayAch_ManageAchievements_Overrides_ExportPaOmittedLocalIcons",
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
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
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
                    L("LOCPlayAch_Status_Succeeded", "Success!") + "\n" + destinationPath,
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed exporting custom game package for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
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

                var successMessage = L("LOCPlayAch_Status_Succeeded", "Success!");
                if (importResult.HasIgnoredPackageImages)
                {
                    successMessage += "\n\n" + string.Format(
                        L(
                            "LOCPlayAch_ManageAchievements_Overrides_ImportIgnoredPackageImages",
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
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
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
                        "LOCPlayAch_ManageAchievements_Overrides_ClearCustomDataConfirm",
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
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed clearing custom data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Reload();
            }
        }

        private bool TrySetProviderOverride(ProviderOverrideData providerOverride)
        {
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null || providerOverride == null)
            {
                return false;
            }

            if (_achievementOverridesService != null)
            {
                _achievementOverridesService.SetProviderOverride(_gameId, providerOverride);
            }
            else
            {
                var store = _plugin?.GameCustomDataStore;
                if (store == null)
                {
                    return false;
                }

                store.Update(_gameId, customData =>
                {
                    customData.ProviderOverride = providerOverride.Clone();
                });
            }

            _persistSettingsForUi?.Invoke();
            _logger?.Info($"Set provider override for '{game.Name}' to {providerOverride.ProviderKey}:{providerOverride.Value ?? string.Empty}");
            TriggerRefresh();
            return true;
        }

        private bool TryClearProviderOverride()
        {
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null)
            {
                return false;
            }

            if (_achievementOverridesService != null)
            {
                _achievementOverridesService.SetProviderOverride(_gameId, null);
            }
            else
            {
                var store = _plugin?.GameCustomDataStore;
                if (store == null)
                {
                    return false;
                }

                store.Update(_gameId, customData =>
                {
                    customData.ProviderOverride = null;
                });
            }

            _persistSettingsForUi?.Invoke();
            _logger?.Info($"Cleared provider override for '{game.Name}'");
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

            return ManualAchievementsProvider.TryUnlinkManageAchievementsLink(
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
                _logger?.Error(ex, $"Failed to refresh manage achievements data for gameId={_gameId}");
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
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear cached data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
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
            OpenAchievementPageCommand?.RaiseCanExecuteChanged();
            ToggleExclusionCommand?.RaiseCanExecuteChanged();
            ToggleSummaryExclusionCommand?.RaiseCanExecuteChanged();
            ApplyProviderOverrideCommand?.RaiseCanExecuteChanged();
            ClearProviderOverrideCommand?.RaiseCanExecuteChanged();
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
            bool forceIconRefresh = false)
        {
            _gameDataSnapshotProvider?.Invalidate();
            _refreshService?.Cache?.NotifyCacheInvalidated();

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
                return L("LOCPlayAch_ManageAchievements_Value_NotAvailable", "N/A");
            }

            var displayName = ProviderRegistry.GetLocalizedName(providerKey);
            return string.IsNullOrWhiteSpace(displayName)
                ? L("LOCPlayAch_ManageAchievements_Value_NotAvailable", "N/A")
                : displayName;
        }

        private string ResolveLibrarySourceDisplayName(Playnite.SDK.Models.Game game, string cachedLibrarySource)
        {
            var fallback = L("LOCPlayAch_ManageAchievements_Value_NotAvailable", "N/A");

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

        private void ReloadProviderOverrideState(GameCustomDataFile currentCustomData)
        {
            var providerOverride = GameCustomDataNormalizer.NormalizeProviderOverride(currentCustomData?.ProviderOverride);
            _isLoadingProviderOverride = true;
            try
            {
                if (providerOverride == null)
                {
                    HasProviderOverride = false;
                    _providerOverrideKey = ProviderOverrideNoneKey;
                    ProviderOverrideValue = string.Empty;
                    SelectedProviderOverrideKey = ProviderOverrideNoneKey;
                    ProviderOverrideInput = string.Empty;
                    return;
                }

                HasProviderOverride = true;
                _providerOverrideKey = providerOverride.ProviderKey;
                ProviderOverrideValue = providerOverride.Value ?? string.Empty;
                SelectedProviderOverrideKey = providerOverride.ProviderKey;
                ProviderOverrideInput = providerOverride.Value ?? string.Empty;
            }
            finally
            {
                _isLoadingProviderOverride = false;
                OnProviderOverrideSelectionChanged();
            }
        }

        private void OnProviderOverrideSelectionChanged()
        {
            OnPropertyChanged(nameof(IsProviderOverrideProviderSelected));
            OnPropertyChanged(nameof(ProviderOverrideInputLabel));
            OnPropertyChanged(nameof(ProviderOverrideStatusText));
            RaiseCommandStates();
        }

        private bool TryCreateProviderOverride(
            string providerKey,
            string value,
            out ProviderOverrideData providerOverride,
            out string validationMessageKey,
            out string validationMessageFallback)
        {
            providerOverride = null;
            validationMessageKey = null;
            validationMessageFallback = null;

            var normalizedKey = NormalizeProviderOverrideSelection(providerKey);
            var trimmedValue = (value ?? string.Empty).Trim();
            switch (normalizedKey)
            {
                case "Steam":
                    if (!int.TryParse(trimmedValue, out var steamAppId) || steamAppId <= 0)
                    {
                        validationMessageKey = "LOCPlayAch_Menu_SteamAppId_InvalidId";
                        validationMessageFallback = "Please enter a valid positive integer Steam AppID.";
                        return false;
                    }

                    providerOverride = new ProviderOverrideData
                    {
                        ProviderKey = normalizedKey,
                        Value = steamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    };
                    return true;

                case "RetroAchievements":
                    if (!int.TryParse(trimmedValue, out var raGameId) || raGameId <= 0)
                    {
                        validationMessageKey = "LOCPlayAch_Menu_RaGameId_InvalidId";
                        validationMessageFallback = "Please enter a valid positive integer game ID.";
                        return false;
                    }

                    providerOverride = new ProviderOverrideData
                    {
                        ProviderKey = normalizedKey,
                        Value = raGameId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    };
                    return true;

                case "Xenia":
                    if (!XeniaTitleIdHelper.TryNormalize(trimmedValue, out var xeniaTitleId))
                    {
                        validationMessageKey = "LOCPlayAch_Menu_XeniaTitleId_InvalidId";
                        validationMessageFallback = "Please enter a valid 8-character hexadecimal Xenia TitleID.";
                        return false;
                    }

                    providerOverride = new ProviderOverrideData
                    {
                        ProviderKey = normalizedKey,
                        Value = xeniaTitleId
                    };
                    return true;

                case "ShadPS4":
                    if (!ShadPS4MatchIdHelper.TryNormalize(trimmedValue, out var shadMatchId))
                    {
                        validationMessageKey = "LOCPlayAch_Menu_ShadPS4MatchId_InvalidId";
                        validationMessageFallback = "Please enter a valid ShadPS4 match ID such as CUSA00432 or NPWR12345_00.";
                        return false;
                    }

                    providerOverride = new ProviderOverrideData
                    {
                        ProviderKey = normalizedKey,
                        Value = shadMatchId
                    };
                    return true;

                case "RPCS3":
                    if (!Rpcs3MatchIdHelper.TryNormalize(trimmedValue, out var rpcs3MatchId))
                    {
                        validationMessageKey = "LOCPlayAch_Menu_Rpcs3MatchId_InvalidId";
                        validationMessageFallback = "Please enter a valid RPCS3 trophy NP Comm ID such as NPWR12345_00.";
                        return false;
                    }

                    providerOverride = new ProviderOverrideData
                    {
                        ProviderKey = normalizedKey,
                        Value = rpcs3MatchId
                    };
                    return true;

                case "Exophase":
                    providerOverride = new ProviderOverrideData
                    {
                        ProviderKey = normalizedKey,
                        Value = string.IsNullOrWhiteSpace(trimmedValue) ? null : trimmedValue
                    };
                    return true;

                default:
                    validationMessageKey = "LOCPlayAch_ManageAchievements_Overrides_ProviderInvalid";
                    validationMessageFallback = "Please select a provider override.";
                    return false;
            }
        }

        private IReadOnlyList<ProviderOverrideOption> BuildProviderOverrideOptions()
        {
            return new List<ProviderOverrideOption>
            {
                new ProviderOverrideOption
                {
                    ProviderKey = ProviderOverrideNoneKey,
                    DisplayName = L("LOCPlayAch_CustomRefresh_None", "None")
                },
                new ProviderOverrideOption
                {
                    ProviderKey = "Steam",
                    DisplayName = ProviderRegistry.GetLocalizedName("Steam")
                },
                new ProviderOverrideOption
                {
                    ProviderKey = "RetroAchievements",
                    DisplayName = ProviderRegistry.GetLocalizedName("RetroAchievements")
                },
                new ProviderOverrideOption
                {
                    ProviderKey = "ShadPS4",
                    DisplayName = ProviderRegistry.GetLocalizedName("ShadPS4")
                },
                new ProviderOverrideOption
                {
                    ProviderKey = "RPCS3",
                    DisplayName = ProviderRegistry.GetLocalizedName("RPCS3")
                },
                new ProviderOverrideOption
                {
                    ProviderKey = "Xenia",
                    DisplayName = ProviderRegistry.GetLocalizedName("Xenia")
                },
                new ProviderOverrideOption
                {
                    ProviderKey = "Exophase",
                    DisplayName = ProviderRegistry.GetLocalizedName("Exophase")
                }
            };
        }

        private string GetProviderOverrideDisplayName(string providerKey)
        {
            var normalizedKey = NormalizeProviderOverrideSelection(providerKey);
            if (string.Equals(normalizedKey, ProviderOverrideNoneKey, StringComparison.OrdinalIgnoreCase))
            {
                return L("LOCPlayAch_CustomRefresh_None", "None");
            }

            return ProviderRegistry.GetLocalizedName(normalizedKey);
        }

        private string GetProviderOverrideInputLabel(string providerKey)
        {
            switch (NormalizeProviderOverrideSelection(providerKey))
            {
                case "Steam":
                    return L("LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Steam", "Steam AppID");
                case "RetroAchievements":
                    return L("LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_RetroAchievements", "RetroAchievements Game ID");
                case "ShadPS4":
                    return L("LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_ShadPS4", "ShadPS4 Match ID");
                case "RPCS3":
                    return L("LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_RPCS3", "RPCS3 NP Comm ID");
                case "Xenia":
                    return L("LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Xenia", "Xenia TitleID");
                case "Exophase":
                    return L("LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Exophase", "Exophase game ID or slug");
                default:
                    return string.Empty;
            }
        }

        private static string NormalizeProviderOverrideSelection(string providerKey)
        {
            var normalized = (providerKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, ProviderOverrideNoneKey, StringComparison.OrdinalIgnoreCase))
            {
                return ProviderOverrideNoneKey;
            }

            if (string.Equals(normalized, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                return "Steam";
            }

            if (string.Equals(normalized, "RetroAchievements", StringComparison.OrdinalIgnoreCase))
            {
                return "RetroAchievements";
            }

            if (string.Equals(normalized, "ShadPS4", StringComparison.OrdinalIgnoreCase))
            {
                return "ShadPS4";
            }

            if (string.Equals(normalized, "RPCS3", StringComparison.OrdinalIgnoreCase))
            {
                return "RPCS3";
            }

            if (string.Equals(normalized, "Xenia", StringComparison.OrdinalIgnoreCase))
            {
                return "Xenia";
            }

            if (string.Equals(normalized, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                return "Exophase";
            }

            return ProviderOverrideNoneKey;
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
                   data?.ProviderOverride != null ||
                   data?.RetroAchievementsGameIdOverride.HasValue == true ||
                   !string.IsNullOrWhiteSpace(data?.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data?.ShadPS4MatchIdOverride) ||
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






