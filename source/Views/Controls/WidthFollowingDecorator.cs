using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Reports no desired width of its own, so its child fills whatever width its siblings
    /// settle the container at instead of widening it. Height is measured normally.
    /// </summary>
    /// <remarks>
    /// The child is measured at the width it was last arranged at, not an unbounded one. WPF
    /// clips an element arranged narrower than it measured rather than re-fitting it, so a
    /// Uniform image measured at its natural width would be cut off instead of scaled down.
    /// Measuring at the arranged width keeps the two in step, and remembering that width keeps
    /// a later measure pass from reverting it.
    /// </remarks>
    public sealed class WidthFollowingDecorator : Decorator
    {
        private double _arrangedWidth = double.NaN;

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child == null)
            {
                return new Size(0, 0);
            }

            var width = double.IsNaN(_arrangedWidth) ? constraint.Width : _arrangedWidth;
            Child.Measure(new Size(width, constraint.Height));
            return new Size(0, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (Child != null)
            {
                if (!arrangeSize.Width.Equals(_arrangedWidth))
                {
                    _arrangedWidth = arrangeSize.Width;
                    Child.Measure(new Size(arrangeSize.Width, arrangeSize.Height));
                }

                Child.Arrange(new Rect(arrangeSize));
            }

            return arrangeSize;
        }
    }
}
