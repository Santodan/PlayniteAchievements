using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    internal static class AchievementNoteHelper
    {
        public const int MaxNoteLength = 4096;

        public static string NormalizeNote(string value)
        {
            var normalized = (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return normalized.Length > MaxNoteLength
                ? normalized.Substring(0, MaxNoteLength)
                : normalized;
        }

        public static Dictionary<string, string> NormalizeNoteMap(IReadOnlyDictionary<string, string> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                var apiName = NormalizeApiName(pair.Key);
                var note = NormalizeNote(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(note))
                {
                    continue;
                }

                normalized[apiName] = note;
            }

            return normalized.Count > 0 ? normalized : null;
        }

        public static string NormalizeApiName(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public static string GetPreviewText(string value, int maxLength = 180)
        {
            var note = NormalizeNote(value);
            if (string.IsNullOrWhiteSpace(note))
            {
                return string.Empty;
            }

            var text = note
                .Replace("**", string.Empty)
                .Replace("__", string.Empty)
                .Replace("*", string.Empty);

            var buffer = new List<char>(text.Length);
            var previousWasWhitespace = false;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!previousWasWhitespace && buffer.Count > 0)
                    {
                        buffer.Add(' ');
                    }

                    previousWasWhitespace = true;
                    continue;
                }

                buffer.Add(ch);
                previousWasWhitespace = false;
            }

            var preview = new string(buffer.ToArray()).Trim();
            if (maxLength <= 0 || preview.Length <= maxLength)
            {
                return preview;
            }

            return preview.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";
        }
    }
}
