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
        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly ILogger _logger;
        private readonly ProviderRegistry _providerRegistry;
        private readonly Func<Window, string, string> _pickColor;
        private DisplaySettingsTab _displaySettingsTab;
        private GeneralSettingsTab _generalSettingsTab;
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

            if (DisplaySettingsContent != null)
            {
                _displaySettingsTab = new DisplaySettingsTab(
                    _settingsViewModel.Settings,
                    _plugin,
                    _logger,
                    _pickColor);
                DisplaySettingsContent.Content = _displaySettingsTab;
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

            _settingsViewModel.Settings.Persisted.PropertyChanged += Persisted_PropertyChanged;

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
            };
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
                case "ThemeMigration":
                    // Theme migration now lives inside the Display tab's navigation.
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
            _autoAuthDebounceCts?.Cancel();
            _autoAuthDebounceCts?.Dispose();
            _autoAuthDebounceCts = null;
            _settingsViewModel.Settings.Persisted.PropertyChanged -= Persisted_PropertyChanged;
            _displaySettingsTab?.Dispose();
            _generalSettingsTab?.Dispose();
        }

        private void Persisted_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.EnableFriendsFeatures)
                && !_settingsViewModel.Settings.Persisted.EnableFriendsFeatures
                && SettingsTabControl.SelectedItem == FriendsTab)
            {
                SettingsTabControl.SelectedItem = GeneralTab;
            }
        }
    }
}
