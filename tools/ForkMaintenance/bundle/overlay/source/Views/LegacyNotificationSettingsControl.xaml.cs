// LegacyNotificationSettingsControl.xaml.cs
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Net;
using System.Globalization;
using PlayniteAchievements.Services;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
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
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WinForms = System.Windows.Forms;

namespace PlayniteAchievements.Views
{
    public partial class LegacyNotificationSettingsControl : UserControl, IDisposable
    {
        // -----------------------------
        // Mock data for settings preview
        // -----------------------------

        private System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> _mockCompactListItems;
        private TextBox _lastNotificationTemplateTextBox;
        private int _lastNotificationTemplateCaretIndex;
        private TextBox _lastScreenshotTemplateTextBox;
        private int _lastScreenshotTemplateCaretIndex;
        private readonly ObservableCollection<NotificationCustomIconOption> _notificationCustomIconOptions = new ObservableCollection<NotificationCustomIconOption>();
        private readonly Dictionary<string, List<SvgRepoSearchResult>> _svgRepoSearchCache = new Dictionary<string, List<SvgRepoSearchResult>>(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<NotificationCustomIconOption> NotificationCustomIconOptions => _notificationCustomIconOptions;

        public sealed class NotificationCustomIconOption
        {
            public string DisplayName { get; set; }
            public string Path { get; set; }
            public string SourcePath { get; set; }
            public ImageSource PreviewSource { get; set; }
            public string SlotUsage { get; set; }
        }

        public sealed class SvgRepoSearchResult
        {
            public string Title { get; set; }
            public string PageUrl { get; set; }
            public string SvgUrl { get; set; }
            public string PreviewUrl { get; set; }
            public ImageSource PreviewSource { get; set; }
        }

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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false, OnUseScrollableAchievementsForThemeMigrationChanged));

        public bool UseScrollableAchievementsForThemeMigration
        {
            get => (bool)GetValue(UseScrollableAchievementsForThemeMigrationProperty);
            set => SetValue(UseScrollableAchievementsForThemeMigrationProperty, value);
        }

        public static readonly DependencyProperty HighlightLatestAchievementForThemeMigrationProperty =
            DependencyProperty.Register(
                nameof(HighlightLatestAchievementForThemeMigration),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(true));

        public bool HighlightLatestAchievementForThemeMigration
        {
            get => (bool)GetValue(HighlightLatestAchievementForThemeMigrationProperty);
            set => SetValue(HighlightLatestAchievementForThemeMigrationProperty, value);
        }

        public static readonly DependencyProperty HasRevertableThemesProperty =
            DependencyProperty.Register(
                nameof(HasRevertableThemes),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
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
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false));

        public bool LegacyManualImportBusy
        {
            get => (bool)GetValue(LegacyManualImportBusyProperty);
            set => SetValue(LegacyManualImportBusyProperty, value);
        }

        public static readonly DependencyProperty StartPageActivityScopeTextProperty =
            DependencyProperty.Register(
                nameof(StartPageActivityScopeText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string StartPageActivityScopeText
        {
            get => (string)GetValue(StartPageActivityScopeTextProperty);
            set => SetValue(StartPageActivityScopeTextProperty, value);
        }

        public static readonly DependencyProperty StartPageProgressScopeTextProperty =
            DependencyProperty.Register(
                nameof(StartPageProgressScopeText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string StartPageProgressScopeText
        {
            get => (string)GetValue(StartPageProgressScopeTextProperty);
            set => SetValue(StartPageProgressScopeTextProperty, value);
        }

        public static readonly DependencyProperty ViewAchievementsHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(ViewAchievementsHotkeyButtonText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string ViewAchievementsHotkeyButtonText
        {
            get => (string)GetValue(ViewAchievementsHotkeyButtonTextProperty);
            set => SetValue(ViewAchievementsHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty ManageAchievementsHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(ManageAchievementsHotkeyButtonText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string ManageAchievementsHotkeyButtonText
        {
            get => (string)GetValue(ManageAchievementsHotkeyButtonTextProperty);
            set => SetValue(ManageAchievementsHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty OverviewHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(OverviewHotkeyButtonText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string OverviewHotkeyButtonText
        {
            get => (string)GetValue(OverviewHotkeyButtonTextProperty);
            set => SetValue(OverviewHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty HotkeyCaptureStatusTextProperty =
            DependencyProperty.Register(
                nameof(HotkeyCaptureStatusText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string HotkeyCaptureStatusText
        {
            get => (string)GetValue(HotkeyCaptureStatusTextProperty);
            set => SetValue(HotkeyCaptureStatusTextProperty, value);
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
        /// <c>ElementName=LegacyNotificationSettingsControlRoot</c>.  Populated lazily when the Achievement
        /// Notifications tab is first initialised; may be null if Exophase is not configured.
        /// </summary>
        public Providers.Exophase.ExophaseSettings ExophaseEditSettings { get; private set; }
        public Providers.RetroAchievements.RetroAchievementsSettings RetroAchievementsEditSettings { get; private set; }
        public static readonly DependencyProperty ApiVerificationActiveMonitoringProperty =
            DependencyProperty.Register(
                nameof(ApiVerificationActiveMonitoring),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false, OnApiVerificationActiveMonitoringChanged));

        public bool ApiVerificationActiveMonitoring
        {
            get => (bool)GetValue(ApiVerificationActiveMonitoringProperty);
            set => SetValue(ApiVerificationActiveMonitoringProperty, value);
        }

        private bool _isRefreshingApiVerificationControls;

        private static void OnApiVerificationActiveMonitoringChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control)
            {
                control.ApplyApiVerificationActiveMonitoring((bool)e.NewValue);
            }
        }

        private readonly Dictionary<string, ProviderSettingsViewBase> _providerViewsByKey = new Dictionary<string, ProviderSettingsViewBase>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<NotificationSoundOption> _notificationSoundOptions = new ObservableCollection<NotificationSoundOption>();
        private readonly ObservableCollection<NotificationStyleOption> _notificationStyleOptions = new ObservableCollection<NotificationStyleOption>();
        private readonly ObservableCollection<NotificationProviderOption> _notificationProviderOptions = new ObservableCollection<NotificationProviderOption>();
        private readonly ObservableCollection<NotificationPreviewGameOption> _notificationPreviewGameOptions = new ObservableCollection<NotificationPreviewGameOption>();
        private readonly ObservableCollection<NotificationPreviewAchievementOption> _notificationPreviewAchievementOptions = new ObservableCollection<NotificationPreviewAchievementOption>();
        private readonly ObservableCollection<CustomStyleSlotOption> _customStyleSlotOptions = new ObservableCollection<CustomStyleSlotOption>();
        private readonly DispatcherTimer _notificationAutoPopupPreviewTimer;
        private readonly DispatcherTimer _notificationInlinePreviewLoopTimer;
        private Providers.Local.LocalSettings _notificationPreviewSettings;
        private bool _isRefreshingNotificationSoundSelection;
        private bool _isRefreshingNotificationStyleSelection;
        private bool _isRefreshingOverlayPresetControls;
        private bool _isRefreshingCustomStyleSlotSelection;
        private ICollectionView _providerNavigationView;
        private bool _providerNavigationBuilt;
        private bool _themeMigrationLoaded;
        private HotkeyCaptureTarget? _capturingHotkey;
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

        private sealed class NotificationPreviewAchievementOption
        {
            public string DisplayName { get; set; }

            public AchievementDetail Achievement { get; set; }
        }

        private sealed class CustomStyleSlotOption
        {
            public string DisplayName { get; set; }

            public int SlotNumber { get; set; }
        }

        private enum HotkeyCaptureTarget
        {
            ViewAchievements,
            ManageAchievements,
            Overview
        }

        public static readonly DependencyProperty LegacyProviderNavigationItemsProperty =
            DependencyProperty.Register(
                nameof(LegacyProviderNavigationItems),
                typeof(ObservableCollection<LegacyProviderNavigationItem>),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(null));

        public ObservableCollection<LegacyProviderNavigationItem> LegacyProviderNavigationItems
        {
            get => (ObservableCollection<LegacyProviderNavigationItem>)GetValue(LegacyProviderNavigationItemsProperty);
            set => SetValue(LegacyProviderNavigationItemsProperty, value);
        }

        public static readonly DependencyProperty SelectedLegacyProviderNavigationItemProperty =
            DependencyProperty.Register(
                nameof(SelectedLegacyProviderNavigationItem),
                typeof(LegacyProviderNavigationItem),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(null, OnSelectedLegacyProviderNavigationItemChanged));

        public LegacyProviderNavigationItem SelectedLegacyProviderNavigationItem
        {
            get => (LegacyProviderNavigationItem)GetValue(SelectedLegacyProviderNavigationItemProperty);
            set => SetValue(SelectedLegacyProviderNavigationItemProperty, value);
        }

        public static readonly DependencyProperty ProviderSearchTextProperty =
            DependencyProperty.Register(
                nameof(ProviderSearchText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
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

        public LegacyNotificationSettingsControl(
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
            _notificationInlinePreviewLoopTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2600)
            };
            _notificationInlinePreviewLoopTimer.Tick += NotificationInlinePreviewLoopTimer_Tick;
            Unloaded += LegacyNotificationSettingsControl_Unloaded;

            InitializeComponent();

            // Initialize provider navigation overview
            LegacyProviderNavigationItems = new ObservableCollection<LegacyProviderNavigationItem>();

            // Playnite does not reliably set DataContext for settings views.
            // Bind directly to the settings model so XAML uses {Binding SomeSetting}.
            DataContext = _settingsViewModel.Settings;

            // Initialize theme collections
            AvailableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            RevertableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            InitializeThemeMigrationCustomOptions();

            // Subscribe to settings property changes to refresh mock previews
            _settingsViewModel.Settings.Persisted.PropertyChanged += OnSettingsPropertyChanged;
            UpdateStartPageScopeTexts();
            UpdateHotkeyButtonTexts();

            // Debug logging to verify DataContext and Settings values
            _logger?.Info($"LegacyNotificationSettingsControl created. DataContext type: {DataContext?.GetType().Name}");
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
                BuildLegacyProviderNavigationItems();

                // Navigate to a specific provider item if requested (e.g., from auth notification click).
                NavigateToPendingProvider();
            };
        }


        private void LegacyNotificationSettingsControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _notificationAutoPopupPreviewTimer?.Stop();
            NotificationPublisher.ClosePersistentSettingsPreview();
        }
        private void NotificationTemplateTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            RememberNotificationTemplateCaret(sender as TextBox);
        }

        private void NotificationTemplateTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            RememberNotificationTemplateCaret(sender as TextBox);
        }

        private void RememberNotificationTemplateCaret(TextBox textBox)
        {
            if (textBox == null)
            {
                return;
            }

            _lastNotificationTemplateTextBox = textBox;
            _lastNotificationTemplateCaretIndex = Math.Max(0, Math.Min(textBox.SelectionStart, textBox.Text?.Length ?? 0));
        }

        private void NotificationTemplateToken_Click(object sender, RoutedEventArgs e)
        {
            var token = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var target = _lastNotificationTemplateTextBox
                ?? NotificationLine1TemplateTextBox
                ?? NotificationLine2TemplateTextBox
                ?? NotificationLine3TemplateTextBox
                ?? NotificationLine4TemplateTextBox
                ?? NotificationLine5TemplateTextBox
                ?? NotificationLine6TemplateTextBox;
            if (target == null)
            {
                return;
            }

            var text = target.Text ?? string.Empty;
            var start = Math.Max(0, Math.Min(_lastNotificationTemplateCaretIndex, text.Length));
            var selectionLength = target.SelectionLength;
            if (ReferenceEquals(target, Keyboard.FocusedElement))
            {
                start = Math.Max(0, Math.Min(target.SelectionStart, text.Length));
                selectionLength = Math.Max(0, Math.Min(target.SelectionLength, text.Length - start));
            }

            target.Text = text.Substring(0, start) + token + text.Substring(Math.Min(text.Length, start + selectionLength));
            var caret = start + token.Length;
            target.Focus();
            target.SelectionStart = caret;
            target.SelectionLength = 0;
            _lastNotificationTemplateTextBox = target;
            _lastNotificationTemplateCaretIndex = caret;
            target.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        private void ScreenshotTemplate_SelectionChanged(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                _lastScreenshotTemplateTextBox = textBox;
                _lastScreenshotTemplateCaretIndex = Math.Max(0, Math.Min(textBox.SelectionStart, textBox.Text?.Length ?? 0));
            }
        }

        private void ScreenshotFilenameToken_Click(object sender, RoutedEventArgs e)
        {
            var token = (sender as FrameworkElement)?.Tag as string;
            var target = _lastScreenshotTemplateTextBox ?? NotificationsScreenshotFilenameTemplateTextBox;
            if (string.IsNullOrWhiteSpace(token) || target == null)
            {
                return;
            }

            var text = target.Text ?? string.Empty;
            var start = Math.Max(0, Math.Min(_lastScreenshotTemplateCaretIndex, text.Length));
            var selectionLength = Math.Max(0, Math.Min(target.SelectionLength, text.Length - start));
            target.Text = text.Substring(0, start) + token + text.Substring(start + selectionLength);

            var caret = start + token.Length;
            target.Focus();
            target.SelectionStart = caret;
            target.SelectionLength = 0;
            _lastScreenshotTemplateTextBox = target;
            _lastScreenshotTemplateCaretIndex = caret;
            target.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        private void NotificationAddLine_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            localSettings.OverlayCustomShowLine1 = true;
            if (!localSettings.OverlayCustomShowGameName)
            {
                localSettings.OverlayCustomShowGameName = true;
                if (string.IsNullOrWhiteSpace(localSettings.OverlayCustomGameNameTemplate))
                {
                    localSettings.OverlayCustomGameNameTemplate = "<achievementDescription>";
                }
                localSettings.OverlayCustomLine2Order = 2;
            }
            else if (!localSettings.OverlayCustomShowMeta)
            {
                localSettings.OverlayCustomShowMeta = true;
                if (string.IsNullOrWhiteSpace(localSettings.OverlayCustomAchievementTemplate))
                {
                    localSettings.OverlayCustomAchievementTemplate = "<points> | <rarityIcon> | <trophyIcon>";
                }
                localSettings.OverlayCustomLine3Order = 3;
            }
            else if (!localSettings.OverlayCustomShowLine4)
            {
                localSettings.OverlayCustomShowLine4 = true;
                if (string.IsNullOrWhiteSpace(localSettings.OverlayCustomLine4Template))
                {
                    localSettings.OverlayCustomLine4Template = "<provider> | <sourceName>";
                }
                localSettings.OverlayCustomLine4Order = 4;
            }
            else if (!localSettings.OverlayCustomShowLine5)
            {
                localSettings.OverlayCustomShowLine5 = true;
                if (string.IsNullOrWhiteSpace(localSettings.OverlayCustomLine5Template))
                {
                    localSettings.OverlayCustomLine5Template = "<dateTime>";
                }
                localSettings.OverlayCustomLine5Order = 5;
            }
            else if (!localSettings.OverlayCustomShowLine6)
            {
                localSettings.OverlayCustomShowLine6 = true;
                if (string.IsNullOrWhiteSpace(localSettings.OverlayCustomLine6Template))
                {
                    localSettings.OverlayCustomLine6Template = "<achievementDescription>";
                }
                localSettings.OverlayCustomLine6Order = 6;
            }

            UpdateCustomStyleInlinePreview(localSettings);
        }

        private void NotificationRemoveLine_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            if (localSettings.OverlayCustomShowLine6)
            {
                localSettings.OverlayCustomShowLine6 = false;
            }
            else if (localSettings.OverlayCustomShowLine5)
            {
                localSettings.OverlayCustomShowLine5 = false;
            }
            else if (localSettings.OverlayCustomShowLine4)
            {
                localSettings.OverlayCustomShowLine4 = false;
            }
            else if (localSettings.OverlayCustomShowMeta)
            {
                localSettings.OverlayCustomShowMeta = false;
            }
            else if (localSettings.OverlayCustomShowGameName)
            {
                localSettings.OverlayCustomShowGameName = false;
            }

            localSettings.OverlayCustomShowLine1 = true;

            UpdateCustomStyleInlinePreview(localSettings);
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

                    // Populate API-backed provider settings so shared notification controls can update them.
                    ExophaseEditSettings = _providerRegistry?.GetSettingsForEdit("Exophase") as Providers.Exophase.ExophaseSettings;
                    RetroAchievementsEditSettings = _providerRegistry?.GetSettingsForEdit("RetroAchievements") as Providers.RetroAchievements.RetroAchievementsSettings;
                    RefreshApiVerificationControls();

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

                    CollectionProgressSoundComboBox.ItemsSource = _notificationSoundOptions;
                    PrestigeProgressSoundComboBox.ItemsSource = _notificationSoundOptions;
                    if (string.IsNullOrWhiteSpace(localSettings.CollectionProgressNotifications.SoundPath))
                    {
                        localSettings.CollectionProgressNotifications.SoundPath = localSettings.UnlockSoundPath;
                    }
                    if (string.IsNullOrWhiteSpace(localSettings.PrestigeProgressNotifications.SoundPath))
                    {
                        localSettings.PrestigeProgressNotifications.SoundPath = localSettings.UnlockSoundPath;
                    }
                    CollectionProgressSoundComboBox.SelectedValue = localSettings.CollectionProgressNotifications.SoundPath;
                    PrestigeProgressSoundComboBox.SelectedValue = localSettings.PrestigeProgressNotifications.SoundPath;
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

            if (NotificationsUnlockSoundLeadMillisecondsTextBox != null)
            {
                NotificationsUnlockSoundLeadMillisecondsTextBox.Text = localSettings.UnlockSoundLeadMilliseconds.ToString();
            }

            if (NotificationsScreenshotDelayMillisecondsTextBox != null)
            {
                NotificationsScreenshotDelayMillisecondsTextBox.Text = localSettings.ScreenshotDelayMilliseconds.ToString();
            }

            RefreshCustomStyleSlotControls(localSettings);
            RefreshNotificationCustomIconOptions(localSettings);
            RefreshSanCompatibilityControls(localSettings);
            UpdateCustomStyleInlinePreview(localSettings);
        }

        private void LocalNotificationSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!(sender is Providers.Local.LocalSettings localSettings))
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(Providers.Local.LocalSettings.UnlockOverlayTransitionStyle), StringComparison.Ordinal))
            {
                localSettings.OverlayCustomAnimationStyle = ResolveSanAnimationStyle(localSettings.UnlockOverlayTransitionStyle);
            }

            if (IsCustomInlinePreviewProperty(e.PropertyName))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ShouldRefreshCustomStyleSlotControls(e.PropertyName))
                    {
                        RefreshCustomStyleSlotControls(localSettings);
                    }

                    RefreshSanElementSideControls(localSettings);
                    RefreshSanViewDurationControls(localSettings);
                    RefreshSanCompatibilityControls(localSettings);
                    if (string.Equals(e.PropertyName, nameof(Providers.Local.LocalSettings.OverlayCustomIconLibraryPaths), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(Providers.Local.LocalSettings.OverlayCustomIconPath), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(Providers.Local.LocalSettings.OverlayCustomSecondaryIconPath), StringComparison.Ordinal))
                    {
                        RefreshNotificationCustomIconOptions(localSettings);
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
                case nameof(Providers.Local.LocalSettings.OverlayCustomSecondaryIconSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomIconCornerRadius):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSecondaryIconCornerRadius):
                case nameof(Providers.Local.LocalSettings.OverlayCustomTitleFontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomDetailFontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomMetaFontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4FontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5FontSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6FontSize):
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
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Template):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Template):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Template):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowLine4):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowLine5):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowLine6):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Color):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Color):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Color):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine1OutlineColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine2OutlineColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine3OutlineColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4OutlineColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5OutlineColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6OutlineColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine1ShadowColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine2ShadowColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine3ShadowColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4ShadowColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5ShadowColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6ShadowColor):
                case nameof(Providers.Local.LocalSettings.OverlayCustomOutlineSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShadowSize):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine1OutlineEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine2OutlineEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine3OutlineEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4OutlineEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5OutlineEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6OutlineEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine1ShadowEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine2ShadowEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine3ShadowEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4ShadowEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5ShadowEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6ShadowEnabled):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLineSpacing):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine1Spacing):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine2Spacing):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine3Spacing):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Spacing):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Spacing):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Spacing):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine1View):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine2View):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine3View):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4View):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5View):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6View):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine1Order):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine2Order):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine3Order):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Order):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Order):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Order):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLayoutStyle):
                case nameof(Providers.Local.LocalSettings.OverlayCustomAnimationStyle):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSanPresetId):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSanView1DurationMilliseconds):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSanView2DurationMilliseconds):
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
                case nameof(Providers.Local.LocalSettings.OverlayCustomIconPath):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSecondaryIconSource):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSecondaryIconPath):
                case nameof(Providers.Local.LocalSettings.OverlayCustomIconLibraryPaths):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowSecondaryIcon):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSecondaryIconNextToPrimary):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSanElementPresetId):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSanElementPosition):
                case nameof(Providers.Local.LocalSettings.OverlayCustomSanProviderLogoSource):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Bold):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Italic):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Underline):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine4Strikethrough):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Bold):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Italic):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Underline):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine5Strikethrough):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Bold):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Italic):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Underline):
                case nameof(Providers.Local.LocalSettings.OverlayCustomLine6Strikethrough):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowIconRarityGlow):
                case nameof(Providers.Local.LocalSettings.OverlayCustomShowSecondaryIconRarityGlow):
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

        private void NotificationInlinePreviewLoopTimer_Tick(object sender, EventArgs e)
        {
            if (_notificationPreviewSettings == null || NotificationsCustomInlinePreviewHost == null || !IsVisible)
            {
                _notificationInlinePreviewLoopTimer.Stop();
                return;
            }

            UpdateCustomStyleInlinePreview(_notificationPreviewSettings);
        }

        private void ScheduleAutoPopupPreview()
        {
            _notificationAutoPopupPreviewTimer.Stop();
            _notificationAutoPopupPreviewTimer.Start();
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

        private void RefreshApiVerificationControls()
        {
            _isRefreshingApiVerificationControls = true;
            try
            {
                ApiVerificationActiveMonitoring =
                    ExophaseEditSettings?.EnableActiveMonitoring == true &&
                    RetroAchievementsEditSettings?.EnableActiveMonitoring == true;

                if (ApiVerificationPollingIntervalSecondsTextBox != null)
                {
                    var interval = ExophaseEditSettings?.MonitoringIntervalSeconds
                        ?? RetroAchievementsEditSettings?.MonitoringIntervalSeconds
                        ?? 300;
                    ApiVerificationPollingIntervalSecondsTextBox.Text = interval.ToString();
                }
            }
            finally
            {
                _isRefreshingApiVerificationControls = false;
            }
        }

        private void ApplyApiVerificationActiveMonitoring(bool enabled)
        {
            if (_isRefreshingApiVerificationControls)
            {
                return;
            }

            if (ExophaseEditSettings != null)
            {
                ExophaseEditSettings.EnableActiveMonitoring = enabled;
            }

            if (RetroAchievementsEditSettings != null)
            {
                RetroAchievementsEditSettings.EnableActiveMonitoring = enabled;
            }
        }

        private void ApiVerificationPollingIntervalSecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyApiVerificationPollingIntervalFromTextBox(updateTextBox: false);
        }

        private void ApiVerificationPollingIntervalSecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyApiVerificationPollingIntervalFromTextBox(updateTextBox: true);
        }

        private void ApplyApiVerificationPollingIntervalFromTextBox(bool updateTextBox)
        {
            if (_isRefreshingApiVerificationControls || ApiVerificationPollingIntervalSecondsTextBox == null)
            {
                return;
            }

            if (int.TryParse(ApiVerificationPollingIntervalSecondsTextBox.Text, out var interval))
            {
                if (ExophaseEditSettings != null)
                {
                    ExophaseEditSettings.MonitoringIntervalSeconds = interval;
                    interval = ExophaseEditSettings.MonitoringIntervalSeconds;
                }

                if (RetroAchievementsEditSettings != null)
                {
                    RetroAchievementsEditSettings.MonitoringIntervalSeconds = interval;
                    interval = RetroAchievementsEditSettings.MonitoringIntervalSeconds;
                }
            }

            if (updateTextBox)
            {
                var resolvedInterval = ExophaseEditSettings?.MonitoringIntervalSeconds
                    ?? RetroAchievementsEditSettings?.MonitoringIntervalSeconds
                    ?? 300;
                ApiVerificationPollingIntervalSecondsTextBox.Text = resolvedInterval.ToString();
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

        private void ScoreProgressSoundBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Wave files|*.wav|All files|*.*");
            if (string.IsNullOrWhiteSpace(path)) return;
            var local = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (local == null) return;
            var isPrestige = string.Equals((sender as FrameworkElement)?.Tag as string, "Prestige", StringComparison.OrdinalIgnoreCase);
            if (isPrestige) local.PrestigeProgressNotifications.SoundPath = path;
            else local.CollectionProgressNotifications.SoundPath = path;
            AddNotificationSoundEntries(local, new[] { path }, autoSelectFirstAdded: false);
            if (isPrestige) PrestigeProgressSoundComboBox.SelectedValue = path;
            else CollectionProgressSoundComboBox.SelectedValue = path;
        }

        private void ScoreProgressNotificationTest_Click(object sender, RoutedEventArgs e)
        {
            var local = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (local == null) return;
            var isPrestige = string.Equals((sender as FrameworkElement)?.Tag as string, "Prestige", StringComparison.OrdinalIgnoreCase);
            var config = isPrestige ? local.PrestigeProgressNotifications : local.CollectionProgressNotifications;
            var scoreName = isPrestige ? "Prestige" : "Collection";
            new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger)
                .SendScoreProgressNotification(scoreName, "tier", 12, "Gold2", config, local);
            NotificationsUnlockSoundStatusTextBlock.Text = $"Tested {scoreName} score notification.";
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
            RefreshCustomPopupPreviewForProviderOverride();
        }


        private void RefreshCustomPopupPreviewForProviderOverride()
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            UpdateCustomStyleInlinePreview(localSettings);
            if (NotificationsAutoPopupPreviewCheckBox?.IsChecked == true)
            {
                ScheduleAutoPopupPreview();
            }
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
            var previewAchievement = GetSelectedNotificationPreviewAchievement();

            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            publisher.SendUnlockPopup(
                previewGameName,
                previewAchievement?.DisplayName ?? $"Preview ({styleOption.DisplayName})",
                achievementIconPath: previewAchievement?.UnlockedIconPath,
                providerKey: providerOption.ProviderKey,
                forcedStyle: styleOption.StyleKey,
                overrideLocalSettings: localSettings,
                game: previewGame,
                achievementDescription: previewAchievement?.Description ?? "Win your first fight without taking damage.",
                achievementPoints: previewAchievement?.Points ?? 25,
                achievementRarity: FormatPreviewAchievementRarity(previewAchievement, "12.7%"),
                achievementTrophy: previewAchievement?.TrophyType ?? "Gold");
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
            var previewAchievement = GetSelectedNotificationPreviewAchievement();

            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            foreach (var style in _notificationStyleOptions)
            {
                publisher.SendUnlockPopup(
                    previewGameName,
                    previewAchievement?.DisplayName ?? $"Preview ({style.DisplayName})",
                    achievementIconPath: previewAchievement?.UnlockedIconPath,
                    providerKey: providerOption.ProviderKey,
                    forcedStyle: style.StyleKey,
                    overrideLocalSettings: localSettings,
                    game: previewGame,
                    achievementDescription: previewAchievement?.Description ?? "Win your first fight without taking damage.",
                    achievementPoints: previewAchievement?.Points ?? 25,
                    achievementRarity: FormatPreviewAchievementRarity(previewAchievement, "12.7%"),
                    achievementTrophy: previewAchievement?.TrophyType ?? "Gold");
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
                var previewAchievement = GetSelectedNotificationPreviewAchievement();
                var previewProviderKey = ResolveCurrentNotificationTestProviderKey();
                var requiresCustomOverlay = IsSanTransitionStyle(localSettings.UnlockOverlayTransitionStyle) ||
                    !string.IsNullOrWhiteSpace(localSettings.OverlayCustomSanElementPresetId);
                if (requiresCustomOverlay && string.IsNullOrWhiteSpace(soundPath))
                {
                    soundPath = NotificationPublisher.ResolveSanDefaultSoundPath(localSettings);
                }

                var forcedDeliveryMode = requiresCustomOverlay
                    ? (LocalUnlockNotificationDeliveryMode?)LocalUnlockNotificationDeliveryMode.Overlay
                    : null;
                var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
                publisher.ShowLocalAchievementUnlocked(
                    previewGameName,
                    new[] { previewAchievement?.DisplayName ?? "Test Achievement" },
                    soundPath,
                    unlockedAchievementIconPath: previewAchievement?.UnlockedIconPath,
                    game: previewGame,
                    achievementDescription: previewAchievement?.Description ?? "Test description for preview.",
                    achievementPoints: previewAchievement?.Points ?? 25,
                    achievementRarity: FormatPreviewAchievementRarity(previewAchievement, "52.4%"),
                    achievementTrophy: previewAchievement?.TrophyType ?? "Gold",
                    forcedStyle: NotificationPublisher.NotificationStyleCustom,
                    forcedDeliveryMode: forcedDeliveryMode,
                    overrideLocalSettings: localSettings,
                    notificationProviderKey: previewProviderKey);

                NotificationsUnlockSoundStatusTextBlock.Text = requiresCustomOverlay
                    ? $"Test custom overlay sent using transition '{localSettings.UnlockOverlayTransitionStyle}'."
                    : $"Test notification sent for {ProviderRegistry.GetLocalizedName(previewProviderKey)} using {localSettings.UnlockNotificationDeliveryMode}.";
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
            RefreshNotificationPreviewAchievementOptions();
        }

        private Game GetSelectedNotificationPreviewGame()
        {
            return (NotificationsPreviewGameComboBox?.SelectedItem as NotificationPreviewGameOption)?.Game;
        }

        private AchievementDetail GetSelectedNotificationPreviewAchievement()
        {
            return (NotificationsPreviewAchievementComboBox?.SelectedItem as NotificationPreviewAchievementOption)?.Achievement;
        }

        private static string FormatPreviewAchievementRarity(AchievementDetail achievement, string fallback)
        {
            return achievement?.GlobalPercentUnlocked.HasValue == true
                ? achievement.GlobalPercentUnlocked.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : fallback;
        }

        private void RefreshNotificationPreviewAchievementOptions()
        {
            if (NotificationsPreviewAchievementComboBox == null)
            {
                return;
            }

            var selectedApiName = GetSelectedNotificationPreviewAchievement()?.ApiName;
            var game = GetSelectedNotificationPreviewGame();
            var achievements = game == null
                ? new List<AchievementDetail>()
                : (_plugin?.AchievementDataService?.GetVisibleGameAchievementData(game.Id)?.Achievements ?? new List<AchievementDetail>())
                    .Where(achievement => achievement != null)
                    .OrderBy(achievement => achievement.DisplayName)
                    .ToList();

            _notificationPreviewAchievementOptions.Clear();
            _notificationPreviewAchievementOptions.Add(new NotificationPreviewAchievementOption
            {
                DisplayName = "Sample achievement",
                Achievement = null
            });

            foreach (var achievement in achievements)
            {
                _notificationPreviewAchievementOptions.Add(new NotificationPreviewAchievementOption
                {
                    DisplayName = string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.ApiName : achievement.DisplayName,
                    Achievement = achievement
                });
            }

            NotificationsPreviewAchievementComboBox.ItemsSource = _notificationPreviewAchievementOptions;
            NotificationsPreviewAchievementComboBox.IsEnabled = game != null && achievements.Count > 0;
            NotificationsPreviewAchievementComboBox.SelectedItem = _notificationPreviewAchievementOptions
                .FirstOrDefault(option => string.Equals(option.Achievement?.ApiName, selectedApiName, StringComparison.Ordinal))
                ?? _notificationPreviewAchievementOptions.FirstOrDefault();
        }

        private void NotificationsPreviewGameComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshNotificationPreviewAchievementOptions();
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

        private void NotificationsPreviewAchievementComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        private void NotificationsCustomPrimaryIconBrowse_Click(object sender, RoutedEventArgs e)
        {
            SetNotificationCustomIconPath(primary: true);
        }

        private void NotificationsCustomSecondaryIconBrowse_Click(object sender, RoutedEventArgs e)
        {
            SetNotificationCustomIconPath(primary: false);
        }

        private void NotificationsCustomPrimaryIconClear_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            localSettings.OverlayCustomIconPath = string.Empty;
            NotificationsUnlockSoundStatusTextBlock.Text = "Primary custom icon cleared.";
        }

        private void NotificationsCustomSecondaryIconClear_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            localSettings.OverlayCustomSecondaryIconPath = string.Empty;
            NotificationsUnlockSoundStatusTextBlock.Text = "Secondary custom icon cleared.";
        }

        private void NotificationsOpenCustomIconsManager_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            RefreshNotificationCustomIconOptions(localSettings);
            var panelBrush = ResolveThemeBrush("PanelBackgroundBrush", Color.FromRgb(30, 30, 30));
            var controlBrush = ResolveThemeBrush("ControlBackgroundBrush", Color.FromRgb(38, 38, 38));
            var textBrush = ResolveThemeBrush("TextBrush", Colors.White);
            var mutedBrush = ResolveThemeBrush("TextBrushDarker", Color.FromRgb(170, 170, 170));
            var borderBrush = ResolveThemeBrush("NormalBorderBrush", Color.FromRgb(80, 80, 80));

            var window = new Window
            {
                Title = "Custom Icons",
                Owner = Window.GetWindow(this),
                Width = 860,
                Height = 520,
                MinWidth = 720,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = panelBrush,
                Foreground = textBrush
            };

            var root = new DockPanel { Margin = new Thickness(12) };
            var frame = new Border
            {
                Padding = new Thickness(0),
                Background = panelBrush
            };
            frame.Child = root;
            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(buttons, Dock.Top);
            root.Children.Add(buttons);

            var importButton = new Button { Content = "Import icons", MinWidth = 92, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(importButton, textBrush);
            importButton.Click += (_, __) => ImportNotificationCustomIcons(localSettings);
            buttons.Children.Add(importButton);

            var primaryButton = new Button { Content = "Use selected as primary", MinWidth = 150, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(primaryButton, textBrush);
            buttons.Children.Add(primaryButton);

            var secondaryButton = new Button { Content = "Use selected as secondary", MinWidth = 160, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(secondaryButton, textBrush);
            buttons.Children.Add(secondaryButton);

            var clearAssignmentButton = new Button { Content = "Clear selected slot", MinWidth = 118, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(clearAssignmentButton, textBrush);
            buttons.Children.Add(clearAssignmentButton);

            var removeButton = new Button { Content = "Remove selected", MinWidth = 118, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(removeButton, textBrush);
            buttons.Children.Add(removeButton);

            var removeAllButton = new Button { Content = "Remove all", MinWidth = 90, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(removeAllButton, textBrush);
            buttons.Children.Add(removeAllButton);

            var iconSearchQuery = new TextBox { Width = 190, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeResources(iconSearchQuery, controlBrush, textBrush, borderBrush);
            buttons.Children.Add(iconSearchQuery);

            var svgRepoButton = new Button { Content = "Browse SVG Repo", MinWidth = 124, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(svgRepoButton, textBrush);
            svgRepoButton.Click += (_, __) => OpenSvgRepoWebView(iconSearchQuery.Text);
            buttons.Children.Add(svgRepoButton);

            var flaticonButton = new Button { Content = "Browse Flaticon", MinWidth = 116, Margin = new Thickness(0, 0, 8, 6) };
            ApplyThemeText(flaticonButton, textBrush);
            flaticonButton.Click += (_, __) => OpenFlaticonWebView(iconSearchQuery.Text);
            buttons.Children.Add(flaticonButton);

            var list = new ListBox
            {
                ItemsSource = NotificationCustomIconOptions,
                SelectedValuePath = "Path"
            };
            ApplyThemeResources(list, controlBrush, textBrush, borderBrush);
            list.ItemTemplate = CreateNotificationCustomIconTemplate(textBrush, mutedBrush, controlBrush);
            root.Children.Add(list);

            primaryButton.Click += (_, __) =>
            {
                if (list.SelectedItem is NotificationCustomIconOption option)
                {
                    localSettings.OverlayCustomIconPath = option.Path;
                    localSettings.OverlayCustomIconSource = LocalOverlayIconSource.CustomIcon;
                    RefreshNotificationCustomIconOptions(localSettings);
                }
            };

            secondaryButton.Click += (_, __) =>
            {
                if (list.SelectedItem is NotificationCustomIconOption option)
                {
                    localSettings.OverlayCustomSecondaryIconPath = option.Path;
                    localSettings.OverlayCustomSecondaryIconSource = LocalOverlayIconSource.CustomIcon;
                    RefreshNotificationCustomIconOptions(localSettings);
                }
            };

            clearAssignmentButton.Click += (_, __) =>
            {
                if (list.SelectedItem is NotificationCustomIconOption option)
                {
                    if (string.Equals(localSettings.OverlayCustomIconPath, option.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        localSettings.OverlayCustomIconPath = string.Empty;
                    }

                    if (string.Equals(localSettings.OverlayCustomSecondaryIconPath, option.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        localSettings.OverlayCustomSecondaryIconPath = string.Empty;
                    }

                    RefreshNotificationCustomIconOptions(localSettings);
                }
            };

            removeButton.Click += (_, __) => RemoveNotificationCustomIcon(localSettings, (list.SelectedItem as NotificationCustomIconOption)?.Path);
            removeAllButton.Click += (_, __) => RemoveAllNotificationCustomIcons(localSettings);

            window.Content = frame;
            window.ShowDialog();
        }

        private Brush ResolveThemeBrush(string key, Color fallback)
        {
            return TryFindResource(key) as Brush
                ?? Application.Current?.TryFindResource(key) as Brush
                ?? new SolidColorBrush(fallback);
        }

        private static void ApplyThemeResources(Control control, Brush background, Brush foreground, Brush border)
        {
            if (control == null)
            {
                return;
            }

            control.Background = background;
            control.Foreground = foreground;
            control.BorderBrush = border;
        }

        private static void ApplyThemeText(Control control, Brush foreground)
        {
            if (control != null)
            {
                control.Foreground = foreground;
            }
        }

        private void NotificationsImportCustomIcons_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            ImportNotificationCustomIcons(localSettings);
        }

        private void ImportNotificationCustomIcons(Providers.Local.LocalSettings localSettings)
        {
            string[] selectedPaths;
            using (var dialog = new WinForms.OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.ico;*.svg|All files|*.*",
                Multiselect = true,
                Title = "Import notification custom icons"
            })
            {
                if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                {
                    return;
                }

                selectedPaths = dialog.FileNames;
            }

            var library = localSettings.GetOverlayCustomIconLibraryPathEntries().ToList();
            var imported = 0;
            foreach (var selectedPath in selectedPaths.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
            {
                var managedPath = CopyNotificationCustomIconToDataPath(selectedPath);
                if (string.IsNullOrWhiteSpace(managedPath))
                {
                    managedPath = selectedPath;
                }
                else
                {
                    SetNotificationCustomIconSourcePath(managedPath, selectedPath);
                }

                if (!library.Any(path => string.Equals(path, managedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    library.Add(managedPath);
                    imported++;
                }
            }

            localSettings.SetOverlayCustomIconLibraryPathEntries(library);
            if (imported > 0)
            {
                if (string.IsNullOrWhiteSpace(localSettings.OverlayCustomIconPath))
                {
                    localSettings.OverlayCustomIconPath = library.LastOrDefault() ?? string.Empty;
                    localSettings.OverlayCustomIconSource = LocalOverlayIconSource.CustomIcon;
                }

                RefreshNotificationCustomIconOptions(localSettings);
                NotificationsUnlockSoundStatusTextBlock.Text = $"Imported {imported} custom icon{(imported == 1 ? string.Empty : "s")}.";
            }
        }

        private void NotificationsRemoveSelectedCustomIcon_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            var selectedPath = _notificationCustomIconOptions.FirstOrDefault()?.Path;
            if (localSettings == null || string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            RemoveNotificationCustomIcon(localSettings, selectedPath);
        }

        private void RemoveNotificationCustomIcon(Providers.Local.LocalSettings localSettings, string selectedPath)
        {
            if (localSettings == null || string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            var library = localSettings.GetOverlayCustomIconLibraryPathEntries()
                .Where(path => !string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            localSettings.SetOverlayCustomIconLibraryPathEntries(library);
            if (string.Equals(localSettings.OverlayCustomIconPath, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                localSettings.OverlayCustomIconPath = string.Empty;
            }

            if (string.Equals(localSettings.OverlayCustomSecondaryIconPath, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                localSettings.OverlayCustomSecondaryIconPath = string.Empty;
            }

            RemoveNotificationCustomIconSourcePath(selectedPath);
            RefreshNotificationCustomIconOptions(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = "Removed selected custom icon.";
        }

        private void RemoveAllNotificationCustomIcons(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null)
            {
                return;
            }

            localSettings.SetOverlayCustomIconLibraryPathEntries(Enumerable.Empty<string>());
            localSettings.OverlayCustomIconPath = string.Empty;
            localSettings.OverlayCustomSecondaryIconPath = string.Empty;
            ClearNotificationCustomIconSourcePaths();
            RefreshNotificationCustomIconOptions(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = "Removed all custom icons.";
        }

        private void NotificationsClearCustomIconChoices_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            localSettings.OverlayCustomIconPath = string.Empty;
            localSettings.OverlayCustomSecondaryIconPath = string.Empty;
            NotificationsUnlockSoundStatusTextBlock.Text = "Custom icon choices cleared.";
        }

        private static DataTemplate CreateNotificationCustomIconTemplate(Brush textBrush, Brush mutedBrush, Brush previewBackground)
        {
            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(FrameworkElement.WidthProperty, 46.0);
            border.SetValue(FrameworkElement.HeightProperty, 46.0);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, previewBackground);
            border.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            dock.AppendChild(border);

            var image = new FrameworkElementFactory(typeof(Image));
            image.SetBinding(Image.SourceProperty, new Binding(nameof(NotificationCustomIconOption.PreviewSource)));
            image.SetValue(Image.StretchProperty, Stretch.Uniform);
            border.AppendChild(image);

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            dock.AppendChild(stack);

            var top = new FrameworkElementFactory(typeof(StackPanel));
            top.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            stack.AppendChild(top);

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(NotificationCustomIconOption.DisplayName)));
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetValue(TextBlock.ForegroundProperty, textBrush);
            top.AppendChild(name);

            var usage = new FrameworkElementFactory(typeof(TextBlock));
            usage.SetBinding(TextBlock.TextProperty, new Binding(nameof(NotificationCustomIconOption.SlotUsage)) { StringFormat = "   {0}" });
            usage.SetValue(TextBlock.ForegroundProperty, Brushes.DeepSkyBlue);
            usage.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            top.AppendChild(usage);

            var path = new FrameworkElementFactory(typeof(TextBlock));
            path.SetBinding(TextBlock.TextProperty, new Binding(nameof(NotificationCustomIconOption.SourcePath)));
            path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            path.SetValue(FrameworkElement.MaxWidthProperty, 720.0);
            path.SetValue(TextBlock.ForegroundProperty, mutedBrush);
            stack.AppendChild(path);

            return new DataTemplate { VisualTree = dock };
        }

        private void OpenFlaticonWebView(string query)
        {
            try
            {
                var url = string.IsNullOrWhiteSpace(query)
                    ? "https://www.flaticon.com/"
                    : "https://www.flaticon.com/search?word=" + Uri.EscapeDataString(query.Trim());
                var window = new Window
                {
                    Title = "Flaticon Search",
                    Owner = Window.GetWindow(this),
                    Width = 1100,
                    Height = 760,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var webView = new WebView2();
                window.Content = webView;
                window.Loaded += async (_, __) =>
                {
                    try
                    {
                        await webView.EnsureCoreWebView2Async();
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        webView.CoreWebView2.Navigate(url);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "Failed to initialize Flaticon WebView.");
                        NotificationsUnlockSoundStatusTextBlock.Text = "Failed to open Flaticon WebView.";
                    }
                };
                window.Closed += (_, __) =>
                {
                    try
                    {
                        webView.Dispose();
                    }
                    catch
                    {
                    }
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to open Flaticon WebView.");
                NotificationsUnlockSoundStatusTextBlock.Text = "Failed to open Flaticon WebView.";
            }
        }

        private void OpenSvgRepoWebView(string query)
        {
            try
            {
                var slug = Regex.Replace((query ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
                var url = string.IsNullOrWhiteSpace(slug)
                    ? "https://www.svgrepo.com/"
                    : "https://www.svgrepo.com/vectors/" + Uri.EscapeDataString(slug) + "/";
                var window = new Window
                {
                    Title = "SVG Repo Search",
                    Owner = Window.GetWindow(this),
                    Width = 1100,
                    Height = 760,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var webView = new WebView2();
                window.Content = webView;
                window.Loaded += async (_, __) =>
                {
                    try
                    {
                        await webView.EnsureCoreWebView2Async();
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        webView.CoreWebView2.Navigate(url);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "Failed to initialize SVG Repo WebView.");
                        NotificationsUnlockSoundStatusTextBlock.Text = "Failed to open SVG Repo WebView.";
                    }
                };
                window.Closed += (_, __) =>
                {
                    try
                    {
                        webView.Dispose();
                    }
                    catch
                    {
                    }
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to open SVG Repo WebView.");
                NotificationsUnlockSoundStatusTextBlock.Text = "Failed to open SVG Repo WebView.";
            }
        }

        private void OpenSvgRepoSearchDialog(Providers.Local.LocalSettings localSettings, string query)
        {
            if (localSettings == null)
            {
                return;
            }

            var panelBrush = ResolveThemeBrush("PanelBackgroundBrush", Color.FromRgb(30, 30, 30));
            var controlBrush = ResolveThemeBrush("ControlBackgroundBrush", Color.FromRgb(38, 38, 38));
            var textBrush = ResolveThemeBrush("TextBrush", Colors.White);
            var mutedBrush = ResolveThemeBrush("TextBrushDarker", Color.FromRgb(170, 170, 170));
            var borderBrush = ResolveThemeBrush("NormalBorderBrush", Color.FromRgb(80, 80, 80));

            var window = new Window
            {
                Title = "SVG Repo Search",
                Owner = Window.GetWindow(this),
                Width = 880,
                Height = 620,
                MinWidth = 720,
                MinHeight = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = panelBrush,
                Foreground = textBrush
            };

            var root = new DockPanel { Margin = new Thickness(12) };
            var frame = new Border { Background = panelBrush, Child = root };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var searchBox = new TextBox { Text = query ?? string.Empty, Width = 260, Margin = new Thickness(0, 0, 8, 0) };
            ApplyThemeResources(searchBox, controlBrush, textBrush, borderBrush);
            top.Children.Add(searchBox);

            var searchButton = new Button { Content = "Search", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            ApplyThemeText(searchButton, textBrush);
            top.Children.Add(searchButton);

            var importButton = new Button { Content = "Import selected", MinWidth = 120 };
            ApplyThemeText(importButton, textBrush);
            DockPanel.SetDock(importButton, Dock.Right);
            top.Children.Add(importButton);

            var results = new ObservableCollection<SvgRepoSearchResult>();
            var list = new ListBox { ItemsSource = results };
            ApplyThemeResources(list, controlBrush, textBrush, borderBrush);
            list.ItemTemplate = CreateSvgRepoResultTemplate(textBrush, mutedBrush, controlBrush);
            root.Children.Add(list);

            async Task RunSearchAsync()
            {
                results.Clear();
                var term = searchBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(term))
                {
                    return;
                }

                try
                {
                    foreach (var result in await SearchSvgRepoAsync(term))
                    {
                        results.Add(result);
                    }

                    NotificationsUnlockSoundStatusTextBlock.Text = $"SVG Repo returned {results.Count} result{(results.Count == 1 ? string.Empty : "s")}.";
                }
                catch (Exception ex)
                {
                    if (IsTooManyRequests(ex))
                    {
                        NotificationsUnlockSoundStatusTextBlock.Text = "SVG Repo rate-limited the search. Try again later or use Browse Flaticon / browser import.";
                    }
                    else
                    {
                        _logger?.Warn(ex, "Failed to search SVG Repo.");
                        NotificationsUnlockSoundStatusTextBlock.Text = "Failed to search SVG Repo.";
                    }
                }
            }

            searchButton.Click += async (_, __) => await RunSearchAsync();
            searchBox.KeyDown += async (_, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    await RunSearchAsync();
                }
            };
            importButton.Click += async (_, __) =>
            {
                if (list.SelectedItem is SvgRepoSearchResult result)
                {
                    await ImportSvgRepoResultAsync(localSettings, result);
                }
            };

            window.Content = frame;
            window.Loaded += async (_, __) => await RunSearchAsync();
            window.ShowDialog();
        }

        private async Task<List<SvgRepoSearchResult>> SearchSvgRepoAsync(string query)
        {
            var slug = Regex.Replace((query ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(slug))
            {
                return new List<SvgRepoSearchResult>();
            }

            if (_svgRepoSearchCache.TryGetValue(slug, out var cachedResults))
            {
                return cachedResults.Select(CloneSvgRepoSearchResult).ToList();
            }

            return await Task.Run(() =>
            {
                var url = $"https://www.svgrepo.com/vectors/{Uri.EscapeDataString(slug)}/";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
                request.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";
                request.Referer = "https://www.svgrepo.com/";
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    var html = reader.ReadToEnd();
                    var matches = Regex.Matches(html, @"(?:(?:href|data-url|data-src|src)=[""'])(?<url>(?:https://www\.svgrepo\.com)?/(?:show|svg)/\d+/[^""']+?)(?:[""'])", RegexOptions.IgnoreCase);
                    var results = new List<SvgRepoSearchResult>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match match in matches)
                    {
                        var resultUrl = WebUtility.HtmlDecode(match.Groups["url"].Value ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(resultUrl))
                        {
                            continue;
                        }

                        if (!resultUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            resultUrl = "https://www.svgrepo.com" + resultUrl;
                        }

                        var svgUrl = resultUrl;
                        var pageUrl = resultUrl;
                        if (resultUrl.IndexOf("/svg/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            pageUrl = Regex.Replace(resultUrl, @"/svg/(\d+)/", "/show/$1/", RegexOptions.IgnoreCase);
                            svgUrl = pageUrl.TrimEnd('/') + ".svg";
                        }
                        else if (!resultUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                        {
                            svgUrl = resultUrl.TrimEnd('/') + ".svg";
                        }

                        if (!seen.Add(svgUrl))
                        {
                            continue;
                        }

                        var title = ResolveSvgRepoTitle(pageUrl);
                        var result = new SvgRepoSearchResult
                        {
                            Title = title,
                            PageUrl = pageUrl,
                            SvgUrl = svgUrl,
                            PreviewUrl = svgUrl,
                            PreviewSource = TryCreateRemoteImagePreview(svgUrl)
                        };
                        results.Add(result);
                        if (results.Count >= 40)
                        {
                            break;
                        }
                    }

                    _svgRepoSearchCache[slug] = results.Select(CloneSvgRepoSearchResult).ToList();
                    return results;
                }
            });
        }

        private static SvgRepoSearchResult CloneSvgRepoSearchResult(SvgRepoSearchResult result)
        {
            return result == null
                ? null
                : new SvgRepoSearchResult
                {
                    Title = result.Title,
                    PageUrl = result.PageUrl,
                    SvgUrl = result.SvgUrl,
                    PreviewUrl = result.PreviewUrl,
                    PreviewSource = result.PreviewSource
                };
        }

        private static bool IsTooManyRequests(Exception ex)
        {
            if (ex is WebException webException && webException.Response is HttpWebResponse response)
            {
                return (int)response.StatusCode == 429;
            }

            return ex?.InnerException != null && IsTooManyRequests(ex.InnerException);
        }

        private async Task ImportSvgRepoResultAsync(Providers.Local.LocalSettings localSettings, SvgRepoSearchResult result)
        {
            if (localSettings == null || result == null || string.IsNullOrWhiteSpace(result.SvgUrl))
            {
                return;
            }

            try
            {
                var directory = Path.Combine(_plugin?.GetPluginUserDataPath() ?? string.Empty, "notification_custom_icons");
                Directory.CreateDirectory(directory);
                var name = Regex.Replace(result.Title ?? "svg-icon", @"[^a-zA-Z0-9._-]+", "_").Trim('_');
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "svg-icon";
                }

                var destination = Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.svg");
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "PlayniteAchievements";
                    await client.DownloadFileTaskAsync(new Uri(result.SvgUrl), destination);
                }

                var library = localSettings.GetOverlayCustomIconLibraryPathEntries().ToList();
                library.Add(destination);
                localSettings.SetOverlayCustomIconLibraryPathEntries(library);
                SetNotificationCustomIconSourcePath(destination, result.PageUrl ?? result.SvgUrl);
                if (string.IsNullOrWhiteSpace(localSettings.OverlayCustomIconPath))
                {
                    localSettings.OverlayCustomIconPath = destination;
                    localSettings.OverlayCustomIconSource = LocalOverlayIconSource.CustomIcon;
                }

                RefreshNotificationCustomIconOptions(localSettings);
                NotificationsUnlockSoundStatusTextBlock.Text = $"Imported {result.Title} from SVG Repo.";
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to import SVG Repo icon.");
                NotificationsUnlockSoundStatusTextBlock.Text = "Failed to import SVG Repo icon.";
            }
        }

        private static string ResolveSvgRepoTitle(string pageUrl)
        {
            var tail = Regex.Match(pageUrl ?? string.Empty, @"/(?:show|svg)/\d+/(?<name>[^/?#]+)", RegexOptions.IgnoreCase).Groups["name"].Value;
            tail = Regex.Replace(tail ?? string.Empty, @"[-_]+", " ").Trim();
            return string.IsNullOrWhiteSpace(tail)
                ? "SVG Repo icon"
                : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tail.ToLowerInvariant());
        }

        private static DataTemplate CreateSvgRepoResultTemplate(Brush textBrush, Brush mutedBrush, Brush previewBackground)
        {
            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(FrameworkElement.WidthProperty, 52.0);
            border.SetValue(FrameworkElement.HeightProperty, 52.0);
            border.SetValue(Border.BackgroundProperty, previewBackground);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            dock.AppendChild(border);
            var image = new FrameworkElementFactory(typeof(Image));
            image.SetBinding(Image.SourceProperty, new Binding(nameof(SvgRepoSearchResult.PreviewSource)));
            image.SetValue(Image.StretchProperty, Stretch.Uniform);
            border.AppendChild(image);
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            dock.AppendChild(stack);
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new Binding(nameof(SvgRepoSearchResult.Title)));
            title.SetValue(TextBlock.ForegroundProperty, textBrush);
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            stack.AppendChild(title);
            var url = new FrameworkElementFactory(typeof(TextBlock));
            url.SetBinding(TextBlock.TextProperty, new Binding(nameof(SvgRepoSearchResult.SvgUrl)));
            url.SetValue(TextBlock.ForegroundProperty, mutedBrush);
            url.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            url.SetValue(FrameworkElement.MaxWidthProperty, 720.0);
            stack.AppendChild(url);
            return new DataTemplate { VisualTree = dock };
        }

        private static ImageSource TryCreateRemoteImagePreview(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void RefreshNotificationCustomIconOptions(Providers.Local.LocalSettings localSettings)
        {
            _notificationCustomIconOptions.Clear();
            if (localSettings == null)
            {
                return;
            }

            foreach (var path in localSettings.GetOverlayCustomIconLibraryPathEntries())
            {
                _notificationCustomIconOptions.Add(new NotificationCustomIconOption
                {
                    DisplayName = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path,
                    SourcePath = ResolveNotificationCustomIconSourcePath(path),
                    PreviewSource = TryCreateNotificationCustomIconPreview(path),
                    SlotUsage = ResolveNotificationCustomIconSlotUsage(localSettings, path)
                });
            }
        }

        private string ResolveNotificationCustomIconSourcePath(string managedPath)
        {
            if (string.IsNullOrWhiteSpace(managedPath))
            {
                return string.Empty;
            }

            var sources = LoadNotificationCustomIconSourcePaths();
            return sources.TryGetValue(managedPath, out var sourcePath) && !string.IsNullOrWhiteSpace(sourcePath)
                ? sourcePath
                : managedPath;
        }

        private string GetNotificationCustomIconSourceMapPath()
        {
            var directory = _plugin?.GetPluginUserDataPath();
            return string.IsNullOrWhiteSpace(directory)
                ? string.Empty
                : Path.Combine(directory, "notification_custom_icons_sources.json");
        }

        private Dictionary<string, string> LoadNotificationCustomIconSourcePaths()
        {
            try
            {
                var path = GetNotificationCustomIconSourceMapPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveNotificationCustomIconSourcePaths(Dictionary<string, string> sources)
        {
            try
            {
                var path = GetNotificationCustomIconSourceMapPath();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(sources ?? new Dictionary<string, string>(), Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to save notification custom icon source paths.");
            }
        }

        private void SetNotificationCustomIconSourcePath(string managedPath, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(managedPath) || string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            var sources = LoadNotificationCustomIconSourcePaths();
            sources[managedPath] = sourcePath;
            SaveNotificationCustomIconSourcePaths(sources);
        }

        private void RemoveNotificationCustomIconSourcePath(string managedPath)
        {
            if (string.IsNullOrWhiteSpace(managedPath))
            {
                return;
            }

            var sources = LoadNotificationCustomIconSourcePaths();
            if (sources.Remove(managedPath))
            {
                SaveNotificationCustomIconSourcePaths(sources);
            }
        }

        private void ClearNotificationCustomIconSourcePaths()
        {
            SaveNotificationCustomIconSourcePaths(new Dictionary<string, string>());
        }

        private static string ResolveNotificationCustomIconSlotUsage(Providers.Local.LocalSettings localSettings, string path)
        {
            var usages = new List<string>();
            if (string.Equals(localSettings?.OverlayCustomIconPath, path, StringComparison.OrdinalIgnoreCase))
            {
                usages.Add("Primary");
            }

            if (string.Equals(localSettings?.OverlayCustomSecondaryIconPath, path, StringComparison.OrdinalIgnoreCase))
            {
                usages.Add("Secondary");
            }

            return usages.Count == 0 ? string.Empty : string.Join(" + ", usages);
        }

        private static ImageSource TryCreateNotificationCustomIconPreview(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void SetNotificationCustomIconPath(bool primary)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.ico;*.svg|All files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath))
            {
                return;
            }

            var managedPath = CopyNotificationCustomIconToDataPath(selectedPath);
            if (string.IsNullOrWhiteSpace(managedPath))
            {
                managedPath = selectedPath;
            }
            else
            {
                SetNotificationCustomIconSourcePath(managedPath, selectedPath);
            }

            if (primary)
            {
                localSettings.OverlayCustomIconPath = managedPath;
                localSettings.OverlayCustomIconSource = LocalOverlayIconSource.CustomIcon;
                NotificationsUnlockSoundStatusTextBlock.Text = "Primary custom icon selected.";
            }
            else
            {
                localSettings.OverlayCustomSecondaryIconPath = managedPath;
                localSettings.OverlayCustomSecondaryIconSource = LocalOverlayIconSource.CustomIcon;
                NotificationsUnlockSoundStatusTextBlock.Text = "Secondary custom icon selected.";
            }
        }

        private string CopyNotificationCustomIconToDataPath(string sourcePath)
        {
            try
            {
                var dataPath = _plugin?.GetPluginUserDataPath();
                if (string.IsNullOrWhiteSpace(dataPath))
                {
                    return string.Empty;
                }

                var destinationDirectory = Path.Combine(dataPath, "notification_custom_icons");
                Directory.CreateDirectory(destinationDirectory);
                var extension = Path.GetExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".png";
                }

                var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = "icon";
                }

                baseName = Regex.Replace(baseName, @"[^a-zA-Z0-9._-]+", "_").Trim('_');
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = "icon";
                }

                var destinationPath = Path.Combine(destinationDirectory, $"{baseName}-{Guid.NewGuid():N}{extension}");
                File.Copy(sourcePath, destinationPath, overwrite: false);
                return destinationPath;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to copy custom notification icon.");
                return string.Empty;
            }
        }

        private void NotificationsSearchFlaticon_Click(object sender, RoutedEventArgs e)
        {
            OpenFlaticonWebView(string.Empty);
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
            var previewAchievement = GetSelectedNotificationPreviewAchievement();

            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            publisher.SendUnlockPopup(
                previewGameName,
                previewAchievement?.DisplayName ?? "Sample Achievement",
                achievementIconPath: previewAchievement?.UnlockedIconPath,
                providerKey: ResolveCurrentNotificationTestProviderKey(),
                forcedStyle: NotificationPublisher.NotificationStyleCustom,
                forcedDeliveryMode: LocalUnlockNotificationDeliveryMode.Overlay,
                overrideLocalSettings: localSettings,
                game: previewGame,
                togglePersistentOverlay: forceStatusMessage,
                refreshPersistentOverlay: !forceStatusMessage,
                achievementDescription: previewAchievement?.Description ?? "Win your first fight without taking damage.",
                achievementPoints: previewAchievement?.Points ?? 25,
                achievementRarity: FormatPreviewAchievementRarity(previewAchievement, "12.7%"),
                achievementTrophy: previewAchievement?.TrophyType ?? "Gold");

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

            RefreshSanElementSideControls(localSettings);
            RefreshSanViewDurationControls(localSettings);
            RefreshSanCompatibilityControls(localSettings);
        }

        private void RefreshSanElementSideControls(Providers.Local.LocalSettings localSettings)
        {
            if (NotificationsSanElementSideComboBox == null)
            {
                return;
            }

            NotificationsSanElementSideComboBox.IsEnabled = !string.IsNullOrWhiteSpace(localSettings?.OverlayCustomSanElementPresetId);
        }

        private void RefreshSanViewDurationControls(Providers.Local.LocalSettings localSettings)
        {
            if (NotificationsSanViewDurationGrid == null)
            {
                return;
            }

            var hasSanSelection = localSettings != null &&
                (IsSanTransitionStyle(localSettings.UnlockOverlayTransitionStyle) ||
                 !string.IsNullOrWhiteSpace(localSettings.OverlayCustomSanElementPresetId));
            NotificationsSanViewDurationGrid.Visibility = hasSanSelection ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NotificationsTransitionStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            NormalizeImportedSanPresentationWhenDisabled(localSettings);
            RefreshSanViewDurationControls(localSettings);
            RefreshSanCompatibilityControls(localSettings);
        }

        private void NotificationsSanElementsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            NormalizeImportedSanPresentationWhenDisabled(localSettings);
            RefreshSanViewDurationControls(localSettings);
            RefreshSanCompatibilityControls(localSettings);
        }

        private static void NormalizeImportedSanPresentationWhenDisabled(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null ||
                IsSanTransitionStyle(localSettings.UnlockOverlayTransitionStyle) ||
                !string.IsNullOrWhiteSpace(localSettings.OverlayCustomSanElementPresetId) ||
                string.IsNullOrWhiteSpace(localSettings.OverlayCustomSanPresetId))
            {
                return;
            }

            // Imported SAN templates carry renderer-specific layout and icon choices.
            // Once neither a SAN transition nor a standalone SAN element is selected,
            // restore the standard custom-card presentation while keeping the SAN data
            // available if the user selects a SAN option again.
            localSettings.OverlayCustomLayoutStyle = LocalCustomOverlayLayoutStyle.Standard;
            localSettings.OverlayCustomAnimationStyle = LocalCustomOverlayAnimationStyle.Standard;
            localSettings.OverlayCustomIconSource = LocalOverlayIconSource.AchievementIcon;
            localSettings.OverlayCustomSecondaryIconSource = LocalOverlayIconSource.None;
            localSettings.OverlayCustomShowSecondaryIcon = false;
        }

        private void RefreshSanCompatibilityControls(Providers.Local.LocalSettings localSettings)
        {
            var selectedTransitionPreset = ResolveSanTransitionPresetId(localSettings?.UnlockOverlayTransitionStyle ?? LocalUnlockOverlayTransitionStyle.Fade);
            var selectedElementPreset = (localSettings?.OverlayCustomSanElementPresetId ?? string.Empty).Trim().ToLowerInvariant();

            if (NotificationsSanElementsComboBox != null)
            {
                foreach (var item in NotificationsSanElementsComboBox.Items.OfType<ComboBoxItem>())
                {
                    var elementPreset = (item.Tag as string ?? string.Empty).Trim().ToLowerInvariant();
                    item.IsEnabled = string.IsNullOrWhiteSpace(elementPreset) ||
                        string.IsNullOrWhiteSpace(selectedTransitionPreset) ||
                        AreSanPresetsCompatibleForSettings(selectedTransitionPreset, elementPreset);
                }
            }

            if (NotificationsTransitionStyleComboBox != null)
            {
                foreach (var item in NotificationsTransitionStyleComboBox.Items.OfType<ComboBoxItem>())
                {
                    if (!(item.Tag is LocalUnlockOverlayTransitionStyle transitionStyle))
                    {
                        item.IsEnabled = true;
                        continue;
                    }

                    var transitionPreset = ResolveSanTransitionPresetId(transitionStyle);
                    item.IsEnabled = string.IsNullOrWhiteSpace(selectedElementPreset) ||
                        string.IsNullOrWhiteSpace(transitionPreset) ||
                        AreSanPresetsCompatibleForSettings(transitionPreset, selectedElementPreset);
                }
            }
        }

        private static bool AreSanPresetsCompatibleForSettings(string transitionPreset, string elementPreset)
        {
            var transition = NormalizeSanPresetForSettings(transitionPreset);
            var element = NormalizeSanPresetForSettings(elementPreset);
            if (string.IsNullOrWhiteSpace(transition) || string.IsNullOrWhiteSpace(element))
            {
                return true;
            }

            return string.Equals(transition, element, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetSanPresetFamilyForSettings(transition), GetSanPresetFamilyForSettings(element), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSanPresetFamilyForSettings(string preset)
        {
            switch (NormalizeSanPresetForSettings(preset))
            {
                case "ps5":
                case "ps4":
                case "ps3":
                    return "playstation";
                case "xboxone":
                case "xbox360":
                    return "xbox";
                default:
                    return NormalizeSanPresetForSettings(preset);
            }
        }

        private static string NormalizeSanPresetForSettings(string preset)
        {
            return (preset ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string ResolveSanTransitionPresetId(LocalUnlockOverlayTransitionStyle transitionStyle)
        {
            switch (transitionStyle)
            {
                case LocalUnlockOverlayTransitionStyle.SanDefault:
                    return "default";
                case LocalUnlockOverlayTransitionStyle.SanXqjan:
                    return "xqjan";
                case LocalUnlockOverlayTransitionStyle.SanDeck:
                    return "steamdeck";
                case LocalUnlockOverlayTransitionStyle.SanEpic:
                    return "epicgames";
                case LocalUnlockOverlayTransitionStyle.SanXboxModern:
                    return "xboxone";
                case LocalUnlockOverlayTransitionStyle.SanXboxClassic:
                    return "xbox360";
                case LocalUnlockOverlayTransitionStyle.SanPsModern:
                    return "ps5";
                case LocalUnlockOverlayTransitionStyle.SanPsClassic:
                    return "ps4";
                case LocalUnlockOverlayTransitionStyle.SanPsRetro:
                    return "ps3";
                case LocalUnlockOverlayTransitionStyle.SanSquare:
                    return "windows";
                case LocalUnlockOverlayTransitionStyle.SanAero:
                    return "gfwl";
                default:
                    return string.Empty;
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

            var slots = EnsureCustomStyleSlots(localSettings);
            var slotIndex = Math.Max(0, Math.Min(slots.Count - 1, selectedSlot.SlotNumber - 1));
            ApplyCustomStyleSlotToEditor(localSettings, slots[slotIndex]);
            UpdateCustomStyleInlinePreview(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = $"Loaded custom style from Slot {selectedSlot.SlotNumber}.";
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

        private void NotificationsClearAllCustomStyleSlots_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var defaultSettings = new Providers.Local.LocalSettings();
            var defaultSlot = new LocalCustomOverlayStyleSlot { Name = "Slot 1" };
            localSettings.CustomOverlayStyleSlots = new List<LocalCustomOverlayStyleSlot> { defaultSlot };
            localSettings.SelectedCustomStyleSlot = 1;
            ApplyCustomStyleSlotToEditor(localSettings, defaultSlot);
            localSettings.UnlockOverlayDurationMilliseconds = defaultSettings.UnlockOverlayDurationMilliseconds;
            localSettings.UnlockOverlayFadeInMilliseconds = defaultSettings.UnlockOverlayFadeInMilliseconds;
            localSettings.UnlockOverlayFadeOutMilliseconds = defaultSettings.UnlockOverlayFadeOutMilliseconds;
            localSettings.UnlockSoundLeadMilliseconds = defaultSettings.UnlockSoundLeadMilliseconds;
            localSettings.UnlockOverlaySlideDistance = defaultSettings.UnlockOverlaySlideDistance;
            localSettings.UnlockOverlayTransitionStyle = defaultSettings.UnlockOverlayTransitionStyle;
            localSettings.UnlockOverlayPosition = defaultSettings.UnlockOverlayPosition;
            localSettings.OverlayCustomOpacity = defaultSettings.OverlayCustomOpacity;
            localSettings.OverlayCustomScale = defaultSettings.OverlayCustomScale;
            localSettings.OverlayCustomSanView1DurationMilliseconds = defaultSettings.OverlayCustomSanView1DurationMilliseconds;
            localSettings.OverlayCustomSanView2DurationMilliseconds = defaultSettings.OverlayCustomSanView2DurationMilliseconds;

            RefreshCustomStyleSlotControls(localSettings);
            RefreshNotificationStyleControls();
            UpdateCustomStyleInlinePreview(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = "Cleared all custom style slots and reset the editor to defaults.";
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
                SecondaryIconSize = localSettings.OverlayCustomSecondaryIconSize,
                IconCornerRadius = localSettings.OverlayCustomIconCornerRadius,
                SecondaryIconCornerRadius = localSettings.OverlayCustomSecondaryIconCornerRadius,
                Width = localSettings.OverlayCustomWidth,
                Height = localSettings.OverlayCustomHeight,
                CornerRadius = localSettings.OverlayCustomCornerRadius,
                TitleFontSize = localSettings.OverlayCustomTitleFontSize,
                DetailFontSize = localSettings.OverlayCustomDetailFontSize,
                MetaFontSize = localSettings.OverlayCustomMetaFontSize,
                Line4FontSize = localSettings.OverlayCustomLine4FontSize,
                Line5FontSize = localSettings.OverlayCustomLine5FontSize,
                Line6FontSize = localSettings.OverlayCustomLine6FontSize,
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
                Line4Template = localSettings.OverlayCustomLine4Template,
                Line5Template = localSettings.OverlayCustomLine5Template,
                Line6Template = localSettings.OverlayCustomLine6Template,
                ShowLine4 = localSettings.OverlayCustomShowLine4,
                ShowLine5 = localSettings.OverlayCustomShowLine5,
                ShowLine6 = localSettings.OverlayCustomShowLine6,
                Line4Color = localSettings.OverlayCustomLine4Color,
                Line5Color = localSettings.OverlayCustomLine5Color,
                Line6Color = localSettings.OverlayCustomLine6Color,
                Line1OutlineColor = localSettings.OverlayCustomLine1OutlineColor,
                Line2OutlineColor = localSettings.OverlayCustomLine2OutlineColor,
                Line3OutlineColor = localSettings.OverlayCustomLine3OutlineColor,
                Line4OutlineColor = localSettings.OverlayCustomLine4OutlineColor,
                Line5OutlineColor = localSettings.OverlayCustomLine5OutlineColor,
                Line6OutlineColor = localSettings.OverlayCustomLine6OutlineColor,
                Line1ShadowColor = localSettings.OverlayCustomLine1ShadowColor,
                Line2ShadowColor = localSettings.OverlayCustomLine2ShadowColor,
                Line3ShadowColor = localSettings.OverlayCustomLine3ShadowColor,
                Line4ShadowColor = localSettings.OverlayCustomLine4ShadowColor,
                Line5ShadowColor = localSettings.OverlayCustomLine5ShadowColor,
                Line6ShadowColor = localSettings.OverlayCustomLine6ShadowColor,
                Line1OutlineSize = localSettings.OverlayCustomLine1OutlineSize,
                Line2OutlineSize = localSettings.OverlayCustomLine2OutlineSize,
                Line3OutlineSize = localSettings.OverlayCustomLine3OutlineSize,
                Line4OutlineSize = localSettings.OverlayCustomLine4OutlineSize,
                Line5OutlineSize = localSettings.OverlayCustomLine5OutlineSize,
                Line6OutlineSize = localSettings.OverlayCustomLine6OutlineSize,
                Line1ShadowSize = localSettings.OverlayCustomLine1ShadowSize,
                Line2ShadowSize = localSettings.OverlayCustomLine2ShadowSize,
                Line3ShadowSize = localSettings.OverlayCustomLine3ShadowSize,
                Line4ShadowSize = localSettings.OverlayCustomLine4ShadowSize,
                Line5ShadowSize = localSettings.OverlayCustomLine5ShadowSize,
                Line6ShadowSize = localSettings.OverlayCustomLine6ShadowSize,
                OutlineSize = localSettings.OverlayCustomOutlineSize,
                ShadowSize = localSettings.OverlayCustomShadowSize,
                Line1OutlineEnabled = localSettings.OverlayCustomLine1OutlineEnabled,
                Line2OutlineEnabled = localSettings.OverlayCustomLine2OutlineEnabled,
                Line3OutlineEnabled = localSettings.OverlayCustomLine3OutlineEnabled,
                Line4OutlineEnabled = localSettings.OverlayCustomLine4OutlineEnabled,
                Line5OutlineEnabled = localSettings.OverlayCustomLine5OutlineEnabled,
                Line6OutlineEnabled = localSettings.OverlayCustomLine6OutlineEnabled,
                Line1ShadowEnabled = localSettings.OverlayCustomLine1ShadowEnabled,
                Line2ShadowEnabled = localSettings.OverlayCustomLine2ShadowEnabled,
                Line3ShadowEnabled = localSettings.OverlayCustomLine3ShadowEnabled,
                Line4ShadowEnabled = localSettings.OverlayCustomLine4ShadowEnabled,
                Line5ShadowEnabled = localSettings.OverlayCustomLine5ShadowEnabled,
                Line6ShadowEnabled = localSettings.OverlayCustomLine6ShadowEnabled,
                LineSpacing = localSettings.OverlayCustomLineSpacing,
                Line1Spacing = localSettings.OverlayCustomLine1Spacing,
                Line2Spacing = localSettings.OverlayCustomLine2Spacing,
                Line3Spacing = localSettings.OverlayCustomLine3Spacing,
                Line4Spacing = localSettings.OverlayCustomLine4Spacing,
                Line5Spacing = localSettings.OverlayCustomLine5Spacing,
                Line6Spacing = localSettings.OverlayCustomLine6Spacing,
                Line1View = localSettings.OverlayCustomLine1View,
                Line2View = localSettings.OverlayCustomLine2View,
                Line3View = localSettings.OverlayCustomLine3View,
                Line4View = localSettings.OverlayCustomLine4View,
                Line5View = localSettings.OverlayCustomLine5View,
                Line6View = localSettings.OverlayCustomLine6View,
                Line1Order = localSettings.OverlayCustomLine1Order,
                Line2Order = localSettings.OverlayCustomLine2Order,
                Line3Order = localSettings.OverlayCustomLine3Order,
                Line4Order = localSettings.OverlayCustomLine4Order,
                Line5Order = localSettings.OverlayCustomLine5Order,
                Line6Order = localSettings.OverlayCustomLine6Order,
                LayoutStyle = localSettings.OverlayCustomLayoutStyle,
                AnimationStyle = localSettings.OverlayCustomAnimationStyle,
                SecondaryIconSource = localSettings.OverlayCustomSecondaryIconSource,
                ShowSecondaryIcon = localSettings.OverlayCustomShowSecondaryIcon,
                SanElementPresetId = localSettings.OverlayCustomSanElementPresetId,
                SanElementPosition = localSettings.OverlayCustomSanElementPosition,
                SanProviderLogoSource = localSettings.OverlayCustomSanProviderLogoSource,
                TransitionStyle = localSettings.UnlockOverlayTransitionStyle,
                SanPresetId = localSettings.OverlayCustomSanPresetId,
                SanPresetHtml = localSettings.OverlayCustomSanPresetHtml,
                SanPresetCss = localSettings.OverlayCustomSanPresetCss,
                SanBaseCss = localSettings.OverlayCustomSanBaseCss,
                SanBaseAnimCss = localSettings.OverlayCustomSanBaseAnimCss,
                SanAssetRootPath = localSettings.OverlayCustomSanAssetRootPath,
                SanThemeJson = localSettings.OverlayCustomSanThemeJson,
                SanThemeDirectory = localSettings.OverlayCustomSanThemeDirectory,
                ManualElementCss = localSettings.OverlayCustomManualElementCss,
                SanView1DurationMilliseconds = localSettings.OverlayCustomSanView1DurationMilliseconds,
                SanView2DurationMilliseconds = localSettings.OverlayCustomSanView2DurationMilliseconds,
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
                Line4Bold = localSettings.OverlayCustomLine4Bold,
                Line4Italic = localSettings.OverlayCustomLine4Italic,
                Line4Underline = localSettings.OverlayCustomLine4Underline,
                Line4Strikethrough = localSettings.OverlayCustomLine4Strikethrough,
                Line5Bold = localSettings.OverlayCustomLine5Bold,
                Line5Italic = localSettings.OverlayCustomLine5Italic,
                Line5Underline = localSettings.OverlayCustomLine5Underline,
                Line5Strikethrough = localSettings.OverlayCustomLine5Strikethrough,
                Line6Bold = localSettings.OverlayCustomLine6Bold,
                Line6Italic = localSettings.OverlayCustomLine6Italic,
                Line6Underline = localSettings.OverlayCustomLine6Underline,
                Line6Strikethrough = localSettings.OverlayCustomLine6Strikethrough,
                IconSource = localSettings.OverlayCustomIconSource,
                CustomIconPath = localSettings.OverlayCustomIconPath,
                SecondaryCustomIconPath = localSettings.OverlayCustomSecondaryIconPath,
                ShowIconRarityGlow = localSettings.OverlayCustomShowIconRarityGlow,
                ShowSecondaryIconRarityGlow = localSettings.OverlayCustomShowSecondaryIconRarityGlow,
                EnableGameCoverInOverlay = localSettings.EnableGameCoverInOverlay,
                GameCoverPosition = localSettings.GameCoverPosition,
                GameCoverWidth = localSettings.GameCoverWidth,
                EnableGameBannerAsBackground = localSettings.EnableGameBannerAsBackground,
                GameBannerOpacity = localSettings.GameBannerOpacity,
                GameBannerBlurRadius = localSettings.GameBannerBlurRadius,
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
                        SecondaryIconSize = localSettings.OverlayCustomSecondaryIconSize,
                        IconCornerRadius = localSettings.OverlayCustomIconCornerRadius,
                        SecondaryIconCornerRadius = localSettings.OverlayCustomSecondaryIconCornerRadius,
                        Width = localSettings.OverlayCustomWidth,
                        Height = localSettings.OverlayCustomHeight,
                        CornerRadius = localSettings.OverlayCustomCornerRadius,
                        TitleFontSize = localSettings.OverlayCustomTitleFontSize,
                        DetailFontSize = localSettings.OverlayCustomDetailFontSize,
                        MetaFontSize = localSettings.OverlayCustomMetaFontSize,
                        Line4FontSize = localSettings.OverlayCustomLine4FontSize,
                        Line5FontSize = localSettings.OverlayCustomLine5FontSize,
                        Line6FontSize = localSettings.OverlayCustomLine6FontSize,
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
                        Line4Template = localSettings.OverlayCustomLine4Template,
                        Line5Template = localSettings.OverlayCustomLine5Template,
                        Line6Template = localSettings.OverlayCustomLine6Template,
                        ShowLine4 = localSettings.OverlayCustomShowLine4,
                        ShowLine5 = localSettings.OverlayCustomShowLine5,
                        ShowLine6 = localSettings.OverlayCustomShowLine6,
                        Line4Color = localSettings.OverlayCustomLine4Color,
                        Line5Color = localSettings.OverlayCustomLine5Color,
                        Line6Color = localSettings.OverlayCustomLine6Color,
                        Line1OutlineColor = localSettings.OverlayCustomLine1OutlineColor,
                        Line2OutlineColor = localSettings.OverlayCustomLine2OutlineColor,
                        Line3OutlineColor = localSettings.OverlayCustomLine3OutlineColor,
                        Line4OutlineColor = localSettings.OverlayCustomLine4OutlineColor,
                        Line5OutlineColor = localSettings.OverlayCustomLine5OutlineColor,
                        Line6OutlineColor = localSettings.OverlayCustomLine6OutlineColor,
                        Line1ShadowColor = localSettings.OverlayCustomLine1ShadowColor,
                        Line2ShadowColor = localSettings.OverlayCustomLine2ShadowColor,
                        Line3ShadowColor = localSettings.OverlayCustomLine3ShadowColor,
                        Line4ShadowColor = localSettings.OverlayCustomLine4ShadowColor,
                        Line5ShadowColor = localSettings.OverlayCustomLine5ShadowColor,
                        Line6ShadowColor = localSettings.OverlayCustomLine6ShadowColor,
                Line1OutlineSize = localSettings.OverlayCustomLine1OutlineSize,
                Line2OutlineSize = localSettings.OverlayCustomLine2OutlineSize,
                Line3OutlineSize = localSettings.OverlayCustomLine3OutlineSize,
                Line4OutlineSize = localSettings.OverlayCustomLine4OutlineSize,
                Line5OutlineSize = localSettings.OverlayCustomLine5OutlineSize,
                Line6OutlineSize = localSettings.OverlayCustomLine6OutlineSize,
                Line1ShadowSize = localSettings.OverlayCustomLine1ShadowSize,
                Line2ShadowSize = localSettings.OverlayCustomLine2ShadowSize,
                Line3ShadowSize = localSettings.OverlayCustomLine3ShadowSize,
                Line4ShadowSize = localSettings.OverlayCustomLine4ShadowSize,
                Line5ShadowSize = localSettings.OverlayCustomLine5ShadowSize,
                Line6ShadowSize = localSettings.OverlayCustomLine6ShadowSize,
                        OutlineSize = localSettings.OverlayCustomOutlineSize,
                        ShadowSize = localSettings.OverlayCustomShadowSize,
                        Line1OutlineEnabled = localSettings.OverlayCustomLine1OutlineEnabled,
                        Line2OutlineEnabled = localSettings.OverlayCustomLine2OutlineEnabled,
                        Line3OutlineEnabled = localSettings.OverlayCustomLine3OutlineEnabled,
                        Line4OutlineEnabled = localSettings.OverlayCustomLine4OutlineEnabled,
                        Line5OutlineEnabled = localSettings.OverlayCustomLine5OutlineEnabled,
                        Line6OutlineEnabled = localSettings.OverlayCustomLine6OutlineEnabled,
                        Line1ShadowEnabled = localSettings.OverlayCustomLine1ShadowEnabled,
                        Line2ShadowEnabled = localSettings.OverlayCustomLine2ShadowEnabled,
                        Line3ShadowEnabled = localSettings.OverlayCustomLine3ShadowEnabled,
                        Line4ShadowEnabled = localSettings.OverlayCustomLine4ShadowEnabled,
                        Line5ShadowEnabled = localSettings.OverlayCustomLine5ShadowEnabled,
                        Line6ShadowEnabled = localSettings.OverlayCustomLine6ShadowEnabled,
                        LineSpacing = localSettings.OverlayCustomLineSpacing,
                        Line1Spacing = localSettings.OverlayCustomLine1Spacing,
                        Line2Spacing = localSettings.OverlayCustomLine2Spacing,
                        Line3Spacing = localSettings.OverlayCustomLine3Spacing,
                        Line4Spacing = localSettings.OverlayCustomLine4Spacing,
                        Line5Spacing = localSettings.OverlayCustomLine5Spacing,
                        Line6Spacing = localSettings.OverlayCustomLine6Spacing,
                        Line1View = localSettings.OverlayCustomLine1View,
                        Line2View = localSettings.OverlayCustomLine2View,
                        Line3View = localSettings.OverlayCustomLine3View,
                        Line4View = localSettings.OverlayCustomLine4View,
                        Line5View = localSettings.OverlayCustomLine5View,
                        Line6View = localSettings.OverlayCustomLine6View,
                        Line1Order = localSettings.OverlayCustomLine1Order,
                        Line2Order = localSettings.OverlayCustomLine2Order,
                        Line3Order = localSettings.OverlayCustomLine3Order,
                        Line4Order = localSettings.OverlayCustomLine4Order,
                        Line5Order = localSettings.OverlayCustomLine5Order,
                        Line6Order = localSettings.OverlayCustomLine6Order,
                        LayoutStyle = localSettings.OverlayCustomLayoutStyle,
                        AnimationStyle = localSettings.OverlayCustomAnimationStyle,
                        SecondaryIconSource = localSettings.OverlayCustomSecondaryIconSource,
                ShowSecondaryIcon = localSettings.OverlayCustomShowSecondaryIcon,
                        SanElementPresetId = localSettings.OverlayCustomSanElementPresetId,
                        SanElementPosition = localSettings.OverlayCustomSanElementPosition,
                        SanProviderLogoSource = localSettings.OverlayCustomSanProviderLogoSource,
                        TransitionStyle = localSettings.UnlockOverlayTransitionStyle,
                        SanPresetId = localSettings.OverlayCustomSanPresetId,
                        SanPresetHtml = localSettings.OverlayCustomSanPresetHtml,
                        SanPresetCss = localSettings.OverlayCustomSanPresetCss,
                        SanBaseCss = localSettings.OverlayCustomSanBaseCss,
                        SanBaseAnimCss = localSettings.OverlayCustomSanBaseAnimCss,
                        SanAssetRootPath = localSettings.OverlayCustomSanAssetRootPath,
                        SanThemeJson = localSettings.OverlayCustomSanThemeJson,
                        SanThemeDirectory = localSettings.OverlayCustomSanThemeDirectory,
                        ManualElementCss = localSettings.OverlayCustomManualElementCss,
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
                        Line4Bold = localSettings.OverlayCustomLine4Bold,
                        Line4Italic = localSettings.OverlayCustomLine4Italic,
                        Line4Underline = localSettings.OverlayCustomLine4Underline,
                        Line4Strikethrough = localSettings.OverlayCustomLine4Strikethrough,
                        Line5Bold = localSettings.OverlayCustomLine5Bold,
                        Line5Italic = localSettings.OverlayCustomLine5Italic,
                        Line5Underline = localSettings.OverlayCustomLine5Underline,
                        Line5Strikethrough = localSettings.OverlayCustomLine5Strikethrough,
                        Line6Bold = localSettings.OverlayCustomLine6Bold,
                        Line6Italic = localSettings.OverlayCustomLine6Italic,
                        Line6Underline = localSettings.OverlayCustomLine6Underline,
                        Line6Strikethrough = localSettings.OverlayCustomLine6Strikethrough,
                        IconSource = localSettings.OverlayCustomIconSource,
                        CustomIconPath = localSettings.OverlayCustomIconPath,
                        SecondaryCustomIconPath = localSettings.OverlayCustomSecondaryIconPath,
                        ShowIconRarityGlow = localSettings.OverlayCustomShowIconRarityGlow,
                        ShowSecondaryIconRarityGlow = localSettings.OverlayCustomShowSecondaryIconRarityGlow,
                        EnableGameCoverInOverlay = localSettings.EnableGameCoverInOverlay,
                        GameCoverPosition = localSettings.GameCoverPosition,
                        GameCoverWidth = localSettings.GameCoverWidth,
                        EnableGameBannerAsBackground = localSettings.EnableGameBannerAsBackground,
                        GameBannerOpacity = localSettings.GameBannerOpacity,
                        GameBannerBlurRadius = localSettings.GameBannerBlurRadius,
                        CustomCoverImagePath = localSettings.OverlayCustomCoverImagePath,
                        CustomBannerImagePath = localSettings.OverlayCustomBannerImagePath
                    },
                    Transition = new NotificationOverlayTransitionTemplate
                    {
                        DurationMilliseconds = localSettings.UnlockOverlayDurationMilliseconds,
                        FadeInMilliseconds = localSettings.UnlockOverlayFadeInMilliseconds,
                        FadeOutMilliseconds = localSettings.UnlockOverlayFadeOutMilliseconds,
                        SoundLeadMilliseconds = localSettings.UnlockSoundLeadMilliseconds,
                        Opacity = localSettings.OverlayCustomOpacity,
                        Scale = localSettings.OverlayCustomScale,
                        OverlayPosition = localSettings.UnlockOverlayPosition.ToString(),
                        TransitionStyle = localSettings.UnlockOverlayTransitionStyle.ToString(),
                        TemplateAnimationStyle = localSettings.OverlayCustomAnimationStyle.ToString(),
                        SlideDistance = localSettings.UnlockOverlaySlideDistance,
                        IconSize = localSettings.OverlayCustomIconSize,
                        SecondaryIconSize = localSettings.OverlayCustomSecondaryIconSize,
                        IconCornerRadius = localSettings.OverlayCustomIconCornerRadius,
                        SecondaryIconCornerRadius = localSettings.OverlayCustomSecondaryIconCornerRadius,
                        IconSource = localSettings.OverlayCustomIconSource.ToString(),
                        SecondaryIconSource = localSettings.OverlayCustomSecondaryIconSource.ToString(),
                        EnableGameCoverInOverlay = localSettings.EnableGameCoverInOverlay,
                        GameCoverPosition = localSettings.GameCoverPosition.ToString(),
                        GameCoverWidth = localSettings.GameCoverWidth,
                        EnableGameBannerAsBackground = localSettings.EnableGameBannerAsBackground,
                        GameBannerOpacity = localSettings.GameBannerOpacity,
                        GameBannerBlurRadius = localSettings.GameBannerBlurRadius,
                        ShowIconRarityGlow = localSettings.OverlayCustomShowIconRarityGlow,
                        ShowSecondaryIconRarityGlow = localSettings.OverlayCustomShowSecondaryIconRarityGlow,
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
                string[] selectedPaths;
                using (var dialog = new WinForms.OpenFileDialog
                {
                    Title = "Import notification templates",
                    Filter = "Notification templates|*.json;*.san;*.html;*.css|JSON|*.json|SAN export|*.san;usertheme.json|SAN preset files|*.html;*.css|All files|*.*",
                    Multiselect = true,
                    CheckFileExists = true
                })
                {
                    if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                    {
                        return;
                    }

                    selectedPaths = dialog.FileNames;
                }

                if (selectedPaths == null || selectedPaths.Length == 0)
                {
                    return;
                }

                var slots = EnsureCustomStyleSlots(localSettings);
                var slotIndex = Math.Max(0, Math.Min(slots.Count - 1, localSettings.SelectedCustomStyleSlot - 1));
                NotificationOverlayTemplateFile lastImported = null;
                LocalCustomOverlayStyleSlot lastImportedSlot = null;
                var importedCount = 0;

                foreach (var selectedPath in selectedPaths.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
                {
                    var imported = LoadNotificationTemplateFile(selectedPath);
                    if (imported?.CustomStyleSlot == null)
                    {
                        continue;
                    }

                    var importedSlot = imported.CustomStyleSlot;
                    importedSlot.Name = string.IsNullOrWhiteSpace(importedSlot.Name)
                        ? (string.IsNullOrWhiteSpace(imported.TemplateName) ? $"Slot {slotIndex + 1}" : imported.TemplateName.Trim())
                        : importedSlot.Name.Trim();

                    if (importedCount > 0 || !IsEmptyCustomStyleSlot(slots[slotIndex]))
                    {
                        slots.Add(importedSlot);
                        slotIndex = slots.Count - 1;
                    }
                    else
                    {
                        slots[slotIndex] = importedSlot;
                    }

                    importedCount++;
                    lastImported = imported;
                    lastImportedSlot = importedSlot;
                }

                if (importedCount == 0 || lastImportedSlot == null)
                {
                    NotificationsUnlockSoundStatusTextBlock.Text = "Invalid template file: missing custom style data.";
                    return;
                }

                localSettings.CustomOverlayStyleSlots = slots;
                localSettings.SelectedCustomStyleSlot = slotIndex + 1;
                ApplyCustomStyleSlotToEditor(localSettings, slots[slotIndex]);
                ApplyImportedTemplateTransition(localSettings, lastImported);

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
                NotificationsUnlockSoundStatusTextBlock.Text = importedCount == 1
                    ? (string.IsNullOrWhiteSpace(lastImportedSlot.SanPresetId)
                        ? $"Imported custom template into Slot {slotIndex + 1} and set Local style to Custom."
                        : $"Imported SAN preset '{lastImportedSlot.SanPresetId}' into Slot {slotIndex + 1} and set Local style to Custom.")
                    : $"Imported {importedCount} notification templates. Slot {slotIndex + 1} is active.";
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to import notification custom style template.");
                NotificationsUnlockSoundStatusTextBlock.Text = "Failed to import custom template.";
            }
        }

        private NotificationOverlayTemplateFile LoadNotificationTemplateFile(string selectedPath)
        {
            var imported = TryImportSanExportTemplate(selectedPath)
                ?? TryImportSanPresetTemplate(selectedPath)
                ?? JsonConvert.DeserializeObject<NotificationOverlayTemplateFile>(File.ReadAllText(selectedPath));

            if (!string.IsNullOrWhiteSpace(imported?.CustomStyleSlot?.SanThemeJson) &&
                string.IsNullOrWhiteSpace(imported.CustomStyleSlot.SanPresetHtml) &&
                !string.IsNullOrWhiteSpace(imported.CustomStyleSlot.SanPresetId))
            {
                var sanPreset = TryBuildSanPresetTemplateFromId(NormalizeSanPresetId(imported.CustomStyleSlot.SanPresetId));
                if (sanPreset?.CustomStyleSlot != null)
                {
                    imported.CustomStyleSlot.SanPresetHtml = sanPreset.CustomStyleSlot.SanPresetHtml;
                    imported.CustomStyleSlot.SanPresetCss = sanPreset.CustomStyleSlot.SanPresetCss;
                    imported.CustomStyleSlot.SanBaseCss = sanPreset.CustomStyleSlot.SanBaseCss;
                    imported.CustomStyleSlot.SanBaseAnimCss = sanPreset.CustomStyleSlot.SanBaseAnimCss;
                    imported.CustomStyleSlot.SanAssetRootPath = sanPreset.CustomStyleSlot.SanAssetRootPath;
                }
            }

            return imported;
        }

        private static void ApplyImportedTemplateTransition(Providers.Local.LocalSettings localSettings, NotificationOverlayTemplateFile imported)
        {
            if (localSettings == null || imported?.Transition == null)
            {
                return;
            }

            localSettings.UnlockOverlayDurationMilliseconds = imported.Transition.DurationMilliseconds;
            localSettings.UnlockOverlayFadeInMilliseconds = imported.Transition.FadeInMilliseconds;
            localSettings.UnlockOverlayFadeOutMilliseconds = imported.Transition.FadeOutMilliseconds;
            localSettings.UnlockSoundLeadMilliseconds = imported.Transition.SoundLeadMilliseconds;
            localSettings.UnlockOverlaySlideDistance = imported.Transition.SlideDistance;

            if (imported.Transition.Opacity.HasValue)
            {
                localSettings.OverlayCustomOpacity = imported.Transition.Opacity.Value;
            }

            if (imported.Transition.Scale.HasValue)
            {
                localSettings.OverlayCustomScale = imported.Transition.Scale.Value;
            }

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

            if (!string.IsNullOrWhiteSpace(imported.Transition.TemplateAnimationStyle) &&
                Enum.TryParse(imported.Transition.TemplateAnimationStyle, true, out LocalCustomOverlayAnimationStyle parsedTemplateAnimationStyle))
            {
                localSettings.OverlayCustomAnimationStyle = parsedTemplateAnimationStyle;
            }
            else
            {
                localSettings.OverlayCustomAnimationStyle = ResolveSanAnimationStyle(localSettings.UnlockOverlayTransitionStyle);
            }

            if (!string.IsNullOrWhiteSpace(imported.Transition.IconSource) &&
                Enum.TryParse(imported.Transition.IconSource, true, out LocalOverlayIconSource parsedIconSource))
            {
                localSettings.OverlayCustomIconSource = parsedIconSource;
            }

            if (imported.Transition.IconSize > 0)
            {
                localSettings.OverlayCustomIconSize = imported.Transition.IconSize;
            }
            localSettings.OverlayCustomIconCornerRadius = imported.Transition.IconCornerRadius;

            if (!string.IsNullOrWhiteSpace(imported.Transition.SecondaryIconSource) &&
                Enum.TryParse(imported.Transition.SecondaryIconSource, true, out LocalOverlayIconSource parsedSecondaryIconSource))
            {
                localSettings.OverlayCustomSecondaryIconSource = parsedSecondaryIconSource;
                localSettings.OverlayCustomShowSecondaryIcon = parsedSecondaryIconSource != LocalOverlayIconSource.None;
            }

            if (imported.Transition.SecondaryIconSize > 0)
            {
                localSettings.OverlayCustomSecondaryIconSize = imported.Transition.SecondaryIconSize;
            }
            localSettings.OverlayCustomSecondaryIconCornerRadius = imported.Transition.SecondaryIconCornerRadius;

            localSettings.OverlayCustomShowIconRarityGlow = imported.Transition.ShowIconRarityGlow;
            localSettings.OverlayCustomShowSecondaryIconRarityGlow = imported.Transition.ShowSecondaryIconRarityGlow;
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

        private static bool IsEmptyCustomStyleSlot(LocalCustomOverlayStyleSlot slot)
        {
            if (slot == null)
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(slot.SanPresetId) &&
                string.IsNullOrWhiteSpace(slot.SanPresetHtml) &&
                string.IsNullOrWhiteSpace(slot.SanThemeJson) &&
                (string.IsNullOrWhiteSpace(slot.Name) || slot.Name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase));
        }

        private NotificationOverlayTemplateFile TryImportSanPresetTemplate(string selectedPath)
        {
            try
            {
                var extension = Path.GetExtension(selectedPath);
                if (!string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".css", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var presetDir = Directory.Exists(selectedPath)
                    ? selectedPath
                    : Path.GetDirectoryName(selectedPath);
                if (string.IsNullOrWhiteSpace(presetDir))
                {
                    return null;
                }

                var indexPath = Path.Combine(presetDir, "index.html");
                var cssPath = Path.Combine(presetDir, "styles.css");
                if (!File.Exists(indexPath) || !File.Exists(cssPath))
                {
                    return null;
                }

                var notifyDir = Directory.GetParent(presetDir)?.Parent?.FullName;
                var baseCssPath = string.IsNullOrWhiteSpace(notifyDir) ? null : Path.Combine(notifyDir, "base.css");
                var baseAnimCssPath = string.IsNullOrWhiteSpace(notifyDir) ? null : Path.Combine(notifyDir, "baseanim.css");
                if (string.IsNullOrWhiteSpace(baseCssPath) || !File.Exists(baseCssPath) ||
                    string.IsNullOrWhiteSpace(baseAnimCssPath) || !File.Exists(baseAnimCssPath))
                {
                    return null;
                }

                var indexHtml = File.ReadAllText(indexPath);
                var presetCss = File.ReadAllText(cssPath);
                var baseCss = File.ReadAllText(baseCssPath);
                var baseAnimCss = File.ReadAllText(baseAnimCssPath);
                var meta = Regex.Match(indexHtml, "<meta\\s+width=\"(?<width>\\d+)\"\\s+height=\"(?<height>\\d+)\"(?:\\s+offset=\"(?<offset>\\d+)\")?", RegexOptions.IgnoreCase);
                if (!meta.Success)
                {
                    return null;
                }

                var presetId = new DirectoryInfo(presetDir).Name.Trim();
                var width = Math.Max(120, int.Parse(meta.Groups["width"].Value));
                var height = Math.Max(32, int.Parse(meta.Groups["height"].Value));
                var displayName = ResolveSanPresetDisplayName(presetId);
                var slot = new LocalCustomOverlayStyleSlot
                {
                    Name = $"SAN {displayName}",
                    SanPresetId = presetId,
                    SanPresetHtml = indexHtml,
                    SanPresetCss = presetCss,
                    SanBaseCss = baseCss,
                    SanBaseAnimCss = baseAnimCss,
                    SanAssetRootPath = Directory.GetParent(notifyDir)?.FullName ?? string.Empty,
                    LayoutStyle = ResolveSanLayoutStyle(presetId),
                    AnimationStyle = ResolveSanAnimationStyle(presetId),
                    TransitionStyle = ResolveSanTransitionStyle(presetId),
                    SanElementPresetId = string.Empty,
                    AutoResizeToContent = false,
                    WrapAllText = false,
                    ShowLine1 = true,
                    ShowBorder = true,
                    ShowGameName = true,
                    ShowMeta = true,
                    IconSize = Math.Max(24, height - 10),
                    SecondaryIconSize = Math.Max(24, height - 10),
                    Width = width,
                    Height = height,
                    CornerRadius = Math.Max(0, height / 4.0),
                    TitleFontSize = 13,
                    DetailFontSize = 11,
                    MetaFontSize = 9,
                    BackgroundColor = "#203E7A",
                    BorderColor = "#0C2A66",
                    AccentColor = "#FFFFFF",
                    TitleColor = "#FFFFFF",
                    DetailColor = "#FFFFFF",
                    MetaColor = "#C7D5E0",
                    TitleTemplate = ResolveSanTitleTemplate(presetId),
                    GameNameTemplate = ResolveSanGameTemplate(presetId),
                    AchievementTemplate = ResolveSanAchievementTemplate(presetId),
                    MetaTemplate = "<provider> / Custom",
                    TitleBold = true,
                    DetailBold = false,
                    MetaBold = false,
                    IconSource = ResolveSanIconSource(presetId),
                    SecondaryIconSource = ResolveSanSecondaryIconSource(presetId, null),
                    ShowSecondaryIcon = ResolveSanSecondaryIconSource(presetId, null) != LocalOverlayIconSource.None,
                    ShowIconRarityGlow = false
                };

                return new NotificationOverlayTemplateFile
                {
                    SchemaVersion = 1,
                    TemplateName = slot.Name,
                    ExportedAtUtc = DateTime.UtcNow,
                    CustomStyleSlot = slot,
                    Transition = new NotificationOverlayTransitionTemplate
                    {
                        DurationMilliseconds = EstimateSanDisplayDurationMilliseconds(presetId),
                        FadeInMilliseconds = 180,
                        FadeOutMilliseconds = 280,
                        SoundLeadMilliseconds = 0,
                        Opacity = 1,
                        Scale = 1,
                        OverlayPosition = LocalUnlockOverlayPosition.BottomRight.ToString(),
                        TransitionStyle = ResolveSanTransitionStyle(presetId).ToString(),
                        TemplateAnimationStyle = slot.AnimationStyle.ToString(),
                        SlideDistance = 72,
                        IconSize = slot.IconSize,
                        SecondaryIconSize = slot.SecondaryIconSize,
                        IconSource = slot.IconSource.ToString(),
                        SecondaryIconSource = slot.SecondaryIconSource.ToString()
                    }
                };
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to parse SAN preset template.");
                return null;
            }
        }

        private NotificationOverlayTemplateFile TryImportSanExportTemplate(string selectedPath)
        {
            try
            {
                var extension = Path.GetExtension(selectedPath);
                var themeJson = string.Empty;
                var themeDirectory = string.Empty;
                var label = Path.GetFileNameWithoutExtension(selectedPath);

                if (string.Equals(extension, ".san", StringComparison.OrdinalIgnoreCase))
                {
                    themeDirectory = ResolveSanExportDirectory(selectedPath);
                    var siblingTheme = string.IsNullOrWhiteSpace(themeDirectory) ? null : Path.Combine(themeDirectory, "usertheme.json");
                    if (File.Exists(siblingTheme))
                    {
                        themeJson = File.ReadAllText(siblingTheme);
                    }
                    else
                    {
                        using (var archive = ZipFile.OpenRead(selectedPath))
                        {
                            var entry = archive.Entries.FirstOrDefault(item => string.Equals(item.FullName.Replace('\\', '/'), "usertheme.json", StringComparison.OrdinalIgnoreCase));
                            if (entry == null)
                            {
                                return null;
                            }

                            themeDirectory = ExtractSanExportArchive(selectedPath, archive);
                            var extractedTheme = Path.Combine(themeDirectory, "usertheme.json");
                            themeJson = File.Exists(extractedTheme) ? File.ReadAllText(extractedTheme) : ReadZipEntryText(entry);
                        }
                    }
                }
                else if (string.Equals(Path.GetFileName(selectedPath), "usertheme.json", StringComparison.OrdinalIgnoreCase))
                {
                    themeDirectory = Path.GetDirectoryName(selectedPath) ?? string.Empty;
                    themeJson = File.ReadAllText(selectedPath);
                }
                else
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(themeJson))
                {
                    return null;
                }

                var theme = JObject.Parse(themeJson);
                var customisation = theme["customisation"] as JObject;
                if (customisation == null)
                {
                    return null;
                }

                var presetId = ResolveSanExportPresetId(theme, selectedPath, themeDirectory, customisation.Value<string>("preset"));
                if (string.IsNullOrWhiteSpace(presetId))
                {
                    presetId = "default";
                }

                customisation["preset"] = presetId;
                themeJson = theme.ToString(Newtonsoft.Json.Formatting.Indented);

                var presetImport = TryBuildSanPresetTemplateFromId(presetId);
                if (presetImport?.CustomStyleSlot == null)
                {
                    return null;
                }

                label = ResolveSanExportLabel(theme, label, presetId);
                var slot = presetImport.CustomStyleSlot;
                var scale = Math.Max(0.1, (customisation.Value<double?>("scale") ?? 100) / 100.0);
                slot.Name = $"SAN {label}";
                slot.SanThemeJson = themeJson;
                slot.SanThemeDirectory = themeDirectory ?? string.Empty;
                slot.TransitionStyle = ResolveSanTransitionStyle(presetId);
                slot.Width = Math.Max(120, slot.Width * scale);
                slot.Height = Math.Max(LocalSettings.MinCustomOverlayHeight, slot.Height * scale);
                slot.IconSource = ResolveSanIconSource(presetId, customisation);
                slot.SecondaryIconSource = ResolveSanSecondaryIconSource(presetId, customisation);
                slot.ShowSecondaryIcon = slot.SecondaryIconSource != LocalOverlayIconSource.None;
                slot.IconSize = ResolveSanImportedIconSize(slot.Height, customisation.Value<double?>("iconscale"), slot.IconSize);
                slot.SecondaryIconSize = ResolveSanImportedIconSize(slot.Height, customisation.Value<double?>("logoscale"), slot.IconSize);
                slot.CornerRadius = Math.Max(0, (customisation.Value<double?>("roundness") ?? 0) / 4.0 * scale);
                slot.ShowBorder = customisation.Value<bool?>("useoutline") == true;
                slot.EnableGameBannerAsBackground = string.Equals(customisation.Value<string>("bgstyle"), "gameart", StringComparison.OrdinalIgnoreCase);
                slot.EnableGameCoverInOverlay = false;
                slot.BackgroundColor = NormalizeSanThemeColor(customisation.Value<string>("primarycolor"), slot.BackgroundColor);
                slot.BorderColor = NormalizeSanThemeColor(
                    customisation.Value<bool?>("useoutline") == true ? customisation.Value<string>("outlinecolor") : customisation.Value<string>("secondarycolor"),
                    slot.BorderColor);
                slot.AccentColor = NormalizeSanThemeColor(customisation.Value<string>("tertiarycolor"), slot.AccentColor);
                slot.TitleColor = NormalizeSanThemeColor(customisation.Value<string>("fontcolor"), slot.TitleColor);
                slot.DetailColor = slot.TitleColor;
                slot.MetaColor = slot.TitleColor;

                var durationSeconds = Math.Max(1, customisation.Value<int?>("displaytime") ?? 10);
                presetImport.TemplateName = slot.Name;
                presetImport.Transition.DurationMilliseconds = durationSeconds * 1000;
                presetImport.Transition.Scale = scale;
                presetImport.Transition.Opacity = Math.Max(0, Math.Min(1, (customisation.Value<double?>("opacity") ?? 100) / 100.0));
                presetImport.Transition.OverlayPosition = ResolveSanOverlayPosition(customisation.Value<string>("pos")).ToString();
                presetImport.Transition.EnableGameBannerAsBackground = slot.EnableGameBannerAsBackground;
                presetImport.Transition.EnableGameCoverInOverlay = slot.EnableGameCoverInOverlay;
                presetImport.Transition.IconSize = slot.IconSize;
                presetImport.Transition.SecondaryIconSize = slot.SecondaryIconSize;
                presetImport.Transition.IconSource = slot.IconSource.ToString();
                presetImport.Transition.SecondaryIconSource = slot.SecondaryIconSource.ToString();
                return presetImport;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to parse SAN export template.");
                return null;
            }
        }

        private NotificationOverlayTemplateFile TryBuildSanPresetTemplateFromId(string presetId)
        {
            var notifyRoot = ResolveSanNotifyRoot();
            if (string.IsNullOrWhiteSpace(notifyRoot))
            {
                return null;
            }

            var presetDir = Path.Combine(notifyRoot, "presets", presetId);
            var indexPath = Path.Combine(presetDir, "index.html");
            var cssPath = Path.Combine(presetDir, "styles.css");
            var baseCssPath = Path.Combine(notifyRoot, "base.css");
            var baseAnimCssPath = Path.Combine(notifyRoot, "baseanim.css");
            if (!File.Exists(indexPath) || !File.Exists(cssPath) || !File.Exists(baseCssPath) || !File.Exists(baseAnimCssPath))
            {
                return null;
            }

            return BuildSanPresetTemplate(
                presetId,
                presetDir,
                File.ReadAllText(indexPath),
                File.ReadAllText(cssPath),
                File.ReadAllText(baseCssPath),
                File.ReadAllText(baseAnimCssPath),
                Directory.GetParent(notifyRoot)?.FullName ?? string.Empty);
        }

        private NotificationOverlayTemplateFile BuildSanPresetTemplate(string presetId, string presetDir, string indexHtml, string presetCss, string baseCss, string baseAnimCss, string assetRootPath)
        {
            var meta = Regex.Match(indexHtml, "<meta\\s+width=\"(?<width>\\d+)\"\\s+height=\"(?<height>\\d+)\"(?:\\s+offset=\"(?<offset>\\d+)\")?", RegexOptions.IgnoreCase);
            if (!meta.Success)
            {
                return null;
            }

            var width = Math.Max(120, int.Parse(meta.Groups["width"].Value));
            var height = Math.Max(32, int.Parse(meta.Groups["height"].Value));
            var displayName = ResolveSanPresetDisplayName(presetId);
            var slot = new LocalCustomOverlayStyleSlot
            {
                Name = $"SAN {displayName}",
                SanPresetId = presetId,
                SanPresetHtml = indexHtml,
                SanPresetCss = presetCss,
                SanBaseCss = baseCss,
                SanBaseAnimCss = baseAnimCss,
                SanAssetRootPath = assetRootPath,
                LayoutStyle = ResolveSanLayoutStyle(presetId),
                AnimationStyle = ResolveSanAnimationStyle(presetId),
                TransitionStyle = ResolveSanTransitionStyle(presetId),
                SanElementPresetId = string.Empty,
                AutoResizeToContent = false,
                WrapAllText = false,
                ShowLine1 = true,
                ShowBorder = true,
                ShowGameName = true,
                ShowMeta = true,
                IconSize = Math.Max(24, height - 10),
                SecondaryIconSize = Math.Max(24, height - 10),
                Width = width,
                Height = height,
                CornerRadius = Math.Max(0, height / 4.0),
                TitleFontSize = 13,
                DetailFontSize = 11,
                MetaFontSize = 9,
                BackgroundColor = "#203E7A",
                BorderColor = "#0C2A66",
                AccentColor = "#FFFFFF",
                TitleColor = "#FFFFFF",
                DetailColor = "#FFFFFF",
                MetaColor = "#C7D5E0",
                TitleTemplate = ResolveSanTitleTemplate(presetId),
                GameNameTemplate = ResolveSanGameTemplate(presetId),
                AchievementTemplate = ResolveSanAchievementTemplate(presetId),
                MetaTemplate = "<provider> / Custom",
                TitleBold = true,
                DetailBold = false,
                MetaBold = false,
                IconSource = ResolveSanIconSource(presetId),
                SecondaryIconSource = ResolveSanSecondaryIconSource(presetId, null),
                ShowSecondaryIcon = ResolveSanSecondaryIconSource(presetId, null) != LocalOverlayIconSource.None,
                ShowIconRarityGlow = false
            };

            return new NotificationOverlayTemplateFile
            {
                SchemaVersion = 1,
                TemplateName = slot.Name,
                ExportedAtUtc = DateTime.UtcNow,
                CustomStyleSlot = slot,
                Transition = new NotificationOverlayTransitionTemplate
                {
                    DurationMilliseconds = EstimateSanDisplayDurationMilliseconds(presetId),
                    FadeInMilliseconds = 180,
                    FadeOutMilliseconds = 280,
                    SoundLeadMilliseconds = 0,
                    Opacity = 1,
                    Scale = 1,
                    OverlayPosition = LocalUnlockOverlayPosition.BottomRight.ToString(),
                    TransitionStyle = ResolveSanTransitionStyle(presetId).ToString(),
                    TemplateAnimationStyle = slot.AnimationStyle.ToString(),
                    SlideDistance = 72,
                    IconSize = slot.IconSize,
                    SecondaryIconSize = slot.SecondaryIconSize,
                    IconSource = slot.IconSource.ToString(),
                    SecondaryIconSource = slot.SecondaryIconSource.ToString()
                }
            };
        }

        private static string ResolveSanPresetDisplayName(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "xqjan": return "xQjan";
                case "steamdeck": return "The Deck";
                case "epicgames": return "Epic";
                case "xboxone": return "XB Modern";
                case "xbox360": return "XB Classic";
                case "ps5": return "PS Modern";
                case "ps4": return "PS Classic";
                case "ps3": return "PS Retro";
                case "windows": return "Square";
                case "gfwl": return "Aero";
                default: return string.IsNullOrWhiteSpace(presetId) ? "Preset" : presetId;
            }
        }

        private static string ResolveSanExportLabel(JObject theme, string fallback, string presetId)
        {
            var label = theme?.Value<string>("label");
            if (!string.IsNullOrWhiteSpace(label) &&
                !string.Equals(label.Trim(), "Default Main", StringComparison.OrdinalIgnoreCase))
            {
                return label.Trim();
            }

            var themeDir = theme?.Value<string>("userthemedir");
            if (!string.IsNullOrWhiteSpace(themeDir))
            {
                return ResolveSanPresetDisplayName(ResolveSanPresetAlias(themeDir));
            }

            return string.IsNullOrWhiteSpace(fallback)
                ? ResolveSanPresetDisplayName(presetId)
                : fallback.Trim();
        }

        private static string ResolveSanExportPresetId(JObject theme, string selectedPath, string themeDirectory, string rawPreset)
        {
            var presetId = NormalizeSanPresetId(rawPreset);
            var candidates = new[]
            {
                theme?.Value<string>("userthemedir"),
                Path.GetFileName(themeDirectory),
                Path.GetFileNameWithoutExtension(selectedPath),
                Path.GetFileName(Path.GetDirectoryName(selectedPath))
            };

            foreach (var candidate in candidates)
            {
                var inferred = ResolveSanPresetAlias(candidate);
                if (!string.IsNullOrWhiteSpace(inferred) && inferred != "default")
                {
                    return inferred;
                }
            }

            return string.IsNullOrWhiteSpace(presetId) ? "default" : presetId;
        }

        private static string ResolveSanPresetAlias(string value)
        {
            var preset = NormalizeSanPresetId(value);
            switch (preset)
            {
                case "xqjan": return "xqjan";
                case "thedeck":
                case "sandeck":
                case "steamdeck": return "steamdeck";
                case "epic":
                case "epicgames": return "epicgames";
                case "xbmodern":
                case "xboxone": return "xboxone";
                case "xbclassic":
                case "xbox360": return "xbox360";
                case "psmodern":
                case "ps5": return "ps5";
                case "psclassic":
                case "ps4": return "ps4";
                case "psretro":
                case "ps3": return "ps3";
                case "square":
                case "windows": return "windows";
                case "aero":
                case "sanaero":
                case "gfwl": return "gfwl";
                case "default": return "default";
                default: return string.Empty;
            }
        }

        private static string NormalizeSanPresetId(string presetId)
        {
            return Regex.Replace((presetId ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9]+", string.Empty);
        }

        private static string NormalizeSanThemeColor(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var trimmed = value.Trim();
            return Regex.IsMatch(trimmed, "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")
                ? trimmed
                : fallback;
        }

        private static LocalUnlockOverlayPosition ResolveSanOverlayPosition(string sanPosition)
        {
            switch ((sanPosition ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "topleft": return LocalUnlockOverlayPosition.TopLeft;
                case "topcenter": return LocalUnlockOverlayPosition.TopCenter;
                case "bottomleft": return LocalUnlockOverlayPosition.BottomLeft;
                case "bottomcenter": return LocalUnlockOverlayPosition.BottomCenter;
                case "bottomright": return LocalUnlockOverlayPosition.BottomRight;
                case "topright":
                default:
                    return LocalUnlockOverlayPosition.TopRight;
            }
        }

        private static string ResolveSanNotifyRoot()
        {
            var assemblyLocation = typeof(LegacyNotificationSettingsControl).Assembly.Location;
            var pluginDirectory = string.IsNullOrWhiteSpace(assemblyLocation)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyLocation) ?? AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(pluginDirectory, "Resources", "Tools", "SAN-source", "notify")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Tools", "SAN-source", "notify")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "tools", "SAN-source", "notify")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tools", "SAN-source", "notify")),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tools", "SAN-source", "notify")),
                @"E:\Programs\Playnite\CustomExtension\PlayniteAchievements\tools\SAN-source\notify"
            };

            return candidates.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path) &&
                File.Exists(Path.Combine(path, "base.css")) &&
                Directory.Exists(Path.Combine(path, "presets")));
        }

        private static string ResolveSanExportDirectory(string sanPath)
        {
            var directory = Path.GetDirectoryName(sanPath);
            var name = Path.GetFileNameWithoutExtension(sanPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var sibling = Path.Combine(directory, name);
            return File.Exists(Path.Combine(sibling, "usertheme.json")) ? sibling : string.Empty;
        }

        private static string ExtractSanExportArchive(string sanPath, ZipArchive archive)
        {
            var safeName = Regex.Replace(Path.GetFileNameWithoutExtension(sanPath) ?? "SANTheme", "[^a-zA-Z0-9_.-]+", "_");
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PlayniteAchievements",
                "SANThemes",
                safeName);

            Directory.CreateDirectory(target);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(target, relative));
                if (!destination.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                entry.ExtractToFile(destination, true);
            }

            return target;
        }

        private static string ReadZipEntryText(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private static LocalCustomOverlayLayoutStyle ResolveSanLayoutStyle(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "xboxone": return LocalCustomOverlayLayoutStyle.XboxModern;
                case "xbox360": return LocalCustomOverlayLayoutStyle.XboxClassic;
                default: return LocalCustomOverlayLayoutStyle.Standard;
            }
        }

        private static LocalCustomOverlayAnimationStyle ResolveSanAnimationStyle(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "default": return LocalCustomOverlayAnimationStyle.SanDefault;
                case "xboxone": return LocalCustomOverlayAnimationStyle.SanExpandCard;
                case "xqjan": return LocalCustomOverlayAnimationStyle.SanXqjan;
                case "epicgames": return LocalCustomOverlayAnimationStyle.SanEpic;
                case "gfwl": return LocalCustomOverlayAnimationStyle.SanAero;
                default: return LocalCustomOverlayAnimationStyle.SanSlideCard;
            }
        }

        private static LocalCustomOverlayAnimationStyle ResolveSanAnimationStyle(LocalUnlockOverlayTransitionStyle transitionStyle)
        {
            switch (transitionStyle)
            {
                case LocalUnlockOverlayTransitionStyle.SanDefault: return LocalCustomOverlayAnimationStyle.SanDefault;
                case LocalUnlockOverlayTransitionStyle.SanXqjan: return LocalCustomOverlayAnimationStyle.SanXqjan;
                case LocalUnlockOverlayTransitionStyle.SanEpic: return LocalCustomOverlayAnimationStyle.SanEpic;
                case LocalUnlockOverlayTransitionStyle.SanAero: return LocalCustomOverlayAnimationStyle.SanAero;
                case LocalUnlockOverlayTransitionStyle.SanXboxModern: return LocalCustomOverlayAnimationStyle.SanExpandCard;
                case LocalUnlockOverlayTransitionStyle.SanXboxClassic:
                case LocalUnlockOverlayTransitionStyle.SanDeck:
                case LocalUnlockOverlayTransitionStyle.SanPsModern:
                case LocalUnlockOverlayTransitionStyle.SanPsClassic:
                case LocalUnlockOverlayTransitionStyle.SanPsRetro:
                case LocalUnlockOverlayTransitionStyle.SanSquare:
                    return LocalCustomOverlayAnimationStyle.SanSlideCard;
                default: return LocalCustomOverlayAnimationStyle.Standard;
            }
        }

        private static bool IsSanTransitionStyle(LocalUnlockOverlayTransitionStyle transitionStyle)
        {
            switch (transitionStyle)
            {
                case LocalUnlockOverlayTransitionStyle.SanDefault:
                case LocalUnlockOverlayTransitionStyle.SanXqjan:
                case LocalUnlockOverlayTransitionStyle.SanDeck:
                case LocalUnlockOverlayTransitionStyle.SanEpic:
                case LocalUnlockOverlayTransitionStyle.SanXboxModern:
                case LocalUnlockOverlayTransitionStyle.SanXboxClassic:
                case LocalUnlockOverlayTransitionStyle.SanPsModern:
                case LocalUnlockOverlayTransitionStyle.SanPsClassic:
                case LocalUnlockOverlayTransitionStyle.SanPsRetro:
                case LocalUnlockOverlayTransitionStyle.SanSquare:
                case LocalUnlockOverlayTransitionStyle.SanAero:
                    return true;
                default:
                    return false;
            }
        }

        private static LocalUnlockOverlayTransitionStyle ResolveSanTransitionStyle(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "default": return LocalUnlockOverlayTransitionStyle.SanDefault;
                case "xqjan": return LocalUnlockOverlayTransitionStyle.SanXqjan;
                case "steamdeck": return LocalUnlockOverlayTransitionStyle.SanDeck;
                case "epicgames": return LocalUnlockOverlayTransitionStyle.SanEpic;
                case "xboxone": return LocalUnlockOverlayTransitionStyle.SanXboxModern;
                case "xbox360": return LocalUnlockOverlayTransitionStyle.SanXboxClassic;
                case "ps5": return LocalUnlockOverlayTransitionStyle.SanPsModern;
                case "ps4": return LocalUnlockOverlayTransitionStyle.SanPsClassic;
                case "ps3": return LocalUnlockOverlayTransitionStyle.SanPsRetro;
                case "windows": return LocalUnlockOverlayTransitionStyle.SanSquare;
                case "gfwl": return LocalUnlockOverlayTransitionStyle.SanAero;
                default: return LocalUnlockOverlayTransitionStyle.Fade;
            }
        }

        private static LocalOverlayIconSource ResolveSanIconSource(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "xqjan": return LocalOverlayIconSource.ProviderIcon;
                case "epicgames": return LocalOverlayIconSource.AchievementIcon;
                case "default": return LocalOverlayIconSource.TrophyIcon;
                default: return LocalOverlayIconSource.TrophyIcon;
            }
        }

        private static LocalOverlayIconSource ResolveSanIconSource(string presetId, JObject customisation)
        {
            if (customisation?.Value<bool?>("usegameicon") == true ||
                customisation?.Value<bool?>("usecustomimgicon") == true)
            {
                return LocalOverlayIconSource.AchievementIcon;
            }

            var rootIcon = customisation?.Parent?.Parent is JObject theme
                ? theme.Value<string>("icon")
                : null;
            var iconFromPath = ResolveSanIconSourceFromPath(rootIcon);
            return iconFromPath == LocalOverlayIconSource.None
                ? ResolveSanIconSource(presetId)
                : iconFromPath;
        }

        private static LocalOverlayIconSource ResolveSanSecondaryIconSource(string presetId, JObject customisation)
        {
            var preset = (presetId ?? string.Empty).Trim().ToLowerInvariant();
            var customIcon = customisation?["customicons"]?[preset];
            if (customIcon is JObject iconObject)
            {
                var logoSource = ResolveSanIconSourceFromPath(iconObject.Value<string>("logo"));
                if (logoSource != LocalOverlayIconSource.None)
                {
                    return logoSource;
                }

                var decorationSource = ResolveSanIconSourceFromPath(iconObject.Value<string>("decoration"));
                if (decorationSource != LocalOverlayIconSource.None)
                {
                    return decorationSource;
                }
            }

            switch (preset)
            {
                case "default":
                case "epicgames":
                case "xboxone":
                case "xbox360":
                case "ps5":
                case "ps4":
                case "ps3":
                case "windows":
                case "gfwl":
                    return LocalOverlayIconSource.AchievementIcon;
                case "xqjan":
                case "steamdeck":
                    return LocalOverlayIconSource.AchievementIcon;
                default:
                    return LocalOverlayIconSource.None;
            }
        }

        private static LocalOverlayIconSource ResolveSanIconSourceFromPath(string path)
        {
            var value = (path ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
            {
                return LocalOverlayIconSource.None;
            }

            if (value.Contains("trophy"))
            {
                return LocalOverlayIconSource.TrophyIcon;
            }

            if (value.Contains("ribbon") || value.Contains("platform") || value.Contains("provider"))
            {
                return LocalOverlayIconSource.ProviderIcon;
            }

            if (value.Contains("sanlogosquare") ||
                value.Contains("achicon") ||
                value.Contains("achievement") ||
                value.Contains("gameicon"))
            {
                return LocalOverlayIconSource.AchievementIcon;
            }

            return LocalOverlayIconSource.None;
        }

        private static double ResolveSanImportedIconSize(double height, double? sanScalePercent, double fallback)
        {
            var baseSize = Math.Max(24, height - 10);
            var scale = Math.Max(0.1, (sanScalePercent ?? 100) / 100.0);
            var size = baseSize * scale;
            if (double.IsNaN(size) || double.IsInfinity(size) || size <= 0)
            {
                size = fallback > 0 ? fallback : baseSize;
            }

            return Math.Max(24, Math.Min(220, size));
        }

        private static string ResolveSanTitleTemplate(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "steamdeck":
                case "epicgames":
                case "ps5": return "<achievementName>";
                case "ps4":
                case "ps3": return "Trophy earned!";
                case "xboxone":
                case "xbox360":
                case "gfwl": return "Achievement unlocked";
                default: return "Achievement unlocked";
            }
        }

        private static string ResolveSanGameTemplate(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "xboxone":
                case "gfwl": return "G <points> - <achievementName>";
                case "xbox360": return "G <points> - <gameName>";
                case "epicgames":
                default: return "<achievementName>";
            }
        }

        private static string ResolveSanAchievementTemplate(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "default":
                case "epicgames":
                case "xboxone":
                case "ps5":
                case "ps4":
                case "ps3":
                case "gfwl": return string.Empty;
                case "xbox360": return string.Empty;
                default: return "<achievementDescription>";
            }
        }

        private static int EstimateSanDisplayDurationMilliseconds(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "epicgames": return 9000;
                case "xboxone": return 9250;
                case "default":
                case "xqjan":
                case "ps4": return 10250;
                case "ps5": return 10900;
                case "xbox360":
                case "gfwl": return 11700;
                case "ps3":
                case "windows": return 12300;
                case "steamdeck": return 13000;
                default: return 10000;
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
            var assemblyDir = Path.GetDirectoryName(typeof(LegacyNotificationSettingsControl).Assembly.Location) ?? string.Empty;
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

        private void ApplyCustomStyleSlotToEditor(Providers.Local.LocalSettings localSettings, LocalCustomOverlayStyleSlot slot)
        {
            if (localSettings == null || slot == null)
            {
                return;
            }

            localSettings.OverlayCustomWidth = slot.Width;
            localSettings.OverlayCustomHeight = slot.Height;
            localSettings.OverlayCustomCornerRadius = slot.CornerRadius;
            localSettings.OverlayCustomIconSize = slot.IconSize;
            localSettings.OverlayCustomSecondaryIconSize = slot.SecondaryIconSize <= 0 ? slot.IconSize : slot.SecondaryIconSize;
            localSettings.OverlayCustomIconCornerRadius = slot.IconCornerRadius;
            localSettings.OverlayCustomSecondaryIconCornerRadius = slot.SecondaryIconCornerRadius;
            localSettings.OverlayCustomTitleFontSize = slot.TitleFontSize;
            localSettings.OverlayCustomDetailFontSize = slot.DetailFontSize;
            localSettings.OverlayCustomMetaFontSize = slot.MetaFontSize;
            localSettings.OverlayCustomLine4FontSize = slot.Line4FontSize;
            localSettings.OverlayCustomLine5FontSize = slot.Line5FontSize;
            localSettings.OverlayCustomLine6FontSize = slot.Line6FontSize;
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
            localSettings.OverlayCustomLine4Template = slot.Line4Template;
            localSettings.OverlayCustomLine5Template = slot.Line5Template;
            localSettings.OverlayCustomLine6Template = slot.Line6Template;
            localSettings.OverlayCustomShowLine4 = slot.ShowLine4;
            localSettings.OverlayCustomShowLine5 = slot.ShowLine5;
            localSettings.OverlayCustomShowLine6 = slot.ShowLine6;
            localSettings.OverlayCustomLine4Color = slot.Line4Color;
            localSettings.OverlayCustomLine5Color = slot.Line5Color;
            localSettings.OverlayCustomLine6Color = slot.Line6Color;
            localSettings.OverlayCustomLine1OutlineColor = slot.Line1OutlineColor;
            localSettings.OverlayCustomLine2OutlineColor = slot.Line2OutlineColor;
            localSettings.OverlayCustomLine3OutlineColor = slot.Line3OutlineColor;
            localSettings.OverlayCustomLine4OutlineColor = slot.Line4OutlineColor;
            localSettings.OverlayCustomLine5OutlineColor = slot.Line5OutlineColor;
            localSettings.OverlayCustomLine6OutlineColor = slot.Line6OutlineColor;
            localSettings.OverlayCustomLine1ShadowColor = slot.Line1ShadowColor;
            localSettings.OverlayCustomLine2ShadowColor = slot.Line2ShadowColor;
            localSettings.OverlayCustomLine3ShadowColor = slot.Line3ShadowColor;
            localSettings.OverlayCustomLine4ShadowColor = slot.Line4ShadowColor;
            localSettings.OverlayCustomLine5ShadowColor = slot.Line5ShadowColor;
            localSettings.OverlayCustomLine6ShadowColor = slot.Line6ShadowColor;
            localSettings.OverlayCustomLine1OutlineSize = slot.Line1OutlineSize;
            localSettings.OverlayCustomLine2OutlineSize = slot.Line2OutlineSize;
            localSettings.OverlayCustomLine3OutlineSize = slot.Line3OutlineSize;
            localSettings.OverlayCustomLine4OutlineSize = slot.Line4OutlineSize;
            localSettings.OverlayCustomLine5OutlineSize = slot.Line5OutlineSize;
            localSettings.OverlayCustomLine6OutlineSize = slot.Line6OutlineSize;
            localSettings.OverlayCustomLine1ShadowSize = slot.Line1ShadowSize;
            localSettings.OverlayCustomLine2ShadowSize = slot.Line2ShadowSize;
            localSettings.OverlayCustomLine3ShadowSize = slot.Line3ShadowSize;
            localSettings.OverlayCustomLine4ShadowSize = slot.Line4ShadowSize;
            localSettings.OverlayCustomLine5ShadowSize = slot.Line5ShadowSize;
            localSettings.OverlayCustomLine6ShadowSize = slot.Line6ShadowSize;
            localSettings.OverlayCustomOutlineSize = slot.OutlineSize;
            localSettings.OverlayCustomShadowSize = slot.ShadowSize;
            localSettings.OverlayCustomLine1OutlineEnabled = slot.Line1OutlineEnabled;
            localSettings.OverlayCustomLine2OutlineEnabled = slot.Line2OutlineEnabled;
            localSettings.OverlayCustomLine3OutlineEnabled = slot.Line3OutlineEnabled;
            localSettings.OverlayCustomLine4OutlineEnabled = slot.Line4OutlineEnabled;
            localSettings.OverlayCustomLine5OutlineEnabled = slot.Line5OutlineEnabled;
            localSettings.OverlayCustomLine6OutlineEnabled = slot.Line6OutlineEnabled;
            localSettings.OverlayCustomLine1ShadowEnabled = slot.Line1ShadowEnabled;
            localSettings.OverlayCustomLine2ShadowEnabled = slot.Line2ShadowEnabled;
            localSettings.OverlayCustomLine3ShadowEnabled = slot.Line3ShadowEnabled;
            localSettings.OverlayCustomLine4ShadowEnabled = slot.Line4ShadowEnabled;
            localSettings.OverlayCustomLine5ShadowEnabled = slot.Line5ShadowEnabled;
            localSettings.OverlayCustomLine6ShadowEnabled = slot.Line6ShadowEnabled;
            localSettings.OverlayCustomLineSpacing = slot.LineSpacing;
            localSettings.OverlayCustomLine1Spacing = slot.Line1Spacing;
            localSettings.OverlayCustomLine2Spacing = slot.Line2Spacing;
            localSettings.OverlayCustomLine3Spacing = slot.Line3Spacing;
            localSettings.OverlayCustomLine4Spacing = slot.Line4Spacing;
            localSettings.OverlayCustomLine5Spacing = slot.Line5Spacing;
            localSettings.OverlayCustomLine6Spacing = slot.Line6Spacing;
            localSettings.OverlayCustomLine1View = slot.Line1View;
            localSettings.OverlayCustomLine2View = slot.Line2View;
            localSettings.OverlayCustomLine3View = slot.Line3View;
            localSettings.OverlayCustomLine4View = slot.Line4View;
            localSettings.OverlayCustomLine5View = slot.Line5View;
            localSettings.OverlayCustomLine6View = slot.Line6View;
            localSettings.OverlayCustomLine1Order = slot.Line1Order;
            localSettings.OverlayCustomLine2Order = slot.Line2Order;
            localSettings.OverlayCustomLine3Order = slot.Line3Order;
            localSettings.OverlayCustomLine4Order = slot.Line4Order;
            localSettings.OverlayCustomLine5Order = slot.Line5Order;
            localSettings.OverlayCustomLine6Order = slot.Line6Order;
            localSettings.OverlayCustomLayoutStyle = slot.LayoutStyle;
            localSettings.OverlayCustomAnimationStyle = slot.AnimationStyle;
            localSettings.OverlayCustomSecondaryIconSource = slot.SecondaryIconSource;
            localSettings.OverlayCustomShowSecondaryIcon = slot.ShowSecondaryIcon;
            localSettings.OverlayCustomSanElementPresetId = slot.SanElementPresetId;
            localSettings.OverlayCustomSanElementPosition = slot.SanElementPosition;
            localSettings.OverlayCustomSanProviderLogoSource = slot.SanProviderLogoSource;
            localSettings.UnlockOverlayTransitionStyle = slot.TransitionStyle;
            localSettings.OverlayCustomSanPresetId = slot.SanPresetId;
            localSettings.OverlayCustomSanPresetHtml = slot.SanPresetHtml;
            localSettings.OverlayCustomSanPresetCss = slot.SanPresetCss;
            localSettings.OverlayCustomSanBaseCss = slot.SanBaseCss;
            localSettings.OverlayCustomSanBaseAnimCss = slot.SanBaseAnimCss;
            localSettings.OverlayCustomSanAssetRootPath = slot.SanAssetRootPath;
            localSettings.OverlayCustomSanThemeJson = slot.SanThemeJson;
            localSettings.OverlayCustomSanThemeDirectory = slot.SanThemeDirectory;
            localSettings.OverlayCustomManualElementCss = slot.ManualElementCss;
            localSettings.OverlayCustomSanView1DurationMilliseconds = slot.SanView1DurationMilliseconds <= 0 ? 5000 : slot.SanView1DurationMilliseconds;
            localSettings.OverlayCustomSanView2DurationMilliseconds = slot.SanView2DurationMilliseconds <= 0 ? 5000 : slot.SanView2DurationMilliseconds;
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
            localSettings.OverlayCustomLine4Bold = slot.Line4Bold;
            localSettings.OverlayCustomLine4Italic = slot.Line4Italic;
            localSettings.OverlayCustomLine4Underline = slot.Line4Underline;
            localSettings.OverlayCustomLine4Strikethrough = slot.Line4Strikethrough;
            localSettings.OverlayCustomLine5Bold = slot.Line5Bold;
            localSettings.OverlayCustomLine5Italic = slot.Line5Italic;
            localSettings.OverlayCustomLine5Underline = slot.Line5Underline;
            localSettings.OverlayCustomLine5Strikethrough = slot.Line5Strikethrough;
            localSettings.OverlayCustomLine6Bold = slot.Line6Bold;
            localSettings.OverlayCustomLine6Italic = slot.Line6Italic;
            localSettings.OverlayCustomLine6Underline = slot.Line6Underline;
            localSettings.OverlayCustomLine6Strikethrough = slot.Line6Strikethrough;
            localSettings.OverlayCustomIconSource = slot.IconSource;
            var primaryCustomIconPath = PrepareImportedCustomIconPath(slot.CustomIconPath, "primary");
            var secondaryCustomIconPath = PrepareImportedCustomIconPath(slot.SecondaryCustomIconPath, "secondary");
            localSettings.OverlayCustomIconPath = primaryCustomIconPath;
            localSettings.OverlayCustomSecondaryIconPath = secondaryCustomIconPath;
            AddImportedCustomIconsToLibrary(localSettings, primaryCustomIconPath, secondaryCustomIconPath);
            localSettings.OverlayCustomShowIconRarityGlow = slot.ShowIconRarityGlow;
            localSettings.OverlayCustomShowSecondaryIconRarityGlow = slot.ShowSecondaryIconRarityGlow;
            localSettings.EnableGameCoverInOverlay = slot.EnableGameCoverInOverlay;
            localSettings.GameCoverPosition = slot.GameCoverPosition;
            localSettings.GameCoverWidth = slot.GameCoverWidth;
            localSettings.EnableGameBannerAsBackground = slot.EnableGameBannerAsBackground;
            localSettings.GameBannerOpacity = slot.GameBannerOpacity;
            localSettings.GameBannerBlurRadius = slot.GameBannerBlurRadius;
            localSettings.OverlayCustomCoverImagePath = slot.CustomCoverImagePath;
            localSettings.OverlayCustomBannerImagePath = slot.CustomBannerImagePath;
        }

        private string PrepareImportedCustomIconPath(string rawPath, string slotName)
        {
            var value = rawPath?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return SaveTemplateDataUriCustomIcon(value, slotName);
            }

            if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    value = new Uri(value).LocalPath;
                }
                catch
                {
                    return string.Empty;
                }
            }

            if (!File.Exists(value))
            {
                return string.Empty;
            }

            var managedPath = CopyNotificationCustomIconToDataPath(value);
            if (!string.IsNullOrWhiteSpace(managedPath))
            {
                SetNotificationCustomIconSourcePath(managedPath, value);
                return managedPath;
            }

            return value;
        }

        private string SaveTemplateDataUriCustomIcon(string dataUri, string slotName)
        {
            try
            {
                var commaIndex = dataUri.IndexOf(',');
                if (commaIndex <= 0)
                {
                    return string.Empty;
                }

                var header = dataUri.Substring(0, commaIndex);
                if (header.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return string.Empty;
                }

                var extension = ".png";
                if (header.IndexOf("svg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    extension = ".svg";
                }
                else if (header.IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         header.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    extension = ".jpg";
                }
                else if (header.IndexOf("webp", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    extension = ".webp";
                }
                else if (header.IndexOf("bmp", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    extension = ".bmp";
                }

                var dataPath = _plugin?.GetPluginUserDataPath();
                if (string.IsNullOrWhiteSpace(dataPath))
                {
                    return string.Empty;
                }

                var destinationDirectory = Path.Combine(dataPath, "notification_custom_icons");
                Directory.CreateDirectory(destinationDirectory);
                var safeSlotName = Regex.Replace(slotName ?? "icon", @"[^a-zA-Z0-9._-]+", "_").Trim('_');
                if (string.IsNullOrWhiteSpace(safeSlotName))
                {
                    safeSlotName = "icon";
                }

                var destinationPath = Path.Combine(destinationDirectory, $"template-{safeSlotName}-{Guid.NewGuid():N}{extension}");
                File.WriteAllBytes(destinationPath, Convert.FromBase64String(dataUri.Substring(commaIndex + 1)));
                SetNotificationCustomIconSourcePath(destinationPath, "Embedded in imported notification template");
                return destinationPath;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to save embedded custom icon from notification template.");
                return string.Empty;
            }
        }

        private static void AddImportedCustomIconsToLibrary(Providers.Local.LocalSettings localSettings, params string[] paths)
        {
            if (localSettings == null || paths == null)
            {
                return;
            }

            var library = localSettings.GetOverlayCustomIconLibraryPathEntries().ToList();
            var changed = false;
            foreach (var path in paths)
            {
                var value = path?.Trim();
                if (string.IsNullOrWhiteSpace(value) ||
                    value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    library.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                library.Add(value);
                changed = true;
            }

            if (changed)
            {
                localSettings.SetOverlayCustomIconLibraryPathEntries(library);
            }
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
            var previewAchievement = GetSelectedNotificationPreviewAchievement();
            var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
            NotificationsCustomInlinePreviewHost.Content = publisher.CreateOverlayPreviewContent(
                previewGameName,
                previewAchievement?.DisplayName ?? "Sample Achievement",
                forcedStyle: NotificationPublisher.NotificationStyleCustom,
                providerKey: ResolveCurrentNotificationTestProviderKey(),
                overrideLocalSettings: localSettings,
                achievementIconPath: previewAchievement?.UnlockedIconPath,
                game: previewGame,
                achievementDescription: previewAchievement?.Description ?? "Win your first fight without taking damage.",
                achievementPoints: previewAchievement?.Points ?? 25,
                achievementRarity: FormatPreviewAchievementRarity(previewAchievement, "12.7%"),
                achievementTrophy: previewAchievement?.TrophyType ?? "Gold");

            if (ShouldLoopCustomInlinePreview(localSettings))
            {
                var displayMs = Math.Max(1200, localSettings.UnlockOverlayDurationMilliseconds);
                var transitionMs = Math.Max(150, localSettings.UnlockOverlayFadeInMilliseconds + localSettings.UnlockOverlayFadeOutMilliseconds);
                _notificationInlinePreviewLoopTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1800, displayMs + transitionMs + 250));
                if (!_notificationInlinePreviewLoopTimer.IsEnabled)
                {
                    _notificationInlinePreviewLoopTimer.Start();
                }
            }
            else
            {
                _notificationInlinePreviewLoopTimer.Stop();
            }
        }

        private static bool ShouldLoopCustomInlinePreview(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null)
            {
                return false;
            }

            return IsSanTransitionStyle(localSettings.UnlockOverlayTransitionStyle)
                || !string.IsNullOrWhiteSpace(localSettings.OverlayCustomSanElementPresetId);
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
                case "Line4":
                    return localSettings.OverlayCustomLine4Color;
                case "Line5":
                    return localSettings.OverlayCustomLine5Color;
                case "Line6":
                    return localSettings.OverlayCustomLine6Color;
                case "Line1Outline":
                    return localSettings.OverlayCustomLine1OutlineColor;
                case "Line2Outline":
                    return localSettings.OverlayCustomLine2OutlineColor;
                case "Line3Outline":
                    return localSettings.OverlayCustomLine3OutlineColor;
                case "Line4Outline":
                    return localSettings.OverlayCustomLine4OutlineColor;
                case "Line5Outline":
                    return localSettings.OverlayCustomLine5OutlineColor;
                case "Line6Outline":
                    return localSettings.OverlayCustomLine6OutlineColor;
                case "Line1Shadow":
                    return localSettings.OverlayCustomLine1ShadowColor;
                case "Line2Shadow":
                    return localSettings.OverlayCustomLine2ShadowColor;
                case "Line3Shadow":
                    return localSettings.OverlayCustomLine3ShadowColor;
                case "Line4Shadow":
                    return localSettings.OverlayCustomLine4ShadowColor;
                case "Line5Shadow":
                    return localSettings.OverlayCustomLine5ShadowColor;
                case "Line6Shadow":
                    return localSettings.OverlayCustomLine6ShadowColor;
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
                case "Line4":
                    localSettings.OverlayCustomLine4Color = colorHex;
                    break;
                case "Line5":
                    localSettings.OverlayCustomLine5Color = colorHex;
                    break;
                case "Line6":
                    localSettings.OverlayCustomLine6Color = colorHex;
                    break;
                case "Line1Outline":
                    localSettings.OverlayCustomLine1OutlineColor = colorHex;
                    break;
                case "Line2Outline":
                    localSettings.OverlayCustomLine2OutlineColor = colorHex;
                    break;
                case "Line3Outline":
                    localSettings.OverlayCustomLine3OutlineColor = colorHex;
                    break;
                case "Line4Outline":
                    localSettings.OverlayCustomLine4OutlineColor = colorHex;
                    break;
                case "Line5Outline":
                    localSettings.OverlayCustomLine5OutlineColor = colorHex;
                    break;
                case "Line6Outline":
                    localSettings.OverlayCustomLine6OutlineColor = colorHex;
                    break;
                case "Line1Shadow":
                    localSettings.OverlayCustomLine1ShadowColor = colorHex;
                    break;
                case "Line2Shadow":
                    localSettings.OverlayCustomLine2ShadowColor = colorHex;
                    break;
                case "Line3Shadow":
                    localSettings.OverlayCustomLine3ShadowColor = colorHex;
                    break;
                case "Line4Shadow":
                    localSettings.OverlayCustomLine4ShadowColor = colorHex;
                    break;
                case "Line5Shadow":
                    localSettings.OverlayCustomLine5ShadowColor = colorHex;
                    break;
                case "Line6Shadow":
                    localSettings.OverlayCustomLine6ShadowColor = colorHex;
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
            CommitThemeMigrationControls();
            await ExecuteThemeMigrationAsync(MigrationMode.Limited);
        }

        private async void MigrateThemeFull_Click(object sender, RoutedEventArgs e)
        {
            CommitThemeMigrationControls();
            await ExecuteThemeMigrationAsync(MigrationMode.Full, BuildFullMigrationSelection());
        }

        private async void MigrateThemeCustom_Click(object sender, RoutedEventArgs e)
        {
            CommitThemeMigrationControls();
            await ExecuteThemeMigrationAsync(MigrationMode.Custom, BuildCustomMigrationSelection());
        }

        private void LegacyThemeMigrationThemeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            CommitThemeMigrationControls();
        }

        private void CommitThemeMigrationControls()
        {
            if (LegacyThemeMigrationThemeComboBox?.SelectedItem is
                ThemeDiscoveryService.ThemeInfo selectedTheme)
            {
                SelectedThemePath = selectedTheme.Path;
            }

            if (LegacyUseScrollableAchievementsCheckBox?.IsChecked is bool useScrollable)
            {
                UseScrollableAchievementsForThemeMigration = useScrollable;
            }

            if (LegacyHighlightLatestAchievementCheckBox?.IsChecked is bool highlightLatest)
            {
                HighlightLatestAchievementForThemeMigration = highlightLatest;
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
            var selectedThemePath = SelectedThemePath;
            if (string.IsNullOrWhiteSpace(selectedThemePath))
            {
                _logger.Warn("Migrate clicked but no theme selected.");
                return;
            }

            var highlightLatest = customSelection?.HighlightLatestUnlockedAchievement;
            var useScrollable = customSelection?.ModernizeCompactAchievementLists;
            _logger.Info(
                $"User requested {mode} theme migration for: {selectedThemePath}; " +
                $"useScrollableAchievements={useScrollable?.ToString() ?? "not-applicable"}; " +
                $"highlightLatestUnlockedAchievement={highlightLatest?.ToString() ?? "not-applicable"}");

            try
            {
                var selectedThemeHasBackup = _themeMigration.HasBackup(selectedThemePath);
                if (selectedThemeHasBackup)
                {
                    _logger.Info($"Selected theme already has backup. Reverting before migration: {selectedThemePath}");

                    var revertResult = await _themeMigration.RevertThemeAsync(selectedThemePath);
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

                var result = await _themeMigration.MigrateThemeAsync(
                    selectedThemePath,
                    mode,
                    customSelection);

                if (result.Success)
                {
                    _logger.Info($"Theme migration ({mode}) successful: {selectedThemePath}");

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
            if (d is LegacyNotificationSettingsControl control)
            {
                control.SetCompactAchievementMigrationOptions((bool)e.NewValue);
            }
        }

        private static void OnSelectedThemePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control)
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
                ModernizeCompactAchievementLists = UseScrollableAchievementsForThemeMigration,
                HighlightLatestUnlockedAchievement =
                    HighlightLatestAchievementForThemeMigration
            };
        }

        private CustomMigrationSelection BuildFullMigrationSelection()
        {
            return new CustomMigrationSelection(
                ControlMappings.LegacyToModernControlNames.Keys,
                modernizeBindings: true)
            {
                ModernizeCompactAchievementLists = UseScrollableAchievementsForThemeMigration,
                HighlightLatestUnlockedAchievement =
                    HighlightLatestAchievementForThemeMigration
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

        private void StartPageActivityScopeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            OpenStartPageActivityScopeContextMenu(sender as Button);
        }

        private void StartPageProgressScopeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            OpenStartPageProgressScopeContextMenu(sender as Button);
        }

        private void OpenStartPageActivityScopeContextMenu(Button button)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (button == null || persisted == null)
            {
                return;
            }

            var options = new[]
            {
                new { Scope = GameActivityScope.Played, Label = L("LOCPlayAch_Filter_Played", "Played") },
                new { Scope = GameActivityScope.Unplayed, Label = L("LOCPlayAch_Filter_Unplayed", "Unplayed") }
            };

            var menu = PrepareStartPageScopeContextMenu(button);
            if (menu == null)
            {
                return;
            }

            var current = persisted.StartPageActivityScope;
            foreach (var option in options)
            {
                var scope = option.Scope;
                var item = CreateStartPageScopeMenuItem(
                    button,
                    option.Label,
                    current.HasFlag(scope),
                    isChecked =>
                    {
                        var settings = _settingsViewModel?.Settings?.Persisted;
                        if (settings == null)
                        {
                            return;
                        }

                        settings.StartPageActivityScope = isChecked
                            ? settings.StartPageActivityScope | scope
                            : settings.StartPageActivityScope & ~scope;
                        UpdateStartPageScopeTexts();
                    });
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private void OpenStartPageProgressScopeContextMenu(Button button)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (button == null || persisted == null)
            {
                return;
            }

            var options = new[]
            {
                new { Scope = GameProgressScope.Completed, Label = L("LOCPlayAch_Filter_Complete", "Complete") },
                new { Scope = GameProgressScope.InProgress, Label = L("LOCPlayAch_Filter_InProgress", "In Progress") },
                new { Scope = GameProgressScope.NoProgress, Label = L("LOCPlayAch_Filter_NoProgress", "No Progress") }
            };

            var menu = PrepareStartPageScopeContextMenu(button);
            if (menu == null)
            {
                return;
            }

            var current = persisted.StartPageProgressScope;
            foreach (var option in options)
            {
                var scope = option.Scope;
                var item = CreateStartPageScopeMenuItem(
                    button,
                    option.Label,
                    current.HasFlag(scope),
                    isChecked =>
                    {
                        var settings = _settingsViewModel?.Settings?.Persisted;
                        if (settings == null)
                        {
                            return;
                        }

                        settings.StartPageProgressScope = isChecked
                            ? settings.StartPageProgressScope | scope
                            : settings.StartPageProgressScope & ~scope;
                        UpdateStartPageScopeTexts();
                    });
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static ContextMenu PrepareStartPageScopeContextMenu(Button button)
        {
            var menu = button?.ContextMenu;
            if (menu == null)
            {
                return null;
            }

            menu.Items.Clear();
            return menu;
        }

        private static MenuItem CreateStartPageScopeMenuItem(
            Button button,
            string header,
            bool isChecked,
            Action<bool> setSelection)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = isChecked
            };

            var itemStyle = button?.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            if (itemStyle != null)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, __) => setSelection?.Invoke(item.IsChecked);
            return item;
        }

        private static void OpenSelectorContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null || menu.Items.Count == 0)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
        }

        private void UpdateStartPageScopeTexts()
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            var activityScope = persisted?.StartPageActivityScope ??
                PersistedSettings.DefaultStartPageActivityScope;
            var progressScope = persisted?.StartPageProgressScope ??
                PersistedSettings.DefaultStartPageProgressScope;

            StartPageActivityScopeText = GetActivityScopeText(activityScope);
            StartPageProgressScopeText = GetProgressScopeText(progressScope);
        }

        private static string GetActivityScopeText(GameActivityScope scope)
        {
            scope = PersistedSettings.NormalizeStartPageActivityScope(scope);
            if (scope == GameActivityScope.None)
            {
                return L("LOCPlayAch_Filter_ActivitySelectorPlaceholder", "Activity");
            }

            var labels = new List<string>();
            if (scope.HasFlag(GameActivityScope.Played))
            {
                labels.Add(L("LOCPlayAch_Filter_Played", "Played"));
            }

            if (scope.HasFlag(GameActivityScope.Unplayed))
            {
                labels.Add(L("LOCPlayAch_Filter_Unplayed", "Unplayed"));
            }

            return labels.Count > 0
                ? string.Join(", ", labels)
                : L("LOCPlayAch_Filter_ActivitySelectorPlaceholder", "Activity");
        }

        private static string GetProgressScopeText(GameProgressScope scope)
        {
            scope = PersistedSettings.NormalizeStartPageProgressScope(scope);
            if (scope == GameProgressScope.None)
            {
                return L("LOCPlayAch_Progress", "Progress");
            }

            var labels = new List<string>();
            if (scope.HasFlag(GameProgressScope.Completed))
            {
                labels.Add(L("LOCPlayAch_Filter_Complete", "Complete"));
            }

            if (scope.HasFlag(GameProgressScope.InProgress))
            {
                labels.Add(L("LOCPlayAch_Filter_InProgress", "In Progress"));
            }

            if (scope.HasFlag(GameProgressScope.NoProgress))
            {
                labels.Add(L("LOCPlayAch_Filter_NoProgress", "No Progress"));
            }

            return labels.Count > 0
                ? string.Join(", ", labels)
                : L("LOCPlayAch_Progress", "Progress");
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
        private void BuildLegacyProviderNavigationItems()
        {
            if (_providerNavigationBuilt)
            {
                return;
            }

            LegacyProviderNavigationItems.Clear();

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

                LegacyProviderNavigationItems.Add(new LegacyProviderNavigationItem(
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

            if (SelectedLegacyProviderNavigationItem == null && LegacyProviderNavigationItems.Count > 0)
            {
                SelectedLegacyProviderNavigationItem = LegacyProviderNavigationItems[0];
            }

            _providerNavigationBuilt = true;
            _logger?.Info($"Built {LegacyProviderNavigationItems.Count} platform navigation items");
        }

        private static string GetProviderSettingsGroupName(string providerKey)
        {
            return ResourceProvider.GetString(ProviderUiPolicies.GetSettingsGroupResourceKey(providerKey));
        }

        private void ConfigureProviderNavigationView()
        {
            _providerNavigationView = CollectionViewSource.GetDefaultView(LegacyProviderNavigationItems);
            if (_providerNavigationView == null)
            {
                return;
            }

            _providerNavigationView.Filter = FilterLegacyProviderNavigationItem;
            _providerNavigationView.GroupDescriptions.Clear();
            _providerNavigationView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(LegacyProviderNavigationItem.GroupName)));
            _providerNavigationView.Refresh();
        }

        private bool FilterLegacyProviderNavigationItem(object item)
        {
            if (!(item is LegacyProviderNavigationItem providerItem))
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
            if (d is LegacyNotificationSettingsControl control)
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

            if (SelectedLegacyProviderNavigationItem != null &&
                _providerNavigationView.Contains(SelectedLegacyProviderNavigationItem))
            {
                return;
            }

            SelectedLegacyProviderNavigationItem = _providerNavigationView
                .Cast<LegacyProviderNavigationItem>()
                .FirstOrDefault();
        }

        private static void OnSelectedLegacyProviderNavigationItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control && e.NewValue is LegacyProviderNavigationItem item)
            {
                control.OnSelectedLegacyProviderNavigationItemChangedInternal(item);
            }
        }

        private async void OnSelectedLegacyProviderNavigationItemChangedInternal(LegacyProviderNavigationItem item)
        {
            if (item == null) return;

            if (item.IsRedirect)
            {
                var redirectItem = LegacyProviderNavigationItems?.FirstOrDefault(x =>
                    string.Equals(x.ProviderKey, item.RedirectProviderKey, StringComparison.OrdinalIgnoreCase));

                if (redirectItem != null && !ReferenceEquals(item, redirectItem))
                {
                    ProviderSearchText = string.Empty;
                    SelectedLegacyProviderNavigationItem = redirectItem;
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
                var targetItem = LegacyProviderNavigationItems?.FirstOrDefault(x =>
                    string.Equals(x.ProviderKey, targetKey, StringComparison.OrdinalIgnoreCase));

                if (targetItem != null && ProvidersTab != null)
                {
                    SettingsTabControl.SelectedItem = ProvidersTab;
                    SelectedLegacyProviderNavigationItem = targetItem;
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

        private void ViewAchievementsHotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyCapture(HotkeyCaptureTarget.ViewAchievements, ViewAchievementsHotkeyCaptureButton);
        }

        private void ManageAchievementsHotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyCapture(HotkeyCaptureTarget.ManageAchievements, ManageAchievementsHotkeyCaptureButton);
        }

        private void OverviewHotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyCapture(HotkeyCaptureTarget.Overview, OverviewHotkeyCaptureButton);
        }

        private void ResetViewAchievementsHotkey_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.ViewAchievementsHotkey = PersistedSettings.DefaultViewAchievementsHotkey;
            EndHotkeyCapture();
        }

        private void ResetManageAchievementsHotkey_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.ManageAchievementsHotkey = PersistedSettings.DefaultManageAchievementsHotkey;
            EndHotkeyCapture();
        }

        private void ResetOverviewHotkey_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.OverviewHotkey = PersistedSettings.DefaultOverviewHotkey;
            EndHotkeyCapture();
        }

        private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey.HasValue)
            {
                return;
            }

            var target = _capturingHotkey.Value;
            if ((target == HotkeyCaptureTarget.ViewAchievements &&
                 !ReferenceEquals(sender, ViewAchievementsHotkeyCaptureButton)) ||
                (target == HotkeyCaptureTarget.ManageAchievements &&
                 !ReferenceEquals(sender, ManageAchievementsHotkeyCaptureButton)) ||
                (target == HotkeyCaptureTarget.Overview &&
                 !ReferenceEquals(sender, OverviewHotkeyCaptureButton)))
            {
                return;
            }

            e.Handled = true;
            var key = GetEffectiveHotkeyCaptureKey(e);
            if (key == Key.Escape)
            {
                EndHotkeyCapture();
                return;
            }

            if (key == Key.Back || key == Key.Delete)
            {
                SetCapturedHotkey(target, string.Empty);
                EndHotkeyCapture();
                return;
            }

            if (!AchievementHotkeyGesture.TryCreate(key, Keyboard.Modifiers, out var gesture))
            {
                HotkeyCaptureStatusText = L(
                    "LOCPlayAch_Hotkeys_InvalidShortcut",
                    "Unsupported shortcut. Press a letter, digit, function key, or a modified shortcut.");
                return;
            }

            if (IsDuplicateHotkey(target, gesture))
            {
                HotkeyCaptureStatusText = L(
                    "LOCPlayAch_Hotkeys_DuplicateShortcut",
                    "That shortcut is already assigned.");
                return;
            }

            SetCapturedHotkey(target, gesture.ToString());
            EndHotkeyCapture();
        }

        private void StartHotkeyCapture(HotkeyCaptureTarget target, Button button)
        {
            _capturingHotkey = target;
            HotkeyCaptureStatusText = L("LOCPlayAch_Hotkeys_CapturePrompt", "Press a shortcut...");

            if (target == HotkeyCaptureTarget.ViewAchievements)
            {
                ViewAchievementsHotkeyButtonText = L("LOCPlayAch_Hotkeys_CaptureButton", "Press keys...");
                ManageAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ManageAchievementsHotkey);
                OverviewHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.OverviewHotkey);
            }
            else if (target == HotkeyCaptureTarget.ManageAchievements)
            {
                ManageAchievementsHotkeyButtonText = L("LOCPlayAch_Hotkeys_CaptureButton", "Press keys...");
                ViewAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ViewAchievementsHotkey);
                OverviewHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.OverviewHotkey);
            }
            else
            {
                OverviewHotkeyButtonText = L("LOCPlayAch_Hotkeys_CaptureButton", "Press keys...");
                ViewAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ViewAchievementsHotkey);
                ManageAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ManageAchievementsHotkey);
            }

            button?.Focus();
            Keyboard.Focus(button);
        }

        private void EndHotkeyCapture()
        {
            _capturingHotkey = null;
            HotkeyCaptureStatusText = string.Empty;
            UpdateHotkeyButtonTexts();
        }

        private void SetCapturedHotkey(HotkeyCaptureTarget target, string hotkey)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            if (target == HotkeyCaptureTarget.ViewAchievements)
            {
                persisted.ViewAchievementsHotkey = hotkey;
            }
            else if (target == HotkeyCaptureTarget.ManageAchievements)
            {
                persisted.ManageAchievementsHotkey = hotkey;
            }
            else
            {
                persisted.OverviewHotkey = hotkey;
            }
        }

        private bool IsDuplicateHotkey(HotkeyCaptureTarget target, AchievementHotkeyGesture gesture)
        {
            if (gesture == null || gesture.IsEmpty)
            {
                return false;
            }

            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return false;
            }

            return IsMatchingHotkey(target, HotkeyCaptureTarget.ViewAchievements, persisted.ViewAchievementsHotkey, gesture) ||
                   IsMatchingHotkey(target, HotkeyCaptureTarget.ManageAchievements, persisted.ManageAchievementsHotkey, gesture) ||
                   IsMatchingHotkey(target, HotkeyCaptureTarget.Overview, persisted.OverviewHotkey, gesture);
        }

        private static bool IsMatchingHotkey(
            HotkeyCaptureTarget currentTarget,
            HotkeyCaptureTarget comparedTarget,
            string comparedText,
            AchievementHotkeyGesture gesture)
        {
            if (currentTarget == comparedTarget)
            {
                return false;
            }

            return AchievementHotkeyGesture.TryParse(comparedText, out var otherGesture) &&
                   otherGesture != null &&
                   !otherGesture.IsEmpty &&
                   gesture.Equals(otherGesture);
        }

        private void UpdateHotkeyButtonTexts()
        {
            if (_capturingHotkey.HasValue)
            {
                return;
            }

            var persisted = _settingsViewModel?.Settings?.Persisted;
            ViewAchievementsHotkeyButtonText = FormatHotkeyButtonText(persisted?.ViewAchievementsHotkey);
            ManageAchievementsHotkeyButtonText = FormatHotkeyButtonText(persisted?.ManageAchievementsHotkey);
            OverviewHotkeyButtonText = FormatHotkeyButtonText(persisted?.OverviewHotkey);
        }

        private string FormatHotkeyButtonText(string hotkey)
        {
            return string.IsNullOrWhiteSpace(hotkey)
                ? L("LOCPlayAch_Hotkeys_None", "None")
                : hotkey;
        }

        private static Key GetEffectiveHotkeyCaptureKey(KeyEventArgs e)
        {
            if (e == null)
            {
                return Key.None;
            }

            if (e.Key == Key.System)
            {
                return e.SystemKey;
            }

            if (e.Key == Key.ImeProcessed)
            {
                return e.ImeProcessedKey;
            }

            return e.Key;
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

            if (e.PropertyName == nameof(Models.Settings.PersistedSettings.StartPageActivityScope) ||
                e.PropertyName == nameof(Models.Settings.PersistedSettings.StartPageProgressScope))
            {
                UpdateStartPageScopeTexts();
            }

            if (e.PropertyName == nameof(Models.Settings.PersistedSettings.ViewAchievementsHotkey) ||
                e.PropertyName == nameof(Models.Settings.PersistedSettings.ManageAchievementsHotkey) ||
                e.PropertyName == nameof(Models.Settings.PersistedSettings.OverviewHotkey))
            {
                UpdateHotkeyButtonTexts();
            }

            if (refreshProperties.Contains(e.PropertyName))
            {
                RefreshMockPreviews();
            }
        }

        // -----------------------------
        // IDisposable implementation
        // -----------------------------

        public object DetachAchievementNotificationsContent()
        {
            // This compatibility control itself is never added to the visual tree, so its
            // Loaded handler will not run. Initialize the notification tab explicitly before
            // moving its content into the v3 settings shell.
            InitializeAchievementNotificationsTab();

            var content = AchievementNotificationsTab?.Content;
            if (content is FrameworkElement contentElement &&
                AchievementNotificationsTab?.DataContext != null)
            {
                // The original tab binds notification controls directly to LocalSettings.
                // Preserve that inherited context when the content is hosted by the v3 shell.
                contentElement.DataContext = AchievementNotificationsTab.DataContext;
            }

            if (AchievementNotificationsTab != null)
            {
                AchievementNotificationsTab.Content = null;
            }

            return content;
        }

        public object DetachThemeMigrationContent()
        {
            if (!_themeMigrationLoaded)
            {
                RefreshThemeMigrationContent();
            }

            var content = ThemeMigrationTab?.Content;
            if (ThemeMigrationTab != null)
            {
                ThemeMigrationTab.Content = null;
            }

            return content;
        }

        public void RefreshThemeMigrationContent()
        {
            LoadThemes();
            RefreshStartPageCompatibilityState();
            _themeMigrationLoaded = true;
        }

        public void Dispose()
        {
            _notificationAutoPopupPreviewTimer?.Stop();
            _notificationInlinePreviewLoopTimer?.Stop();
            NotificationPublisher.ClosePersistentSettingsPreview();
            if (_notificationPreviewSettings != null)
            {
                _notificationPreviewSettings.PropertyChanged -= LocalNotificationSettings_PropertyChanged;
                _notificationPreviewSettings = null;
            }

            if (_settingsViewModel?.Settings?.Persisted != null)
            {
                _settingsViewModel.Settings.Persisted.PropertyChanged -= OnSettingsPropertyChanged;
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
    public sealed class LegacyProviderNavigationItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly IProviderSettings _settings;

        public LegacyProviderNavigationItem(
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




