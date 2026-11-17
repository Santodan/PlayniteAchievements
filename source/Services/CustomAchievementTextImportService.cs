using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services
{
    public sealed class CustomAchievementTextImportResult
    {
        public List<CustomAchievementDefinition> Definitions { get; } =
            new List<CustomAchievementDefinition>();

        public List<string> Errors { get; } = new List<string>();

        public bool HasErrors => Errors.Count > 0;
    }

    /// <summary>
    /// The rows of an achievement CSV, each holding only the cells that were filled in.
    /// </summary>
    public sealed class CustomAchievementCsvParseResult
    {
        public List<CustomAchievementCsvRow> Rows { get; } = new List<CustomAchievementCsvRow>();

        public List<string> Errors { get; } = new List<string>();

        /// <summary>Header cells that name no known column, in file order.</summary>
        public List<string> IgnoredColumns { get; } = new List<string>();

        public bool HasErrors => Errors.Count > 0;
    }

    public sealed class CustomAchievementTextImportService
    {
        internal const string TrophyTypeMessage = "is not bronze, silver, gold or platinum.";

        internal const string RarityMessage ="is not a percent from 0 to 100, or Common, Uncommon, Rare or Ultra Rare.";

        /// <summary>
        /// Reads every row, keeping only the cells that were filled in, and checks each value.
        /// A row may leave Title blank when it names an ID, since it can be updating an existing
        /// achievement; whether the ID is new is for the caller to decide.
        /// </summary>
        /// <param name="nowUtc">The clock an unlock time is checked against; the current time
        /// when null.</param>
        /// <param name="iconBaseDirectory">The folder of the CSV file, when it was read from one.
        /// Icon cells must then be a web address or a file that exists, a relative path read
        /// against this folder. Without it icon cells pass through as written, for a package
        /// whose reader resolves its own entries.</param>
        public CustomAchievementCsvParseResult Parse(string text, DateTime? nowUtc = null, string iconBaseDirectory = null)
        {
            var result = new CustomAchievementCsvParseResult();
            var records = DelimitedTextReader.Parse(text, null, DelimitedTextReader.SpreadsheetDelimiters);
            var rows = records.Rows;
            var headerIndex = rows.FindIndex(row => !IsEmptyRow(row));
            if (headerIndex < 0)
            {
                result.Errors.Add("No rows were found.");
                return result;
            }

            var header = rows[headerIndex];
            var mapping = new List<LinkedFileField>(header.Count);
            var seen = new HashSet<LinkedFileField>();
            foreach (var cell in header)
            {
                var field = LinkedFileColumnGuesser.ResolveCsvHeader(cell);
                if (field == LinkedFileField.Ignore)
                {
                    var name = NormalizeText(cell);
                    if (name != null)
                    {
                        result.IgnoredColumns.Add(name);
                    }
                }
                else if (!seen.Add(field))
                {
                    result.Errors.Add($"Column \"{NormalizeText(cell)}\" appears more than once.");
                }

                mapping.Add(field);
            }

            if (!seen.Contains(LinkedFileField.ApiName) && !seen.Contains(LinkedFileField.DisplayName))
            {
                result.Errors.Add("An ID or Title column is required.");
            }

            if (result.HasErrors)
            {
                return result;
            }

            var latestUnlock = (nowUtc ?? DateTime.UtcNow).AddMinutes(1);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
            {
                var cells = rows[rowIndex];
                if (IsEmptyRow(cells))
                {
                    continue;
                }

                var rowNumber = rowIndex + 1;

                // One separator for the whole file: a row saved with another, or one whose
                // unquoted text pushed cells past the header, would otherwise land its values in
                // the wrong columns without a word.
                if (header.Count > 1 && records.MixedRows.Contains(rowIndex))
                {
                    result.Errors.Add($"Row {rowNumber}: uses a different separator than the header ({DescribeDelimiter(records.Delimiter)}).");
                    continue;
                }

                if (cells.Count > header.Count && cells.Skip(header.Count).Any(value => !string.IsNullOrWhiteSpace(value)))
                {
                    result.Errors.Add($"Row {rowNumber}: has more cells than the header; a value containing {DescribeDelimiter(records.Delimiter)} needs quotes.");
                    continue;
                }

                var row = new CustomAchievementCsvRow { RowNumber = rowNumber };
                var rowErrorCount = result.Errors.Count;
                for (var columnIndex = 0; columnIndex < cells.Count && columnIndex < mapping.Count; columnIndex++)
                {
                    ApplyField(result, row, mapping[columnIndex], NormalizeText(header[columnIndex]), cells[columnIndex], rowNumber, iconBaseDirectory);
                }

                if (row.Id == null && row.DisplayName == null)
                {
                    result.Errors.Add($"Row {rowNumber}: an ID or a Title is required.");
                }

                if (row.Id != null && !usedIds.Add(row.Id))
                {
                    result.Errors.Add($"Row {rowNumber}, ID: \"{row.Id}\" is used by an earlier row.");
                }

                if (row.Unlocked == false && row.UnlockTimeUtc.HasValue)
                {
                    result.Errors.Add($"Row {rowNumber}, Unlock Time: set while Unlocked is false.");
                }

                if (row.UnlockTimeUtc.HasValue && row.UnlockTimeUtc.Value > latestUnlock)
                {
                    result.Errors.Add($"Row {rowNumber}, Unlock Time: is in the future.");
                }

                if (row.ProgressNum.HasValue &&
                    row.ProgressDenom.HasValue &&
                    row.ProgressNum.Value > row.ProgressDenom.Value)
                {
                    result.Errors.Add($"Row {rowNumber}, Progress: is greater than Progress Total.");
                }

                if (result.Errors.Count == rowErrorCount)
                {
                    result.Rows.Add(row);
                }
            }

            return result;
        }

        /// <summary>
        /// Reads a CSV as complete definitions, for a package whose rows are all new: every row
        /// needs a Title, and a blank ID is generated from it.
        /// </summary>
        public CustomAchievementTextImportResult Import(string text)
        {
            var result = new CustomAchievementTextImportResult();
            var parsed = Parse(text);
            result.Errors.AddRange(parsed.Errors);
            if (parsed.HasErrors)
            {
                return result;
            }

            var usedIds = new HashSet<string>(
                parsed.Rows.Where(row => row.Id != null).Select(row => row.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var row in parsed.Rows)
            {
                if (row.DisplayName == null)
                {
                    result.Errors.Add($"Row {row.RowNumber}, Title: is required.");
                    continue;
                }

                var id = row.Id ?? CustomAchievementProjectionService.GenerateId(row.DisplayName, usedIds);
                usedIds.Add(id);
                result.Definitions.Add(ToDefinition(row, id));
            }

            if (result.Definitions.Count == 0 && !result.HasErrors)
            {
                result.Errors.Add("No importable achievements were found.");
            }

            return result;
        }

        /// <summary>A new authored achievement built from a row's cells.</summary>
        public static CustomAchievementDefinition ToDefinition(CustomAchievementCsvRow row, string id)
        {
            var definition = new CustomAchievementDefinition
            {
                Id = id,
                DisplayName = row.DisplayName,
                Description = row.Description,
                Points = row.Points,
                TrophyType = row.TrophyType,
                Hidden = row.Hidden ?? false,
                Category = row.Category,
                ProgressNum = row.ProgressNum,
                ProgressDenom = row.ProgressDenom,
                Unlocked = row.Unlocked ?? row.UnlockTimeUtc.HasValue,
                UnlockTimeUtc = row.Unlocked == false ? null : row.UnlockTimeUtc,
                UnlockedIconPath = row.UnlockedIconPath,
                LockedIconPath = row.LockedIconPath
            };
            SetRarity(definition, row.RarityPercent, row.RarityTier);
            return definition;
        }

        /// <summary>
        /// Stores a parsed rarity cell on a definition: a percent sets the percent and the tier it
        /// falls in; a tier alone sets the tier and clears the percent.
        /// </summary>
        public static void SetRarity(CustomAchievementDefinition definition, double? percent, string tier)
        {
            definition.Rarity = percent.HasValue
                ? PercentRarityHelper.GetRarityTier(percent.Value).ToString()
                : tier;
            definition.GlobalPercentUnlocked = percent;
        }

        /// <summary>
        /// Reads a rarity cell: a percent with or without a % sign, or a tier name in any case,
        /// with spaces, dashes and underscores ignored.
        /// </summary>
        public static bool TryParseRarity(string value, out double? percent, out string tier)
        {
            percent = null;
            tier = null;
            var normalized = NormalizeText(value);
            if (normalized == null)
            {
                return false;
            }

            var percentText = normalized.TrimEnd('%').Trim();
            if (TryParseDouble(percentText, out var parsed))
            {
                if (parsed < 0 || parsed > 100)
                {
                    return false;
                }

                percent = parsed;
                return true;
            }

            var compact = new string(normalized
                .Where(c => c != ' ' && c != '-' && c != '_')
                .ToArray());
            // Matched by name, not Enum.TryParse, which also takes numbers and comma lists.
            tier = Enum.GetNames(typeof(RarityTier))
                .FirstOrDefault(name => string.Equals(name, compact, StringComparison.OrdinalIgnoreCase));
            return tier != null;
        }

        /// <summary>Lowercase bronze, silver, gold or platinum, or null for anything else.</summary>
        public static string NormalizeTrophyType(string value)
        {
            var normalized = NormalizeText(value)?.ToLowerInvariant();
            switch (normalized)
            {
                case "bronze":
                case "silver":
                case "gold":
                case "platinum":
                    return normalized;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Reads a category cell written as its display path ("Parent &gt; Child") into the stored
        /// path. Null for a blank cell.
        /// </summary>
        public static string ParseCategoryPath(string value)
        {
            var normalized = NormalizeText(value);
            if (normalized == null)
            {
                return null;
            }

            return CategoryPathHelper.JoinRaw(normalized.Split('>'));
        }

        private static void ApplyField(
            CustomAchievementCsvParseResult result,
            CustomAchievementCsvRow row,
            LinkedFileField field,
            string column,
            string rawValue,
            int rowNumber,
            string iconBaseDirectory)
        {
            var value = NormalizeText(rawValue);
            if (field == LinkedFileField.Ignore || value == null)
            {
                return;
            }

            void Error(string message) =>
                result.Errors.Add($"Row {rowNumber}, {column}: \"{value}\" {message}");

            switch (field)
            {
                case LinkedFileField.ApiName:
                    row.Id = CustomAchievementProjectionService.NormalizeId(value);
                    break;
                case LinkedFileField.DisplayName:
                    row.DisplayName = value;
                    break;
                case LinkedFileField.Description:
                    row.Description = value;
                    break;
                case LinkedFileField.Points:
                    if (TryParseInt(value, out var points) && points >= 0)
                    {
                        row.Points = points;
                    }
                    else
                    {
                        Error("is not a whole number of 0 or more.");
                    }
                    break;
                case LinkedFileField.TrophyType:
                    row.TrophyType = NormalizeTrophyType(value);
                    if (row.TrophyType == null)
                    {
                        Error(TrophyTypeMessage);
                    }
                    break;
                case LinkedFileField.Hidden:
                    if (TryParseBoolean(value, out var hidden))
                    {
                        row.Hidden = hidden;
                    }
                    else
                    {
                        Error("is not true or false.");
                    }
                    break;
                case LinkedFileField.Rarity:
                    if (TryParseRarity(value, out var percent, out var tier))
                    {
                        row.RarityPercent = percent;
                        row.RarityTier = tier;
                    }
                    else
                    {
                        Error(RarityMessage);
                    }
                    break;
                case LinkedFileField.Category:
                    row.Category = ParseCategoryPath(value);
                    break;
                case LinkedFileField.ProgressNum:
                    if (TryParseInt(value, out var progress) && progress >= 0)
                    {
                        row.ProgressNum = progress;
                    }
                    else
                    {
                        Error("is not a whole number of 0 or more.");
                    }
                    break;
                case LinkedFileField.ProgressDenom:
                    if (TryParseInt(value, out var total) && total > 0)
                    {
                        row.ProgressDenom = total;
                    }
                    else
                    {
                        Error("is not a whole number greater than 0.");
                    }
                    break;
                case LinkedFileField.Unlocked:
                    if (TryParseBoolean(value, out var unlocked))
                    {
                        row.Unlocked = unlocked;
                    }
                    else
                    {
                        Error("is not true or false.");
                    }
                    break;
                case LinkedFileField.UnlockTime:
                    if (TryParseUnlockTimeUtc(value, out var unlockTimeUtc))
                    {
                        row.UnlockTimeUtc = unlockTimeUtc;
                    }
                    else
                    {
                        Error("is not a date and time.");
                    }
                    break;
                case LinkedFileField.UnlockedIconPath:
                case LinkedFileField.LockedIconPath:
                    var icon = ResolveIconSource(value, iconBaseDirectory);
                    if (icon == null)
                    {
                        Error("is not a web address or a file that exists.");
                    }
                    else if (field == LinkedFileField.UnlockedIconPath)
                    {
                        row.UnlockedIconPath = icon;
                    }
                    else
                    {
                        row.LockedIconPath = icon;
                    }
                    break;
            }
        }

        /// <summary>
        /// An icon cell as the source to fetch it from: a web address as written, or a local file
        /// as a full path. Null when it is neither. Without a base folder the cell is kept as
        /// written.
        /// </summary>
        private static string ResolveIconSource(string value, string baseDirectory)
        {
            return LinkedFiles.LinkedFileValueParser.ResolveIconSource(value, baseDirectory);
        }

        private static bool TryParseInt(string value, out int parsed)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ||
                   int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out parsed);
        }

        private static bool TryParseDouble(string value, out double parsed)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                   double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed);
        }

        private static bool TryParseBoolean(string value, out bool parsed)
        {
            parsed = false;
            switch (NormalizeHeader(value))
            {
                case "true":
                case "yes":
                case "y":
                case "1":
                case "unlocked":
                case "earned":
                    parsed = true;
                    return true;
                case "false":
                case "no":
                case "n":
                case "0":
                case "locked":
                    parsed = false;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads an unlock time as local time unless it carries an offset or Z. The current
        /// culture is tried first because a spreadsheet saves dates in the user's own format.
        /// </summary>
        private static bool TryParseUnlockTimeUtc(string value, out DateTime utc)
        {
            const DateTimeStyles styles =
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces;
            if (DateTime.TryParse(value, CultureInfo.CurrentCulture, styles, out utc) ||
                DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out utc))
            {
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                return true;
            }

            return false;
        }

        private static bool IsEmptyRow(IEnumerable<string> row)
        {
            return row == null || row.All(value => string.IsNullOrWhiteSpace(value));
        }

        private static string DescribeDelimiter(char delimiter)
        {
            return delimiter == '\t' ? "tab" : "\"" + delimiter + "\"";
        }

        private static string NormalizeHeader(string value)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var builder = new StringBuilder(normalized.Length);
            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.ToString();
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
