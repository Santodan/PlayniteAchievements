using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Lays its children out in fixed pairs by position (first and second, third and fourth, ...),
    /// in equal columns as wide as the widest child. A collapsed child drops out of its pair and
    /// the other spans both columns; a pair with both children collapsed takes no row.
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
            var rows = VisibleRows();
            if (rows.Count == 0)
            {
                return new Size(0, 0);
            }

            var cellConstraint = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0, (availableSize.Width - ColumnGap) / 2);

            var cellWidth = 0.0;
            var spanWidth = 0.0;
            var anyPair = false;
            var height = 0.0;
            for (var i = 0; i < rows.Count; i++)
            {
                var (first, second) = rows[i];
                double rowHeight;
                if (second == null)
                {
                    first.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                    rowHeight = first.DesiredSize.Height;
                    spanWidth = Math.Max(spanWidth, first.DesiredSize.Width);
                }
                else
                {
                    anyPair = true;
                    first.Measure(new Size(cellConstraint, double.PositiveInfinity));
                    second.Measure(new Size(cellConstraint, double.PositiveInfinity));
                    rowHeight = Math.Max(first.DesiredSize.Height, second.DesiredSize.Height);
                    cellWidth = Math.Max(cellWidth, Math.Max(first.DesiredSize.Width, second.DesiredSize.Width));
                }

                height += rowHeight + (i > 0 ? RowGap : 0);
            }

            var width = anyPair
                ? Math.Max((cellWidth * 2) + ColumnGap, spanWidth)
                : spanWidth;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var cellWidth = Math.Max(0, (finalSize.Width - ColumnGap) / 2);
            var y = 0.0;
            var rows = VisibleRows();
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    y += RowGap;
                }

                var (first, second) = rows[i];
                if (second == null)
                {
                    first.Arrange(new Rect(0, y, finalSize.Width, first.DesiredSize.Height));
                    y += first.DesiredSize.Height;
                    continue;
                }

                var rowHeight = Math.Max(first.DesiredSize.Height, second.DesiredSize.Height);
                first.Arrange(new Rect(0, y, cellWidth, rowHeight));
                second.Arrange(new Rect(cellWidth + ColumnGap, y, cellWidth, rowHeight));
                y += rowHeight;
            }

            return finalSize;
        }

        /// <summary>
        /// Rows of the fixed pairs that still have a visible child. A row with one visible child
        /// has it in <c>first</c> and a null <c>second</c>.
        /// </summary>
        private List<(UIElement first, UIElement second)> VisibleRows()
        {
            var rows = new List<(UIElement first, UIElement second)>((InternalChildren.Count + 1) / 2);
            for (var i = 0; i < InternalChildren.Count; i += 2)
            {
                var a = VisibleOrNull(InternalChildren[i]);
                var b = i + 1 < InternalChildren.Count ? VisibleOrNull(InternalChildren[i + 1]) : null;
                if (a != null)
                {
                    rows.Add((a, b));
                }
                else if (b != null)
                {
                    rows.Add((b, null));
                }
            }

            return rows;
        }

        private static UIElement VisibleOrNull(UIElement child)
        {
            return child != null && child.Visibility != Visibility.Collapsed ? child : null;
        }
    }
}
