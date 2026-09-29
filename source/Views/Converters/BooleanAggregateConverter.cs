using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Defines how several boolean inputs combine into one.
    /// </summary>
    public enum BooleanAggregateMode
    {
        /// <summary>
        /// True when at least one input is true.
        /// </summary>
        Any,

        /// <summary>
        /// True only when every input is true.
        /// </summary>
        All
    }

    /// <summary>
    /// Combines several booleans into one, so a row can be enabled by more than one master switch
    /// without a computed property on the settings model. Non-boolean inputs (including
    /// <see cref="DependencyProperty.UnsetValue"/> before a binding resolves) count as false.
    /// </summary>
    public class BooleanAggregateConverter : IMultiValueConverter
    {
        /// <summary>
        /// Gets or sets how the inputs combine.
        /// </summary>
        public BooleanAggregateMode Mode { get; set; } = BooleanAggregateMode.Any;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length == 0)
            {
                return false;
            }

            return Mode == BooleanAggregateMode.All
                ? values.All(value => value is bool flag && flag)
                : values.Any(value => value is bool flag && flag);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("BooleanAggregateConverter does not support ConvertBack.");
        }
    }
}
