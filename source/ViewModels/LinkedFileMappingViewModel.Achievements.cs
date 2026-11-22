using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The Link File window's achievements list: each of the game's achievements, the file row it
    /// pairs with, and what that row says, so the user checks the result against achievements
    /// they know. An achievement's row cell pairs it with a row by hand.
    /// </summary>
    public sealed partial class LinkedFileMappingViewModel
    {
        private static readonly HashSet<LinkedFileField> DetailFields = new HashSet<LinkedFileField>
        {
            LinkedFileField.DisplayName, LinkedFileField.Description, LinkedFileField.Hidden,
            LinkedFileField.UnlockedIconPath, LinkedFileField.LockedIconPath, LinkedFileField.Points,
            LinkedFileField.TrophyType, LinkedFileField.Rarity, LinkedFileField.Category
        };

        private readonly LinkedFileRowPickContext _rowPick;
        private readonly Dictionary<string, LinkedFileAchievementRow> _achievementRowsByKey =
            new Dictionary<string, LinkedFileAchievementRow>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Rows the user set to pair with none from an achievement's dropdown, by API name, so Automatic undoes them.</summary>
        private readonly Dictionary<string, string> _skippedFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<LinkedFileAchievementRow> _achievementRows = Array.Empty<LinkedFileAchievementRow>();
        private IReadOnlyList<LinkedFileValueColumn> _valueColumns = Array.Empty<LinkedFileValueColumn>();
        private bool _showsDetailsHint;

        public IReadOnlyList<LinkedFileAchievementRow> AchievementRows
        {
            get => _achievementRows;
            private set => SetValue(ref _achievementRows, value);
        }

        /// <summary>The columns beside each achievement: the status, then each mapped field.</summary>
        public IReadOnlyList<LinkedFileValueColumn> ValueColumns => _valueColumns;

        /// <summary>Raised after <see cref="ValueColumns"/> changed, so the view regenerates the list's columns.</summary>
        public event EventHandler ValueColumnsRebuilt;

        /// <summary>
        /// Whether a column feeds a name, description or other detail on a game whose details the
        /// file cannot change: they apply to custom achievements only.
        /// </summary>
        public bool ShowsDetailsHint
        {
            get => _showsDetailsHint;
            private set => SetValue(ref _showsDetailsHint, value);
        }

        private void BuildAchievementRows(
            IReadOnlyList<AchievementDetail> achievements,
            LinkedFileMatchResult match,
            LinkedFileTable table,
            LinkedFileSchema schema,
            GameCustomDataFile record)
        {
            var columns = BuildValueColumns(schema);
            if (!columns.Select(column => column.Field).SequenceEqual(_valueColumns.Select(column => column.Field)))
            {
                _valueColumns = columns;
                OnPropertyChanged(nameof(ValueColumns));
                ValueColumnsRebuilt?.Invoke(this, EventArgs.Empty);
            }

            ShowsDetailsHint = !_createUnmatched &&
                               schema.Columns?.Any(mapping => DetailFields.Contains(mapping.Field)) == true &&
                               !achievements.Any(achievement => achievement?.IsCustom == true);

            var rowByAchievement = new Dictionary<AchievementDetail, int>();
            foreach (var paired in match.Matches)
            {
                rowByAchievement[paired.Achievement] = paired.RowIndex;
            }

            _rowPick.Rows = BuildRowOptions(table, schema, match);
            var handPaired = new HashSet<string>(_pairings.Values.Where(value => !string.IsNullOrEmpty(value)), StringComparer.OrdinalIgnoreCase);
            var directory = SafeDirectory(_filePath);
            var now = DateTime.UtcNow;
            var rows = new List<LinkedFileAchievementRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements.Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName)))
            {
                var apiName = achievement.ApiName.Trim();
                if (!seen.Add(apiName))
                {
                    continue;
                }

                if (!_achievementRowsByKey.TryGetValue(apiName, out var row))
                {
                    row = new LinkedFileAchievementRow(apiName);
                    _achievementRowsByKey[apiName] = row;
                }

                var mask = AchievementSpoilerMask.Apply(achievement, SpoilersFor(achievement), L);
                string rowText;
                string[] values;
                var isMatched = rowByAchievement.TryGetValue(achievement, out var rowIndex);
                if (isMatched)
                {
                    var parsed = LinkedFileRowValues.Parse(table, rowIndex, schema, directory, now);
                    rowText = DescribeRowLong(table, schema, rowIndex);
                    values = columns.Select(column => ValueText(column.Field, parsed, table, schema, rowIndex)).ToArray();
                }
                else
                {
                    rowText = L("LOCPlayAch_LinkedFile_NoMatch");
                    values = null;
                }

                row.Set(
                    mask.Name,
                    mask.Description,
                    mask.Icon,
                    rowText,
                    isMatched,
                    handPaired.Contains(apiName) || _skippedFor.ContainsKey(apiName),
                    values,
                    _rowPick);
                rows.Add(row);
            }

            if (!rows.SequenceEqual(_achievementRows))
            {
                AchievementRows = rows;
            }
        }

        private List<LinkedFileValueColumn> BuildValueColumns(LinkedFileSchema schema)
        {
            var columns = new List<LinkedFileValueColumn> { new LinkedFileValueColumn(LinkedFileField.Unlocked, L("LOCPlayAch_Column_Status")) };
            foreach (var mapping in schema.Columns ?? new List<LinkedFileColumnMapping>())
            {
                if (mapping.Field == LinkedFileField.Ignore || mapping.Field == LinkedFileField.Unlocked)
                {
                    continue;
                }

                var name = FieldOptions.FirstOrDefault(option => option.Value == mapping.Field)?.Name ?? mapping.Field.ToString();
                columns.Add(new LinkedFileValueColumn(mapping.Field, name));
            }

            return columns;
        }

        /// <summary>The status and unlock time as the plugin reads them; any other field as the file writes it.</summary>
        private string ValueText(LinkedFileField field, LinkedFileRowValues values, LinkedFileTable table, LinkedFileSchema schema, int rowIndex)
        {
            switch (field)
            {
                case LinkedFileField.Unlocked:
                    return values.HasUnlockInfo
                        ? L(values.Unlocked ? "LOCPlayAch_Common_Unlocked" : "LOCPlayAch_Common_Locked")
                        : string.Empty;
                case LinkedFileField.UnlockTime when values.UnlockTimeUtc.HasValue:
                    return values.UnlockTimeUtc.Value.ToLocalTime().ToString("g", FormattingCulture.Current);
            }

            if (!schema.TryGetColumn(field, out var column))
            {
                return string.Empty;
            }

            var index = table.IndexOfColumn(column);
            return index < 0 ? string.Empty : (table.Cell(rowIndex, index) ?? string.Empty).Trim();
        }

        /// <summary>The file's rows for an achievement's dropdown, each naming the achievement it pairs with now.</summary>
        private IReadOnlyList<MatchOption> BuildRowOptions(LinkedFileTable table, LinkedFileSchema schema, LinkedFileMatchResult match)
        {
            var achievementByRow = new Dictionary<int, AchievementDetail>();
            foreach (var paired in match.Matches)
            {
                achievementByRow[paired.RowIndex] = paired.Achievement;
            }

            var options = new List<MatchOption>(table.Rows.Count);
            for (var row = 0; row < table.Rows.Count; row++)
            {
                var option = MatchOption.Create(
                    LinkedFileMatcher.RowKey(table, schema, row),
                    DescribeRowLong(table, schema, row),
                    DescribeCells(table, row),
                    icon: null,
                    list: null);
                if (achievementByRow.TryGetValue(row, out var achievement))
                {
                    var mask = AchievementSpoilerMask.Apply(achievement, SpoilersFor(achievement), L);
                    option.SetPairedWith(mask.Name, mask.Icon);
                }

                options.Add(option);
            }

            return options;
        }

        /// <summary>The row's number, then its match cell when that names it otherwise.</summary>
        private static string DescribeRowLong(LinkedFileTable table, LinkedFileSchema schema, int rowIndex)
        {
            var number = "#" + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
            var described = DescribeRow(table, schema, rowIndex);
            return string.Equals(described, number, StringComparison.Ordinal) ? number : number + " · " + described;
        }

        /// <summary>The row's own cells, without the position column, for the dropdown's tooltip and search.</summary>
        private static string DescribeCells(LinkedFileTable table, int rowIndex)
        {
            var cells = new List<string>();
            for (var column = 0; column < table.Columns.Count; column++)
            {
                if (string.Equals(table.Columns[column], LinkedFileSchema.PositionColumn, StringComparison.Ordinal))
                {
                    continue;
                }

                var cell = table.Cell(rowIndex, column)?.Trim();
                if (!string.IsNullOrEmpty(cell))
                {
                    cells.Add(cell);
                }
            }

            var text = string.Join(" · ", cells);
            return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
        }

        /// <summary>
        /// Stages a pairing from an achievement's dropdown: a row key, "" for none, or null to
        /// leave the achievement to matching. A row that another way of matching pairs with the
        /// achievement on its own switches to that way instead.
        /// </summary>
        private void PairAchievement(string apiName, string rowKey)
        {
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return;
            }

            if (!string.IsNullOrEmpty(rowKey) && TryLearnMatch(rowKey, apiName))
            {
                return;
            }

            foreach (var key in _pairings.Where(pair => string.Equals(pair.Value, apiName, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList())
            {
                _pairings.Remove(key);
            }

            if (_skippedFor.TryGetValue(apiName, out var skipped))
            {
                if (_pairings.TryGetValue(skipped, out var value) && value.Length == 0)
                {
                    _pairings.Remove(skipped);
                }

                _skippedFor.Remove(apiName);
            }

            if (rowKey == null)
            {
                Recompute();
                return;
            }

            if (rowKey.Length == 0)
            {
                // The row it pairs with on its own now pairs with nothing.
                var current = _lastMatch?.Matches.FirstOrDefault(paired =>
                    string.Equals(paired.Achievement.ApiName?.Trim(), apiName, StringComparison.OrdinalIgnoreCase));
                var table = _lastRead?.Success == true ? _lastRead.Table : null;
                if (current != null && table != null)
                {
                    var key = LinkedFileMatcher.RowKey(table, BuildSchema(), current.RowIndex);
                    _pairings[key] = string.Empty;
                    _skippedFor[apiName] = key;
                }

                Recompute();
                return;
            }

            _pairings[rowKey] = apiName;
            Recompute();
        }
    }
}
