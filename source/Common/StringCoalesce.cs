namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Picks the first non-blank (not null, empty or whitespace) value from a list of candidates.
    /// </summary>
    internal static class StringCoalesce
    {
        /// <summary>
        /// Returns the first non-blank value unchanged, or null when every value is blank.
        /// </summary>
        public static string FirstNonBlank(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the first non-blank value with surrounding whitespace trimmed, or null when every
        /// value is blank.
        /// </summary>
        public static string FirstNonBlankTrimmed(params string[] values)
        {
            return FirstNonBlank(values)?.Trim();
        }
    }
}
