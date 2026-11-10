using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Returns a frozen copy of a brush with its opacity multiplied by the converter parameter
    /// (a double, or a string parsed invariantly). Dims one run of text without dimming the other
    /// inlines of its TextBlock, which an Opacity on the TextBlock would.
    /// </summary>
    public sealed class BrushOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is Brush brush))
            {
                return value;
            }

            var factor = parameter is double number
                ? number
                : double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 1d;

            if (factor >= 1d)
            {
                return brush;
            }

            var copy = brush.CloneCurrentValue();
            copy.Opacity = brush.Opacity * Math.Max(0d, factor);
            copy.Freeze();
            return copy;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
