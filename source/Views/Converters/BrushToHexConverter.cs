using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>A solid brush as "#RRGGBB"; empty for anything else.</summary>
    public class BrushToHexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is SolidColorBrush brush
                ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", brush.Color.R, brush.Color.G, brush.Color.B)
                : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
