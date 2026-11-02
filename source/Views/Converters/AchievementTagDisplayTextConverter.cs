using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Summary text of the Tags selector: Text when the tags show as plain names, otherwise the
    /// chosen parts, e.g. "Icons, Colors".
    /// </summary>
    public class AchievementTagDisplayTextConverter : IValueConverter
    {
        internal const string IconsKey = "LOCPlayAch_ManageAchievements_Tab_CustomIcons";
        internal const string ColorsKey = "LOCPlayAch_Settings_Display_Colors";
        private const string TextKey = "LOCPlayAch_Showcase_Widget_Text";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var display = value is AchievementTagDisplay flags ? flags : AchievementTagDisplay.None;
            var parts = new List<string>();
            if ((display & AchievementTagDisplay.Icons) != 0)
            {
                parts.Add(ResourceProvider.GetString(IconsKey));
            }

            if ((display & AchievementTagDisplay.Colors) != 0)
            {
                parts.Add(ResourceProvider.GetString(ColorsKey));
            }

            return parts.Count == 0 ? ResourceProvider.GetString(TextKey) : string.Join(", ", parts);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
