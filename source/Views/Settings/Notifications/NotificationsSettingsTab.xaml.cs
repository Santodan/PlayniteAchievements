using System;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views.Settings.General;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notifications settings tab: a master-detail navigation over the three notification
    /// sections (General, Appearance, Capture). Sections are created lazily when first
    /// selected.
    /// </summary>
    public partial class NotificationsSettingsTab : UserControl, IDisposable
    {
        private ObservableCollection<SettingsNavigationItem> _navigationItems;

        private NotificationsSection _generalSection;
        private NotificationAppearanceSection _appearanceSection;
        private CaptureSettingsSection _captureSection;

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
                    "General",
                    ResourceProvider.GetString("LOCPlayAch_Common_General"),
                    iconGlyph: "",
                    viewFactory: () => _generalSection =
                        new NotificationsSection(settings, plugin, logger)),
                new SettingsNavigationItem(
                    "Appearance",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Appearance"),
                    iconGlyph: "",
                    viewFactory: () => _appearanceSection =
                        new NotificationAppearanceSection(settings, plugin, logger)),
                new SettingsNavigationItem(
                    "Capture",
                    ResourceProvider.GetString("LOCPlayAch_Section_Capture"),
                    iconGlyph: "",
                    viewFactory: () => _captureSection =
                        new CaptureSettingsSection(settings, plugin, logger))
            };

            MasterDetail.ItemsSource = _navigationItems;
            MasterDetail.SelectedItem = _navigationItems[0];
        }

        public void Dispose()
        {
            _generalSection?.Dispose();
            _appearanceSection?.Dispose();
            _captureSection?.Dispose();
        }
    }
}
