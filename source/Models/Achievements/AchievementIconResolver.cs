using System;
using System.IO;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Icon resolution utilities for achievement display.
    /// </summary>
    public static class AchievementIconResolver
    {
        private const string DefaultIconPackUri = "pack://application:,,,/PlayniteAchievements;component/Resources/HiddenAchIcon.png";
        private const string DefaultUnlockedIconPackUri = "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";
        private const string GrayPrefix = "gray:";
        private const string CacheBustPrefix = "cachebust|";

        /// <summary>
        /// Resolve the display icon for an achievement based on its unlock state.
        /// </summary>
        public static string GetDisplayIcon(bool unlocked, string iconPath)
        {
            return unlocked
                ? GetUnlockedDisplayIcon(iconPath)
                : GetLockedDisplayIcon(iconPath, null);
        }

        public static string NormalizeIconPath(string iconPath)
        {
            return NormalizeDisplaySource(iconPath);
        }

        /// <summary>
        /// True if two icon identifiers are the same (case-insensitive, trimmed).
        /// Kept for compatibility with existing code.
        /// </summary>
        public static bool AreSameIcon(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return true;
            }

            return string.Equals(
                NormalizeIconPath(left),
                NormalizeIconPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Get the default hidden icon pack URI.
        /// </summary>
        public static string GetDefaultIcon() => DefaultIconPackUri;

        public static string GetDefaultUnlockedIcon() => DefaultUnlockedIconPackUri;

        /// <summary>
        /// Returns a plain path/pack URI without cache-busting or grayscale prefixes.
        /// This preserves compatibility with legacy themes that bind directly to Image.Source.
        /// </summary>
        public static string GetLegacyCompatibleIcon(string iconPath)
        {
            var normalized = NormalizeDisplaySource(iconPath);
            return string.IsNullOrWhiteSpace(normalized)
                ? DefaultIconPackUri
                : normalized;
        }

        public static string GetLegacyCompatibleLockedIcon(string unlockedIconPath, string lockedIconPath)
        {
            if (HasExplicitLockedIcon(lockedIconPath, unlockedIconPath))
            {
                return GetLegacyCompatibleIcon(lockedIconPath);
            }

            return GetLegacyCompatibleIcon(unlockedIconPath);
        }

        public static string GetUnlockedDisplayIcon(string unlockedIconPath) =>
            string.IsNullOrWhiteSpace(unlockedIconPath)
                ? DefaultIconPackUri
                : BuildDisplayIcon(unlockedIconPath, gray: false);

        public static string GetLockedDisplayIcon(string unlockedIconPath, string lockedIconPath)
        {
            if (HasExplicitLockedIcon(lockedIconPath, unlockedIconPath))
            {
                return BuildDisplayIcon(lockedIconPath, gray: false);
            }

            var candidate = BuildDisplayIcon(unlockedIconPath, gray: true);
            return string.IsNullOrWhiteSpace(candidate) ? DefaultIconPackUri : candidate;
        }

        public static bool HasExplicitLockedIcon(string lockedIconPath, string unlockedIconPath)
        {
            var normalizedLockedIconPath = NormalizeDisplaySource(lockedIconPath);
            if (string.IsNullOrWhiteSpace(normalizedLockedIconPath))
            {
                return false;
            }

            if (!IsUsableDisplayPath(normalizedLockedIconPath))
            {
                return false;
            }

            var normalizedUnlockedIconPath = NormalizeDisplaySource(unlockedIconPath);
            if (string.IsNullOrWhiteSpace(normalizedUnlockedIconPath))
            {
                return true;
            }

            return !string.Equals(
                NormalizeIcon(normalizedLockedIconPath),
                NormalizeIcon(normalizedUnlockedIconPath),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Prefixes the icon identifier with "gray:" when not already prefixed.
        /// </summary>
        public static string ApplyGrayPrefix(string icon)
        {
            var normalized = NormalizeDisplaySource(icon);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            return normalized.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase)
                ? normalized
                : GrayPrefix + normalized;
        }

        private static string NormalizeIcon(string value) => value?.Trim();

        private static bool IsUsableDisplayPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (value.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return File.Exists(value);
        }

        private static string BuildDisplayIcon(string iconPath, bool gray)
        {
            var normalized = NormalizeDisplaySource(iconPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            var candidate = gray ? ApplyGrayPrefix(normalized) : normalized;
            var cacheBustToken = TryGetCacheBustToken(candidate);
            return string.IsNullOrWhiteSpace(cacheBustToken)
                ? candidate
                : string.Concat(CacheBustPrefix, cacheBustToken, "|", candidate);
        }

        private static string TryGetCacheBustToken(string value)
        {
            var normalized = NormalizeDisplaySource(value);
            if (string.IsNullOrWhiteSpace(normalized) || !Path.IsPathRooted(normalized) || !File.Exists(normalized))
            {
                return null;
            }

            try
            {
                var fileInfo = new FileInfo(normalized);
                return string.Concat(fileInfo.LastWriteTimeUtc.Ticks.ToString(), ":", fileInfo.Length.ToString());
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeDisplaySource(string value)
        {
            var normalized = NormalizeIcon(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            while (true)
            {
                var changed = false;

                if (normalized.StartsWith(CacheBustPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var firstSeparator = normalized.IndexOf('|');
                    if (firstSeparator >= 0)
                    {
                        var secondSeparator = normalized.IndexOf('|', firstSeparator + 1);
                        if (secondSeparator >= 0 && secondSeparator + 1 < normalized.Length)
                        {
                            normalized = normalized.Substring(secondSeparator + 1);
                            changed = true;
                        }
                    }
                }

                if (normalized.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(GrayPrefix.Length);
                    changed = true;
                }

                if (!changed)
                {
                    break;
                }
            }

            if (string.Equals(normalized, "Resources/UnlockedAchIcon.png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "UnlockedAchIcon.png", StringComparison.OrdinalIgnoreCase))
            {
                normalized = DefaultUnlockedIconPackUri;
            }
            else if (string.Equals(normalized, "Resources/HiddenAchIcon.png", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "HiddenAchIcon.png", StringComparison.OrdinalIgnoreCase))
            {
                normalized = DefaultIconPackUri;
            }

            return NormalizeIcon(normalized);
        }
    }
}
