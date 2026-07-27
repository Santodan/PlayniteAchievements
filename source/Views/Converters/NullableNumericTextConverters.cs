using System;
using System.Globalization;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Shared ConvertBack logic for the nullable numeric text converters. Blank input maps to
    /// null, unparseable input maps to Binding.DoNothing, and a ConverterParameter minimum
    /// clamps positive values from below. Numeric parsing stays type-specific via the supplied
    /// TryParse implementation (int and double accept different formats).
    /// </summary>
    internal static class NullableNumericTextHelper
    {
        internal delegate bool TryParseNumber<T>(string text, CultureInfo culture, out T value);

        internal static bool TryParseDouble(string text, CultureInfo culture, out double value)
        {
            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out value);
        }

        internal static bool TryParseInt(string text, CultureInfo culture, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, culture, out value);
        }

        internal static object ConvertBack<T>(object value, object parameter, CultureInfo culture, TryParseNumber<T> tryParse)
            where T : struct, IComparable<T>
        {
            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (tryParse(text, culture, out var parsed))
            {
                var minimum = ParseMinimum(parameter, culture, tryParse);
                if (minimum.CompareTo(default(T)) > 0 && parsed.CompareTo(default(T)) > 0 && parsed.CompareTo(minimum) < 0)
                {
                    return minimum;
                }

                return parsed;
            }

            return Binding.DoNothing;
        }

        private static T ParseMinimum<T>(object parameter, CultureInfo culture, TryParseNumber<T> tryParse)
            where T : struct
        {
            if (parameter == null)
            {
                return default(T);
            }

            var parameterText = parameter.ToString();
            if (string.IsNullOrWhiteSpace(parameterText))
            {
                return default(T);
            }

            if (tryParse(parameterText, CultureInfo.InvariantCulture, out var invariantValue))
            {
                return invariantValue;
            }

            return tryParse(parameterText, culture, out var cultureValue)
                ? cultureValue
                : default(T);
        }
    }

    /// <summary>
    /// Converts a TextBox string to nullable double, treating blank input as null.
    /// This is used for settings fields where an empty string means "unlimited".
    /// </summary>
    public class NullableDoubleTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double doubleValue)
            {
                return doubleValue.ToString(culture);
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return NullableNumericTextHelper.ConvertBack<double>(value, parameter, culture, NullableNumericTextHelper.TryParseDouble);
        }
    }

    /// <summary>
    /// Converts a TextBox string to nullable int, treating blank input as null.
    /// </summary>
    public class NullableIntTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                return intValue.ToString(culture);
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return NullableNumericTextHelper.ConvertBack<int>(value, parameter, culture, NullableNumericTextHelper.TryParseInt);
        }
    }
}
