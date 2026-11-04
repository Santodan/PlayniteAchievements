using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// A horizontal row in which one child, marked with <see cref="FillProperty"/>, is measured
    /// with whatever width the others leave and then takes only the width it asks for, so the
    /// children after it follow it directly rather than being pushed to the far edge. Used by the
    /// headers that show a game name, a category path, and a count beside it.
    /// </summary>
    public sealed class FlexibleRowPanel : Panel
    {
        public static readonly DependencyProperty FillProperty =
            DependencyProperty.RegisterAttached(
                "Fill",
                typeof(bool),
                typeof(FlexibleRowPanel),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static bool GetFill(DependencyObject element) => (bool)element.GetValue(FillProperty);

        public static void SetFill(DependencyObject element, bool value) => element.SetValue(FillProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            var infinite = new Size(double.PositiveInfinity, availableSize.Height);
            var fixedWidth = 0d;
            var height = 0d;
            UIElement fill = null;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                {
                    continue;
                }

                if (fill == null && GetFill(child))
                {
                    fill = child;
                    continue;
                }

                child.Measure(infinite);
                fixedWidth += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }

            var fillWidth = 0d;
            if (fill != null)
            {
                var constraint = double.IsInfinity(availableSize.Width)
                    ? double.PositiveInfinity
                    : Math.Max(0, availableSize.Width - fixedWidth);
                fill.Measure(new Size(constraint, availableSize.Height));
                fillWidth = fill.DesiredSize.Width;
                height = Math.Max(height, fill.DesiredSize.Height);
            }

            return new Size(fixedWidth + fillWidth, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var x = 0d;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                {
                    continue;
                }

                var width = child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }
    }
}
