using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// INI text as a table, shaped by the file. When the same keys repeat from section to
    /// section, as in one section per achievement, every section is one row with its name in
    /// <c>$section</c> and its keys as columns. Otherwise every key is one row with its name in
    /// <c>$key</c> and one column per section holding its value there, so a file with
    /// <c>[unlocked]</c> and <c>[progress]</c> sections reads both for each achievement; keys
    /// before any section header fill a <c>$value</c> column. A mapping that already uses
    /// <c>$section</c> or <c>$key</c> keeps that shape. Comment lines start with ';' or
    /// '#'; a value keeps everything after the first '=' apart from one pair of surrounding quotes.
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

            // A mapping that names $section or $key keeps its shape, so a file that holds one
            // section today, such as one written at the first unlock, still reads the same way.
            var mapsSection = schema.Columns?.Any(mapping => string.Equals(mapping?.Column, SectionColumn, StringComparison.OrdinalIgnoreCase)) == true;
            var mapsKey = schema.Columns?.Any(mapping => string.Equals(mapping?.Column, KeyColumn, StringComparison.OrdinalIgnoreCase)) == true;
            var sectionsAreRecords = mapsSection || (!mapsKey && sawHeader && SectionsAreRecords(sections));
            if (!sectionsAreRecords)
            {
                return LinkedFileReadResult.Ok(KeysAsRows(sections));
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

        /// <summary>
        /// Whether sections are the records: there are at least two, at least half of the
        /// distinct keys appear in more than one section, and keys do not outnumber sections more
        /// than two to one. A section per achievement repeats a few field names across many
        /// sections; a section per kind of data, such as unlocks and progress, lists many
        /// achievements in a few sections even when every achievement appears in each.
        /// </summary>
        private static bool SectionsAreRecords(IReadOnlyList<Section> sections)
        {
            if (sections.Count < 2)
            {
                return false;
            }

            var sectionsByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in sections.SelectMany(section => section.Entries))
            {
                sectionsByKey.TryGetValue(entry.Key, out var count);
                sectionsByKey[entry.Key] = count + 1;
            }

            var shared = sectionsByKey.Values.Count(count => count > 1);
            return sectionsByKey.Count > 0 &&
                   shared * 2 >= sectionsByKey.Count &&
                   sections.Count * 2 >= sectionsByKey.Count;
        }

        /// <summary>One row per distinct key, in file order, with one column per section.</summary>
        private static LinkedFileTable KeysAsRows(IReadOnlyList<Section> sections)
        {
            var columns = new List<string> { KeyColumn };
            columns.AddRange(sections.Select(section => section.Name.Length == 0 ? ValueColumn : section.Name));

            var rows = new List<string[]>();
            var rowByKey = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < sections.Count; i++)
            {
                foreach (var entry in sections[i].Entries)
                {
                    if (!rowByKey.TryGetValue(entry.Key, out var row))
                    {
                        row = Enumerable.Repeat(string.Empty, columns.Count).ToArray();
                        row[0] = entry.Key;
                        rowByKey[entry.Key] = row;
                        rows.Add(row);
                    }

                    row[i + 1] = entry.Value;
                }
            }

            return LinkedFileTable.Build(columns, rows);
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
