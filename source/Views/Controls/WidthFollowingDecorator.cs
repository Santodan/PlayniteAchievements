using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Reports no desired width of its own, so its child fills whatever width its siblings
    /// settle the container at instead of widening it. Height is measured normally.
    /// </summary>
    public sealed class WidthFollowingDecorator : Decorator
    {
        protected override Size MeasureOverride(Size constraint)
        {
            if (Child == null)
            {
                return new Size(0, 0);
            }

            Child.Measure(constraint);
            return new Size(0, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Child?.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }
}
