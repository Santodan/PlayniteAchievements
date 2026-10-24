// SettingsControl.xaml.cs
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Settings.Display.ThemeControls;
using PlayniteAchievements.Views.Settings.Display;
using PlayniteAchievements.Views.Settings.General;
using PlayniteAchievements.Views.Settings.Navigation;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;

namespace PlayniteAchievements.Views
{
    public partial class SettingsControl : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsPlugin _plugin;
        private bool _windowPlacementAttached;
        private Window _hostWindow;
        private bool _disposed;
        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly ILogger _logger;
        private readonly ProviderRegistry _providerRegistry;
        private readonly Func<Window, string, string> _pickColor;
        private DisplaySettingsTab _displaySettingsTab;
        private Settings.Themes.ThemesSettingsTab _themesSettingsTab;
        // Shared by the Display and Themes copies of the migration page so the two stay in sync.
        private ThemeMigrationController _themeMigrationController;
        private GeneralSettingsTab _generalSettingsTab;
        private Settings.Notifications.NotificationsSettingsTab _notificationsSettingsTab;
        private Settings.Workshop.WorkshopSettingsTab _workshopSettingsTab;
        private Settings.Metadata.MetadataSettingsTab _metadataSettingsTab;

        /// <summary>
        /// The place the next SettingsControl should open on. Set by the plugin before it asks
        /// Playnite for the settings dialog, which takes no arguments, and consumed once.
        /// </summary>
        internal static SettingsNavigationRequest PendingNavigation { get; set; }

        private static WeakReference<SettingsControl> _live;

        /// <summary>
        /// The settings view on screen, or null when no settings window is open. Set when a view
        /// loads; a view that has unloaded is no longer live.
        /// </summary>
        internal static SettingsControl Live =>
            _live != null && _live.TryGetTarget(out var control) && control.IsLoaded ? control : null;
        private bool _providerNavigationBuilt;
        private readonly HashSet<string> _autoAuthCheckedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _autoAuthDebounceCts;

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

        /// <summary>
        /// Set before opening settings to navigate directly to a provider tab.
        /// Cleared after use.
        /// </summary>
        public static string PendingNavigationProviderKey { get; set; }

        public SettingsControl(
            PlayniteAchievementsSettingsViewModel settingsViewModel,
            ILogger logger,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry providerRegistry,
            Func<Window, string, string> pickColor)
        {
            _settingsViewModel = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
            _pickColor = pickColor ?? throw new ArgumentNullException(nameof(pickColor));

            InitializeComponent();
            FormattingCulture.Apply(this);

            // Initialize provider navigation overview
            ProviderNavigationItems = new ObservableCollection<ProviderNavigationItem>();

            // Playnite does not reliably set DataContext for settings views.
            // Bind directly to the settings model so XAML uses {Binding SomeSetting}.
            DataContext = _settingsViewModel.Settings;
            if (FriendsSettingsContent != null)
            {
                FriendsSettingsContent.Content = new FriendsSettingsTab(
                    _settingsViewModel.Settings,
                    _plugin,
                    _providerRegistry,
                    _logger);
            }

            if (MetadataSettingsContent != null)
            {
                MetadataSettingsContent.Content = _metadataSettingsTab = new Settings.Metadata.MetadataSettingsTab(
                    _settingsViewModel.Settings,
                    _plugin,
                    _providerRegistry);
            }

            _themeMigrationController = new ThemeMigrationController(
                _settingsViewModel.Settings,
                _plugin,
                _logger);

            if (DisplaySettingsContent != null)
            {
                _displaySettingsTab = new DisplaySettingsTab(
                    _settingsViewModel.Settings,
                    _plugin,
                    _logger,
                    _pickColor,
                    _themeMigrationController,
                    () => _themesSettingsTab?.RefreshAfterDisplaySettingsReset());
                DisplaySettingsContent.Content = _displaySettingsTab;
            }

            if (ThemesSettingsContent != null)
            {
                _themesSettingsTab = new Settings.Themes.ThemesSettingsTab(
                    _settingsViewModel.Settings,
                    _themeMigrationController);
                ThemesSettingsContent.Content = _themesSettingsTab;
            }

            if (GeneralSettingsContent != null)
            {
                _generalSettingsTab = new GeneralSettingsTab(
                    _settingsViewModel.Settings,
                    _plugin,
                    _logger,
                    JumpToTab);
                GeneralSettingsContent.Content = _generalSettingsTab;
            }

            if (NotificationsSettingsContent != null)
            {
                _notificationsSettingsTab = new Settings.Notifications.NotificationsSettingsTab(
                    _settingsViewModel.Settings,
                    _plugin,
                    _logger);
                NotificationsSettingsContent.Content = _notificationsSettingsTab;
            }

            var pending = PendingNavigation;
            PendingNavigation = null;

            if (WorkshopSettingsContent != null)
            {
                _workshopSettingsTab = new Settings.Workshop.WorkshopSettingsTab(
                    _settingsViewModel.Settings,
                    _plugin,
                    _logger,
                    string.Equals(pending?.TabKey, SettingsNavigationRequest.WorkshopTab, StringComparison.Ordinal) ? pending.PageKey : null);
                WorkshopSettingsContent.Content = _workshopSettingsTab;
            }

            NavigateTo(pending);

            _settingsViewModel.Settings.PropertyChanged += Settings_PropertyChanged;
            AttachPersistedSettings(_settingsViewModel.Settings.Persisted);

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

                // Navigate to a specific provider item if requested (e.g., from auth notification click).
                if (!string.IsNullOrWhiteSpace(PendingNavigationProviderKey) && ProvidersTab != null)
                {
                    SettingsTabControl.SelectedItem = ProvidersTab;
                    NavigateToPendingProvider();
                }

                AttachSettingsWindowPlacement();
                DisposeWithHostWindow();
                _live = new WeakReference<SettingsControl>(this);
            };
        }

        // Playnite builds a new view for every settings open and does not dispose the old one, so
        // the view releases its subscriptions to the plugin's settings when its window closes.
        // Not on Unloaded: the shared add-ons window unloads the view when another add-on is
        // picked and loads it again on return.
        private void DisposeWithHostWindow()
        {
            if (_hostWindow != null)
            {
                return;
            }

            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.Closed += HostWindow_Closed;
            }
        }

        private void HostWindow_Closed(object sender, EventArgs e)
        {
            if (_hostWindow != null)
            {
                _hostWindow.Closed -= HostWindow_Closed;
            }

            Dispose();
        }

        /// <summary>
        /// Switches to a place in the settings: the tab, then the tab's page, and on the
        /// notification Appearance page the platform and surface.
        /// </summary>
        internal void NavigateTo(SettingsNavigationRequest request)
        {
            if (string.IsNullOrEmpty(request?.TabKey))
            {
                return;
            }

            JumpToTab(request.TabKey);
            switch (request.TabKey)
            {
                case SettingsNavigationRequest.DisplayTab:
                    if (!string.IsNullOrEmpty(request.PageKey))
                    {
                        _displaySettingsTab?.NavigateToPage(request.PageKey);
                    }

                    break;
                case SettingsNavigationRequest.NotificationsTab:
                    _notificationsSettingsTab?.NavigateTo(request);
                    break;
                case SettingsNavigationRequest.WorkshopTab:
                    if (!string.IsNullOrEmpty(request.PageKey))
                    {
                        _workshopSettingsTab?.NavigateToPage(request.PageKey);
                    }

                    break;
                case SettingsNavigationRequest.MetadataTab:
                    if (!string.IsNullOrEmpty(request.PageKey))
                    {
                        _metadataSettingsTab?.NavigateToPage(request.PageKey);
                    }

                    break;
            }
        }

        // -----------------------------
        // Provider Navigation Overview
        // -----------------------------

        /// <summary>
        /// Builds provider navigation items for the overview from registered providers.
        /// Items are added in the natural discovery order.
        /// </summary>
        private void BuildProviderNavigationItems(bool selectDefault = true)
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

                var displayName = ProviderRegistry.GetLocalizedName(providerKey);
                var iconKey = $"ProviderIcon{providerKey}";
                var colorHex = "#FF4084D6";
                string redirectProviderKey = null;
                string subtitle = null;
                Func<ProviderSettingsViewBase> settingsViewFactory = null;

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

                if (ProviderUiPolicies.TryGetSettingsRedirectProviderKey(providerKey, out redirectProviderKey))
                {
                    subtitle = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_ProviderServicedByFormat"),
                        ProviderRegistry.GetLocalizedName(redirectProviderKey));
                }
                else
                {
                    settingsViewFactory = () => _providerRegistry.CreateSettingsView(providerKey);
                }

                ProviderNavigationItems.Add(new ProviderNavigationItem(
                    providerKey,
                    displayName,
                    GetProviderSettingsGroupName(providerKey),
                    iconKey,
                    colorHex,
                    settings,
                    settingsViewFactory,
                    redirectProviderKey,
                    subtitle));
            }

            if (selectDefault && SelectedProviderNavigationItem == null && ProviderNavigationItems.Count > 0)
            {
                SelectedProviderNavigationItem = ProviderNavigationItems[0];
            }

            _providerNavigationBuilt = true;
            _logger?.Info($"Built {ProviderNavigationItems.Count} platform navigation items");
        }

        private static void OnSelectedProviderNavigationItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsControl control)
            {
                control.ScheduleAutoAuthCheck(e.NewValue as ProviderNavigationItem);
            }
        }

        /// <summary>
        /// Schedules a debounced, once-per-settings-session auth check for the selected
        /// provider page. Selecting another page cancels a still-pending check.
        /// </summary>
        private void ScheduleAutoAuthCheck(ProviderNavigationItem item)
        {
            _autoAuthDebounceCts?.Cancel();
            _autoAuthDebounceCts?.Dispose();
            _autoAuthDebounceCts = null;

            if (item == null ||
                item.IsRedirect ||
                !item.IsEnabled ||
                _autoAuthCheckedProviders.Contains(item.ProviderKey))
            {
                return;
            }

            _autoAuthDebounceCts = new CancellationTokenSource();
            _ = RunAutoAuthCheckAsync(item, _autoAuthDebounceCts.Token);
        }

        private async Task RunAutoAuthCheckAsync(ProviderNavigationItem item, CancellationToken token)
        {
            try
            {
                await Task.Delay(300, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested ||
                !ReferenceEquals(SelectedProviderNavigationItem, item))
            {
                return;
            }

            if (!(item.EnsureView() is IAuthRefreshable refreshable))
            {
                return;
            }

            _autoAuthCheckedProviders.Add(item.ProviderKey);

            try
            {
                _logger?.Info($"Auto-refreshing auth status for {item.ProviderKey}");
                await refreshable.RefreshAuthStatusAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to refresh auth status for {item.ProviderKey}");
            }
        }

        private static string GetProviderSettingsGroupName(string providerKey)
        {
            return ResourceProvider.GetString(ProviderUiPolicies.GetSettingsGroupResourceKey(providerKey));
        }

        // Playnite owns the window hosting this control, so placement persistence
        // cannot be attached at window creation the way the plugin's own windows do.
        // Attach only to Playnite's dedicated plugin-settings dialog; when this control
        // is embedded in the shared add-ons window, resizing it would affect unrelated UI.
        private void AttachSettingsWindowPlacement()
        {
            if (_windowPlacementAttached)
            {
                return;
            }

            var window = Window.GetWindow(this);
            if (window == null || window.GetType().Name != "PluginSettingsWindow")
            {
                return;
            }

            _windowPlacementAttached = true;
            Helpers.WindowPlacementPersistenceService.Attach(
                window,
                _settingsViewModel.Settings?.Persisted,
                _plugin.PersistSettingsForUi,
                "PluginSettings",
                _logger);
        }

        private void NavigateToPendingProvider()
        {
            var targetKey = PendingNavigationProviderKey;
            if (string.IsNullOrWhiteSpace(targetKey))
                return;

            BuildProviderNavigationItems(selectDefault: false);

            PendingNavigationProviderKey = null;

            // Find and select the provider navigation item matching the target key
            var targetItem = ProviderNavigationItems?.FirstOrDefault(x =>
                string.Equals(x.ProviderKey, targetKey, StringComparison.OrdinalIgnoreCase));

            if (targetItem != null)
            {
                // Ensure Providers tab is visible
                if (ProvidersTab != null)
                {
                    SettingsTabControl.SelectedItem = ProvidersTab;
                }

                SelectedProviderNavigationItem = targetItem;
                _logger?.Info($"Navigated to pending provider: {targetKey}");
            }
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is TabControl)) return;

            // Inspect the newly selected TabItem by name (uses x:Name from XAML)
            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (e.AddedItems[0] is not TabItem selected) return;

            var name = selected.Name ?? string.Empty;

            if (string.Equals(name, "ProvidersTab", StringComparison.OrdinalIgnoreCase))
            {
                BuildProviderNavigationItems(selectDefault: string.IsNullOrWhiteSpace(PendingNavigationProviderKey));
                NavigateToPendingProvider();
            }
        }

        // Quick navigation callback used by the General tab's quick-link buttons
        private void JumpToTab(string tabKey)
        {
            TabItem tab;
            switch (tabKey)
            {
                case "Display":
                    tab = DisplayTab;
                    break;
                case "Providers":
                    tab = ProvidersTab;
                    break;
                case "Themes":
                    tab = ThemesTab;
                    break;
                case "Notifications":
                    tab = NotificationsTab;
                    break;
                case "Workshop":
                    tab = WorkshopTab;
                    break;
                case "Metadata":
                    tab = MetadataTab;
                    break;
                case "ThemeMigration":
                    // Theme migration is listed on both Display and Themes. Display is the
                    // canonical target because it comes first in the tab strip.
                    tab = DisplayTab;
                    break;
                default:
                    return;
            }

            if (tab == null)
            {
                return;
            }

            SettingsTabControl.SelectedItem = tab;
            if (ReferenceEquals(tab, ProvidersTab))
            {
                BuildProviderNavigationItems();
            }

            if (string.Equals(tabKey, "ThemeMigration", StringComparison.Ordinal))
            {
                _displaySettingsTab?.NavigateToPage("Migration");
            }

            tab.BringIntoView();
        }

        // -----------------------------
        // IDisposable implementation
        // -----------------------------

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_hostWindow != null)
            {
                _hostWindow.Closed -= HostWindow_Closed;
                _hostWindow = null;
            }

            _autoAuthDebounceCts?.Cancel();
            _autoAuthDebounceCts?.Dispose();
            _autoAuthDebounceCts = null;
            _settingsViewModel.Settings.PropertyChanged -= Settings_PropertyChanged;
            AttachPersistedSettings(null);
            _displaySettingsTab?.Dispose();
            _themesSettingsTab?.Dispose();
            _generalSettingsTab?.Dispose();
            _notificationsSettingsTab?.Dispose();
            _workshopSettingsTab?.Dispose();
        }

        private void Persisted_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) ||
                e.PropertyName == nameof(PersistedSettings.ProviderColorOverrides))
            {
                foreach (var item in ProviderNavigationItems)
                {
                    item.RefreshProviderAppearance();
                }
            }

            if (e.PropertyName == nameof(PersistedSettings.EnableFriendsFeatures)
                && !_settingsViewModel.Settings.Persisted.EnableFriendsFeatures
                && SettingsTabControl.SelectedItem == FriendsTab)
            {
                SettingsTabControl.SelectedItem = GeneralTab;
            }
        }

        private PersistedSettings _subscribedPersistedSettings;

        private void Settings_PropertyChanged(
            object sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) ||
                e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                AttachPersistedSettings(_settingsViewModel.Settings.Persisted);
                foreach (var item in ProviderNavigationItems)
                {
                    item.RefreshProviderAppearance();
                }
            }
        }

        private void AttachPersistedSettings(PersistedSettings persisted)
        {
            if (ReferenceEquals(_subscribedPersistedSettings, persisted))
            {
                return;
            }

            if (_subscribedPersistedSettings != null)
            {
                _subscribedPersistedSettings.PropertyChanged -= Persisted_PropertyChanged;
            }

            _subscribedPersistedSettings = persisted;
            if (_subscribedPersistedSettings != null)
            {
                _subscribedPersistedSettings.PropertyChanged += Persisted_PropertyChanged;
            }
        }
    }
}
