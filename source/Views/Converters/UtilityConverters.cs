using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Converts control height to font size (approximately 1/3 of height).
    /// Used for dynamically sizing text based on parent control height.
    /// </summary>
    public class HeightToFontSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double height && height > 0)
            {
                return height * 0.3;
            }
            return 12.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

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

    /// <summary>
    /// Converts a resource key string to the actual resource (DrawingImage) from application resources.
    /// Used for dynamically loading icons based on data binding.
    /// </summary>
    public class ResourceKeyToImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string resourceKey && !string.IsNullOrEmpty(resourceKey))
            {
                try
                {
                    return Application.Current.FindResource(resourceKey);
                }
                catch
                {
                    // Resource not found, return null
                    return null;
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a provider icon key and color hex to a colored DrawingImage.
    /// Takes IconKey as the binding value and ColorHex as the converter parameter.
    /// </summary>
    public class ProviderIconConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                values[0] is string iconKey &&
                values[1] is string colorHex &&
                !string.IsNullOrEmpty(iconKey) &&
                !string.IsNullOrEmpty(colorHex))
            {
                try
                {
                    // Try to find a "Geo" + iconName resource (e.g., GeoSteam for ProviderIconSteam)
                    string geoKey = "Geo" + iconKey.Replace("ProviderIcon", "");

                    var geometry = Application.Current.TryFindResource(geoKey) as Geometry;
                    if (geometry != null)
                    {
                        // Parse the color
                        if (ColorConverter.ConvertFromString(colorHex) is Color color)
                        {
                            // Create a new DrawingImage with the color applied
                            var drawingImage = new DrawingImage();
                            drawingImage.Drawing = new GeometryDrawing
                            {
                                Geometry = geometry,
                                Brush = new SolidColorBrush(color)
                            };
                            drawingImage.Freeze();
                            return drawingImage;
                        }
                    }
                }
                catch
                {
                    // Fall through to null
                }
            }
            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Provides fallback to theme-specified default images when games don't have cover/logo/icon metadata.
    /// Returns the path string when available (for async loading) or the theme default ImageSource when null/empty.
    /// ConverterParameter: "icon" for DefaultGameIcon, "cover" for DefaultGameCover
    /// </summary>
    public class DefaultGameImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // If value is a non-empty string, return it (AsyncImage will handle async loading)
            if (value is string str && !string.IsNullOrEmpty(str))
            {
                return value;
            }

            // If null/empty, return the appropriate default resource from the theme
            string imageType = parameter?.ToString();
            string resourceKey = imageType == "cover" ? "DefaultGameCover" : "DefaultGameIcon";

            try
            {
                var defaultImage = Application.Current.TryFindResource(resourceKey);
                if (defaultImage != null)
                {
                    return defaultImage;
                }
            }
            catch
            {
                // Resource not found, fall through to null
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
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
            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var parsed))
            {
                var minimum = ParseMinimum(parameter, culture);
                if (minimum > 0 && parsed > 0 && parsed < minimum)
                {
                    return minimum;
                }

                return parsed;
            }

            return Binding.DoNothing;
        }

        private static double ParseMinimum(object parameter, CultureInfo culture)
        {
            if (parameter == null)
            {
                return 0;
            }

            var parameterText = parameter.ToString();
            if (string.IsNullOrWhiteSpace(parameterText))
            {
                return 0;
            }

            if (double.TryParse(parameterText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var invariantValue))
            {
                return invariantValue;
            }

            return double.TryParse(parameterText, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var cultureValue)
                ? cultureValue
                : 0;
        }
    }

    /// <summary>
    /// Converts pie chart tooltip data to display string.
    /// For locked slices: shows just the count.
    /// For completed-games slices: shows just the count.
    /// For all other slices: shows "unlocked / total" format.
    /// </summary>
    public class PieTooltipConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 3 &&
                values[0] is int unlockedCount &&
                values[1] is int totalCount &&
                values[2] is bool isLocked)
            {
                var iconKey = values.Length > 3 ? values[3] as string : null;
                var isCompletedGamesSlice = string.Equals(iconKey, "BadgeCompletedGame", StringComparison.Ordinal);

                if (isLocked || isCompletedGamesSlice)
                {
                    return unlockedCount.ToString();
                }

                return $"{unlockedCount} / {totalCount}";
            }
            return string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
