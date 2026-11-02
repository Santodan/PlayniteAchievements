using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// The names a source found a game under, kept as its link's label and offered to the other
    /// sources' searches: one line per page, its title, after the category it was found for and a
    /// tab when it was found for one. A label from before (just the game's name) reads as one name
    /// for the whole game.
    /// </summary>
    internal static class MetadataFoundNames
    {
        public static string Format(IEnumerable<(string Category, string Title)> names)
        {
            var lines = (names ?? Enumerable.Empty<(string Category, string Title)>())
                .Where(name => !string.IsNullOrWhiteSpace(name.Title))
                .Select(name => string.IsNullOrWhiteSpace(name.Category)
                    ? Clean(name.Title)
                    : Clean(name.Category) + "\t" + Clean(name.Title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        public static IReadOnlyList<(string Category, string Title)> Parse(string label)
        {
            return (label ?? string.Empty)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line =>
                {
                    var tab = line.IndexOf('\t');
                    return tab < 0
                        ? ((string)null, line)
                        : (line.Substring(0, tab).Trim(), line.Substring(tab + 1).Trim());
                })
                .Where(name => name.Item2.Length > 0)
                .ToList();
        }

        private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
