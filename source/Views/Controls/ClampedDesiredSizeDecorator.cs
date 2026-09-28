using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Passes the available size through to its child but never reports a desired size larger
    /// than that available size. WPF clips any element arranged smaller than it asked to be, so
    /// a child that measures past its slot (an <c>Image</c> with <c>UniformToFill</c>, a wrap
    /// panel behind a scroll viewer with horizontal scrolling disabled) would otherwise inflate
    /// every ancestor's desired size and switch on the layout clip of the grid that hosts it,
    /// cutting off anything that grid draws outside its bounds.
    /// </summary>
    public sealed class ClampedDesiredSizeDecorator : Decorator
    {
        protected override Size MeasureOverride(Size constraint)
        {
            var child = Child;
            if (child == null)
            {
                return new Size(0, 0);
            }

            child.Measure(constraint);
            var desired = child.DesiredSize;
            return new Size(
                double.IsInfinity(constraint.Width) ? desired.Width : Math.Min(desired.Width, constraint.Width),
                double.IsInfinity(constraint.Height) ? desired.Height : Math.Min(desired.Height, constraint.Height));
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Child?.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }
}
