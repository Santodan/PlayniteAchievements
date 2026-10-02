using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The profile card's stat strip: equal-width columns like a UniformGrid, but a short last
    /// row can center under the full rows instead of sitting in the leftmost columns, which is
    /// what keeps the centered and stacked profile layouts symmetric past one row of stats.
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
            var height = 0.0;
            var rowHeight = 0.0;
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                widest = Math.Max(widest, child.DesiredSize.Width);
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                if (i % columns == columns - 1 || i == InternalChildren.Count - 1)
                {
                    height += rowHeight;
                    rowHeight = 0;
                }
            }

            var width = double.IsInfinity(columnWidth) ? widest * columns : availableSize.Width;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var columns = EffectiveColumns;
            var count = InternalChildren.Count;
            var columnWidth = finalSize.Width / columns;
            var y = 0.0;
            for (var rowStart = 0; rowStart < count; rowStart += columns)
            {
                var inRow = Math.Min(columns, count - rowStart);
                var rowHeight = 0.0;
                for (var i = rowStart; i < rowStart + inRow; i++)
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
    }
}
