using System;
using System.Globalization;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Sizes square badge glyphs (platform icons, status checkmarks) to the
    /// smaller of the available column width and row height so they scale with
    /// the row without being clipped. Image cells no longer use this converter;
    /// they are bounded by their container and Stretch="Uniform" instead.
    /// Values: row ActualHeight, cell ActualWidth.
    /// ConverterParameter: mode,dimension,horizontalPadding,verticalPadding,fallbackSize.
    /// The mode and dimension tokens are kept for call-site stability; the
    /// result is a square, so width and height are identical.
    /// </summary>
    public class ImageFitDimensionConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var parts = (parameter?.ToString() ?? string.Empty).Split(',');
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
                : fallbackSize;
            var size = Math.Min(availableHeight, availableWidth);
            if (double.IsNaN(size) || double.IsInfinity(size) || size <= 0)
            {
                size = fallbackSize;
            }

            return size;
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
}
