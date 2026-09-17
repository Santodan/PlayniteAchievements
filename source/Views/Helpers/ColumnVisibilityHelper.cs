using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    public static class ColumnVisibilityHelper
    {
        public static readonly DependencyProperty ColumnKeyProperty =
            DependencyProperty.RegisterAttached(
                "ColumnKey",
                typeof(string),
                typeof(ColumnVisibilityHelper),
                new PropertyMetadata(null));

        public static void SetColumnKey(DependencyObject element, string value)
        {
            element?.SetValue(ColumnKeyProperty, value);
        }

        public static string GetColumnKey(DependencyObject element)
        {
            return element?.GetValue(ColumnKeyProperty) as string;
        }

        /// <summary>
        /// The name a column goes by in the show/hide menu, for columns whose Header is a control
        /// rather than text. Without it such a column reads as its Header's type name, because
        /// resolving a display name falls back to ToString().
        /// </summary>
        public static readonly DependencyProperty ColumnDisplayNameProperty =
            DependencyProperty.RegisterAttached(
                "ColumnDisplayName",
                typeof(string),
                typeof(ColumnVisibilityHelper),
                new PropertyMetadata(null));

        public static void SetColumnDisplayName(DependencyObject element, string value)
        {
            element?.SetValue(ColumnDisplayNameProperty, value);
        }

        public static string GetColumnDisplayName(DependencyObject element)
        {
            return element?.GetValue(ColumnDisplayNameProperty) as string;
        }
    }
}
