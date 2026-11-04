using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Draws a grid's empty cells in one element instead of one Border each: the block surface
    /// for every cell it is given and, while editing, the block outline and (on cells large
    /// enough for it) the add-widget button. It spans the grid behind the block containers and
    /// reads track positions from the grid's definitions, so track resizes and window resizes
    /// only redraw it. A page of spacer cells therefore costs one element whatever its size.
    /// </summary>
    public sealed class ShowcaseCellLayer : FrameworkElement
    {
        // Match ShowcaseControl.CreateBlockContainer: the container's margin, its edit outline's
        // thickness, and the add button's size and glyph.
        public const double Inset = 4;
        public const double AddButtonSize = 68;
        private const double OutlineThickness = 2;
        private const string AddGlyph = "";
        private const double AddGlyphSize = 34;

        private IReadOnlyList<(int Row, int Column)> _cells = Array.Empty<(int Row, int Column)>();
        private bool _editing;

        public ShowcaseCellLayer()
        {
            IsHitTestVisible = false;
            Focusable = false;
        }

        /// <summary>Raised on a left click while editing: the cell, and whether it hit the add button.</summary>
        public event Action<int, int, bool> CellClicked;

        /// <summary>
        /// The cells to draw and whether the grid is being edited. While editing the layer takes
        /// clicks and drags over any cell no block container covers.
        /// </summary>
        public void Update(IReadOnlyList<(int Row, int Column)> cells, bool editing)
        {
            _cells = cells ?? Array.Empty<(int Row, int Column)>();
            _editing = editing;
            IsHitTestVisible = editing;
            AllowDrop = editing;
            InvalidateVisual();
        }

        /// <summary>The cell under a point in this layer's coordinates, or (-1, -1) outside the grid.</summary>
        public (int Row, int Column) CellAt(Point point)
        {
            var grid = VisualTreeHelper.GetParent(this) as Grid;
            if (grid == null)
            {
                return (-1, -1);
            }

            var row = TrackAt(grid.RowDefinitions, point.Y);
            var column = TrackAt(grid.ColumnDefinitions, point.X);
            return row < 0 || column < 0 ? (-1, -1) : (row, column);
        }

        private static int TrackAt(RowDefinitionCollection rows, double y)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (y < rows[index].Offset + rows[index].ActualHeight)
                {
                    return y >= rows[index].Offset ? index : -1;
                }
            }

            return -1;
        }

        private static int TrackAt(ColumnDefinitionCollection columns, double x)
        {
            for (var index = 0; index < columns.Count; index++)
            {
                if (x < columns[index].Offset + columns[index].ActualWidth)
                {
                    return x >= columns[index].Offset ? index : -1;
                }
            }

            return -1;
        }

        /// <summary>The cell's surface rectangle (inside the block inset), or Rect.Empty.</summary>
        private Rect SurfaceRect(Grid grid, int row, int column)
        {
            if (row < 0 || column < 0 ||
                row >= grid.RowDefinitions.Count ||
                column >= grid.ColumnDefinitions.Count)
            {
                return Rect.Empty;
            }

            var rowDefinition = grid.RowDefinitions[row];
            var columnDefinition = grid.ColumnDefinitions[column];
            var width = columnDefinition.ActualWidth - 2 * Inset;
            var height = rowDefinition.ActualHeight - 2 * Inset;
            return width <= 0 || height <= 0
                ? Rect.Empty
                : new Rect(columnDefinition.Offset + Inset, rowDefinition.Offset + Inset, width, height);
        }

        /// <summary>
        /// The add button's side for a block surface of the given size: full size where it fits,
        /// shrinking with the block, and 0 (no button) below <see cref="MinAddButtonSize"/>, where
        /// it would cover the block's merge chevrons. Shared with the block containers' button.
        /// </summary>
        public static double AddButtonSizeFor(double width, double height)
        {
            var size = Math.Min(AddButtonSize, Math.Min(width, height));
            return size < MinAddButtonSize ? 0 : size;
        }

        public const double MinAddButtonSize = 32;

        private static Rect AddButtonRect(Rect surface)
        {
            var size = surface.IsEmpty ? 0 : AddButtonSizeFor(surface.Width, surface.Height);
            return size <= 0
                ? Rect.Empty
                : new Rect(
                    surface.X + (surface.Width - size) / 2,
                    surface.Y + (surface.Height - size) / 2,
                    size,
                    size);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var grid = VisualTreeHelper.GetParent(this) as Grid;
            if (!_editing || grid == null)
            {
                return;
            }

            var point = e.GetPosition(this);
            var (row, column) = CellAt(point);
            if (row < 0)
            {
                return;
            }

            var onAdd = AddButtonRect(SurfaceRect(grid, row, column)).Contains(point);
            e.Handled = true;
            CellClicked?.Invoke(row, column, onAdd);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var grid = VisualTreeHelper.GetParent(this) as Grid;
            if (grid == null)
            {
                return;
            }

            if (_editing)
            {
                // A hit target over the whole grid; the block containers above it take their own cells.
                drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            }

            if (_cells.Count == 0)
            {
                return;
            }

            // Theme brushes are read per render and never frozen: a theme switch replaces them.
            var surface = TryFindResource("PlayAch.Brush.GridSurface") as Brush;
            var radius = Radius(TryFindResource("PlayAch.Radius.Section"));
            var outline = _editing && TryFindResource("PlayAch.Brush.Border") is Brush border
                ? new Pen(border, OutlineThickness)
                : null;
            var addBackground = TryFindResource("PlayAch.Brush.Overlay.Tint.08") as Brush;
            var glyphs = new Dictionary<double, FormattedText>();

            // All surfaces go into one geometry and all outlines into another, so a large grid
            // is two draw calls rather than two per cell; cells never overlap, so the result is
            // the same as drawing each one.
            var surfaces = new StreamGeometry();
            var outlines = new StreamGeometry();
            var addButtons = new List<Rect>();
            using (var surfaceContext = surfaces.Open())
            using (var outlineContext = outlines.Open())
            {
                foreach (var (row, column) in _cells)
                {
                    var rect = SurfaceRect(grid, row, column);
                    if (rect.IsEmpty)
                    {
                        continue;
                    }

                    AddRoundedRect(surfaceContext, rect, radius);
                    if (outline != null)
                    {
                        var stroke = Rect.Inflate(rect, -OutlineThickness / 2, -OutlineThickness / 2);
                        if (stroke.Width > 0 && stroke.Height > 0)
                        {
                            AddRoundedRect(outlineContext, stroke, radius);
                        }
                    }

                    var add = _editing ? AddButtonRect(rect) : Rect.Empty;
                    if (!add.IsEmpty)
                    {
                        addButtons.Add(add);
                    }
                }
            }

            surfaces.Freeze();
            outlines.Freeze();
            drawingContext.DrawGeometry(surface, null, surfaces);
            if (outline != null)
            {
                drawingContext.DrawGeometry(null, outline, outlines);
            }

            foreach (var add in addButtons)
            {
                if (!glyphs.TryGetValue(add.Width, out var glyph))
                {
                    glyph = CreateAddGlyph(AddGlyphSize * add.Width / AddButtonSize);
                    glyphs[add.Width] = glyph;
                }

                drawingContext.DrawRoundedRectangle(addBackground, null, add, 3, 3);
                if (glyph != null)
                {
                    drawingContext.PushOpacity(0.85);
                    drawingContext.DrawText(
                        glyph,
                        new Point(
                            add.X + (add.Width - glyph.Width) / 2,
                            add.Y + (add.Height - glyph.Height) / 2));
                    drawingContext.Pop();
                }
            }
        }

        // A rounded rectangle as DrawRoundedRectangle draws it: the radius is capped at half the
        // shorter side.
        private static void AddRoundedRect(StreamGeometryContext context, Rect rect, double radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
            if (radius <= 0)
            {
                context.BeginFigure(rect.TopLeft, isFilled: true, isClosed: true);
                context.LineTo(rect.TopRight, isStroked: true, isSmoothJoin: false);
                context.LineTo(rect.BottomRight, isStroked: true, isSmoothJoin: false);
                context.LineTo(rect.BottomLeft, isStroked: true, isSmoothJoin: false);
                return;
            }

            var corner = new Size(radius, radius);
            context.BeginFigure(new Point(rect.Left + radius, rect.Top), isFilled: true, isClosed: true);
            context.LineTo(new Point(rect.Right - radius, rect.Top), true, false);
            context.ArcTo(new Point(rect.Right, rect.Top + radius), corner, 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(rect.Right, rect.Bottom - radius), true, false);
            context.ArcTo(new Point(rect.Right - radius, rect.Bottom), corner, 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(rect.Left + radius, rect.Bottom), true, false);
            context.ArcTo(new Point(rect.Left, rect.Bottom - radius), corner, 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(rect.Left, rect.Top + radius), true, false);
            context.ArcTo(new Point(rect.Left + radius, rect.Top), corner, 0, false, SweepDirection.Clockwise, true, false);
        }

        private FormattedText CreateAddGlyph(double emSize)
        {
            var family = TryFindResource("PlayAch.FontFamily.Icon") as FontFamily;
            var brush = TryFindResource("PlayAch.Brush.Accent") as Brush;
            if (family == null || brush == null)
            {
                return null;
            }

            return new FormattedText(
                AddGlyph,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                emSize,
                brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        private static double Radius(object resource)
        {
            switch (resource)
            {
                case CornerRadius corner:
                    return corner.TopLeft;
                case double value:
                    return value;
                default:
                    return 0;
            }
        }
    }
}
