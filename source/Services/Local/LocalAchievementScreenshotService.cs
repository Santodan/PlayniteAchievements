using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Services.Local
{
    /// <summary>
    /// Path and filename helpers shared by the fork's Memories screenshot and clip capture.
    /// </summary>
    internal static class LocalAchievementScreenshotService
    {
        private const string ProviderName = "Local";
        private static readonly Regex TokenPattern = new Regex("<([a-zA-Z0-9]+)>", RegexOptions.Compiled);

        internal static string ReplaceTokens(
            string template,
            Game game,
            string achievementName,
            DateTime timestamp,
            int unlockIndex,
            int unlockCount,
            bool sanitizeValues,
            string providerName = ProviderName)
        {
            var sourceName = game?.Source?.Name ?? string.Empty;
            return TokenPattern.Replace(template ?? string.Empty, match =>
            {
                string value;
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "gamename":
                        value = sanitizeValues
                            ? PlayniteAchievements.Services.UI.UnlockScreenshotService.SanitizeCaptureGameName(game?.Name)
                            : game?.Name ?? string.Empty;
                        break;
                    case "achievementname":
                        value = achievementName ?? string.Empty;
                        break;
                    case "date":
                        value = timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        break;
                    case "time":
                        value = timestamp.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
                        break;
                    case "datetime":
                        value = timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                        break;
                    case "source":
                        value = sourceName;
                        break;
                    case "provider":
                        value = providerName ?? ProviderName;
                        break;
                    case "gameid":
                        value = game?.Id.ToString() ?? string.Empty;
                        break;
                    case "unlockindex":
                        value = unlockIndex.ToString(CultureInfo.InvariantCulture);
                        break;
                    case "unlockcount":
                        value = unlockCount.ToString(CultureInfo.InvariantCulture);
                        break;
                    default:
                        return match.Value;
                }

                return sanitizeValues ? SanitizePathComponent(value) : value;
            });
        }

        private static string SanitizePathComponent(string value)
        {
            var sanitized = SanitizeFileName(value);
            return string.Equals(sanitized, "unlock_screenshot", StringComparison.Ordinal) ? "_" : sanitized;
        }

        internal static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value?.Length ?? 0);

            foreach (var character in value ?? string.Empty)
            {
                builder.Append(invalidChars.Contains(character) ? '_' : character);
            }

            var sanitized = builder
                .ToString()
                .Replace("/", "_")
                .Replace("\\", "_")
                .Trim();

            while (sanitized.Contains("__"))
            {
                sanitized = sanitized.Replace("__", "_");
            }

            sanitized = sanitized.Trim(' ', '.','_');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return "unlock_screenshot";
            }

            return sanitized.Length <= 120 ? sanitized : sanitized.Substring(0, 120).Trim(' ', '.', '_');
        }

        internal static string BuildUniquePath(string directory, string baseFileName, string extension)
        {
            var candidate = Path.Combine(directory, baseFileName + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            for (var suffix = 1; suffix < 10000; suffix++)
            {
                candidate = Path.Combine(directory, $"{baseFileName}_{suffix}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{baseFileName}_{Guid.NewGuid():N}{extension}");
        }
    }
}
