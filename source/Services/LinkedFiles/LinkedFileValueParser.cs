using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Cell values as achievement fields: booleans by word or number, whole numbers, unlock
    /// times in the schema's format or by inspection, and icon cells as fetchable sources.
    /// </summary>
    public static class LinkedFileValueParser
    {
        /// <summary>Unix seconds up to the start of 2100; a larger integer is milliseconds.</summary>
        public const long UnixSecondsMax = 4102444800L;

        /// <summary>Unix milliseconds up to 1e14; a larger integer is a Windows file time.</summary>
        public const long UnixMillisecondsMax = 100000000000000L;

        private const DateTimeStyles LocalStyles =
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces;

        private const DateTimeStyles UtcStyles =
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces;

        /// <summary>
        /// Reads a cell as unlocked or locked. When the schema lists its own values, they are the
        /// whole unlocked set: a cell matching one, by text or by numeric value, is unlocked and
        /// any other non-blank cell is locked. Otherwise the default words decide, and a number is
        /// unlocked when it is not zero. Anything else is not a boolean.
        /// </summary>
        public static bool TryParseBoolean(string value, IReadOnlyCollection<string> trueValues, out bool parsed)
        {
            parsed = false;
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return false;
            }

            var listed = trueValues?
                .Select(candidate => (candidate ?? string.Empty).Trim().ToLowerInvariant())
                .Where(candidate => candidate.Length > 0)
                .ToList();
            if (listed != null && listed.Count > 0)
            {
                var isNumber = double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var cellNumber);
                parsed = listed.Any(candidate =>
                    string.Equals(candidate, normalized, StringComparison.Ordinal) ||
                    (isNumber &&
                     double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var candidateNumber) &&
                     candidateNumber == cellNumber));
                return true;
            }

            if (LinkedFileSchema.DefaultTrueValues.Contains(normalized))
            {
                parsed = true;
                return true;
            }

            if (LinkedFileSchema.DefaultFalseValues.Contains(normalized))
            {
                parsed = false;
                return true;
            }

            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                parsed = number != 0;
                return true;
            }

            return false;
        }

        public static bool TryParseInt(string value, out int parsed)
        {
            var text = (value ?? string.Empty).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ||
                int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out parsed))
            {
                return true;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                Math.Abs(number - Math.Round(number)) < 0.000001 &&
                number >= int.MinValue && number <= int.MaxValue)
            {
                parsed = (int)Math.Round(number);
                return true;
            }

            parsed = 0;
            return false;
        }

        /// <summary>
        /// Reads an unlock time cell. Returns false only for text that is not a time; a blank cell
        /// or a zero stamp returns true with no time.
        /// </summary>
        public static bool TryParseUnlockTimeUtc(string value, LinkedFileSchema schema, out DateTime? utc)
        {
            utc = null;
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return true;
            }

            var assumeLocal = schema?.AssumeLocalTime ?? true;
            switch (schema?.TimeFormat ?? LinkedFileTimeFormat.Auto)
            {
                case LinkedFileTimeFormat.UnixSeconds:
                    return TryParseInteger(text, out var seconds) && TryFromUnixSeconds(seconds, out utc);
                case LinkedFileTimeFormat.UnixMilliseconds:
                    return TryParseInteger(text, out var milliseconds) && TryFromUnixMilliseconds(milliseconds, out utc);
                case LinkedFileTimeFormat.WindowsFileTime:
                    return TryParseInteger(text, out var fileTime) && TryFromFileTime(fileTime, out utc);
                case LinkedFileTimeFormat.Iso8601:
                    return TryParseIsoLike(text, assumeLocal, out utc);
                case LinkedFileTimeFormat.Custom:
                    return TryParseCustom(text, schema.CustomTimeFormat, assumeLocal, out utc);
                default:
                    return TryParseAuto(text, assumeLocal, out utc);
            }
        }

        /// <summary>
        /// An icon cell as the source to fetch it from: a web address as written, or a local file
        /// as a full path, relative paths resolved against <paramref name="baseDirectory"/>. Null
        /// when it is neither. Without a base folder the cell is kept as written.
        /// </summary>
        public static string ResolveIconSource(string value, string baseDirectory)
        {
            if (baseDirectory == null ||
                value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            try
            {
                var path = Path.IsPathRooted(value)
                    ? value
                    : Path.GetFullPath(Path.Combine(baseDirectory, value));
                return File.Exists(path) ? path : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private static bool TryParseAuto(string text, bool assumeLocal, out DateTime? utc)
        {
            utc = null;
            if (TryParseInteger(text, out var stamp))
            {
                if (stamp == 0)
                {
                    return true;
                }

                if (stamp < 0)
                {
                    return false;
                }

                if (stamp <= UnixSecondsMax)
                {
                    return TryFromUnixSeconds(stamp, out utc);
                }

                if (stamp <= UnixMillisecondsMax)
                {
                    return TryFromUnixMilliseconds(stamp, out utc);
                }

                return TryFromFileTime(stamp, out utc);
            }

            if (LooksIsoLike(text))
            {
                return TryParseIsoLike(text, assumeLocal, out utc);
            }

            return TryParseText(text, assumeLocal, out utc);
        }

        private static bool LooksIsoLike(string text)
        {
            if (text.Length >= 10 && char.IsDigit(text[0]) && text[4] == '-' && text[7] == '-')
            {
                return true;
            }

            return text.IndexOf('T') > 0 && char.IsDigit(text[0]);
        }

        private static bool TryParseIsoLike(string text, bool assumeLocal, out DateTime? utc)
        {
            utc = null;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                return TryParseText(text, assumeLocal, out utc);
            }

            utc = ToUtc(parsed, assumeLocal);
            return true;
        }

        private static bool TryParseText(string text, bool assumeLocal, out DateTime? utc)
        {
            utc = null;
            var styles = assumeLocal ? LocalStyles : UtcStyles;
            if (DateTime.TryParse(text, CultureInfo.CurrentCulture, styles, out var parsed) ||
                DateTime.TryParse(text, CultureInfo.InvariantCulture, styles, out parsed))
            {
                utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return true;
            }

            return false;
        }

        private static bool TryParseCustom(string text, string format, bool assumeLocal, out DateTime? utc)
        {
            utc = null;
            if (string.IsNullOrWhiteSpace(format))
            {
                return TryParseAuto(text, assumeLocal, out utc);
            }

            var styles = assumeLocal ? LocalStyles : UtcStyles;
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, styles, out var parsed) ||
                DateTime.TryParseExact(text, format, CultureInfo.CurrentCulture, styles, out parsed))
            {
                utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return true;
            }

            return false;
        }

        private static DateTime ToUtc(DateTime parsed, bool assumeLocal)
        {
            switch (parsed.Kind)
            {
                case DateTimeKind.Utc:
                    return parsed;
                case DateTimeKind.Local:
                    return parsed.ToUniversalTime();
                default:
                    return assumeLocal
                        ? DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime()
                        : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
        }

        private static bool TryParseInteger(string text, out long value)
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                Math.Abs(number - Math.Round(number)) < 0.5 &&
                number >= long.MinValue && number <= long.MaxValue)
            {
                value = (long)Math.Round(number);
                return true;
            }

            value = 0;
            return false;
        }

        private static bool TryFromUnixSeconds(long seconds, out DateTime? utc)
        {
            utc = null;
            if (seconds == 0)
            {
                return true;
            }

            if (seconds < 0 || seconds > 253402300799L)
            {
                return false;
            }

            utc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            return true;
        }

        private static bool TryFromUnixMilliseconds(long milliseconds, out DateTime? utc)
        {
            utc = null;
            if (milliseconds == 0)
            {
                return true;
            }

            if (milliseconds < 0 || milliseconds > 253402300799999L)
            {
                return false;
            }

            utc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
            return true;
        }

        private static bool TryFromFileTime(long fileTime, out DateTime? utc)
        {
            utc = null;
            if (fileTime == 0)
            {
                return true;
            }

            if (fileTime < 0)
            {
                return false;
            }

            try
            {
                utc = DateTime.FromFileTimeUtc(fileTime);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
