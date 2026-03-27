using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.ThemeMigration;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Models;
namespace PlayniteAchievements.Views
{
    /// <summary>
    /// Landing page shown to users on first plugin open to guide them through initial setup.
    /// Shows different content based on whether they have configured auth and have cached data.
    /// </summary>
    public partial class FirstTimeLandingPage : IDisposable, INotifyPropertyChanged
    {
        private readonly ILogger _logger;
        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly PlayniteAchievementsPlugin _plugin;
        private PlayniteAchievementsSettings _settings;
        private readonly ProviderRegistry _providerRegistry;
        private readonly IPlayniteAPI _api;
        private readonly ThemeDiscoveryService _themeDiscovery;
        private readonly ThemeMigrationService _themeMigration;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// View model for provider authentication status display.
        /// </summary>
        public class ProviderStatus : ObservableObject
        {
            private bool _isAuthenticated;
            private bool _isEnabled = true;
            private bool _isChecking;

            public string Name { get; set; }
            public string ProviderKey { get; set; }

            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusSubtitle));
                    OnPropertyChanged(nameof(StatusBadgeText));
                }
            }

            public bool IsAuthenticated
            {
                get => _isAuthenticated;
                set
                {
                    _isAuthenticated = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusIcon));
                    OnPropertyChanged(nameof(StatusSubtitle));
                    OnPropertyChanged(nameof(StatusBadgeText));
                }
            }

            public bool IsChecking
            {
                get => _isChecking;
                set
                {
                    _isChecking = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusIcon));
                    OnPropertyChanged(nameof(StatusSubtitle));
                    OnPropertyChanged(nameof(StatusBadgeText));
                }
            }

            public string StatusIcon => IsChecking ? "\uE946" : IsAuthenticated ? "\uE73E" : "\uE711";

            /// <summary>
            /// Gets the localized subtitle text based on authentication and enabled status.
            /// </summary>
            public string StatusSubtitle
            {
                get
                {
                    if (IsChecking)
                    {
                        return ResourceProvider.GetString("LOCPlayAch_Landing_Status_Checking")
                            ?? "Checking...";
                    }
                    if (!IsEnabled)
                    {
                        return ResourceProvider.GetString("LOCPlayAch_Settings_ProviderDisabledMessage");
                    }
                    return IsAuthenticated
                        ? ResourceProvider.GetString("LOCPlayAch_Landing_Status_ReadyToRefresh")
                        : ResourceProvider.GetString("LOCPlayAch_Landing_Status_ConfigureInSettings");
                }
            }

            /// <summary>
            /// Gets the localized badge text based on authentication and enabled status.
            /// </summary>
            public string StatusBadgeText
            {
                get
                {
                    if (IsChecking)
                    {
                        return ResourceProvider.GetString("LOCPlayAch_Landing_Status_BadgeChecking")
                            ?? "CHECKING";
                    }
                    if (!IsEnabled)
                    {
                        return ResourceProvider.GetString("LOCPlayAch_Landing_Status_BadgeDisabled");
                    }
                    return IsAuthenticated
                        ? ResourceProvider.GetString("LOCPlayAch_Landing_Status_BadgeReady")
                        : ResourceProvider.GetString("LOCPlayAch_Landing_Status_BadgeSetup");
                }
            }
        }

        private readonly ObservableCollection<ProviderStatus> _providers;
        private bool _hasAnyProviderAuth;

        public ObservableCollection<ProviderStatus> Providers => _providers;

        /// <summary>
        /// Available refresh modes for the refresh dropdown.
        /// </summary>
        public ObservableCollection<RefreshMode> RefreshModes { get; }

        private string _selectedRefreshMode = RefreshModeType.Installed.GetKey();

        /// <summary>
        /// The selected refresh mode key.
        /// </summary>
        public string SelectedRefreshMode
        {
            get => _selectedRefreshMode;
            set
            {
                _selectedRefreshMode = string.IsNullOrWhiteSpace(value)
                    ? RefreshModeType.Installed.GetKey()
                    : value;
                OnPropertyChanged(nameof(SelectedRefreshMode));
                OnPropertyChanged(nameof(RefreshActionButtonText));
            }
        }

        /// <summary>
        /// Gets the button text for the refresh action, adapting based on selected mode.
        /// Shows "Configure" when Custom mode is selected, otherwise "Begin Refresh".
        /// </summary>
        public string RefreshActionButtonText => string.Equals(
            SelectedRefreshMode,
            RefreshModeType.Custom.GetKey(),
            StringComparison.Ordinal)
            ? ResourceProvider.GetString("LOCPlayAch_Button_Configure")
            : ResourceProvider.GetString("LOCPlayAch_Landing_BeginRefresh");

        /// <summary>
        /// Gets or sets the global language for achievement text.
        /// </summary>
        public string GlobalLanguage
        {
            get => _settings.Persisted.GlobalLanguage;
            set
            {
                if (_settings.Persisted.GlobalLanguage != value)
                {
                    _settings.Persisted.GlobalLanguage = value;
                    OnPropertyChanged(nameof(GlobalLanguage));
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// Gets the Playnite application icon path for use in the UI.
        /// Returns the Playnite executable path which can be used as an Image source.
        /// </summary>
        public string PlayniteIconPath
        {
            get
            {
                try
                {
                    var playnitePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(playnitePath) && System.IO.File.Exists(playnitePath))
                    {
                        return playnitePath;
                    }
                }
                catch
                {
                    // Ignore errors
                }
                return null;
            }
        }

        /// <summary>
        /// Event raised when setup is complete and the sidebar should be shown.
        /// </summary>
        public event EventHandler SetupComplete;

        public FirstTimeLandingPage(
            IPlayniteAPI api,
            ILogger logger,
            RefreshRuntime refreshRuntime,
            ICacheManager cacheManager,
            RefreshEntryPoint refreshEntryPoint,
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry providerRegistry)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _refreshCoordinator = refreshEntryPoint ?? throw new ArgumentNullException(nameof(refreshEntryPoint));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));

            _providers = new ObservableCollection<ProviderStatus>();
            _availableThemes = new ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            _revertableThemes = new ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            _themeDiscovery = new ThemeDiscoveryService(_logger, _api);
            _themeMigration = new ThemeMigrationService(
                _logger,
                _settings,
                () => _plugin.SavePluginSettings(_settings));

            var modes = _refreshService.GetRefreshModes();
            RefreshModes = new ObservableCollection<RefreshMode>(
                modes.Where(m =>
                    m.Type != RefreshModeType.LibrarySelected &&
                    m.Type != RefreshModeType.Single));
            if (!RefreshModes.Any(m => string.Equals(m.Key, _selectedRefreshMode, StringComparison.Ordinal)))
            {
                _selectedRefreshMode = RefreshModes.FirstOrDefault()?.Key ?? RefreshModeType.Installed.GetKey();
            }

            InitializeComponent();
            InitializeThemeMigrationCustomOptions();

            DataContext = this;

            RefreshProviderStatuses();
            LoadAvailableThemes();
        }

        /// <summary>
        /// Refreshes the authentication status for all providers.
        /// Called when settings are updated to reflect credential changes.
        /// Also triggers a full state refresh to update panel visibility.
        /// </summary>
        public void RefreshProviderStatuses()
        {
            _ = RefreshProviderStatusesAsync();
        }

        private async Task RefreshProviderStatusesAsync()
        {
            // Sync provider registry from the latest persisted settings
            _providerRegistry.SyncFromSettings(_settings.Persisted);

            var providers = _refreshService.Providers;

            // Clear and populate immediately with "checking" state
            _providers.Clear();
            foreach (var provider in providers)
            {
                if (ProviderUiPolicies.ShouldHideFromSetupSurfaces(provider?.ProviderKey))
                {
                    continue;
                }

                _providers.Add(new ProviderStatus
                {
                    Name = provider.ProviderName,
                    ProviderKey = provider.ProviderKey,
                    IsEnabled = _providerRegistry.IsProviderEnabled(provider.ProviderKey),
                    IsChecking = true
                });
            }

            // Check auth in-place, updating each provider as results arrive
            foreach (var provider in providers)
            {
                if (ProviderUiPolicies.ShouldHideFromSetupSurfaces(provider?.ProviderKey))
                {
                    continue;
                }

                var status = _providers.FirstOrDefault(s => s.ProviderKey == provider.ProviderKey);
                if (status == null) continue;

                if (status.IsEnabled)
                {
                    var isAuthenticated = await _refreshService
                        .IsProviderAuthenticatedAsync(provider, CancellationToken.None)
                        .ConfigureAwait(true);
                    status.IsAuthenticated = isAuthenticated;
                }
                status.IsChecking = false;
            }

            _hasAnyProviderAuth = _providers.Any(s => s.IsEnabled && s.IsAuthenticated);

            // Raise PropertyChanged for all state-dependent properties to refresh UI
            OnPropertyChanged(nameof(GlobalLanguage));
            OnPropertyChanged(nameof(HasAnyProviderAuth));
            OnPropertyChanged(nameof(CurrentState));
            OnPropertyChanged(nameof(ShowNoAuthPanel));
            OnPropertyChanged(nameof(ShowNeedsRefreshPanel));
            OnPropertyChanged(nameof(ShowHasDataPanel));
        }

        /// <summary>
        /// Gets whether at least one enabled provider is authenticated.
        /// Delegates the auth check to RefreshRuntime.
        /// </summary>
        public bool HasAnyProviderAuth => _hasAnyProviderAuth;

        /// <summary>
        /// Gets the settings for checking if setup is complete.
        /// </summary>
        private PlayniteAchievementsSettings CurrentSettings => _plugin.Settings ?? _settings;

        /// <summary>
        /// Gets whether cached achievement data exists.
        /// </summary>
        public bool HasCachedData
        {
            get
            {
                try
                {
                    var cachedIds = _cacheManager.GetCachedGameIds();
                    return cachedIds != null && cachedIds.Count > 0;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to check for cached data.");
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets the current landing state based on auth and cache status.
        /// </summary>
        public LandingState CurrentState
        {
            get
            {
                if (!HasAnyProviderAuth)
                {
                    return LandingState.NoAuth;
                }
                else if (!HasCachedData)
                {
                    return LandingState.NeedsRefresh;
                }
                else
                {
                    return LandingState.HasData;
                }
            }
        }

        /// <summary>
        /// Gets whether to show the No Auth panel.
        /// </summary>
        public bool ShowNoAuthPanel => CurrentState == LandingState.NoAuth;

        /// <summary>
        /// Gets whether to show the Needs Refresh panel.
        /// </summary>
        public bool ShowNeedsRefreshPanel => CurrentState == LandingState.NeedsRefresh;

        /// <summary>
        /// Gets whether to show the Has Data panel.
        /// </summary>
        public bool ShowHasDataPanel => CurrentState == LandingState.HasData;

        /// <summary>
        /// Command to begin the first refresh.
        /// </summary>
        public ICommand BeginRefreshCommand => new RelayCommand(() =>
        {
            try
            {
                var modeToRun = SelectedRefreshMode;
                if (RefreshModeComboBox?.SelectedValue is string selectedValue && !string.IsNullOrWhiteSpace(selectedValue))
                {
                    modeToRun = selectedValue;
                }
                else if (RefreshModeComboBox?.SelectedItem is RefreshMode selectedMode && !string.IsNullOrWhiteSpace(selectedMode.Key))
                {
                    modeToRun = selectedMode.Key;
                }

                _logger.Info($"User clicked Begin Refresh from first-time landing page with mode: {modeToRun}");

                RefreshRequest refreshRequest;
                if (string.Equals(modeToRun, RefreshModeType.Custom.GetKey(), StringComparison.Ordinal))
                {
                    if (!CustomRefreshControl.TryShowDialog(
                        _api,
                        _refreshService,
                        () => _plugin?.PersistSettingsForUi(),
                        CurrentSettings,
                        _logger,
                        out var customOptions))
                    {
                        return;
                    }

                    refreshRequest = new RefreshRequest
                    {
                        Mode = RefreshModeType.Custom,
                        CustomOptions = customOptions
                    };
                }
                else
                {
                    refreshRequest = new RefreshRequest { ModeKey = modeToRun };
                }

                MarkSetupComplete();

                _ = _refreshCoordinator.ExecuteAsync(
                    refreshRequest,
                    new RefreshExecutionPolicy
                    {
                        ValidateAuthentication = true,
                        UseProgressWindow = false,
                        SwallowExceptions = true,
                        ErrorLogMessage = "Failed to start refresh from first-time landing page."
                    });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to begin refresh from first-time landing page.");
                _api?.Notifications?.Add(new NotificationMessage(
                    "PlayAch_FirstTimeRefreshError",
                    $"Failed to start refresh: {ex.Message}",
                    NotificationType.Error));
            }
        });

        /// <summary>
        /// Command to continue to the sidebar without refreshing.
        /// </summary>
        public ICommand ContinueCommand => new RelayCommand(() =>
        {
            try
            {
                _logger.Info("User clicked Continue from first-time landing page.");

                MarkSetupComplete();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to continue from first-time landing page.");
            }
        });

        /// <summary>
        /// Command to open the plugin settings window.
        /// </summary>
        public ICommand OpenPluginSettingsCommand => new RelayCommand(() =>
        {
            try
            {
                _logger.Info("User clicked Open Plugin Settings from first-time landing page.");
                _api.MainView.OpenPluginSettings(_plugin.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open plugin settings from first-time landing page.");
            }
        });

        /// <summary>
        /// Command to toggle a provider's enabled state.
        /// </summary>
        public ICommand ToggleProviderCommand => new RelayCommand<string>(providerKey =>
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            _ = ToggleProviderAsync(providerKey);
        });

        private async Task ToggleProviderAsync(string providerKey)
        {
            try
            {
                _logger.Info($"Toggling provider: {providerKey}");

                var newState = !_providerRegistry.IsProviderEnabled(providerKey);
                _providerRegistry.SetProviderEnabled(providerKey, newState);
                _providerRegistry.SyncToSettings(_settings.Persisted);
                SaveSettings();

                await RefreshProviderStatusesAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to toggle provider: {providerKey}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                _plugin.SavePluginSettings(_settings);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save settings from landing page.");
            }
        }

        private void MarkSetupComplete()
        {
            _settings.Persisted.FirstTimeSetupCompleted = true;

            // Save the settings to persist FirstTimeSetupCompleted
            _plugin.SavePluginSettings(_settings);

            SetupComplete?.Invoke(this, EventArgs.Empty);
        }

        // ====================================================================
        // THEME MIGRATION
        // ====================================================================

        private readonly ObservableCollection<ThemeDiscoveryService.ThemeInfo> _availableThemes;
        private readonly ObservableCollection<ThemeDiscoveryService.ThemeInfo> _revertableThemes;
        private readonly ObservableCollection<ThemeMigrationElementOption> _themeMigrationCustomOptions
            = new ObservableCollection<ThemeMigrationElementOption>();
        private string _selectedThemePath;
        private string _selectedRevertThemePath;
        private bool _showNoThemesMessage;
        private bool _showNoRevertableThemesMessage;

        /// <summary>
        /// Gets the collection of themes available for migration.
        /// </summary>
        public ObservableCollection<ThemeDiscoveryService.ThemeInfo> AvailableThemes => _availableThemes;

        /// <summary>
        /// Gets the collection of themes that can be reverted.
        /// </summary>
        public ObservableCollection<ThemeDiscoveryService.ThemeInfo> RevertableThemes => _revertableThemes;

        /// <summary>
        /// Gets the collection of custom migration element options.
        /// </summary>
        public ObservableCollection<ThemeMigrationElementOption> ThemeMigrationCustomOptions => _themeMigrationCustomOptions;

        /// <summary>
        /// Gets or sets the selected theme path for migration.
        /// </summary>
        public string SelectedThemePath
        {
            get => _selectedThemePath;
            set
            {
                _selectedThemePath = value;
                OnPropertyChanged(nameof(SelectedThemePath));
            }
        }

        /// <summary>
        /// Gets or sets the selected theme path for revert.
        /// </summary>
        public string SelectedRevertThemePath
        {
            get => _selectedRevertThemePath;
            set
            {
                _selectedRevertThemePath = value;
                OnPropertyChanged(nameof(SelectedRevertThemePath));
            }
        }

        /// <summary>
        /// Gets whether there are themes that need migrating.
        /// </summary>
        public bool HasThemesToMigrate => _availableThemes?.Count > 0;

        /// <summary>
        /// Gets whether there are themes that can be reverted.
        /// </summary>
        public bool HasRevertableThemes => _revertableThemes?.Count > 0;

        /// <summary>
        /// Gets the message to show when no themes need migrating.
        /// </summary>
        public string NoThemesMessage => ResourceProvider.GetString("LOCPlayAch_ThemeMigration_NoThemesMessage")
            ?? "No themes found that need migrating.";

        /// <summary>
        /// Gets whether to show the no themes message.
        /// </summary>
        public bool ShowNoThemesMessage
        {
            get => _showNoThemesMessage;
            private set
            {
                _showNoThemesMessage = value;
                OnPropertyChanged(nameof(ShowNoThemesMessage));
            }
        }

        /// <summary>
        /// Gets whether to show the no revertable themes message.
        /// </summary>
        public bool ShowNoRevertableThemesMessage
        {
            get => _showNoRevertableThemesMessage;
            private set
            {
                _showNoRevertableThemesMessage = value;
                OnPropertyChanged(nameof(ShowNoRevertableThemesMessage));
            }
        }

        /// <summary>
        /// Command to migrate the selected theme with Limited mode (text replacements only).
        /// </summary>
        public ICommand MigrateThemeLimitedCommand => new RelayCommand(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SelectedThemePath))
                {
                    _logger.Warn("Migrate theme limited command executed but no theme selected.");
                    return;
                }

                _logger.Info($"User requested Limited theme migration for: {SelectedThemePath}");

                await ExecuteThemeMigrationAsync(SelectedThemePath, MigrationMode.Limited);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to execute theme migration command.");
                _api?.Notifications?.Add(new NotificationMessage(
                    "PlayAch_MigrationError",
                    $"Theme migration failed: {ex.Message}",
                    NotificationType.Error));
            }
        });

        /// <summary>
        /// Command to migrate the selected theme with Full mode (text + control/binding replacements).
        /// </summary>
        public ICommand MigrateThemeFullCommand => new RelayCommand(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SelectedThemePath))
                {
                    _logger.Warn("Migrate theme full command executed but no theme selected.");
                    return;
                }

                _logger.Info($"User requested Full theme migration for: {SelectedThemePath}");

                await ExecuteThemeMigrationAsync(SelectedThemePath, MigrationMode.Full);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to execute full theme migration command.");
                _api?.Notifications?.Add(new NotificationMessage(
                    "PlayAch_MigrationError",
                    $"Theme migration failed: {ex.Message}",
                    NotificationType.Error));
            }
        });

        /// <summary>
        /// Command to migrate the selected theme with Custom mode (user-selected control modernizations).
        /// </summary>
        public ICommand MigrateThemeCustomCommand => new RelayCommand(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SelectedThemePath))
                {
                    _logger.Warn("Migrate theme custom command executed but no theme selected.");
                    return;
                }

                _logger.Info($"User requested Custom theme migration for: {SelectedThemePath}");

                await ExecuteThemeMigrationAsync(SelectedThemePath, MigrationMode.Custom,
                    BuildCustomMigrationSelection());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to execute custom theme migration command.");
                _api?.Notifications?.Add(new NotificationMessage(
                    "PlayAch_MigrationError",
                    $"Theme migration failed: {ex.Message}",
                    NotificationType.Error));
            }
        });

        /// <summary>
        /// Command to set all custom migration options to Legacy.
        /// </summary>
        public ICommand SetAllMigrationLegacyCommand => new RelayCommand(
            () => SetAllThemeMigrationCustomOptions(false));

        /// <summary>
        /// Command to set all custom migration options to Modern.
        /// </summary>
        public ICommand SetAllMigrationModernCommand => new RelayCommand(
            () => SetAllThemeMigrationCustomOptions(true));

        /// <summary>
        /// Command to set a single migration row to Legacy.
        /// </summary>
        public ICommand SetMigrationRowLegacyCommand => new RelayCommand<ThemeMigrationElementOption>(
            option => { if (option != null) option.IsModern = false; });

        /// <summary>
        /// Command to set a single migration row to Modern.
        /// </summary>
        public ICommand SetMigrationRowModernCommand => new RelayCommand<ThemeMigrationElementOption>(
            option => { if (option != null) option.IsModern = true; });

        /// <summary>
        /// Command to revert the selected theme.
        /// </summary>
        public ICommand RevertThemeCommand => new RelayCommand(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SelectedRevertThemePath))
                {
                    _logger.Warn("Revert theme command executed but no theme selected.");
                    return;
                }

                _logger.Info($"User requested theme revert for: {SelectedRevertThemePath}");

                await ExecuteThemeRevertAsync(SelectedRevertThemePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to execute theme revert command.");
                _api?.Notifications?.Add(new NotificationMessage(
                    "PlayAch_RevertError",
                    $"Theme revert failed: {ex.Message}",
                    NotificationType.Error));
            }
        });

        /// <summary>
        /// Loads the list of themes that need migrating.
        /// </summary>
        private void LoadAvailableThemes()
        {
            // Ensure we're on the UI thread before accessing ObservableCollection
            if (Dispatcher.CheckAccess())
            {
                LoadAvailableThemesInternal();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(LoadAvailableThemesInternal));
            }
        }

        private void LoadAvailableThemesInternal()
        {
            try
            {
                _availableThemes.Clear();
                _revertableThemes.Clear();

                var themesPath = _themeDiscovery.GetDefaultThemesPath();
                if (string.IsNullOrEmpty(themesPath))
                {
                    _logger.Info("No themes path found, skipping theme discovery.");
                    ShowNoThemesMessage = true;
                    ShowNoRevertableThemesMessage = true;
                    OnPropertyChanged(nameof(HasThemesToMigrate));
                    OnPropertyChanged(nameof(HasRevertableThemes));
                    return;
                }

                var cache = _settings?.Persisted?.ThemeMigrationVersionCache;
                var themes = _themeDiscovery.DiscoverThemes(themesPath, cache);

                // Load themes that need migration
                var themesNeedingMigration = themes.Where(t => t.NeedsMigration).ToList();
                foreach (var theme in themesNeedingMigration)
                {
                    _availableThemes.Add(theme);
                }

                // Load themes that can be reverted
                var themesWithBackups = themes.Where(t => t.HasBackup).ToList();
                foreach (var theme in themesWithBackups)
                {
                    _revertableThemes.Add(theme);
                }

                ShowNoThemesMessage = _availableThemes.Count == 0;
                ShowNoRevertableThemesMessage = _revertableThemes.Count == 0;
                OnPropertyChanged(nameof(HasThemesToMigrate));
                OnPropertyChanged(nameof(HasRevertableThemes));

                _logger.Info($"Loaded {_availableThemes.Count} themes that need migrating, {_revertableThemes.Count} themes that can be reverted.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load available themes.");
            }
        }

        /// <summary>
        /// Executes the theme migration.
        /// </summary>
        private async Task ExecuteThemeMigrationAsync(string themePath, MigrationMode mode,
            CustomMigrationSelection customSelection = null)
        {
            var result = await _themeMigration.MigrateThemeAsync(themePath, mode, customSelection);

            if (result.Success)
            {
                _logger.Info($"Theme migration successful: {themePath}");

                if (result.FilesBackedUp > 0)
                {
                    _api?.Dialogs?.ShowMessage(
                        result.Message + "\n\nPlease restart Playnite to apply the theme changes.",
                        "Theme Migration",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _api?.Notifications?.Add(new NotificationMessage(
                        "PlayAch_MigrationSuccess",
                        result.Message,
                        NotificationType.Info));
                }

                LoadAvailableThemes();
            }
            else
            {
                _logger.Warn($"Theme migration failed: {result.Message}");
                _api?.Notifications?.Add(new NotificationMessage(
                    "PlayAch_MigrationFailed",
                    result.Message,
                    NotificationType.Error));
            }
        }

        /// <summary>
        /// Executes the theme revert.
        /// </summary>
        private async Task ExecuteThemeRevertAsync(string themePath)
        {
            var result = await _themeMigration.RevertThemeAsync(themePath);

            if (result.Success)
            {
                _logger.Info($"Theme revert successful: {themePath}");

                _api?.Dialogs?.ShowMessage(
                    result.Message + "\n\nPlease restart Playnite to apply the theme changes.",
                    ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Revert") ?? "Revert Theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                LoadAvailableThemes();
            }
            else
            {
                _logger.Warn($"Theme revert failed: {result.Message}");
                _api?.Notifications?.Add(new NotificationMessage(
                    "PlayAch_RevertFailed",
                    result.Message,
                    NotificationType.Error));
            }
        }

        private void ThemeMigrationCustomExpander_Expanded(object sender, RoutedEventArgs e)
        {
            UpdateThemeMigrationModeButtonState();
        }

        private void ThemeMigrationCustomExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            UpdateThemeMigrationModeButtonState();
        }

        private void UpdateThemeMigrationModeButtonState()
        {
            if (ThemeMigrationPresetButtons != null && ThemeMigrationCustomExpander != null)
            {
                ThemeMigrationPresetButtons.IsEnabled = !ThemeMigrationCustomExpander.IsExpanded;
            }
        }

        public void Dispose()
        {
            SetupComplete = null;
        }

        private void InitializeThemeMigrationCustomOptions()
        {
            _themeMigrationCustomOptions.Clear();

            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginButton",
                "LOCPlayAch_ThemeMigration_Custom_Button",
                "Button"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginChart",
                "LOCPlayAch_ThemeMigration_Custom_Chart",
                "Bar Chart"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactList",
                "LOCPlayAch_ThemeMigration_Custom_CompactList",
                "Compact List"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactLocked",
                "LOCPlayAch_ThemeMigration_Custom_CompactLocked",
                "Compact Locked List"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactUnlocked",
                "LOCPlayAch_ThemeMigration_Custom_CompactUnlocked",
                "Compact Unlocked List"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginList",
                "LOCPlayAch_ThemeMigration_Custom_List",
                "Achievement Grid"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginProgressBar",
                "LOCPlayAch_ThemeMigration_Custom_ProgressBar",
                "Progress Bar"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginUserStats",
                "LOCPlayAch_ThemeMigration_Custom_UserStats",
                "Stats Panel"));
            _themeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginViewItem",
                "LOCPlayAch_ThemeMigration_Custom_ViewItem",
                "View Item"));
        }

        private ThemeMigrationElementOption CreateThemeMigrationControlOption(
            string key,
            string resourceKey,
            string fallback)
        {
            return new ThemeMigrationElementOption(
                key,
                ResourceProvider.GetString(resourceKey) ?? fallback,
                isBindingOption: false,
                isModern: true);
        }

        private CustomMigrationSelection BuildCustomMigrationSelection()
        {
            var modernControlNames = _themeMigrationCustomOptions
                .Where(option => option.IsModern)
                .Select(option => option.Key)
                .ToList();

            return new CustomMigrationSelection(modernControlNames, modernizeBindings: true);
        }

        private void SetAllThemeMigrationCustomOptions(bool isModern)
        {
            foreach (var option in _themeMigrationCustomOptions)
            {
                option.IsModern = isModern;
            }
        }

        /// <summary>
        /// Simple command implementation for landing page actions.
        /// </summary>
        private class RelayCommand : ICommand
        {
            private readonly Action _execute;

            public RelayCommand(Action execute)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            }

#pragma warning disable CS0067 // Event is never raised (CanExecute always returns true)
            public event EventHandler CanExecuteChanged;
#pragma warning restore CS0067

            public bool CanExecute(object parameter) => true;

            public void Execute(object parameter) => _execute();
        }

        /// <summary>
        /// Generic command implementation for landing page actions with parameter.
        /// </summary>
        private class RelayCommand<T> : ICommand
        {
            private readonly Action<T> _execute;

            public RelayCommand(Action<T> execute)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            }

#pragma warning disable CS0067 // Event is never raised (CanExecute always returns true)
            public event EventHandler CanExecuteChanged;
#pragma warning restore CS0067

            public bool CanExecute(object parameter) => true;

            public void Execute(object parameter)
            {
                if (parameter is T typedParam)
                {
                    _execute(typedParam);
                }
                else if (parameter == null && default(T) == null)
                {
                    _execute(default);
                }
                else if (parameter is string strParam && typeof(T) == typeof(string))
                {
                    _execute((T)(object)strParam);
                }
            }
        }
    }

    /// <summary>
    /// States for the first-time landing page.
    /// </summary>
    public enum LandingState
    {
        /// <summary>
        /// No provider authentication is configured.
        /// Shows settings navigation diagram.
        /// </summary>
        NoAuth,

        /// <summary>
        /// Authentication is configured but no cached data exists.
        /// Shows "Begin Refresh" button.
        /// </summary>
        NeedsRefresh,

        /// <summary>
        /// Authentication is configured and cached data exists.
        /// Shows "Continue" button for existing users.
        /// </summary>
        HasData
    }
}




