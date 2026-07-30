using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Converts a provider icon key and color hex to a colored DrawingImage.
    /// Takes IconKey as the binding value and ColorHex as the converter parameter.
    /// </summary>
    public class ProviderIconConverter : IMultiValueConverter
    {
        // Cache of colored provider icon images keyed by geometry resource key plus color hex
        // (the rendered image depends on both). Geo* geometry resources are defined statically
        // and are not rewritten at runtime. Only successful resolutions are cached so
        // late-loading resource dictionaries can still be found. Converters run on the UI
        // thread only, so an unlocked Dictionary is acceptable.
        private static readonly Dictionary<string, DrawingImage> IconImageCache = new Dictionary<string, DrawingImage>();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                values[0] is string iconKey &&
                values[1] is string colorHex)
            {
                return BuildIcon(iconKey, colorHex);
            }

            return null;
        }

        /// <summary>
        /// Builds the colored provider (platform) icon as a frozen <see cref="DrawingImage"/> from
        /// an icon key (e.g. "ProviderIconSteam") and a color hex, or null when either is blank or
        /// the geometry is not found. Shared by the multi-binding converter and the view model's
        /// <c>ProviderIcon</c> binding so theme/custom templates can bind <c>Image.Source</c>
        /// directly without the converter. Results are cached by geometry key + color.
        /// </summary>
        public static DrawingImage BuildIcon(string iconKey, string colorHex)
        {
            if (string.IsNullOrEmpty(iconKey) || string.IsNullOrEmpty(colorHex))
            {
                return null;
            }

            try
            {
                // Try to find a "Geo" + iconName resource (e.g., GeoSteam for ProviderIconSteam)
                string geoKey = "Geo" + iconKey.Replace("ProviderIcon", "");
                string cacheKey = geoKey + "|" + colorHex;

                if (IconImageCache.TryGetValue(cacheKey, out var cachedImage))
                {
                    return cachedImage;
                }

                var geometry = Application.Current.TryFindResource(geoKey) as Geometry;
                if (geometry != null && ColorConverter.ConvertFromString(colorHex) is Color color)
                {
                    var drawingImage = new DrawingImage
                    {
                        Drawing = new GeometryDrawing
                        {
                            Geometry = geometry,
                            Brush = new SolidColorBrush(color)
                        }
                    };
                    drawingImage.Freeze();
                    IconImageCache[cacheKey] = drawingImage;
                    return drawingImage;
                }
            }
            catch
            {
                // Fall through to null
            }

            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
