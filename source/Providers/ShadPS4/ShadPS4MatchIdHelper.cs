using System;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.ShadPS4
{
    internal enum ShadPS4MatchIdKind
    {
        None = 0,
        TitleId,
        NpCommId
    }

    internal static class ShadPS4MatchIdHelper
    {
        private static readonly Regex TitleIdPattern =
            new Regex(@"^[A-Z]{4}\d{5}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NpCommIdPattern =
            new Regex(@"^NPWR\d{5}_\d{2}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = Normalize(value);
            return !string.IsNullOrWhiteSpace(normalized);
        }

        public static string Normalize(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return GetKind(normalized) == ShadPS4MatchIdKind.None
                ? null
                : normalized;
        }

        public static ShadPS4MatchIdKind GetKind(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return ShadPS4MatchIdKind.None;
            }

            if (NpCommIdPattern.IsMatch(normalized))
            {
                return ShadPS4MatchIdKind.NpCommId;
            }

            if (TitleIdPattern.IsMatch(normalized))
            {
                return ShadPS4MatchIdKind.TitleId;
            }

            return ShadPS4MatchIdKind.None;
        }
    }
}
