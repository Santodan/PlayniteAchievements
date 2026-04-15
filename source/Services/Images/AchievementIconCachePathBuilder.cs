using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PlayniteAchievements.Services.Images
{
    public enum AchievementIconVariant
    {
        Unlocked = 0,
        Locked = 1
    }

    internal static class AchievementIconCachePathBuilder
    {
        private const string FallbackStem = "achievement";
        private const int MaxStemLength = 96;
        private const string CustomFolderName = "custom";
        private static readonly HashSet<string> ReservedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        };

        public static string GetModeFolder(bool preserveOriginalResolution)
        {
            return preserveOriginalResolution ? "original" : "128";
        }

        public static string GetCustomFolder()
        {
            return CustomFolderName;
        }

        public static IReadOnlyDictionary<string, string> BuildFileStems(IEnumerable<string> apiNames)
        {
            var normalizedApiNames = (apiNames ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sanitizedByApiName = normalizedApiNames.ToDictionary(
                apiName => apiName,
                SanitizeApiName,
                StringComparer.OrdinalIgnoreCase);

            var collisionsByStem = sanitizedByApiName
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var apiName in normalizedApiNames)
            {
                var sanitizedStem = sanitizedByApiName[apiName];
                if (!collisionsByStem.TryGetValue(sanitizedStem, out var collisionCount) ||
                    collisionCount <= 1)
                {
                    result[apiName] = sanitizedStem;
                    continue;
                }

                var suffix = "_" + GetApiNameHashSuffix(apiName);
                result[apiName] = TrimStemForSuffix(sanitizedStem, suffix.Length) + suffix;
            }

            return result;
        }

        public static string BuildRelativePath(
            string gameId,
            bool preserveOriginalResolution,
            string fileStem,
            AchievementIconVariant variant)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                gameId = Guid.Empty.ToString("D");
            }

            var stem = string.IsNullOrWhiteSpace(fileStem) ? FallbackStem : fileStem.Trim();
            var fileName = variant == AchievementIconVariant.Locked
                ? stem + ".locked.png"
                : stem + ".png";

            return Path.Combine(
                "icon_cache",
                gameId.Trim(),
                GetModeFolder(preserveOriginalResolution),
                fileName);
        }

        public static string BuildCustomRelativePath(
            string gameId,
            string fileStem,
            AchievementIconVariant variant)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                gameId = Guid.Empty.ToString("D");
            }

            var stem = string.IsNullOrWhiteSpace(fileStem) ? FallbackStem : fileStem.Trim();
            var fileName = variant == AchievementIconVariant.Locked
                ? stem + ".locked.png"
                : stem + ".png";

            return Path.Combine(
                "icon_cache",
                gameId.Trim(),
                CustomFolderName,
                fileName);
        }

        private static string SanitizeApiName(string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return FallbackStem;
            }

            var trimmed = apiName.Trim();
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(trimmed.Length);
            var lastWasUnderscore = false;

            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                var replace = char.IsControl(c) || invalidChars.Contains(c);
                if (replace)
                {
                    if (!lastWasUnderscore)
                    {
                        builder.Append('_');
                        lastWasUnderscore = true;
                    }

                    continue;
                }

                builder.Append(c);
                lastWasUnderscore = false;
            }

            var sanitized = builder
                .ToString()
                .Trim()
                .TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = FallbackStem;
            }

            if (ReservedFileNames.Contains(sanitized))
            {
                sanitized += "_";
            }

            if (sanitized.Length > MaxStemLength)
            {
                sanitized = sanitized.Substring(0, MaxStemLength).TrimEnd('.', ' ');
            }

            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return FallbackStem;
            }

            return sanitized;
        }

        private static string GetApiNameHashSuffix(string apiName)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(apiName ?? string.Empty));
                var builder = new StringBuilder(8);
                for (var i = 0; i < 4; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string TrimStemForSuffix(string stem, int suffixLength)
        {
            var maxLength = Math.Max(1, MaxStemLength - Math.Max(0, suffixLength));
            if (string.IsNullOrWhiteSpace(stem) || stem.Length <= maxLength)
            {
                return string.IsNullOrWhiteSpace(stem) ? FallbackStem : stem;
            }

            var trimmed = stem.Substring(0, maxLength).TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(trimmed) ? FallbackStem : trimmed;
        }
    }
}
