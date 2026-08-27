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
        private const string GrayPrefix = "gray:";
        private const string CacheBustPrefix = "cachebust|";

        /// <summary>
        /// Supplies the user's custom locked cover image path, or null/blank for the built-in
        /// placeholder. Assigned once at plugin startup. Read through on every call so a settings
        /// dialog's in-flight edits and its cancel-time instance swap are both picked up without
        /// depending on PropertyChanged handler ordering.
        /// </summary>
        public static Func<string> LockedFallbackPathAccessor { get; set; }

        /// <summary>
        /// Supplies the user's custom hidden cover image path, or null/blank for the built-in
        /// placeholder. See <see cref="LockedFallbackPathAccessor"/>.
        /// </summary>
        public static Func<string> HiddenFallbackPathAccessor { get; set; }

        /// <summary>
        /// Get the built-in placeholder pack URI. This is the "no image" thumbnail used by the
        /// icon editors and the stand-in for an achievement with no artwork at all, so it ignores
        /// the user's cover settings; the masked states use <see cref="GetLockedFallbackIcon"/> and
        /// <see cref="GetHiddenFallbackIcon"/> instead.
        /// </summary>
        public static string GetDefaultIcon() => DefaultIconPackUri;

        /// <summary>
        /// The cover drawn over a locked achievement's icon while it is masked, that is, while
        /// ShowLockedIcon is off and the row has not been revealed. Clicking the cover reveals the
        /// real icon underneath, which never uses this image.
        /// </summary>
        public static string GetLockedFallbackIcon() =>
            ResolveCustomFallback(LockedFallbackPathAccessor) ?? DefaultIconPackUri;

        /// <summary>
        /// The cover drawn over a hidden achievement's icon while it is masked. Takes precedence
        /// over the locked cover when a row is both hidden and locked-masked.
        /// </summary>
        public static string GetHiddenFallbackIcon() =>
            ResolveCustomFallback(HiddenFallbackPathAccessor) ?? DefaultIconPackUri;

        /// <summary>
        /// The icon for one grid row: the hidden cover when the row's icon is hidden-masked, the
        /// locked cover when it is locked-masked, otherwise the real artwork. Hidden is tested
        /// first so the more spoiler-sensitive state wins when both apply.
        ///
        /// AchievementDisplayItem.DisplayIcon deliberately keeps its own expanded copy of this
        /// decision rather than delegating here: a source-text test
        /// (AchievementSpoilerVisibilityDefinitionTests) asserts on its literal branch lines.
        /// </summary>
        public static string ResolveRowDisplayIcon(
            bool isIconHidden,
            bool isLockedIconHidden,
            bool unlocked,
            string unlockedIconPath,
            string lockedIconPath)
        {
            if (isIconHidden)
            {
                return GetHiddenFallbackIcon();
            }

            if (isLockedIconHidden)
            {
                return GetLockedFallbackIcon();
            }

            return unlocked
                ? GetUnlockedDisplayIcon(unlockedIconPath)
                : GetLockedDisplayIcon(unlockedIconPath, lockedIconPath);
        }

        /// <summary>
        /// Resolves a configured cover path into a display source, or null when unset or the
        /// file is gone. Routed through <see cref="BuildDisplayIcon"/> so the managed file picks up
        /// a cache-bust token: the slot filename is fixed, so replacing the image overwrites the
        /// same path and would otherwise keep serving the previously decoded bitmap.
        /// </summary>
        private static string ResolveCustomFallback(Func<string> accessor)
        {
            if (accessor == null)
            {
                return null;
            }

            string configured;
            try
            {
                configured = accessor();
            }
            catch
            {
                return null;
            }

            var normalized = NormalizeDisplaySource(configured);
            if (string.IsNullOrWhiteSpace(normalized) || !IsUsableDisplayPath(normalized))
            {
                return null;
            }

            var candidate = BuildDisplayIcon(normalized, gray: false);
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }

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

        public static string GetUnlockedDisplayIcon(string unlockedIconPath) =>
            string.IsNullOrWhiteSpace(unlockedIconPath)
                ? DefaultIconPackUri
                : BuildDisplayIcon(unlockedIconPath, gray: false);

        /// <summary>
        /// The real artwork for a locked achievement: an explicit locked icon when one exists,
        /// otherwise the grayscaled unlocked icon, otherwise the built-in placeholder.
        ///
        /// The user's custom locked image is deliberately not consulted here. It is cover art for
        /// the masked state only (<see cref="GetLockedFallbackIcon"/>), so revealing a cover shows
        /// the achievement's own artwork rather than the same custom image again.
        /// </summary>
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
        /// Wraps a local file path with a cache-bust token derived from the file's
        /// last write time and length so overwriting the file at the same path
        /// produces a new cache key. Idempotent: any existing token is replaced
        /// and an existing grayscale marker is preserved.
        /// Non-file sources are returned normalized and unwrapped.
        /// </summary>
        public static string ApplyCacheBust(string path) => BuildDisplayIcon(path, gray: ContainsGrayMarker(path));

        private static bool ContainsGrayMarker(string value)
        {
            var normalized = NormalizeIcon(value);
            while (!string.IsNullOrWhiteSpace(normalized) &&
                   normalized.StartsWith(CacheBustPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var firstSeparator = normalized.IndexOf('|');
                var secondSeparator = firstSeparator >= 0 ? normalized.IndexOf('|', firstSeparator + 1) : -1;
                if (secondSeparator < 0 || secondSeparator + 1 >= normalized.Length)
                {
                    break;
                }

                normalized = normalized.Substring(secondSeparator + 1);
            }

            return normalized?.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase) == true;
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

            return NormalizeIcon(normalized);
        }
    }
}
