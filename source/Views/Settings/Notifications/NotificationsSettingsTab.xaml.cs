using System;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notifications settings tab: a master-detail navigation over the four notification concerns
    /// — Behavior (what fires and how), Captures (screenshots and recordings), Appearance
    /// (templates and surface styles) and Platforms (per-provider overrides). Sections are created
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
                    iconGlyph: "\uEA8F",
                    viewFactory: () => _behaviorSection =
                        new NotificationBehaviorSection(settings, plugin, logger)),
                new SettingsNavigationItem(
                    "Captures",
                    ResourceProvider.GetString("LOCPlayAch_Column_Captures"),
                    iconGlyph: "\uE722",
                    viewFactory: () => _capturesSection =
                        new NotificationCapturesSection(settings, plugin)),
                new SettingsNavigationItem(
                    "Appearance",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Appearance"),
                    iconGlyph: "\uE790",
                    viewFactory: () => _appearanceSection =
                        new NotificationAppearanceSection(settings, plugin, logger)),
                new SettingsNavigationItem(
                    "Platforms",
                    ResourceProvider.GetString("LOCPlayAch_Common_Label_Platforms"),
                    iconGlyph: "\uE7FC",
                    viewFactory: () => _platformsSection =
                        new NotificationPlatformsSection(settings, plugin, logger))
            };

            MasterDetail.ItemsSource = _navigationItems;
            MasterDetail.SelectedItem = _navigationItems[0];
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
