using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The page-wide cell lattice shown while editing, as one element: a hairline on every
    /// internal row and column boundary of the grid it spans, read from the grid's definitions
    /// so track resizes only redraw it. Never hit-testable.
    /// </summary>
    public sealed class ShowcaseLatticeLayer : FrameworkElement
    {
        private const double LineOpacity = 0.5;

        public ShowcaseLatticeLayer()
        {
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var grid = VisualTreeHelper.GetParent(this) as Grid;
            // Accent rather than the border brush: PlayAch.Brush.Border follows the theme's
            // NormalBorderBrush, which in several themes is too low-contrast to see as a hairline.
            if (grid == null || !(TryFindResource("PlayAch.Brush.Accent") is Brush brush))
            {
                return;
            }

            var width = RenderSize.Width;
            var height = RenderSize.Height;
            drawingContext.PushOpacity(LineOpacity);
            for (var index = 1; index < grid.ColumnDefinitions.Count; index++)
            {
                var x = grid.ColumnDefinitions[index].Offset;
                DrawLine(drawingContext, brush, new Rect(x - 0.5, 0, 1, height));
            }

            for (var index = 1; index < grid.RowDefinitions.Count; index++)
            {
                var y = grid.RowDefinitions[index].Offset;
                DrawLine(drawingContext, brush, new Rect(0, y - 0.5, width, 1));
            }

            drawingContext.Pop();
        }

        // Snapped to device pixels so a hairline stays one pixel wide at any track offset.
        private static void DrawLine(DrawingContext drawingContext, Brush brush, Rect rect)
        {
            var guidelines = new GuidelineSet();
            guidelines.GuidelinesX.Add(rect.Left);
            guidelines.GuidelinesX.Add(rect.Right);
            guidelines.GuidelinesY.Add(rect.Top);
            guidelines.GuidelinesY.Add(rect.Bottom);
            drawingContext.PushGuidelineSet(guidelines);
            drawingContext.DrawRectangle(brush, null, rect);
            drawingContext.Pop();
        }
    }
}
