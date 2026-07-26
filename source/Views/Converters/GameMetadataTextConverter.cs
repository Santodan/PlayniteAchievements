using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Joins the per-game platform, playtime, and region text into the secondary metadata line,
    /// including only the parts whose corresponding visibility flag is enabled.
    /// Expects six bound values: platformText, playtimeText, regionText, showPlatform,
    /// showPlaytime, showRegion. When targeting <see cref="Visibility"/>, returns Collapsed when
    /// the joined result is empty; otherwise returns the joined string.
    /// </summary>
    public class GameMetadataTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var platformText = GetString(values, 0);
            var playtimeText = GetString(values, 1);
            var regionText = GetString(values, 2);
            var showPlatform = GetBool(values, 3);
            var showPlaytime = GetBool(values, 4);
            var showRegion = GetBool(values, 5);

            var text = PlayniteGameMetadataFormatter.BuildOverviewMetadataText(
                platformText,
                playtimeText,
                regionText,
                showPlatform,
                showPlaytime,
                showRegion);

            if (targetType == typeof(Visibility))
            {
                return string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            return text;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private static string GetString(object[] values, int index)
        {
            return values != null && values.Length > index ? values[index] as string : null;
        }

        private static bool GetBool(object[] values, int index)
        {
            return values != null && values.Length > index && values[index] is bool value && value;
        }
    }
}
