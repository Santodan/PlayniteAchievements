using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The upper half of a mixed category's self-row connector: a dashed drop from the bottom of
    /// the category's name text to the bottom edge of its row, where the self row's
    /// <see cref="CategoryTreeGuide"/> picks it up and turns right.
    ///
    /// Zero-size and layout-inert; everything is measured from the name TextBlock it is pointed
    /// at. The drop centres on the text's bottom LINE (via the text pointer of the last character),
    /// not the block, so a wrapped title anchors under its final line. The x is published onto the
    /// self row's item in name-cell template space - the one space both rows share exactly - and
    /// re-measured on LayoutUpdated, so column resizes and alignment changes move both halves
    /// together. Draws outside its own bounds deliberately; nothing in the row clips it.
    /// </summary>
    public sealed class CategorySelfDropStub : FrameworkElement
    {
        private double _lastAnchor = double.NaN;
        private Point _lastTop;
        private double _lastBottom = double.NaN;

        static CategorySelfDropStub()
        {
            // Decoration over an already-hit-testable row: clicks belong to the row.
            IsHitTestVisibleProperty.OverrideMetadata(
                typeof(CategorySelfDropStub),
                new UIPropertyMetadata(false));
        }

        public CategorySelfDropStub()
        {
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public static readonly DependencyProperty ShapeProperty =
            DependencyProperty.Register(
                nameof(Shape),
                typeof(CategoryTreeShape),
                typeof(CategorySelfDropStub),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public CategoryTreeShape Shape
        {
            get => (CategoryTreeShape)GetValue(ShapeProperty);
            set => SetValue(ShapeProperty, value);
        }

        public static readonly DependencyProperty LineBrushProperty =
            DependencyProperty.Register(
                nameof(LineBrush),
                typeof(Brush),
                typeof(CategorySelfDropStub),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush LineBrush
        {
            get => (Brush)GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        /// <summary>The name TextBlock whose bottom line the drop hangs from.</summary>
        public static readonly DependencyProperty TargetTextProperty =
            DependencyProperty.Register(
                nameof(TargetText),
                typeof(TextBlock),
                typeof(CategorySelfDropStub),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public TextBlock TargetText
        {
            get => (TextBlock)GetValue(TargetTextProperty);
            set => SetValue(TargetTextProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(0d, 0d);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var lineBrush = LineBrush;
            if (lineBrush == null || !TryMeasureDrop(out _, out var top, out var bottom))
            {
                return;
            }

            var guidelines = new GuidelineSet();
            guidelines.GuidelinesX.Add(top.X + 0.5d);
            guidelines.Freeze();
            drawingContext.PushGuidelineSet(guidelines);
            try
            {
                var pen = CategoryTreeGuide.CreateGuidePen(lineBrush, 1d, CategoryTreeGuide.TwigDashStyle);
                drawingContext.DrawLine(pen, top, new Point(top.X, bottom));
            }
            finally
            {
                drawingContext.Pop();
            }
        }

        /// <summary>
        /// Everything the drop needs, measured fresh: the anchor x in name-cell template space
        /// (published to the self row), and the drop's top point and bottom y in this element's own
        /// space (drawn here). False when the row is not a stamped drop source or layout is not
        /// ready. Never throws - an exception escaping OnRender takes the application down.
        /// </summary>
        private bool TryMeasureDrop(out double anchorInPanel, out Point topInSelf, out double bottomYInSelf)
        {
            anchorInPanel = double.NaN;
            topInSelf = default;
            bottomYInSelf = double.NaN;

            var shape = Shape;
            var text = TargetText;
            if (shape == null || !shape.HasSelfRowBelow || text == null || !text.IsVisible)
            {
                return false;
            }

            var panel = VisualTreeHelpers.FindVisualParent<DockPanel>(this);
            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(this);
            if (panel == null || row == null ||
                !panel.IsAncestorOf(text) || !row.IsAncestorOf(this))
            {
                return false;
            }

            try
            {
                // The bottom line's own extent, not the block's: a wrapped title anchors under its
                // final line, and the line's left edge is measured rather than assumed at zero -
                // centre and right text alignments start the line wherever layout put it.
                var end = text.ContentEnd;
                var lastCharacter = end.GetCharacterRect(LogicalDirection.Backward);
                if (lastCharacter.IsEmpty)
                {
                    return false;
                }

                var lineLeft = lastCharacter.Left;
                var lineStart = end.GetLineStartPosition(0);
                if (lineStart != null)
                {
                    var firstCharacter = lineStart.GetCharacterRect(LogicalDirection.Forward);
                    if (!firstCharacter.IsEmpty)
                    {
                        lineLeft = Math.Min(firstCharacter.Left, lastCharacter.Left);
                    }
                }

                if (lastCharacter.Right <= lineLeft)
                {
                    return false;
                }

                var lineAnchor = new Point((lineLeft + lastCharacter.Right) / 2d, lastCharacter.Bottom);

                anchorInPanel = text.TransformToAncestor(panel).Transform(lineAnchor).X;

                var anchorInRow = text.TransformToAncestor(row).Transform(lineAnchor);
                var selfOriginInRow = TransformToAncestor(row).Transform(new Point(0d, 0d));
                topInSelf = new Point(
                    anchorInRow.X - selfOriginInRow.X,
                    anchorInRow.Y - selfOriginInRow.Y + 2d);
                bottomYInSelf = row.ActualHeight - selfOriginInRow.Y;
            }
            catch (InvalidOperationException)
            {
                // A transform target detached mid-layout (row recycling); skip this pass.
                return false;
            }

            return bottomYInSelf > topInSelf.Y;
        }

        // LayoutUpdated is the one signal that fires for everything that can move the text under
        // us - column resizes, alignment changes, row height changes - none of which re-arrange
        // this zero-size element itself. The handler is a few matrix transforms and bails when
        // nothing moved.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LayoutUpdated += OnLayoutUpdated;
            OnLayoutUpdated(this, EventArgs.Empty);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            LayoutUpdated -= OnLayoutUpdated;
        }

        private void OnLayoutUpdated(object sender, EventArgs e)
        {
            if (!TryMeasureDrop(out var anchor, out var top, out var bottom))
            {
                if (!double.IsNaN(_lastBottom))
                {
                    _lastAnchor = double.NaN;
                    _lastTop = default;
                    _lastBottom = double.NaN;
                    InvalidateVisual();
                }

                return;
            }

            var moved =
                double.IsNaN(_lastBottom) ||
                Math.Abs(anchor - _lastAnchor) > 0.5d ||
                Math.Abs(top.X - _lastTop.X) > 0.5d ||
                Math.Abs(top.Y - _lastTop.Y) > 0.5d ||
                Math.Abs(bottom - _lastBottom) > 0.5d;
            if (!moved)
            {
                return;
            }

            _lastAnchor = anchor;
            _lastTop = top;
            _lastBottom = bottom;

            // Hand the self row beneath the anchor so its half of the drop starts at the same x.
            // Name-cell template space works as the shared space because both rows instantiate the
            // same cell template in the same column.
            if (DataContext is CategorySummaryItem category && category.SelfRow != null)
            {
                category.SelfRow.SelfDropAnchorX = anchor;
            }

            InvalidateVisual();
        }
    }
}
