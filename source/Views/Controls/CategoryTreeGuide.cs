using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Draws one category row's share of the tree: the lanes still open above it, its own stem and
    /// arm, a junction dot, and a descender into its children.
    ///
    /// Rendered rather than composed from panels because the connectors have to line up across
    /// virtualized, recycled rows: a shared lane table and one measured width per depth give every
    /// row the same x positions, where nested panels would drift with each row's own content.
    ///
    /// A null <see cref="Shape"/> collapses the element to zero width, which is what keeps this off
    /// game-summary rows and off a category list that has no nesting to show.
    /// </summary>
    public sealed class CategoryTreeGuide : FrameworkElement
    {
        /// <summary>
        /// Never take more than this share of the cell. The name column can be dragged down to its
        /// MinWidth, and a guide sized purely by depth would leave nothing for the name; past this
        /// the guide clips instead, losing outer lanes rather than the label.
        /// </summary>
        private const double MaxCellWidthShare = 0.5d;

        static CategoryTreeGuide()
        {
            // Guide lines are decoration over an already-hit-testable row: clicks belong to the row.
            IsHitTestVisibleProperty.OverrideMetadata(
                typeof(CategoryTreeGuide),
                new UIPropertyMetadata(false));
        }

        public static readonly DependencyProperty ShapeProperty =
            DependencyProperty.Register(
                nameof(Shape),
                typeof(CategoryTreeShape),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.AffectsMeasure |
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public CategoryTreeShape Shape
        {
            get => (CategoryTreeShape)GetValue(ShapeProperty);
            set => SetValue(ShapeProperty, value);
        }

        public static readonly DependencyProperty LineBrushProperty =
            DependencyProperty.Register(
                nameof(LineBrush),
                typeof(Brush),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush LineBrush
        {
            get => (Brush)GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        /// <summary>Fill for the junction bead; falls back to the line brush.</summary>
        public static readonly DependencyProperty NodeBrushProperty =
            DependencyProperty.Register(
                nameof(NodeBrush),
                typeof(Brush),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush NodeBrush
        {
            get => (Brush)GetValue(NodeBrushProperty);
            set => SetValue(NodeBrushProperty, value);
        }

        /// <summary>
        /// Opacity of the connector lines. Held below the beads deliberately: the lines are
        /// structure and the beads are the rows, so the beads should read first. It also separates
        /// the lanes from the grid's own row separators, which are full-strength border brush.
        ///
        /// Applied uniformly rather than per lane depth - a lane is drawn by every row it passes
        /// through, so any per-row variation would show up as the line changing weight mid-column.
        /// </summary>
        public static readonly DependencyProperty LineOpacityProperty =
            DependencyProperty.Register(
                nameof(LineOpacity),
                typeof(double),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(0.55d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double LineOpacity
        {
            get => (double)GetValue(LineOpacityProperty);
            set => SetValue(LineOpacityProperty, value);
        }

        /// <summary>
        /// Opts this instance into the expand/collapse toggle drawn on a parent's descender. Off by
        /// default so the other guide surfaces (category picker, filter dropdown, Manage tab) keep
        /// rendering pure decoration. Turning it on re-enables hit testing per instance - but only
        /// the toggle's circle claims hits (see <see cref="HitTestCore"/>), so row clicks elsewhere
        /// keep belonging to the row.
        /// </summary>
        public static readonly DependencyProperty ShowCollapseToggleProperty =
            DependencyProperty.Register(
                nameof(ShowCollapseToggle),
                typeof(bool),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnShowCollapseToggleChanged));

        public bool ShowCollapseToggle
        {
            get => (bool)GetValue(ShowCollapseToggleProperty);
            set => SetValue(ShowCollapseToggleProperty, value);
        }

        private static void OnShowCollapseToggleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var guide = (CategoryTreeGuide)d;
            var enabled = (bool)e.NewValue;
            guide.IsHitTestVisible = enabled;
            guide.Cursor = enabled ? Cursors.Hand : null;
        }

        /// <summary>
        /// Whether this row's subtree is currently hidden. Flips the toggle glyph from "-" to "+"
        /// and stands in for the children the shape can no longer see - a collapsed row's stamped
        /// shape reads HasChildren = false because its children were filtered out before stamping.
        /// </summary>
        public static readonly DependencyProperty IsCollapsedProperty =
            DependencyProperty.Register(
                nameof(IsCollapsed),
                typeof(bool),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsCollapsed
        {
            get => (bool)GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        /// <summary>Fill behind the toggle glyph while the mouse is over it; null for no fill.</summary>
        public static readonly DependencyProperty ToggleHoverBackgroundBrushProperty =
            DependencyProperty.Register(
                nameof(ToggleHoverBackgroundBrush),
                typeof(Brush),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush ToggleHoverBackgroundBrush
        {
            get => (Brush)GetValue(ToggleHoverBackgroundBrushProperty);
            set => SetValue(ToggleHoverBackgroundBrushProperty, value);
        }

        /// <summary>Raised when the collapse toggle is clicked; bubbles to the hosting grid.</summary>
        public static readonly RoutedEvent CollapseToggleClickedEvent =
            EventManager.RegisterRoutedEvent(
                "CollapseToggleClicked",
                RoutingStrategy.Bubble,
                typeof(RoutedEventHandler),
                typeof(CategoryTreeGuide));

        public event RoutedEventHandler CollapseToggleClicked
        {
            add => AddHandler(CollapseToggleClickedEvent, value);
            remove => RemoveHandler(CollapseToggleClickedEvent, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var shape = Shape;
            if (shape == null)
            {
                return new Size(0d, 0d);
            }

            var width = CategoryTreeGuideMetrics.GetGuideWidth(shape.Depth);
            if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0d)
            {
                // The width cap protects the name label from a deep guide.
                width = Math.Min(width, availableSize.Width * MaxCellWidthShare);
            }

            // Zero desired height: the row decides how tall it is, and the guide stretches into it.
            return new Size(Math.Max(0d, width), 0d);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var shape = Shape;
            var lineBrush = LineBrush;
            var height = RenderSize.Height;
            if (shape == null || lineBrush == null || height <= 0d || RenderSize.Width <= 0d)
            {
                return;
            }

            var pen = CreateGuidePen(lineBrush, 1d);

            var mid = Math.Round(height / 2d);
            var dotX = CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth);

            // A collapsed parent's children were filtered out before stamping, so its shape reads
            // HasChildren = false; the IsCollapsed flag carries the parenthood cue in that state.
            var effectiveHasChildren = shape.HasChildren || (ShowCollapseToggle && IsCollapsed);
            var beadRadius = CategoryTreeGuideMetrics.GetBeadRadius(shape.Depth, effectiveHasChildren);

            // A leaf's bead is an outline with the row showing through it, so the arm has to stop at
            // its edge - anything drawn under it shows through the middle. A parent's bead is solid
            // and hides what it covers, so its arm runs the whole way in.
            var armEndX = effectiveHasChildren
                ? dotX
                : Math.Max(CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth - 1), dotX - beadRadius);

            var hasToggle = TryGetToggleGeometry(out var toggleCentre, out var toggleRadius);

            // Half-pixel offsets on a 1px pen, so the lines land on device pixels instead of
            // straddling two and rendering as a soft 2px smear.
            drawingContext.PushGuidelineSet(BuildGuidelines(
                shape, dotX, mid, hasToggle ? toggleCentre.Y : double.NaN));
            drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));

            // Lines under the beads: the structure recedes, the rows read first.
            drawingContext.PushOpacity(Math.Max(0d, Math.Min(1d, LineOpacity)));
            try
            {
                DrawAncestorLanes(drawingContext, pen, shape, height);
                DrawOwnStem(drawingContext, pen, shape, armEndX, mid, height);

                if (hasToggle)
                {
                    // The glyph circle's interior is transparent, so the descender is split around
                    // it. Collapsed, nothing follows below: only the stub down to the glyph stays,
                    // keeping the circled "+" attached to the tree instead of floating.
                    drawingContext.DrawLine(
                        pen, new Point(dotX, mid), new Point(dotX, toggleCentre.Y - toggleRadius));
                    if (!IsCollapsed)
                    {
                        drawingContext.DrawLine(
                            pen, new Point(dotX, toggleCentre.Y + toggleRadius), new Point(dotX, height));
                    }
                }
                else if (shape.HasChildren)
                {
                    drawingContext.DrawLine(pen, new Point(dotX, mid), new Point(dotX, height));
                }
            }
            finally
            {
                drawingContext.Pop();
            }

            try
            {
                DrawJunction(drawingContext, pen, effectiveHasChildren, dotX, mid, beadRadius);

                if (hasToggle)
                {
                    DrawToggle(drawingContext, pen, toggleCentre, toggleRadius);
                }
            }
            finally
            {
                drawingContext.Pop();
                drawingContext.Pop();
            }
        }

        /// <summary>
        /// Whether this row shows the expand/collapse toggle, and where. The glyph sits on the
        /// descender at the child lane, just inside the row's bottom edge - reading as "on the line
        /// between this row and its children" while staying inside this row's render bounds
        /// (per-row rendering cannot straddle the row boundary). On a short row it shrinks to stay
        /// clear of the junction bead, and past a floor of 3px it is skipped entirely - children
        /// stay reachable through Expand All.
        /// </summary>
        private bool TryGetToggleGeometry(out Point centre, out double radius)
        {
            centre = default(Point);
            radius = 0d;

            var shape = Shape;
            var height = RenderSize.Height;
            if (!ShowCollapseToggle || shape == null ||
                (!shape.HasChildren && !IsCollapsed) || height <= 0d)
            {
                return false;
            }

            var mid = Math.Round(height / 2d);
            var dotX = CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth);
            var beadRadius = CategoryTreeGuideMetrics.GetBeadRadius(shape.Depth, hasChildren: true);

            var toggleRadius = CategoryTreeGuideMetrics.GetToggleRadius(shape.Depth);
            var clearance = mid + beadRadius + 1d;
            var maxRadius = (height - 1.5d - clearance) / 2d;
            toggleRadius = Math.Min(toggleRadius, maxRadius);
            if (toggleRadius < 3d)
            {
                return false;
            }

            centre = new Point(dotX, height - toggleRadius - 1.5d);
            radius = toggleRadius;
            return true;
        }

        /// <summary>
        /// The circled "-" (expanded) or "+" (collapsed), drawn at the beads' full strength in the
        /// bead brush. Hover fills the circle with the row-hover background - the same feedback the
        /// control bar's chevron toggles give - and IsMouseOver only reads true over the toggle's
        /// own hit circle, because that circle is all this element ever claims for hit testing.
        /// </summary>
        private void DrawToggle(DrawingContext drawingContext, Pen linePen, Point centre, double radius)
        {
            var glyphBrush = NodeBrush ?? linePen.Brush;
            var fill = IsMouseOver ? ToggleHoverBackgroundBrush : null;
            drawingContext.DrawEllipse(fill, CreateGuidePen(glyphBrush, 1.2d), centre, radius, radius);

            var arm = radius - 2d;
            var strokePen = CreateGuidePen(glyphBrush, 1.4d);
            drawingContext.DrawLine(
                strokePen, new Point(centre.X - arm, centre.Y), new Point(centre.X + arm, centre.Y));
            if (IsCollapsed)
            {
                drawingContext.DrawLine(
                    strokePen, new Point(centre.X, centre.Y - arm), new Point(centre.X, centre.Y + arm));
            }
        }

        /// <summary>
        /// Only the toggle's (enlarged) circle is a hit target; everywhere else the guide stays
        /// transparent to the mouse and clicks belong to the row, exactly as when hit testing is
        /// off. The circle is grown past the drawn glyph because a 3-5px glyph is an unfair target.
        /// </summary>
        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            if (!TryGetToggleGeometry(out var centre, out var radius))
            {
                return null;
            }

            var hitRadius = Math.Max(9d, radius + 4d);
            return (hitTestParameters.HitPoint - centre).Length <= hitRadius
                ? new PointHitTestResult(this, hitTestParameters.HitPoint)
                : null;
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);

            // Reached only via HitTestCore's circle. Handling the tunneling event marks the shared
            // args handled, so the paired bubbling MouseLeftButtonDown never reaches DataGridCell -
            // no selection change, and so no drill-in.
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(CollapseToggleClickedEvent, this));
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            if (ShowCollapseToggle)
            {
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (ShowCollapseToggle)
            {
                InvalidateVisual();
            }
        }

        /// <summary>Full-height lines for the ancestors that still have siblings below this row.</summary>
        private static void DrawAncestorLanes(
            DrawingContext drawingContext,
            Pen pen,
            CategoryTreeShape shape,
            double height)
        {
            for (var lane = 0; lane < shape.AncestorContinues.Count; lane++)
            {
                if (!shape.AncestorContinues[lane])
                {
                    continue;
                }

                var x = CategoryTreeGuideMetrics.GetLaneCentre(lane + 1);
                drawingContext.DrawLine(pen, new Point(x, 0d), new Point(x, height));
            }
        }

        /// <summary>
        /// The row's own connector. A last sibling closes with a rounded elbow and stops at the
        /// middle; anything else keeps the lane running to the bottom for the sibling underneath.
        /// A root has neither - it has no parent to connect to.
        /// </summary>
        private static void DrawOwnStem(
            DrawingContext drawingContext,
            Pen pen,
            CategoryTreeShape shape,
            double armEndX,
            double mid,
            double height)
        {
            if (shape.Depth <= 1)
            {
                return;
            }

            var stemX = CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth - 1);
            if (!shape.IsLastSibling)
            {
                drawingContext.DrawLine(pen, new Point(stemX, 0d), new Point(stemX, height));
                drawingContext.DrawLine(pen, new Point(stemX, mid), new Point(armEndX, mid));
                return;
            }

            var radius = Math.Min(CategoryTreeGuideMetrics.CornerRadius, Math.Max(0d, armEndX - stemX));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(stemX, 0d), false, false);
                context.LineTo(new Point(stemX, mid - radius), true, false);
                context.QuadraticBezierTo(
                    new Point(stemX, mid),
                    new Point(stemX + radius, mid),
                    true,
                    false);
                context.LineTo(new Point(armEndX, mid), true, false);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        /// <summary>
        /// A solid bead for a node that opens a subtree, an outlined one for a leaf. This is the
        /// one cue that survives the guide clipping on a narrow column, so it carries
        /// folder-or-leaf on its own, and it is drawn at full strength - it is the part of the
        /// guide meant to be seen, against lanes that are deliberately faint.
        ///
        /// A leaf's bead is left unfilled rather than filled with the row's background colour. The
        /// row background is a theme resource that plenty of themes leave transparent, and it
        /// changes under hover and selection regardless - so any fill chosen here is a guess at the
        /// backdrop. Nothing is drawn under the bead instead (see the arm trim in OnRender), which
        /// needs no such guess.
        /// </summary>
        private void DrawJunction(
            DrawingContext drawingContext,
            Pen pen,
            bool hasChildren,
            double dotX,
            double mid,
            double beadRadius)
        {
            var centre = new Point(dotX, mid);
            var beadBrush = NodeBrush ?? pen.Brush;
            if (hasChildren)
            {
                drawingContext.DrawEllipse(beadBrush, null, centre, beadRadius, beadRadius);
                return;
            }

            var outline = CreateGuidePen(beadBrush, 1.5d);
            drawingContext.DrawEllipse(null, outline, centre, beadRadius, beadRadius);
        }

        /// <summary>
        /// Builds a pen, frozen only when it actually can be.
        ///
        /// The brush comes from a theme resource, and a Playnite theme brush routinely resolves
        /// through DynamicResource or carries unfrozen sub-values - which makes the pen built from
        /// it unfreezable. Freeze() throws on such a pen rather than returning false, and an
        /// exception out of OnRender takes the whole application down, so the CanFreeze check is
        /// load-bearing rather than defensive.
        /// </summary>
        internal static Pen CreateGuidePen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness);
            if (pen.CanFreeze)
            {
                pen.Freeze();
            }

            return pen;
        }

        private static GuidelineSet BuildGuidelines(
            CategoryTreeShape shape,
            double dotX,
            double mid,
            double toggleY = double.NaN)
        {
            var guidelines = new GuidelineSet();
            guidelines.GuidelinesY.Add(mid + 0.5d);
            if (!double.IsNaN(toggleY))
            {
                guidelines.GuidelinesY.Add(toggleY + 0.5d);
            }

            for (var lane = 0; lane < shape.AncestorContinues.Count; lane++)
            {
                guidelines.GuidelinesX.Add(CategoryTreeGuideMetrics.GetLaneCentre(lane + 1) + 0.5d);
            }

            if (shape.Depth > 1)
            {
                guidelines.GuidelinesX.Add(CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth - 1) + 0.5d);
            }

            guidelines.GuidelinesX.Add(dotX + 0.5d);

            guidelines.Freeze();
            return guidelines;
        }
    }
}
