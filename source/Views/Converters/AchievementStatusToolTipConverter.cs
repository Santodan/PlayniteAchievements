using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Builds the status column tooltip from the same row flags the status glyphs switch on,
    /// e.g. "Locked (Missable)" or "Unlocked (Capstone)".
    /// </summary>
    public class AchievementStatusToolTipConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // values[0] = Unlocked, [1] = IsCapstone, [2] = IsGoal, [3] = IsFiltered,
            // [4] = IsFilteredFromSummaries, [5] = IsMissable
            if (values == null || values.Length < 6)
            {
                return DependencyProperty.UnsetValue;
            }

            if (Flag(values[3]))
            {
                return ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Status_Excluded");
            }

            if (Flag(values[4]))
            {
                return ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Status_ExcludedFromSummaries");
            }

            var unlocked = Flag(values[0]);
            var qualifiers = new List<string>();
            if (!unlocked && Flag(values[2]))
            {
                qualifiers.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Editor_Goal"));
            }
            else if (Flag(values[1]))
            {
                qualifiers.Add(ResourceProvider.GetString("LOCPlayAch_Dynamic_Capstone"));
            }

            if (!unlocked && Flag(values[5]))
            {
                qualifiers.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Category_Type_Missable"));
            }

            var state = ResourceProvider.GetString(unlocked ? "LOCPlayAch_Common_Unlocked" : "LOCPlayAch_Common_Locked");
            return qualifiers.Count == 0
                ? state
                : state + " (" + string.Join(", ", qualifiers) + ")";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static bool Flag(object value) => value is bool b && b;
    }
}
