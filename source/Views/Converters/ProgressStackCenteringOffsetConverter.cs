using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Computes the vertical translate offset that keeps the progress bar centered in its cell
    /// when the progress stack is center-aligned. The name row (above) and badge footer (below)
    /// pull the bar off the stack's midpoint by half of their occupied heights; translating the
    /// stack by half the difference puts the bar back on the cell's midline.
    /// Bindings: [0] stack VerticalAlignment, [1] name row ActualHeight, [2] name row Margin,
    /// [3] footer ActualHeight, [4] footer Margin. Collapsed rows measure as zero height and
    /// contribute nothing, margins included.
    /// </summary>
    public class ProgressStackCenteringOffsetConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 5 ||
                !(values[0] is VerticalAlignment alignment) ||
                alignment != VerticalAlignment.Center)
            {
                return 0d;
            }

            var above = OccupiedHeight(values[1], values[2]);
            var below = OccupiedHeight(values[3], values[4]);
            return Math.Round((below - above) / 2d);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("ProgressStackCenteringOffsetConverter does not support ConvertBack.");
        }

        private static double OccupiedHeight(object actualHeight, object margin)
        {
            var height = actualHeight is double d && !double.IsNaN(d) ? d : 0d;
            if (height <= 0d)
            {
                return 0d;
            }

            var thickness = margin is Thickness t ? t : default(Thickness);
            return height + thickness.Top + thickness.Bottom;
        }
    }
}
