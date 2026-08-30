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
        /// Fill punched into a leaf's bead, normally the row background. Without it a leaf bead is a
        /// ring with the lane running through it; with it the bead sits on the line.
        /// </summary>
        public static readonly DependencyProperty NodeHoleBrushProperty =
            DependencyProperty.Register(
                nameof(NodeHoleBrush),
                typeof(Brush),
                typeof(CategoryTreeGuide),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush NodeHoleBrush
        {
            get => (Brush)GetValue(NodeHoleBrushProperty);
            set => SetValue(NodeHoleBrushProperty, value);
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

            var pen = new Pen(lineBrush, 1d);
            pen.Freeze();

            var mid = Math.Round(height / 2d);
            var dotX = CategoryTreeGuideMetrics.GetLaneCentre(shape.Depth);

            // Half-pixel offsets on a 1px pen, so the lines land on device pixels instead of
            // straddling two and rendering as a soft 2px smear.
            drawingContext.PushGuidelineSet(BuildGuidelines(shape, dotX, mid));
            drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));

            // Lines under the beads: the structure recedes, the rows read first.
            drawingContext.PushOpacity(Math.Max(0d, Math.Min(1d, LineOpacity)));
            try
            {
                DrawAncestorLanes(drawingContext, pen, shape, height);
                DrawOwnStem(drawingContext, pen, shape, dotX, mid, height);

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
                DrawJunction(drawingContext, pen, shape, dotX, mid);
            }
            finally
            {
                drawingContext.Pop();
                drawingContext.Pop();
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
            double dotX,
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
                drawingContext.DrawLine(pen, new Point(stemX, mid), new Point(dotX, mid));
                return;
            }

            var radius = Math.Min(CategoryTreeGuideMetrics.CornerRadius, Math.Max(0d, dotX - stemX));
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
                context.LineTo(new Point(dotX, mid), true, false);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        /// <summary>
        /// A solid bead for a node that opens a subtree, an outlined one for a leaf. This is the
        /// one cue that survives the guide clipping on a narrow column, so it carries
        /// folder-or-leaf on its own, and it is drawn at full strength - it is the part of the
        /// guide meant to be seen, against lanes that are deliberately faint.
        /// </summary>
        private void DrawJunction(
            DrawingContext drawingContext,
            Pen pen,
            CategoryTreeShape shape,
            double dotX,
            double mid)
        {
            var centre = new Point(dotX, mid);
            var beadBrush = NodeBrush ?? pen.Brush;
            if (shape.HasChildren)
            {
                drawingContext.DrawEllipse(
                    beadBrush,
                    null,
                    centre,
                    CategoryTreeGuideMetrics.NodeRadius,
                    CategoryTreeGuideMetrics.NodeRadius);
                return;
            }

            // Hole fill first, so the lane does not show through the middle of a leaf bead.
            var leafRadius = CategoryTreeGuideMetrics.LeafNodeRadius;
            var outline = new Pen(beadBrush, 1.5d);
            outline.Freeze();
            drawingContext.DrawEllipse(NodeHoleBrush, outline, centre, leafRadius, leafRadius);
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
            guidelines.Freeze();
            return guidelines;
        }
    }
}
