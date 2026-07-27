using System;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings tab: a master-detail navigation over the six General sections. Sections
    /// are created lazily when first selected.
    /// </summary>
    public partial class GeneralSettingsTab : UserControl, IDisposable
    {
        private ObservableCollection<SettingsNavigationItem> _navigationItems;

        private GeneralOverviewSection _overviewSection;
        private SyncUpdatesSection _syncUpdatesSection;
        private NotificationsSection _notificationsSection;
        private HotkeySettingsSection _hotkeySection;
        private TaggingSettingsSection _taggingSection;
        private MaintenanceSettingsSection _maintenanceSection;

        public GeneralSettingsTab()
        {
            InitializeComponent();
        }

        internal GeneralSettingsTab(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Action<string> jumpToTab)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (jumpToTab == null) throw new ArgumentNullException(nameof(jumpToTab));

            _navigationItems = new ObservableCollection<SettingsNavigationItem>
            {
                new SettingsNavigationItem(
                    "General",
                    ResourceProvider.GetString("LOCPlayAch_Common_General"),
                    iconGlyph: "",
                    viewFactory: () => _overviewSection =
                        new GeneralOverviewSection(jumpToTab)),
                new SettingsNavigationItem(
                    "SyncUpdates",
                    ResourceProvider.GetString("LOCPlayAch_Section_SyncUpdates"),
                    iconGlyph: "",
                    viewFactory: () => _syncUpdatesSection =
                        new SyncUpdatesSection()),
                new SettingsNavigationItem(
                    "Notifications",
                    ResourceProvider.GetString("LOCPlayAch_Settings_TabNotifications"),
                    iconGlyph: "",
                    viewFactory: () => _notificationsSection =
                        new NotificationsSection(settings, plugin, logger)),
                new SettingsNavigationItem(
                    "Hotkeys",
                    ResourceProvider.GetString("LOCPlayAch_Hotkeys_Title"),
                    iconGlyph: "",
                    viewFactory: () => _hotkeySection =
                        new HotkeySettingsSection(settings)),
                new SettingsNavigationItem(
                    "Tagging",
                    ResourceProvider.GetString("LOCPlayAch_Settings_TaggingHeader"),
                    iconGlyph: "",
                    viewFactory: () => _taggingSection =
                        new TaggingSettingsSection(plugin, logger)),
                new SettingsNavigationItem(
                    "Maintenance",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Maintenance_Title"),
                    iconGlyph: "",
                    viewFactory: () => _maintenanceSection =
                        new MaintenanceSettingsSection(settings, plugin, logger))
            };

            MasterDetail.ItemsSource = _navigationItems;
            MasterDetail.SelectedItem = _navigationItems[0];
        }

        public void Dispose()
        {
            _notificationsSection?.Dispose();
            _hotkeySection?.Dispose();
        }
    }
}
