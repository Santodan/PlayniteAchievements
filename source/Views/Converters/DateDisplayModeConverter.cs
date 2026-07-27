using System;
using System.Globalization;
using System.Windows.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Formats a local <see cref="DateTime"/> for display according to a <see cref="DateDisplayMode"/>.
    /// Inputs: [0] DateTime? (already converted to local time), [1] DateDisplayMode.
    /// With ConverterParameter "Exact", returns the exact date-and-time string for use as a
    /// tooltip, or null (no tooltip) when the mode already displays date and time.
    /// Display only; sorting binds to the raw value via SortMemberPath and is unaffected.
    /// </summary>
    public class DateDisplayModeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var exactToolTip = string.Equals(parameter as string, "Exact", StringComparison.Ordinal);

            if (values == null || values.Length < 1)
            {
                return exactToolTip ? null : (object)string.Empty;
            }

            if (!(values[0] is DateTime local) || local == DateTime.MinValue)
            {
                return exactToolTip ? null : (object)string.Empty;
            }

            var mode = values.Length > 1 && values[1] is DateDisplayMode m
                ? m
                : DateDisplayMode.DateAndTime;

            if (exactToolTip)
            {
                return mode == DateDisplayMode.DateAndTime
                    ? null
                    : local.ToString("g", CultureInfo.CurrentCulture);
            }

            // Match the historical LastPlayedText/UnlockTimeText formatting, which used the
            // parameterless ToString("g"/"d") overloads (CurrentCulture), not the WPF binding culture.
            switch (mode)
            {
                case DateDisplayMode.DateOnly:
                    return local.ToString("d", CultureInfo.CurrentCulture);
                case DateDisplayMode.Relative:
                    return RelativeDateFormatter.ToRelativeLabel(local, DateTime.Now);
                default:
                    return local.ToString("g", CultureInfo.CurrentCulture);
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
