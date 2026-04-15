using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Selects which icon URI to display based on the locked state.
    /// Values: [0]=IsLocked (bool), [1]=IconCustom (string), [2]=Icon (string).
    /// Returns IconCustom when locked, Icon when unlocked.
    /// IconCustom may be a real locked icon path or a gray-prefixed unlocked icon fallback.
    /// </summary>
    public sealed class LockedIconSelectorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3)
            {
                return DependencyProperty.UnsetValue;
            }

            bool isLocked = values[0] is bool b && b;
            string iconCustom = values[1] as string;
            string icon = values[2] as string;

            // When locked, use IconCustom
            // When unlocked, use Icon (color)
            string selected = isLocked ? iconCustom : icon;

            if (string.IsNullOrWhiteSpace(selected))
            {
                return DependencyProperty.UnsetValue;
            }

            return selected;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
