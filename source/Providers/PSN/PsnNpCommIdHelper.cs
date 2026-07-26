using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>
    /// Validates and normalizes a PSN NP Communication ID (e.g. NPWR12345_00), the identifier
    /// used to fetch a title's trophy set.
    /// </summary>
    internal static class PsnNpCommIdHelper
    {
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

            return NpCommIdPattern.IsMatch(normalized) ? normalized : null;
        }
    }
}
