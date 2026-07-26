using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal static class DynamicThemeFilterExpression
    {
        private static readonly char[] Separators = { '+', ',', ';', '|' };

        public static bool TryNormalizeOne(
            object parameter,
            IReadOnlyDictionary<string, string> keyMap,
            out string normalizedKey)
        {
            var raw = NormalizeParameter(parameter);
            if (string.IsNullOrWhiteSpace(raw) || keyMap == null || !keyMap.TryGetValue(raw, out normalizedKey))
            {
                normalizedKey = null;
                return false;
            }

            return true;
        }

        public static bool TryNormalize(
            object parameter,
            IReadOnlyDictionary<string, string> keyMap,
            out string normalizedKey)
        {
            var raw = NormalizeParameter(parameter);
            if (string.IsNullOrWhiteSpace(raw) || keyMap == null)
            {
                normalizedKey = null;
                return false;
            }

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || !keyMap.TryGetValue(trimmed, out var normalized))
                {
                    normalizedKey = null;
                    return false;
                }

                if (string.Equals(normalized, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    keys.Add(normalized);
                }
            }

            normalizedKey = keys.Count == 0
                ? DynamicThemeViewKeys.All
                : string.Join("+", keys);
            return true;
        }

        public static IEnumerable<string> Enumerate(string filterKey)
        {
            if (string.IsNullOrWhiteSpace(filterKey) ||
                string.Equals(filterKey, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return Enumerable.Empty<string>();
            }

            return filterKey
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim())
                .Where(key =>
                    !string.IsNullOrWhiteSpace(key) &&
                    !string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static string NormalizeParameter(object parameter)
        {
            if (parameter is DynamicThemeOption option)
            {
                return option.Key?.Trim();
            }

            return parameter?.ToString()?.Trim();
        }
    }
}
