using System;
using System.Globalization;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Performs mathematical operations on binding values.
    /// Used for calculating widths/heights by adding/subtracting values.
    /// ConverterParameter: "+" for addition, "-" for subtraction
    /// </summary>
    public class ValueOperationConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                values[0] is double first &&
                values[1] is double second)
            {
                string operation = parameter?.ToString() ?? "-";

                if (operation == "+")
                    return first + second;
                else if (operation == "-")
                    return first - second;
                else if (operation == "*")
                    return first * second;
                else if (operation == "/" && second != 0)
                    return first / second;
            }

            return values[0];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
