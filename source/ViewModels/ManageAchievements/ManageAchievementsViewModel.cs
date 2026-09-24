using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
// WinForms dialogs: the WPF Microsoft.Win32 pickers render legacy-style on .NET Framework.
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels.Items;
using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed class ManageAchievementsViewModel : PlayniteAchievements.Common.ObservableObject
    {
        private const string ProviderOverrideNoneKey = "None";

        public enum GameExclusionMode
        {
            None,
            Refreshes,
            Summaries
        }

        public sealed class GameExclusionOption
        {
            public GameExclusionOption(GameExclusionMode mode, string displayName)
            {
                Mode = mode;
                DisplayName = displayName;
            }

            public GameExclusionMode Mode { get; }

            public string DisplayName { get; }
        }

        public sealed class ProviderOverrideOption
        {
            public string ProviderKey { get; set; }

            public string DisplayName { get; set; }

            public ProviderOverrideDescriptor Descriptor { get; set; }
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

        private Task _iconOverridesApplyChain;
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
        private bool _hasAchievementData;
        private bool _hasAchievementPageLink;
        private bool _isRefreshing;
        private string _cachedProviderKey;
        private bool _cachedHasAchievements;
        private string _manualTrackingWarningAcceptedForProvider;
        private bool _useSeparateLockedIconsOverride;
        private bool _isLoadingProviderOverride;
        private string _selectedProviderOverrideKey = ProviderOverrideNoneKey;
        private string _providerOverrideInput;
        private bool _hasProviderOverride;
        private string _providerOverrideKey = ProviderOverrideNoneKey;
        private string _providerOverrideValue;
        private string _exophaseEnrichmentSlugInput;
        private bool _hasExophaseEnrichmentSlugOverride;
        private string _exophaseEnrichmentSlugValue;
        private string _exophaseEnrichmentSlugPlaceholder;
        private bool _isExophaseEnrichmentSlugSectionVisible;
        private bool _canExportCustomJson;
        private const string ManualProviderKey = "Manual";

        private bool _canClearCustomData;
        private int _customDataRevision;
        private ManageOverviewSummary _overviewSummary = ManageOverviewSummary.Empty;

        public IReadOnlyList<ProviderOverrideOption> ProviderOverrideOptions { get; }

        public RelayCommand OpenAchievementsCommand { get; }
        public AsyncCommand OpenAchievementPageCommand { get; }
        public RelayCommand ApplyProviderOverrideCommand { get; }
        public RelayCommand ClearProviderOverrideCommand { get; }
        public RelayCommand ApplyExophaseEnrichmentSlugCommand { get; }
        public RelayCommand ClearExophaseEnrichmentSlugCommand { get; }
        public RelayCommand UnlinkManualTrackingCommand { get; }
        public RelayCommand RefreshStateCommand { get; }
        public AsyncCommand RefreshGameCommand { get; }
        public RelayCommand ClearGameDataCommand { get; }
        public RelayCommand ExportCustomCommand { get; }
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
            ApplyProviderOverrideCommand = new RelayCommand(_ => ApplyProviderOverride(), _ => HasGame);
            ClearProviderOverrideCommand = new RelayCommand(_ => ClearProviderOverride(), _ => HasGame && HasProviderOverride);
            ApplyExophaseEnrichmentSlugCommand = new RelayCommand(_ => ApplyExophaseEnrichmentSlug(), _ => HasGame);
            ClearExophaseEnrichmentSlugCommand = new RelayCommand(_ => ClearExophaseEnrichmentSlug(), _ => HasGame && HasExophaseEnrichmentSlugOverride);
            UnlinkManualTrackingCommand = new RelayCommand(_ => UnlinkManualTracking(), _ => HasGame && HasManualTrackingLink);
            RefreshStateCommand = new RelayCommand(_ => Reload());
            RefreshGameCommand = new AsyncCommand(_ => RefreshGameAsync(), _ => HasGame && !IsRefreshing && !(_refreshService?.IsRebuilding ?? false));
            ClearGameDataCommand = new RelayCommand(_ => ClearGameData(), _ => HasGame);
            ExportCustomCommand = new RelayCommand(_ => ExportCustom(), _ => HasGame && CanExportCustomJson);
            ImportCustomJsonCommand = new RelayCommand(_ => ImportCustomJson(), _ => HasGame);
            ClearCustomDataCommand = new RelayCommand(_ => ClearCustomData(), _ => HasGame && CanClearCustomData);

            Reload();
        }

        public Guid GameId => _gameId;

        public string GameIdText => _gameId.ToString();

        public string EffectiveProviderKey =>
            _gameDataSnapshotProvider?.GetHydratedGameData()?.EffectiveProviderKey ??
            _plugin?.AchievementDataService?.GetGameAchievementData(_gameId)?.EffectiveProviderKey ??
            _cachedProviderKey;

        public ManageAchievementsTab SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (_selectedTab == value)
                {
                    return;
                }

                if (!HasAchievementData && ManageAchievementsTabs.RequireAchievementData.Contains(value))
                {
                    return;
                }

                SetValue(ref _selectedTab, value);
            }
        }

        /// <summary>
        /// Warns, once per provider, that linking manual tracking replaces the data the game
        /// already has from a provider. Returns false when the user backs out.
        /// </summary>
        /// <remarks>
        /// This used to guard opening the Manual Tracking tab. The editor took the tab's place and
        /// runs linking from its own header, so the warning moved to that command rather than
        /// going away with the tab.
        /// </remarks>
        public bool ConfirmManualTrackingOverride()
        {
            if (!ShouldWarnAboutManualTrackingOverride(out var existingProviderKey) ||
                string.Equals(_manualTrackingWarningAcceptedForProvider, existingProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var displayName = ProviderRegistry.GetLocalizedName(existingProviderKey);
            var message = string.Format(
                L("LOCPlayAch_ManageAchievements_Manual_ReplaceProviderWarning"),
                displayName);

            var result = _playniteApi?.Dialogs?.ShowMessage(
                message,
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.OK)
            {
                return false;
            }

            _manualTrackingWarningAcceptedForProvider = existingProviderKey;
            return true;
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
                    return L("LOCPlayAch_ManageAchievements_Overrides_LockedIcons_StatusOverride");
                }

                if (GameCustomDataLookup.ShouldUseSeparateLockedIcons(_gameId, _settings?.Persisted))
                {
                    return L("LOCPlayAch_ManageAchievements_Overrides_LockedIcons_StatusSettings");
                }

                return L("LOCPlayAch_Common_Status_Disabled");
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
                        var descriptor = GetProviderOverrideDescriptor(normalized);
                        ProviderOverrideInput = descriptor?.ValueKind == ProviderOverrideValueKind.Choice
                            ? descriptor.Choices.FirstOrDefault()?.Value ?? string.Empty
                            : string.Empty;
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
                }
            }
        }

        public bool IsProviderOverrideProviderSelected =>
            !string.Equals(SelectedProviderOverrideKey, ProviderOverrideNoneKey, StringComparison.OrdinalIgnoreCase);

        private ProviderOverrideValueKind SelectedProviderOverrideValueKind =>
            GetProviderOverrideDescriptor(SelectedProviderOverrideKey)?.ValueKind ?? ProviderOverrideValueKind.None;

        public bool IsProviderOverrideValueVisible =>
            IsProviderOverrideProviderSelected &&
            SelectedProviderOverrideValueKind != ProviderOverrideValueKind.None;

        public bool IsProviderOverrideTextVisible =>
            IsProviderOverrideProviderSelected &&
            SelectedProviderOverrideValueKind == ProviderOverrideValueKind.Text;

        public bool IsProviderOverrideChoiceVisible =>
            IsProviderOverrideProviderSelected &&
            SelectedProviderOverrideValueKind == ProviderOverrideValueKind.Choice;

        public IReadOnlyList<ProviderOverrideChoice> ProviderOverrideChoices =>
            GetProviderOverrideDescriptor(SelectedProviderOverrideKey)?.Choices ?? Array.Empty<ProviderOverrideChoice>();

        public string ProviderOverrideInputLabel => GetProviderOverrideInputLabel(SelectedProviderOverrideKey);

        public string ProviderOverrideStatusText
        {
            get
            {
                if (!HasProviderOverride)
                {
                    return L("LOCPlayAch_Common_Status_NoOverrideSet");
                }

                var providerName = GetProviderOverrideDisplayName(_providerOverrideKey);
                var descriptor = GetProviderOverrideDescriptor(_providerOverrideKey);

                if (descriptor != null &&
                    descriptor.ValueKind == ProviderOverrideValueKind.None)
                {
                    return string.Format(
                        L("LOCPlayAch_ManageAchievements_Overrides_ProviderStatusNoValue"),
                        providerName);
                }

                if (string.IsNullOrWhiteSpace(ProviderOverrideValue) &&
                    (descriptor?.ValueOptional ?? false))
                {
                    return string.Format(
                        L("LOCPlayAch_ManageAchievements_Overrides_ProviderStatusAuto"),
                        providerName);
                }

                var valueDisplay = descriptor?.GetValueDisplay(ProviderOverrideValue) ?? ProviderOverrideValue;
                return string.Format(
                    L("LOCPlayAch_ManageAchievements_Overrides_ProviderStatusValue"),
                    providerName,
                    valueDisplay);
            }
        }

        public string ExophaseEnrichmentSlugInput
        {
            get => _exophaseEnrichmentSlugInput;
            set
            {
                if (SetValueAndReturn(ref _exophaseEnrichmentSlugInput, value ?? string.Empty))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasExophaseEnrichmentSlugOverride
        {
            get => _hasExophaseEnrichmentSlugOverride;
            private set
            {
                if (SetValueAndReturn(ref _hasExophaseEnrichmentSlugOverride, value))
                {
                    OnPropertyChanged(nameof(ExophaseEnrichmentSlugStatusText));
                    RaiseCommandStates();
                }
            }
        }

        public bool IsExophaseEnrichmentSlugSectionVisible
        {
            get => _isExophaseEnrichmentSlugSectionVisible;
            private set => SetValue(ref _isExophaseEnrichmentSlugSectionVisible, value);
        }

        public string ExophaseEnrichmentSlugPlaceholder
        {
            get => _exophaseEnrichmentSlugPlaceholder;
            private set => SetValue(ref _exophaseEnrichmentSlugPlaceholder, value);
        }

        public string ExophaseEnrichmentSlugStatusText =>
            HasExophaseEnrichmentSlugOverride
                ? string.Format(
                    L("LOCPlayAch_ManageAchievements_Overrides_ExophaseEnrichmentStatusValue"),
                    _exophaseEnrichmentSlugValue)
                : L("LOCPlayAch_Common_Status_NoOverrideSet");

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
                    return $"0 / 0 ({PercentFormatter.FormatWhole(0)})";
                }

                var percent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(UnlockedAchievements, TotalAchievements);
                return $"{UnlockedAchievements} / {TotalAchievements} ({PercentFormatter.FormatWhole(percent)})";
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
                    OnPropertyChanged(nameof(ExclusionMode));
                }
            }
        }

        public bool IsExcludedFromSummaries
        {
            get => _isExcludedFromSummaries;
            private set
            {
                if (SetValueAndReturn(ref _isExcludedFromSummaries, value))
                {
                    OnPropertyChanged(nameof(ExclusionMode));
                }
            }
        }

        public IReadOnlyList<GameExclusionOption> ExclusionModeOptions { get; } = new[]
        {
            new GameExclusionOption(GameExclusionMode.None, L("LOCPlayAch_Common_None")),
            new GameExclusionOption(GameExclusionMode.Refreshes, L("LOCPlayAch_ManageAchievements_Status_ExcludedFromRefreshes")),
            new GameExclusionOption(GameExclusionMode.Summaries, L("LOCPlayAch_ManageAchievements_Status_ExcludedFromSummaries"))
        };

        /// <summary>
        /// The game's two exclusions as one choice. Picking one clears the other; a game that
        /// already stores both reads as excluded from refreshes.
        /// </summary>
        /// <remarks>
        /// Excluding from refreshes here leaves the cached data in place, unlike the game menu's
        /// "Exclude and Clear Data": the Overview already has a Clear button beside it.
        /// </remarks>
        public GameExclusionMode ExclusionMode
        {
            get => IsExcluded
                ? GameExclusionMode.Refreshes
                : IsExcludedFromSummaries ? GameExclusionMode.Summaries : GameExclusionMode.None;
            set => ApplyExclusionMode(value);
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
            ? L("LOCPlayAch_Common_Status_Linked")
            : L("LOCPlayAch_Common_Status_NotLinked");

        public bool HasAchievementData
        {
            get => _hasAchievementData;
            private set => SetValue(ref _hasAchievementData, value);
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

        /// <summary>
        /// The Overview's rarity, trophy and category breakdowns and its per-kind customization
        /// counts. Replaced as one value on every reload.
        /// </summary>
        public ManageOverviewSummary OverviewSummary
        {
            get => _overviewSummary;
            private set => SetValue(ref _overviewSummary, value ?? ManageOverviewSummary.Empty);
        }

        // Both of these are cache hits once the snapshot is warm and a full load when it is not,
        // and they run on the UI thread. On a game with hundreds of achievements a cold call is
        // the freeze the user sees, so the scope reports which edits are paying for one.
        private GameAchievementData GetHydratedGameData()
        {
            using (PerfScope.Start(_logger, "Manage.GetHydratedGameData", thresholdMs: 25))
            {
                return _gameDataSnapshotProvider?.GetHydratedGameData() ??
                       _plugin?.AchievementDataService?.GetGameAchievementData(_gameId);
            }
        }

        private GameAchievementData GetRawGameData()
        {
            using (PerfScope.Start(_logger, "Manage.GetRawGameData", thresholdMs: 25))
            {
                return _gameDataSnapshotProvider?.GetRawGameData() ??
                       _plugin?.AchievementDataService?.GetRawGameAchievementData(_gameId);
            }
        }

        /// <summary>
        /// Recomputes only the window cover image, so category metadata saves can update
        /// it immediately without a full reload.
        /// </summary>
        internal void RefreshGameImage()
        {
            GameImagePath = ResolveGameImagePath(_playniteApi?.Database?.Games?.Get(_gameId));
        }

        private string ResolveGameImagePath(Playnite.SDK.Models.Game game)
        {
            // Summary-category art selected via Manage Categories wins over the
            // Playnite cover/icon, matching the game summaries grid.
            var imagePath = GameSummaryArtResolver.ResolveForGame(_gameId);
            if (string.IsNullOrWhiteSpace(imagePath) && game != null)
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

            return imagePath;
        }

        public void Reload()
        {
            using (PerfScope.Start(_logger, "Manage.Reload", thresholdMs: 25))
            {
                ReloadCore();
            }
        }

        private void ReloadCore()
        {
            try
            {
                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                HasGame = game != null;
                GameName = game?.Name ?? L("LOCPlayAch_Text_UnknownGame");

                GameImagePath = ResolveGameImagePath(game);

                var gameData = GetHydratedGameData();

                // Only read when the hydrated copy is missing. The single consumer below builds
                // an AchievementPageLinkContext, whose BestGameData is `GameData ?? RawGameData`,
                // and no provider reads RawGameData directly -- so when gameData is present the
                // raw copy can never be observed. Reading it unconditionally made every reload
                // two cold snapshot loads instead of one, on the UI thread.
                var rawGameData = gameData != null ? null : GetRawGameData();

                HasCachedData = gameData != null;
                _cachedProviderKey = gameData?.ProviderKey?.Trim();
                _cachedHasAchievements = gameData?.HasAchievements ?? false;
                var isExcluded = _plugin?.IsGameExcluded(_gameId) ?? false;
                ManualAchievementLink manualLink;
                var hasManualLink = ManualAchievementsProvider.TryGetManualLink(_gameId, out manualLink);
                ProviderName = ResolveProviderDisplayName(gameData);
                LibrarySourceName = ResolveLibrarySourceDisplayName(game, gameData?.LibrarySourceName);

                if (gameData?.LastUpdatedUtc > DateTime.MinValue)
                {
                    LastUpdatedUtcText = gameData.LastUpdatedUtc.ToString("u");
                    LastUpdatedLocalText = gameData.LastUpdatedUtc.ToLocalTime().ToString("g");
                }
                else
                {
                    LastUpdatedUtcText = L("LOCPlayAch_ManageAchievements_Value_NotAvailable");
                    LastUpdatedLocalText = L("LOCPlayAch_ManageAchievements_Value_NotAvailable");
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
                        : L("LOCPlayAch_Common_None");
                HasAchievementData = (gameData?.HasAchievements ?? false) && list.Count > 0;

                var currentCustomData = TryLoadStoredCustomData(_plugin?.GameCustomDataStore);
                OverviewSummary = BuildOverviewSummary(list, currentCustomData);
                IsExcluded = isExcluded;
                IsExcludedFromSummaries = GameCustomDataLookup.IsExcludedFromSummaries(_gameId, _settings?.Persisted);
                SetValue(
                    ref _useSeparateLockedIconsOverride,
                    currentCustomData?.UseSeparateLockedIconsOverride == true);
                OnPropertyChanged(nameof(SeparateLockedIconsStatusText));
                ReloadProviderOverrideState(currentCustomData);
                ReloadExophaseEnrichmentSlugState(currentCustomData, game, gameData?.ProviderGameKey);

                HasManualTrackingLink = hasManualLink;
                ManualTrackingSummary = ManualAchievementsProvider.GetManageAchievementsLinkSummary(manualLink);
                HasAchievementPageLink = HasGame && _achievementPageLinkResolver.CanResolve(
                    new AchievementPageLinkContext(game, gameData, rawGameData, manualLink));

                RefreshCustomDataState();

                if (!HasAchievementData && ManageAchievementsTabs.RequireAchievementData.Contains(SelectedTab))
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

        private static ManageOverviewSummary BuildOverviewSummary(
            IReadOnlyList<AchievementDetail> achievements,
            GameCustomDataFile customData)
        {
            var breakdown = ManageOverviewSummaryBuilder.BuildBreakdown(achievements);
            var stats = breakdown.Stats;

            return new ManageOverviewSummary
            {
                RarityCommon = Stat(stats.CommonCount, stats.TotalCommonPossible),
                RarityUncommon = Stat(stats.UncommonCount, stats.TotalUncommonPossible),
                RarityRare = Stat(stats.RareCount, stats.TotalRarePossible),
                RarityUltraRare = Stat(stats.UltraRareCount, stats.TotalUltraRarePossible),
                TrophyPlatinum = Stat(stats.TrophyPlatinumCount, stats.TrophyPlatinumTotal),
                TrophyGold = Stat(stats.TrophyGoldCount, stats.TrophyGoldTotal),
                TrophySilver = Stat(stats.TrophySilverCount, stats.TrophySilverTotal),
                TrophyBronze = Stat(stats.TrophyBronzeCount, stats.TrophyBronzeTotal),
                Points = Stat(breakdown.UnlockedPoints, breakdown.TotalPoints),
                // Shown only once something is categorized: "0 / 60" says nothing on its own.
                Categorized = breakdown.CategorizedCount > 0
                    ? Stat(breakdown.CategorizedCount, stats.TotalAchievements)
                    : ManageOverviewStat.None,
                Goals = Stat(breakdown.UnlockedGoalCount, breakdown.GoalCount),
                Customizations = ManageOverviewSummaryBuilder.BuildCustomizationCounts(customData)
                    .Select(entry => new ManageOverviewCustomizationChip(
                        L(entry.LabelKey),
                        entry.Count.HasValue ? FormatCount(entry.Count.Value) : null,
                        entry.LabelKey == CapstoneLabelKey ? BuildCapstoneToolTip(achievements) : null))
                    .ToList()
            };
        }

        private static readonly string CapstoneLabelKey =
            AchievementCustomizationFacetLabels.GetLabelKey(AchievementCustomizationFacet.Capstone);

        /// <summary>
        /// One capstone per line, prefixed with its category when it stands for a category rather
        /// than the whole game.
        /// </summary>
        private static string BuildCapstoneToolTip(IReadOnlyList<AchievementDetail> achievements)
        {
            var lines = ManageOverviewSummaryBuilder.BuildCapstones(achievements)
                .Select(capstone => capstone.Item1 == null
                    ? capstone.Item2
                    : AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(capstone.Item1) + ": " + capstone.Item2)
                .ToList();
            return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
        }

        private static ManageOverviewStat Stat(int value, int total)
        {
            return total > 0
                ? new ManageOverviewStat(FormatProgress(value, total), true)
                : ManageOverviewStat.None;
        }

        private static string FormatProgress(int unlocked, int total)
        {
            return string.Format(FormattingCulture.Current, "{0:N0} / {1:N0}", unlocked, total);
        }

        private static string FormatCount(int value)
        {
            return value.ToString("N0", FormattingCulture.Current);
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
                L("LOCPlayAch_ManageAchievements_Overview_AchievementPageUnavailable"),
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ApplyExclusionMode(GameExclusionMode mode)
        {
            if (!HasGame || _achievementOverridesService == null || mode == ExclusionMode)
            {
                return;
            }

            var excludeFromRefreshes = mode == GameExclusionMode.Refreshes;
            var excludeFromSummaries = mode == GameExclusionMode.Summaries;

            if (IsExcluded != excludeFromRefreshes)
            {
                _achievementOverridesService.SetExcludedByUser(
                    _gameId,
                    excludeFromRefreshes,
                    clearCachedDataWhenExcluding: false);
            }

            if (IsExcludedFromSummaries != excludeFromSummaries)
            {
                _achievementOverridesService.SetExcludedFromSummaries(_gameId, excludeFromSummaries);
            }

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

            if (!TryCreateProviderOverride(providerKey, ProviderOverrideInput, out var providerOverride, out var validationMessageKey))
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    L(validationMessageKey),
                    L("LOCPlayAch_Title_PluginName"),
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

        private void ApplyExophaseEnrichmentSlug()
        {
            if (string.IsNullOrWhiteSpace(ExophaseEnrichmentSlugInput))
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_ManageAchievements_Overrides_ProviderValueRequired"),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!ExophaseApiClient.TryNormalizeSlugInput(ExophaseEnrichmentSlugInput, out var slug))
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_ManageAchievements_Overrides_ExophaseEnrichmentInvalid"),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (TrySetExophaseEnrichmentSlug(slug))
            {
                Reload();
            }
        }

        private void ClearExophaseEnrichmentSlug()
        {
            if (TrySetExophaseEnrichmentSlug(null))
            {
                Reload();
            }
        }

        private bool TrySetExophaseEnrichmentSlug(string slug)
        {
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null)
            {
                return false;
            }

            if (_achievementOverridesService != null)
            {
                _achievementOverridesService.SetExophaseEnrichmentSlugOverride(_gameId, slug);
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
                    customData.ExophaseEnrichmentSlugOverride = string.IsNullOrWhiteSpace(slug) ? null : slug.Trim();
                });
            }

            _persistSettingsForUi?.Invoke();
            _logger?.Info(string.IsNullOrWhiteSpace(slug)
                ? $"Cleared Exophase enrichment slug override for '{game.Name}'"
                : $"Set Exophase enrichment slug override for '{game.Name}' to '{slug}'");
            TriggerRefresh();
            return true;
        }

        private void ExportCustom()
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
                    FileName = BuildDefaultPortableFileBaseName()
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var destinationPath = NormalizePortableExportPath(
                    dialog.FileName,
                    GameCustomDataStore.PortableFileExtension);
                store.ExportPortablePackage(_gameId, destinationPath);
                var successMessage = L("LOCPlayAch_Status_Succeeded") + "\n" + destinationPath;

                _playniteApi?.Dialogs?.ShowMessage(
                    successMessage,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed exporting custom game data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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
                    Filter = "Playnite Achievements Portable (*.pa)|*.pa;*.pa.zip",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != DialogResult.OK)
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

                var successMessage = L("LOCPlayAch_Status_Succeeded");
                if (importResult.HasIgnoredPackageImages)
                {
                    successMessage += "\n\n" + string.Format(
                        L("LOCPlayAch_ManageAchievements_Overrides_ImportIgnoredPackageImages"),
                        importResult.IgnoredPackageImageCount);
                }

                _playniteApi?.Dialogs?.ShowMessage(
                    successMessage,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    importResult.HasIgnoredPackageImages
                        ? MessageBoxImage.Warning
                        : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed importing custom game data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Whether clearing this game's custom data would leave the achievements a manual link
        /// produced behind with nothing owning them.
        /// </summary>
        /// <remarks>
        /// A manual link is where the unlock state of the achievements it produced lives, and the
        /// Manual provider only services a game while the link exists. Deleting the link on its own
        /// leaves rows that no provider services, that the editor refuses to let anyone edit, and
        /// that no refresh will ever update -- a real provider's own refresh returns nothing for
        /// them and is turned away by the empty-payload guard, so they are frozen for good.
        /// </remarks>
        private bool WouldStrandManualAchievements(GameCustomDataFile currentData)
        {
            if (currentData?.ManualLink == null)
            {
                return false;
            }

            var rawGameData = GetRawGameData();
            return rawGameData?.Achievements?.Count > 0 &&
                   string.Equals(rawGameData.ProviderKey, ManualProviderKey, StringComparison.OrdinalIgnoreCase);
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

            // The manual link is not customization but the game's source of achievements, so
            // dropping it has to take what it produced with it.
            var strandsManualAchievements = WouldStrandManualAchievements(currentData);
            var result = _playniteApi?.Dialogs?.ShowMessage(
                string.Format(
                    L(strandsManualAchievements
                        ? "LOCPlayAch_ManageAchievements_Overrides_ClearCustomDataConfirmManual"
                        : "LOCPlayAch_ManageAchievements_Overrides_ClearCustomDataConfirm"),
                    GameName),
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                if (strandsManualAchievements)
                {
                    // Removes the link from both of its homes and the achievements it produced
                    // from the cache, so nothing is left that no provider services.
                    _achievementOverridesService?.ClearGameData(_gameId, GameName);
                }

                store.Delete(_gameId);
                var transitionEffects = AnalyzeCustomDataTransition(currentData, null);
                NotifyCustomDataChanged(transitionEffects.RequiresRefresh, transitionEffects.ForceIconRefresh);

                _playniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded"),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed clearing custom data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
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
                    SurfaceUserNotices = true,
                    Options = new RefreshOptions
                    {
                        Subjects = RefreshSubjects.CurrentUser,
                        Scope = RefreshGameScope.SelectedGame,
                        PlayniteGameIds = new[] { _gameId },
                        RespectUserExclusions = false,
                        ForceBypassExclusionsForExplicitIncludes = true,
                        ForceIconRefresh = forceIconRefresh
                    }
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
                            SingleGameId = _gameId,
                            SurfaceUserNotices = true
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
                        L("LOCPlayAch_Error_RefreshFailed"),
                        ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
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
                    L("LOCPlayAch_Menu_ClearData_ConfirmSingle"),
                    GameName),
                L("LOCPlayAch_Title_PluginName"),
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
                    L("LOCPlayAch_Status_Succeeded"),
                    L("LOCPlayAch_Title_PluginName"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear cached data for gameId={_gameId}");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
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
            ApplyProviderOverrideCommand?.RaiseCanExecuteChanged();
            ClearProviderOverrideCommand?.RaiseCanExecuteChanged();
            ApplyExophaseEnrichmentSlugCommand?.RaiseCanExecuteChanged();
            ClearExophaseEnrichmentSlugCommand?.RaiseCanExecuteChanged();
            UnlinkManualTrackingCommand?.RaiseCanExecuteChanged();
            RefreshStateCommand?.RaiseCanExecuteChanged();
            RefreshGameCommand?.RaiseCanExecuteChanged();
            ClearGameDataCommand?.RaiseCanExecuteChanged();
            ExportCustomCommand?.RaiseCanExecuteChanged();
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

        internal void NotifyCapstoneChanged(string displayName)
        {
            CurrentCapstoneName = string.IsNullOrWhiteSpace(displayName)
                ? L("LOCPlayAch_Common_None")
                : displayName.Trim();            RefreshCustomDataState();
        }

        internal void NotifyCustomDataChanged(
            bool requiresRefresh,
            bool forceIconRefresh = false)
        {
            // The whole cost of one edit burst reaching the window: the reload below plus every
            // tab the revision bump marks stale, all of it on the UI thread.
            using (PerfScope.Start(_logger, "Manage.NotifyCustomDataChanged", thresholdMs: 25))
            {
                NotifyCustomDataChangedCore(requiresRefresh, forceIconRefresh);
            }
        }

        private void NotifyCustomDataChangedCore(bool requiresRefresh, bool forceIconRefresh)
        {
            _gameDataSnapshotProvider?.Invalidate();

            // Scoped to this game, and it has to stay. The store already raised CustomDataChanged
            // for this write, but the consumers that matter honour AffectsSummaryData -- and the
            // editor's category, category-type, filter and icon writes all pass false, so the
            // overview drops them: OnCustomDataChanged returns early on that flag so a
            // reorder-only change cannot rebuild the selected game and discard the user's
            // filters. Without this invalidation those edits reached the store and nothing on
            // screen re-read them.
            //
            // This was briefly removed as redundant. It is not: it is the only thing that routes
            // a named game to the overview's per-game fragment path for a write that does not
            // claim to move a summary. What made it expensive was the projection treating a
            // scoped invalidation as a full one and rebuilding the whole library eagerly; that is
            // fixed at the projection instead, where a scoped change now defers its warm.
            _refreshService?.Cache?.NotifyCacheInvalidated(new[] { _gameId });

            if (_settings?.SelectedGame?.Id == _gameId)
            {
                _plugin?.ThemeUpdateService?.RequestUpdate(_gameId, forceRefresh: true);
            }

            // Deferred, not skipped, and deliberately before the revision bump. The bump below
            // fires HandleCustomDataRevisionChanged synchronously in this same call stack, and
            // that rehydrates the snapshot for the visible tab -- so by the time this reload
            // runs it reads a warm snapshot instead of forcing a second cold load on the UI
            // thread. Measured at 369ms of UI-thread SQLite per edit before deferring.
            //
            // Deferring rather than suppressing on the editor's self-write marker: a self-write
            // can still move the shell's totals, and ConsumeEditorSelfWrite clears the flag on
            // read, so the marker cannot be consulted twice.
            ScheduleShellReload();
            CustomDataRevision = unchecked(CustomDataRevision + 1);

            if (requiresRefresh)
            {
                TriggerRefresh(forceIconRefresh);
            }
        }

        // Trailing-edge coalescer for the shell reload. A burst of edits -- the editor persists
        // on every completed field -- collapses into one reload.
        private static readonly TimeSpan ShellReloadDebounce = TimeSpan.FromMilliseconds(150);
        private DispatcherTimer _shellReloadTimer;

        internal void ScheduleShellReload()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || !dispatcher.CheckAccess())
            {
                // No dispatcher to defer onto (tests, or an off-thread caller): keep the old
                // synchronous behaviour rather than silently dropping the reload.
                Reload();
                return;
            }

            if (_shellReloadTimer == null)
            {
                _shellReloadTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = ShellReloadDebounce
                };
                _shellReloadTimer.Tick += ShellReloadTimer_Tick;
            }

            _shellReloadTimer.Stop();
            _shellReloadTimer.Start();
        }

        private void ShellReloadTimer_Tick(object sender, EventArgs e)
        {
            _shellReloadTimer?.Stop();
            Reload();
        }

        /// <summary>
        /// Runs any pending shell reload now. Called when the window is closing, so a deferred
        /// reload is never simply dropped.
        /// </summary>
        internal void FlushPendingShellReload()
        {
            if (_shellReloadTimer?.IsEnabled != true)
            {
                return;
            }

            _shellReloadTimer.Stop();
            Reload();
        }

        internal void NotifyIconOverridesChanged(IReadOnlyCollection<string> changedApiNames)
        {
            if (changedApiNames == null || changedApiNames.Count == 0)
            {
                return;
            }

            // Chain onto the previous apply so overlapping edit bursts run one at a time;
            // each run re-reads the override store, so the final run converges to the
            // last persisted state.
            _iconOverridesApplyChain = ApplyIconOverridesAsync(_iconOverridesApplyChain, changedApiNames);
        }

        private async Task ApplyIconOverridesAsync(
            Task previousApply,
            IReadOnlyCollection<string> changedApiNames)
        {
            try
            {
                await (previousApply ?? Task.CompletedTask);
            }
            catch
            {
                // Already logged by the run that faulted.
            }

            try
            {
                if (_refreshService != null)
                {
                    // The apply completes mostly synchronously when icon files are already
                    // cached (including the SQLite SaveGameData at the end), so it must run
                    // on a worker thread or it stalls the dispatcher and input goes jerky.
                    // The outer await has no ConfigureAwait(false): the post-apply
                    // invalidation below must resume on the dispatcher.
                    await Task.Run(() => _refreshService.ApplyAchievementIconOverridesAsync(_gameId, changedApiNames));
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed applying icon overrides for gameId={_gameId}");
            }

            _gameDataSnapshotProvider?.Invalidate();
            _refreshService?.Cache?.NotifyCacheInvalidated(new[] { _gameId });
            if (_settings?.SelectedGame?.Id == _gameId)
            {
                _plugin?.ThemeUpdateService?.RequestUpdate(_gameId, forceRefresh: true);
            }

            RefreshCustomDataState();
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
                return L("LOCPlayAch_ManageAchievements_Value_NotAvailable");
            }

            var displayName = ProviderRegistry.GetLocalizedName(providerKey);
            return string.IsNullOrWhiteSpace(displayName)
                ? L("LOCPlayAch_ManageAchievements_Value_NotAvailable")
                : displayName;
        }

        private string ResolveLibrarySourceDisplayName(Playnite.SDK.Models.Game game, string cachedLibrarySource)
        {
            var fallback = L("LOCPlayAch_ManageAchievements_Value_NotAvailable");

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

        private void ReloadExophaseEnrichmentSlugState(
            GameCustomDataFile currentCustomData,
            Playnite.SDK.Models.Game game,
            string providerGameKey)
        {
            var slug = (currentCustomData?.ExophaseEnrichmentSlugOverride ?? string.Empty).Trim();
            _exophaseEnrichmentSlugValue = slug;
            HasExophaseEnrichmentSlugOverride = !string.IsNullOrWhiteSpace(slug);
            ExophaseEnrichmentSlugInput = slug;
            OnPropertyChanged(nameof(ExophaseEnrichmentSlugStatusText));
            ExophaseEnrichmentSlugPlaceholder =
                ExophaseRarityEnrichment.GetCandidateSlug(_cachedProviderKey, game, providerGameKey) ?? string.Empty;

            // Raw ProviderKey (not EffectiveProviderKey): Exophase-serviced games report their
            // proxied platform as the effective key but never run the enricher.
            IsExophaseEnrichmentSlugSectionVisible =
                HasGame && ExophaseRarityEnrichment.IsEnabledForProvider(_cachedProviderKey);
        }

        private void OnProviderOverrideSelectionChanged()
        {
            OnPropertyChanged(nameof(IsProviderOverrideProviderSelected));
            OnPropertyChanged(nameof(IsProviderOverrideValueVisible));
            OnPropertyChanged(nameof(IsProviderOverrideTextVisible));
            OnPropertyChanged(nameof(IsProviderOverrideChoiceVisible));
            OnPropertyChanged(nameof(ProviderOverrideChoices));
            OnPropertyChanged(nameof(ProviderOverrideInputLabel));
            OnPropertyChanged(nameof(ProviderOverrideStatusText));
            RaiseCommandStates();
        }

        private bool TryCreateProviderOverride(
            string providerKey,
            string value,
            out ProviderOverrideData providerOverride,
            out string validationMessageKey)
        {
            providerOverride = null;
            validationMessageKey = null;

            var normalizedKey = NormalizeProviderOverrideSelection(providerKey);
            var descriptor = GetProviderOverrideDescriptor(normalizedKey);
            if (descriptor == null)
            {
                validationMessageKey = "LOCPlayAch_ManageAchievements_Overrides_ProviderInvalid";
                return false;
            }

            var result = descriptor.Validate(value);
            if (!result.IsValid)
            {
                validationMessageKey = result.ErrorMessageKey ?? "LOCPlayAch_ManageAchievements_Overrides_ProviderInvalid";
                return false;
            }

            providerOverride = new ProviderOverrideData
            {
                ProviderKey = normalizedKey,
                Value = result.NormalizedValue
            };
            return true;
        }

        private IReadOnlyList<ProviderOverrideOption> BuildProviderOverrideOptions()
        {
            var options = new List<ProviderOverrideOption>
            {
                new ProviderOverrideOption
                {
                    ProviderKey = ProviderOverrideNoneKey,
                    DisplayName = L("LOCPlayAch_Common_None"),
                    Descriptor = null
                }
            };

            var providers = ProviderRegistry.Instance?.GetAllProviders() ?? Array.Empty<IDataProvider>();
            foreach (var provider in providers)
            {
                if (provider is IProviderOverride overrideProvider &&
                    overrideProvider.OverrideDescriptor != null)
                {
                    options.Add(new ProviderOverrideOption
                    {
                        ProviderKey = provider.ProviderKey,
                        DisplayName = ProviderRegistry.GetLocalizedName(provider.ProviderKey),
                        Descriptor = overrideProvider.OverrideDescriptor
                    });
                }
            }

            return options;
        }

        private ProviderOverrideOption FindProviderOverrideOption(string providerKey)
        {
            var normalized = (providerKey ?? string.Empty).Trim();
            return ProviderOverrideOptions?.FirstOrDefault(option =>
                string.Equals(option.ProviderKey, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private ProviderOverrideDescriptor GetProviderOverrideDescriptor(string providerKey)
            => FindProviderOverrideOption(providerKey)?.Descriptor;

        private string GetProviderOverrideDisplayName(string providerKey)
        {
            var normalizedKey = NormalizeProviderOverrideSelection(providerKey);
            if (string.Equals(normalizedKey, ProviderOverrideNoneKey, StringComparison.OrdinalIgnoreCase))
            {
                return L("LOCPlayAch_Common_None");
            }

            return ProviderRegistry.GetLocalizedName(normalizedKey);
        }

        private string GetProviderOverrideInputLabel(string providerKey)
        {
            var descriptor = GetProviderOverrideDescriptor(NormalizeProviderOverrideSelection(providerKey));
            if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.InputLabelKey))
            {
                return string.Empty;
            }

            return L(descriptor.InputLabelKey);
        }

        private string NormalizeProviderOverrideSelection(string providerKey)
        {
            var normalized = (providerKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, ProviderOverrideNoneKey, StringComparison.OrdinalIgnoreCase))
            {
                return ProviderOverrideNoneKey;
            }

            return FindProviderOverrideOption(normalized)?.ProviderKey ?? ProviderOverrideNoneKey;
        }

        private bool ProviderOverrideUsesOptionalValue(string providerKey)
        {
            var descriptor = GetProviderOverrideDescriptor(NormalizeProviderOverrideSelection(providerKey));
            return descriptor != null &&
                   (descriptor.ValueKind == ProviderOverrideValueKind.None || descriptor.ValueOptional);
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
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
                   !string.IsNullOrWhiteSpace(data?.ExophaseSlugOverride) ||
                   !string.IsNullOrWhiteSpace(data?.ExophaseEnrichmentSlugOverride);
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






