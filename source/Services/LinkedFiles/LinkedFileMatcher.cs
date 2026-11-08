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

        /// <summary>Achievements no row matched, in the game's order; not those the user paired with no row.</summary>
        public List<AchievementDetail> UnmatchedAchievements { get; } = new List<AchievementDetail>();

        /// <summary>Why a row or achievement was left out, for the log and the mapping window.</summary>
        public List<string> Diagnostics { get; } = new List<string>();
    }

    /// <summary>
    /// Pairs file rows with achievements. The user's own pairings come first, by achievement.
    /// Then API names match, exactly (a custom achievement
    /// also answers to its bare id), then display names by their normalized text, each over the
    /// rows and achievements still unmatched. With neither column mapped, rows pair by order
    /// when the file has no more rows than the game has achievements.
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

            if (schema.MatchesByRowOrder)
            {
                MatchByRowOrder(table, candidates, rowTaken, achievementTaken, result);
            }
            else
            {
                MatchByColumn(
                    table, schema, LinkedFileField.ApiName, candidates, rowTaken, achievementTaken, result,
                    LinkedFileMatchKey.ApiName, ApiNameKeys, ApiNameRowKeys, StringComparer.OrdinalIgnoreCase);
                MatchByColumn(
                    table, schema, LinkedFileField.DisplayName, candidates, rowTaken, achievementTaken, result,
                    LinkedFileMatchKey.DisplayName, DisplayNameKeys, DisplayNameRowKeys, StringComparer.Ordinal);
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

        private static void MatchByColumn(
            LinkedFileTable table,
            LinkedFileSchema schema,
            LinkedFileField field,
            IReadOnlyList<AchievementDetail> candidates,
            bool[] rowTaken,
            HashSet<AchievementDetail> achievementTaken,
            LinkedFileMatchResult result,
            LinkedFileMatchKey matchedBy,
            Func<AchievementDetail, IEnumerable<string>> achievementKeys,
            Func<string, IEnumerable<string>> rowKeys,
            StringComparer comparer)
        {
            if (!schema.TryGetColumn(field, out var column))
            {
                return;
            }

            var columnIndex = table.IndexOfColumn(column);
            if (columnIndex < 0)
            {
                return;
            }

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

        private static void MatchByRowOrder(
            LinkedFileTable table,
            IReadOnlyList<AchievementDetail> candidates,
            bool[] rowTaken,
            HashSet<AchievementDetail> achievementTaken,
            LinkedFileMatchResult result)
        {
            if (table.Rows.Count == 0)
            {
                return;
            }

            if (table.Rows.Count > candidates.Count)
            {
                result.Diagnostics.Add(
                    "Row order was not used because the file has " + table.Rows.Count.ToString(CultureInfo.InvariantCulture) +
                    " rows and the game has " + candidates.Count.ToString(CultureInfo.InvariantCulture) + " achievements.");
                return;
            }

            // Position still decides: row N pairs with achievement N, unless either was paired by hand.
            for (var row = 0; row < table.Rows.Count; row++)
            {
                var achievement = candidates[row];
                if (rowTaken[row] || achievementTaken.Contains(achievement))
                {
                    continue;
                }

                rowTaken[row] = true;
                achievementTaken.Add(achievement);
                result.Matches.Add(new LinkedFileRowMatch { RowIndex = row, Achievement = achievement, MatchedBy = LinkedFileMatchKey.RowOrder });
            }
        }

        /// <summary>
        /// The key a hand pairing is stored under: the row's API name cell, else its name cell,
        /// else its position, so a pairing follows the row when the file's order changes and
        /// either column is mapped.
        /// </summary>
        public static string RowKey(LinkedFileTable table, LinkedFileSchema schema, int rowIndex)
        {
            var apiName = CellText(table, schema, LinkedFileField.ApiName, rowIndex);
            if (apiName != null)
            {
                return "id:" + apiName;
            }

            var name = CellText(table, schema, LinkedFileField.DisplayName, rowIndex);
            if (name != null)
            {
                return "name:" + name;
            }

            return "row:" + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
        }

        private static string CellText(LinkedFileTable table, LinkedFileSchema schema, LinkedFileField field, int rowIndex)
        {
            if (table == null || schema == null || !schema.TryGetColumn(field, out var column))
            {
                return null;
            }

            var columnIndex = table.IndexOfColumn(column);
            var text = columnIndex < 0 ? null : table.Cell(rowIndex, columnIndex)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// Applies the user's pairings, by achievement API name: a row key, or "" for an achievement
        /// that takes no row. A key on more than one row names the first; a pairing whose row is
        /// gone, or already taken, leaves the achievement to automatic matching.
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
            var pairingByApiName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairings)
            {
                var apiName = pair.Key?.Trim();
                if (!string.IsNullOrEmpty(apiName) && !pairingByApiName.ContainsKey(apiName))
                {
                    pairingByApiName[apiName] = pair.Value?.Trim() ?? string.Empty;
                }
            }

            var rowByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var row = 0; row < rowTaken.Length; row++)
            {
                var key = RowKey(table, schema, row);
                if (!rowByKey.ContainsKey(key))
                {
                    rowByKey[key] = row;
                }
            }

            foreach (var achievement in candidates)
            {
                var apiName = achievement.ApiName?.Trim();
                if (string.IsNullOrEmpty(apiName) ||
                    achievementTaken.Contains(achievement) ||
                    !pairingByApiName.TryGetValue(apiName, out var rowKey))
                {
                    continue;
                }

                if (rowKey.Length == 0)
                {
                    achievementTaken.Add(achievement);
                    continue;
                }

                if (!rowByKey.TryGetValue(rowKey, out var row))
                {
                    result.Diagnostics.Add("'" + apiName + "' is paired with a row the file does not have.");
                    continue;
                }

                if (rowTaken[row])
                {
                    result.Diagnostics.Add("'" + apiName + "': " + Describe(row) + " is already paired with another achievement.");
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
