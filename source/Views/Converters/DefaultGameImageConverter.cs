using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
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
}
