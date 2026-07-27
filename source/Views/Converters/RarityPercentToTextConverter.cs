using System;
using System.Globalization;
using System.Windows.Data;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Formats a rarity percent value for compact whole-percent surfaces (e.g. the rarity bar
    /// overlay) through AchievementRarityResolver so the display honors the global
    /// RoundRarityPercentages setting. Display-only; the bound value is unchanged.
    /// </summary>
    public class RarityPercentToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percent)
            {
                return AchievementRarityResolver.FormatWholePercent(percent);
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
