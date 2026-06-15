// SettingsControl.xaml.cs
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Threading.Tasks;
using System.ComponentModel;
using PlayniteAchievements.Services;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.ThemeMigration;
using Playnite.SDK;
using Playnite.SDK.Models;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using WinForms = System.Windows.Forms;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views
{
    public partial class SettingsControl : UserControl, IDisposable
    {
        // -----------------------------
        // Mock data for settings preview
        // -----------------------------

        private System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> _mockCompactListItems;

        /// <summary>
        /// Gets mock achievement items for compact list preview in settings.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> MockCompactListItems
        {
            get
            {
                if (_mockCompactListItems == null)
                {
                    _mockCompactListItems = new System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem>(
                        MockDataHelper.CreateMockCompactListItems(
                            GetShowRarityBar(), GetShowRarityGlow(),
                            GetShowHiddenIcon(), GetShowHiddenTitle(),
                            GetShowHiddenDescription(), GetShowHiddenSuffix(), GetShowLockedIcon()));
                }
                return _mockCompactListItems;
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> _mockCompactUnlockedListItems;

        /// <summary>
        /// Gets mock unlocked achievement items for unlocked list preview.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> MockCompactUnlockedListItems
        {
            get
            {
                if (_mockCompactUnlockedListItems == null)
                {
                    _mockCompactUnlockedListItems = new System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem>(
                        MockDataHelper.CreateMockUnlockedListItems(
                            GetShowRarityBar(), GetShowRarityGlow(), GetShowLockedIcon()));
                }
                return _mockCompactUnlockedListItems;
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> _mockCompactLockedListItems;

        /// <summary>
        /// Gets mock locked achievement items for locked list preview.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> MockCompactLockedListItems
        {
            get
            {
                if (_mockCompactLockedListItems == null)
                {
                    _mockCompactLockedListItems = new System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem>(
                        MockDataHelper.CreateMockLockedListItems(
                            GetShowRarityBar(), GetShowRarityGlow(),
                            GetShowHiddenIcon(), GetShowHiddenTitle(),
                            GetShowHiddenDescription(), GetShowHiddenSuffix(), GetShowLockedIcon()));
                }
                return _mockCompactLockedListItems;
            }
        }

        private List<AchievementDisplayItem> _mockDataGridItems;

        /// <summary>
        /// Gets mock achievement items for datagrid preview in settings.
        /// </summary>
        public List<AchievementDisplayItem> MockDataGridItems
        {
            get
            {
                if (_mockDataGridItems == null)
                {
                    _mockDataGridItems = MockDataHelper.CreateMockDataGridItems(
                        GetShowRarityBar(), GetShowRarityGlow(),
                        GetShowHiddenIcon(), GetShowHiddenTitle(),
                        GetShowHiddenDescription(), GetShowHiddenSuffix(), GetShowLockedIcon());
                }
                return _mockDataGridItems;
            }
        }

        private ModernThemeBindings _previewThemeData;
        private ModernThemeBindings _unlockedPreviewThemeData;
        private ModernThemeBindings _hiddenPreviewThemeData;
        private ModernThemeBindings _lockedPreviewThemeData;

        /// <summary>
        /// Gets modern theme bindings populated with mock achievements for modern control previews.
        /// Used by modern controls via ThemeDataOverride binding.
        /// </summary>
        public ModernThemeBindings PreviewThemeData
        {
            get
            {
                if (_previewThemeData == null)
                {
                    _previewThemeData = MockDataHelper.GetPreviewThemeData();
                }
                return _previewThemeData;
            }
        }

        /// <summary>
        /// Gets modern theme bindings with a single unlocked achievement for visibility preview.
        /// </summary>
        public ModernThemeBindings UnlockedPreviewThemeData
        {
            get
            {
                if (_unlockedPreviewThemeData == null)
                {
                    _unlockedPreviewThemeData = MockDataHelper.GetUnlockedPreviewThemeData();
                }
                return _unlockedPreviewThemeData;
            }
        }

        /// <summary>
        /// Gets modern theme bindings with a single hidden achievement for visibility preview.
        /// </summary>
        public ModernThemeBindings HiddenPreviewThemeData
        {
            get
            {
                if (_hiddenPreviewThemeData == null)
                {
                    _hiddenPreviewThemeData = MockDataHelper.GetHiddenPreviewThemeData();
                }
                return _hiddenPreviewThemeData;
            }
        }

        /// <summary>
        /// Gets modern theme bindings with a single locked achievement for visibility preview.
        /// </summary>
        public ModernThemeBindings LockedPreviewThemeData
        {
            get
            {
                if (_lockedPreviewThemeData == null)
                {
                    _lockedPreviewThemeData = MockDataHelper.GetLockedPreviewThemeData();
                }
                return _lockedPreviewThemeData;
            }
        }

        // Helper methods to get settings values with defaults
        private bool GetShowRarityBar() => _settingsViewModel?.Settings?.Persisted?.ShowCompactListRarityBar ?? true;
        private bool GetShowRarityGlow() => _settingsViewModel?.Settings?.Persisted?.ShowRarityGlow ?? true;
        private bool GetShowHiddenIcon() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenIcon ?? true;
        private bool GetShowHiddenTitle() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenTitle ?? true;
        private bool GetShowHiddenDescription() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenDescription ?? true;
        private bool GetShowHiddenSuffix() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenSuffix ?? true;
        private bool GetShowLockedIcon() => _settingsViewModel?.Settings?.Persisted?.ShowLockedIcon ?? true;

        /// <summary>
        /// Refreshes mock preview items to reflect current settings.
        /// Repopulates collections with new items that have updated visibility settings.
        /// </summary>
        public void RefreshMockPreviews()
        {
            var settings = _settingsViewModel?.Settings?.Persisted;
            if (settings == null) return;

            // Repopulate compact list items
            if (_mockCompactListItems != null)
            {
                _mockCompactListItems.Clear();
                var newItems = MockDataHelper.CreateMockCompactListItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow,
                    settings.ShowHiddenIcon, settings.ShowHiddenTitle,
                    settings.ShowHiddenDescription, settings.ShowHiddenSuffix, settings.ShowLockedIcon);
                foreach (var item in newItems)
                    _mockCompactListItems.Add(item);
            }

            // Repopulate unlocked list items
            if (_mockCompactUnlockedListItems != null)
            {
                _mockCompactUnlockedListItems.Clear();
                var newItems = MockDataHelper.CreateMockUnlockedListItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow, settings.ShowLockedIcon);
                foreach (var item in newItems)
                    _mockCompactUnlockedListItems.Add(item);
            }

            // Repopulate locked list items
            if (_mockCompactLockedListItems != null)
            {
                _mockCompactLockedListItems.Clear();
                var newItems = MockDataHelper.CreateMockLockedListItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow,
                    settings.ShowHiddenIcon, settings.ShowHiddenTitle,
                    settings.ShowHiddenDescription, settings.ShowHiddenSuffix, settings.ShowLockedIcon);
                foreach (var item in newItems)
                    _mockCompactLockedListItems.Add(item);
            }

            // Repopulate datagrid items
            if (_mockDataGridItems != null)
            {
                _mockDataGridItems = MockDataHelper.CreateMockDataGridItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow,
                    settings.ShowHiddenIcon, settings.ShowHiddenTitle,
                    settings.ShowHiddenDescription, settings.ShowHiddenSuffix, settings.ShowLockedIcon);
                // For List<T>, need to raise property changed - but since binding uses ItemsSource,
                // we'll assign a new list which triggers refresh
            }

            // Refresh the preview modern theme bindings used by modern controls
            _previewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
            _unlockedPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
            _hiddenPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
            _lockedPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
        }

        // Theme migration UI state properties
        public static readonly DependencyProperty AvailableThemesProperty =
            DependencyProperty.Register(
                nameof(AvailableThemes),
                typeof(System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>),
                typeof(SettingsControl),
                new PropertyMetadata(null));

        public System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo> AvailableThemes
        {
            get => (System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>)GetValue(AvailableThemesProperty);
            set => SetValue(AvailableThemesProperty, value);
        }

        public static readonly DependencyProperty RevertableThemesProperty =
            DependencyProperty.Register(
                nameof(RevertableThemes),
                typeof(System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>),
                typeof(SettingsControl),
                new PropertyMetadata(null));

        public System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo> RevertableThemes
        {
            get => (System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>)GetValue(RevertableThemesProperty);
            set => SetValue(RevertableThemesProperty, value);
        }

        public static readonly DependencyProperty SelectedThemePathProperty =
            DependencyProperty.Register(
                nameof(SelectedThemePath),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Empty, OnSelectedThemePathChanged));

        public string SelectedThemePath
        {
            get => (string)GetValue(SelectedThemePathProperty);
            set => SetValue(SelectedThemePathProperty, value);
        }

        public static readonly DependencyProperty SelectedRevertThemePathProperty =
            DependencyProperty.Register(
                nameof(SelectedRevertThemePath),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Empty));

        public string SelectedRevertThemePath
        {
            get => (string)GetValue(SelectedRevertThemePathProperty);
            set => SetValue(SelectedRevertThemePathProperty, value);
        }

        public static readonly DependencyProperty HasThemesToMigrateProperty =
            DependencyProperty.Register(
                nameof(HasThemesToMigrate),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool HasThemesToMigrate
        {
            get => (bool)GetValue(HasThemesToMigrateProperty);
            set => SetValue(HasThemesToMigrateProperty, value);
        }

        public static readonly DependencyProperty UseScrollableAchievementsForThemeMigrationProperty =
            DependencyProperty.Register(
                nameof(UseScrollableAchievementsForThemeMigration),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false, OnUseScrollableAchievementsForThemeMigrationChanged));

        public bool UseScrollableAchievementsForThemeMigration
        {
            get => (bool)GetValue(UseScrollableAchievementsForThemeMigrationProperty);
            set => SetValue(UseScrollableAchievementsForThemeMigrationProperty, value);
        }

        public static readonly DependencyProperty HasRevertableThemesProperty =
            DependencyProperty.Register(
                nameof(HasRevertableThemes),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool HasRevertableThemes
        {
            get => (bool)GetValue(HasRevertableThemesProperty);
            set => SetValue(HasRevertableThemesProperty, value);
        }

        public static readonly DependencyProperty ShowNoThemesMessageProperty =
            DependencyProperty.Register(
                nameof(ShowNoThemesMessage),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(true));

        public bool ShowNoThemesMessage
        {
            get => (bool)GetValue(ShowNoThemesMessageProperty);
            set => SetValue(ShowNoThemesMessageProperty, value);
        }

        public static readonly DependencyProperty ShowNoRevertableThemesMessageProperty =
            DependencyProperty.Register(
                nameof(ShowNoRevertableThemesMessage),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(true));

        public bool ShowNoRevertableThemesMessage
        {
            get => (bool)GetValue(ShowNoRevertableThemesMessageProperty);
            set => SetValue(ShowNoRevertableThemesMessageProperty, value);
        }

        public static readonly DependencyProperty HasStartPageInstalledProperty =
            DependencyProperty.Register(
                nameof(HasStartPageInstalled),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool HasStartPageInstalled
        {
            get => (bool)GetValue(HasStartPageInstalledProperty);
            set => SetValue(HasStartPageInstalledProperty, value);
        }

        public static readonly DependencyProperty ShowStartPageNotInstalledMessageProperty =
            DependencyProperty.Register(
                nameof(ShowStartPageNotInstalledMessage),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(true));

        public bool ShowStartPageNotInstalledMessage
        {
            get => (bool)GetValue(ShowStartPageNotInstalledMessageProperty);
            set => SetValue(ShowStartPageNotInstalledMessageProperty, value);
        }

        public static readonly DependencyProperty HasStartPageBackupProperty =
            DependencyProperty.Register(
                nameof(HasStartPageBackup),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool HasStartPageBackup
        {
            get => (bool)GetValue(HasStartPageBackupProperty);
            set => SetValue(HasStartPageBackupProperty, value);
        }

        public ObservableCollection<ThemeMigrationElementOption> ThemeMigrationCustomOptions { get; } =
            new ObservableCollection<ThemeMigrationElementOption>();

        public static readonly DependencyProperty LegacyManualImportStatusProperty =
            DependencyProperty.Register(
                nameof(LegacyManualImportStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_CustomRefresh_ProviderStatus_Ready")));

        public string LegacyManualImportStatus
        {
            get => (string)GetValue(LegacyManualImportStatusProperty);
            set => SetValue(LegacyManualImportStatusProperty, value);
        }

        public static readonly DependencyProperty LegacyManualImportBusyProperty =
            DependencyProperty.Register(
                nameof(LegacyManualImportBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool LegacyManualImportBusy
        {
            get => (bool)GetValue(LegacyManualImportBusyProperty);
            set => SetValue(LegacyManualImportBusyProperty, value);
        }

        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly ILogger _logger;
        private readonly ThemeDiscoveryService _themeDiscovery;
        private readonly ThemeMigrationService _themeMigration;
        private readonly StartPageCompatibilityService _startPageCompatibility;
        private readonly ProviderRegistry _providerRegistry;

        /// <summary>
        /// Exposes the Exophase provider settings edit copy so XAML can bind to it via
        /// <c>ElementName=SettingsControlRoot</c>.  Populated lazily when the Achievement
        /// Notifications tab is first initialised; may be null if Exophase is not configured.
        /// </summary>
        public Providers.Exophase.ExophaseSettings ExophaseEditSettings { get; private set; }

        private readonly Dictionary<string, ProviderSettingsViewBase> _providerViewsByKey = new Dictionary<string, ProviderSettingsViewBase>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<NotificationSoundOption> _notificationSoundOptions = new ObservableCollection<NotificationSoundOption>();
        private readonly ObservableCollection<NotificationStyleOption> _notificationStyleOptions = new ObservableCollection<NotificationStyleOption>();
        private readonly ObservableCollection<NotificationProviderOption> _notificationProviderOptions = new ObservableCollection<NotificationProviderOption>();
        private readonly ObservableCollection<NotificationPreviewGameOption> _notificationPreviewGameOptions = new ObservableCollection<NotificationPreviewGameOption>();
        private readonly ObservableCollection<CustomStyleSlotOption> _customStyleSlotOptions = new ObservableCollection<CustomStyleSlotOption>();
        private readonly DispatcherTimer _notificationAutoPopupPreviewTimer;
        private Providers.Local.LocalSettings _notificationPreviewSettings;
        private bool _isRefreshingNotificationSoundSelection;
        private bool _isRefreshingNotificationStyleSelection;
        private bool _isRefreshingOverlayPresetControls;
        private bool _isRefreshingCustomStyleSlotSelection;
        private ICollectionView _providerNavigationView;
        private bool _providerNavigationBuilt;
        private const string SuccessStoryExtensionId = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private const string SuccessStoryFolderName = "SuccessStory";
        private const string DefaultSteamSoundPath = @"Resources\Sounds\Steam.wav";

        private sealed class NotificationSoundOption
        {
            public string DisplayName { get; set; }

            public string SoundPath { get; set; }

            public bool IsDefault { get; set; }
        }

        private sealed class NotificationStyleOption
        {
            public string DisplayName { get; set; }

            public string StyleKey { get; set; }
        }

        private sealed class NotificationProviderOption
        {
            public string DisplayName { get; set; }

            public string ProviderKey { get; set; }
        }

        private sealed class NotificationPreviewGameOption
        {
            public string DisplayName { get; set; }

            public Game Game { get; set; }
        }

        private sealed class CustomStyleSlotOption
        {
            public string DisplayName { get; set; }

            public int SlotNumber { get; set; }
        }

        public static readonly DependencyProperty ProviderNavigationItemsProperty =
            DependencyProperty.Register(
                nameof(ProviderNavigationItems),
                typeof(ObservableCollection<ProviderNavigationItem>),
                typeof(SettingsControl),
                new PropertyMetadata(null));

        public ObservableCollection<ProviderNavigationItem> ProviderNavigationItems
        {
            get => (ObservableCollection<ProviderNavigationItem>)GetValue(ProviderNavigationItemsProperty);
            set => SetValue(ProviderNavigationItemsProperty, value);
        }

        public static readonly DependencyProperty SelectedProviderNavigationItemProperty =
            DependencyProperty.Register(
                nameof(SelectedProviderNavigationItem),
                typeof(ProviderNavigationItem),
                typeof(SettingsControl),
                new PropertyMetadata(null, OnSelectedProviderNavigationItemChanged));

        public ProviderNavigationItem SelectedProviderNavigationItem
        {
            get => (ProviderNavigationItem)GetValue(SelectedProviderNavigationItemProperty);
            set => SetValue(SelectedProviderNavigationItemProperty, value);
        }

        public static readonly DependencyProperty ProviderSearchTextProperty =
            DependencyProperty.Register(
                nameof(ProviderSearchText),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Empty, OnProviderSearchTextChanged));

        public string ProviderSearchText
        {
            get => (string)GetValue(ProviderSearchTextProperty);
            set => SetValue(ProviderSearchTextProperty, value);
        }

        /// <summary>
        /// Set before opening settings to navigate directly to a provider tab.
        /// Cleared after use.
        /// </summary>
        public static string PendingNavigationProviderKey { get; set; }
        public static string PendingNavigationTabName { get; set; }
        public static string CurrentSelectedTabName { get; private set; }
        public static string CurrentSelectedProviderKey { get; private set; }

        public SettingsControl(
            PlayniteAchievementsSettingsViewModel settingsViewModel,
            ILogger logger,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry providerRegistry)
        {
            _settingsViewModel = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));

            _themeDiscovery = new ThemeDiscoveryService(_logger, plugin.PlayniteApi);
            _themeMigration = new ThemeMigrationService(
                _logger,
                _settingsViewModel.Settings,
                () =>
                {
                    _plugin.SavePluginSettings(_settingsViewModel.Settings);
                    NotificationPublisher.ClosePersistentSettingsPreview();
                });
            _startPageCompatibility = new StartPageCompatibilityService(_logger, _plugin, plugin.PlayniteApi);

            _notificationAutoPopupPreviewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _notificationAutoPopupPreviewTimer.Tick += NotificationAutoPopupPreviewTimer_Tick;

            InitializeComponent();

            // Initialize provider navigation overview
            ProviderNavigationItems = new ObservableCollection<ProviderNavigationItem>();

            // Playnite does not reliably set DataContext for settings views.
            // Bind directly to the settings model so XAML uses {Binding SomeSetting}.
            DataContext = _settingsViewModel.Settings;

            // Initialize theme collections
            AvailableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            RevertableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            InitializeThemeMigrationCustomOptions();

            // Subscribe to settings property changes to refresh mock previews
            _settingsViewModel.Settings.Persisted.PropertyChanged += OnSettingsPropertyChanged;

            // Debug logging to verify DataContext and Settings values
            _logger?.Info($"SettingsControl created. DataContext type: {DataContext?.GetType().Name}");
            _logger?.Info($"Settings.EnablePeriodicUpdates: {_settingsViewModel.Settings.Persisted.EnablePeriodicUpdates}");

            Loaded += (s, e) =>
            {
                // Ensure DataContext is still correct after Playnite initialization
                if (DataContext is not PlayniteAchievementsSettings)
                {
                    _logger?.Info($"DataContext was wrong type in Loaded event: {DataContext?.GetType().Name}, fixing...");
                    DataContext = _settingsViewModel.Settings;
                    _logger?.Info($"DataContext fixed to: {DataContext?.GetType().Name}");
                }
                else
                {
                    _logger?.Info($"DataContext verified correct in Loaded event: {DataContext?.GetType().Name}");
                }

                // Load themes on initial load
                LoadThemes();
                RefreshStartPageCompatibilityState();

                // Initialize Achievement Notifications tab with Local provider settings
                InitializeAchievementNotificationsTab();

                // Build provider navigation items for sidebar
                BuildProviderNavigationItems();

                // Navigate to a specific provider item if requested (e.g., from auth notification click).
                NavigateToPendingProvider();
            };
        }

        private void InitializeAchievementNotificationsTab()
        {
            try
            {
                var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
                if (localSettings == null)
                {
                    _logger?.Warn("Could not get Local provider settings for Achievement Notifications tab.");
                    return;
                }

                var achievementNotificationsTab = SettingsTabControl?.Items?.Cast<TabItem>()
                    ?.FirstOrDefault(t => string.Equals(t.Name, "AchievementNotificationsTab", StringComparison.OrdinalIgnoreCase));

                if (achievementNotificationsTab != null)
                {
                    achievementNotificationsTab.DataContext = localSettings;

                    // Populate ExophaseEditSettings so the XAML Exophase checkbox can bind via ElementName.
                    ExophaseEditSettings = _providerRegistry?.GetSettingsForEdit("Exophase") as Providers.Exophase.ExophaseSettings;

                    if (!ReferenceEquals(_notificationPreviewSettings, localSettings))
                    {
                        if (_notificationPreviewSettings != null)
                        {
                            _notificationPreviewSettings.PropertyChanged -= LocalNotificationSettings_PropertyChanged;
                        }

                        _notificationPreviewSettings = localSettings;
                        _notificationPreviewSettings.PropertyChanged += LocalNotificationSettings_PropertyChanged;
                    }

                    RefreshAchievementNotificationControls(localSettings);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to initialize Achievement Notifications tab.");
            }
        }

        private void RefreshAchievementNotificationControls(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null)
                return;

            MigrateLegacyCustomSoundPath(localSettings);

            var soundOptions = BuildNotificationSoundOptions(localSettings);

            if (NotificationsBundledUnlockSoundComboBox != null)
            {
                _isRefreshingNotificationSoundSelection = true;
                try
                {
                    _notificationSoundOptions.Clear();
                    foreach (var option in soundOptions)
                    {
                        _notificationSoundOptions.Add(option);
                    }

                    NotificationsBundledUnlockSoundComboBox.ItemsSource = _notificationSoundOptions;

                    var selectedPath = localSettings.EffectiveBundledUnlockSoundPath;
                    var selectedOption = _notificationSoundOptions.FirstOrDefault(option =>
                        string.Equals(option.SoundPath, selectedPath, StringComparison.OrdinalIgnoreCase));

                    if (selectedOption == null)
                    {
                        selectedOption = _notificationSoundOptions.FirstOrDefault();
                    }

                    NotificationsBundledUnlockSoundComboBox.SelectedItem = selectedOption;
                    if (selectedOption != null)
                    {
                        localSettings.BundledUnlockSoundPath = selectedOption.SoundPath;
                    }
                }
                finally
                {
                    _isRefreshingNotificationSoundSelection = false;
                }
            }

            UpdateNotificationSoundButtons();
            RefreshNotificationStyleControls();
            RefreshOverlayRuntimeControls(localSettings);

            if (NotificationsPollingIntervalSecondsTextBox != null)
            {
                NotificationsPollingIntervalSecondsTextBox.Text = localSettings.ActiveGameMonitoringIntervalSeconds.ToString();
            }

            if (NotificationsOverlayFadeInMillisecondsTextBox != null)
            {
                NotificationsOverlayFadeInMillisecondsTextBox.Text = localSettings.UnlockOverlayFadeInMilliseconds.ToString();
            }

            if (NotificationsOverlayFadeOutMillisecondsTextBox != null)
            {
                NotificationsOverlayFadeOutMillisecondsTextBox.Text = localSettings.UnlockOverlayFadeOutMilliseconds.ToString();
            }

            if (NotificationsUnlockSoundLeadMillisecondsTextBox != null)
            {
                NotificationsUnlockSoundLeadMillisecondsTextBox.Text = localSettings.UnlockSoundLeadMilliseconds.ToString();
            }

            if (NotificationsScreenshotDelayMillisecondsTextBox != null)
            {
                NotificationsScreenshotDelayMillisecondsTextBox.Text = localSettings.ScreenshotDelayMilliseconds.ToString();
            }

            RefreshCustomStyleSlotControls(localSettings);
            UpdateCustomStyleInlinePreview(localSettings);
        }

        private void LocalNotificationSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!(sender is Providers.Local.LocalSettings localSettings))
            {
                return;
            }

            if (IsCustomInlinePreviewProperty(e.PropertyName))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ShouldRefreshCustomStyleSlotControls(e.PropertyName))
                    {
                        RefreshCustomStyleSlotControls(localSettings);
                    }

                    UpdateCustomStyleInlinePreview(localSettings);
                }));
            }

            if (NotificationsAutoPopupPreviewCheckBox?.IsChecked == true && IsCustomPopupPreviewProperty(e.PropertyName))
            {
                Dispatcher.BeginInvoke(new Action(ScheduleAutoPopupPreview));
            }
        }

        private static bool ShouldRefreshCustomStyleSlotControls(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return true;
            }

            return string.Equals(propertyName, nameof(Providers.Local.LocalSettings.SelectedCustomStyleSlot), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.CustomOverlayStyleSlots), StringComparison.Ordinal);
        }

        private static bool IsCustomInlinePreviewProperty(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return true;
            }

            switch (propertyName)
            {
                case nameof(Providers.Local.LocalSettings.OverlayCustomWidth):
                case nameof(Providers.Local.LocalSettings.OverlayCustomHeight):
                case nameof(Providers.Local.LocalSettings.OverlayCustomCornerRadius):
                case nameof(Providers.Local.LocalSettings.OverlayCustomIconSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleFontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomDetailFontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaFontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomAutoResizeToContent):
                case nameof(Providers.Local.LocalSettings.OverlayCustomWrapAllText):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowLine1):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowBorder):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowGameName):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowMeta):
                case nameof(Providers.Local.LocalSettings.OverlayCustomBackgroundColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomBorderColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomAccentColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomDetailColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomBackgroundImagePath):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleTemplate):
                case nameof(Providers.Local.LocalSettings.OverlayCustomGameNameTemplate):
                case nameof(Providers.Local.LocalSettings.OverlayCustomAchievementTemplate):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaTemplate):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleBold):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleItalic):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleUnderline):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleStrikethrough):
                case nameof(Providers.Local.LocalSettings.OverlayCustomDetailBold):
                case nameof(Providers.Local.LocalSettings.OverlayCustomDetailItalic):
                case nameof(Providers.Local.LocalSettings.OverlayCustomDetailUnderline):
                case nameof(Providers.Local.LocalSettings.OverlayCustomDetailStrikethrough):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaBold):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaItalic):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaUnderline):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaStrikethrough):
                case nameof(Providers.Local.LocalSettings.OverlayCustomIconSource):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowIconRarityGlow):
                case nameof(Providers.Local.LocalSettings.OverlayCustomCoverImagePath):
                case nameof(Providers.Local.LocalSettings.OverlayCustomBannerImagePath):
                case nameof(Providers.Local.LocalSettings.EnableGameBannerAsBackground):
                case nameof(Providers.Local.LocalSettings.GameBannerOpacity):
                case nameof(Providers.Local.LocalSettings.GameBannerBlurRadius):
                case nameof(Providers.Local.LocalSettings.EnableGameCoverInOverlay):
                case nameof(Providers.Local.LocalSettings.GameCoverPosition):
                case nameof(Providers.Local.LocalSettings.GameCoverWidth):
                case nameof(Providers.Local.LocalSettings.SelectedCustomStyleSlot):
                case nameof(Providers.Local.LocalSettings.CustomOverlayStyleSlots):
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsCustomPopupPreviewProperty(string propertyName)
        {
            return IsCustomInlinePreviewProperty(propertyName)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.UnlockOverlayDurationMilliseconds), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.UnlockOverlayFadeInMilliseconds), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.UnlockOverlayFadeOutMilliseconds), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.UnlockOverlayPosition), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.UnlockOverlayTransitionStyle), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.UnlockOverlaySlideDistance), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.OverlayCustomOpacity), StringComparison.Ordinal)
                || string.Equals(propertyName, nameof(Providers.Local.LocalSettings.OverlayCustomScale), StringComparison.Ordinal);
        }

        private void NotificationAutoPopupPreviewTimer_Tick(object sender, EventArgs e)
        {
            _notificationAutoPopupPreviewTimer.Stop();
            ShowCustomPopupPreview(forceStatusMessage: false);
        }

        private void ScheduleAutoPopupPreview()
        {
            _notificationAutoPopupPreviewTimer.Stop();
            _notificationAutoPopupPreviewTimer.Start();
        }

        private void NotificationsOverlayFadeInMillisecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNotificationsOverlayFadeInFromTextBox(updateTextBox: false);
        }

        private void NotificationsOverlayFadeInMillisecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyNotificationsOverlayFadeInFromTextBox(updateTextBox: true);
        }

        private void ApplyNotificationsOverlayFadeInFromTextBox(bool updateTextBox)
        {
            if (NotificationsOverlayFadeInMillisecondsTextBox == null)
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            if (int.TryParse(NotificationsOverlayFadeInMillisecondsTextBox.Text, out var value))
            {
                localSettings.UnlockOverlayFadeInMilliseconds = value;
            }

            if (updateTextBox)
            {
                NotificationsOverlayFadeInMillisecondsTextBox.Text = localSettings.UnlockOverlayFadeInMilliseconds.ToString();
            }
        }

        private void NotificationsOverlayFadeOutMillisecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNotificationsOverlayFadeOutFromTextBox(updateTextBox: false);
        }

        private void NotificationsOverlayFadeOutMillisecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyNotificationsOverlayFadeOutFromTextBox(updateTextBox: true);
        }

        private void ApplyNotificationsOverlayFadeOutFromTextBox(bool updateTextBox)
        {
            if (NotificationsOverlayFadeOutMillisecondsTextBox == null)
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            if (int.TryParse(NotificationsOverlayFadeOutMillisecondsTextBox.Text, out var value))
            {
                localSettings.UnlockOverlayFadeOutMilliseconds = value;
            }

            if (updateTextBox)
            {
                NotificationsOverlayFadeOutMillisecondsTextBox.Text = localSettings.UnlockOverlayFadeOutMilliseconds.ToString();
            }
        }

        private void NotificationsUnlockSoundLeadMillisecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNotificationsUnlockSoundLeadFromTextBox(updateTextBox: false);
        }

        private void NotificationsUnlockSoundLeadMillisecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyNotificationsUnlockSoundLeadFromTextBox(updateTextBox: true);
        }

        private void ApplyNotificationsUnlockSoundLeadFromTextBox(bool updateTextBox)
        {
            if (NotificationsUnlockSoundLeadMillisecondsTextBox == null)
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            if (int.TryParse(NotificationsUnlockSoundLeadMillisecondsTextBox.Text, out var value))
            {
                localSettings.UnlockSoundLeadMilliseconds = value;
            }

            if (updateTextBox)
            {
                NotificationsUnlockSoundLeadMillisecondsTextBox.Text = localSettings.UnlockSoundLeadMilliseconds.ToString();
            }
        }

        private void NotificationsPollingIntervalSecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNotificationsPollingIntervalFromTextBox(updateTextBox: false);
        }

        private void NotificationsPollingIntervalSecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyNotificationsPollingIntervalFromTextBox(updateTextBox: true);
        }

        private void ApplyNotificationsPollingIntervalFromTextBox(bool updateTextBox)
        {
            if (NotificationsPollingIntervalSecondsTextBox == null)
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            if (int.TryParse(NotificationsPollingIntervalSecondsTextBox.Text, out var interval))
            {
                localSettings.ActiveGameMonitoringIntervalSeconds = interval;
            }

            if (updateTextBox)
            {
                NotificationsPollingIntervalSecondsTextBox.Text = localSettings.ActiveGameMonitoringIntervalSeconds.ToString();
            }
        }

        private void NotificationsScreenshotDelayMillisecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNotificationsScreenshotDelayFromTextBox(updateTextBox: false);
        }

        private void NotificationsScreenshotDelayMillisecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyNotificationsScreenshotDelayFromTextBox(updateTextBox: true);
        }

        private void ApplyNotificationsScreenshotDelayFromTextBox(bool updateTextBox)
        {
            if (NotificationsScreenshotDelayMillisecondsTextBox == null)
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            if (int.TryParse(NotificationsScreenshotDelayMillisecondsTextBox.Text, out var delay))
            {
                localSettings.ScreenshotDelayMilliseconds = delay;
            }

            if (updateTextBox)
            {
                NotificationsScreenshotDelayMillisecondsTextBox.Text = localSettings.ScreenshotDelayMilliseconds.ToString();
            }
        }

        private void NotificationsBundledUnlockSoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingNotificationSoundSelection)
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null || NotificationsBundledUnlockSoundComboBox.SelectedItem == null)
                return;

            if (NotificationsBundledUnlockSoundComboBox.SelectedItem is NotificationSoundOption option)
            {
                localSettings.CustomUnlockSoundPath = string.Empty;
                localSettings.BundledUnlockSoundPath = option.SoundPath ?? string.Empty;
            }

            UpdateNotificationSoundButtons();
        }

        private void NotificationsAddSoundFile_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Wave files|*.wav|All files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            AddNotificationSoundEntries(localSettings, new[] { selectedPath }, autoSelectFirstAdded: true);
        }

        private void NotificationsGlobalStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingNotificationStyleSelection)
            {
                return;
            }

            if (!(NotificationsGlobalStyleComboBox?.SelectedItem is NotificationStyleOption styleOption))
            {
                return;
            }

            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.DefaultUnlockNotificationStyle = styleOption.StyleKey;
            SyncSelectedProviderStyleSelection();
        }

        private void NotificationsProviderStyleProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingNotificationStyleSelection)
            {
                return;
            }

            SyncSelectedProviderStyleSelection();
        }

        private void NotificationsSaveProviderStyleOverride_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            if (!(NotificationsProviderStyleProviderComboBox?.SelectedItem is NotificationProviderOption providerOption) ||
                !(NotificationsProviderStyleComboBox?.SelectedItem is NotificationStyleOption styleOption))
            {
                return;
            }

            var updated = persisted.ProviderUnlockNotificationStyles != null
                ? new Dictionary<string, string>(persisted.ProviderUnlockNotificationStyles, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            updated[providerOption.ProviderKey] = styleOption.StyleKey;
            persisted.ProviderUnlockNotificationStyles = updated;

            NotificationsUnlockSoundStatusTextBlock.Text = $"Saved style override for {providerOption.DisplayName}.";
        }

        private void NotificationsClearProviderStyleOverride_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            if (!(NotificationsProviderStyleProviderComboBox?.SelectedItem is NotificationProviderOption providerOption))
            {
                return;
            }

            var updated = persisted.ProviderUnlockNotificationStyles != null
                ? new Dictionary<string, string>(persisted.ProviderUnlockNotificationStyles, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (updated.Remove(providerOption.ProviderKey))
            {
                persisted.ProviderUnlockNotificationStyles = updated;
                NotificationsUnlockSoundStatusTextBlock.Text = $"Cleared style override for {providerOption.DisplayName}.";
            }

            SyncSelectedProviderStyleSelection();
        }

        private void NotificationsPreviewSelectedStyle_Click(object sender, RoutedEventArgs e)
        {
            if (!(NotificationsProviderStyleProviderComboBox?.SelectedItem is NotificationProviderOption providerOption) ||
                !(NotificationsProviderStyleComboBox?.SelectedItem is NotificationStyleOption styleOption))
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var previewGame = GetSelectedNotificationPreviewGame();
            var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;

            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            publisher.SendUnlockPopup(
                previewGameName,
                $"Preview ({styleOption.DisplayName})",
                providerKey: providerOption.ProviderKey,
                forcedStyle: styleOption.StyleKey,
                overrideLocalSettings: localSettings,
                game: previewGame,
                achievementDescription: "Win your first fight without taking damage.",
                achievementPoints: 25,
                achievementRarity: "12.7%",
                achievementTrophy: "Gold");
            NotificationsUnlockSoundStatusTextBlock.Text = $"Previewed {styleOption.DisplayName} style for {providerOption.DisplayName} using {localSettings.UnlockNotificationDeliveryMode}.";
        }

        private void NotificationsPreviewAllStyles_Click(object sender, RoutedEventArgs e)
        {
            if (!(NotificationsProviderStyleProviderComboBox?.SelectedItem is NotificationProviderOption providerOption))
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var previewGame = GetSelectedNotificationPreviewGame();
            var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;

            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            foreach (var style in _notificationStyleOptions)
            {
                publisher.SendUnlockPopup(
                    previewGameName,
                    $"Preview ({style.DisplayName})",
                    providerKey: providerOption.ProviderKey,
                    forcedStyle: style.StyleKey,
                    overrideLocalSettings: localSettings,
                    game: previewGame,
                    achievementDescription: "Win your first fight without taking damage.",
                    achievementPoints: 25,
                    achievementRarity: "12.7%",
                    achievementTrophy: "Gold");
            }

            NotificationsUnlockSoundStatusTextBlock.Text = $"Previewed all styles for {providerOption.DisplayName} using {localSettings.UnlockNotificationDeliveryMode}.";
        }

        private void NotificationsAddSoundFolder_Click(object sender, RoutedEventArgs e)
        {
            var selectedFolder = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedFolder))
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            var folderEntries = Directory.EnumerateFiles(selectedFolder, "*.wav", SearchOption.TopDirectoryOnly)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (folderEntries.Count == 0)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = "No .wav files found in selected folder.";
                return;
            }

            AddNotificationSoundEntries(localSettings, folderEntries, autoSelectFirstAdded: true);
            NotificationsUnlockSoundStatusTextBlock.Text = $"Added {folderEntries.Count} sound(s) from folder.";
        }

        private void NotificationsRemoveSound_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            if (!(NotificationsBundledUnlockSoundComboBox?.SelectedItem is NotificationSoundOption selected))
                return;

            if (selected.IsDefault)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = "Steam default sound cannot be removed.";
                return;
            }

            var updatedPaths = localSettings.GetExtraUnlockSoundPathEntries()
                .Where(path => !string.Equals(path, selected.SoundPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            localSettings.SetExtraUnlockSoundPathEntries(updatedPaths);

            if (string.Equals(localSettings.BundledUnlockSoundPath, selected.SoundPath, StringComparison.OrdinalIgnoreCase))
            {
                localSettings.BundledUnlockSoundPath = DefaultSteamSoundPath;
            }

            RefreshAchievementNotificationControls(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = "Removed selected sound.";
        }

        private void NotificationsScreenshotSaveFolder_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            localSettings.ScreenshotSaveFolder = selectedPath;
        }

        private void NotificationsTestUnlockSoundButton_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            var soundPath = localSettings.UnlockSoundPath?.Trim();
            if (!string.IsNullOrWhiteSpace(soundPath))
            {
                soundPath = Services.NotificationPublisher.ResolveSoundPath(soundPath);
            }

            try
            {
                NotificationsTestUnlockSoundButton.IsEnabled = false;
                NotificationsUnlockSoundStatusTextBlock.Text = "Sending test notification...";

                var previewGame = GetSelectedNotificationPreviewGame();
                var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;
                var previewProviderKey = ResolveCurrentNotificationTestProviderKey();
                var resolvedStyle = ResolveCurrentNotificationTestStyle(previewProviderKey);
                var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
                publisher.ShowLocalAchievementUnlocked(
                    previewGameName,
                    new[] { "Test Achievement" },
                    soundPath,
                    unlockedAchievementIconPath: null,
                    game: previewGame,
                    achievementDescription: "Test description for preview.",
                    achievementPoints: 25,
                    achievementRarity: "Rare",
                    achievementTrophy: "Gold",
                    forcedStyle: resolvedStyle,
                    overrideLocalSettings: localSettings,
                    notificationProviderKey: previewProviderKey);

                NotificationsUnlockSoundStatusTextBlock.Text = $"Test notification sent for {ProviderRegistry.GetLocalizedName(previewProviderKey)} using {localSettings.UnlockNotificationDeliveryMode}.";
                if (NotificationsTestUnlockSoundButton != null)
                    NotificationsTestUnlockSoundButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = $"Failed to send test notification: {ex.Message}";
                if (NotificationsTestUnlockSoundButton != null)
                    NotificationsTestUnlockSoundButton.IsEnabled = true;
            }
        }

        private string ResolveCurrentNotificationTestProviderKey()
        {
            return (NotificationsProviderStyleProviderComboBox?.SelectedItem as NotificationProviderOption)?.ProviderKey ?? "Local";
        }

        private string ResolveCurrentNotificationTestStyle(string providerKey)
        {
            if (NotificationsProviderStyleProviderComboBox?.SelectedItem is NotificationProviderOption providerOption &&
                string.Equals(providerOption.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                NotificationsProviderStyleComboBox?.SelectedItem is NotificationStyleOption selectedProviderStyle)
            {
                return selectedProviderStyle.StyleKey;
            }

            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted?.ProviderUnlockNotificationStyles != null &&
                persisted.ProviderUnlockNotificationStyles.TryGetValue(providerKey, out var providerStyle) &&
                !string.IsNullOrWhiteSpace(providerStyle))
            {
                return providerStyle;
            }

            if (NotificationsGlobalStyleComboBox?.SelectedItem is NotificationStyleOption selectedGlobalStyle)
            {
                return selectedGlobalStyle.StyleKey;
            }

            return persisted?.DefaultUnlockNotificationStyle;
        }

        private void MigrateLegacyCustomSoundPath(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null || string.IsNullOrWhiteSpace(localSettings.CustomUnlockSoundPath))
            {
                return;
            }

            var soundPath = localSettings.CustomUnlockSoundPath.Trim();
            var updatedPaths = localSettings.GetExtraUnlockSoundPathEntries().ToList();
            if (!updatedPaths.Any(path => string.Equals(path, soundPath, StringComparison.OrdinalIgnoreCase)))
            {
                updatedPaths.Add(soundPath);
                localSettings.SetExtraUnlockSoundPathEntries(updatedPaths);
            }

            localSettings.BundledUnlockSoundPath = soundPath;
            localSettings.CustomUnlockSoundPath = string.Empty;
        }

        private List<NotificationSoundOption> BuildNotificationSoundOptions(Providers.Local.LocalSettings localSettings)
        {
            var options = new List<NotificationSoundOption>
            {
                new NotificationSoundOption
                {
                    DisplayName = "Steam (Default)",
                    SoundPath = DefaultSteamSoundPath,
                    IsDefault = true
                }
            };

            foreach (var soundPath in localSettings.GetExtraUnlockSoundPathEntries())
            {
                if (string.Equals(soundPath, DefaultSteamSoundPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                options.Add(new NotificationSoundOption
                {
                    DisplayName = GetSoundDisplayName(soundPath),
                    SoundPath = soundPath,
                    IsDefault = false
                });
            }

            var selectedPath = localSettings.EffectiveBundledUnlockSoundPath;
            if (!string.IsNullOrWhiteSpace(selectedPath) &&
                !options.Any(option => string.Equals(option.SoundPath, selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new NotificationSoundOption
                {
                    DisplayName = GetSoundDisplayName(selectedPath),
                    SoundPath = selectedPath,
                    IsDefault = string.Equals(selectedPath, DefaultSteamSoundPath, StringComparison.OrdinalIgnoreCase)
                });
            }

            return options;
        }

        private static string GetSoundDisplayName(string soundPath)
        {
            if (string.IsNullOrWhiteSpace(soundPath))
            {
                return "Unnamed Sound";
            }

            if (string.Equals(soundPath, DefaultSteamSoundPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Steam (Default)";
            }

            var fileName = Path.GetFileNameWithoutExtension(soundPath);
            return string.IsNullOrWhiteSpace(fileName)
                ? soundPath
                : fileName;
        }

        private void AddNotificationSoundEntries(
            Providers.Local.LocalSettings localSettings,
            IEnumerable<string> soundPaths,
            bool autoSelectFirstAdded)
        {
            if (localSettings == null || soundPaths == null)
            {
                return;
            }

            var updatedPaths = localSettings.GetExtraUnlockSoundPathEntries().ToList();
            string firstAdded = null;
            foreach (var rawPath in soundPaths)
            {
                var soundPath = rawPath?.Trim();
                if (string.IsNullOrWhiteSpace(soundPath))
                {
                    continue;
                }

                if (!updatedPaths.Any(path => string.Equals(path, soundPath, StringComparison.OrdinalIgnoreCase)))
                {
                    updatedPaths.Add(soundPath);
                    if (firstAdded == null)
                    {
                        firstAdded = soundPath;
                    }
                }
            }

            localSettings.SetExtraUnlockSoundPathEntries(updatedPaths);
            if (autoSelectFirstAdded && !string.IsNullOrWhiteSpace(firstAdded))
            {
                localSettings.BundledUnlockSoundPath = firstAdded;
            }

            RefreshAchievementNotificationControls(localSettings);
        }

        private void UpdateNotificationSoundButtons()
        {
            if (NotificationsRemoveSoundButton == null)
            {
                return;
            }

            var selected = NotificationsBundledUnlockSoundComboBox?.SelectedItem as NotificationSoundOption;
            NotificationsRemoveSoundButton.IsEnabled = selected != null && !selected.IsDefault;
        }

        private void RefreshNotificationStyleControls()
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            _isRefreshingNotificationStyleSelection = true;
            try
            {
                if (_notificationStyleOptions.Count == 0)
                {
                    _notificationStyleOptions.Add(new NotificationStyleOption
                    {
                        DisplayName = "Steam",
                        StyleKey = NotificationPublisher.NotificationStyleSteam
                    });
                    _notificationStyleOptions.Add(new NotificationStyleOption
                    {
                        DisplayName = "PlayStation",
                        StyleKey = NotificationPublisher.NotificationStylePlayStation
                    });
                    _notificationStyleOptions.Add(new NotificationStyleOption
                    {
                        DisplayName = "Xbox",
                        StyleKey = NotificationPublisher.NotificationStyleXbox
                    });
                    _notificationStyleOptions.Add(new NotificationStyleOption
                    {
                        DisplayName = "Minimal",
                        StyleKey = NotificationPublisher.NotificationStyleMinimal
                    });
                    _notificationStyleOptions.Add(new NotificationStyleOption
                    {
                        DisplayName = "Custom",
                        StyleKey = NotificationPublisher.NotificationStyleCustom
                    });
                }

                if (_notificationProviderOptions.Count == 0)
                {
                    _notificationProviderOptions.Add(new NotificationProviderOption { DisplayName = "Local", ProviderKey = "Local" });
                    _notificationProviderOptions.Add(new NotificationProviderOption { DisplayName = "Steam", ProviderKey = "Steam" });
                    _notificationProviderOptions.Add(new NotificationProviderOption { DisplayName = "Epic", ProviderKey = "Epic" });
                    _notificationProviderOptions.Add(new NotificationProviderOption { DisplayName = "GOG", ProviderKey = "GOG" });
                    _notificationProviderOptions.Add(new NotificationProviderOption { DisplayName = "PlayStation", ProviderKey = "PSN" });
                    _notificationProviderOptions.Add(new NotificationProviderOption { DisplayName = "Xbox", ProviderKey = "Xbox" });
                    _notificationProviderOptions.Add(new NotificationProviderOption { DisplayName = "RetroAchievements", ProviderKey = "RetroAchievements" });
                }

                if (NotificationsGlobalStyleComboBox != null)
                {
                    NotificationsGlobalStyleComboBox.ItemsSource = _notificationStyleOptions;
                    var selectedGlobal = _notificationStyleOptions.FirstOrDefault(option =>
                        string.Equals(option.StyleKey, persisted.DefaultUnlockNotificationStyle, StringComparison.OrdinalIgnoreCase))
                        ?? _notificationStyleOptions.FirstOrDefault(option =>
                            string.Equals(option.StyleKey, NotificationPublisher.NotificationStyleSteam, StringComparison.OrdinalIgnoreCase));
                    NotificationsGlobalStyleComboBox.SelectedItem = selectedGlobal;
                }

                if (NotificationsProviderStyleProviderComboBox != null)
                {
                    NotificationsProviderStyleProviderComboBox.ItemsSource = _notificationProviderOptions;
                    if (NotificationsProviderStyleProviderComboBox.SelectedItem == null)
                    {
                        NotificationsProviderStyleProviderComboBox.SelectedItem = _notificationProviderOptions.FirstOrDefault();
                    }
                }

                if (NotificationsProviderStyleComboBox != null)
                {
                    NotificationsProviderStyleComboBox.ItemsSource = _notificationStyleOptions;
                }

                RefreshNotificationPreviewGameOptions();

                if (NotificationsOverlayPresetStyleComboBox != null)
                {
                    NotificationsOverlayPresetStyleComboBox.ItemsSource = _notificationStyleOptions;
                    if (NotificationsOverlayPresetStyleComboBox.SelectedItem == null)
                    {
                        NotificationsOverlayPresetStyleComboBox.SelectedItem = _notificationStyleOptions.FirstOrDefault(option =>
                            string.Equals(option.StyleKey, persisted.DefaultUnlockNotificationStyle, StringComparison.OrdinalIgnoreCase))
                            ?? _notificationStyleOptions.FirstOrDefault();
                    }
                }

                SyncSelectedProviderStyleSelection();
                SyncOverlayPresetControlsFromSelectedStyle();
            }
            finally
            {
                _isRefreshingNotificationStyleSelection = false;
            }
        }

        private void RefreshNotificationPreviewGameOptions()
        {
            if (NotificationsPreviewGameComboBox == null)
            {
                return;
            }

            var selectedGameId = (NotificationsPreviewGameComboBox.SelectedItem as NotificationPreviewGameOption)?.Game?.Id;
            var games = _plugin?.PlayniteApi?.Database?.Games?
                .OrderBy(game => game.Name)
                .ToList() ?? new List<Game>();

            _notificationPreviewGameOptions.Clear();
            _notificationPreviewGameOptions.Add(new NotificationPreviewGameOption
            {
                DisplayName = "Current Game / no Playnite art",
                Game = null
            });

            foreach (var game in games)
            {
                var displayName = string.IsNullOrWhiteSpace(game?.Name)
                    ? "<Unnamed game>"
                    : game.Name.Trim();

                _notificationPreviewGameOptions.Add(new NotificationPreviewGameOption
                {
                    DisplayName = displayName,
                    Game = game
                });
            }

            NotificationsPreviewGameComboBox.ItemsSource = _notificationPreviewGameOptions;
            NotificationsPreviewGameComboBox.SelectedItem = _notificationPreviewGameOptions.FirstOrDefault(option => option.Game?.Id == selectedGameId)
                ?? _notificationPreviewGameOptions.FirstOrDefault();
        }

        private Game GetSelectedNotificationPreviewGame()
        {
            return (NotificationsPreviewGameComboBox?.SelectedItem as NotificationPreviewGameOption)?.Game;
        }

        private void NotificationsPreviewGameComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings != null)
            {
                UpdateCustomStyleInlinePreview(localSettings);
            }

            if (NotificationsAutoPopupPreviewCheckBox?.IsChecked == true)
            {
                ScheduleAutoPopupPreview();
            }
        }

        private void SyncSelectedProviderStyleSelection()
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null ||
                NotificationsProviderStyleComboBox == null ||
                !(NotificationsProviderStyleProviderComboBox?.SelectedItem is NotificationProviderOption providerOption))
            {
                return;
            }

            _isRefreshingNotificationStyleSelection = true;
            try
            {
                string style = null;
                if (persisted.ProviderUnlockNotificationStyles != null)
                {
                    persisted.ProviderUnlockNotificationStyles.TryGetValue(providerOption.ProviderKey, out style);
                }

                if (string.IsNullOrWhiteSpace(style))
                {
                    style = persisted.DefaultUnlockNotificationStyle;
                }

                var selectedStyle = _notificationStyleOptions.FirstOrDefault(option =>
                    string.Equals(option.StyleKey, style, StringComparison.OrdinalIgnoreCase))
                    ?? _notificationStyleOptions.FirstOrDefault();
                NotificationsProviderStyleComboBox.SelectedItem = selectedStyle;
            }
            finally
            {
                _isRefreshingNotificationStyleSelection = false;
            }
        }

        private void RefreshOverlayRuntimeControls(Providers.Local.LocalSettings localSettings)
        {
            // Slider value and text block are now bound via TwoWay XAML binding.
            // This method remains for any future runtime-only overlay controls.
        }

        private void NotificationsOverlayDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Text block is now bound via XAML StringFormat binding; nothing to do here.
        }

        private void NotificationsOverlayPresetStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingOverlayPresetControls)
            {
                return;
            }

            SyncOverlayPresetControlsFromSelectedStyle();
        }

        private void NotificationsOverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isRefreshingOverlayPresetControls)
            {
                return;
            }

            if (!(NotificationsOverlayPresetStyleComboBox?.SelectedItem is NotificationStyleOption styleOption) ||
                NotificationsOverlayOpacitySlider == null)
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var value = Math.Round(NotificationsOverlayOpacitySlider.Value, 2);
            ApplyOverlayOpacityForStyle(localSettings, styleOption.StyleKey, value);
            if (NotificationsOverlayOpacityValueTextBlock != null)
            {
                NotificationsOverlayOpacityValueTextBlock.Text = value.ToString("0.00");
            }
        }

        private void NotificationsOverlayScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isRefreshingOverlayPresetControls)
            {
                return;
            }

            if (!(NotificationsOverlayPresetStyleComboBox?.SelectedItem is NotificationStyleOption styleOption) ||
                NotificationsOverlayScaleSlider == null)
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var value = Math.Round(NotificationsOverlayScaleSlider.Value, 2);
            ApplyOverlayScaleForStyle(localSettings, styleOption.StyleKey, value);
            if (NotificationsOverlayScaleValueTextBlock != null)
            {
                NotificationsOverlayScaleValueTextBlock.Text = value.ToString("0.00") + "x";
            }
        }

        private void SyncOverlayPresetControlsFromSelectedStyle()
        {
            if (!(NotificationsOverlayPresetStyleComboBox?.SelectedItem is NotificationStyleOption styleOption))
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            _isRefreshingOverlayPresetControls = true;
            try
            {
                var opacity = GetOverlayOpacityForStyle(localSettings, styleOption.StyleKey);
                var scale = GetOverlayScaleForStyle(localSettings, styleOption.StyleKey);

                if (NotificationsOverlayOpacitySlider != null)
                {
                    NotificationsOverlayOpacitySlider.Value = opacity;
                }

                if (NotificationsOverlayScaleSlider != null)
                {
                    NotificationsOverlayScaleSlider.Value = scale;
                }

                if (NotificationsOverlayOpacityValueTextBlock != null)
                {
                    NotificationsOverlayOpacityValueTextBlock.Text = opacity.ToString("0.00");
                }

                if (NotificationsOverlayScaleValueTextBlock != null)
                {
                    NotificationsOverlayScaleValueTextBlock.Text = scale.ToString("0.00") + "x";
                }
            }
            finally
            {
                _isRefreshingOverlayPresetControls = false;
            }
        }

        private static double GetOverlayOpacityForStyle(Providers.Local.LocalSettings settings, string styleKey)
        {
            if (string.Equals(styleKey, NotificationPublisher.NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayPlayStationOpacity;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayXboxOpacity;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayMinimalOpacity;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayCustomOpacity;
            }

            return settings.OverlaySteamOpacity;
        }

        private static double GetOverlayScaleForStyle(Providers.Local.LocalSettings settings, string styleKey)
        {
            if (string.Equals(styleKey, NotificationPublisher.NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayPlayStationScale;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayXboxScale;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayMinimalScale;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                return settings.OverlayCustomScale;
            }

            return settings.OverlaySteamScale;
        }

        private static void ApplyOverlayOpacityForStyle(Providers.Local.LocalSettings settings, string styleKey, double value)
        {
            if (string.Equals(styleKey, NotificationPublisher.NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayPlayStationOpacity = value;
                return;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayXboxOpacity = value;
                return;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayMinimalOpacity = value;
                return;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayCustomOpacity = value;
                return;
            }

            settings.OverlaySteamOpacity = value;
        }

        private static void ApplyOverlayScaleForStyle(Providers.Local.LocalSettings settings, string styleKey, double value)
        {
            if (string.Equals(styleKey, NotificationPublisher.NotificationStylePlayStation, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayPlayStationScale = value;
                return;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleXbox, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayXboxScale = value;
                return;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleMinimal, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayMinimalScale = value;
                return;
            }

            if (string.Equals(styleKey, NotificationPublisher.NotificationStyleCustom, StringComparison.OrdinalIgnoreCase))
            {
                settings.OverlayCustomScale = value;
                return;
            }

            settings.OverlaySteamScale = value;
        }

        private void NotificationsCustomBackgroundImageBrowse_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            localSettings.OverlayCustomBackgroundImagePath = selectedPath;
            NotificationsUnlockSoundStatusTextBlock.Text = "Custom background image selected.";
        }

        private void NotificationsCustomBackgroundImageClear_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            localSettings.OverlayCustomBackgroundImagePath = string.Empty;
            NotificationsUnlockSoundStatusTextBlock.Text = "Custom background image cleared.";
        }

        private void NotificationsCustomCoverImageBrowse_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            localSettings.OverlayCustomCoverImagePath = selectedPath;
            NotificationsUnlockSoundStatusTextBlock.Text = "Custom cover image selected.";
        }

        private void NotificationsCustomCoverImageClear_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            localSettings.OverlayCustomCoverImagePath = string.Empty;
            NotificationsUnlockSoundStatusTextBlock.Text = "Custom cover image cleared.";
        }

        private void NotificationsCustomBannerImageBrowse_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            localSettings.OverlayCustomBannerImagePath = selectedPath;
            NotificationsUnlockSoundStatusTextBlock.Text = "Custom banner image selected.";
        }

        private void NotificationsCustomBannerImageClear_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            localSettings.OverlayCustomBannerImagePath = string.Empty;
            NotificationsUnlockSoundStatusTextBlock.Text = "Custom banner image cleared.";
        }

        private void NotificationsShowCustomPopupPreview_Click(object sender, RoutedEventArgs e)
        {
            ShowCustomPopupPreview(forceStatusMessage: true);
        }

        private void ShowCustomPopupPreview(bool forceStatusMessage)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var previewGame = GetSelectedNotificationPreviewGame();
            var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;

            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            publisher.SendUnlockPopup(
                previewGameName,
                "Sample Achievement",
                providerKey: "Local",
                forcedStyle: NotificationPublisher.NotificationStyleCustom,
                forcedDeliveryMode: LocalUnlockNotificationDeliveryMode.Overlay,
                overrideLocalSettings: localSettings,
                game: previewGame,
                togglePersistentOverlay: forceStatusMessage,
                refreshPersistentOverlay: !forceStatusMessage,
                achievementDescription: "Win your first fight without taking damage.",
                achievementPoints: 25,
                achievementRarity: "12.7%",
                achievementTrophy: "Gold");

            if (forceStatusMessage)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = string.IsNullOrWhiteSpace(previewGame?.Name)
                    ? "Toggled silent Custom overlay preview."
                    : $"Toggled silent Custom overlay preview for {previewGame.Name}.";
            }
        }

        private void RefreshCustomStyleSlotControls(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null || NotificationsCustomStyleSlotComboBox == null)
                return;

            _isRefreshingCustomStyleSlotSelection = true;
            try
            {
                var slots = EnsureCustomStyleSlots(localSettings);
                _customStyleSlotOptions.Clear();
                for (var index = 0; index < slots.Count; index++)
                {
                    var slot = slots[index];
                    var name = string.IsNullOrWhiteSpace(slot?.Name) ? $"Slot {index + 1}" : slot.Name.Trim();
                    _customStyleSlotOptions.Add(new CustomStyleSlotOption
                    {
                        SlotNumber = index + 1,
                        DisplayName = name
                    });
                }

                NotificationsCustomStyleSlotComboBox.ItemsSource = _customStyleSlotOptions;
                var selectedOption = _customStyleSlotOptions
                    .FirstOrDefault(o => o.SlotNumber == localSettings.SelectedCustomStyleSlot)
                    ?? _customStyleSlotOptions.FirstOrDefault();
                NotificationsCustomStyleSlotComboBox.SelectedItem = selectedOption;

                // Update the editable text to match selected slot name
                if (selectedOption != null)
                    NotificationsCustomStyleSlotComboBox.Text = selectedOption.DisplayName;
            }
            finally
            {
                _isRefreshingCustomStyleSlotSelection = false;
            }
        }

        private static List<LocalCustomOverlayStyleSlot> EnsureCustomStyleSlots(Providers.Local.LocalSettings localSettings)
        {
            var slots = localSettings.CustomOverlayStyleSlots;
            if (slots == null || slots.Count == 0)
            {
                localSettings.CustomOverlayStyleSlots = new List<LocalCustomOverlayStyleSlot>
                {
                    new LocalCustomOverlayStyleSlot { Name = "Slot 1" }
                };
            }
            return localSettings.CustomOverlayStyleSlots;
        }

        private void NotificationsCustomStyleSlotComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingCustomStyleSlotSelection)
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null || !(NotificationsCustomStyleSlotComboBox?.SelectedItem is CustomStyleSlotOption selectedSlot))
                return;

            localSettings.SelectedCustomStyleSlot = selectedSlot.SlotNumber;
            NotificationsCustomStyleSlotComboBox.Text = selectedSlot.DisplayName;
        }

        private void NotificationsCustomStyleSlotComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isRefreshingCustomStyleSlotSelection)
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            var slots = EnsureCustomStyleSlots(localSettings);
            var slotIndex = Math.Max(0, Math.Min(slots.Count - 1, localSettings.SelectedCustomStyleSlot - 1));
            var newName = NotificationsCustomStyleSlotComboBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(newName))
                newName = $"Slot {slotIndex + 1}";
            slots[slotIndex].Name = newName;
            localSettings.CustomOverlayStyleSlots = slots;
            RefreshCustomStyleSlotControls(localSettings);
        }

        private void NotificationsAddCustomStyleSlot_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            var slots = EnsureCustomStyleSlots(localSettings);
            var newSlot = new LocalCustomOverlayStyleSlot { Name = $"Slot {slots.Count + 1}" };
            slots.Add(newSlot);
            localSettings.CustomOverlayStyleSlots = slots;
            localSettings.SelectedCustomStyleSlot = slots.Count;
            RefreshCustomStyleSlotControls(localSettings);
        }

        private void NotificationsRemoveCustomStyleSlot_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            var slots = EnsureCustomStyleSlots(localSettings);
            if (slots.Count <= 1)
                return; // Cannot remove the last slot

            var removeIndex = Math.Max(0, Math.Min(slots.Count - 1, localSettings.SelectedCustomStyleSlot - 1));
            slots.RemoveAt(removeIndex);
            localSettings.CustomOverlayStyleSlots = slots;
            localSettings.SelectedCustomStyleSlot = Math.Max(1, Math.Min(slots.Count, localSettings.SelectedCustomStyleSlot));
            RefreshCustomStyleSlotControls(localSettings);
        }

        private void NotificationsSaveCustomStyleSlot_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var slots = EnsureCustomStyleSlots(localSettings);

            var slotIndex = Math.Max(0, Math.Min(slots.Count - 1, localSettings.SelectedCustomStyleSlot - 1));
            var currentName = NotificationsCustomStyleSlotComboBox?.Text?.Trim();
            slots[slotIndex] = new LocalCustomOverlayStyleSlot
            {
                Name = string.IsNullOrWhiteSpace(currentName)
                    ? (string.IsNullOrWhiteSpace(slots[slotIndex]?.Name) ? $"Slot {slotIndex + 1}" : slots[slotIndex].Name)
                    : currentName,
                AutoResizeToContent = localSettings.OverlayCustomAutoResizeToContent,
                WrapAllText = localSettings.OverlayCustomWrapAllText,
                ShowLine1 = localSettings.OverlayCustomShowLine1,
                ShowBorder = localSettings.OverlayCustomShowBorder,
                ShowGameName = localSettings.OverlayCustomShowGameName,
                ShowMeta = localSettings.OverlayCustomShowMeta,
                IconSize = localSettings.OverlayCustomIconSize,
                Width = localSettings.OverlayCustomWidth,
                Height = localSettings.OverlayCustomHeight,
                CornerRadius = localSettings.OverlayCustomCornerRadius,
                TitleFontSize = localSettings.OverlayCustomTitleFontSize,
                DetailFontSize = localSettings.OverlayCustomDetailFontSize,
                MetaFontSize = localSettings.OverlayCustomMetaFontSize,
                BackgroundColor = localSettings.OverlayCustomBackgroundColor,
                BorderColor = localSettings.OverlayCustomBorderColor,
                AccentColor = localSettings.OverlayCustomAccentColor,
                TitleColor = localSettings.OverlayCustomTitleColor,
                DetailColor = localSettings.OverlayCustomDetailColor,
                MetaColor = localSettings.OverlayCustomMetaColor,
                BackgroundImagePath = localSettings.OverlayCustomBackgroundImagePath,
                TitleTemplate = localSettings.OverlayCustomTitleTemplate,
                GameNameTemplate = localSettings.OverlayCustomGameNameTemplate,
                AchievementTemplate = localSettings.OverlayCustomAchievementTemplate,
                MetaTemplate = localSettings.OverlayCustomMetaTemplate,
                TitleBold = localSettings.OverlayCustomTitleBold,
                TitleItalic = localSettings.OverlayCustomTitleItalic,
                TitleUnderline = localSettings.OverlayCustomTitleUnderline,
                TitleStrikethrough = localSettings.OverlayCustomTitleStrikethrough,
                DetailBold = localSettings.OverlayCustomDetailBold,
                DetailItalic = localSettings.OverlayCustomDetailItalic,
                DetailUnderline = localSettings.OverlayCustomDetailUnderline,
                DetailStrikethrough = localSettings.OverlayCustomDetailStrikethrough,
                MetaBold = localSettings.OverlayCustomMetaBold,
                MetaItalic = localSettings.OverlayCustomMetaItalic,
                MetaUnderline = localSettings.OverlayCustomMetaUnderline,
                MetaStrikethrough = localSettings.OverlayCustomMetaStrikethrough,
                IconSource = localSettings.OverlayCustomIconSource,
                ShowIconRarityGlow = localSettings.OverlayCustomShowIconRarityGlow,
                CustomCoverImagePath = localSettings.OverlayCustomCoverImagePath,
                CustomBannerImagePath = localSettings.OverlayCustomBannerImagePath
            };

            localSettings.CustomOverlayStyleSlots = slots;
            RefreshCustomStyleSlotControls(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = $"Saved current custom style to Slot {slotIndex + 1}.";
        }

        private void NotificationsExportCustomStyleTemplate_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            try
            {
                var targetPath = _plugin?.PlayniteApi?.Dialogs?.SaveFile("JSON|*.json");
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    return;
                }

                if (!string.Equals(Path.GetExtension(targetPath), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    targetPath += ".json";
                }

                var slots = EnsureCustomStyleSlots(localSettings);
                var slotIndex = Math.Max(0, Math.Min(slots.Count - 1, localSettings.SelectedCustomStyleSlot - 1));
                var slotName = NotificationsCustomStyleSlotComboBox?.Text?.Trim();

                var template = new NotificationOverlayTemplateFile
                {
                    SchemaVersion = 1,
                    TemplateName = string.IsNullOrWhiteSpace(slotName) ? $"Slot {slotIndex + 1}" : slotName,
                    ExportedAtUtc = DateTime.UtcNow,
                    CustomStyleSlot = new LocalCustomOverlayStyleSlot
                    {
                        Name = string.IsNullOrWhiteSpace(slotName)
                            ? (string.IsNullOrWhiteSpace(slots[slotIndex]?.Name) ? $"Slot {slotIndex + 1}" : slots[slotIndex].Name)
                            : slotName,
                        AutoResizeToContent = localSettings.OverlayCustomAutoResizeToContent,
                        WrapAllText = localSettings.OverlayCustomWrapAllText,
                        ShowLine1 = localSettings.OverlayCustomShowLine1,
                        ShowBorder = localSettings.OverlayCustomShowBorder,
                        ShowGameName = localSettings.OverlayCustomShowGameName,
                        ShowMeta = localSettings.OverlayCustomShowMeta,
                        IconSize = localSettings.OverlayCustomIconSize,
                        Width = localSettings.OverlayCustomWidth,
                        Height = localSettings.OverlayCustomHeight,
                        CornerRadius = localSettings.OverlayCustomCornerRadius,
                        TitleFontSize = localSettings.OverlayCustomTitleFontSize,
                        DetailFontSize = localSettings.OverlayCustomDetailFontSize,
                        MetaFontSize = localSettings.OverlayCustomMetaFontSize,
                        BackgroundColor = localSettings.OverlayCustomBackgroundColor,
                        BorderColor = localSettings.OverlayCustomBorderColor,
                        AccentColor = localSettings.OverlayCustomAccentColor,
                        TitleColor = localSettings.OverlayCustomTitleColor,
                        DetailColor = localSettings.OverlayCustomDetailColor,
                        MetaColor = localSettings.OverlayCustomMetaColor,
                        BackgroundImagePath = localSettings.OverlayCustomBackgroundImagePath,
                        TitleTemplate = localSettings.OverlayCustomTitleTemplate,
                        GameNameTemplate = localSettings.OverlayCustomGameNameTemplate,
                        AchievementTemplate = localSettings.OverlayCustomAchievementTemplate,
                        MetaTemplate = localSettings.OverlayCustomMetaTemplate,
                        TitleBold = localSettings.OverlayCustomTitleBold,
                        TitleItalic = localSettings.OverlayCustomTitleItalic,
                        TitleUnderline = localSettings.OverlayCustomTitleUnderline,
                        TitleStrikethrough = localSettings.OverlayCustomTitleStrikethrough,
                        DetailBold = localSettings.OverlayCustomDetailBold,
                        DetailItalic = localSettings.OverlayCustomDetailItalic,
                        DetailUnderline = localSettings.OverlayCustomDetailUnderline,
                        DetailStrikethrough = localSettings.OverlayCustomDetailStrikethrough,
                        MetaBold = localSettings.OverlayCustomMetaBold,
                        MetaItalic = localSettings.OverlayCustomMetaItalic,
                        MetaUnderline = localSettings.OverlayCustomMetaUnderline,
                        MetaStrikethrough = localSettings.OverlayCustomMetaStrikethrough,
                        IconSource = localSettings.OverlayCustomIconSource,
                        ShowIconRarityGlow = localSettings.OverlayCustomShowIconRarityGlow,
                        CustomCoverImagePath = localSettings.OverlayCustomCoverImagePath,
                        CustomBannerImagePath = localSettings.OverlayCustomBannerImagePath
                    },
                    Transition = new NotificationOverlayTransitionTemplate
                    {
                        DurationMilliseconds = localSettings.UnlockOverlayDurationMilliseconds,
                        FadeInMilliseconds = localSettings.UnlockOverlayFadeInMilliseconds,
                        FadeOutMilliseconds = localSettings.UnlockOverlayFadeOutMilliseconds,
                        SoundLeadMilliseconds = localSettings.UnlockSoundLeadMilliseconds,
                        OverlayPosition = localSettings.UnlockOverlayPosition.ToString(),
                        TransitionStyle = localSettings.UnlockOverlayTransitionStyle.ToString(),
                        SlideDistance = localSettings.UnlockOverlaySlideDistance,
                        IconSource = localSettings.OverlayCustomIconSource.ToString(),
                        EnableGameCoverInOverlay = localSettings.EnableGameCoverInOverlay,
                        GameCoverPosition = localSettings.GameCoverPosition.ToString(),
                        GameCoverWidth = localSettings.GameCoverWidth,
                        EnableGameBannerAsBackground = localSettings.EnableGameBannerAsBackground,
                        GameBannerOpacity = localSettings.GameBannerOpacity,
                        GameBannerBlurRadius = localSettings.GameBannerBlurRadius,
                        ShowIconRarityGlow = localSettings.OverlayCustomShowIconRarityGlow,
                        CustomCoverImagePath = localSettings.OverlayCustomCoverImagePath,
                        CustomBannerImagePath = localSettings.OverlayCustomBannerImagePath
                    }
                };

                var json = JsonConvert.SerializeObject(template, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(targetPath, json);
                NotificationsUnlockSoundStatusTextBlock.Text = $"Exported custom template to: {targetPath}";
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to export notification custom style template.");
                NotificationsUnlockSoundStatusTextBlock.Text = "Failed to export custom template.";
            }
        }

        private void NotificationsImportCustomStyleTemplate_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            try
            {
                var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("JSON|*.json|All files|*.*");
                if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath))
                {
                    return;
                }

                var rawJson = File.ReadAllText(selectedPath);
                var imported = JsonConvert.DeserializeObject<NotificationOverlayTemplateFile>(rawJson);
                if (imported?.CustomStyleSlot == null)
                {
                    NotificationsUnlockSoundStatusTextBlock.Text = "Invalid template file: missing custom style data.";
                    return;
                }

                var slots = EnsureCustomStyleSlots(localSettings);
                var slotIndex = Math.Max(0, Math.Min(slots.Count - 1, localSettings.SelectedCustomStyleSlot - 1));
                var importedSlot = imported.CustomStyleSlot;
                importedSlot.Name = string.IsNullOrWhiteSpace(importedSlot.Name)
                    ? (string.IsNullOrWhiteSpace(imported.TemplateName) ? $"Slot {slotIndex + 1}" : imported.TemplateName.Trim())
                    : importedSlot.Name.Trim();

                slots[slotIndex] = importedSlot;
                localSettings.CustomOverlayStyleSlots = slots;

                ApplyCustomStyleSlotToEditor(localSettings, slots[slotIndex]);

                if (imported.Transition != null)
                {
                    localSettings.UnlockOverlayDurationMilliseconds = imported.Transition.DurationMilliseconds;
                    localSettings.UnlockOverlayFadeInMilliseconds = imported.Transition.FadeInMilliseconds;
                    localSettings.UnlockOverlayFadeOutMilliseconds = imported.Transition.FadeOutMilliseconds;
                    localSettings.UnlockSoundLeadMilliseconds = imported.Transition.SoundLeadMilliseconds;
                    localSettings.UnlockOverlaySlideDistance = imported.Transition.SlideDistance;

                    if (!string.IsNullOrWhiteSpace(imported.Transition.OverlayPosition) &&
                        Enum.TryParse(imported.Transition.OverlayPosition, true, out LocalUnlockOverlayPosition parsedPosition))
                    {
                        localSettings.UnlockOverlayPosition = parsedPosition;
                    }

                    if (!string.IsNullOrWhiteSpace(imported.Transition.TransitionStyle) &&
                        Enum.TryParse(imported.Transition.TransitionStyle, true, out LocalUnlockOverlayTransitionStyle parsedTransitionStyle))
                    {
                        localSettings.UnlockOverlayTransitionStyle = parsedTransitionStyle;
                    }

                    if (!string.IsNullOrWhiteSpace(imported.Transition.IconSource) &&
                        Enum.TryParse(imported.Transition.IconSource, true, out LocalOverlayIconSource parsedIconSource))
                    {
                        localSettings.OverlayCustomIconSource = parsedIconSource;
                    }

                    localSettings.OverlayCustomShowIconRarityGlow = imported.Transition.ShowIconRarityGlow;
                    localSettings.OverlayCustomCoverImagePath = imported.Transition.CustomCoverImagePath ?? string.Empty;
                    localSettings.OverlayCustomBannerImagePath = imported.Transition.CustomBannerImagePath ?? string.Empty;

                    localSettings.EnableGameCoverInOverlay = imported.Transition.EnableGameCoverInOverlay;
                    localSettings.EnableGameBannerAsBackground = imported.Transition.EnableGameBannerAsBackground;
                    localSettings.GameCoverWidth = imported.Transition.GameCoverWidth;
                    localSettings.GameBannerOpacity = imported.Transition.GameBannerOpacity;
                    localSettings.GameBannerBlurRadius = imported.Transition.GameBannerBlurRadius;

                    if (!string.IsNullOrWhiteSpace(imported.Transition.GameCoverPosition) &&
                        Enum.TryParse(imported.Transition.GameCoverPosition, true, out LocalOverlayCoverPosition parsedCoverPosition))
                    {
                        localSettings.GameCoverPosition = parsedCoverPosition;
                    }
                }

                var persisted = _settingsViewModel?.Settings?.Persisted;
                if (persisted != null)
                {
                    var updated = persisted.ProviderUnlockNotificationStyles != null
                        ? new Dictionary<string, string>(persisted.ProviderUnlockNotificationStyles, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    updated["Local"] = NotificationPublisher.NotificationStyleCustom;
                    persisted.ProviderUnlockNotificationStyles = updated;
                }

                RefreshCustomStyleSlotControls(localSettings);
                RefreshNotificationStyleControls();
                UpdateCustomStyleInlinePreview(localSettings);
                NotificationsUnlockSoundStatusTextBlock.Text = $"Imported custom template into Slot {slotIndex + 1} and set Local style to Custom.";
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to import notification custom style template.");
                NotificationsUnlockSoundStatusTextBlock.Text = "Failed to import custom template.";
            }
        }

        private void NotificationsOpenTemplateBuilder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var builderPath = ResolveNotificationTemplateBuilderPath();
                if (string.IsNullOrWhiteSpace(builderPath) || !File.Exists(builderPath))
                {
                    NotificationsUnlockSoundStatusTextBlock.Text = "Template builder file was not found.";
                    _plugin?.PlayniteApi?.Dialogs?.ShowMessage(
                        "The bundled notification template builder could not be found. Rebuild/reinstall the extension so Resources\\Tools\\notification-template-builder.html is included.",
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = builderPath,
                    UseShellExecute = true,
                    Verb = "open"
                });

                NotificationsUnlockSoundStatusTextBlock.Text = "Opened notification template builder.";
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to open notification template builder.");
                NotificationsUnlockSoundStatusTextBlock.Text = "Failed to open notification template builder.";
                _plugin?.PlayniteApi?.Dialogs?.ShowMessage(
                    $"Failed to open notification template builder: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string ResolveNotificationTemplateBuilderPath()
        {
            const string relativeBuilderPath = @"Resources\Tools\notification-template-builder.html";
            var assemblyDir = Path.GetDirectoryName(typeof(SettingsControl).Assembly.Location) ?? string.Empty;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;

            var candidates = new[]
            {
                Path.Combine(assemblyDir, relativeBuilderPath),
                Path.Combine(baseDir, relativeBuilderPath),
                Path.GetFullPath(Path.Combine(assemblyDir, @"..\..\..\tools\notification-template-builder.html")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\tools\notification-template-builder.html")),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"tools\notification-template-builder.html"))
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private void NotificationsLoadCustomStyleSlot_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var slots = localSettings.CustomOverlayStyleSlots;
            if (slots == null || slots.Count == 0)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = "No custom style slots saved yet.";
                return;
            }

            var slotIndex = Math.Max(0, Math.Min(slots.Count - 1, localSettings.SelectedCustomStyleSlot - 1));
            var slot = slots[slotIndex];
            if (slot == null)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = $"Slot {slotIndex + 1} is empty.";
                return;
            }

            ApplyCustomStyleSlotToEditor(localSettings, slot);

            RefreshCustomStyleSlotControls(localSettings);
            UpdateCustomStyleInlinePreview(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = $"Loaded custom style from Slot {slotIndex + 1}.";
        }

        private static void ApplyCustomStyleSlotToEditor(Providers.Local.LocalSettings localSettings, LocalCustomOverlayStyleSlot slot)
        {
            if (localSettings == null || slot == null)
            {
                return;
            }

            localSettings.OverlayCustomWidth = slot.Width;
            localSettings.OverlayCustomHeight = slot.Height;
            localSettings.OverlayCustomCornerRadius = slot.CornerRadius;
            localSettings.OverlayCustomIconSize = slot.IconSize;
            localSettings.OverlayCustomTitleFontSize = slot.TitleFontSize;
            localSettings.OverlayCustomDetailFontSize = slot.DetailFontSize;
            localSettings.OverlayCustomMetaFontSize = slot.MetaFontSize;
            localSettings.OverlayCustomAutoResizeToContent = slot.AutoResizeToContent;
            localSettings.OverlayCustomWrapAllText = slot.WrapAllText;
            localSettings.OverlayCustomShowLine1 = slot.ShowLine1;
            localSettings.OverlayCustomShowBorder = slot.ShowBorder;
            localSettings.OverlayCustomShowGameName = slot.ShowGameName;
            localSettings.OverlayCustomShowMeta = slot.ShowMeta;
            localSettings.OverlayCustomBackgroundColor = slot.BackgroundColor;
            localSettings.OverlayCustomBorderColor = slot.BorderColor;
            localSettings.OverlayCustomAccentColor = slot.AccentColor;
            localSettings.OverlayCustomTitleColor = slot.TitleColor;
            localSettings.OverlayCustomDetailColor = slot.DetailColor;
            localSettings.OverlayCustomMetaColor = slot.MetaColor;
            localSettings.OverlayCustomBackgroundImagePath = slot.BackgroundImagePath;
            localSettings.OverlayCustomTitleTemplate = slot.TitleTemplate;
            localSettings.OverlayCustomGameNameTemplate = slot.GameNameTemplate;
            localSettings.OverlayCustomAchievementTemplate = slot.AchievementTemplate;
            localSettings.OverlayCustomMetaTemplate = slot.MetaTemplate;
            localSettings.OverlayCustomTitleBold = slot.TitleBold;
            localSettings.OverlayCustomTitleItalic = slot.TitleItalic;
            localSettings.OverlayCustomTitleUnderline = slot.TitleUnderline;
            localSettings.OverlayCustomTitleStrikethrough = slot.TitleStrikethrough;
            localSettings.OverlayCustomDetailBold = slot.DetailBold;
            localSettings.OverlayCustomDetailItalic = slot.DetailItalic;
            localSettings.OverlayCustomDetailUnderline = slot.DetailUnderline;
            localSettings.OverlayCustomDetailStrikethrough = slot.DetailStrikethrough;
            localSettings.OverlayCustomMetaBold = slot.MetaBold;
            localSettings.OverlayCustomMetaItalic = slot.MetaItalic;
            localSettings.OverlayCustomMetaUnderline = slot.MetaUnderline;
            localSettings.OverlayCustomMetaStrikethrough = slot.MetaStrikethrough;
            localSettings.OverlayCustomIconSource = slot.IconSource;
            localSettings.OverlayCustomShowIconRarityGlow = slot.ShowIconRarityGlow;
            localSettings.OverlayCustomCoverImagePath = slot.CustomCoverImagePath;
            localSettings.OverlayCustomBannerImagePath = slot.CustomBannerImagePath;
        }

        private void NotificationsPickCustomColor_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button))
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var colorKey = button.Tag as string;
            var currentHex = ResolveCustomColorHex(localSettings, colorKey);
            var initialColor = System.Drawing.Color.White;

            try
            {
                if (!string.IsNullOrWhiteSpace(currentHex))
                {
                    initialColor = System.Drawing.ColorTranslator.FromHtml(currentHex);
                }
            }
            catch
            {
            }

            using (var dialog = new WinForms.ColorDialog
            {
                AllowFullOpen = true,
                AnyColor = true,
                FullOpen = true,
                Color = initialColor
            })
            {
                if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                {
                    return;
                }

                var selectedHex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
                ApplyCustomColorHex(localSettings, colorKey, selectedHex);
                UpdateCustomStyleInlinePreview(localSettings);
                NotificationsUnlockSoundStatusTextBlock.Text = $"Selected color {selectedHex} for {colorKey}.";
            }
        }

        private void UpdateCustomStyleInlinePreview(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null || NotificationsCustomInlinePreviewHost == null)
            {
                return;
            }

            var previewGame = GetSelectedNotificationPreviewGame();
            var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;
            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            NotificationsCustomInlinePreviewHost.Content = publisher.CreateOverlayPreviewContent(
                previewGameName,
                "Sample Achievement",
                forcedStyle: NotificationPublisher.NotificationStyleCustom,
                providerKey: "Local",
                overrideLocalSettings: localSettings,
                game: previewGame,
                achievementDescription: "Win your first fight without taking damage.",
                achievementPoints: 25,
                achievementRarity: "12.7%",
                achievementTrophy: "Gold");
        }

        private static Brush ResolveInlinePreviewBackground(Providers.Local.LocalSettings localSettings)
        {
            var imagePath = localSettings?.OverlayCustomBackgroundImagePath?.Trim();
            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var brush = new ImageBrush(bitmap)
                    {
                        Stretch = Stretch.UniformToFill,
                        Opacity = 0.92
                    };
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                }
            }

            return ParseColorBrush(localSettings?.OverlayCustomBackgroundColor, Color.FromRgb(30, 36, 48));
        }

        private static Brush ParseColorBrush(string colorValue, Color fallback)
        {
            if (!string.IsNullOrWhiteSpace(colorValue))
            {
                try
                {
                    var converted = (Color)ColorConverter.ConvertFromString(colorValue.Trim());
                    var brush = new SolidColorBrush(converted);
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                }
            }

            var fallbackBrush = new SolidColorBrush(fallback);
            fallbackBrush.Freeze();
            return fallbackBrush;
        }

        private static string ResolveCustomColorHex(Providers.Local.LocalSettings localSettings, string colorKey)
        {
            switch (colorKey)
            {
                case "Background":
                    return localSettings.OverlayCustomBackgroundColor;
                case "Border":
                    return localSettings.OverlayCustomBorderColor;
                case "Accent":
                    return localSettings.OverlayCustomAccentColor;
                case "Title":
                    return localSettings.OverlayCustomTitleColor;
                case "Detail":
                    return localSettings.OverlayCustomDetailColor;
                case "Meta":
                    return localSettings.OverlayCustomMetaColor;
                default:
                    return "#FFFFFF";
            }
        }

        private static void ApplyCustomColorHex(Providers.Local.LocalSettings localSettings, string colorKey, string colorHex)
        {
            switch (colorKey)
            {
                case "Background":
                    localSettings.OverlayCustomBackgroundColor = colorHex;
                    break;
                case "Border":
                    localSettings.OverlayCustomBorderColor = colorHex;
                    break;
                case "Accent":
                    localSettings.OverlayCustomAccentColor = colorHex;
                    break;
                case "Title":
                    localSettings.OverlayCustomTitleColor = colorHex;
                    break;
                case "Detail":
                    localSettings.OverlayCustomDetailColor = colorHex;
                    break;
                case "Meta":
                    localSettings.OverlayCustomMetaColor = colorHex;
                    break;
            }
        }

        // -----

        // Theme migration
        // -----

        private void LoadThemes()
        {
            // Ensure we're on the UI thread before accessing DependencyProperties
            if (Dispatcher.CheckAccess())
            {
                LoadThemesInternal();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(LoadThemesInternal));
            }
        }

        private void LoadThemesInternal()
        {
            try
            {
                AvailableThemes.Clear();
                RevertableThemes.Clear();

                var cache = _settingsViewModel?.Settings?.Persisted?.ThemeMigrationVersionCache;
                var themes = _themeDiscovery
                    .DiscoverDefaultThemes(cache)
                    .Where(theme => !IsExcludedTheme(theme))
                    .ToList();
                if (themes.Count == 0)
                {
                    _logger.Info("No themes path found for theme migration.");
                    UpdateThemeMigrationState();
                    return;
                }

                // Show all discovered themes in both dropdowns so users can
                // re-run migration/revert on any theme without hunting in separate lists.
                foreach (var theme in themes)
                {
                    AvailableThemes.Add(theme);
                }

                foreach (var theme in themes)
                {
                    RevertableThemes.Add(theme);
                }

                if (!string.IsNullOrWhiteSpace(SelectedThemePath) &&
                    !AvailableThemes.Any(t => string.Equals(t.Path, SelectedThemePath, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedThemePath = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(SelectedThemePath))
                {
                    SelectedThemePath = AvailableThemes.FirstOrDefault()?.Path ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(SelectedRevertThemePath) &&
                    !RevertableThemes.Any(t => string.Equals(t.Path, SelectedRevertThemePath, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedRevertThemePath = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(SelectedRevertThemePath))
                {
                    SelectedRevertThemePath = RevertableThemes.FirstOrDefault()?.Path ?? string.Empty;
                }

                UpdateThemeMigrationState();

                _logger.Info($"Loaded {AvailableThemes.Count} themes to migrate, {RevertableThemes.Count} themes to revert.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load themes for migration.");
            }
        }

        private void UpdateThemeMigrationState()
        {
            var hasThemes = AvailableThemes.Count > 0;
            var hasRevertable = RevertableThemes.Any(theme => theme?.HasBackup == true);

            HasThemesToMigrate = hasThemes;
            HasRevertableThemes = hasRevertable;
            ShowNoThemesMessage = !hasThemes;
            ShowNoRevertableThemesMessage = !hasRevertable;
            UpdateThemeMigrationModeButtonState();
            RefreshStartPageCompatibilityState();
        }

        private void RefreshStartPageCompatibilityState()
        {
            try
            {
                HasStartPageInstalled = _startPageCompatibility?.IsStartPageInstalled() == true;
                ShowStartPageNotInstalledMessage = !HasStartPageInstalled;
                HasStartPageBackup = _startPageCompatibility?.HasBackup() == true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to detect StartPage installation state.");
                HasStartPageInstalled = false;
                ShowStartPageNotInstalledMessage = true;
                HasStartPageBackup = false;
            }
        }

        private async void MigrateThemeLimited_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteThemeMigrationAsync(MigrationMode.Limited);
        }

        private async void MigrateThemeFull_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteThemeMigrationAsync(MigrationMode.Full, BuildFullMigrationSelection());
        }

        private async void MigrateThemeCustom_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteThemeMigrationAsync(MigrationMode.Custom, BuildCustomMigrationSelection());
        }

        private void ThemeMigrationCustomExpander_Expanded(object sender, RoutedEventArgs e)
        {
            UpdateThemeMigrationModeButtonState();
        }

        private void ThemeMigrationCustomExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            UpdateThemeMigrationModeButtonState();
        }

        private void ThemeMigrationSetAllLegacy_Click(object sender, RoutedEventArgs e)
        {
            SetAllThemeMigrationCustomOptions(false);
        }

        private void ThemeMigrationSetAllModern_Click(object sender, RoutedEventArgs e)
        {
            SetAllThemeMigrationCustomOptions(true);
        }

        private async Task ExecuteThemeMigrationAsync(MigrationMode mode, CustomMigrationSelection customSelection = null)
        {
            if (string.IsNullOrWhiteSpace(SelectedThemePath))
            {
                _logger.Warn("Migrate clicked but no theme selected.");
                return;
            }

            _logger.Info($"User requested {mode} theme migration for: {SelectedThemePath}");

            try
            {
                var selectedThemeHasBackup = _themeMigration.HasBackup(SelectedThemePath);
                if (selectedThemeHasBackup)
                {
                    _logger.Info($"Selected theme already has backup. Reverting before migration: {SelectedThemePath}");

                    var revertResult = await _themeMigration.RevertThemeAsync(SelectedThemePath);
                    if (!revertResult.Success)
                    {
                        _logger.Warn($"Pre-migration revert failed: {revertResult.Message}");
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            revertResult.Message,
                            L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }
                }

                var result = await _themeMigration.MigrateThemeAsync(SelectedThemePath, mode, customSelection);

                if (result.Success)
                {
                    _logger.Info($"Theme migration ({mode}) successful: {SelectedThemePath}");

                    // Show restart notice whenever files were modified.
                    if (result.FilesProcessed > 0)
                    {
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            $"{result.Message}{Environment.NewLine}{Environment.NewLine}{L("LOCPlayAch_ThemeMigration_RestartRequired", "Please restart Playnite to apply the theme changes.")}",
                            L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        // No changes were made, just show info message
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            result.Message,
                            L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }

                    // Reload themes to update the lists
                    LoadThemes();
                }
                else
                {
                    _logger.Warn($"Theme migration failed: {result.Message}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to execute theme migration.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void RevertTheme_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SelectedRevertThemePath))
            {
                _logger.Warn("Revert clicked but no theme selected.");
                return;
            }

            if (!_themeMigration.HasBackup(SelectedRevertThemePath))
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_ThemeMigration_NoRevertableThemes", "No themes with backups to revert."),
                    L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _logger.Info($"User requested theme revert for: {SelectedRevertThemePath}");

            try
            {
                var result = await _themeMigration.RevertThemeAsync(SelectedRevertThemePath);

                if (result.Success)
                {
                    _logger.Info($"Theme revert successful: {SelectedRevertThemePath}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // Reload themes to update the lists
                    LoadThemes();
                }
                else
                {
                    _logger.Warn($"Theme revert failed: {result.Message}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to execute theme revert.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void ApplyStartPageCompatibility_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshStartPageCompatibilityState();
                if (!HasStartPageInstalled)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_NotInstalled", "StartPage is not installed in this Playnite environment."),
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var result = await _startPageCompatibility.ApplyAsync();
                if (result.Success)
                {
                    var restartText = L("LOCPlayAch_ThemeMigration_RestartRequired", "Please restart Playnite to apply the theme changes.");
                    var details = string.IsNullOrWhiteSpace(result.BackupPath)
                        ? result.Message
                        : $"{result.Message}{Environment.NewLine}{Environment.NewLine}Backup: {result.BackupPath}";

                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        $"{details}{Environment.NewLine}{Environment.NewLine}{restartText}",
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                RefreshStartPageCompatibilityState();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to apply StartPage compatibility migration from settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void RevertStartPageCompatibility_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var confirm = _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_RevertConfirm",
                        "Restore the achievement cache from the last backup? This will undo the compatibility migration."),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                var result = await _startPageCompatibility.RevertAsync();
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    result.Message,
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);

                RefreshStartPageCompatibilityState();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to revert StartPage compatibility migration from settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RestartPlaynite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    // Launch cmd.exe to wait for this process to exit, then relaunch Playnite.
                    // This avoids the "another instance is running" startup error.
                    var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

                    // If a StartPage DLL update is pending, apply it after Playnite has exited
                    // (the DLL is locked while loaded; taskkill releases it before the copy).
                    var hasPendingDll = _startPageCompatibility?.HasPendingDllUpdate() == true;
                    var pendingDll = hasPendingDll ? _startPageCompatibility.GetPendingDllPath() : null;
                    var installedDll = hasPendingDll ? _startPageCompatibility.GetInstalledDllPath() : null;

                    var dllChain = (hasPendingDll && !string.IsNullOrWhiteSpace(installedDll))
                        ? $" & copy /Y \"{pendingDll}\" \"{installedDll}\" >nul 2>&1 & del \"{pendingDll}\" >nul 2>&1"
                        : string.Empty;

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c taskkill /pid {pid} /f >nul 2>&1 & timeout /t 2 /nobreak >nul{dllChain} & start \"\" \"{exePath}\"",
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                else
                {
                    System.Windows.Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to restart Playnite.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void InitializeThemeMigrationCustomOptions()
        {
            ThemeMigrationCustomOptions.Clear();

            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginButton",
                "LOCPlayAch_Settings_ButtonPreview",
                "Button"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginChart",
                "LOCPlayAch_Settings_BarChartPreview",
                "Bar Chart"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactList",
                "LOCPlayAch_Settings_CompactListPreview",
                "Compact List",
                isModern: UseScrollableAchievementsForThemeMigration));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactLocked",
                "LOCPlayAch_Settings_CompactLockedListPreview",
                "Compact Locked List",
                isModern: UseScrollableAchievementsForThemeMigration));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactUnlocked",
                "LOCPlayAch_Settings_CompactUnlockedListPreview",
                "Compact Unlocked List",
                isModern: UseScrollableAchievementsForThemeMigration));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginList",
                "LOCPlayAch_Settings_AchievementDataGridPreview",
                "Achievement DataGrid"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginProgressBar",
                "LOCPlayAch_Settings_ProgressBarPreview",
                "Progress Bar"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginUserStats",
                "LOCPlayAch_Settings_StatsPreview",
                "Stats"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginViewItem",
                "LOCPlayAch_Settings_ViewItemPreview",
                "View Item"));
        }

        private ThemeMigrationElementOption CreateThemeMigrationControlOption(
            string key,
            string resourceKey,
            string fallback,
            bool isModern = true)
        {
            return new ThemeMigrationElementOption(
                key,
                L(resourceKey, fallback),
                isBindingOption: false,
                isModern: isModern);
        }

        private static void OnUseScrollableAchievementsForThemeMigrationChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsControl control)
            {
                control.SetCompactAchievementMigrationOptions((bool)e.NewValue);
            }
        }

        private static void OnSelectedThemePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsControl control)
            {
                control.UpdateThemeMigrationModeButtonState();
            }
        }

        private void UpdateThemeMigrationModeButtonState()
        {
            var isCustomExpanded = ThemeMigrationCustomExpander?.IsExpanded == true;
            var isFullscreenTheme = ThemeMigrationService.IsFullscreenThemePath(SelectedThemePath);

            if (isFullscreenTheme && isCustomExpanded)
            {
                ThemeMigrationCustomExpander.IsExpanded = false;
                isCustomExpanded = false;
            }

            if (ThemeMigrationPresetButtons != null)
            {
                ThemeMigrationPresetButtons.IsEnabled = HasThemesToMigrate && !isCustomExpanded;
            }

            if (ThemeMigrationFullButton != null)
            {
                ThemeMigrationFullButton.IsEnabled = HasThemesToMigrate && !isCustomExpanded && !isFullscreenTheme;
            }

            if (ThemeMigrationCustomContainer != null)
            {
                ThemeMigrationCustomContainer.IsEnabled = HasThemesToMigrate && !isFullscreenTheme;
            }
        }

        private CustomMigrationSelection BuildCustomMigrationSelection()
        {
            var modernControlNames = ThemeMigrationCustomOptions
                .Where(option => option.IsModern)
                .Select(option => option.Key)
                .ToList();

            return new CustomMigrationSelection(modernControlNames, modernizeBindings: true)
            {
                ModernizeCompactAchievementLists = UseScrollableAchievementsForThemeMigration
            };
        }

        private CustomMigrationSelection BuildFullMigrationSelection()
        {
            return new CustomMigrationSelection(
                ControlMappings.LegacyToModernControlNames.Keys,
                modernizeBindings: true)
            {
                ModernizeCompactAchievementLists = UseScrollableAchievementsForThemeMigration
            };
        }

        private void ThemeMigrationRowSetLegacy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: ThemeMigrationElementOption option })
            {
                option.IsModern = false;
            }
        }

        private void ThemeMigrationRowSetModern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: ThemeMigrationElementOption option })
            {
                option.IsModern = true;
            }
        }

        private void SetAllThemeMigrationCustomOptions(bool isModern)
        {
            foreach (var option in ThemeMigrationCustomOptions)
            {
                option.IsModern = isModern;
            }
        }

        private void SetCompactAchievementMigrationOptions(bool isModern)
        {
            foreach (var option in ThemeMigrationCustomOptions)
            {
                if (ControlMappings.CompactAchievementListControlNames.Contains(option.Key))
                {
                    option.IsModern = isModern;
                }
            }
        }

        // -----------------------------
        // Cache actions
        // -----------------------------

        private void WipeCache_Click(object sender, RoutedEventArgs e)
        {
            string message = null;
            var image = MessageBoxImage.Information;
            Exception operationError = null;
            var progressText = L(
                "LOCPlayAch_Settings_Cache_ProgressClearing",
                "Clearing cached achievement data...");

            RunMaintenanceProgress(
                progressText,
                isIndeterminate: true,
                operation: progress =>
                {
                    try
                    {
                        _plugin.RefreshRuntime.Cache.ClearCache();
                        message = L("LOCPlayAch_Status_Succeeded", "Success!");
                        image = MessageBoxImage.Information;
                    }
                    catch (Exception ex)
                    {
                        operationError = ex;
                    }
                });

            if (operationError != null)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", operationError.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _plugin.PlayniteApi.Dialogs.ShowMessage(
                message ?? L("LOCPlayAch_Status_Succeeded", "Success!"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
        }

        private void ClearAllIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.All);

        private void ClearCompressedIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.CompressedOnly);

        private void ClearFullResolutionIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.FullResolutionOnly);

        private void ClearLockedIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.LockedOnly);

        private void ClearIconCache(IconCacheClearScope scope)
        {
            var fileLabel = ResourceProvider.GetString(GetIconCacheFileLabelResourceKey(scope));
            var scanningText = LF(
                "LOCPlayAch_Settings_IconCache_ProgressScanning",
                "Scanning cached {0} files...",
                fileLabel);
            var deletingTextFormat = L(
                "LOCPlayAch_Settings_IconCache_ProgressDeletingCount",
                "Deleting cached {0} files... ({1}/{2})");
            var deletedCount = 0;
            Exception operationError = null;

            RunMaintenanceProgress(
                scanningText,
                isIndeterminate: false,
                operation: progress =>
                {
                    try
                    {
                        UpdateMaintenanceProgress(progress, current: 0, max: 1);

                        IEnumerable<string> additionalPaths = null;
                        if (scope == IconCacheClearScope.LockedOnly)
                        {
                            additionalPaths = GetExplicitLockedIconCachePaths(progress);
                        }

                        _plugin.ImageService?.Clear();
                        deletedCount = _plugin.ImageService?.ClearDiskCache(
                            scope,
                            additionalPaths,
                            (processed, total) =>
                            {
                                var safeTotal = Math.Max(1, total);
                                var safeProcessed = total <= 0
                                    ? 1
                                    : Math.Max(0, Math.Min(total, processed));

                                var progressText = total <= 0
                                    ? LF(
                                        "LOCPlayAch_Settings_IconCache_ProgressNoFiles",
                                        "No cached {0} files were found.",
                                        fileLabel)
                                    : string.Format(
                                        deletingTextFormat,
                                        fileLabel,
                                        safeProcessed,
                                        total);

                                UpdateMaintenanceProgress(
                                    progress,
                                    text: progressText,
                                    current: safeProcessed,
                                    max: safeTotal);
                            }) ?? 0;
                    }
                    catch (Exception ex)
                    {
                        operationError = ex;
                    }
                });

            if (operationError != null)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", operationError.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var message = L("LOCPlayAch_Status_Succeeded", "Success!");

            _plugin.PlayniteApi.Dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private string GetIconCacheFileLabelResourceKey(IconCacheClearScope scope)
        {
            switch (scope)
            {
                case IconCacheClearScope.CompressedOnly:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_Compressed";
                case IconCacheClearScope.FullResolutionOnly:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_FullResolution";
                case IconCacheClearScope.LockedOnly:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_Locked";
                default:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_All";
            }
        }

        private void RunMaintenanceProgress(
            string initialText,
            bool isIndeterminate,
            Action<GlobalProgressActionArgs> operation)
        {
            var progressOptions = new GlobalProgressOptions(initialText)
            {
                Cancelable = false,
                IsIndeterminate = isIndeterminate
            };

            _plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(async progress =>
            {
                UpdateMaintenanceProgress(progress, text: initialText, isIndeterminate: isIndeterminate);
                await Task.Run(() => operation?.Invoke(progress)).ConfigureAwait(false);
            }, progressOptions);
        }

        private void UpdateMaintenanceProgress(
            GlobalProgressActionArgs progress,
            string text = null,
            int? current = null,
            int? max = null,
            bool? isIndeterminate = null)
        {
            if (progress == null)
            {
                return;
            }

            Action update = () =>
            {
                if (max.HasValue)
                {
                    progress.ProgressMaxValue = max.Value;
                }

                if (current.HasValue)
                {
                    progress.CurrentProgressValue = current.Value;
                }

                if (isIndeterminate.HasValue)
                {
                    progress.IsIndeterminate = isIndeterminate.Value;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    progress.Text = text;
                }
            };

            if (progress.MainDispatcher != null)
            {
                progress.MainDispatcher.InvokeIfNeeded(update);
            }
            else
            {
                update();
            }
        }

        private IEnumerable<string> GetExplicitLockedIconCachePaths(GlobalProgressActionArgs progress = null)
        {
            var dataService = _plugin?.AchievementDataService;
            var cachedGameIds = dataService?.GetCachedGameIds();
            if (cachedGameIds == null || cachedGameIds.Count == 0)
            {
                if (progress != null)
                {
                    UpdateMaintenanceProgress(
                        progress,
                        text: L(
                            "LOCPlayAch_Settings_IconCache_ProgressNoLockedReferences",
                            "No cached locked icon references were found."),
                        current: 1,
                        max: 1);
                }

                return Array.Empty<string>();
            }

            var lockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (progress != null)
            {
                UpdateMaintenanceProgress(progress, current: 0, max: cachedGameIds.Count);
            }

            for (var i = 0; i < cachedGameIds.Count; i++)
            {
                var gameId = cachedGameIds[i];
                if (progress != null)
                {
                    UpdateMaintenanceProgress(
                        progress,
                        text: LF(
                            "LOCPlayAch_Settings_IconCache_ProgressScanningLockedReferences",
                            "Scanning cached locked icon references... ({0}/{1})",
                            i + 1,
                            cachedGameIds.Count),
                        current: i + 1,
                        max: cachedGameIds.Count);
                }

                var gameData = dataService?.GetRawGameAchievementData(gameId);
                var achievements = gameData?.Achievements;
                if (achievements == null)
                {
                    continue;
                }

                foreach (var achievement in achievements)
                {
                    var lockedPath = achievement?.LockedIconPath;
                    if (!DiskImageService.IsLocalIconPath(lockedPath))
                    {
                        continue;
                    }

                    var unlockedPath = achievement?.UnlockedIconPath;
                    if (!string.IsNullOrWhiteSpace(unlockedPath) &&
                        string.Equals(lockedPath.Trim(), unlockedPath.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    lockedPaths.Add(lockedPath);
                }
            }

            return lockedPaths;
        }

        private void ResetFirstTimeSetup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger.Info($"Resetting FirstTimeSetupCompleted. Current value before: {_settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted}");

                _settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted = false;

                _logger.Info($"Value after setting to false: {_settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted}");

                _plugin.SavePluginSettings(_settingsViewModel.Settings);
                NotificationPublisher.ClosePersistentSettingsPreview();

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reset first-time setup.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ResetDisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger.Info("Resetting Display tab settings to defaults.");

                _settingsViewModel.Settings.Persisted.ResetDisplaySettingsToDefaults();
                PercentRarityHelper.ApplyBadgeApplicationResources(
                    _settingsViewModel.Settings.Persisted.UseUniformRarityBadges);
                RefreshMockPreviews();
                _plugin.SavePluginSettings(_settingsViewModel.Settings);

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reset Display tab settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExportDatabase_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exportBaseDir = _plugin.GetPluginUserDataPath();
                var exportDir = _plugin.RefreshRuntime.Cache.ExportDatabaseToCsv(exportBaseDir);

                _logger.Info($"Database exported to: {exportDir}");

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded", "Success!") + "\n" + exportDir,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to export database.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dataPath = _plugin.GetPluginUserDataPath();

                if (!Directory.Exists(dataPath))
                {
                    Directory.CreateDirectory(dataPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = dataPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to open extension data folder.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ForceRebuildHashIndex_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var pluginUserDataPath = _plugin.GetPluginUserDataPath();
                var raCacheDir = System.IO.Path.Combine(pluginUserDataPath, "ra");

                if (!System.IO.Directory.Exists(raCacheDir))
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_Status_Succeeded", "Success!"),
                        L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // Find and delete all hash index cache files
                var hashIndexFiles = System.IO.Directory.GetFiles(raCacheDir, "hashindex_*.json.gz");
                var deletedCount = 0;

                foreach (var file in hashIndexFiles)
                {
                    try
                    {
                        System.IO.File.Delete(file);
                        deletedCount++;
                        _logger.Info($"Deleted hash index cache: {file}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, $"Failed to delete hash index cache: {file}");
                    }
                }

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to force hash index rebuild.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // -----------------------------
        // Compact list sort direction toggles
        // -----------------------------

        private void ToggleCompactListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactListSortDescending = !persisted.CompactListSortDescending;
            }
        }

        private void ToggleCompactUnlockedListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactUnlockedListSortDescending = !persisted.CompactUnlockedListSortDescending;
            }
        }

        private void ToggleCompactLockedListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactLockedListSortDescending = !persisted.CompactLockedListSortDescending;
            }
        }

        private void ToggleOverviewGameSummariesGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.OverviewGameSummariesGridSortDescending = !persisted.OverviewGameSummariesGridSortDescending;
            }
        }

        private void ToggleStartPageGameSummariesGridSortDescending(object sender, RoutedEventArgs e)
        {
            var settings = _settingsViewModel?.Settings?.Persisted?.StartPageGameSummariesGrid;
            if (settings != null)
            {
                settings.SortDescending = !settings.SortDescending;
            }
        }

        private void ToggleOverviewSelectedGameGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.OverviewSelectedGameGridSortDescending = !persisted.OverviewSelectedGameGridSortDescending;
            }
        }

        private void ToggleDefaultAchievementSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.DefaultAchievementSortDescending = !persisted.DefaultAchievementSortDescending;
            }
        }

        private void ConfigureManualAchievementSort_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsViewModel?.Settings == null)
            {
                return;
            }

            if (ManualAchievementSortDialog.TryShowDialog(
                _settingsViewModel.Settings,
                () =>
                {
                    _plugin.SavePluginSettings(_settingsViewModel.Settings);
                    NotificationPublisher.ClosePersistentSettingsPreview();
                }))
            {
                _plugin.SavePluginSettings(_settingsViewModel.Settings);
                NotificationPublisher.ClosePersistentSettingsPreview();
            }
        }
        private void ToggleSidebarSelectedGameGridSortDescending(object sender, RoutedEventArgs e)
        {
            ToggleOverviewSelectedGameGridSortDescending(sender, e);
        }

        private void ToggleGamesOverviewGridSortDescending(object sender, RoutedEventArgs e)
        {
            ToggleOverviewGameSummariesGridSortDescending(sender, e);
        }

        private void ToggleSingleGameGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.SingleGameGridSortDescending = !persisted.SingleGameGridSortDescending;
            }
        }

        private void ToggleStartPageRecentUnlocksGridSortDescending(object sender, RoutedEventArgs e)
        {
            var settings = _settingsViewModel?.Settings?.Persisted?.StartPageRecentUnlocksGrid;
            if (settings != null)
            {
                settings.SortDescending = !settings.SortDescending;
            }
        }

        private void ToggleAchievementDataGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.AchievementDataGridSortDescending = !persisted.AchievementDataGridSortDescending;
            }
        }

        // -----------------------------
        // Tagging Methods
        // -----------------------------

        /// <summary>
        /// Commits pending tag name changes from text boxes to the source.
        /// </summary>
        private void CommitTagNameBindings()
        {
            var textBoxes = new TextBox[]
            {
                HasAchievementsTagTextBox,
                InProgressTagTextBox,
                CompletedTagTextBox,
                NoAchievementsTagTextBox,
                CustomizedTagTextBox,
                NotCustomizedTagTextBox,
                ExcludedTagTextBox,
                ExcludedFromSummariesTagTextBox
            };

            foreach (var textBox in textBoxes)
            {
                var binding = textBox?.GetBindingExpression(TextBox.TextProperty);
                if (binding != null)
                {
                    binding.UpdateSource();
                }
            }
        }

        private void ApplyAndSync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Commit any pending text box changes
                CommitTagNameBindings();

                var tagSyncService = _plugin.TagSyncService;
                if (tagSyncService == null)
                {
                    _logger?.Warn("TagSyncService not available");
                    return;
                }

                // Sync all tags (handles orphan cleanup and re-tagging with new names)
                tagSyncService.SyncAllTags();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to apply and sync tags.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RemoveAllTags_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Tagging_RemoveAllConfirm", "Remove all Playnite Achievements tags from all games?"),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                var tagSyncService = _plugin.TagSyncService;
                if (tagSyncService == null)
                {
                    _logger?.Warn("TagSyncService not available");
                    return;
                }

                tagSyncService.RemoveAllTags();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to remove tags.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // -----------------------------
        // Provider Navigation Overview
        // -----------------------------

        /// <summary>
        /// Builds provider navigation items for the overview from registered providers.
        /// Items are added in the natural discovery order.
        /// </summary>
        private void BuildProviderNavigationItems()
        {
            if (_providerNavigationBuilt)
            {
                return;
            }

            ProviderNavigationItems.Clear();

            foreach (var providerKey in _providerRegistry.GetSettingsViewProviderKeys())
            {
                var settings = _providerRegistry.GetSettingsForEdit(providerKey);
                if (settings == null) continue;

                var view = _providerRegistry.CreateSettingsView(providerKey);
                var displayName = ProviderRegistry.GetLocalizedName(providerKey);
                var iconKey = $"ProviderIcon{providerKey}";
                var colorHex = "#FF4084D6";
                string redirectProviderKey = null;
                string subtitle = null;

                if (_providerRegistry.TryGetProviderVisuals(providerKey, out var providerIconKey, out var providerColorHex))
                {
                    if (!string.IsNullOrWhiteSpace(providerIconKey))
                    {
                        iconKey = providerIconKey;
                    }

                    if (!string.IsNullOrWhiteSpace(providerColorHex))
                    {
                        colorHex = providerColorHex;
                    }
                }

                if (view == null)
                {
                    if (!ProviderUiPolicies.TryGetSettingsRedirectProviderKey(providerKey, out redirectProviderKey))
                    {
                        continue;
                    }

                    subtitle = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_ProviderServicedByFormat"),
                        ProviderRegistry.GetLocalizedName(redirectProviderKey));
                }
                else
                {
                    view.Initialize(settings);
                }

                ProviderNavigationItems.Add(new ProviderNavigationItem(
                    providerKey,
                    displayName,
                    GetProviderSettingsGroupName(providerKey),
                    iconKey,
                    colorHex,
                    settings,
                    view,
                    redirectProviderKey,
                    subtitle));
            }

            ConfigureProviderNavigationView();

            if (SelectedProviderNavigationItem == null && ProviderNavigationItems.Count > 0)
            {
                SelectedProviderNavigationItem = ProviderNavigationItems[0];
            }

            _providerNavigationBuilt = true;
            _logger?.Info($"Built {ProviderNavigationItems.Count} platform navigation items");
        }

        private static string GetProviderSettingsGroupName(string providerKey)
        {
            return ResourceProvider.GetString(ProviderUiPolicies.GetSettingsGroupResourceKey(providerKey));
        }

        private void ConfigureProviderNavigationView()
        {
            _providerNavigationView = CollectionViewSource.GetDefaultView(ProviderNavigationItems);
            if (_providerNavigationView == null)
            {
                return;
            }

            _providerNavigationView.Filter = FilterProviderNavigationItem;
            _providerNavigationView.GroupDescriptions.Clear();
            _providerNavigationView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(ProviderNavigationItem.GroupName)));
            _providerNavigationView.Refresh();
        }

        private bool FilterProviderNavigationItem(object item)
        {
            if (!(item is ProviderNavigationItem providerItem))
            {
                return false;
            }

            var searchText = ProviderSearchText;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            return ContainsSearchText(providerItem.DisplayName, searchText) ||
                ContainsSearchText(providerItem.ProviderKey, searchText) ||
                ContainsSearchText(providerItem.GroupName, searchText) ||
                ContainsSearchText(providerItem.Subtitle, searchText);
        }

        private static bool ContainsSearchText(string value, string searchText)
            => !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void OnProviderSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsControl control)
            {
                control._providerNavigationView?.Refresh();
                control.SelectFirstVisibleProviderIfNeeded();
            }
        }

        private void SelectFirstVisibleProviderIfNeeded()
        {
            if (_providerNavigationView == null)
            {
                return;
            }

            if (SelectedProviderNavigationItem != null &&
                _providerNavigationView.Contains(SelectedProviderNavigationItem))
            {
                return;
            }

            SelectedProviderNavigationItem = _providerNavigationView
                .Cast<ProviderNavigationItem>()
                .FirstOrDefault();
        }

        private static void OnSelectedProviderNavigationItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsControl control && e.NewValue is ProviderNavigationItem item)
            {
                control.OnSelectedProviderNavigationItemChangedInternal(item);
            }
        }

        private async void OnSelectedProviderNavigationItemChangedInternal(ProviderNavigationItem item)
        {
            if (item == null) return;

            if (item.IsRedirect)
            {
                var redirectItem = ProviderNavigationItems?.FirstOrDefault(x =>
                    string.Equals(x.ProviderKey, item.RedirectProviderKey, StringComparison.OrdinalIgnoreCase));

                if (redirectItem != null && !ReferenceEquals(item, redirectItem))
                {
                    ProviderSearchText = string.Empty;
                    SelectedProviderNavigationItem = redirectItem;
                    _logger?.Info($"Redirected {item.ProviderKey} settings navigation to {redirectItem.ProviderKey}");
                }
                else
                {
                    _logger?.Warn($"Provider settings redirect target not found for {item.ProviderKey}: {item.RedirectProviderKey}");
                }

                return;
            }

            if (item.SettingsView == null) return;

            // Refresh auth status if the view implements IAuthRefreshable
            if (item.SettingsView is IAuthRefreshable authRefreshable)
            {
                try
                {
                    await authRefreshable.RefreshAuthStatusAsync();
                    _logger?.Info($"Refreshed auth status for {item.ProviderKey}");
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to refresh auth status for {item.ProviderKey}");
                }
            }
        }

        private void NavigateToPendingProvider()
        {
            var targetKey = PendingNavigationProviderKey;
            PendingNavigationProviderKey = null;

            if (!string.IsNullOrWhiteSpace(targetKey))
            {
                var targetItem = ProviderNavigationItems?.FirstOrDefault(x =>
                    string.Equals(x.ProviderKey, targetKey, StringComparison.OrdinalIgnoreCase));

                if (targetItem != null && ProvidersTab != null)
                {
                    SettingsTabControl.SelectedItem = ProvidersTab;
                    SelectedProviderNavigationItem = targetItem;
                    _logger?.Info($"Navigated to pending provider: {targetKey}");
                    return;
                }

                foreach (TabItem tab in SettingsTabControl.Items)
                {
                    if (string.Equals(tab.Tag as string, targetKey, StringComparison.OrdinalIgnoreCase))
                    {
                        SettingsTabControl.SelectedItem = tab;
                        return;
                    }
                }
            }

            var targetTabName = PendingNavigationTabName;
            PendingNavigationTabName = null;
            if (string.IsNullOrWhiteSpace(targetTabName))
            {
                return;
            }

            foreach (TabItem tab in SettingsTabControl.Items)
            {
                if (string.Equals(tab.Name, targetTabName, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsTabControl.SelectedItem = tab;
                    return;
                }
            }
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is TabControl)) return;

            // Inspect the newly selected TabItem by name (uses x:Name from XAML)
            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (e.AddedItems[0] is not TabItem selected) return;

            var name = selected.Name ?? string.Empty;
            var tag = selected.Tag as string ?? string.Empty;
            CurrentSelectedTabName = name;
            CurrentSelectedProviderKey = string.IsNullOrWhiteSpace(tag) ? null : tag;

            if (string.Equals(name, "ThemeMigrationTab", StringComparison.OrdinalIgnoreCase))
            {
                LoadThemes();
                _logger?.Info("Loaded themes for Theme Migration tab.");
            }
        }

        // Quick navigation button handlers from General tab
        private void JumpToDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (DisplayTab != null)
            {
                SettingsTabControl.SelectedItem = DisplayTab;
                DisplayTab.BringIntoView();
            }
        }

        private void JumpToProviders_Click(object sender, RoutedEventArgs e)
        {
            if (ProvidersTab != null)
            {
                SettingsTabControl.SelectedItem = ProvidersTab;
                ProvidersTab.BringIntoView();
            }
        }

        private void JumpToThemeMigration_Click(object sender, RoutedEventArgs e)
        {
            if (ThemeMigrationTab != null)
            {
                SettingsTabControl.SelectedItem = ThemeMigrationTab;
                ThemeMigrationTab.BringIntoView();
            }
        }

        // -----------------------------
        // Settings property change handling for mock preview refresh
        // -----------------------------

        private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null &&
                e.PropertyName == nameof(Models.Settings.PersistedSettings.DefaultAchievementSortMode) &&
                persisted.DefaultAchievementSortMode == Models.Settings.CompactListSortMode.DisplayOrder &&
                persisted.DefaultAchievementSortDescending)
            {
                // RetroAchievements progression order should default to the API sequence (ascending DisplayOrder).
                persisted.DefaultAchievementSortDescending = false;
            }

            // Refresh mock previews when display-affecting settings change
            var refreshProperties = new[]
            {
                nameof(Models.Settings.PersistedSettings.ShowCompactListRarityBar),
                nameof(Models.Settings.PersistedSettings.ShowRarityGlow),
                nameof(Models.Settings.PersistedSettings.ShowHiddenIcon),
                nameof(Models.Settings.PersistedSettings.ShowHiddenTitle),
                nameof(Models.Settings.PersistedSettings.ShowHiddenDescription),
                nameof(Models.Settings.PersistedSettings.ShowHiddenSuffix),
                nameof(Models.Settings.PersistedSettings.ShowLockedIcon),
                nameof(Models.Settings.PersistedSettings.UseSeparateLockedIconsWhenAvailable),
                nameof(Models.Settings.PersistedSettings.UseUniformRarityBadges)
            };

            if (e.PropertyName == nameof(Models.Settings.PersistedSettings.UseUniformRarityBadges))
            {
                PercentRarityHelper.ApplyBadgeApplicationResources(
                    _settingsViewModel?.Settings?.Persisted?.UseUniformRarityBadges ?? false);
            }

            if (refreshProperties.Contains(e.PropertyName))
            {
                RefreshMockPreviews();
            }
        }

        // -----------------------------
        // IDisposable implementation
        // -----------------------------

        public void Dispose()
        {
            _notificationAutoPopupPreviewTimer?.Stop();
            NotificationPublisher.ClosePersistentSettingsPreview();
            if (_notificationPreviewSettings != null)
            {
                _notificationPreviewSettings.PropertyChanged -= LocalNotificationSettings_PropertyChanged;
                _notificationPreviewSettings = null;
            }
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string LF(string key, string fallbackFormat, params object[] args)
        {
            return string.Format(L(key, fallbackFormat), args);
        }

        private static bool IsExcludedTheme(ThemeDiscoveryService.ThemeInfo theme)
        {
            if (theme == null)
            {
                return true;
            }

            if (string.Equals(theme.DisplayName, "Default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(theme.Name, "Default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(theme.Name) &&
                (theme.Name.EndsWith("/Default", StringComparison.OrdinalIgnoreCase) ||
                 theme.Name.EndsWith("\\Default", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }
        
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Represents a provider item in the settings Providers overview navigation.
    /// </summary>
    public sealed class ProviderNavigationItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly IProviderSettings _settings;

        public ProviderNavigationItem(
            string providerKey,
            string displayName,
            string groupName,
            string providerIconKey,
            string providerColorHex,
            IProviderSettings settings,
            ProviderSettingsViewBase settingsView,
            string redirectProviderKey = null,
            string subtitle = null)
        {
            ProviderKey = providerKey;
            DisplayName = displayName;
            GroupName = groupName;
            ProviderIconKey = providerIconKey;
            ProviderColorHex = providerColorHex;
            SettingsView = settingsView;
            RedirectProviderKey = redirectProviderKey;
            Subtitle = subtitle;
            _settings = settings;

            if (_settings != null)
            {
                _settings.PropertyChanged += Settings_PropertyChanged;
            }
        }

        public string ProviderKey { get; }
        public string DisplayName { get; }
        public string GroupName { get; }
        public string ProviderIconKey { get; }
        public string ProviderColorHex { get; }
        public string RedirectProviderKey { get; }
        public string Subtitle { get; }
        public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
        public bool IsRedirect => !string.IsNullOrWhiteSpace(RedirectProviderKey);
        public bool IsEnabled => _settings?.IsEnabled ?? true;
        public ProviderSettingsViewBase SettingsView { get; }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                string.Equals(e.PropertyName, nameof(IProviderSettings.IsEnabled), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsEnabled));
            }
        }
    }

    public sealed class ThemeMigrationElementOption : INotifyPropertyChanged
    {
        private bool _isModern;

        public ThemeMigrationElementOption(string key, string displayName, bool isBindingOption, bool isModern)
        {
            Key = key;
            DisplayName = displayName;
            IsBindingOption = isBindingOption;
            _isModern = isModern;
        }

        public string Key { get; }

        public string DisplayName { get; }

        public bool IsBindingOption { get; }

        public bool IsModern
        {
            get => _isModern;
            set
            {
                if (_isModern != value)
                {
                    _isModern = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModern)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}




