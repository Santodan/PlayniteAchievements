using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The stat tiles' grid, shared by the Statistics widget and the Profile stat strip:
    /// equal-width columns like a UniformGrid, but a short last row can center under the full
    /// rows instead of sitting in the leftmost columns. With <see cref="StretchRows"/> the rows
    /// are one height and share out any extra height, as a UniformGrid's do.
    /// </summary>
    public sealed class ShowcaseStatGridPanel : Panel
    {
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(ShowcaseStatGridPanel),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty CenterLastRowProperty = DependencyProperty.Register(
            nameof(CenterLastRow),
            typeof(bool),
            typeof(ShowcaseStatGridPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty StretchRowsProperty = DependencyProperty.Register(
            nameof(StretchRows),
            typeof(bool),
            typeof(ShowcaseStatGridPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public bool StretchRows
        {
            get => (bool)GetValue(StretchRowsProperty);
            set => SetValue(StretchRowsProperty, value);
        }

        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        public bool CenterLastRow
        {
            get => (bool)GetValue(CenterLastRowProperty);
            set => SetValue(CenterLastRowProperty, value);
        }

        private int EffectiveColumns => Math.Max(1, Columns);

        protected override Size MeasureOverride(Size availableSize)
        {
            var columns = EffectiveColumns;
            var columnWidth = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : availableSize.Width / columns;
            var widest = 0.0;
            var tallest = 0.0;
            var height = 0.0;
            var rowHeight = 0.0;
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                widest = Math.Max(widest, child.DesiredSize.Width);
                tallest = Math.Max(tallest, child.DesiredSize.Height);
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                if (i % columns == columns - 1 || i == InternalChildren.Count - 1)
                {
                    height += rowHeight;
                    rowHeight = 0;
                }
            }

            if (StretchRows)
            {
                height = tallest * RowCount(columns);
            }

            var width = double.IsInfinity(columnWidth) ? widest * columns : availableSize.Width;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var columns = EffectiveColumns;
            var count = InternalChildren.Count;
            var columnWidth = finalSize.Width / columns;
            var uniformRowHeight = 0.0;
            if (StretchRows && count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    uniformRowHeight = Math.Max(uniformRowHeight, InternalChildren[i].DesiredSize.Height);
                }

                uniformRowHeight = Math.Max(uniformRowHeight, finalSize.Height / RowCount(columns));
            }

            var y = 0.0;
            for (var rowStart = 0; rowStart < count; rowStart += columns)
            {
                var inRow = Math.Min(columns, count - rowStart);
                var rowHeight = uniformRowHeight;
                for (var i = rowStart; i < rowStart + inRow && !StretchRows; i++)
                {
                    rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
                }

                var x = CenterLastRow ? (columns - inRow) * columnWidth / 2 : 0;
                for (var i = rowStart; i < rowStart + inRow; i++)
                {
                    InternalChildren[i].Arrange(new Rect(x, y, columnWidth, rowHeight));
                    x += columnWidth;
                }

                y += rowHeight;
            }

            return finalSize;
        }

        private int RowCount(int columns) =>
            Math.Max(1, (InternalChildren.Count + columns - 1) / columns);
    }
}
