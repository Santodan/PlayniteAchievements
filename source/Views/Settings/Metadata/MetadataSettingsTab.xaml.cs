using System;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings tab: which sources supply icons, categories, rarity and capstone art per
    /// platform, and how auto capstones are generated. Sections are created when first selected.
    /// </summary>
    public partial class MetadataSettingsTab : UserControl
    {
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

            var items = new ObservableCollection<SettingsNavigationItem>
            {
                new SettingsNavigationItem(
                    "Sources",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Metadata_Sources"),
                    iconGlyph: "",
                    viewFactory: () => new MetadataSourcesSection(settings, registry)),
                new SettingsNavigationItem(
                    "AutoCapstone",
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_AutoCapstone"),
                    iconGlyph: "",
                    viewFactory: () => new AutoCapstoneSettingsSection(settings, plugin))
            };

            MasterDetail.ItemsSource = items;
            MasterDetail.SelectedItem = items[0];
        }
    }
}
