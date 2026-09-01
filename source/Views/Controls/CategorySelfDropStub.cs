using System;
using System.Windows;
using System.Windows.Controls;
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
    /// Lives inside the grid that hugs the name text, bottom-centered with zero desired size, so
    /// layout - the only thing that knows where the text landed - is what centers the drop under
    /// the text's last line. It publishes that x (in row coordinates) onto the self row's item so
    /// the two halves meet at the row boundary. Draws below its own bounds deliberately; nothing
    /// in the row clips it.
    /// </summary>
    public sealed class CategorySelfDropStub : FrameworkElement
    {
        private DataGridRow _observedRow;

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

        protected override Size MeasureOverride(Size availableSize)
        {
            // Zero size: the name text alone decides the layout; the drop is drawn past the bounds.
            return new Size(0d, 0d);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var shape = Shape;
            var lineBrush = LineBrush;
            if (shape == null || !shape.HasSelfRowBelow || lineBrush == null)
            {
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(this);
            if (row == null || !row.IsAncestorOf(this))
            {
                return;
            }

            var originInRow = TransformToAncestor(row).Transform(new Point(0d, 0d));
            var length = row.ActualHeight - originInRow.Y;
            if (length <= 0d)
            {
                return;
            }

            // Hand the self row beneath the anchor so its half of the drop starts at the same x.
            // Row coordinates work as the shared space because both rows lay their columns out
            // identically.
            if (DataContext is CategorySummaryItem category && category.SelfRow != null)
            {
                category.SelfRow.SelfDropAnchorX = originInRow.X;
            }

            var guidelines = new GuidelineSet();
            guidelines.GuidelinesX.Add(0.5d);
            guidelines.Freeze();
            drawingContext.PushGuidelineSet(guidelines);
            try
            {
                var pen = CategoryTreeGuide.CreateGuidePen(lineBrush, 1d, CategoryTreeGuide.TwigDashStyle);
                drawingContext.DrawLine(pen, new Point(0d, 0d), new Point(0d, length));
            }
            finally
            {
                drawingContext.Pop();
            }
        }

        // The drop's length follows the row's height, which this element's own zero-size layout
        // never sees change - so it watches the row directly, and lets go on Unloaded rather than
        // holding the recycled row alive.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _observedRow = VisualTreeHelpers.FindVisualParent<DataGridRow>(this);
            if (_observedRow != null)
            {
                _observedRow.SizeChanged += OnRowSizeChanged;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_observedRow != null)
            {
                _observedRow.SizeChanged -= OnRowSizeChanged;
                _observedRow = null;
            }
        }

        private void OnRowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            InvalidateVisual();
        }
    }
}
