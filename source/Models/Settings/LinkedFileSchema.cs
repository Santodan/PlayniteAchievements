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
        Category,

        /// <summary>
        /// Read only from mappings saved before <see cref="LinkedFileSchema.MatchColumn"/>, which
        /// <see cref="LinkedFileSchema.Normalize"/> turns into a match by position.
        /// </summary>
        Order
    }

    /// <summary>How the match column's cells name an achievement.</summary>
    public enum LinkedFileMatchBy
    {
        /// <summary>The achievement's API name, or a custom achievement's bare id.</summary>
        ApiName,

        /// <summary>The achievement's name, compared by its normalized text.</summary>
        Name,

        /// <summary>The achievement's place in the game's order, the first being 1.</summary>
        PositionFromOne,

        /// <summary>The achievement's place in the game's order, the first being 0.</summary>
        PositionFromZero
    }

    /// <summary>How a file row was matched to an achievement.</summary>
    public enum LinkedFileMatchKey
    {
        ApiName,
        DisplayName,
        Paired,
        Order
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

        /// <summary>
        /// The column every table ends with, whose cells are the row's 1-based position. It is
        /// the plugin's own numbering rather than anything the file holds, so a file that names
        /// its achievements nowhere is matched by it like by any other column.
        /// </summary>
        public const string PositionColumn = "$row";

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

        /// <summary>
        /// The column whose cells say which achievement a row is, read as <see cref="MatchBy"/>
        /// says. Null matches rows by hand pairings only; a mapping saved before this setting
        /// existed gets one from <see cref="Normalize"/>.
        /// </summary>
        public string MatchColumn { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public LinkedFileMatchBy MatchBy { get; set; }

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
        /// ignored mappings, one mapping per column and per field. Returns null for null input only.
        /// </summary>
        /// <param name="upgrade">
        /// Gives a mapping saved before <see cref="MatchColumn"/> existed the match it was read
        /// with: its API Name column, else its Order column, else its Name column, else the row's
        /// position. The Link File window passes false, since a mapping it is still editing has
        /// no match column only until one is chosen.
        /// </param>
        public static LinkedFileSchema Normalize(LinkedFileSchema schema, bool upgrade = true)
        {
            if (schema == null)
            {
                return null;
            }

            var name = SanitizeName(schema.Name);
            var customTimeFormat = Trim(schema.CustomTimeFormat);
            var columns = NormalizeColumns(schema.Columns);
            var matchColumn = Trim(schema.MatchColumn);
            var matchBy = schema.MatchBy;
            if (matchColumn == null && upgrade && columns != null)
            {
                UpgradeMatch(columns, out matchColumn, out matchBy);
            }

            // Order only ever said which achievement a row is, which the match column says now.
            columns?.RemoveAll(mapping => mapping.Field == LinkedFileField.Order);
            var normalized = new LinkedFileSchema
            {
                Name = name.Length > 0 ? name : null,
                Format = schema.Format,
                Delimiter = NormalizeDelimiter(schema.Delimiter),
                HasHeader = schema.HasHeader,
                SheetName = Trim(schema.SheetName),
                RootPath = Trim(schema.RootPath),
                RowElementPath = Trim(schema.RowElementPath),
                Columns = columns?.Count > 0 ? columns : null,
                MatchColumn = matchColumn,
                MatchBy = matchBy,
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

        private static void UpgradeMatch(IReadOnlyList<LinkedFileColumnMapping> columns, out string column, out LinkedFileMatchBy matchBy)
        {
            string ColumnOf(LinkedFileField field) => columns.FirstOrDefault(mapping => mapping.Field == field)?.Column;

            if ((column = ColumnOf(LinkedFileField.ApiName)) != null)
            {
                matchBy = LinkedFileMatchBy.ApiName;
            }
            else if ((column = ColumnOf(LinkedFileField.Order)) != null)
            {
                matchBy = LinkedFileMatchBy.PositionFromOne;
            }
            else if ((column = ColumnOf(LinkedFileField.DisplayName)) != null)
            {
                matchBy = LinkedFileMatchBy.Name;
            }
            else
            {
                column = PositionColumn;
                matchBy = LinkedFileMatchBy.PositionFromOne;
            }
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
                MatchColumn = MatchColumn,
                MatchBy = MatchBy,
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
        /// A listed row means unlocked when the file says which achievement each row is and
        /// nothing else, as in files that list only earned achievements. Any other mapped column,
        /// such as an unlock state, a time, progress or a description, leaves unlock state to the
        /// columns.
        /// </summary>
        [JsonIgnore]
        public bool RowPresenceMeansUnlocked =>
            !string.IsNullOrWhiteSpace(MatchColumn) &&
            (Columns == null || Columns.All(mapping => mapping == null || NamesAchievement(mapping.Field)));

        /// <summary>Whether a column of <paramref name="field"/> only names the achievement.</summary>
        private static bool NamesAchievement(LinkedFileField field)
        {
            return field == LinkedFileField.Ignore || field == LinkedFileField.ApiName ||
                   field == LinkedFileField.DisplayName || field == LinkedFileField.Order;
        }

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
