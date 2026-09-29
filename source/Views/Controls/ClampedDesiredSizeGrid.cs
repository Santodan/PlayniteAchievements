using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// A <see cref="Grid"/> that never asks for more than the space it is given. A grid's
    /// desired size is the sum of its tracks' minimum sizes, and each track's minimum follows
    /// its largest child, so one oversized child makes the whole grid request more than its
    /// slot; WPF then arranges it smaller than requested and clips it to its bounds. Clamping
    /// the request keeps the grid unclipped, so children that deliberately hang past its edges
    /// (grippers, rulers) stay visible whatever sits in its cells.
    /// </summary>
    public class ClampedDesiredSizeGrid : Grid
    {
        protected override Size MeasureOverride(Size constraint)
        {
            var desired = base.MeasureOverride(constraint);
            return new Size(
                double.IsInfinity(constraint.Width) ? desired.Width : Math.Min(desired.Width, constraint.Width),
                double.IsInfinity(constraint.Height) ? desired.Height : Math.Min(desired.Height, constraint.Height));
        }
    }
}
