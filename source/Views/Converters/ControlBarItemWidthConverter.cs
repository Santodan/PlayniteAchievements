using System;
using System.Globalization;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// A control bar item's width: its own (values[0]), or, while the bar fills its width
    /// (values[1] true), the bar's width (values[2]) less the item's left margin, so a lone item
    /// spans a settings row.
    /// </summary>
    public sealed class ControlBarItemWidthConverter : IMultiValueConverter
    {
        // The items' PlayAch.Thickness.Left.Xs margin.
        private const double ItemMargin = 4;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var own = values?.Length > 0 && values[0] is double width ? width : double.NaN;
            if (values?.Length > 2 && values[1] is bool fills && fills &&
                values[2] is double barWidth && barWidth > ItemMargin)
            {
                return barWidth - ItemMargin;
            }

            return own;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}