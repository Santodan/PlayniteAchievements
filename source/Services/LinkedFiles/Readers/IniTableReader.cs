using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// INI text as a table. Section mode: every section is one row with its name in
    /// <c>$section</c> and its keys as columns. Flat mode, chosen by the schema or when the
    /// file has no section headers: every key is one row with <c>$section</c>, <c>$key</c> and
    /// <c>$value</c> columns. Comment lines start with ';' or '#'; a value keeps everything
    /// after the first '=' apart from one pair of surrounding quotes.
    /// </summary>
    internal static class IniTableReader
    {
        public const string SectionColumn = "$section";

        public const string KeyColumn = "$key";

        public const string ValueColumn = "$value";

        private sealed class Section
        {
            public Section(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public List<KeyValuePair<string, string>> Entries { get; } = new List<KeyValuePair<string, string>>();
        }

        public static LinkedFileReadResult Read(string text, LinkedFileSchema schema)
        {
            var sections = new List<Section>();
            var byName = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);
            var sawHeader = false;
            Section current = null;

            Section GetSection(string name)
            {
                if (!byName.TryGetValue(name, out var section))
                {
                    section = new Section(name);
                    byName[name] = section;
                    sections.Add(section);
                }

                return section;
            }

            foreach (var rawLine in (text ?? string.Empty).Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    var close = line.IndexOf(']');
                    var name = (close > 0 ? line.Substring(1, close - 1) : line.Substring(1)).Trim();
                    current = GetSection(name);
                    sawHeader = true;
                    continue;
                }

                var separator = line.IndexOf('=');
                string key;
                string value;
                if (separator < 0)
                {
                    key = line;
                    value = string.Empty;
                }
                else
                {
                    key = line.Substring(0, separator).Trim();
                    value = Unquote(line.Substring(separator + 1).Trim());
                }

                if (key.Length == 0)
                {
                    continue;
                }

                if (current == null)
                {
                    current = GetSection(string.Empty);
                }

                var index = current.Entries.FindIndex(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    current.Entries[index] = new KeyValuePair<string, string>(current.Entries[index].Key, value);
                }
                else
                {
                    current.Entries.Add(new KeyValuePair<string, string>(key, value));
                }
            }

            if (sections.Count == 0)
            {
                return LinkedFileReadResult.Ok(LinkedFileTable.Empty);
            }

            if (schema.IniFlatMode || !sawHeader)
            {
                var flatRows = sections.SelectMany(section => section.Entries.Select(entry => new[] { section.Name, entry.Key, entry.Value }));
                return LinkedFileReadResult.Ok(LinkedFileTable.Build(new[] { SectionColumn, KeyColumn, ValueColumn }, flatRows));
            }

            var columns = new List<string> { SectionColumn };
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SectionColumn };
            foreach (var entry in sections.SelectMany(section => section.Entries))
            {
                if (known.Add(entry.Key))
                {
                    columns.Add(entry.Key);
                }
            }

            var rows = sections.Select(section =>
            {
                var lookup = section.Entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                return columns.Select(column =>
                    column == SectionColumn ? section.Name : lookup.TryGetValue(column, out var value) ? value : string.Empty);
            });

            return LinkedFileReadResult.Ok(LinkedFileTable.Build(columns, rows));
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}
