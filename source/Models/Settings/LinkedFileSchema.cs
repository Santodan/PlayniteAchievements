using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>How a linked file is read into a table.</summary>
    public enum LinkedFileFormat
    {
        Delimited,
        Json,
        Xlsx,
        Xml,
        Ini,
        Toml,
        Binary
    }

    /// <summary>The achievement field a file column feeds.</summary>
    public enum LinkedFileField
    {
        Ignore,
        ApiName,
        DisplayName,
        Description,
        Unlocked,
        UnlockTime,
        ProgressNum,
        ProgressDenom,
        Hidden,
        UnlockedIconPath,
        LockedIconPath,
        Points,
        TrophyType,
        Rarity,
        Category
    }

    /// <summary>How a file row was matched to an achievement.</summary>
    public enum LinkedFileMatchKey
    {
        ApiName,
        DisplayName,
        RowOrder,
        Paired
    }

    /// <summary>How unlock time cells are read.</summary>
    public enum LinkedFileTimeFormat
    {
        Auto,
        UnixSeconds,
        UnixMilliseconds,
        WindowsFileTime,
        Iso8601,
        Custom
    }

    /// <summary>One file column and the achievement field it feeds.</summary>
    public sealed class LinkedFileColumnMapping
    {
        /// <summary>
        /// The column header, or its 1-based position as text when the file has no header row.
        /// </summary>
        public string Column { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public LinkedFileField Field { get; set; }

        public LinkedFileColumnMapping Clone()
        {
            return new LinkedFileColumnMapping
            {
                Column = Column,
                Field = Field
            };
        }
    }

    /// <summary>
    /// The mapping that reads a linked file: the format and its options, which column feeds which
    /// achievement field, how rows are matched to achievements, and how cell values are parsed.
    /// Travels in the portable package and in the saved mapping list; never holds a file path.
    /// </summary>
    public sealed class LinkedFileSchema
    {
        public const int MaxNameLength = 64;

        public static readonly string[] DefaultTrueValues = { "true", "yes", "y", "1", "unlocked", "earned", "achieved" };

        public static readonly string[] DefaultFalseValues = { "false", "no", "n", "0", "locked" };

        public string Name { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public LinkedFileFormat Format { get; set; }

        /// <summary>Delimited text: null detects among comma, semicolon, tab and pipe; "\t" is tab.</summary>
        public string Delimiter { get; set; }

        /// <summary>Delimited text and workbooks: the first row names the columns.</summary>
        public bool HasHeader { get; set; } = true;

        /// <summary>Workbooks: the sheet to read; null reads the first sheet.</summary>
        public string SheetName { get; set; }

        /// <summary>
        /// JSON and TOML: dotted path to the node holding the rows, with <c>[*]</c> for every
        /// element of an array; null reads the root.
        /// </summary>
        public string RootPath { get; set; }

        /// <summary>XML: dotted element names from the root to the element that is one row.</summary>
        public string RowElementPath { get; set; }

        public List<LinkedFileColumnMapping> Columns { get; set; }

        /// <summary>Rows that match no achievement become new custom achievements.</summary>
        public bool CreateUnmatchedRows { get; set; }

        /// <summary>Cell values that read as unlocked; null uses <see cref="DefaultTrueValues"/>.</summary>
        public List<string> UnlockedTrueValues { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public LinkedFileTimeFormat TimeFormat { get; set; }

        /// <summary>A .NET date format string, used when <see cref="TimeFormat"/> is Custom.</summary>
        public string CustomTimeFormat { get; set; }

        /// <summary>Times without a zone are local; otherwise they are UTC.</summary>
        public bool AssumeLocalTime { get; set; } = true;

        public static string SanitizeName(string name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length > MaxNameLength)
            {
                trimmed = trimmed.Substring(0, MaxNameLength);
            }

            return trimmed;
        }

        /// <summary>
        /// A fresh copy with every field in its stored form: trimmed strings, no blank or
        /// ignored mappings, one mapping per column and per field, a non-empty match order.
        /// Returns null for null input only.
        /// </summary>
        public static LinkedFileSchema Normalize(LinkedFileSchema schema)
        {
            if (schema == null)
            {
                return null;
            }

            var name = SanitizeName(schema.Name);
            var customTimeFormat = Trim(schema.CustomTimeFormat);
            var normalized = new LinkedFileSchema
            {
                Name = name.Length > 0 ? name : null,
                Format = schema.Format,
                Delimiter = NormalizeDelimiter(schema.Delimiter),
                HasHeader = schema.HasHeader,
                SheetName = Trim(schema.SheetName),
                RootPath = Trim(schema.RootPath),
                RowElementPath = Trim(schema.RowElementPath),
                Columns = NormalizeColumns(schema.Columns),
                CreateUnmatchedRows = schema.CreateUnmatchedRows,
                UnlockedTrueValues = NormalizeValues(schema.UnlockedTrueValues),
                TimeFormat = schema.TimeFormat == LinkedFileTimeFormat.Custom && customTimeFormat == null
                    ? LinkedFileTimeFormat.Auto
                    : schema.TimeFormat,
                CustomTimeFormat = customTimeFormat,
                AssumeLocalTime = schema.AssumeLocalTime
            };

            return normalized;
        }

        public LinkedFileSchema Clone()
        {
            return new LinkedFileSchema
            {
                Name = Name,
                Format = Format,
                Delimiter = Delimiter,
                HasHeader = HasHeader,
                SheetName = SheetName,
                RootPath = RootPath,
                RowElementPath = RowElementPath,
                Columns = Columns?.Where(mapping => mapping != null).Select(mapping => mapping.Clone()).ToList(),
                CreateUnmatchedRows = CreateUnmatchedRows,
                UnlockedTrueValues = UnlockedTrueValues?.ToList(),
                TimeFormat = TimeFormat,
                CustomTimeFormat = CustomTimeFormat,
                AssumeLocalTime = AssumeLocalTime
            };
        }

        /// <summary>The column mapped to <paramref name="field"/>, if any.</summary>
        public bool TryGetColumn(LinkedFileField field, out string column)
        {
            column = Columns?
                .FirstOrDefault(mapping => mapping != null && mapping.Field == field && !string.IsNullOrWhiteSpace(mapping.Column))?
                .Column;
            return column != null;
        }

        public bool HasColumn(LinkedFileField field)
        {
            return TryGetColumn(field, out _);
        }

        /// <summary>
        /// A listed row means unlocked when the mapping names achievements and nothing else, as
        /// in files that list only earned achievements. Any other mapped column, such as an
        /// unlock state, a time, progress or a description, leaves unlock state to the columns.
        /// </summary>
        [JsonIgnore]
        public bool RowPresenceMeansUnlocked =>
            Columns != null &&
            Columns.Any(mapping => mapping != null && (mapping.Field == LinkedFileField.ApiName || mapping.Field == LinkedFileField.DisplayName)) &&
            Columns.All(mapping => mapping == null ||
                                   mapping.Field == LinkedFileField.Ignore ||
                                   mapping.Field == LinkedFileField.ApiName ||
                                   mapping.Field == LinkedFileField.DisplayName);

        /// <summary>
        /// Rows pair with achievements by row order only when no column is mapped to API Name or
        /// Name to match them by.
        /// </summary>
        [JsonIgnore]
        public bool MatchesByRowOrder =>
            !HasColumn(LinkedFileField.ApiName) && !HasColumn(LinkedFileField.DisplayName);

        /// <summary>The values that read as unlocked, lowercased, falling back to the defaults.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> EffectiveTrueValues =>
            UnlockedTrueValues != null && UnlockedTrueValues.Count > 0 ? (IReadOnlyList<string>)UnlockedTrueValues : DefaultTrueValues;

        /// <summary>
        /// The delimiter character an explicit <see cref="Delimiter"/> names, or null to detect.
        /// </summary>
        [JsonIgnore]
        public char? DelimiterChar
        {
            get
            {
                var normalized = NormalizeDelimiter(Delimiter);
                return normalized == null ? (char?)null : normalized[0];
            }
        }

        private static string Trim(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string NormalizeDelimiter(string value)
        {
            if (value == null)
            {
                return null;
            }

            if (value == "\t" || string.Equals(value.Trim(), "tab", StringComparison.OrdinalIgnoreCase) || value.Trim() == "\\t")
            {
                return "\t";
            }

            // A space delimiter is whitespace itself, so it is given as one space or by name.
            if (value == " " || string.Equals(value.Trim(), "space", StringComparison.OrdinalIgnoreCase))
            {
                return " ";
            }

            var trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed.Substring(0, 1);
        }

        private static List<LinkedFileColumnMapping> NormalizeColumns(IEnumerable<LinkedFileColumnMapping> columns)
        {
            if (columns == null)
            {
                return null;
            }

            var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFields = new HashSet<LinkedFileField>();
            var normalized = new List<LinkedFileColumnMapping>();
            foreach (var mapping in columns)
            {
                var column = Trim(mapping?.Column);
                if (column == null || mapping.Field == LinkedFileField.Ignore)
                {
                    continue;
                }

                if (!seenColumns.Add(column) || !seenFields.Add(mapping.Field))
                {
                    continue;
                }

                normalized.Add(new LinkedFileColumnMapping { Column = column, Field = mapping.Field });
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static List<string> NormalizeValues(IEnumerable<string> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = values
                .Select(value => (value ?? string.Empty).Trim().ToLowerInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return normalized.Count > 0 ? normalized : null;
        }
    }
}
