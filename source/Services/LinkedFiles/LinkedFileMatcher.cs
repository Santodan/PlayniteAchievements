using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    public sealed class LinkedFileRowMatch
    {
        public int RowIndex { get; set; }

        public AchievementDetail Achievement { get; set; }

        public LinkedFileMatchKey MatchedBy { get; set; }
    }

    public sealed class LinkedFileMatchResult
    {
        public List<LinkedFileRowMatch> Matches { get; } = new List<LinkedFileRowMatch>();

        /// <summary>Row indexes that matched no achievement, in file order.</summary>
        public List<int> UnmatchedRows { get; } = new List<int>();

        /// <summary>Row indexes the user paired with nothing; they are neither matched nor created.</summary>
        public List<int> SkippedRows { get; } = new List<int>();

        /// <summary>Achievements no row matched, in the game's order.</summary>
        public List<AchievementDetail> UnmatchedAchievements { get; } = new List<AchievementDetail>();

        /// <summary>Why a row or achievement was left out, for the log and the mapping window.</summary>
        public List<string> Diagnostics { get; } = new List<string>();
    }

    /// <summary>
    /// Pairs file rows with achievements. The user's own pairings come first, by row key. Then
    /// the match column's cells pair the remaining rows, read as the mapping's
    /// <see cref="LinkedFileSchema.MatchBy"/> says: as API names, exactly (a custom achievement
    /// also answers to its bare id), as names by their normalized text, or as positions in the
    /// game's order. A mapping with no match column pairs by hand pairings only.
    /// A row matches at most one achievement and an achievement at most one row; anything
    /// ambiguous stays unmatched and is reported.
    /// </summary>
    public static class LinkedFileMatcher
    {
        public static LinkedFileMatchResult Match(
            LinkedFileTable table,
            LinkedFileSchema schema,
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyDictionary<string, string> pairings = null)
        {
            var result = new LinkedFileMatchResult();
            table = table ?? LinkedFileTable.Empty;
            schema = schema ?? new LinkedFileSchema();
            var candidates = (achievements ?? Array.Empty<AchievementDetail>()).Where(achievement => achievement != null).ToList();

            var rowTaken = new bool[table.Rows.Count];
            var achievementTaken = new HashSet<AchievementDetail>();

            if (pairings != null && pairings.Count > 0)
            {
                MatchByPairing(table, schema, pairings, candidates, rowTaken, achievementTaken, result);
            }

            var columnIndex = string.IsNullOrWhiteSpace(schema.MatchColumn) ? -1 : table.IndexOfColumn(schema.MatchColumn);
            if (columnIndex >= 0)
            {
                switch (schema.MatchBy)
                {
                    case LinkedFileMatchBy.ApiName:
                        MatchByKeys(
                            table, columnIndex, candidates, rowTaken, achievementTaken, result,
                            LinkedFileMatchKey.ApiName, ApiNameKeys, ApiNameRowKeys, StringComparer.OrdinalIgnoreCase);
                        break;
                    case LinkedFileMatchBy.Name:
                        MatchByKeys(
                            table, columnIndex, candidates, rowTaken, achievementTaken, result,
                            LinkedFileMatchKey.DisplayName, DisplayNameKeys, DisplayNameRowKeys, StringComparer.Ordinal);
                        break;
                    default:
                        MatchByPosition(
                            table, columnIndex, schema.MatchBy == LinkedFileMatchBy.PositionFromZero ? 0 : 1,
                            candidates, rowTaken, achievementTaken, result);
                        break;
                }
            }

            for (var row = 0; row < rowTaken.Length; row++)
            {
                if (!rowTaken[row])
                {
                    result.UnmatchedRows.Add(row);
                }
            }

            result.UnmatchedAchievements.AddRange(candidates.Where(achievement => !achievementTaken.Contains(achievement)));
            result.Matches.Sort((left, right) => left.RowIndex.CompareTo(right.RowIndex));
            return result;
        }

        private static void MatchByKeys(
            LinkedFileTable table,
            int columnIndex,
            IReadOnlyList<AchievementDetail> candidates,
            bool[] rowTaken,
            HashSet<AchievementDetail> achievementTaken,
            LinkedFileMatchResult result,
            LinkedFileMatchKey matchedBy,
            Func<AchievementDetail, IEnumerable<string>> achievementKeys,
            Func<string, IEnumerable<string>> rowKeys,
            StringComparer comparer)
        {
            // Achievements by key; a key shared by two achievements is ambiguous and matches nothing.
            var byKey = new Dictionary<string, List<AchievementDetail>>(comparer);
            foreach (var achievement in candidates.Where(candidate => !achievementTaken.Contains(candidate)))
            {
                foreach (var key in achievementKeys(achievement).Where(key => key != null).Distinct(comparer))
                {
                    if (!byKey.TryGetValue(key, out var list))
                    {
                        list = new List<AchievementDetail>();
                        byKey[key] = list;
                    }

                    if (!list.Contains(achievement))
                    {
                        list.Add(achievement);
                    }
                }
            }

            // Rows by their first key; two rows with the same key are ambiguous as well.
            var rowsByKey = new Dictionary<string, List<int>>(comparer);
            var primaryKeyByRow = new Dictionary<int, string>();
            for (var row = 0; row < rowTaken.Length; row++)
            {
                if (rowTaken[row])
                {
                    continue;
                }

                var primary = rowKeys(table.Cell(row, columnIndex)).FirstOrDefault(key => key != null);
                if (primary == null)
                {
                    continue;
                }

                primaryKeyByRow[row] = primary;
                if (!rowsByKey.TryGetValue(primary, out var rows))
                {
                    rows = new List<int>();
                    rowsByKey[primary] = rows;
                }

                rows.Add(row);
            }

            foreach (var row in primaryKeyByRow.Keys.OrderBy(row => row))
            {
                var primary = primaryKeyByRow[row];
                if (rowsByKey[primary].Count > 1)
                {
                    result.Diagnostics.Add(Describe(row) + ": '" + table.Cell(row, columnIndex) + "' appears on more than one row.");
                    continue;
                }

                AchievementDetail matched = null;
                var ambiguous = false;
                foreach (var key in rowKeys(table.Cell(row, columnIndex)).Where(key => key != null))
                {
                    if (!byKey.TryGetValue(key, out var list))
                    {
                        continue;
                    }

                    var open = list.Where(achievement => !achievementTaken.Contains(achievement)).ToList();
                    if (open.Count == 1)
                    {
                        matched = open[0];
                        break;
                    }

                    if (open.Count > 1)
                    {
                        ambiguous = true;
                        break;
                    }
                }

                if (ambiguous)
                {
                    result.Diagnostics.Add(Describe(row) + ": '" + table.Cell(row, columnIndex) + "' matches more than one achievement.");
                    continue;
                }

                if (matched == null)
                {
                    continue;
                }

                rowTaken[row] = true;
                achievementTaken.Add(matched);
                result.Matches.Add(new LinkedFileRowMatch { RowIndex = row, Achievement = matched, MatchedBy = matchedBy });
            }
        }

        /// <summary>
        /// Pairs each row with the achievement at the position its cell gives, counting from
        /// <paramref name="first"/>. A number outside the list, or on more than one row, pairs nothing.
        /// </summary>
        private static void MatchByPosition(
            LinkedFileTable table,
            int columnIndex,
            int first,
            IReadOnlyList<AchievementDetail> candidates,
            bool[] rowTaken,
            HashSet<AchievementDetail> achievementTaken,
            LinkedFileMatchResult result)
        {
            // Every row's number counts toward duplicates, paired or not.
            var numberByRow = new Dictionary<int, int>();
            var rowsByNumber = new Dictionary<int, int>();
            for (var row = 0; row < rowTaken.Length; row++)
            {
                var cell = table.Cell(row, columnIndex);
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                if (!LinkedFileValueParser.TryParseInt(cell, out var number) || number < 0)
                {
                    if (!rowTaken[row])
                    {
                        result.Diagnostics.Add(Describe(row) + ": '" + cell.Trim() + "' is not a position.");
                    }

                    continue;
                }

                numberByRow[row] = number;
                rowsByNumber.TryGetValue(number, out var count);
                rowsByNumber[number] = count + 1;
            }

            foreach (var row in numberByRow.Keys.OrderBy(row => row))
            {
                if (rowTaken[row])
                {
                    continue;
                }

                var number = numberByRow[row];
                var text = number.ToString(CultureInfo.InvariantCulture);
                if (rowsByNumber[number] > 1)
                {
                    result.Diagnostics.Add(Describe(row) + ": position " + text + " appears on more than one row.");
                    continue;
                }

                var index = number - first;
                if (index < 0 || index >= candidates.Count)
                {
                    result.Diagnostics.Add(
                        Describe(row) + ": position " + text + " is outside the game's " +
                        candidates.Count.ToString(CultureInfo.InvariantCulture) + " achievements.");
                    continue;
                }

                var achievement = candidates[index];
                if (achievementTaken.Contains(achievement))
                {
                    result.Diagnostics.Add(Describe(row) + ": the achievement at position " + text + " is already paired with another row.");
                    continue;
                }

                rowTaken[row] = true;
                achievementTaken.Add(achievement);
                result.Matches.Add(new LinkedFileRowMatch { RowIndex = row, Achievement = achievement, MatchedBy = LinkedFileMatchKey.Order });
            }
        }

        /// <summary>
        /// The key a hand pairing is stored under: the row's match cell, prefixed by how it is
        /// read, else its position, so a pairing follows the row when the file's order changes.
        /// The prefixes are the ones keys had when API Name, Order and Name were separate fields,
        /// and the position column keeps "row:", so pairings saved then still find their row.
        /// </summary>
        public static string RowKey(LinkedFileTable table, LinkedFileSchema schema, int rowIndex)
        {
            var columnIndex = table == null || string.IsNullOrWhiteSpace(schema?.MatchColumn) ? -1 : table.IndexOfColumn(schema.MatchColumn);
            var text = columnIndex < 0 ? null : table.Cell(rowIndex, columnIndex)?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                switch (schema.MatchBy)
                {
                    case LinkedFileMatchBy.ApiName:
                        return "id:" + text;
                    case LinkedFileMatchBy.Name:
                        return "name:" + text;
                    default:
                        return string.Equals(schema.MatchColumn, LinkedFileSchema.PositionColumn, StringComparison.Ordinal)
                            ? "row:" + text
                            : "order:" + text;
                }
            }

            return "row:" + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Applies the user's pairings. A key on more than one row pairs only the first; a pairing
        /// whose achievement is gone, or already paired, leaves the row to automatic matching.
        /// </summary>
        private static void MatchByPairing(
            LinkedFileTable table,
            LinkedFileSchema schema,
            IReadOnlyDictionary<string, string> pairings,
            IReadOnlyList<AchievementDetail> candidates,
            bool[] rowTaken,
            HashSet<AchievementDetail> achievementTaken,
            LinkedFileMatchResult result)
        {
            var byApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in candidates)
            {
                var apiName = achievement.ApiName?.Trim();
                if (!string.IsNullOrEmpty(apiName) && !byApiName.ContainsKey(apiName))
                {
                    byApiName[apiName] = achievement;
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var row = 0; row < rowTaken.Length; row++)
            {
                var key = RowKey(table, schema, row);
                if (!pairings.TryGetValue(key, out var paired) || !seen.Add(key))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(paired))
                {
                    rowTaken[row] = true;
                    result.SkippedRows.Add(row);
                    continue;
                }

                if (!byApiName.TryGetValue(paired.Trim(), out var achievement))
                {
                    result.Diagnostics.Add(Describe(row) + " is paired with '" + paired + "', which the game does not have.");
                    continue;
                }

                if (achievementTaken.Contains(achievement))
                {
                    result.Diagnostics.Add(Describe(row) + ": '" + paired + "' is already paired with another row.");
                    continue;
                }

                rowTaken[row] = true;
                achievementTaken.Add(achievement);
                result.Matches.Add(new LinkedFileRowMatch { RowIndex = row, Achievement = achievement, MatchedBy = LinkedFileMatchKey.Paired });
            }
        }

        private static IEnumerable<string> ApiNameKeys(AchievementDetail achievement)
        {
            var apiName = achievement.ApiName?.Trim();
            if (string.IsNullOrEmpty(apiName))
            {
                yield break;
            }

            yield return apiName;
            if (achievement.IsCustom && CustomAchievementProjectionService.TryGetCustomId(apiName, out var id))
            {
                yield return id;
            }
        }

        private static IEnumerable<string> ApiNameRowKeys(string cell)
        {
            var trimmed = (cell ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                yield break;
            }

            yield return trimmed;
            if (!CustomAchievementProjectionService.IsCustomApiName(trimmed))
            {
                yield return CustomAchievementProjectionService.ApiNamePrefix + trimmed;
            }
        }

        private static IEnumerable<string> DisplayNameKeys(AchievementDetail achievement)
        {
            var normalized = CrossSourceAchievementMatcher.NormalizeText(achievement.DisplayName);
            if (normalized != null)
            {
                yield return normalized;
            }
        }

        private static IEnumerable<string> DisplayNameRowKeys(string cell)
        {
            var normalized = CrossSourceAchievementMatcher.NormalizeText(cell);
            if (normalized != null)
            {
                yield return normalized;
            }
        }

        private static string Describe(int rowIndex)
        {
            return "Row " + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
        }
    }
}
