namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Formats percent values (0-100 space) for display using the plugin formatting culture.
    /// Cultures that separate the value and percent sign (e.g. German "100 %") get that
    /// spacing automatically from the culture's percent pattern.
    /// </summary>
    public static class PercentFormatter
    {
        /// <summary>
        /// Formats a 0-100 percent value with the given number of decimal places.
        /// </summary>
        public static string Format(double percentValue, int decimals)
        {
            if (double.IsNaN(percentValue) || double.IsInfinity(percentValue))
            {
                return string.Empty;
            }

            return (percentValue / 100d).ToString("P" + decimals, FormattingCulture.Current);
        }

        /// <summary>
        /// Formats a 0-100 percent value as a whole percent.
        /// </summary>
        public static string FormatWhole(double percentValue)
        {
            return Format(percentValue, 0);
        }

        /// <summary>
        /// Formats "less than" a whole percent threshold, e.g. "&lt;1%" (en) or "&lt; 1 %" (de).
        /// The space after "&lt;" mirrors whether the culture separates the value and percent sign,
        /// checking both the regular space and the no-break space some cultures use before "%".
        /// </summary>
        public static string FormatLessThanWhole(double thresholdPercent)
        {
            var formatted = FormatWhole(thresholdPercent);
            var spaced = formatted.IndexOf(' ') >= 0 || formatted.IndexOf('\u00A0') >= 0;
            return spaced ? "< " + formatted : "<" + formatted;
        }
    }
}
