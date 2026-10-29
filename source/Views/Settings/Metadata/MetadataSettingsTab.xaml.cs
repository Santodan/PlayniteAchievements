using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings tab: a master-detail navigation over the per-platform sources grid, the
    /// global tag list and the auto capstone settings. Sections are created when first selected.
    /// </summary>
    public partial class MetadataSettingsTab : UserControl
    {
        private ObservableCollection<SettingsNavigationItem> _navigationItems;

        public MetadataSettingsTab()
        {
            InitializeComponent();
        }

        internal MetadataSettingsTab(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry registry)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var items = _navigationItems = new ObservableCollection<SettingsNavigationItem>
            {
                new SettingsNavigationItem(
                    "Sources",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Metadata_Sources"),
                    iconGlyph: "",
                    viewFactory: () => Page(new MetadataSourcesSection(settings, registry), scrolls: false)),
                new SettingsNavigationItem(
                    "Tags",
                    ResourceProvider.GetString("LOCPlayAch_Common_Tags"),
                    iconGlyph: "",
                    viewFactory: () => Page(new AchievementTagsSettingsSection(settings, plugin))),
                new SettingsNavigationItem(
                    "AutoCapstone",
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_AutoCapstone"),
                    iconGlyph: "",
                    viewFactory: () => Page(new AutoCapstoneSettingsSection(settings, plugin)))
            };

            MasterDetail.ItemsSource = items;
            MasterDetail.SelectedItem = items[0];
        }

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
        /// A section as its own page, with the margin the other tabs' sections have. A page that
        /// does not scroll gives the section the page's height (the sources grid scrolls itself).
        /// </summary>
        private static UserControl Page(FrameworkElement section, bool scrolls = true)
        {
            section.Margin = new Thickness(20);
            return new UserControl
            {
                Content = scrolls
                    ? new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = section
                    }
                    : (object)section
            };
        }
    }
}
