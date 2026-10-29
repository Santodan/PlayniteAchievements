using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notifications settings tab: a master-detail navigation over the four notification concerns
    /// — Behavior (what fires and how), Captures (screenshots and recordings), Styles
    /// (templates, surface styles and unlock sounds) and Platforms (per-provider overrides). Sections are created
    /// lazily when first selected. The three unlock-event features are siblings, each with its own
    /// master switch, because none of them depends on the others.
    /// </summary>
    public partial class NotificationsSettingsTab : UserControl, IDisposable
    {
        private ObservableCollection<SettingsNavigationItem> _navigationItems;

        private NotificationBehaviorSection _behaviorSection;
        private NotificationCapturesSection _capturesSection;
        private NotificationAppearanceSection _appearanceSection;
        private NotificationPlatformsSection _platformsSection;

        public NotificationsSettingsTab()
        {
            InitializeComponent();
        }

        internal NotificationsSettingsTab(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            _navigationItems = new ObservableCollection<SettingsNavigationItem>
            {
                new SettingsNavigationItem(
                    "Behavior",
                    ResourceProvider.GetString("LOCPlayAch_Settings_ToastBehavior"),
                    iconGlyph: "\uEEA3",
                    viewFactory: () => _behaviorSection =
                        new NotificationBehaviorSection(settings, plugin, logger)),
                new SettingsNavigationItem(
                    "Captures",
                    ResourceProvider.GetString("LOCPlayAch_Column_Captures"),
                    iconGlyph: "\uEECF",
                    viewFactory: () => _capturesSection =
                        new NotificationCapturesSection(settings, plugin)),
                new SettingsNavigationItem(
                    "Appearance",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Appearance"),
                    iconGlyph: "\uEFB3",
                    viewFactory: () => _appearanceSection =
                        new NotificationAppearanceSection(settings, plugin, logger)),
                new SettingsNavigationItem(
                    "Platforms",
                    ResourceProvider.GetString("LOCPlayAch_Common_Platforms"),
                    iconGlyph: "\uEA30",
                    viewFactory: () => _platformsSection =
                        new NotificationPlatformsSection(settings, plugin, logger))
            };

            MasterDetail.ItemsSource = _navigationItems;
            MasterDetail.SelectedItem = _navigationItems[0];
        }

        /// <summary>Selects the navigation item with the given key (e.g. "Appearance").</summary>
        public void NavigateToPage(string key)
        {
            var item = _navigationItems?.FirstOrDefault(x =>
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                MasterDetail.SelectedItem = item;
            }
        }

        /// <summary>
        /// Shows the request's page, then on Styles its platform and tab. Selecting a page creates
        /// its section synchronously.
        /// </summary>
        internal void NavigateTo(SettingsNavigationRequest request)
        {
            if (request == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(request.PageKey))
            {
                NavigateToPage(request.PageKey);
            }

            if (request.Surface.HasValue
                && string.Equals(request.PageKey, SettingsNavigationRequest.AppearancePage, StringComparison.OrdinalIgnoreCase))
            {
                _appearanceSection?.Preselect(request.ProviderKey, request.Surface.Value);
            }
        }

        public void Dispose()
        {
            _behaviorSection?.Dispose();
            _capturesSection?.Dispose();
            _appearanceSection?.Dispose();
            // Flushes the overrides grid's debounced persistence.
            _platformsSection?.Dispose();
        }
    }
}
