using System.Windows;

namespace PlayniteAchievements.Views.Controls
{
    // The height a Tags cell's tags may take. Auto-height rows measure their cells without a
    // limit, so it comes from the row height the grid sets, or the row style's minimum.
    public partial class AchievementDataGridControl
    {
        // AchievementCellStyle pads cells 6 top and bottom; the row style's MinHeight is 88.
        private const double TagCellVerticalPadding = 12;
        private const double AutoRowMinHeight = 88;

        private static readonly DependencyPropertyKey TagAreaHeightPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagAreaHeight), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(AutoRowMinHeight - TagCellVerticalPadding));

        /// <summary>Identifies the TagAreaHeight dependency property: the height a Tags cell's tags may take.</summary>
        public static readonly DependencyProperty TagAreaHeightProperty = TagAreaHeightPropertyKey.DependencyProperty;

        public double TagAreaHeight => (double)GetValue(TagAreaHeightProperty);

        private void UpdateTagAreaHeight() =>
            SetValue(TagAreaHeightPropertyKey, (ResolveFixedRowHeight() ?? AutoRowMinHeight) - TagCellVerticalPadding);
    }
}
