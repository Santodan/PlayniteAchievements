using System;
using System.Collections.Generic;
namespace PlayniteAchievements.Services
{
    internal static class AchievementIconOverrideHelper
    {
        public static bool HasOverrides(IReadOnlyDictionary<string, string> unlockedOverrides, IReadOnlyDictionary<string, string> lockedOverrides)
        {
            return (unlockedOverrides != null && unlockedOverrides.Count > 0) ||
                   (lockedOverrides != null && lockedOverrides.Count > 0);
        }

        public static string GetOverrideValue(
            IReadOnlyDictionary<string, string> overrides,
            string apiName)
        {
            if (overrides == null)
            {
                return null;
            }

            var normalizedKey = NormalizeKey(apiName);
            if (string.IsNullOrWhiteSpace(normalizedKey) ||
                !overrides.TryGetValue(normalizedKey, out var value))
            {
                return null;
            }

            return NormalizeKey(value);
        }

        public static string ResolveEffectiveLockedPath(
            string unlockedIconPath,
            string lockedIconPath,
            bool useSeparateLockedIcons,
            bool hasExplicitUnlockedIcon,
            bool hasExplicitLockedIcon)
        {
            if (hasExplicitLockedIcon && !string.IsNullOrWhiteSpace(lockedIconPath))
            {
                return lockedIconPath;
            }

            if (hasExplicitUnlockedIcon || !useSeparateLockedIcons)
            {
                return unlockedIconPath;
            }

            return !string.IsNullOrWhiteSpace(lockedIconPath)
                ? lockedIconPath
                : unlockedIconPath;
        }

        private static string NormalizeKey(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
