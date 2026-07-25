using System;
using System.Globalization;
using System.Windows.Data;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Formats a numeric value as "N0" using the plugin formatting culture, rendering
    /// null, zero, NaN, and infinity as an empty string. Used by grid columns where
    /// zero doubles as the "no data" sentinel and should show nothing.
    /// </summary>
    public class BlankZeroNumberConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value)
            {
                case int intValue:
                    return intValue == 0 ? string.Empty : intValue.ToString("N0", FormattingCulture.Current);
                case long longValue:
                    return longValue == 0 ? string.Empty : longValue.ToString("N0", FormattingCulture.Current);
                case double doubleValue:
                    return doubleValue == 0d || double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)
                        ? string.Empty
                        : doubleValue.ToString("N0", FormattingCulture.Current);
                default:
                    return string.Empty;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("BlankZeroNumberConverter does not support ConvertBack.");
        }
    }
}
