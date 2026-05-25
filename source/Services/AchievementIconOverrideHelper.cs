using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;
namespace PlayniteAchievements.Services
{
    internal static class AchievementIconOverrideHelper
    {
        internal const string DefaultOverrideKey = "__default__";

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
                if (!overrides.TryGetValue(DefaultOverrideKey, out value))
                {
                    return null;
                }
            }

            return NormalizeKey(value);
        }

        public static string GetExplicitOverrideValue(
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

        public static string GetDefaultOverrideValue(IReadOnlyDictionary<string, string> overrides)
        {
            if (overrides == null || !overrides.TryGetValue(DefaultOverrideKey, out var value))
            {
                return null;
            }

            return NormalizeKey(value);
        }

        public static bool ShouldApplyDefaultOverride(string currentIconPath, bool isLockedIcon)
        {
            var normalized = AchievementIconResolver.NormalizeIconPath(currentIconPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            var defaultIcon = isLockedIcon
                ? AchievementIconResolver.GetDefaultIcon()
                : AchievementIconResolver.GetDefaultUnlockedIcon();
            if (string.Equals(normalized, defaultIcon, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Any non-empty, non-placeholder path means the achievement already has an icon
            // defined (e.g. from the JSON schema). The default override must not replace it,
            // even when the path is relative or the file cannot be verified at this moment.
            return false;
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
