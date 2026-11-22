using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// How the Link File window matches rows: the column whose cells say which achievement a row
    /// is and how they are read. A new link takes the best way the file offers, and keeps
    /// following the file until the user picks one; a stored link keeps its own. Pairing a row by
    /// hand with an achievement that another way pairs it with on its own switches to that way.
    /// </summary>
    public sealed partial class LinkedFileMappingViewModel
    {
        private string _matchColumn;
        private LinkedFileMatchBy _matchBy;

        /// <summary>The match was loaded or picked, so a new read does not replace it with the best.</summary>
        private bool _matchChosen;

        private IReadOnlyList<LinkedFileMatchCandidate> _candidates = Array.Empty<LinkedFileMatchCandidate>();
        private LinkedFileMatchChoice _selectedMatch;
        private string _matchSummary;

        /// <summary>
        /// Whether the game has achievements for rows to match. Without any, every row becomes a
        /// new achievement, so the window shows neither the match nor the achievements list; the
        /// match still follows the mapped columns, so later reads find what the first one added.
        /// </summary>
        public bool ShowsMatching => HasAchievements();

        /// <summary>The ways the file's rows can be matched, the best first, with what each pairs.</summary>
        public ObservableCollection<LinkedFileMatchChoice> MatchChoices { get; } = new ObservableCollection<LinkedFileMatchChoice>();

        public LinkedFileMatchChoice SelectedMatch
        {
            get => _selectedMatch;
            set
            {
                // Clearing the list sets the dropdown to nothing; that is not a choice.
                if (value == null || ReferenceEquals(value, _selectedMatch))
                {
                    return;
                }

                ChooseMatch(value.Column, value.By);
            }
        }

        /// <summary>How many achievements the rows pair with, leading into the match dropdown; null before a read.</summary>
        public string MatchSummary
        {
            get => _matchSummary;
            private set => SetValue(ref _matchSummary, value);
        }

        /// <summary>Tries every way to match the newly read rows and, unless one was chosen, takes the best.</summary>
        private void FindMatches(LinkedFileTable table)
        {
            var achievements = _services.LoadAchievements?.Invoke() ?? Array.Empty<AchievementDetail>();
            _candidates = LinkedFileMatchFinder.Find(table, achievements);
            if (!_matchChosen && table.Rows.Count > 0)
            {
                var best = LinkedFileMatchFinder.Best(_candidates);
                if (best != null)
                {
                    _matchColumn = best.Column;
                    _matchBy = best.By;
                }
                else
                {
                    MatchByMappedColumns();
                }
            }

            RebuildMatchChoices();
        }

        /// <summary>
        /// On a game with no achievements yet, which the file can only add to, nothing can be
        /// matched to try the ways out on: the rows are matched by their API Name column, else
        /// their Name column, as new achievements will be named. With achievements, no fitting
        /// way leaves the match for the user to choose.
        /// </summary>
        private void MatchByMappedColumns()
        {
            _matchColumn = null;
            _matchBy = LinkedFileMatchBy.ApiName;
            if (HasAchievements())
            {
                return;
            }

            var columns = BuildSchema().Columns;
            var apiName = columns?.FirstOrDefault(mapping => mapping.Field == LinkedFileField.ApiName)?.Column;
            var name = columns?.FirstOrDefault(mapping => mapping.Field == LinkedFileField.DisplayName)?.Column;
            _matchColumn = apiName ?? name;
            _matchBy = apiName != null ? LinkedFileMatchBy.ApiName : LinkedFileMatchBy.Name;
        }

        /// <summary>After a column's field changed: a match that follows the mapped columns follows them.</summary>
        private void FollowMappedColumns()
        {
            if (_matchChosen || _candidates.Count > 0 || HasAchievements())
            {
                return;
            }

            MatchByMappedColumns();
            RebuildMatchChoices();
        }

        /// <summary>Takes the user's way of matching, moving hand pairings to the keys it gives their rows.</summary>
        private void ChooseMatch(string column, LinkedFileMatchBy by)
        {
            var before = BuildSchema();
            _matchColumn = column;
            _matchBy = by;
            _matchChosen = true;
            RekeyPairings(before, BuildSchema());
            RebuildMatchChoices();
            OnSettingChanged(reread: false);
        }

        private void RebuildMatchChoices()
        {
            MatchChoices.Clear();
            foreach (var candidate in _candidates)
            {
                MatchChoices.Add(BuildChoice(candidate.Column, candidate.By, candidate.Matched));
            }

            // A stored way the file no longer bears out stays listed, so the dropdown shows it.
            if (_matchColumn != null && !MatchChoices.Any(choice => choice.Is(_matchColumn, _matchBy)))
            {
                MatchChoices.Add(BuildChoice(_matchColumn, _matchBy, matched: 0));
            }

            _selectedMatch = _matchColumn == null ? null : MatchChoices.FirstOrDefault(choice => choice.Is(_matchColumn, _matchBy));
            OnPropertyChanged(nameof(SelectedMatch));
        }

        private LinkedFileMatchChoice BuildChoice(string column, LinkedFileMatchBy by, int matched)
        {
            var count = string.Format(FormattingCulture.Current, L("LOCPlayAch_LinkedFile_Count_Matched"), matched);
            if (string.Equals(column, LinkedFileSchema.PositionColumn, StringComparison.Ordinal))
            {
                return new LinkedFileMatchChoice(column, by, L("LOCPlayAch_LinkedFile_MatchBy_RowOrder"), string.Empty, count);
            }

            string reading;
            switch (by)
            {
                case LinkedFileMatchBy.ApiName:
                    reading = L("LOCPlayAch_Column_ApiName");
                    break;
                case LinkedFileMatchBy.Name:
                    reading = L("LOCPlayAch_Common_Name");
                    break;
                case LinkedFileMatchBy.PositionFromZero:
                    reading = L("LOCPlayAch_LinkedFile_MatchBy_PositionFromZero");
                    break;
                default:
                    reading = L("LOCPlayAch_LinkedFile_MatchBy_PositionFromOne");
                    break;
            }

            return new LinkedFileMatchChoice(column, by, column, reading, count);
        }

        private string DescribeMatched(LinkedFileMatchResult match, IReadOnlyList<AchievementDetail> achievements)
        {
            var total = achievements.Count(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName));
            return string.Format(FormattingCulture.Current, L("LOCPlayAch_LinkedFile_MatchedBy"), match.Matches.Count, total);
        }

        /// <summary>Moves each hand pairing to the key its row has under the new way of matching.</summary>
        private void RekeyPairings(LinkedFileSchema before, LinkedFileSchema after)
        {
            var table = _lastRead?.Success == true ? _lastRead.Table : null;
            if (table == null || _pairings.Count == 0)
            {
                return;
            }

            var moved = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var row = 0; row < table.Rows.Count; row++)
            {
                var newKey = LinkedFileMatcher.RowKey(table, after, row);
                if (_pairings.TryGetValue(LinkedFileMatcher.RowKey(table, before, row), out var paired) && !moved.ContainsKey(newKey))
                {
                    moved[newKey] = paired;
                }
            }

            _pairings.Clear();
            foreach (var pair in moved)
            {
                _pairings[pair.Key] = pair.Value;
            }
        }

        /// <summary>
        /// When another way of matching pairs the row with the achievement on its own, and pairs
        /// at least as many rows as the current way, switches to it and returns true.
        /// </summary>
        private bool TryLearnMatch(string rowKey, string apiName)
        {
            var table = _lastRead?.Success == true ? _lastRead.Table : null;
            if (table == null)
            {
                return false;
            }

            var schema = BuildSchema();
            var rowIndex = RowIndexOf(table, schema, rowKey);
            var achievement = (_services.LoadAchievements?.Invoke() ?? Array.Empty<AchievementDetail>())
                .FirstOrDefault(candidate => string.Equals(candidate?.ApiName?.Trim(), apiName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (rowIndex < 0 || achievement == null)
            {
                return false;
            }

            var current = _candidates.FirstOrDefault(candidate => candidate.Is(_matchColumn, _matchBy));
            if (current != null && current.Pairs(rowIndex, achievement))
            {
                // The current way already pairs them; a stale pairing is all there is to clear.
                _pairings.Remove(rowKey);
                Recompute();
                return true;
            }

            var learned = LinkedFileMatchFinder.Pairing(_candidates, rowIndex, achievement).FirstOrDefault();
            if (learned == null || learned.Matched < (current?.Matched ?? 0))
            {
                return false;
            }

            _pairings.Remove(rowKey);
            ChooseMatch(learned.Column, learned.By);
            return true;
        }

        /// <summary>The first row whose key is <paramref name="rowKey"/>, or -1.</summary>
        private static int RowIndexOf(LinkedFileTable table, LinkedFileSchema schema, string rowKey)
        {
            for (var row = 0; row < table.Rows.Count; row++)
            {
                if (string.Equals(LinkedFileMatcher.RowKey(table, schema, row), rowKey, StringComparison.Ordinal))
                {
                    return row;
                }
            }

            return -1;
        }
    }
}
