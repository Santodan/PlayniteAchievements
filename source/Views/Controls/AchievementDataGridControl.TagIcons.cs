using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Controls
{
    // The Tags column's icon size and the height its tags may take: as large as the row height
    // and column width allow, the same in every row. A row with more tags than fit shows "+N".
    public partial class AchievementDataGridControl
    {
        // AchievementCellStyle pads cells 10,6; the row style's MinHeight is 88.
        private const double TagCellHorizontalPadding = 20;
        private const double TagCellVerticalPadding = 12;
        private const double AutoRowMinHeight = 88;

        private static readonly DependencyPropertyKey TagIconSizePropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagIconSize), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(16.0));

        /// <summary>Identifies the TagIconSize dependency property: the Tags column's icon size.</summary>
        public static readonly DependencyProperty TagIconSizeProperty = TagIconSizePropertyKey.DependencyProperty;

        public double TagIconSize => (double)GetValue(TagIconSizeProperty);

        private static readonly DependencyPropertyKey TagAreaHeightPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagAreaHeight), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(double.PositiveInfinity));

        /// <summary>Identifies the TagAreaHeight dependency property: the height a Tags cell's tags may take.</summary>
        public static readonly DependencyProperty TagAreaHeightProperty = TagAreaHeightPropertyKey.DependencyProperty;

        public double TagAreaHeight => (double)GetValue(TagAreaHeightProperty);

        private static readonly DependencyProperty TagsColumnWidthProperty =
            DependencyProperty.Register("TagsColumnWidth", typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(0.0, OnTagIconLayoutChanged));

        private static void OnTagIconLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((AchievementDataGridControl)d).UpdateTagIconSize();

        private void ObserveTagIconLayout()
        {
            SetBinding(TagsColumnWidthProperty, new Binding(nameof(DataGridColumn.ActualWidth)) { Source = TagsColumn });
        }

        private void UpdateTagIconSize()
        {
            if (TagsColumn == null || TagsColumn.ActualWidth <= 0)
            {
                return;
            }

            // One pixel of slack on each axis absorbs layout rounding.
            var width = TagsColumn.ActualWidth - TagCellHorizontalPadding - 1;
            var height = (ResolveFixedRowHeight() ?? AutoRowMinHeight) - TagCellVerticalPadding - 1;
            var floor = TryFindResource("PlayAch.FontSize.Body") is double fontSize && fontSize > 0
                ? Math.Ceiling(fontSize)
                : Math.Ceiling(SystemFonts.MessageFontSize);
            SetValue(TagIconSizePropertyKey, Math.Max(floor, Math.Floor(Math.Min(width, height))));
            SetValue(TagAreaHeightPropertyKey, Math.Max(floor, height));
        }
    }
}
