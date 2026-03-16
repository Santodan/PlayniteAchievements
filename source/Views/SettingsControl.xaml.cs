// SettingsControl.xaml.cs
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using PlayniteAchievements.Services;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.GOG;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.PSN;
using PlayniteAchievements.Providers.Xbox;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services.ThemeMigration;
using Playnite.SDK;
using System.Diagnostics;
using System.Windows.Navigation;

namespace PlayniteAchievements.Views
{
    public partial class SettingsControl : UserControl, IDisposable
    {
        // -----------------------------
        // Option A (safe): UserControl DependencyProperties for auth UI
        // -----------------------------

        public static readonly DependencyProperty SteamAuthStatusProperty =
            DependencyProperty.Register(
                nameof(SteamAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Settings_Status_NotChecked")));

        public string SteamAuthStatus
        {
            get => (string)GetValue(SteamAuthStatusProperty);
            set => SetValue(SteamAuthStatusProperty, value);
        }

        public static readonly DependencyProperty SteamAuthBusyProperty =
            DependencyProperty.Register(
                nameof(SteamAuthBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool SteamAuthBusy
        {
            get => (bool)GetValue(SteamAuthBusyProperty);
            set => SetValue(SteamAuthBusyProperty, value);
        }

        public static readonly DependencyProperty GogAuthStatusProperty =
            DependencyProperty.Register(
                nameof(GogAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotChecked"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_GOG"))));

        public string GogAuthStatus
        {
            get => (string)GetValue(GogAuthStatusProperty);
            set => SetValue(GogAuthStatusProperty, value);
        }

        public static readonly DependencyProperty GogAuthBusyProperty =
            DependencyProperty.Register(
                nameof(GogAuthBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool GogAuthBusy
        {
            get => (bool)GetValue(GogAuthBusyProperty);
            set => SetValue(GogAuthBusyProperty, value);
        }

        public static readonly DependencyProperty GogAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(GogAuthenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool GogAuthenticated
        {
            get => (bool)GetValue(GogAuthenticatedProperty);
            set => SetValue(GogAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty EpicAuthStatusProperty =
            DependencyProperty.Register(
                nameof(EpicAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotChecked"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Epic"))));

        public string EpicAuthStatus
        {
            get => (string)GetValue(EpicAuthStatusProperty);
            set => SetValue(EpicAuthStatusProperty, value);
        }

        public static readonly DependencyProperty EpicAuthBusyProperty =
            DependencyProperty.Register(
                nameof(EpicAuthBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool EpicAuthBusy
        {
            get => (bool)GetValue(EpicAuthBusyProperty);
            set => SetValue(EpicAuthBusyProperty, value);
        }

        public static readonly DependencyProperty EpicAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(EpicAuthenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool EpicAuthenticated
        {
            get => (bool)GetValue(EpicAuthenticatedProperty);
            set => SetValue(EpicAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty PsnAuthStatusProperty =
            DependencyProperty.Register(
                nameof(PsnAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotChecked"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_PSN"))));

        public string PsnAuthStatus
        {
            get => (string)GetValue(PsnAuthStatusProperty);
            set => SetValue(PsnAuthStatusProperty, value);
        }

        public static readonly DependencyProperty PsnAuthBusyProperty =
            DependencyProperty.Register(
                nameof(PsnAuthBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool PsnAuthBusy
        {
            get => (bool)GetValue(PsnAuthBusyProperty);
            set => SetValue(PsnAuthBusyProperty, value);
        }

        public static readonly DependencyProperty PsnAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(PsnAuthenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool PsnAuthenticated
        {
            get => (bool)GetValue(PsnAuthenticatedProperty);
            set => SetValue(PsnAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty XboxAuthStatusProperty =
            DependencyProperty.Register(
                nameof(XboxAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotChecked"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Xbox"))));

        public string XboxAuthStatus
        {
            get => (string)GetValue(XboxAuthStatusProperty);
            set => SetValue(XboxAuthStatusProperty, value);
        }

        public static readonly DependencyProperty XboxAuthBusyProperty =
            DependencyProperty.Register(
                nameof(XboxAuthBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool XboxAuthBusy
        {
            get => (bool)GetValue(XboxAuthBusyProperty);
            set => SetValue(XboxAuthBusyProperty, value);
        }

        public static readonly DependencyProperty XboxAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(XboxAuthenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool XboxAuthenticated
        {
            get => (bool)GetValue(XboxAuthenticatedProperty);
            set => SetValue(XboxAuthenticatedProperty, value);
        }

        // -----------------------------
        // Exophase Auth DependencyProperties
        // -----------------------------

        public static readonly DependencyProperty ExophaseAuthStatusProperty =
            DependencyProperty.Register(
                nameof(ExophaseAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Auth_NotChecked"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Exophase"))));

        public string ExophaseAuthStatus
        {
            get => (string)GetValue(ExophaseAuthStatusProperty);
            set => SetValue(ExophaseAuthStatusProperty, value);
        }

        public static readonly DependencyProperty ExophaseAuthBusyProperty =
            DependencyProperty.Register(
                nameof(ExophaseAuthBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool ExophaseAuthBusy
        {
            get => (bool)GetValue(ExophaseAuthBusyProperty);
            set => SetValue(ExophaseAuthBusyProperty, value);
        }

        public static readonly DependencyProperty ExophaseAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(ExophaseAuthenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool ExophaseAuthenticated
        {
            get => (bool)GetValue(ExophaseAuthenticatedProperty);
            set => SetValue(ExophaseAuthenticatedProperty, value);
        }

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

        private MockThemeData _mockThemeData;

        /// <summary>
        /// Gets mock theme data for controls that bind to ThemeData properties.
        /// </summary>
        public MockThemeData MockThemeData
        {
            get
            {
                if (_mockThemeData == null)
                {
                    _mockThemeData = MockDataHelper.GetMockThemeData();
                }
                return _mockThemeData;
            }
        }

        private ThemeData _previewThemeData;
        private ThemeData _unlockedPreviewThemeData;
        private ThemeData _hiddenPreviewThemeData;
        private ThemeData _lockedPreviewThemeData;

        /// <summary>
        /// Gets a ThemeData populated with mock achievements for desktop control previews.
        /// Used by desktop controls via ThemeDataOverride binding.
        /// </summary>
        public ThemeData PreviewThemeData
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
        /// Gets a ThemeData with a single unlocked achievement for visibility preview.
        /// </summary>
        public ThemeData UnlockedPreviewThemeData
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
        /// Gets a ThemeData with a single hidden achievement for visibility preview.
        /// </summary>
        public ThemeData HiddenPreviewThemeData
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
        /// Gets a ThemeData with a single locked achievement for visibility preview.
        /// </summary>
        public ThemeData LockedPreviewThemeData
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

            // Refresh the preview ThemeData used by desktop controls
            _previewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowLockedIcon, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);

            // Clear single-achievement preview caches to force refresh on next access
            _unlockedPreviewThemeData = null;
            _hiddenPreviewThemeData = null;
            _lockedPreviewThemeData = null;
        }

        public static readonly DependencyProperty ShadPS4AuthStatusProperty =
            DependencyProperty.Register(
                nameof(ShadPS4AuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Settings_ShadPS4_NotConfigured")));

        public string ShadPS4AuthStatus
        {
            get => (string)GetValue(ShadPS4AuthStatusProperty);
            set => SetValue(ShadPS4AuthStatusProperty, value);
        }

        public static readonly DependencyProperty ShadPS4AuthenticatedProperty =
            DependencyProperty.Register(
                nameof(ShadPS4Authenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool ShadPS4Authenticated
        {
            get => (bool)GetValue(ShadPS4AuthenticatedProperty);
            set => SetValue(ShadPS4AuthenticatedProperty, value);
        }

        public static readonly DependencyProperty Rpcs3AuthStatusProperty =
            DependencyProperty.Register(
                nameof(Rpcs3AuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Settings_Rpcs3_NotConfigured")));

        public string Rpcs3AuthStatus
        {
            get => (string)GetValue(Rpcs3AuthStatusProperty);
            set => SetValue(Rpcs3AuthStatusProperty, value);
        }

        public static readonly DependencyProperty Rpcs3AuthenticatedProperty =
            DependencyProperty.Register(
                nameof(Rpcs3Authenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool Rpcs3Authenticated
        {
            get => (bool)GetValue(Rpcs3AuthenticatedProperty);
            set => SetValue(Rpcs3AuthenticatedProperty, value);
        }

        public static readonly DependencyProperty SteamAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(SteamAuthenticated),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool SteamAuthenticated
        {
            get => (bool)GetValue(SteamAuthenticatedProperty);
            set => SetValue(SteamAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty SteamFullyConfiguredProperty =
            DependencyProperty.Register(
                nameof(SteamFullyConfigured),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool SteamFullyConfigured
        {
            get => (bool)GetValue(SteamFullyConfiguredProperty);
            set => SetValue(SteamFullyConfiguredProperty, value);
        }

        public static readonly DependencyProperty RaFullyConfiguredProperty =
            DependencyProperty.Register(
                nameof(RaFullyConfigured),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool RaFullyConfigured
        {
            get => (bool)GetValue(RaFullyConfiguredProperty);
            set => SetValue(RaFullyConfiguredProperty, value);
        }

        public static readonly DependencyProperty RaAuthStatusProperty =
            DependencyProperty.Register(
                nameof(RaAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Settings_Ra_NotChecked")));

        public string RaAuthStatus
        {
            get => (string)GetValue(RaAuthStatusProperty);
            set => SetValue(RaAuthStatusProperty, value);
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
                new PropertyMetadata(string.Empty));

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

        public static readonly DependencyProperty LegacyManualImportStatusProperty =
            DependencyProperty.Register(
                nameof(LegacyManualImportStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_StatusIdle")));

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
        private readonly SteamSessionManager _steamSessionManager;
        private readonly GogSessionManager _gogSessionManager;
        private readonly EpicSessionManager _epicSessionManager;
        private readonly PsnSessionManager _psnSessionManager;
        private readonly XboxSessionManager _xboxSessionManager;
        private readonly ExophaseSessionManager _exophaseSessionManager;
        private const string SuccessStoryExtensionId = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private const string SuccessStoryFolderName = "SuccessStory";

        public SettingsControl(PlayniteAchievementsSettingsViewModel settingsViewModel, ILogger logger, PlayniteAchievementsPlugin plugin, SteamSessionManager steamSessionManager, GogSessionManager gogSessionManager, EpicSessionManager epicSessionManager, PsnSessionManager psnSessionManager, XboxSessionManager xboxSessionManager, ExophaseSessionManager exophaseSessionManager)
        {
            _settingsViewModel = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _steamSessionManager = steamSessionManager ?? throw new ArgumentNullException(nameof(steamSessionManager));
            _gogSessionManager = gogSessionManager ?? throw new ArgumentNullException(nameof(gogSessionManager));
            _epicSessionManager = epicSessionManager ?? throw new ArgumentNullException(nameof(epicSessionManager));
            _psnSessionManager = psnSessionManager ?? throw new ArgumentNullException(nameof(psnSessionManager));
            _xboxSessionManager = xboxSessionManager ?? throw new ArgumentNullException(nameof(xboxSessionManager));
            _exophaseSessionManager = exophaseSessionManager ?? throw new ArgumentNullException(nameof(exophaseSessionManager));

            _themeDiscovery = new ThemeDiscoveryService(_logger, plugin.PlayniteApi);
            _themeMigration = new ThemeMigrationService(
                _logger,
                _settingsViewModel.Settings,
                () => _plugin.SavePluginSettings(_settingsViewModel.Settings));

            InitializeComponent();

            // Playnite does not reliably set DataContext for settings views.
            // Bind directly to the settings model so XAML uses {Binding SomeSetting}.
            DataContext = _settingsViewModel.Settings;

            // Initialize theme collections
            AvailableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            RevertableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();

            // Subscribe to settings property changes to refresh mock previews
            _settingsViewModel.Settings.Persisted.PropertyChanged += OnSettingsPropertyChanged;

            // Debug logging to verify DataContext and Settings values
            _logger?.Info($"SettingsControl created. DataContext type: {DataContext?.GetType().Name}");
            _logger?.Info($"Settings.Persisted.UltraRareThreshold: {_settingsViewModel.Settings.Persisted.UltraRareThreshold}, EnablePeriodicUpdates: {_settingsViewModel.Settings.Persisted.EnablePeriodicUpdates}");

            Loaded += async (s, e) =>
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
                await CheckSteamAuthAsync().ConfigureAwait(false);
                await CheckGogAuthAsync().ConfigureAwait(false);
                await CheckEpicAuthAsync().ConfigureAwait(false);
                await CheckPsnAuthAsync().ConfigureAwait(false);
                await CheckXboxAuthAsync().ConfigureAwait(false);
                await CheckExophaseAuthAsync().ConfigureAwait(false);
                UpdateRaAuthState();
                CheckShadPS4Auth();
                CheckRpcs3Auth();
                EnsureLegacyManualImportPathDefault();
                SetLegacyManualImportStatus(L(
                    "LOCPlayAch_Settings_Manual_Legacy_StatusIdle",
                    "Ready to import Legacy manual links."));

                // Load themes on initial load
                LoadThemes();
            };
        }

        // -----------------------------
        // Theme migration
        // -----------------------------

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

                var themesPath = _themeDiscovery.GetDefaultThemesPath();
                if (string.IsNullOrEmpty(themesPath))
                {
                    _logger.Info("No themes path found for theme migration.");
                    UpdateThemeMigrationState();
                    return;
                }

                var cache = _settingsViewModel?.Settings?.Persisted?.ThemeMigrationVersionCache;
                var themes = _themeDiscovery.DiscoverThemes(themesPath, cache);

                // Themes that need migration (no backup, has SuccessStory)
                foreach (var theme in themes.Where(t => t.NeedsMigration))
                {
                    AvailableThemes.Add(theme);
                }

                // Themes that can be reverted (has backup)
                foreach (var theme in themes.Where(t => t.HasBackup))
                {
                    RevertableThemes.Add(theme);
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
            var hasRevertable = RevertableThemes.Count > 0;

            HasThemesToMigrate = hasThemes;
            HasRevertableThemes = hasRevertable;
            ShowNoThemesMessage = !hasThemes;
            ShowNoRevertableThemesMessage = !hasRevertable;
        }

        private async void MigrateThemeLimited_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteThemeMigrationAsync(MigrationMode.Limited);
        }

        private async void MigrateThemeFull_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteThemeMigrationAsync(MigrationMode.Full);
        }

        private async Task ExecuteThemeMigrationAsync(MigrationMode mode)
        {
            if (string.IsNullOrWhiteSpace(SelectedThemePath))
            {
                _logger.Warn("Migrate clicked but no theme selected.");
                return;
            }

            _logger.Info($"User requested {mode} theme migration for: {SelectedThemePath}");

            try
            {
                var result = await _themeMigration.MigrateThemeAsync(SelectedThemePath, mode);

                if (result.Success)
                {
                    _logger.Info($"Theme migration ({mode}) successful: {SelectedThemePath}");

                    // Only show restart dialog if files were actually modified
                    if (result.FilesBackedUp > 0)
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
                    LF("LOCPlayAch_ThemeMigration_Failed", "Theme migration failed: {0}", ex.Message),
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
                    LF("LOCPlayAch_ThemeMigration_RevertFailed", "Revert failed: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void UpdateRaAuthState()
        {
            var settings = _settingsViewModel.Settings;
            var hasUsername = !string.IsNullOrEmpty(settings?.Persisted?.RaUsername);
            var hasApiKey = !string.IsNullOrEmpty(settings?.Persisted?.RaWebApiKey);
            var fullyConfigured = hasUsername && hasApiKey;

            SetRaFullyConfigured(fullyConfigured);

            // Set status message based on combined state
            if (fullyConfigured)
            {
                SetRaAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Ra_Configured"));
            }
            else if (!hasUsername && !hasApiKey)
            {
                SetRaAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Ra_BothNeeded"));
            }
            else if (!hasUsername)
            {
                SetRaAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Ra_UsernameNeeded"));
            }
            else
            {
                SetRaAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Ra_ApiKeyNeeded"));
            }
        }

        private void SetRaFullyConfigured(bool fullyConfigured)
        {
            if (Dispatcher.CheckAccess())
            {
                RaFullyConfigured = fullyConfigured;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => RaFullyConfigured = fullyConfigured));
            }
        }

        private void SetRaAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                RaAuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => RaAuthStatus = status));
            }
        }

        // -----------------------------
        // Credential text box handlers (Enter key commits entry)
        // -----------------------------

        private async void SteamApiKey_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await CheckSteamAuthAsync().ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(new Action(() => MoveFocusFrom((TextBox)sender)));
            }
        }

        private async void SteamApiKey_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            await CheckSteamAuthAsync().ConfigureAwait(false);
        }

        private void RaUsername_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                UpdateRaAuthState();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private void RaUsername_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            UpdateRaAuthState();
        }

        private void RaApiKey_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                UpdateRaAuthState();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private void RaApiKey_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            UpdateRaAuthState();
        }

        private void Rpcs3ExecutablePath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                CheckRpcs3Auth();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private void Rpcs3ExecutablePath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CheckRpcs3Auth();
        }

        private void ShadPS4GameDataPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                CheckShadPS4Auth();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private void ShadPS4GameDataPath_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CheckShadPS4Auth();
        }

        private void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        // -----------------------------
        // Steam auth UI
        // -----------------------------

        private async void SteamAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            await CheckSteamAuthAsync().ConfigureAwait(false);
        }

        private async void SteamAuth_Authenticate_Click(object sender, RoutedEventArgs e)
        {
            SetSteamAuthBusy(true);

            try
            {
                var (ok, msg) = await _steamSessionManager.AuthenticateInteractiveAsync(CancellationToken.None).ConfigureAwait(false);
                SetSteamAuthStatus(msg);

                // Update auth state after authentication attempt
                if (ok)
                {
                    SetSteamAuthenticated(true);
                }
                else
                {
                    // Verify actual login state even if auth reported failure
                    await CheckSteamAuthAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                SetSteamAuthBusy(false);
            }
        }

        private void SteamAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            _steamSessionManager.ClearSession();
            SetSteamAuthenticated(false);
            UpdateCombinedAuthState(false);
            SetSteamAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Status_CookiesCleared"));
        }

        private async Task CheckSteamAuthAsync()
        {
            SetSteamAuthBusy(true);
            try
            {
                var (isLoggedIn, _) = await _steamSessionManager.ProbeLoggedInAsync(CancellationToken.None).ConfigureAwait(false);
                SetSteamAuthenticated(isLoggedIn);

                // Update combined auth state
                UpdateCombinedAuthState(isLoggedIn);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Steam auth check failed.");
                SetSteamAuthenticated(false);
                SetSteamAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Status_AuthError"));
                UpdateCombinedAuthState(false);
            }
            finally
            {
                SetSteamAuthBusy(false);
            }
        }

        private void UpdateCombinedAuthState(bool webAuthSuccessful)
        {
            var settings = _settingsViewModel.Settings;
            var hasApiKey = !string.IsNullOrEmpty(settings?.Persisted?.SteamApiKey);
            var fullyConfigured = hasApiKey && webAuthSuccessful;

            SetSteamFullyConfigured(fullyConfigured);

            // Set status message based on combined state
            if (fullyConfigured)
            {
                SetSteamAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_SteamAuth_OK"));
            }
            else if (!hasApiKey && !webAuthSuccessful)
            {
                SetSteamAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Status_BothNeeded"));
            }
            else if (!hasApiKey)
            {
                SetSteamAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Status_ApiKeyNeeded"));
            }
            else
            {
                SetSteamAuthStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Status_WebAuthNeeded"));
            }
        }

        private void SetSteamAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                SteamAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => SteamAuthenticated = authenticated));
            }
        }

        private void SetSteamFullyConfigured(bool fullyConfigured)
        {
            if (Dispatcher.CheckAccess())
            {
                SteamFullyConfigured = fullyConfigured;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => SteamFullyConfigured = fullyConfigured));
            }
        }

        private void SetSteamAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                SteamAuthStatus = status;
            }
            else
            {
                // Use BeginInvoke for non-blocking marshal to UI thread
                Dispatcher.BeginInvoke(new Action(() => SteamAuthStatus = status));
            }
        }

        private void SetSteamAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                SteamAuthBusy = busy;
            }
            else
            {
                // Use BeginInvoke for non-blocking marshal to UI thread
                Dispatcher.BeginInvoke(new Action(() => SteamAuthBusy = busy));
            }
        }

        // -----------------------------
        // GOG auth UI
        // -----------------------------

        private async void GogAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[GogAuth] Settings: Check Auth clicked.");
            await CheckGogAuthAsync().ConfigureAwait(false);
        }

        private async void GogAuth_Login_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[GogAuth] Settings: Login clicked.");
            SetGogAuthBusy(true);
            SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_Checking");

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
                {
                    var result = await _gogSessionManager.AuthenticateInteractiveAsync(
                        forceInteractive: true,
                        ct: cts.Token).ConfigureAwait(false);
                    ApplyGogAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetGogAuthenticated(false);
                SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[GogAuth] Login failed.");
                SetGogAuthenticated(false);
                SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_Failed");
            }
            finally
            {
                SetGogAuthBusy(false);
            }
        }

        private void GogAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            _gogSessionManager.ClearSession();
            SetGogAuthenticated(false);
            SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_CookiesCleared");
        }

        private async Task CheckGogAuthAsync()
        {
            SetGogAuthBusy(true);
            SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_Checking");

            // Small delay to ensure user sees the checking feedback
            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var result = await _gogSessionManager.ProbeAuthenticationAsync(timeoutCts.Token).ConfigureAwait(false);
                    ApplyGogAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetGogAuthenticated(false);
                SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "GOG auth check failed.");
                SetGogAuthenticated(false);
                SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_ProbeFailed");
            }
            finally
            {
                SetGogAuthBusy(false);
            }
        }

        private void ApplyGogAuthResult(GogAuthResult result)
        {
            if (result == null)
            {
                _logger?.Debug("[GogAuth] ApplyGogAuthResult: result is null");
                SetGogAuthenticated(false);
                SetGogAuthStatusByKey("LOCPlayAch_Settings_GogAuth_Failed");
                return;
            }

            _logger?.Debug($"[GogAuth] ApplyGogAuthResult: Outcome={result.Outcome}, IsSuccess={result.IsSuccess}, UserId={result.UserId}");

            var authenticated =
                result.Outcome == GogAuthOutcome.Authenticated ||
                result.Outcome == GogAuthOutcome.AlreadyAuthenticated;

            _logger?.Debug($"[GogAuth] ApplyGogAuthResult: authenticated={authenticated}");

            // Set authenticated state
            SetGogAuthenticated(authenticated);

            var statusKey = string.IsNullOrWhiteSpace(result.MessageKey)
                ? GetDefaultMessageKeyForOutcome(result.Outcome)
                : result.MessageKey;

            if (!result.WindowOpened &&
                !result.IsSuccess &&
                (result.Outcome == GogAuthOutcome.Failed || result.Outcome == GogAuthOutcome.Cancelled))
            {
                statusKey = "LOCPlayAch_Settings_GogAuth_WindowNotOpened";
            }

            SetGogAuthStatusByKey(statusKey);
        }

        private static string TryMapProviderAuthKeyToGeneric(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var authMarkerIndex = key.IndexOf("Auth_", StringComparison.Ordinal);
            if (authMarkerIndex < 0)
            {
                return null;
            }

            var suffixStart = authMarkerIndex + "Auth_".Length;
            if (suffixStart >= key.Length)
            {
                return null;
            }

            var suffix = key.Substring(suffixStart);
            switch (suffix)
            {
                case "NotChecked":
                case "Checking":
                case "CheckingExistingSession":
                case "OpeningWindow":
                case "WaitingForLogin":
                case "VerifyingSession":
                case "Completed":
                case "Verified":
                case "NotAuthenticated":
                case "TimedOut":
                case "Failed":
                case "Cancelled":
                case "CookiesCleared":
                case "AlreadyAuthenticated":
                case "ProbeFailed":
                case "WindowNotOpened":
                    return $"LOCPlayAch_Settings_Auth_{suffix}";
                default:
                    return null;
            }
        }

        private static string ResolveAuthStatusValue(string key, string providerResourceKey, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var mappedKey = TryMapProviderAuthKeyToGeneric(key);
            var resourceKey = string.IsNullOrWhiteSpace(mappedKey) ? key : mappedKey;
            var value = ResourceProvider.GetString(resourceKey);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = resourceKey;
            }

            var formatArgs = args;
            if (!string.IsNullOrWhiteSpace(mappedKey))
            {
                var providerName = ResourceProvider.GetString(providerResourceKey);
                if (string.IsNullOrWhiteSpace(providerName))
                {
                    providerName = providerResourceKey;
                }

                if (args == null || args.Length == 0)
                {
                    formatArgs = new object[] { providerName };
                }
                else
                {
                    var combinedArgs = new object[args.Length + 1];
                    combinedArgs[0] = providerName;
                    Array.Copy(args, 0, combinedArgs, 1, args.Length);
                    formatArgs = combinedArgs;
                }
            }

            if (formatArgs != null && formatArgs.Length > 0)
            {
                try
                {
                    return string.Format(value, formatArgs);
                }
                catch
                {
                    return value;
                }
            }

            return value;
        }

        private static string GetDefaultMessageKeyForOutcome(GogAuthOutcome outcome)
        {
            switch (outcome)
            {
                case GogAuthOutcome.Authenticated:
                    return "LOCPlayAch_Settings_Auth_Verified";
                case GogAuthOutcome.AlreadyAuthenticated:
                    return "LOCPlayAch_Settings_Auth_AlreadyAuthenticated";
                case GogAuthOutcome.NotAuthenticated:
                    return "LOCPlayAch_Settings_Auth_NotAuthenticated";
                case GogAuthOutcome.Cancelled:
                    return "LOCPlayAch_Settings_Auth_Cancelled";
                case GogAuthOutcome.TimedOut:
                    return "LOCPlayAch_Settings_Auth_TimedOut";
                case GogAuthOutcome.ProbeFailed:
                    return "LOCPlayAch_Settings_Auth_ProbeFailed";
                case GogAuthOutcome.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private static string GetGogProgressMessageKey(GogAuthProgressStep step)
        {
            switch (step)
            {
                case GogAuthProgressStep.CheckingExistingSession:
                    return "LOCPlayAch_Settings_Auth_CheckingExistingSession";
                case GogAuthProgressStep.OpeningLoginWindow:
                    return "LOCPlayAch_Settings_Auth_OpeningWindow";
                case GogAuthProgressStep.WaitingForUserLogin:
                    return "LOCPlayAch_Settings_Auth_WaitingForLogin";
                case GogAuthProgressStep.VerifyingSession:
                    return "LOCPlayAch_Settings_Auth_VerifyingSession";
                case GogAuthProgressStep.Completed:
                    return "LOCPlayAch_Settings_Auth_Completed";
                case GogAuthProgressStep.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private void SetGogAuthStatusByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var value = ResolveAuthStatusValue(key, "LOCPlayAch_Provider_GOG");
            SetGogAuthStatus(string.IsNullOrWhiteSpace(value) ? key : value);
        }

        private void SetGogAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                GogAuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => GogAuthStatus = status));
            }
        }

        private void SetGogAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                GogAuthBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => GogAuthBusy = busy));
            }
        }

        private void SetGogAuthenticated(bool authenticated)
        {
            _logger?.Debug($"[GogAuth] SetGogAuthenticated: value={authenticated}, CheckAccess={Dispatcher.CheckAccess()}");
            if (Dispatcher.CheckAccess())
            {
                GogAuthenticated = authenticated;
                _logger?.Debug($"[GogAuth] SetGogAuthenticated: set directly, GogAuthenticated={GogAuthenticated}");
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    GogAuthenticated = authenticated;
                    _logger?.Debug($"[GogAuth] SetGogAuthenticated: set via Dispatcher, GogAuthenticated={GogAuthenticated}");
                }));
            }
        }

        // -----------------------------
        // Epic auth UI
        // -----------------------------

        private async void EpicAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[EpicAuth] Settings: Check Auth clicked.");
            await CheckEpicAuthAsync().ConfigureAwait(false);
        }

        private async void EpicAuth_Login_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[EpicAuth] Settings: Login clicked.");
            SetEpicAuthBusy(true);
            SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_Checking");

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
                {
                    var result = await _epicSessionManager.AuthenticateInteractiveAsync(
                        forceInteractive: true,
                        ct: cts.Token).ConfigureAwait(false);
                    ApplyEpicAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetEpicAuthenticated(false);
                SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[EpicAuth] Login failed.");
                SetEpicAuthenticated(false);
                SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_Failed");
            }
            finally
            {
                SetEpicAuthBusy(false);
            }
        }

        private async void EpicAuth_LoginAlternative_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[EpicAuth] Settings: Alternative Login clicked.");
            SetEpicAuthBusy(true);
            SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_Checking");

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
                {
                    var result = await _epicSessionManager.LoginAlternativeAsync(cts.Token).ConfigureAwait(false);
                    ApplyEpicAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetEpicAuthenticated(false);
                SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[EpicAuth] Alternative login failed.");
                SetEpicAuthenticated(false);
                SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_Failed");
            }
            finally
            {
                SetEpicAuthBusy(false);
            }
        }

        private void EpicAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            _epicSessionManager.ClearSession();
            SetEpicAuthenticated(false);
            SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_CookiesCleared");
        }

        private async Task CheckEpicAuthAsync()
        {
            SetEpicAuthBusy(true);
            SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_Checking");

            // Small delay to ensure user sees the checking feedback
            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var result = await _epicSessionManager.ProbeAuthenticationAsync(timeoutCts.Token).ConfigureAwait(false);
                    ApplyEpicAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetEpicAuthenticated(false);
                SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Epic auth check failed.");
                SetEpicAuthenticated(false);
                SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_ProbeFailed");
            }
            finally
            {
                SetEpicAuthBusy(false);
            }
        }

        private void ApplyEpicAuthResult(EpicAuthResult result)
        {
            if (result == null)
            {
                SetEpicAuthenticated(false);
                SetEpicAuthStatusByKey("LOCPlayAch_Settings_EpicAuth_Failed");
                return;
            }

            var authenticated =
                result.Outcome == EpicAuthOutcome.Authenticated ||
                result.Outcome == EpicAuthOutcome.AlreadyAuthenticated;

            SetEpicAuthenticated(authenticated);

            var statusKey = string.IsNullOrWhiteSpace(result.MessageKey)
                ? GetDefaultEpicMessageKeyForOutcome(result.Outcome)
                : result.MessageKey;

            if (!result.WindowOpened &&
                !result.IsSuccess &&
                (result.Outcome == EpicAuthOutcome.Failed || result.Outcome == EpicAuthOutcome.Cancelled))
            {
                statusKey = "LOCPlayAch_Settings_EpicAuth_WindowNotOpened";
            }

            SetEpicAuthStatusByKey(statusKey);
        }

        private static string GetDefaultEpicMessageKeyForOutcome(EpicAuthOutcome outcome)
        {
            switch (outcome)
            {
                case EpicAuthOutcome.Authenticated:
                    return "LOCPlayAch_Settings_Auth_Verified";
                case EpicAuthOutcome.AlreadyAuthenticated:
                    return "LOCPlayAch_Settings_Auth_AlreadyAuthenticated";
                case EpicAuthOutcome.NotAuthenticated:
                    return "LOCPlayAch_Settings_Auth_NotAuthenticated";
                case EpicAuthOutcome.Cancelled:
                    return "LOCPlayAch_Settings_Auth_Cancelled";
                case EpicAuthOutcome.TimedOut:
                    return "LOCPlayAch_Settings_Auth_TimedOut";
                case EpicAuthOutcome.ProbeFailed:
                    return "LOCPlayAch_Settings_Auth_ProbeFailed";
                case EpicAuthOutcome.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private static string GetEpicProgressMessageKey(EpicAuthProgressStep step)
        {
            switch (step)
            {
                case EpicAuthProgressStep.CheckingExistingSession:
                    return "LOCPlayAch_Settings_Auth_CheckingExistingSession";
                case EpicAuthProgressStep.OpeningLoginWindow:
                    return "LOCPlayAch_Settings_Auth_OpeningWindow";
                case EpicAuthProgressStep.WaitingForUserLogin:
                    return "LOCPlayAch_Settings_Auth_WaitingForLogin";
                case EpicAuthProgressStep.VerifyingSession:
                    return "LOCPlayAch_Settings_Auth_VerifyingSession";
                case EpicAuthProgressStep.Completed:
                    return "LOCPlayAch_Settings_Auth_Completed";
                case EpicAuthProgressStep.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private void SetEpicAuthStatusByKey(string key, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var value = ResolveAuthStatusValue(key, "LOCPlayAch_Provider_Epic", args);
            SetEpicAuthStatus(string.IsNullOrWhiteSpace(value) ? key : value);
        }

        private void SetEpicAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                EpicAuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => EpicAuthStatus = status));
            }
        }

        private void SetEpicAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                EpicAuthBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => EpicAuthBusy = busy));
            }
        }

        private void SetEpicAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                EpicAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => EpicAuthenticated = authenticated));
            }
        }

        // -----------------------------
        // PSN auth UI
        // -----------------------------

        private async void PsnNpsso_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await CheckPsnAuthAsync().ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(new Action(() => MoveFocusFrom((TextBox)sender)));
            }
        }

        private async void PsnNpsso_LostFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            await CheckPsnAuthAsync().ConfigureAwait(false);
        }

        private async void PsnAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[PSNAch] Settings: Check Auth clicked.");
            await CheckPsnAuthAsync().ConfigureAwait(false);
        }

        private async void PsnAuth_Login_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[PSNAch] Settings: Login clicked.");
            SetPsnAuthBusy(true);
            SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_Checking");

            var progress = new Progress<PsnAuthProgressStep>(step =>
            {
                SetPsnAuthStatusByKey(GetPsnProgressMessageKey(step));
            });

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180)))
                {
                    var result = await _psnSessionManager
                        .AuthenticateInteractiveAsync(forceInteractive: true, ct: cts.Token, progress: progress)
                        .ConfigureAwait(false);
                    ApplyPsnAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetPsnAuthenticated(false);
                SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Login failed.");
                SetPsnAuthenticated(false);
                SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_Failed");
            }
            finally
            {
                SetPsnAuthBusy(false);
            }
        }

        private void PsnAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            _psnSessionManager.ClearSession();
            SetPsnAuthenticated(false);
            SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_CookiesCleared");
        }

        private async Task CheckPsnAuthAsync()
        {
            SetPsnAuthBusy(true);
            SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_Checking");

            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var result = await _psnSessionManager.ProbeAuthenticationAsync(timeoutCts.Token).ConfigureAwait(false);
                    ApplyPsnAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetPsnAuthenticated(false);
                SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "PSN auth check failed.");
                SetPsnAuthenticated(false);
                SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_ProbeFailed");
            }
            finally
            {
                SetPsnAuthBusy(false);
            }
        }

        private void ApplyPsnAuthResult(PsnAuthResult result)
        {
            if (result == null)
            {
                SetPsnAuthenticated(false);
                SetPsnAuthStatusByKey("LOCPlayAch_Settings_PsnAuth_Failed");
                return;
            }

            var authenticated =
                result.Outcome == PsnAuthOutcome.Authenticated ||
                result.Outcome == PsnAuthOutcome.AlreadyAuthenticated;

            SetPsnAuthenticated(authenticated);

            var statusKey = string.IsNullOrWhiteSpace(result.MessageKey)
                ? GetDefaultPsnMessageKeyForOutcome(result.Outcome)
                : result.MessageKey;

            if (!result.WindowOpened &&
                !result.IsSuccess &&
                (result.Outcome == PsnAuthOutcome.Failed || result.Outcome == PsnAuthOutcome.Cancelled))
            {
                statusKey = "LOCPlayAch_Settings_PsnAuth_WindowNotOpened";
            }

            SetPsnAuthStatusByKey(statusKey);
        }

        private static string GetDefaultPsnMessageKeyForOutcome(PsnAuthOutcome outcome)
        {
            switch (outcome)
            {
                case PsnAuthOutcome.Authenticated:
                    return "LOCPlayAch_Settings_Auth_Verified";
                case PsnAuthOutcome.AlreadyAuthenticated:
                    return "LOCPlayAch_Settings_Auth_AlreadyAuthenticated";
                case PsnAuthOutcome.NotAuthenticated:
                    return "LOCPlayAch_Settings_Auth_NotAuthenticated";
                case PsnAuthOutcome.Cancelled:
                    return "LOCPlayAch_Settings_Auth_Cancelled";
                case PsnAuthOutcome.TimedOut:
                    return "LOCPlayAch_Settings_Auth_TimedOut";
                case PsnAuthOutcome.ProbeFailed:
                    return "LOCPlayAch_Settings_Auth_ProbeFailed";
                case PsnAuthOutcome.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private static string GetPsnProgressMessageKey(PsnAuthProgressStep step)
        {
            switch (step)
            {
                case PsnAuthProgressStep.CheckingExistingSession:
                    return "LOCPlayAch_Settings_Auth_CheckingExistingSession";
                case PsnAuthProgressStep.OpeningLoginWindow:
                    return "LOCPlayAch_Settings_Auth_OpeningWindow";
                case PsnAuthProgressStep.WaitingForUserLogin:
                    return "LOCPlayAch_Settings_Auth_WaitingForLogin";
                case PsnAuthProgressStep.Completed:
                    return "LOCPlayAch_Settings_Auth_Completed";
                case PsnAuthProgressStep.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private void SetPsnAuthStatusByKey(string key, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var value = ResolveAuthStatusValue(key, "LOCPlayAch_Provider_PSN", args);
            SetPsnAuthStatus(string.IsNullOrWhiteSpace(value) ? key : value);
        }

        private void SetPsnAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                PsnAuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => PsnAuthStatus = status));
            }
        }

        private void SetPsnAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                PsnAuthBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => PsnAuthBusy = busy));
            }
        }

        private void SetPsnAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                PsnAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => PsnAuthenticated = authenticated));
            }
        }

        // -----------------------------
        // Xbox auth UI
        // -----------------------------

        private async void XboxAuth_Login_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[XboxAch] Settings: Login clicked.");
            SetXboxAuthBusy(true);
            SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_Checking");

            var progress = new Progress<XboxAuthProgressStep>(step =>
            {
                SetXboxAuthStatusByKey(GetXboxProgressMessageKey(step));
            });

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180)))
                {
                    var result = await _xboxSessionManager
                        .AuthenticateInteractiveAsync(forceInteractive: true, ct: cts.Token, progress: progress)
                        .ConfigureAwait(false);
                    ApplyXboxAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetXboxAuthenticated(false);
                SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[XboxAch] Login failed.");
                SetXboxAuthenticated(false);
                SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_Failed");
            }
            finally
            {
                SetXboxAuthBusy(false);
            }
        }

        private void XboxAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            _xboxSessionManager.ClearSession();
            SetXboxAuthenticated(false);
            SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_CookiesCleared");
        }

        private async Task CheckXboxAuthAsync()
        {
            SetXboxAuthBusy(true);
            SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_Checking");

            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var result = await _xboxSessionManager.ProbeAuthenticationAsync(timeoutCts.Token).ConfigureAwait(false);
                    ApplyXboxAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetXboxAuthenticated(false);
                SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Xbox auth check failed.");
                SetXboxAuthenticated(false);
                SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_ProbeFailed");
            }
            finally
            {
                SetXboxAuthBusy(false);
            }
        }

        private void ApplyXboxAuthResult(XboxAuthResult result)
        {
            if (result == null)
            {
                SetXboxAuthenticated(false);
                SetXboxAuthStatusByKey("LOCPlayAch_Settings_XboxAuth_Failed");
                return;
            }

            var authenticated =
                result.Outcome == XboxAuthOutcome.Authenticated ||
                result.Outcome == XboxAuthOutcome.AlreadyAuthenticated;

            SetXboxAuthenticated(authenticated);

            var statusKey = string.IsNullOrWhiteSpace(result.MessageKey)
                ? GetDefaultXboxMessageKeyForOutcome(result.Outcome)
                : result.MessageKey;

            if (!result.WindowOpened &&
                (result.Outcome == XboxAuthOutcome.Failed || result.Outcome == XboxAuthOutcome.Cancelled))
            {
                statusKey = "LOCPlayAch_Settings_XboxAuth_WindowNotOpened";
            }

            SetXboxAuthStatusByKey(statusKey);
        }

        private static string GetDefaultXboxMessageKeyForOutcome(XboxAuthOutcome outcome)
        {
            switch (outcome)
            {
                case XboxAuthOutcome.Authenticated:
                    return "LOCPlayAch_Settings_Auth_Verified";
                case XboxAuthOutcome.AlreadyAuthenticated:
                    return "LOCPlayAch_Settings_Auth_AlreadyAuthenticated";
                case XboxAuthOutcome.NotAuthenticated:
                    return "LOCPlayAch_Settings_Auth_NotAuthenticated";
                case XboxAuthOutcome.Cancelled:
                    return "LOCPlayAch_Settings_Auth_Cancelled";
                case XboxAuthOutcome.TimedOut:
                    return "LOCPlayAch_Settings_Auth_TimedOut";
                case XboxAuthOutcome.ProbeFailed:
                    return "LOCPlayAch_Settings_Auth_ProbeFailed";
                case XboxAuthOutcome.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private static string GetXboxProgressMessageKey(XboxAuthProgressStep step)
        {
            switch (step)
            {
                case XboxAuthProgressStep.CheckingExistingSession:
                    return "LOCPlayAch_Settings_Auth_CheckingExistingSession";
                case XboxAuthProgressStep.OpeningLoginWindow:
                    return "LOCPlayAch_Settings_Auth_OpeningWindow";
                case XboxAuthProgressStep.WaitingForUserLogin:
                    return "LOCPlayAch_Settings_Auth_WaitingForLogin";
                case XboxAuthProgressStep.Completed:
                    return "LOCPlayAch_Settings_Auth_Completed";
                case XboxAuthProgressStep.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private void SetXboxAuthStatusByKey(string key, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var value = ResolveAuthStatusValue(key, "LOCPlayAch_Provider_Xbox", args);
            SetXboxAuthStatus(string.IsNullOrWhiteSpace(value) ? key : value);
        }

        private void SetXboxAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                XboxAuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => XboxAuthStatus = status));
            }
        }

        private void SetXboxAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                XboxAuthBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => XboxAuthBusy = busy));
            }
        }

        private void SetXboxAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                XboxAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => XboxAuthenticated = authenticated));
            }
        }

        // -----------------------------
        // Exophase auth UI
        // -----------------------------

        private async void ExophaseAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[ExophaseAuth] Settings: Check Auth clicked.");
            await CheckExophaseAuthAsync().ConfigureAwait(false);
        }

        private async void ExophaseAuth_Login_Click(object sender, RoutedEventArgs e)
        {
            _logger?.Info("[ExophaseAuth] Settings: Login clicked.");
            SetExophaseAuthBusy(true);
            SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_Checking");

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180)))
                {
                    var progress = new Progress<ExophaseAuthProgressStep>(step =>
                    {
                        var msgKey = GetExophaseProgressMessageKey(step);
                        SetExophaseAuthStatusByKey(msgKey);
                    });

                    var result = await _exophaseSessionManager.AuthenticateInteractiveAsync(
                        forceInteractive: true,
                        ct: cts.Token,
                        progress: progress).ConfigureAwait(false);
                    ApplyExophaseAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetExophaseAuthenticated(false);
                SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ExophaseAuth] Login failed.");
                SetExophaseAuthenticated(false);
                SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_Failed");
            }
            finally
            {
                SetExophaseAuthBusy(false);
            }
        }

        private void ExophaseAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            _exophaseSessionManager.ClearSession();
            SetExophaseAuthenticated(false);
            SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_NotAuthenticated");
        }

        private async Task CheckExophaseAuthAsync()
        {
            SetExophaseAuthBusy(true);
            SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_Checking");

            // Small delay to ensure user sees the checking feedback
            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var result = await _exophaseSessionManager.ProbeAuthenticationAsync(timeoutCts.Token).ConfigureAwait(false);
                    ApplyExophaseAuthResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                SetExophaseAuthenticated(false);
                SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_TimedOut");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Exophase auth check failed.");
                SetExophaseAuthenticated(false);
                SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_ProbeFailed");
            }
            finally
            {
                SetExophaseAuthBusy(false);
            }
        }

        private void ApplyExophaseAuthResult(ExophaseAuthResult result)
        {
            if (result == null)
            {
                _logger?.Debug("[ExophaseAuth] ApplyExophaseAuthResult: result is null");
                SetExophaseAuthenticated(false);
                SetExophaseAuthStatusByKey("LOCPlayAch_Settings_ExophaseAuth_Failed");
                return;
            }

            _logger?.Debug($"[ExophaseAuth] ApplyExophaseAuthResult: Outcome={result.Outcome}, IsSuccess={result.IsSuccess}, Username={result.Username}");

            var authenticated =
                result.Outcome == ExophaseAuthOutcome.Authenticated ||
                result.Outcome == ExophaseAuthOutcome.AlreadyAuthenticated;

            _logger?.Debug($"[ExophaseAuth] ApplyExophaseAuthResult: authenticated={authenticated}");

            // Set authenticated state
            SetExophaseAuthenticated(authenticated);

            var statusKey = string.IsNullOrWhiteSpace(result.MessageKey)
                ? GetDefaultExophaseMessageKeyForOutcome(result.Outcome)
                : result.MessageKey;

            if (!result.WindowOpened &&
                !result.IsSuccess &&
                (result.Outcome == ExophaseAuthOutcome.Failed || result.Outcome == ExophaseAuthOutcome.Cancelled))
            {
                statusKey = "LOCPlayAch_Settings_ExophaseAuth_WindowNotOpened";
            }

            SetExophaseAuthStatusByKey(statusKey);
        }

        private static string GetDefaultExophaseMessageKeyForOutcome(ExophaseAuthOutcome outcome)
        {
            switch (outcome)
            {
                case ExophaseAuthOutcome.Authenticated:
                    return "LOCPlayAch_Settings_Auth_Verified";
                case ExophaseAuthOutcome.AlreadyAuthenticated:
                    return "LOCPlayAch_Settings_Auth_AlreadyAuthenticated";
                case ExophaseAuthOutcome.NotAuthenticated:
                    return "LOCPlayAch_Settings_Auth_NotAuthenticated";
                case ExophaseAuthOutcome.Cancelled:
                    return "LOCPlayAch_Settings_Auth_Cancelled";
                case ExophaseAuthOutcome.TimedOut:
                    return "LOCPlayAch_Settings_Auth_TimedOut";
                case ExophaseAuthOutcome.ProbeFailed:
                    return "LOCPlayAch_Settings_Auth_ProbeFailed";
                case ExophaseAuthOutcome.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private static string GetExophaseProgressMessageKey(ExophaseAuthProgressStep step)
        {
            switch (step)
            {
                case ExophaseAuthProgressStep.CheckingExistingSession:
                    return "LOCPlayAch_Settings_Auth_CheckingExistingSession";
                case ExophaseAuthProgressStep.OpeningLoginWindow:
                    return "LOCPlayAch_Settings_Auth_OpeningWindow";
                case ExophaseAuthProgressStep.WaitingForUserLogin:
                    return "LOCPlayAch_Settings_Auth_WaitingForLogin";
                case ExophaseAuthProgressStep.VerifyingSession:
                    return "LOCPlayAch_Settings_Auth_VerifyingSession";
                case ExophaseAuthProgressStep.Completed:
                    return "LOCPlayAch_Settings_Auth_Completed";
                case ExophaseAuthProgressStep.Failed:
                default:
                    return "LOCPlayAch_Settings_Auth_Failed";
            }
        }

        private void SetExophaseAuthStatusByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var value = ResolveAuthStatusValue(key, "LOCPlayAch_Provider_Exophase");
            SetExophaseAuthStatus(string.IsNullOrWhiteSpace(value) ? key : value);
        }

        private void SetExophaseAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                ExophaseAuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => ExophaseAuthStatus = status));
            }
        }

        private void SetExophaseAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                ExophaseAuthBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => ExophaseAuthBusy = busy));
            }
        }

        private void SetExophaseAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                ExophaseAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => ExophaseAuthenticated = authenticated));
            }
        }

        // -----------------------------
        // Legacy Manual Import actions
        // -----------------------------

        private void EnsureLegacyManualImportPathDefault()
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null || !string.IsNullOrWhiteSpace(persisted.LegacyManualImportPath))
            {
                return;
            }

            var extensionsDataPath = _plugin?.PlayniteApi?.Paths?.ExtensionsDataPath;
            if (string.IsNullOrWhiteSpace(extensionsDataPath))
            {
                return;
            }

            persisted.LegacyManualImportPath = Path.Combine(
                extensionsDataPath,
                SuccessStoryExtensionId,
                SuccessStoryFolderName);
        }

        private void ManualLegacyBrowse_Click(object sender, RoutedEventArgs e)
        {
            EnsureLegacyManualImportPathDefault();

            var selectedPath = _plugin.PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            _settingsViewModel.Settings.Persisted.LegacyManualImportPath = selectedPath;
            _plugin.SavePluginSettings(_settingsViewModel.Settings);
            _plugin.ProviderRegistry?.SyncFromSettings(_settingsViewModel.Settings.Persisted);
            PlayniteAchievementsPlugin.NotifySettingsSaved();
            SetLegacyManualImportStatus(L(
                "LOCPlayAch_Settings_Manual_Legacy_StatusPathUpdated",
                "Import folder updated."));
        }

        private async void ManualLegacyImport_Click(object sender, RoutedEventArgs e)
        {
            if (LegacyManualImportBusy)
            {
                return;
            }

            EnsureLegacyManualImportPathDefault();
            var importPath = _settingsViewModel?.Settings?.Persisted?.LegacyManualImportPath;
            if (string.IsNullOrWhiteSpace(importPath) || !Directory.Exists(importPath))
            {
                var invalidPathMessage = LF(
                    "LOCPlayAch_Settings_Manual_Legacy_PathInvalid",
                    "Legacy folder not found: {0}",
                    importPath ?? string.Empty);

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    invalidPathMessage,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                SetLegacyManualImportStatus(invalidPathMessage);
                return;
            }

            SetLegacyManualImportBusy(true);
            SetLegacyManualImportStatus(L(
                "LOCPlayAch_Settings_Manual_Legacy_StatusRunning",
                "Importing Legacy manual links..."));

            LegacyManualImportResult importResult;
            try
            {
                importResult = _plugin.ImportLegacyManualLinks(importPath);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Legacy manual import failed.");
                var failureMessage = LF(
                    "LOCPlayAch_Settings_Manual_Legacy_ImportFailed",
                    "Legacy import failed: {0}",
                    ex.Message);

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    failureMessage,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                SetLegacyManualImportStatus(failureMessage);
                SetLegacyManualImportBusy(false);
                return;
            }
            finally
            {
                SetLegacyManualImportBusy(false);
            }

            var autoRefreshStarted = false;
            var autoRefreshSkipped = false;

            if (importResult.ImportedGameIds.Count > 0)
            {
                if (_plugin.AchievementService.IsRebuilding)
                {
                    autoRefreshSkipped = true;
                }
                else
                {
                    var refreshGameIds = importResult.ImportedGameIds
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();

                    if (refreshGameIds.Count > 0)
                    {
                        var progressSingleGameId = refreshGameIds.Count == 1
                            ? (Guid?)refreshGameIds[0]
                            : null;

                        await _plugin.RefreshCoordinator.ExecuteAsync(
                            new RefreshRequest
                            {
                                GameIds = refreshGameIds
                            },
                            RefreshExecutionPolicy.ProgressWindow(progressSingleGameId));

                        autoRefreshStarted = true;
                    }
                }
            }

            var summary = BuildLegacyManualImportSummary(
                importResult,
                autoRefreshStarted,
                autoRefreshSkipped);

            SetLegacyManualImportStatus(summary);
        }

        private string BuildLegacyManualImportSummary(
            LegacyManualImportResult result,
            bool autoRefreshStarted,
            bool autoRefreshSkipped)
        {
            var lines = new List<string>
            {
                L("LOCPlayAch_Settings_Manual_Legacy_SummaryHeader", "Legacy import complete."),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummaryScanned", "Scanned: {0}", result.Scanned),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummaryImported", "Imported: {0}", result.Imported),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummaryParseFailed", "Parse failures: {0}", result.ParseFailures),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipNotManual", "Skipped (not manual): {0}", result.SkippedNotManual),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipIgnored", "Skipped (ignored): {0}", result.SkippedIgnored),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipInvalidFile", "Skipped (invalid file name): {0}", result.SkippedInvalidFileName),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipMissingGame", "Skipped (game not found): {0}", result.SkippedGameMissing),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipManualExists", "Skipped (manual link exists): {0}", result.SkippedManualLinkExists),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipCachedData", "Skipped (cached provider data exists): {0}", result.SkippedCachedProviderData),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipUnsupportedSource", "Skipped (unsupported source): {0}", result.SkippedUnsupportedSource),
                LF("LOCPlayAch_Settings_Manual_Legacy_SummarySkipUnresolvedId", "Skipped (source game id unresolved): {0}", result.SkippedUnresolvedSourceGameId)
            };

            if (result.ManualProviderAutoEnabled)
            {
                lines.Add(L(
                    "LOCPlayAch_Settings_Manual_Legacy_ManualProviderAutoEnabled",
                    "Manual provider was enabled automatically."));
            }

            if (result.UnsupportedSources.Count > 0)
            {
                lines.Add(L(
                    "LOCPlayAch_Settings_Manual_Legacy_UnsupportedSourcesHeader",
                    "Unsupported source breakdown:"));

                foreach (var pair in result.UnsupportedSources.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"- {pair.Key}: {pair.Value}");
                }
            }

            if (autoRefreshSkipped)
            {
                lines.Add(L(
                    "LOCPlayAch_Settings_Manual_Legacy_AutoRefreshSkippedRunning",
                    "Refresh already running; auto-refresh skipped."));
            }
            else if (autoRefreshStarted)
            {
                lines.Add(L(
                    "LOCPlayAch_Settings_Manual_Legacy_AutoRefreshStarted",
                    "Auto-refresh started for imported games."));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private void SetLegacyManualImportStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                LegacyManualImportStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => LegacyManualImportStatus = status));
            }
        }

        private void SetLegacyManualImportBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                LegacyManualImportBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => LegacyManualImportBusy = busy));
            }
        }

        // -----------------------------
        // ShadPS4 actions
        // -----------------------------

        private void ShadPS4_Browse_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _plugin.PlayniteApi.Dialogs.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _settingsViewModel.Settings.Persisted.ShadPS4GameDataPath = selectedPath;
                CheckShadPS4Auth();
            }
        }

        // -----------------------------
        // RPCS3 actions
        // -----------------------------

        private void Rpcs3_Browse_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _plugin.PlayniteApi.Dialogs.SelectFile("rpcs3.exe|rpcs3.exe|Executable files|*.exe");
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _settingsViewModel.Settings.Persisted.Rpcs3ExecutablePath = selectedPath;

                // Check if the selected file is valid
                CheckRpcs3Auth();
            }
        }

        private void CheckShadPS4Auth()
        {
            var gameDataPath = _settingsViewModel.Settings?.Persisted?.ShadPS4GameDataPath;

            if (string.IsNullOrWhiteSpace(gameDataPath))
            {
                SetShadPS4Authenticated(false);
                SetShadPS4AuthStatusByKey("LOCPlayAch_Settings_ShadPS4_NotConfigured");
                return;
            }

            if (System.IO.Directory.Exists(gameDataPath))
            {
                SetShadPS4Authenticated(true);
                SetShadPS4AuthStatusByKey("LOCPlayAch_Settings_ShadPS4_Verified");
            }
            else
            {
                SetShadPS4Authenticated(false);
                SetShadPS4AuthStatusByKey("LOCPlayAch_Settings_ShadPS4_FolderNotFound");
            }
        }

        private void SetShadPS4AuthStatusByKey(string key)
        {
            var value = ResourceProvider.GetString(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetShadPS4AuthStatus(value);
            }
        }

        private void SetShadPS4AuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                ShadPS4AuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => ShadPS4AuthStatus = status));
            }
        }

        private void SetShadPS4Authenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                ShadPS4Authenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => ShadPS4Authenticated = authenticated));
            }
        }

        private void CheckRpcs3Auth()
        {
            var exePath = _settingsViewModel.Settings?.Persisted?.Rpcs3ExecutablePath;

            if (string.IsNullOrWhiteSpace(exePath))
            {
                SetRpcs3Authenticated(false);
                SetRpcs3AuthStatusByKey("LOCPlayAch_Settings_Rpcs3_NotConfigured");
                return;
            }

            // Derive installation folder from executable path
            var installFolder = System.IO.Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(installFolder))
            {
                SetRpcs3Authenticated(false);
                SetRpcs3AuthStatusByKey("LOCPlayAch_Settings_Rpcs3_NotConfigured");
                return;
            }

            // Validate RPCS3 installation folder structure
            // Expected structure: {installFolder}\dev_hdd0\home\{userId}\trophy
            if (!System.IO.Directory.Exists(installFolder))
            {
                SetRpcs3Authenticated(false);
                SetRpcs3AuthStatusByKey("LOCPlayAch_Rpcs3Validation_InvalidPath");
                return;
            }

            var homePath = System.IO.Path.Combine(installFolder, "dev_hdd0", "home");
            if (!System.IO.Directory.Exists(homePath))
            {
                SetRpcs3Authenticated(false);
                SetRpcs3AuthStatusByKey("LOCPlayAch_Rpcs3Validation_NotRpcs3");
                return;
            }

            // Find user ID (8-digit numeric directory)
            string userId = null;
            try
            {
                foreach (var dir in System.IO.Directory.GetDirectories(homePath))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(name) && name.Length == 8 && name.All(char.IsDigit))
                    {
                        userId = name;
                        break;
                    }
                }
            }
            catch { /* ignore */ }

            if (string.IsNullOrWhiteSpace(userId))
            {
                SetRpcs3Authenticated(false);
                SetRpcs3AuthStatusByKey("LOCPlayAch_Rpcs3Validation_NoUser");
                return;
            }

            var trophyPath = System.IO.Path.Combine(homePath, userId, "trophy");
            if (!System.IO.Directory.Exists(trophyPath))
            {
                SetRpcs3Authenticated(false);
                SetRpcs3AuthStatusByKey("LOCPlayAch_Rpcs3Validation_NoTrophyFolder");
                return;
            }

            // Count trophy folders for success message
            var trophyCount = 0;
            try
            {
                trophyCount = System.IO.Directory.GetDirectories(trophyPath)
                    .Count(d => System.IO.File.Exists(System.IO.Path.Combine(d, "TROPCONF.SFM")));
            }
            catch { /* ignore */ }

            SetRpcs3Authenticated(true);
            var successMsg = ResourceProvider.GetString("LOCPlayAch_Rpcs3Validation_Success");
            if (!string.IsNullOrWhiteSpace(successMsg))
            {
                SetRpcs3AuthStatus(string.Format(successMsg, trophyCount));
            }
            else
            {
                SetRpcs3AuthStatus($"Valid - Found {trophyCount} trophy folders");
            }
        }

        private void SetRpcs3AuthStatusByKey(string key)
        {
            var value = ResourceProvider.GetString(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetRpcs3AuthStatus(value);
            }
        }

        private void SetRpcs3AuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                Rpcs3AuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => Rpcs3AuthStatus = status));
            }
        }

        private void SetRpcs3Authenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                Rpcs3Authenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => Rpcs3Authenticated = authenticated));
            }
        }

        // -----------------------------
        // Cache actions
        // -----------------------------

        private void WipeCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _plugin.AchievementService.Cache.ClearCache();
                var stillPresent = _plugin.AchievementService.Cache.CacheFileExists();
                
                var (msg, img) = !stillPresent
                    ? (ResourceProvider.GetString("LOCPlayAch_Settings_Cache_Wiped"), MessageBoxImage.Information)
                    : (ResourceProvider.GetString("LOCPlayAch_Settings_Cache_WipeFailed"), MessageBoxImage.Error);
                
                _plugin.PlayniteApi.Dialogs.ShowMessage(msg, ResourceProvider.GetString("LOCPlayAch_Title_PluginName"), MessageBoxButton.OK, img);
            }
            catch (Exception ex)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Settings_Cache_WipeFailedWithError", "Failed to wipe cache: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ClearImageCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _plugin.ImageService?.ClearDiskCache();
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Settings_ImageCache_Cleared"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Settings_ImageCache_ClearFailed") + ": " + ex.Message,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ResetFirstTimeSetup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger.Info($"Resetting FirstTimeSetupCompleted. Current value before: {_settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted}");

                _settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted = false;

                _logger.Info($"Value after setting to false: {_settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted}");

                _plugin.SavePluginSettings(_settingsViewModel.Settings);

                _logger.Info("Settings saved. Verifying save...");

                // Verify the save worked by re-loading
                var reloaded = _plugin.LoadPluginSettings<PlayniteAchievementsSettings>();
                var reloadedValue = reloaded?.Persisted.FirstTimeSetupCompleted ?? true;
                _logger.Info($"Value after reload: {reloadedValue}");

                if (!reloadedValue)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_Settings_ResetFirstTimeSetupDone", "First-time setup has been reset. Close and reopen the sidebar to see the landing page."),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_Settings_ResetFirstTimeSetupVerifyFailed", "Failed to verify reset. The settings may not have been saved correctly."),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reset first-time setup.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Settings_ResetFirstTimeSetupFailed", "Failed to reset first-time setup: {0}", ex.Message),
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
                var cache = _plugin.AchievementService?.Cache;

                if (cache == null)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_Settings_ExportDatabaseFailed", "Database not available."),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var exportDir = cache.ExportDatabaseToCsv(exportBaseDir);

                _logger.Info($"Database exported to: {exportDir}");

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Settings_ExportDatabaseDone", "Database exported to:\n{0}", exportDir),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to export database.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Settings_ExportDatabaseFailed", "Failed to export database: {0}", ex.Message),
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
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_OpenDataFolderFailed"), ex.Message),
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
                        L("LOCPlayAch_Settings_HashIndex_NoCacheDir", "No RetroAchievements cache directory found."),
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

                if (deletedCount > 0)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        LF("LOCPlayAch_Settings_HashIndex_DeletedCount", "Deleted {0} hash index cache file(s).{1}{1}The hash index will be rebuilt on the next refresh.", deletedCount, Environment.NewLine),
                        L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        LF("LOCPlayAch_Settings_HashIndex_NoFiles", "No hash index cache files found to delete.{0}{0}The cache may have already been cleared, or no refreshes have been performed yet.", Environment.NewLine),
                        L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to force hash index rebuild.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Settings_HashIndex_ClearFailed", "Failed to clear hash index cache: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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
                    LF("LOCPlayAch_Tagging_SyncFailed", "Failed to apply and sync tags: {0}", ex.Message),
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
                    LF("LOCPlayAch_Tagging_RemoveFailed", "Failed to remove tags: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tc = sender as TabControl;
            if (tc == null) return;

            // Inspect the newly selected TabItem by name (uses x:Name from XAML)
            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (e.AddedItems[0] is not TabItem selected) return;

            var name = selected.Name ?? string.Empty;
            if (string.Equals(name, "SteamTab", StringComparison.OrdinalIgnoreCase))
            {
                await CheckSteamAuthAsync().ConfigureAwait(false);
                _logger?.Info("Checked Steam auth for Steam tab.");
            }
            else if (string.Equals(name, "GogTab", StringComparison.OrdinalIgnoreCase))
            {
                await CheckGogAuthAsync().ConfigureAwait(false);
                _logger?.Info("Checked GOG auth for GOG tab.");
            }
            else if (string.Equals(name, "EpicTab", StringComparison.OrdinalIgnoreCase))
            {
                await CheckEpicAuthAsync().ConfigureAwait(false);
                _logger?.Info("Checked Epic auth for Epic tab.");
            }
            else if (string.Equals(name, "PsnTab", StringComparison.OrdinalIgnoreCase))
            {
                await CheckPsnAuthAsync().ConfigureAwait(false);
                _logger?.Info("Checked PSN auth for PSN tab.");
            }
            else if (string.Equals(name, "XboxTab", StringComparison.OrdinalIgnoreCase))
            {
                await CheckXboxAuthAsync().ConfigureAwait(false);
                _logger?.Info("Checked Xbox auth for Xbox tab.");
            }
            else if (string.Equals(name, "ExophaseTab", StringComparison.OrdinalIgnoreCase))
            {
                await CheckExophaseAuthAsync().ConfigureAwait(false);
                _logger?.Info("Checked Exophase auth for Exophase tab.");
            }
            else if (string.Equals(name, "ShadPS4Tab", StringComparison.OrdinalIgnoreCase))
            {
                CheckShadPS4Auth();
                _logger?.Info("Checked ShadPS4 auth for ShadPS4 tab.");
            }
            else if (string.Equals(name, "Rpcs3Tab", StringComparison.OrdinalIgnoreCase))
            {
                CheckRpcs3Auth();
                _logger?.Info("Checked RPCS3 auth for RPCS3 tab.");
            }
            else if (string.Equals(name, "ManualTab", StringComparison.OrdinalIgnoreCase))
            {
                EnsureLegacyManualImportPathDefault();
                _logger?.Info("Prepared Legacy manual import defaults for Manual tab.");
            }
            else if (string.Equals(name, "ThemeMigrationTab", StringComparison.OrdinalIgnoreCase))
            {
                LoadThemes();
                _logger?.Info("Loaded themes for Theme Migration tab.");
            }
        }

        private void ExophasePlatform_CheckboxLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkbox)
            {
                var platform = checkbox.Tag as string;
                if (string.IsNullOrWhiteSpace(platform)) return;

                var settings = _settingsViewModel.Settings?.Persisted;
                if (settings == null) return;

                checkbox.IsChecked = settings.ExophaseManagedPlatforms.Contains(platform);
            }
        }

        private void ExophasePlatform_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkbox)
            {
                var platform = checkbox.Tag as string;
                if (string.IsNullOrWhiteSpace(platform)) return;

                var settings = _settingsViewModel.Settings?.Persisted;
                if (settings == null) return;

                if (checkbox.IsChecked == true)
                {
                    if (!settings.ExophaseManagedPlatforms.Contains(platform))
                    {
                        settings.ExophaseManagedPlatforms.Add(platform);
                        _logger?.Info($"Added platform '{platform}' to Exophase managed platforms");
                    }
                }
                else
                {
                    if (settings.ExophaseManagedPlatforms.Remove(platform))
                    {
                        _logger?.Info($"Removed platform '{platform}' from Exophase managed platforms");
                    }
                }
            }
        }

        // -----------------------------
        // Settings property change handling for mock preview refresh
        // -----------------------------

        private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Refresh mock previews when display-affecting settings change
            var refreshProperties = new[]
            {
                nameof(Models.Settings.PersistedSettings.ShowCompactListRarityBar),
                nameof(Models.Settings.PersistedSettings.ShowRarityGlow),
                nameof(Models.Settings.PersistedSettings.ShowHiddenIcon),
                nameof(Models.Settings.PersistedSettings.ShowHiddenTitle),
                nameof(Models.Settings.PersistedSettings.ShowHiddenDescription),
                nameof(Models.Settings.PersistedSettings.ShowHiddenSuffix),
                nameof(Models.Settings.PersistedSettings.ShowLockedIcon)
            };

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
        
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}

