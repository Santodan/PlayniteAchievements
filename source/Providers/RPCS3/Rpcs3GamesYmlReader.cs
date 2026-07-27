using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Providers.RPCS3
{
    internal static class Rpcs3GamesYmlReader
    {
        public static IReadOnlyDictionary<string, string> ReadTitlePathMap(string gamesYmlPath, ILogger logger = null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(gamesYmlPath) || !File.Exists(gamesYmlPath))
            {
                return map;
            }

            try
            {
                foreach (var rawLine in File.ReadLines(gamesYmlPath))
                {
                    var line = StripComment(rawLine).Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var titleId = Unquote(line.Substring(0, separatorIndex).Trim());
                    var path = Unquote(StripYamlStringTag(line.Substring(separatorIndex + 1).Trim()));

                    if (!string.IsNullOrWhiteSpace(titleId) && !string.IsNullOrWhiteSpace(path))
                    {
                        map[titleId] = path;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[RPCS3] Failed to parse games.yml at '{gamesYmlPath}'");
            }

            return map;
        }

        private static string StripYamlStringTag(string value)
        {
            const string stringTag = "!!str";
            return value.StartsWith(stringTag, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(stringTag.Length).Trim()
                : value;
        }

        private static string StripComment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var inSingleQuote = false;
            var inDoubleQuote = false;

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (c == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (c == '#' && !inSingleQuote && !inDoubleQuote)
                {
                    return value.Substring(0, i);
                }
            }

            return value;
        }

        private static string Unquote(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length < 2)
            {
                return trimmed;
            }

            var first = trimmed[0];
            var last = trimmed[trimmed.Length - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return trimmed.Substring(1, trimmed.Length - 2);
            }

            return trimmed;
        }
    }
}
