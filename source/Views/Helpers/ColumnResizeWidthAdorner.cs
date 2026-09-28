using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Transient "120 px" pills shown over the two columns that share the boundary being dragged,
    /// styled like the showcase layout's track rulers. Lives in the grid's adorner layer for the
    /// duration of a gripper drag and is removed when the drag ends.
    /// </summary>
    internal sealed class ColumnResizeWidthAdorner : Adorner
    {
        private const double PillGap = 2d;

        private readonly DataGrid _grid;
        private readonly Canvas _canvas = new Canvas { IsHitTestVisible = false };
        private readonly List<Pill> _pills = new List<Pill>();

        public ColumnResizeWidthAdorner(DataGrid grid)
            : base(grid)
        {
            _grid = grid;
            IsHitTestVisible = false;
            AddVisualChild(_canvas);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _canvas;
        }

        /// <summary>
        /// Replaces the pills with one per column, in the given order.
        /// </summary>
        public void SetColumns(IEnumerable<DataGridColumn> columns)
        {
            _pills.Clear();
            _canvas.Children.Clear();
            foreach (var column in (columns ?? Enumerable.Empty<DataGridColumn>()).Where(c => c != null).Distinct())
            {
                var pill = CreatePill(column);
                _pills.Add(pill);
                _canvas.Children.Add(pill.Border);
            }
        }

        /// <summary>
        /// Updates each pill's text and re-centres it over its column's header. Cheap enough to run
        /// on every layout pass while the drag lasts; nothing is written when nothing changed.
        /// </summary>
        public void Refresh(Func<DataGridColumn, string> label)
        {
            if (_pills.Count == 0 || label == null)
            {
                return;
            }

            var headers = VisualTreeHelpers.FindVisualChildren<DataGridColumnHeader>(_grid).ToList();
            foreach (var pill in _pills)
            {
                var text = label(pill.Column) ?? string.Empty;
                if (!string.Equals(pill.Text.Text, text, StringComparison.Ordinal))
                {
                    pill.Text.Text = text;
                }

                var header = headers.FirstOrDefault(h => ReferenceEquals(h.Column, pill.Column));
                if (header == null || !header.IsVisible || header.ActualWidth <= 0)
                {
                    SetVisibility(pill.Border, Visibility.Collapsed);
                    continue;
                }

                Point origin;
                try
                {
                    origin = header.TranslatePoint(new Point(0, 0), _grid);
                }
                catch (InvalidOperationException)
                {
                    SetVisibility(pill.Border, Visibility.Collapsed);
                    continue;
                }

                SetVisibility(pill.Border, Visibility.Visible);
                pill.Border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = pill.Border.DesiredSize;
                var left = origin.X + (header.ActualWidth - size.Width) / 2;
                var top = origin.Y - size.Height - PillGap;
                if (!FitsInLayer(top))
                {
                    // No room above the grid: sit just inside the header instead.
                    top = origin.Y + PillGap;
                }

                SetCanvasPosition(pill.Border, left, top);
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _canvas.Arrange(new Rect(new Point(0, 0), finalSize));
            return finalSize;
        }

        private bool FitsInLayer(double top)
        {
            if (!(VisualTreeHelper.GetParent(this) is UIElement layer))
            {
                return true;
            }

            try
            {
                return _grid.TranslatePoint(new Point(0, top), layer).Y >= 0;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static void SetVisibility(UIElement element, Visibility visibility)
        {
            if (element.Visibility != visibility)
            {
                element.Visibility = visibility;
            }
        }

        private static void SetCanvasPosition(UIElement element, double left, double top)
        {
            if (Math.Abs(Canvas.GetLeft(element) - left) > 0.5 || double.IsNaN(Canvas.GetLeft(element)))
            {
                Canvas.SetLeft(element, left);
            }

            if (Math.Abs(Canvas.GetTop(element) - top) > 0.5 || double.IsNaN(Canvas.GetTop(element)))
            {
                Canvas.SetTop(element, top);
            }
        }

        private Pill CreatePill(DataGridColumn column)
        {
            var text = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            text.SetResourceReference(TextBlock.FontSizeProperty, "PlayAch.FontSize.Caption");

            var border = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                Opacity = 0.9,
                Child = text,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(border, 41);
            if (_grid.TryFindResource("PlayAch.Brush.PopupSurface") is Brush)
            {
                border.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.PopupSurface");
            }
            else
            {
                border.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));
            }

            return new Pill { Column = column, Border = border, Text = text };
        }

        private sealed class Pill
        {
            public DataGridColumn Column { get; set; }
            public Border Border { get; set; }
            public TextBlock Text { get; set; }
        }
    }
}
