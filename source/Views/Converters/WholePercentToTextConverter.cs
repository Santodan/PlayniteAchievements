using System;
using System.Globalization;
using System.Windows.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Formats a 0-100 percent value as a whole percent using the plugin formatting culture
    /// (e.g. "57%" or "57 %"). Unlike RarityPercentToTextConverter, the value does not honor
    /// the RoundRarityPercentages setting; progress percents always render as whole percents.
    /// Raw doubles strictly between 99 and 100 render as 99% so only true completion reads 100%.
    /// </summary>
    public class WholePercentToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percent)
            {
                return PercentFormatter.FormatWhole(AchievementCompletionPercentCalculator.RoundPercentForDisplay(percent));
            }

            if (value is int intPercent)
            {
                return PercentFormatter.FormatWhole(intPercent);
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
