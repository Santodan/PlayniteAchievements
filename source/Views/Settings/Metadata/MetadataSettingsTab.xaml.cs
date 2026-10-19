using System;
using System.Windows.Controls;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings tab: one page with the per-platform sources grid and, below it, the auto
    /// capstone settings that use the borrowed platinum art.
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

            Sections.Children.Add(new MetadataSourcesSection(settings, registry));
            Sections.Children.Add(new AutoCapstoneSettingsSection(settings, plugin));
        }
    }
}
