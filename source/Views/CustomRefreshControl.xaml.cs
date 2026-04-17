using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
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

namespace PlayniteAchievements.Views
{
    public partial class CustomRefreshControl : UserControl, INotifyPropertyChanged
    {
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
        private CancellationTokenSource _summaryCts;
        private int _summaryVersion;

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

        public event EventHandler RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        public bool? DialogResult { get; private set; }
        public CustomRefreshOptions ResultOptions { get; private set; }

        public ObservableCollection<ProviderOptionItem> ProviderOptions { get; } = new ObservableCollection<ProviderOptionItem>();
        public ObservableCollection<ScopeOptionItem> ScopeOptions { get; } = new ObservableCollection<ScopeOptionItem>();
        public ObservableCollection<GameOptionItem> GameOptions { get; } = new ObservableCollection<GameOptionItem>();
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
                RecalculateSummary();
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
                RecalculateSummary();
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
                RecalculateSummary();
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
                RecalculateSummary();
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
                RecalculateSummary();
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
                RecalculateSummary();
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
                RecalculateSummary();
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
                Name = L("LOCPlayAch_CustomRefresh_Presets_NoneOption", " "),
                Options = null
            };

            _cachedGameIds = LoadCachedGameIds();

            InitializeScopeOptions();
            InitializeProviders();
            _ = RefreshProviderOptionsAsync();
            InitializeGames();
            InitializePresets();
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
            control.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (control.DialogResult == true && control.ResultOptions != null)
            {
                options = control.ResultOptions;
                return true;
            }

            return false;
        }

        private void InitializeScopeOptions()
        {
            ScopeOptions.Clear();
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.All, DisplayName = L("LOCPlayAch_CustomRefresh_Scope_All", "All games") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Installed, DisplayName = L("LOCPlayAch_RefreshModeShort_Installed", "Installed") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Favorites, DisplayName = L("LOCPlayAch_RefreshModeShort_Favorites", "Favorites") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Recent, DisplayName = L("LOCPlayAch_RefreshModeShort_Recent", "Recent") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.LibrarySelected, DisplayName = L("LOCPlayAch_CustomRefresh_Scope_LibrarySelected", "Library selected") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Missing, DisplayName = L("LOCPlayAch_RefreshModeShort_Missing", "Missing") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = CustomGameScope.Explicit, DisplayName = L("LOCPlayAch_CustomRefresh_Scope_Explicit", "Explicit only") });
        }

        private void InitializeProviders()
        {
            ProviderOptions.Clear();
            _providersByKey.Clear();

            var readyText = L("LOCPlayAch_CustomRefresh_ProviderStatus_Ready", "Ready");
            var disabledText = L("LOCPlayAch_Common_Status_Disabled", "Disabled");
            var noAuthText = L("LOCPlayAch_Common_NotAuthenticated", "Not authenticated");

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
            }

            RecalculateSummary();
        }

        private void InitializeGames()
        {
            GameOptions.Clear();
            _gamesById.Clear();

            foreach (var game in _api.Database.Games
                .Where(game => game != null && game.Id != Guid.Empty)
                .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase))
            {
                _gamesById[game.Id] = game;

                var gameItem = new GameOptionItem(game.Id, BuildGameDisplayName(game));
                gameItem.PropertyChanged += OnGameOptionChanged;
                GameOptions.Add(gameItem);
            }
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
                L("LOCPlayAch_CustomRefresh_Presets_NameDialogHint", "Enter a preset name."),
                defaultName ?? string.Empty);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                L("LOCPlayAch_CustomRefresh_Presets_NameDialogTitle", "Save preset"),
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
                        L("LOCPlayAch_CustomRefresh_Presets_NameInvalid", "Preset name must be between 1 and {0} characters."),
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
                return new HashSet<string>(
                    _refreshService?.Cache?.GetCachedGameIds() ?? new List<string>(),
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
                RecalculateSummary();
            }
        }

        private void OnGameOptionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(GameOptionItem.IsIncluded) ||
                e?.PropertyName == nameof(GameOptionItem.IsExcluded))
            {
                RecalculateSummary();
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

        private List<Guid> ResolveEstimatedTargets(
            IReadOnlyList<IDataProvider> providers,
            CustomGameScope scope,
            bool includeUnplayed,
            int recentLimit,
            bool respectUserExclusions,
            bool forceBypassExclusionsForExplicitIncludes,
            IReadOnlyCollection<Guid> includeIds,
            IReadOnlyCollection<Guid> excludeIds,
            IReadOnlyCollection<Guid> librarySelectedIds,
            CancellationToken cancellationToken)
        {
            if (providers == null || providers.Count == 0)
            {
                return new List<Guid>();
            }

            var capabilityCache = new Dictionary<Guid, bool>();

            IEnumerable<Game> scopedGames;
            switch (scope)
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
                    scopedGames = (librarySelectedIds ?? Array.Empty<Guid>())
                        .Select(gameId => _gamesById.TryGetValue(gameId, out var game) ? game : null)
                        .Where(game => game != null);
                    break;

                case CustomGameScope.Missing:
                    scopedGames = _gamesById.Values.Where(game =>
                        !_cachedGameIds.Contains(game.Id.ToString()) &&
                        IsCapableForAnyProvider(game, providers, capabilityCache));
                    break;

                case CustomGameScope.Explicit:
                    scopedGames = Enumerable.Empty<Game>();
                    break;

                default:
                    scopedGames = _gamesById.Values;
                    break;
            }

            var explicitIncludeSet = new HashSet<Guid>(includeIds ?? Array.Empty<Guid>());
            var explicitExcludeSet = new HashSet<Guid>(excludeIds ?? Array.Empty<Guid>());

            var orderedIds = new List<Guid>();
            var seen = new HashSet<Guid>();

            foreach (var game in scopedGames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (game == null || game.Id == Guid.Empty || !seen.Add(game.Id))
                {
                    continue;
                }

                orderedIds.Add(game.Id);
            }

            foreach (var includeId in includeIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (seen.Add(includeId))
                {
                    orderedIds.Add(includeId);
                }
            }

            if (explicitExcludeSet.Count > 0)
            {
                orderedIds = orderedIds.Where(id => !explicitExcludeSet.Contains(id)).ToList();
            }

            if (respectUserExclusions)
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

                            return forceBypassExclusionsForExplicitIncludes &&
                                   explicitIncludeSet.Contains(id) &&
                                   !explicitExcludeSet.Contains(id);
                        })
                        .ToList();
                }
            }

            var resolvedIds = new List<Guid>();
            foreach (var gameId in orderedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_gamesById.TryGetValue(gameId, out var game) && IsCapableForAnyProvider(game, providers, capabilityCache))
                {
                    resolvedIds.Add(gameId);
                }
            }

            return resolvedIds;
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

        private bool IsCapableForAnyProvider(Game game, IReadOnlyList<IDataProvider> providers, IDictionary<Guid, bool> capabilityCache = null)
        {
            if (game == null || providers == null || providers.Count == 0)
            {
                return false;
            }

            if (capabilityCache != null && capabilityCache.TryGetValue(game.Id, out var cachedResult))
            {
                return cachedResult;
            }

            foreach (var provider in providers)
            {
                if (provider == null)
                {
                    continue;
                }

                try
                {
                    if (provider.IsCapable(game))
                    {
                        capabilityCache?[game.Id] = true;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Provider capability check failed for game '{game?.Name}'.");
                }
            }

            capabilityCache?[game.Id] = false;

            return false;
        }

        private static bool IsInstalledOrHasOverride(Game game)
        {
            return game != null &&
                   (game.IsInstalled ||
                    GameCustomDataLookup.TryGetXeniaTitleIdOverride(game.Id, out _) ||
                    GameCustomDataLookup.TryGetShadPS4MatchIdOverride(game.Id, out _));
        }

        private void RecalculateSummary()
        {
            _ = RecalculateSummaryAsync();
        }

        private async Task RecalculateSummaryAsync()
        {
            var selectedProviders = GetSelectedProviders();
            var selectedProviderNames = selectedProviders
                .Select(provider => provider.ProviderName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            var providerDisplay = selectedProviderNames.Count == 0
                ? L("LOCPlayAch_CustomRefresh_None", "None")
                : string.Join(", ", selectedProviderNames);

            if (selectedProviders.Count == 0)
            {
                SummaryText = string.Format(
                    L("LOCPlayAch_CustomRefresh_SummaryFormat", "Providers: {0} | Estimated targets: {1}"),
                    providerDisplay,
                    0);
                CanRun = false;
                return;
            }

            var includeUnplayed = UseIncludeUnplayedOverride
                ? IncludeUnplayedOverrideValue
                : (_settings?.Persisted?.IncludeUnplayedGames ?? true);
            var recentLimit = ResolveRecentLimitForEstimate();
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
            var librarySelectedIds = SelectedScope == CustomGameScope.LibrarySelected
                ? (_api.MainView.SelectedGames?
                    .Where(game => game != null && game.Id != Guid.Empty)
                    .Select(game => game.Id)
                    .Distinct()
                    .ToList() ?? new List<Guid>())
                : new List<Guid>();

            var version = Interlocked.Increment(ref _summaryVersion);
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _summaryCts, newCts);
            try { oldCts?.Cancel(); } catch { }
            try { oldCts?.Dispose(); } catch { }

            SummaryText = string.Format(
                L("LOCPlayAch_CustomRefresh_SummaryFormat", "Providers: {0} | Estimated targets: {1}"),
                providerDisplay,
                L("LOCPlayAch_Common_Loading", "Calculating..."));
            CanRun = false;

            int estimatedTargets;
            try
            {
                estimatedTargets = await Task.Run(
                    () => ResolveEstimatedTargets(
                        selectedProviders,
                        SelectedScope,
                        includeUnplayed,
                        recentLimit,
                        RespectUserExclusions,
                        ForceBypassExclusionsForExplicitIncludes,
                        includeIds,
                        excludeIds,
                        librarySelectedIds,
                        newCts.Token).Count,
                    newCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to calculate custom refresh summary.");
                if (version != _summaryVersion)
                {
                    return;
                }

                SummaryText = string.Format(
                    L("LOCPlayAch_CustomRefresh_SummaryFormat", "Providers: {0} | Estimated targets: {1}"),
                    providerDisplay,
                    L("LOCPlayAch_Common_Unknown", "Unknown"));
                CanRun = selectedProviders.Count > 0;
                return;
            }

            if (version != _summaryVersion)
            {
                return;
            }

            SummaryText = string.Format(
                L("LOCPlayAch_CustomRefresh_SummaryFormat", "Providers: {0} | Estimated targets: {1}"),
                providerDisplay,
                estimatedTargets);

            CanRun = selectedProviders.Count > 0 && estimatedTargets > 0;
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

        private string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
        }

        private bool TryCreateCurrentOptions(out CustomRefreshOptions options)
        {
            options = null;

            var hasRecentLimit = int.TryParse(RecentLimitOverrideText, out var recentLimit) && recentLimit > 0;
            if (UseRecentLimitOverride && !hasRecentLimit)
            {
                _api.Dialogs.ShowMessage(
                    L("LOCPlayAch_CustomRefresh_InvalidRecentLimit", "Recent limit override must be a positive number."),
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
                providerOption.IsSelected = providerOption.IsSelectable &&
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

            RecalculateSummary();
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
            if (SelectedPreset?.Options == null)
            {
                return;
            }

            var availableProviderKeys = ProviderOptions
                .Where(option => option.IsSelectable)
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
                        L("LOCPlayAch_CustomRefresh_Presets_LoadPrunedSummary", "Preset loaded with adjustments: removed {0} unavailable provider(s) and {1} missing game reference(s)."),
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
                    L("LOCPlayAch_CustomRefresh_Presets_OverwriteConfirm", "Overwrite preset \"{0}\"?"),
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
                        L("LOCPlayAch_CustomRefresh_Presets_MaxReached", "You can save up to {0} presets."),
                        CustomRefreshPreset.MaxPresetCount),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (existingPreset != null && !ConfirmDialog(
                string.Format(
                    L("LOCPlayAch_CustomRefresh_Presets_OverwriteConfirm", "Overwrite preset \"{0}\"?"),
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
                    L("LOCPlayAch_CustomRefresh_Presets_DeleteConfirm", "Delete preset \"{0}\"?"),
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


