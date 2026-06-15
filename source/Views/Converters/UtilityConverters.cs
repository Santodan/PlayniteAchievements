using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, culture, out var parsed))
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

        private static int ParseMinimum(object parameter, CultureInfo culture)
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

            if (int.TryParse(parameterText, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var invariantValue))
            {
                return invariantValue;
            }

            return int.TryParse(parameterText, NumberStyles.Integer | NumberStyles.AllowThousands, culture, out var cultureValue)
                ? cultureValue
                : 0;
        }
    }

    /// <summary>
    /// Sizes image cells from the available column width. Platform badges keep row-aware uniform sizing.
    /// ConverterParameter: mode,dimension,horizontalPadding,verticalPadding,fallbackSize.
    /// mode is icon, cover, platform, or auto. dimension is width or height.
    /// </summary>
    public class ImageFitDimensionConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var parts = (parameter?.ToString() ?? "icon,width").Split(',');
            var mode = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : "icon";
            var dimension = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "width";
            var useCover = mode == "cover" ||
                (mode == "auto" && values != null && values.Length > 2 && values[2] is bool boolValue && boolValue);
            var aspectRatio = useCover ? 2d / 3d : 1d;
            var horizontalPadding = ParseDouble(parts, 2, 8d);
            var verticalPadding = ParseDouble(parts, 3, 8d);
            var fallbackSize = ParseDouble(parts, 4, 12d);

            var cellWidth = GetFiniteDouble(values, 1);
            var rowHeight = GetFiniteDouble(values, 0);
            var availableWidth = cellWidth.HasValue
                ? Math.Max(1d, cellWidth.Value - horizontalPadding)
                : fallbackSize;
            var availableHeight = rowHeight.HasValue
                ? Math.Max(1d, rowHeight.Value - verticalPadding)
                : fallbackSize / aspectRatio;
            var fitWidth = mode == "icon" || mode == "cover" || mode == "auto";
            var height = fitWidth
                ? availableWidth / aspectRatio
                : Math.Min(availableHeight, availableWidth / aspectRatio);
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            {
                height = fallbackSize / aspectRatio;
            }

            return dimension == "height" ? height : height * aspectRatio;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private static double? GetFiniteDouble(object[] values, int index)
        {
            if (values == null || values.Length <= index || !(values[index] is double value))
            {
                return null;
            }

            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0
                ? (double?)null
                : value;
        }

        private static double ParseDouble(string[] parts, int index, double fallback)
        {
            if (parts == null ||
                parts.Length <= index ||
                !double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return fallback;
            }

            return Math.Max(0d, value);
        }
    }

    public class DataGridCompletedBorderThicknessConverter : IMultiValueConverter
    {
        private static readonly Thickness MiddleColumnThickness = new Thickness(0, 2, 0, 2);

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var cell = values != null && values.Length > 0 ? values[0] as DataGridCell : null;
            var grid = values != null
                ? values.OfType<DataGrid>().FirstOrDefault()
                : null;
            var column = cell?.Column;
            if (column == null || grid?.Columns == null)
            {
                return MiddleColumnThickness;
            }

            var visibleColumns = grid.Columns
                .Where(item => item?.Visibility == Visibility.Visible)
                .OrderBy(item => item.DisplayIndex)
                .ToList();
            if (visibleColumns.Count == 0)
            {
                return MiddleColumnThickness;
            }

            var left = ReferenceEquals(column, visibleColumns[0]) ? 2d : 0d;
            var right = ReferenceEquals(column, visibleColumns[visibleColumns.Count - 1]) ? 2d : 0d;
            return new Thickness(left, 2, right, 2);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

}
