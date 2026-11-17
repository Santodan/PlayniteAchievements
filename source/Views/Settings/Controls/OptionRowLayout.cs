using System.Windows;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// The one row metric of the grid display settings and the showcase widget settings: every
    /// option row is at least a control's height tall and spaced the same, so checkbox rows line
    /// up with dropdown and number rows. The XAML rows of <see cref="GridOptionsEditor"/> read the
    /// same resources.
    /// </summary>
    public static class OptionRowLayout
    {
        public const string MinHeightKey = "PlayAch.Size.OptionRow.MinHeight";
        public const string MarginKey = "PlayAch.Thickness.OptionRow";

        public static void Apply(FrameworkElement row)
        {
            row.SetResourceReference(FrameworkElement.MarginProperty, MarginKey);
            row.SetResourceReference(FrameworkElement.MinHeightProperty, MinHeightKey);
        }
    }
}
