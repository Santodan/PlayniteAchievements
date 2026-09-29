using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Lays its visible children out two per row in equal columns, as wide as the widest child.
    /// A child left alone on the last row spans both columns. Collapsed children take no cell,
    /// so the rest close up.
    /// </summary>
    public sealed class PairedRowsPanel : Panel
    {
        public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
            nameof(ColumnGap),
            typeof(double),
            typeof(PairedRowsPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
            nameof(RowGap),
            typeof(double),
            typeof(PairedRowsPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double ColumnGap
        {
            get => (double)GetValue(ColumnGapProperty);
            set => SetValue(ColumnGapProperty, value);
        }

        public double RowGap
        {
            get => (double)GetValue(RowGapProperty);
            set => SetValue(RowGapProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var visible = VisibleChildren();
            if (visible.Count == 0)
            {
                return new Size(0, 0);
            }

            var cellConstraint = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0, (availableSize.Width - ColumnGap) / 2);

            var cellWidth = 0.0;
            var height = 0.0;
            for (var i = 0; i < visible.Count; i += 2)
            {
                var spans = i + 1 >= visible.Count;
                var first = visible[i];
                first.Measure(new Size(spans ? availableSize.Width : cellConstraint, double.PositiveInfinity));
                var rowHeight = first.DesiredSize.Height;
                cellWidth = Math.Max(cellWidth, spans
                    ? Math.Max(0, (first.DesiredSize.Width - ColumnGap) / 2)
                    : first.DesiredSize.Width);

                if (!spans)
                {
                    var second = visible[i + 1];
                    second.Measure(new Size(cellConstraint, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, second.DesiredSize.Height);
                    cellWidth = Math.Max(cellWidth, second.DesiredSize.Width);
                }

                height += rowHeight + (i > 0 ? RowGap : 0);
            }

            var width = visible.Count == 1 ? visible[0].DesiredSize.Width : (cellWidth * 2) + ColumnGap;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var visible = VisibleChildren();
            var cellWidth = Math.Max(0, (finalSize.Width - ColumnGap) / 2);
            var y = 0.0;
            for (var i = 0; i < visible.Count; i += 2)
            {
                if (i > 0)
                {
                    y += RowGap;
                }

                var first = visible[i];
                if (i + 1 >= visible.Count)
                {
                    first.Arrange(new Rect(0, y, finalSize.Width, first.DesiredSize.Height));
                    y += first.DesiredSize.Height;
                    continue;
                }

                var second = visible[i + 1];
                var rowHeight = Math.Max(first.DesiredSize.Height, second.DesiredSize.Height);
                first.Arrange(new Rect(0, y, cellWidth, rowHeight));
                second.Arrange(new Rect(cellWidth + ColumnGap, y, cellWidth, rowHeight));
                y += rowHeight;
            }

            return finalSize;
        }

        private List<UIElement> VisibleChildren()
        {
            var visible = new List<UIElement>(InternalChildren.Count);
            foreach (UIElement child in InternalChildren)
            {
                if (child != null && child.Visibility != Visibility.Collapsed)
                {
                    visible.Add(child);
                }
            }

            return visible;
        }
    }
}
