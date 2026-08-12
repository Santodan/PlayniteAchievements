using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Positions the goal accent bar drawn by the shared achievement row template.
    /// </summary>
    public static class DataGridRowAccentBehavior
    {
        /// <summary>
        /// Margin applied to the accent bar. Set this on the DataGrid and it flows to every row.
        /// A grid whose first column is not the one the accent should mark (the Goals tab leads
        /// with a drag handle) offsets the bar to the left edge of the column it belongs to.
        /// </summary>
        public static readonly DependencyProperty AccentMarginProperty =
            DependencyProperty.RegisterAttached(
                "AccentMargin",
                typeof(Thickness),
                typeof(DataGridRowAccentBehavior),
                new FrameworkPropertyMetadata(
                    new Thickness(0, 7, 0, 7),
                    FrameworkPropertyMetadataOptions.Inherits));

        public static Thickness GetAccentMargin(DependencyObject obj)
        {
            return (Thickness)obj.GetValue(AccentMarginProperty);
        }

        public static void SetAccentMargin(DependencyObject obj, Thickness value)
        {
            obj.SetValue(AccentMarginProperty, value);
        }
    }
}
