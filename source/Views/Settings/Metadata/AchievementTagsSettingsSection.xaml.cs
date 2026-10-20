using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels.Tags;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings: the global achievement tag list every game can use. Edits land on the
    /// settings being edited, so Cancel discards them like any other setting.
    /// </summary>
    public partial class AchievementTagsSettingsSection : UserControl
    {
        public AchievementTagsSettingsSection()
        {
            InitializeComponent();
        }

        internal AchievementTagsSettingsSection(PlayniteAchievementsSettings settings, PlayniteAchievementsPlugin plugin)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            Editor.DataContext = new AchievementTagListViewModel(
                settings.Persisted?.ResolveAchievementTags(),
                definitions =>
                {
                    if (settings.Persisted != null)
                    {
                        settings.Persisted.AchievementTags = definitions.ToList();
                    }
                },
                current => plugin.PickColor(Window.GetWindow(this), current));
        }
    }
}
