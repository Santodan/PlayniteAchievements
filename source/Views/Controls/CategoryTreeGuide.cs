using System;
using System.Windows;
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

        // Long dashes for the self-row twig: at 1px the stock Dash style reads as dots, and the
        // twig has a whole label width to cover.
        private static readonly DashStyle TwigDashStyle = CreateTwigDashStyle();

        private static DashStyle CreateTwigDashStyle()
        {
            var style = new DashStyle(new double[] { 4d, 3d }, 0d);
            style.Freeze();
            return style;
        }

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
                // The width cap protects the name label from a deep guide; a self row has no name,
                // so its guide claims the whole cell and the twig runs through the label void.
                width = shape.IsSelfRow
                    ? availableSize.Width
                    : Math.Min(width, availableSize.Width * MaxCellWidthShare);
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

            var pen = CreatePen(lineBrush, 1d);

            var mid = Math.Round(height / 2d);
            var dotX = CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth);
            var beadRadius = CategoryTreeGuideMetrics.GetBeadRadius(shape.Depth, shape.HasChildren);

            // A leaf's bead is an outline with the row showing through it, so the arm has to stop at
            // its edge - anything drawn under it shows through the middle. A parent's bead is solid
            // and hides what it covers, so its arm runs the whole way in.
            var armEndX = shape.HasChildren
                ? dotX
                : Math.Max(CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth - 1), dotX - beadRadius);

            // Half-pixel offsets on a 1px pen, so the lines land on device pixels instead of
            // straddling two and rendering as a soft 2px smear.
            drawingContext.PushGuidelineSet(BuildGuidelines(shape, dotX, mid));
            drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));

            // Lines under the beads: the structure recedes, the rows read first.
            drawingContext.PushOpacity(Math.Max(0d, Math.Min(1d, LineOpacity)));
            try
            {
                DrawAncestorLanes(drawingContext, pen, shape, height);
                DrawOwnStem(drawingContext, pen, shape, armEndX, mid, height);

                if (shape.HasChildren)
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
                // A self row is an annotation on its category, not a node of the tree: instead of
                // a bead it gets the dashed twig, drawn at the beads' full strength - it is the
                // row's one identifying mark against deliberately faint lanes.
                if (shape.IsSelfRow)
                {
                    DrawSelfTwig(drawingContext, shape, mid, RenderSize.Width);
                }
                else
                {
                    DrawJunction(drawingContext, pen, shape, dotX, mid, beadRadius);
                }
            }
            finally
            {
                drawingContext.Pop();
                drawingContext.Pop();
            }
        }

        /// <summary>
        /// The self row's own connector: a dashed drop falling out of the parent's name directly
        /// above, a rounded turn right, and a run through the label void to the cell's edge (the
        /// guide claims the whole cell for a self row, see MeasureOverride), ending in no bead
        /// where every real node ends in one. Hanging off the name rather than the lanes is what
        /// says "the row above, itself" - the lanes say where in the tree, the name says which
        /// category. Dashed, and at full strength against the deliberately faint lanes; it stays
        /// in the line brush - the accent is reserved for the beads.
        /// </summary>
        private void DrawSelfTwig(
            DrawingContext drawingContext,
            CategoryTreeShape shape,
            double mid,
            double width)
        {
            var dropX = GetSelfDropX(shape);
            var armEnd = Math.Max(dropX + CategoryTreeGuideMetrics.CornerRadius, width - 2d);
            var radius = Math.Min(CategoryTreeGuideMetrics.CornerRadius, Math.Max(0d, mid));

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(dropX, 0d), false, false);
                context.LineTo(new Point(dropX, mid - radius), true, false);
                context.QuadraticBezierTo(
                    new Point(dropX, mid),
                    new Point(dropX + radius, mid),
                    true,
                    false);
                context.LineTo(new Point(armEnd, mid), true, false);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, CreatePen(LineBrush, 1d, TwigDashStyle), geometry);
        }

        /// <summary>
        /// Where the self row's drop sits: just inside the point the parent's name starts, one
        /// level up, so the line reads as falling out of that name rather than out of the lanes.
        /// </summary>
        private static double GetSelfDropX(CategoryTreeShape shape)
        {
            return CategoryTreeGuideMetrics.GetGuideWidth(shape.Depth - 1) +
                CategoryTreeGuideMetrics.CornerRadius;
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
        /// A root has neither - it has no parent to connect to. A self row draws no stem or arm
        /// here at all: its own mark is the dashed drop-and-turn hanging off the parent's name
        /// (see DrawSelfTwig), visibly not another node of the tree.
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
            if (shape.IsSelfRow)
            {
                // Only the lane continuation, kept solid and faint like every shared lane - a
                // per-row texture change would show as the column flickering mid-run. By
                // construction the child categories that made the node mixed follow beneath, so
                // the lane continues; a defensive last-sibling self row skips it rather than
                // drawing a line stopping mid-air. The row's own arm is the dashed twig, drawn
                // with the beads at full strength (see DrawSelfTwig).
                if (!shape.IsLastSibling)
                {
                    drawingContext.DrawLine(pen, new Point(stemX, 0d), new Point(stemX, height));
                }

                return;
            }

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
            CategoryTreeShape shape,
            double dotX,
            double mid,
            double beadRadius)
        {
            var centre = new Point(dotX, mid);
            var beadBrush = NodeBrush ?? pen.Brush;
            if (shape.HasChildren)
            {
                drawingContext.DrawEllipse(beadBrush, null, centre, beadRadius, beadRadius);
                return;
            }

            var outline = CreatePen(beadBrush, 1.5d);
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
        private static Pen CreatePen(Brush brush, double thickness, DashStyle dashStyle = null)
        {
            var pen = new Pen(brush, thickness);
            if (dashStyle != null)
            {
                pen.DashStyle = dashStyle;
                pen.DashCap = PenLineCap.Flat;
            }

            if (pen.CanFreeze)
            {
                pen.Freeze();
            }

            return pen;
        }

        private static GuidelineSet BuildGuidelines(CategoryTreeShape shape, double dotX, double mid)
        {
            var guidelines = new GuidelineSet();
            guidelines.GuidelinesY.Add(mid + 0.5d);

            for (var lane = 0; lane < shape.AncestorContinues.Count; lane++)
            {
                guidelines.GuidelinesX.Add(CategoryTreeGuideMetrics.GetLaneCentre(lane + 1) + 0.5d);
            }

            if (shape.Depth > 1)
            {
                guidelines.GuidelinesX.Add(CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth - 1) + 0.5d);
            }

            guidelines.GuidelinesX.Add(dotX + 0.5d);
            if (shape.IsSelfRow)
            {
                guidelines.GuidelinesX.Add(GetSelfDropX(shape) + 0.5d);
            }

            guidelines.Freeze();
            return guidelines;
        }
    }
}
