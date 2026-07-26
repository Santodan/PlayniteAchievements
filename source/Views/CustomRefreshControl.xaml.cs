using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace PlayniteAchievements.Views
{
    public partial class CustomRefreshControl : UserControl, INotifyPropertyChanged
    {
        private const string WindowPlacementKey = "CustomRefresh";

        public sealed class ScopeOptionItem
        {
            public CustomGameScope Scope { get; set; }
            public string DisplayName { get; set; }
        }

        public sealed class ProviderOptionItem : PlayniteAchievements.Common.ObservableObject
        {
            private bool _isSelected;
            private bool _isEnabled;
            private bool _isAuthenticated;
            private readonly string _enabledAndAuthText;
            private readonly string _disabledText;
            private readonly string _noAuthText;

            public string ProviderKey { get; }
            public string ProviderName { get; }

            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (SetValueAndReturn(ref _isEnabled, value))
                    {
                        OnStateChanged();
                    }
                }
            }

            public bool IsAuthenticated
            {
                get => _isAuthenticated;
                set
                {
                    if (SetValueAndReturn(ref _isAuthenticated, value))
                    {
                        OnStateChanged();
                    }
                }
            }

            public bool IsSelectable => IsEnabled && IsAuthenticated;

            public string StatusText
            {
                get
                {
                    if (!IsEnabled)
                    {
                        return _disabledText;
                    }

                    if (!IsAuthenticated)
                    {
                        return _noAuthText;
                    }

                    return _enabledAndAuthText;
                }
            }

            public bool IsSelected
            {
                get => _isSelected;
                set => SetValue(ref _isSelected, value);
            }

            public ProviderOptionItem(
                string providerKey,
                string providerName,
                bool isEnabled,
                bool isAuthenticated,
                string enabledAndAuthText,
                string disabledText,
                string noAuthText)
            {
                ProviderKey = providerKey;
                ProviderName = providerName;
                _enabledAndAuthText = enabledAndAuthText;
                _disabledText = disabledText;
                _noAuthText = noAuthText;
                _isEnabled = isEnabled;
                _isAuthenticated = isAuthenticated;
            }

            private void OnStateChanged()
            {
                OnPropertyChanged(nameof(IsSelectable));
                OnPropertyChanged(nameof(StatusText));

                if (!IsSelectable && IsSelected)
                {
                    IsSelected = false;
                }
            }
        }

        public sealed class GameOptionItem : PlayniteAchievements.Common.ObservableObject
        {
            private bool _isIncluded;
            private bool _isExcluded;

            public Guid GameId { get; }
            public string DisplayName { get; }

            public bool IsIncluded
            {
                get => _isIncluded;
                set => SetValue(ref _isIncluded, value);
            }

            public bool IsExcluded
            {
                get => _isExcluded;
                set => SetValue(ref _isExcluded, value);
            }

            public GameOptionItem(Guid gameId, string displayName)
            {
                GameId = gameId;
                DisplayName = displayName;
            }
        }

        private readonly IPlayniteAPI _api;
        private readonly RefreshRuntime _refreshService;
        private readonly Action _persistSettingsForUi;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly Dictionary<string, IDataProvider> _providersByKey = new Dictionary<string, IDataProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, Game> _gamesById = new Dictionary<Guid, Game>();
        private readonly HashSet<string> _cachedGameIds;

        // Provider capability results are stable while the dialog is open, but IsCapable can be
        // expensive (custom-data lookups, emulator file probing), so cache per provider+game.
        // Guarded by a lock because estimates run on background threads.
        private readonly object _capabilityCacheSync = new object();
        private readonly Dictionary<string, Dictionary<Guid, bool>> _capabilityCacheByProvider =
            new Dictionary<string, Dictionary<Guid, bool>>(StringComparer.OrdinalIgnoreCase);

        // UI-thread-only generation counter; stale background estimate results are dropped.
        private int _summaryGeneration;

        private string _includeSearchText = string.Empty;
        private string _excludeSearchText = string.Empty;
        private CustomGameScope _selectedScope = CustomGameScope.All;
        private bool _useRecentLimitOverride;
        private string _recentLimitOverrideText;
        private bool _useIncludeUnplayedOverride;
        private bool _includeUnplayedOverrideValue = true;
        private bool _respectUserExclusions = true;
        private bool _forceBypassExclusionsForExplicitIncludes = true;
        private bool _useParallelOverride;
        private bool _runProvidersInParallelValue = true;
        private string _summaryText;
        private bool _canRun;
        private CustomRefreshPreset _selectedPreset;
        private CustomRefreshPreset _placeholderPreset;
        private DispatcherTimer _summaryDebounceTimer;

        public event EventHandler RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        public bool? DialogResult { get; private set; }
        public CustomRefreshOptions ResultOptions { get; private set; }

        public ObservableCollection<ProviderOptionItem> ProviderOptions { get; } = new ObservableCollection<ProviderOptionItem>();
        public ObservableCollection<ScopeOptionItem> ScopeOptions { get; } = new ObservableCollection<ScopeOptionItem>();
        public BulkObservableCollection<GameOptionItem> GameOptions { get; } = new BulkObservableCollection<GameOptionItem>();
        public ObservableCollection<CustomRefreshPreset> PresetOptions { get; } = new ObservableCollection<CustomRefreshPreset>();

        public ICollectionView IncludeGameView { get; }
        public ICollectionView ExcludeGameView { get; }

        public CustomRefreshPreset SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (ReferenceEquals(_selectedPreset, value))
                {
                    return;
                }

                _selectedPreset = value;
                OnPropertyChanged(nameof(SelectedPreset));
                OnPropertyChanged(nameof(CanLoadPreset));
                OnPropertyChanged(nameof(CanSavePreset));
                OnPropertyChanged(nameof(CanDeletePreset));

                // Selecting a preset applies it immediately; the Load button remains as an
                // explicit way to re-apply the preset after tweaking individual options.
                if (_selectedPreset?.Options != null)
                {
                    ApplySelectedPreset();
                }
            }
        }

        public bool CanLoadPreset => SelectedPreset?.Options != null;
        public bool CanSavePreset => SelectedPreset?.Options != null;
        public bool CanDeletePreset => SelectedPreset?.Options != null;

        public string IncludeSearchText
        {
            get => _includeSearchText;
            set
            {
                if (string.Equals(_includeSearchText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _includeSearchText = value ?? string.Empty;
                OnPropertyChanged(nameof(IncludeSearchText));
                IncludeGameView?.Refresh();
            }
        }

        public string ExcludeSearchText
        {
            get => _excludeSearchText;
            set
            {
                if (string.Equals(_excludeSearchText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _excludeSearchText = value ?? string.Empty;
                OnPropertyChanged(nameof(ExcludeSearchText));
                ExcludeGameView?.Refresh();
            }
        }

        public CustomGameScope SelectedScope
        {
            get => _selectedScope;
            set
            {
                if (_selectedScope == value)
                {
                    return;
                }

                _selectedScope = value;
                OnPropertyChanged(nameof(SelectedScope));
                ScheduleSummaryRecalculation();
            }
        }

        public bool UseRecentLimitOverride
        {
            get => _useRecentLimitOverride;
            set
            {
                if (_useRecentLimitOverride == value)
                {
                    return;
                }

                _useRecentLimitOverride = value;
                OnPropertyChanged(nameof(UseRecentLimitOverride));
                ScheduleSummaryRecalculation();
            }
        }

        public string RecentLimitOverrideText
        {
            get => _recentLimitOverrideText;
            set
            {
                if (string.Equals(_recentLimitOverrideText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _recentLimitOverrideText = value;
                OnPropertyChanged(nameof(RecentLimitOverrideText));
                ScheduleSummaryRecalculation();
            }
        }

        public bool UseIncludeUnplayedOverride
        {
            get => _useIncludeUnplayedOverride;
            set
            {
                if (_useIncludeUnplayedOverride == value)
                {
                    return;
                }

                _useIncludeUnplayedOverride = value;
                OnPropertyChanged(nameof(UseIncludeUnplayedOverride));
                ScheduleSummaryRecalculation();
            }
        }

        public bool IncludeUnplayedOverrideValue
        {
            get => _includeUnplayedOverrideValue;
            set
            {
                if (_includeUnplayedOverrideValue == value)
                {
                    return;
                }

                _includeUnplayedOverrideValue = value;
                OnPropertyChanged(nameof(IncludeUnplayedOverrideValue));
                ScheduleSummaryRecalculation();
            }
        }

        public bool RespectUserExclusions
        {
            get => _respectUserExclusions;
            set
            {
                if (_respectUserExclusions == value)
                {
                    return;
                }

                _respectUserExclusions = value;
                OnPropertyChanged(nameof(RespectUserExclusions));
                ScheduleSummaryRecalculation();
            }
        }

        public bool ForceBypassExclusionsForExplicitIncludes
        {
            get => _forceBypassExclusionsForExplicitIncludes;
            set
            {
                if (_forceBypassExclusionsForExplicitIncludes == value)
                {
                    return;
                }

                _forceBypassExclusionsForExplicitIncludes = value;
                OnPropertyChanged(nameof(ForceBypassExclusionsForExplicitIncludes));
                ScheduleSummaryRecalculation();
            }
        }

        public bool UseParallelOverride
        {
            get => _useParallelOverride;
            set
            {
                if (_useParallelOverride == value)
                {
                    return;
                }

                _useParallelOverride = value;
                OnPropertyChanged(nameof(UseParallelOverride));
            }
        }

        public bool RunProvidersInParallelValue
        {
            get => _runProvidersInParallelValue;
            set
            {
                if (_runProvidersInParallelValue == value)
                {
                    return;
                }

                _runProvidersInParallelValue = value;
                OnPropertyChanged(nameof(RunProvidersInParallelValue));
            }
        }

        public string SummaryText
        {
            get => _summaryText;
            private set
            {
                if (string.Equals(_summaryText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _summaryText = value;
                OnPropertyChanged(nameof(SummaryText));
            }
        }

        public bool CanRun
        {
            get => _canRun;
            private set
            {
                if (_canRun == value)
                {
                    return;
                }

                _canRun = value;
                OnPropertyChanged(nameof(CanRun));
            }
        }

        public CustomRefreshControl(
            IPlayniteAPI api,
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            InitializeComponent();

            IncludeGameView = new ListCollectionView(GameOptions);
            IncludeGameView.Filter = IncludeGameFilter;
            ExcludeGameView = new ListCollectionView(GameOptions);
            ExcludeGameView.Filter = ExcludeGameFilter;

            DataContext = this;

            _runProvidersInParallelValue = _settings?.Persisted?.EnableParallelProviderRefresh ?? true;
            _recentLimitOverrideText = (_settings?.Persisted?.RecentRefreshGamesCount ?? 10).ToString();
            _placeholderPreset = new CustomRefreshPreset
            {
                Name = L("LOCPlayAch_Common_None"),
                Options = null
            };

            _cachedGameIds = LoadCachedGameIds();

            InitializeScopeOptions();
            InitializeProviders();
            _ = RefreshProviderOptionsAsync();
            InitializeGames();
            InitializePresets();

            // Kicks off the estimate on a background thread; the window opens without
            // waiting for the full library capability walk.
            RecalculateSummary();
        }

        public static bool TryShowDialog(
            IPlayniteAPI api,
            RefreshRuntime refreshRuntime,
            Action persistSettingsForUi,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            out CustomRefreshOptions options)
        {
            options = null;

            EnsureThemeResources(settings);

            var control = new CustomRefreshControl(api, refreshRuntime, persistSettingsForUi, settings, logger);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_RefreshMode_Custom"),
                control,
                new WindowOptions
                {
                    Width = 980,
                    Height = 760,
                    CanBeResizable = true,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            window.MinWidth = 820;
            window.MinHeight = 620;
            WindowPlacementPersistenceService.Attach(
                window,
                settings?.Persisted,
                persistSettingsForUi,
                WindowPlacementKey,
                logger);
            control.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (control.DialogResult == true && control.ResultOptions != null)
            {
                options = control.ResultOptions;
                return true;
            }

            return false;
        }

        private static void EnsureThemeResources(PlayniteAchievementsSettings settings)
        {
            var resources = Application.Current?.Resources;
            if (resources != null)
            {
                PlayAchResourceService.Apply(
                    resources,
                    settings?.Persisted?.ResourceOverrides,
                    settings?.Persisted);
            }
        }

        private void InitializeScopeOptions()
        {
            ScopeOptions.Clear();
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.All, DisplayName = L("LOCPlayAch_CustomRefresh_Scope_All") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Installed, DisplayName = L("LOCPlayAch_RefreshModeShort_Installed") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Favorites, DisplayName = L("LOCPlayAch_RefreshModeShort_Favorites") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Recent, DisplayName = L("LOCPlayAch_RefreshModeShort_Recent") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.LibrarySelected, DisplayName = L("LOCPlayAch_CustomRefresh_Scope_LibrarySelected") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Missing, DisplayName = L("LOCPlayAch_RefreshModeShort_Missing") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Explicit, DisplayName = L("LOCPlayAch_CustomRefresh_Scope_Explicit") });
        }

        private void InitializeProviders()
        {
            ProviderOptions.Clear();
            _providersByKey.Clear();

            var readyText = L("LOCPlayAch_CustomRefresh_ProviderStatus_Ready");
            var disabledText = L("LOCPlayAch_Common_Status_Disabled");
            var noAuthText = L("LOCPlayAch_Common_NotAuthenticated");

            foreach (var provider in _refreshService.Providers)
            {
                if (provider == null || ProviderUiPolicies.ShouldHideFromSetupSurfaces(provider.ProviderKey))
                {
                    continue;
                }

                var isEnabled = _refreshService.ProviderRegistry.IsProviderEnabled(provider.ProviderKey);
                var isAuthenticated = provider.AuthSession == null && provider.IsAuthenticated;
                var item = new ProviderOptionItem(
                    provider.ProviderKey,
                    provider.ProviderName,
                    isEnabled,
                    isAuthenticated,
                    readyText,
                    disabledText,
                    noAuthText)
                {
                    IsSelected = isEnabled && isAuthenticated
                };

                item.PropertyChanged += OnProviderOptionChanged;
                ProviderOptions.Add(item);
                _providersByKey[provider.ProviderKey] = provider;
            }
        }

        private async Task RefreshProviderOptionsAsync()
        {
            foreach (var option in ProviderOptions)
            {
                if (!_providersByKey.TryGetValue(option.ProviderKey, out var provider) || provider == null)
                {
                    continue;
                }

                var isEnabled = _refreshService.ProviderRegistry.IsProviderEnabled(provider.ProviderKey);
                option.IsEnabled = isEnabled;
                option.IsAuthenticated = isEnabled &&
                    await _refreshService.IsProviderAuthenticatedAsync(provider, CancellationToken.None).ConfigureAwait(true);

                // Settle any selection made while this probe was pending (e.g. a preset applied at
                // window open): a provider confirmed unavailable cannot stay selected.
                if (!option.IsSelectable && option.IsSelected)
                {
                    option.IsSelected = false;
                }
            }

            ScheduleSummaryRecalculation();
        }

        private void InitializeGames()
        {
            _gamesById.Clear();

            // Both collection views observe GameOptions; ReplaceAll raises a single
            // Reset so bulk population runs one filter pass per view instead of two
            // per added game. (DeferRefresh cannot be used here: mutating the source
            // collection while a ListCollectionView refresh is deferred throws.)
            var gameItems = new List<GameOptionItem>();
            foreach (var game in _api.Database.Games
                .Where(game => game != null && game.Id != Guid.Empty)
                .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase))
            {
                _gamesById[game.Id] = game;

                var gameItem = new GameOptionItem(game.Id, BuildGameDisplayName(game));
                gameItem.PropertyChanged += OnGameOptionChanged;
                gameItems.Add(gameItem);
            }

            GameOptions.ReplaceAll(gameItems);
        }

        private void InitializePresets()
        {
            var normalizedPresets = CustomRefreshPreset.NormalizePresets(
                _settings?.Persisted?.CustomRefreshPresets,
                CustomRefreshPreset.MaxPresetCount);

            PresetOptions.Clear();
            PresetOptions.Add(_placeholderPreset);
            foreach (var preset in normalizedPresets)
            {
                PresetOptions.Add(preset.Clone());
            }

            SelectedPreset = _placeholderPreset;
        }

        private void ReplacePresets(
            IEnumerable<CustomRefreshPreset> presets,
            string selectedName = null)
        {
            var previousSelectedName = SelectedPreset?.Options != null
                ? SelectedPreset.Name
                : null;
            var normalized = CustomRefreshPreset.NormalizePresets(
                presets,
                CustomRefreshPreset.MaxPresetCount);

            PresetOptions.Clear();
            PresetOptions.Add(_placeholderPreset);
            foreach (var preset in normalized)
            {
                PresetOptions.Add(preset.Clone());
            }

            var targetSelectionName = !string.IsNullOrWhiteSpace(selectedName)
                ? selectedName
                : previousSelectedName;
            if (!string.IsNullOrWhiteSpace(targetSelectionName))
            {
                SelectedPreset = PresetOptions.FirstOrDefault(
                    preset => preset?.Options != null &&
                              string.Equals(preset.Name, targetSelectionName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                SelectedPreset = null;
            }

            if (SelectedPreset == null)
            {
                SelectedPreset = _placeholderPreset;
            }
        }

        private void PersistPresetCollection()
        {
            try
            {
                var normalized = CustomRefreshPreset.NormalizePresets(
                    PresetOptions.Where(preset => preset?.Options != null),
                    CustomRefreshPreset.MaxPresetCount);
                _settings.Persisted.CustomRefreshPresets = new List<CustomRefreshPreset>(normalized);
                _persistSettingsForUi();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist custom refresh presets.");
            }
        }

        private bool TryPromptPresetName(string defaultName, out string presetName)
        {
            presetName = null;

            var inputDialog = new TextInputDialog(
                L("LOCPlayAch_CustomRefresh_Presets_NameDialogHint"),
                defaultName ?? string.Empty);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                L("LOCPlayAch_CustomRefresh_Presets_NameDialogTitle"),
                inputDialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 460,
                    Height = 200
                });

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = _api?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch
            {
            }

            inputDialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (inputDialog.DialogResult != true)
            {
                return false;
            }

            var rawName = inputDialog.InputText?.Trim();
            if (string.IsNullOrWhiteSpace(rawName) || rawName.Length > CustomRefreshPreset.MaxNameLength)
            {
                _api.Dialogs.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_CustomRefresh_Presets_NameInvalid"),
                        CustomRefreshPreset.MaxNameLength),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            presetName = CustomRefreshPreset.SanitizeName(rawName);
            return !string.IsNullOrWhiteSpace(presetName);
        }

        private bool ConfirmDialog(string message)
        {
            return _api.Dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private HashSet<string> LoadCachedGameIds()
        {
            try
            {
                var dataService = PlayniteAchievementsPlugin.Instance?.AchievementDataService;
                return new HashSet<string>(
                    dataService?.GetCachedGameIds() ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed loading cached game IDs for custom refresh dialog.");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private bool IncludeGameFilter(object item)
        {
            if (!(item is GameOptionItem game))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(IncludeSearchText))
            {
                return true;
            }

            return game.DisplayName?.IndexOf(IncludeSearchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool ExcludeGameFilter(object item)
        {
            if (!(item is GameOptionItem game))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(ExcludeSearchText))
            {
                return true;
            }

            return game.DisplayName?.IndexOf(ExcludeSearchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnProviderOptionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(ProviderOptionItem.IsSelected) ||
                e?.PropertyName == nameof(ProviderOptionItem.IsAuthenticated) ||
                e?.PropertyName == nameof(ProviderOptionItem.IsEnabled))
            {
                ScheduleSummaryRecalculation();
            }
        }

        private void OnGameOptionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(GameOptionItem.IsIncluded) ||
                e?.PropertyName == nameof(GameOptionItem.IsExcluded))
            {
                ScheduleSummaryRecalculation();
            }
        }

        private IReadOnlyList<IDataProvider> GetSelectedProviders()
        {
            return ProviderOptions
                .Where(option => option.IsSelected && option.IsSelectable)
                .Select(option =>
                {
                    _providersByKey.TryGetValue(option.ProviderKey, out var provider);
                    return provider;
                })
                .Where(provider => provider != null)
                .ToList();
        }

        /// <summary>
        /// Immutable snapshot of the UI state an estimate depends on, captured on the UI
        /// thread so the estimate itself can run on a background thread.
        /// </summary>
        private sealed class SummaryEstimateRequest
        {
            public IReadOnlyList<IDataProvider> Providers { get; set; }
            public CustomGameScope Scope { get; set; }
            public bool IncludeUnplayed { get; set; }
            public int RecentLimit { get; set; }
            public List<Game> LibrarySelectedGames { get; set; }
            public List<Guid> IncludeIds { get; set; }
            public List<Guid> ExcludeIds { get; set; }
            public bool RespectUserExclusions { get; set; }
            public bool ForceBypassExclusionsForExplicitIncludes { get; set; }
        }

        private SummaryEstimateRequest BuildSummaryEstimateRequest(IReadOnlyList<IDataProvider> providers)
        {
            return new SummaryEstimateRequest
            {
                Providers = providers,
                Scope = SelectedScope,
                IncludeUnplayed = UseIncludeUnplayedOverride
                    ? IncludeUnplayedOverrideValue
                    : (_settings?.Persisted?.IncludeUnplayedGames ?? true),
                RecentLimit = ResolveRecentLimitForEstimate(),
                LibrarySelectedGames = SelectedScope == CustomGameScope.LibrarySelected
                    ? (_api.MainView.SelectedGames?.Where(game => game != null).ToList() ?? new List<Game>())
                    : null,
                IncludeIds = GameOptions
                    .Where(option => option.IsIncluded)
                    .Select(option => option.GameId)
                    .Distinct()
                    .ToList(),
                ExcludeIds = GameOptions
                    .Where(option => option.IsExcluded)
                    .Select(option => option.GameId)
                    .Distinct()
                    .ToList(),
                RespectUserExclusions = RespectUserExclusions,
                ForceBypassExclusionsForExplicitIncludes = ForceBypassExclusionsForExplicitIncludes
            };
        }

        private List<Guid> ResolveEstimatedTargets(SummaryEstimateRequest request)
        {
            var providers = request?.Providers;
            if (providers == null || providers.Count == 0)
            {
                return new List<Guid>();
            }

            var includeUnplayed = request.IncludeUnplayed;
            var recentLimit = request.RecentLimit;

            IEnumerable<Game> scopedGames;
            switch (request.Scope)
            {
                case CustomGameScope.All:
                    scopedGames = _gamesById.Values;
                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }
                    break;

                case CustomGameScope.Installed:
                    scopedGames = _gamesById.Values.Where(IsInstalledOrHasOverride);
                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }
                    break;

                case CustomGameScope.Favorites:
                    scopedGames = _gamesById.Values.Where(game => game.Favorite);
                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }
                    break;

                case CustomGameScope.Recent:
                    scopedGames = _gamesById.Values
                        .Where(game => game.LastActivity.HasValue)
                        .OrderByDescending(game => game.LastActivity.Value);
                    if (!includeUnplayed)
                    {
                        scopedGames = scopedGames.Where(game => game.Playtime > 0);
                    }

                    scopedGames = scopedGames.Take(recentLimit);
                    break;

                case CustomGameScope.LibrarySelected:
                    scopedGames = request.LibrarySelectedGames ?? Enumerable.Empty<Game>();
                    break;

                case CustomGameScope.Missing:
                    scopedGames = _gamesById.Values.Where(game =>
                        !_cachedGameIds.Contains(game.Id.ToString()) &&
                        IsCapableForAnyProvider(game, providers));
                    break;

                case CustomGameScope.Explicit:
                    scopedGames = Enumerable.Empty<Game>();
                    break;

                default:
                    scopedGames = _gamesById.Values;
                    break;
            }

            if (ShouldApplyHiddenFilter(request.Scope))
            {
                scopedGames = BulkRefreshGameFilter.ApplyHiddenFilter(scopedGames, _settings?.Persisted);
            }

            var includeIds = request.IncludeIds ?? new List<Guid>();
            var excludeIds = request.ExcludeIds ?? new List<Guid>();

            var explicitIncludeSet = new HashSet<Guid>(includeIds);
            var explicitExcludeSet = new HashSet<Guid>(excludeIds);

            var orderedIds = new List<Guid>();
            var seen = new HashSet<Guid>();

            foreach (var game in scopedGames)
            {
                if (game == null || game.Id == Guid.Empty || !seen.Add(game.Id))
                {
                    continue;
                }

                orderedIds.Add(game.Id);
            }

            foreach (var includeId in includeIds)
            {
                if (seen.Add(includeId))
                {
                    orderedIds.Add(includeId);
                }
            }

            if (explicitExcludeSet.Count > 0)
            {
                orderedIds = orderedIds.Where(id => !explicitExcludeSet.Contains(id)).ToList();
            }

            if (request.RespectUserExclusions)
            {
                var excludedByUser = GameCustomDataLookup.GetExcludedRefreshGameIds(_settings?.Persisted);
                if (excludedByUser != null && excludedByUser.Count > 0)
                {
                    orderedIds = orderedIds
                        .Where(id =>
                        {
                            if (!excludedByUser.Contains(id))
                            {
                                return true;
                            }

                            return request.ForceBypassExclusionsForExplicitIncludes &&
                                   explicitIncludeSet.Contains(id) &&
                                   !explicitExcludeSet.Contains(id);
                        })
                        .ToList();
                }
            }

            return orderedIds
                .Where(id => _gamesById.TryGetValue(id, out var game) && IsCapableForAnyProvider(game, providers))
                .ToList();
        }

        private int ResolveRecentLimitForEstimate()
        {
            if (UseRecentLimitOverride &&
                int.TryParse(RecentLimitOverrideText, out var overrideValue) &&
                overrideValue > 0)
            {
                return overrideValue;
            }

            return Math.Max(1, _settings?.Persisted?.RecentRefreshGamesCount ?? 10);
        }

        private bool IsCapableForAnyProvider(Game game, IReadOnlyList<IDataProvider> providers)
        {
            if (game == null || providers == null || providers.Count == 0)
            {
                return false;
            }

            foreach (var provider in providers)
            {
                if (provider == null)
                {
                    continue;
                }

                if (IsCapableCached(provider, game))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsCapableCached(IDataProvider provider, Game game)
        {
            Dictionary<Guid, bool> providerCache;
            lock (_capabilityCacheSync)
            {
                if (!_capabilityCacheByProvider.TryGetValue(provider.ProviderKey, out providerCache))
                {
                    providerCache = new Dictionary<Guid, bool>();
                    _capabilityCacheByProvider[provider.ProviderKey] = providerCache;
                }

                if (providerCache.TryGetValue(game.Id, out var cached))
                {
                    return cached;
                }
            }

            bool result;
            try
            {
                result = provider.IsCapable(game);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Provider capability check failed for game '{game?.Name}'.");
                result = false;
            }

            lock (_capabilityCacheSync)
            {
                providerCache[game.Id] = result;
            }

            return result;
        }

        private static bool ShouldApplyHiddenFilter(CustomGameScope scope)
        {
            switch (scope)
            {
                case CustomGameScope.All:
                case CustomGameScope.Installed:
                case CustomGameScope.Favorites:
                case CustomGameScope.Recent:
                case CustomGameScope.Missing:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsInstalledOrHasOverride(Game game)
        {
            return game != null &&
                   (game.IsInstalled ||
                    GameCustomDataLookup.TryGetProviderOverride(game.Id, out _));
        }

        /// <summary>
        /// Debounces summary recalculation so bursts of checkbox toggles coalesce into one
        /// recompute. The recompute itself runs on a background thread (see
        /// <see cref="RecalculateSummary"/>), so the debounce only limits redundant work.
        /// </summary>
        private void ScheduleSummaryRecalculation()
        {
            if (_summaryDebounceTimer == null)
            {
                _summaryDebounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _summaryDebounceTimer.Tick += (s, e) =>
                {
                    _summaryDebounceTimer.Stop();
                    RecalculateSummary();
                };
            }

            _summaryDebounceTimer.Stop();
            _summaryDebounceTimer.Start();
        }

        /// <summary>
        /// Snapshots the estimate inputs on the UI thread, then resolves the estimated-target
        /// count on a background thread. The estimate walks the library with per-provider
        /// capability checks (cached per provider+game after first evaluation), which is too
        /// slow to run on the UI thread for large libraries. Stale results are dropped via a
        /// generation counter so rapid changes cannot apply out of order.
        /// </summary>
        private void RecalculateSummary()
        {
            var selectedProviders = GetSelectedProviders();
            var selectedProviderNames = selectedProviders
                .Select(provider => provider.ProviderName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            var providerDisplay = selectedProviderNames.Count == 0
                ? L("LOCPlayAch_Common_None")
                : string.Join(", ", selectedProviderNames);

            var request = BuildSummaryEstimateRequest(selectedProviders);
            var generation = ++_summaryGeneration;

            _ = Task.Run(() =>
            {
                int estimatedTargets;
                try
                {
                    estimatedTargets = ResolveEstimatedTargets(request).Count;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed resolving estimated custom refresh targets.");
                    estimatedTargets = 0;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _summaryGeneration)
                    {
                        return;
                    }

                    SummaryText = string.Format(
                        L("LOCPlayAch_CustomRefresh_SummaryFormat"),
                        providerDisplay,
                        estimatedTargets.ToString("N0", FormattingCulture.Current));

                    CanRun = selectedProviders.Count > 0 && estimatedTargets > 0;
                }));
            });
        }

        private string BuildGameDisplayName(Game game)
        {
            if (game == null)
            {
                return string.Empty;
            }

            var sourceName = game.Source?.Name;
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return game.Name ?? game.Id.ToString();
            }

            return $"{game.Name} [{sourceName}]";
        }

        private string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private bool TryCreateCurrentOptions(out CustomRefreshOptions options)
        {
            options = null;

            var hasRecentLimit = int.TryParse(RecentLimitOverrideText, out var recentLimit) && recentLimit > 0;
            if (UseRecentLimitOverride && !hasRecentLimit)
            {
                _api.Dialogs.ShowMessage(
                    L("LOCPlayAch_CustomRefresh_InvalidRecentLimit"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var selectedProviderKeys = ProviderOptions
                .Where(option => option.IsSelected && option.IsSelectable)
                .Select(option => option.ProviderKey)
                .ToList();
            var includeIds = GameOptions
                .Where(option => option.IsIncluded)
                .Select(option => option.GameId)
                .Distinct()
                .ToList();
            var excludeIds = GameOptions
                .Where(option => option.IsExcluded)
                .Select(option => option.GameId)
                .Distinct()
                .ToList();

            options = new CustomRefreshOptions
            {
                ProviderKeys = selectedProviderKeys,
                Scope = SelectedScope,
                IncludeGameIds = includeIds,
                ExcludeGameIds = excludeIds,
                RecentLimitOverride = UseRecentLimitOverride && hasRecentLimit ? (int?)recentLimit : null,
                IncludeUnplayedOverride = UseIncludeUnplayedOverride ? (bool?)IncludeUnplayedOverrideValue : null,
                RespectUserExclusions = RespectUserExclusions,
                ForceBypassExclusionsForExplicitIncludes = ForceBypassExclusionsForExplicitIncludes,
                RunProvidersInParallelOverride = UseParallelOverride ? (bool?)RunProvidersInParallelValue : null
            };
            return true;
        }

        private void ApplyOptions(CustomRefreshOptions options)
        {
            var resolved = options?.Clone() ?? new CustomRefreshOptions();

            var providerKeys = new HashSet<string>(
                resolved.ProviderKeys?
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim()) ??
                Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var providerOption in ProviderOptions)
            {
                // Gate only on IsEnabled (known synchronously). IsAuthenticated is probed
                // asynchronously after the window opens and starts pessimistically false for
                // session-managed providers (e.g. Steam), so gating on IsSelectable here would
                // silently drop a preset's providers when it is applied before the probe finishes.
                // RefreshProviderOptionsAsync deselects any provider whose probe confirms it is
                // unavailable.
                providerOption.IsSelected = providerOption.IsEnabled &&
                    providerKeys.Contains(providerOption.ProviderKey);
            }

            SelectedScope = resolved.Scope;

            var defaultRecentLimit = Math.Max(1, _settings?.Persisted?.RecentRefreshGamesCount ?? 10);
            UseRecentLimitOverride = resolved.RecentLimitOverride.HasValue;
            RecentLimitOverrideText = (resolved.RecentLimitOverride ?? defaultRecentLimit).ToString();

            UseIncludeUnplayedOverride = resolved.IncludeUnplayedOverride.HasValue;
            IncludeUnplayedOverrideValue = resolved.IncludeUnplayedOverride ??
                (_settings?.Persisted?.IncludeUnplayedGames ?? true);

            RespectUserExclusions = resolved.RespectUserExclusions;
            ForceBypassExclusionsForExplicitIncludes = resolved.ForceBypassExclusionsForExplicitIncludes;

            UseParallelOverride = resolved.RunProvidersInParallelOverride.HasValue;
            RunProvidersInParallelValue = resolved.RunProvidersInParallelOverride ??
                (_settings?.Persisted?.EnableParallelProviderRefresh ?? true);

            var includeIds = new HashSet<Guid>(
                resolved.IncludeGameIds?.Where(gameId => gameId != Guid.Empty) ?? Enumerable.Empty<Guid>());
            var excludeIds = new HashSet<Guid>(
                resolved.ExcludeGameIds?.Where(gameId => gameId != Guid.Empty) ?? Enumerable.Empty<Guid>());
            foreach (var gameOption in GameOptions)
            {
                gameOption.IsIncluded = includeIds.Contains(gameOption.GameId);
                gameOption.IsExcluded = excludeIds.Contains(gameOption.GameId);
            }

            ScheduleSummaryRecalculation();
        }

        private void UpsertPreset(string presetName, CustomRefreshOptions options)
        {
            var normalizedName = CustomRefreshPreset.SanitizeName(presetName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return;
            }

            var next = PresetOptions
                .Where(preset => preset?.Options != null)
                .Select(preset => preset.Clone())
                .ToList();
            var existingIndex = next.FindIndex(preset =>
                string.Equals(preset.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
            var updatedPreset = new CustomRefreshPreset
            {
                Name = normalizedName,
                Options = options?.Clone() ?? new CustomRefreshOptions()
            };

            if (existingIndex >= 0)
            {
                next[existingIndex] = updatedPreset;
            }
            else
            {
                next.Add(updatedPreset);
            }

            ReplacePresets(next, normalizedName);
            PersistPresetCollection();
        }

        private void LoadPresetButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySelectedPreset();
        }

        private void ApplySelectedPreset()
        {
            if (SelectedPreset?.Options == null)
            {
                return;
            }

            // Prune against providers that are missing or disabled, not against IsSelectable:
            // authentication is probed asynchronously and starts pessimistically false, so a
            // selectable provider would otherwise be pruned when the preset is applied right
            // after the window opens.
            var availableProviderKeys = ProviderOptions
                .Where(option => option.IsEnabled)
                .Select(option => option.ProviderKey)
                .ToList();
            var availableGameIds = _gamesById.Keys.ToList();
            var prunedOptions = CustomRefreshPreset.PruneUnavailableSelections(
                SelectedPreset.Options,
                availableProviderKeys,
                availableGameIds,
                out var removedProviderCount,
                out var removedGameCount);

            ApplyOptions(prunedOptions);

            if (removedProviderCount > 0 || removedGameCount > 0)
            {
                _api.Dialogs.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_CustomRefresh_Presets_LoadPrunedSummary"),
                        removedProviderCount,
                        removedGameCount),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void SavePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPreset?.Options == null)
            {
                return;
            }

            if (!TryCreateCurrentOptions(out var options))
            {
                return;
            }

            if (!ConfirmDialog(
                string.Format(
                    L("LOCPlayAch_CustomRefresh_Presets_OverwriteConfirm"),
                    SelectedPreset.Name)))
            {
                return;
            }

            UpsertPreset(SelectedPreset.Name, options);
        }

        private void SaveAsPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCreateCurrentOptions(out var options))
            {
                return;
            }

            if (!TryPromptPresetName(SelectedPreset?.Options != null ? SelectedPreset.Name : string.Empty, out var presetName))
            {
                return;
            }

            var savedPresets = PresetOptions
                .Where(preset => preset?.Options != null)
                .ToList();
            var existingPreset = savedPresets.FirstOrDefault(preset =>
                string.Equals(preset.Name, presetName, StringComparison.OrdinalIgnoreCase));
            if (existingPreset == null && savedPresets.Count >= CustomRefreshPreset.MaxPresetCount)
            {
                _api.Dialogs.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_CustomRefresh_Presets_MaxReached"),
                        CustomRefreshPreset.MaxPresetCount),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (existingPreset != null && !ConfirmDialog(
                string.Format(
                    L("LOCPlayAch_CustomRefresh_Presets_OverwriteConfirm"),
                    presetName)))
            {
                return;
            }

            UpsertPreset(presetName, options);
        }

        private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPreset?.Options == null)
            {
                return;
            }

            if (!ConfirmDialog(
                string.Format(
                    L("LOCPlayAch_CustomRefresh_Presets_DeleteConfirm"),
                    SelectedPreset.Name)))
            {
                return;
            }

            var toDeleteName = SelectedPreset.Name;
            var next = PresetOptions
                .Where(preset => preset?.Options != null &&
                                 !string.Equals(preset.Name, toDeleteName, StringComparison.OrdinalIgnoreCase))
                .Select(preset => preset.Clone())
                .ToList();
            ReplacePresets(next, selectedName: null);
            PersistPresetCollection();
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCreateCurrentOptions(out var options))
            {
                return;
            }

            ResultOptions = options;

            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}


