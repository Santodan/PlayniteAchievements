using PlayniteAchievements.Models.Settings;
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

    public sealed class CustomAchievementTextImportService
    {
        private enum Field
        {
            Unknown,
            Id,
            DisplayName,
            Description,
            Unlocked,
            UnlockTimeUtc,
            UnlockedIconPath,
            LockedIconPath,
            Points,
            TrophyType,
            Hidden,
            Rarity,
            GlobalPercentUnlocked,
            ProgressNum,
            ProgressDenom
        }

        private static readonly Dictionary<string, Field> HeaderAliases =
            new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = Field.Id,
                ["customid"] = Field.Id,
                ["api"] = Field.Id,
                ["apiname"] = Field.Id,
                ["name"] = Field.DisplayName,
                ["title"] = Field.DisplayName,
                ["achievement"] = Field.DisplayName,
                ["achievementname"] = Field.DisplayName,
                ["displayname"] = Field.DisplayName,
                ["description"] = Field.Description,
                ["desc"] = Field.Description,
                ["details"] = Field.Description,
                ["unlocked"] = Field.Unlocked,
                ["earned"] = Field.Unlocked,
                ["done"] = Field.Unlocked,
                ["unlocktime"] = Field.UnlockTimeUtc,
                ["unlocktimeutc"] = Field.UnlockTimeUtc,
                ["unlockedat"] = Field.UnlockTimeUtc,
                ["earnedat"] = Field.UnlockTimeUtc,
                ["dateunlocked"] = Field.UnlockTimeUtc,
                ["points"] = Field.Points,
                ["score"] = Field.Points,
                ["gamerscore"] = Field.Points,
                ["trophy"] = Field.TrophyType,
                ["trophytype"] = Field.TrophyType,
                ["hidden"] = Field.Hidden,
                ["secret"] = Field.Hidden,
                ["rarity"] = Field.Rarity,
                ["percent"] = Field.GlobalPercentUnlocked,
                ["globalpercent"] = Field.GlobalPercentUnlocked,
                ["globalpercentunlocked"] = Field.GlobalPercentUnlocked,
                ["percentunlocked"] = Field.GlobalPercentUnlocked,
                ["progress"] = Field.ProgressNum,
                ["progressnum"] = Field.ProgressNum,
                ["current"] = Field.ProgressNum,
                ["progressdenom"] = Field.ProgressDenom,
                ["progresstotal"] = Field.ProgressDenom,
                ["total"] = Field.ProgressDenom,
                ["goal"] = Field.ProgressDenom,
                ["icon"] = Field.UnlockedIconPath,
                ["unlockedicon"] = Field.UnlockedIconPath,
                ["unlockediconpath"] = Field.UnlockedIconPath,
                ["lockedicon"] = Field.LockedIconPath,
                ["lockediconpath"] = Field.LockedIconPath
            };

        public CustomAchievementTextImportResult Import(string text)
        {
            var result = new CustomAchievementTextImportResult();
            var rows = ParseRows(text);
            if (rows.Count == 0)
            {
                result.Errors.Add("No rows were found.");
                return result;
            }

            var header = rows[0];
            var mapping = header
                .Select(cell => ResolveField(cell))
                .ToList();

            if (!mapping.Contains(Field.DisplayName))
            {
                result.Errors.Add("A title/name/displayName column is required.");
                return result;
            }

            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (IsEmptyRow(row))
                {
                    continue;
                }

                var definition = new CustomAchievementDefinition();
                var rowErrorCount = result.Errors.Count;
                for (var columnIndex = 0; columnIndex < row.Count && columnIndex < mapping.Count; columnIndex++)
                {
                    ApplyField(
                        result,
                        definition,
                        mapping[columnIndex],
                        row[columnIndex],
                        rowIndex + 1);
                }

                definition.DisplayName = NormalizeText(definition.DisplayName);
                if (string.IsNullOrWhiteSpace(definition.DisplayName))
                {
                    result.Errors.Add($"Row {rowIndex + 1}: title/name is required.");
                    continue;
                }

                if (result.Errors.Count > rowErrorCount)
                {
                    continue;
                }

                definition.Id = CustomAchievementProjectionService.NormalizeId(definition.Id);
                if (string.IsNullOrWhiteSpace(definition.Id))
                {
                    definition.Id = CustomAchievementProjectionService.GenerateId(definition.DisplayName, usedIds);
                }

                if (!usedIds.Add(definition.Id))
                {
                    result.Errors.Add($"Row {rowIndex + 1}: duplicate custom ID '{definition.Id}'.");
                    continue;
                }

                result.Definitions.Add(definition);
            }

            if (result.Definitions.Count == 0 && !result.HasErrors)
            {
                result.Errors.Add("No importable achievements were found.");
            }

            return result;
        }

        private static Field ResolveField(string header)
        {
            var normalized = NormalizeHeader(header);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   HeaderAliases.TryGetValue(normalized, out var field)
                ? field
                : Field.Unknown;
        }

        private static void ApplyField(
            CustomAchievementTextImportResult result,
            CustomAchievementDefinition definition,
            Field field,
            string rawValue,
            int rowNumber)
        {
            var value = NormalizeText(rawValue);
            if (field == Field.Unknown || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            switch (field)
            {
                case Field.Id:
                    definition.Id = value;
                    break;
                case Field.DisplayName:
                    definition.DisplayName = value;
                    break;
                case Field.Description:
                    definition.Description = value;
                    break;
                case Field.Unlocked:
                    if (TryParseBoolean(value, out var unlocked))
                    {
                        definition.Unlocked = unlocked;
                    }
                    else
                    {
                        result.Errors.Add($"Row {rowNumber}: unlocked must be true/false, yes/no, or 1/0.");
                    }
                    break;
                case Field.UnlockTimeUtc:
                    if (TryParseDateTime(value, out var unlockedAt))
                    {
                        definition.UnlockTimeUtc = unlockedAt;
                        definition.Unlocked = true;
                    }
                    else
                    {
                        result.Errors.Add($"Row {rowNumber}: unlock time is not a valid date/time.");
                    }
                    break;
                case Field.UnlockedIconPath:
                    definition.UnlockedIconPath = value;
                    break;
                case Field.LockedIconPath:
                    definition.LockedIconPath = value;
                    break;
                case Field.Points:
                    definition.Points = ParseNonNegativeInt(result, value, rowNumber, "points");
                    break;
                case Field.TrophyType:
                    definition.TrophyType = value;
                    break;
                case Field.Hidden:
                    if (TryParseBoolean(value, out var hidden))
                    {
                        definition.Hidden = hidden;
                    }
                    else
                    {
                        result.Errors.Add($"Row {rowNumber}: hidden must be true/false, yes/no, or 1/0.");
                    }
                    break;
                case Field.Rarity:
                    definition.Rarity = value;
                    break;
                case Field.GlobalPercentUnlocked:
                    definition.GlobalPercentUnlocked = ParsePercent(result, value, rowNumber);
                    break;
                case Field.ProgressNum:
                    definition.ProgressNum = ParseNonNegativeInt(result, value, rowNumber, "progress");
                    break;
                case Field.ProgressDenom:
                    definition.ProgressDenom = ParsePositiveInt(result, value, rowNumber, "progress total");
                    break;
            }
        }

        private static int? ParseNonNegativeInt(
            CustomAchievementTextImportResult result,
            string value,
            int rowNumber,
            string label)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= 0)
            {
                return parsed;
            }

            result.Errors.Add($"Row {rowNumber}: {label} must be a non-negative integer.");
            return null;
        }

        private static int? ParsePositiveInt(
            CustomAchievementTextImportResult result,
            string value,
            int rowNumber,
            string label)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                return parsed;
            }

            result.Errors.Add($"Row {rowNumber}: {label} must be a positive integer.");
            return null;
        }

        private static double? ParsePercent(
            CustomAchievementTextImportResult result,
            string value,
            int rowNumber)
        {
            var normalized = value.Trim().TrimEnd('%');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= 0 &&
                parsed <= 100)
            {
                return parsed;
            }

            result.Errors.Add($"Row {rowNumber}: percent must be between 0 and 100.");
            return null;
        }

        private static bool TryParseBoolean(string value, out bool parsed)
        {
            parsed = false;
            var normalized = NormalizeHeader(value);
            switch (normalized)
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
                case "notunlocked":
                    parsed = false;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseDateTime(string value, out DateTime utc)
        {
            utc = default;
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                    out var offset))
            {
                utc = offset.UtcDateTime;
                return true;
            }

            if (DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
            {
                utc = parsed.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : parsed.ToUniversalTime();
                return true;
            }

            return false;
        }

        private static bool IsEmptyRow(IEnumerable<string> row)
        {
            return row == null || row.All(value => string.IsNullOrWhiteSpace(value));
        }

        private static List<List<string>> ParseRows(string text)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return rows;
            }

            var delimiter = DetectDelimiter(text);
            var row = new List<string>();
            var cell = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                    continue;
                }

                if (c == delimiter)
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    continue;
                }

                cell.Append(c);
            }

            row.Add(cell.ToString());
            if (!IsEmptyRow(row))
            {
                rows.Add(row);
            }

            return rows.Where(rowValues => !IsEmptyRow(rowValues)).ToList();
        }

        private static char DetectDelimiter(string text)
        {
            var header = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r' || c == '\n')
                {
                    break;
                }

                header.Append(c);
            }

            var headerText = header.ToString();
            return CountOutsideQuotes(headerText, '\t') > CountOutsideQuotes(headerText, ',')
                ? '\t'
                : ',';
        }

        private static int CountOutsideQuotes(string value, char delimiter)
        {
            var count = 0;
            var inQuotes = false;
            for (var i = 0; i < (value?.Length ?? 0); i++)
            {
                var c = value[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < value.Length && value[i + 1] == '"')
                    {
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (!inQuotes && c == delimiter)
                {
                    count++;
                }
            }

            return count;
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
