using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Attaches to a <see cref="TextBox"/> to edit a 0-1 value with culture-aware parsing and
    /// display (using <see cref="FormattingCulture"/>, so the decimal separator matches the
    /// user's language). The text is left alone while typing and only coerced when the box loses
    /// focus: it is parsed, clamped to [0,1], written back to <see cref="ValueProperty"/> (a
    /// two-way binding to the setting), and re-displayed in normalized form. Unparseable input
    /// reverts to the last good value.
    /// </summary>
    public static class UnitIntervalInput
    {
        private const string DisplayFormat = "0.##";

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.RegisterAttached(
                "Value", typeof(double), typeof(UnitIntervalInput),
                new FrameworkPropertyMetadata(
                    0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnValueChanged));

        public static void SetValue(DependencyObject element, double value) =>
            element.SetValue(ValueProperty, value);

        public static double GetValue(DependencyObject element) =>
            (double)element.GetValue(ValueProperty);

        // Marks a TextBox as wired so events are hooked only once.
        private static readonly DependencyProperty HookedProperty =
            DependencyProperty.RegisterAttached(
                "Hooked", typeof(bool), typeof(UnitIntervalInput),
                new PropertyMetadata(false));

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBox textBox))
            {
                return;
            }

            if (!(bool)textBox.GetValue(HookedProperty))
            {
                textBox.SetValue(HookedProperty, true);
                textBox.LostFocus += OnLostFocus;
            }

            // Reflect source-driven changes, but never fight the user mid-edit.
            if (!textBox.IsKeyboardFocused)
            {
                textBox.Text = Format((double)e.NewValue);
            }
        }

        private static void OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (!(sender is TextBox textBox))
            {
                return;
            }

            var culture = FormattingCulture.Current;
            if (double.TryParse(textBox.Text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var parsed) ||
                double.TryParse(textBox.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
            {
                var clamped = parsed < 0.0 ? 0.0 : (parsed > 1.0 ? 1.0 : parsed);
                SetValue(textBox, clamped);
                // Read back through the (clamped) source and normalize the displayed text.
                textBox.Text = Format(GetValue(textBox));
            }
            else
            {
                // Unparseable: discard the edit and restore the current value.
                textBox.Text = Format(GetValue(textBox));
            }
        }

        private static string Format(double value) =>
            value.ToString(DisplayFormat, FormattingCulture.Current);
    }
}
