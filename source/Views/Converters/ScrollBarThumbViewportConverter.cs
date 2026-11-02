using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// The viewport size a scroll bar's Track is given so its thumb is never shorter than the
    /// parameter's length (capped at half the track). Track's own floor comes from the system
    /// scroll bar metrics, which ignore the template and leave a thumb shorter than its rounded
    /// chrome on long content. Values: the scroll bar's ViewportSize, Minimum and Maximum, then the
    /// Track's ActualWidth, ActualHeight and Orientation.
    /// </summary>
    public sealed class ScrollBarThumbViewportConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 6 ||
                !(values[0] is double viewport) ||
                !(values[1] is double minimum) ||
                !(values[2] is double maximum) ||
                !(values[3] is double width) ||
                !(values[4] is double height) ||
                !(values[5] is Orientation orientation))
            {
                return values?.Length > 0 && values[0] is double fallback ? fallback : double.NaN;
            }

            var range = maximum - minimum;
            var trackLength = orientation == Orientation.Vertical ? height : width;
            if (double.IsNaN(viewport) || viewport <= 0 || range <= 0 || !(trackLength > 0))
            {
                return viewport;
            }

            var minThumb = Math.Min(ParseLength(parameter), trackLength / 2);
            var thumb = trackLength * viewport / (range + viewport);
            if (thumb >= minThumb)
            {
                return viewport;
            }

            // Solves trackLength * v / (range + v) = minThumb for v.
            return minThumb * range / (trackLength - minThumb);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static double ParseLength(object parameter)
        {
            if (parameter is double length)
            {
                return length;
            }

            return double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }
    }
}
