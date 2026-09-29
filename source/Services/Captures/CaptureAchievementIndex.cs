using System;
using System.Collections.Generic;
using System.IO;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Captures
{
    /// <summary>
    /// Joins captures back to the achievements they were taken for.
    /// <para>
    /// A <see cref="CaptureItem"/> carries no achievement identity: its folder is a sanitized game
    /// name and its stem is a sanitized achievement name, both produced by the capture writer
    /// (<c>UnlockScreenshotService.BuildCaptureName</c>). Rather than invert those sanitizers, this
    /// runs them forward over achievement rows, so the keys match the ones
    /// <see cref="CaptureFileNameParser"/> yields by construction.
    /// </para>
    /// Kept free of WPF types and generic over the row type so it can be unit-tested and reused by
    /// any capture surface.
    /// </summary>
    internal static class CaptureAchievementIndex
    {
        // An invalid file-name character, so sanitization can never leave one inside a folder or
        // stem and a key can never straddle the separator ambiguously.
        private const char KeySeparator = '|';

        /// <summary>
        /// The index key for an achievement, sanitized exactly as the capture writer would have
        /// named its file.
        /// </summary>
        public static string BuildKey(string gameName, string achievementName) =>
            UnlockScreenshotService.SanitizeCaptureGameName(gameName) +
            KeySeparator +
            AchievementIconCachePathBuilder.SanitizeSegment(achievementName);

        /// <summary>
        /// The index key for a capture. The folder name on disk and the parsed stem are already in
        /// sanitized form, so neither is re-sanitized here.
        /// </summary>
        public static string KeyForCapture(string filePath, string achievementStem) =>
            GetCaptureFolderName(filePath) + KeySeparator + (achievementStem ?? string.Empty);

        /// <summary>The sanitized game-name folder a capture lives in, or empty when unavailable.</summary>
        public static string GetCaptureFolderName(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Indexes rows by <see cref="BuildKey"/>. First row wins on collision: two achievements in
        /// one game can sanitize to the same stem, and their captures are equally ambiguous in that
        /// case, so there is nothing better to prefer.
        /// </summary>
        public static Dictionary<string, T> Build<T>(
            IEnumerable<T> rows,
            Func<T, string> gameName,
            Func<T, string> achievementName)
            where T : class
        {
            var index = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            if (rows == null || gameName == null || achievementName == null)
            {
                return index;
            }

            foreach (var row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                var key = BuildKey(gameName(row), achievementName(row));
                if (!index.ContainsKey(key))
                {
                    index[key] = row;
                }
            }

            return index;
        }
    }
}
