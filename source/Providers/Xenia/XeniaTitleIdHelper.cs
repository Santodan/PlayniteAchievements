using System;

namespace PlayniteAchievements.Providers.Xenia
{
    internal static class XeniaTitleIdHelper
    {
        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = Normalize(value);
            return !string.IsNullOrWhiteSpace(normalized);
        }

        public static string Normalize(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(2);
            }

            normalized = normalized.Trim().ToUpperInvariant();
            if (normalized.Length != 8)
            {
                return null;
            }

            foreach (var character in normalized)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return null;
                }
            }

            return normalized;
        }
    }
}
