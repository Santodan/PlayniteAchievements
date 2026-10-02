using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The Pie widget body: the first child (the pie) gets a square sized to the height left over
    /// beside the second child (the legend), and the pie + legend pair is centered horizontally so
    /// the space left of the pie matches the space right of the legend. A collapsed legend takes
    /// no width, which leaves the pie centered on its own.
    /// </summary>
    public sealed class ShowcasePieLegendPanel : Panel
    {
        private UIElement Pie => InternalChildren.Count > 0 ? InternalChildren[0] : null;

        private UIElement Legend => InternalChildren.Count > 1 ? InternalChildren[1] : null;

        protected override Size MeasureOverride(Size availableSize)
        {
            var legend = Legend;
            legend?.Measure(availableSize);
            var legendSize = legend?.DesiredSize ?? new Size();

            var side = ResolvePieSide(availableSize, legendSize.Width);
            Pie?.Measure(new Size(side, side));

            return new Size(
                double.IsInfinity(availableSize.Width) ? side + legendSize.Width : availableSize.Width,
                Math.Max(side, legendSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var legendWidth = Legend?.DesiredSize.Width ?? 0;
            var side = ResolvePieSide(finalSize, legendWidth);
            var left = Math.Max(0, (finalSize.Width - side - legendWidth) / 2.0);

            Pie?.Arrange(new Rect(left, (finalSize.Height - side) / 2.0, side, side));
            Legend?.Arrange(new Rect(left + side, 0, legendWidth, finalSize.Height));
            return finalSize;
        }

        private static double ResolvePieSide(Size size, double legendWidth)
        {
            var width = double.IsInfinity(size.Width) ? double.PositiveInfinity : size.Width - legendWidth;
            var side = Math.Min(width, size.Height);
            return double.IsInfinity(side) || double.IsNaN(side) ? 0 : Math.Max(0, side);
        }
    }
}
