using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
using System.Collections.Generic;
using System.Linq;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>A value with its display name, for the mapping window's dropdowns.</summary>
    public sealed class LinkedFileOption<T>
    {
        public LinkedFileOption(T value, string name)
        {
            Value = value;
            Name = name;
        }

        public T Value { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// One file column in the mapping window: its name and the achievement field it feeds. The
    /// header of the preview column hosts the field dropdown.
    /// </summary>
    public sealed class LinkedFileColumnItem : ObservableObject
    {
        private LinkedFileOption<LinkedFileField> _selectedField;

        public LinkedFileColumnItem(int index, string columnName, LinkedFileOption<LinkedFileField> selectedField)
        {
            Index = index;
            ColumnName = columnName;
            _selectedField = selectedField;
        }

        public int Index { get; }

        public string ColumnName { get; }

        /// <summary>Raised after the user picks a field; the view model keeps one column per field.</summary>
        public event Action<LinkedFileColumnItem> FieldChanged;

        public LinkedFileOption<LinkedFileField> SelectedField
        {
            get => _selectedField;
            set
            {
                if (value == null || ReferenceEquals(value, _selectedField))
                {
                    return;
                }

                _selectedField = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMapped));
                FieldChanged?.Invoke(this);
            }
        }

        public LinkedFileField Field => _selectedField?.Value ?? LinkedFileField.Ignore;

        public bool IsMapped => Field != LinkedFileField.Ignore;

        /// <summary>Sets the field without raising <see cref="FieldChanged"/>.</summary>
        internal void SetFieldSilently(LinkedFileOption<LinkedFileField> option)
        {
            if (option == null || ReferenceEquals(option, _selectedField))
            {
                return;
            }

            _selectedField = option;
            OnPropertyChanged(nameof(SelectedField));
            OnPropertyChanged(nameof(IsMapped));
        }
    }

    /// <summary>
    /// What a preview row's pairing dropdown offers and where a pick goes: the game's
    /// achievements, the Automatic and None choices, and the window's handler for a pick
    /// (row key, then the achievement's API name, "" for none, or null for automatic).
    /// </summary>
    public sealed class LinkedFilePairingContext
    {
        public LinkedFilePairingContext(MatchOption automatic, MatchOption none, Action<string, string> pair)
        {
            Automatic = automatic;
            None = none;
            Pair = pair;
        }

        public MatchOption Automatic { get; }

        public MatchOption None { get; }

        public Action<string, string> Pair { get; }

        public IReadOnlyList<MatchOption> Achievements { get; set; } = Array.Empty<MatchOption>();
    }

    /// <summary>
    /// One preview row: its cells by column index, and what the row pairs with, so the preview
    /// shows the match beside the file's own columns. Once a match ran, the match cell is a
    /// dropdown that pairs the row with an achievement by hand.
    /// </summary>
    public sealed class LinkedFilePreviewRow : MatchPickerItem
    {
        private readonly string[] _cells;
        private string _match;
        private bool _isMatched;
        private bool _isUserSet;
        private string _rowKey;
        private LinkedFilePairingContext _pairing;

        public LinkedFilePreviewRow(string[] cells, string match, bool isMatched)
        {
            _cells = cells ?? Array.Empty<string>();
            _match = match;
            _isMatched = isMatched;
        }

        public string this[int index] => index >= 0 && index < _cells.Length ? _cells[index] : string.Empty;

        public int Count => _cells.Length;

        /// <summary>The achievement the row pairs with, or why it pairs with none.</summary>
        public string Match => _match;

        public bool IsMatched => _isMatched;

        /// <summary>Whether the user paired the row by hand, with an achievement or with none.</summary>
        public bool IsUserSet => _isUserSet;

        /// <summary>The key the row's hand pairing is stored under.</summary>
        public string RowKey => _rowKey;

        /// <summary>Whether the row can be paired by hand; not before the file was matched.</summary>
        public bool CanPick => _pairing != null;

        private string _matchIcon;
        private bool[] _failedCells = Array.Empty<bool>();
        private string[] _cellErrors = Array.Empty<string>();

        /// <summary>The icon of the achievement the row pairs with; null when it pairs with none.</summary>
        public string MatchIcon => _matchIcon;

        /// <summary>Per column index, whether the cell's value could not be read for its field.</summary>
        public bool[] FailedCells => _failedCells;

        /// <summary>Per column index, why the cell could not be read; null for a readable cell.</summary>
        public string[] CellErrors => _cellErrors;

        internal void SetMatchIcon(string icon)
        {
            if (!string.Equals(_matchIcon, icon, StringComparison.Ordinal))
            {
                _matchIcon = icon;
                OnPropertyChanged(nameof(MatchIcon));
            }
        }

        /// <summary>Marks the cells that could not be read, by column index, with the reason for each.</summary>
        internal void SetCellErrors(string[] errors)
        {
            errors = errors ?? Array.Empty<string>();
            if (errors.SequenceEqual(_cellErrors))
            {
                return;
            }

            _cellErrors = errors;
            _failedCells = errors.Select(error => error != null).ToArray();
            OnPropertyChanged(nameof(CellErrors));
            OnPropertyChanged(nameof(FailedCells));
        }

        /// <summary>Sets what the row pairs with, keeping the instance so the grid keeps its place.</summary>
        internal void SetMatch(string match, bool isMatched, bool isUserSet, string rowKey, LinkedFilePairingContext pairing)
        {
            var couldPick = CanPick;
            _rowKey = rowKey;
            _pairing = pairing;
            if (!string.Equals(_match, match, StringComparison.Ordinal))
            {
                _match = match;
                OnPropertyChanged(nameof(Match));
            }

            if (_isMatched != isMatched)
            {
                _isMatched = isMatched;
                OnPropertyChanged(nameof(IsMatched));
            }

            if (_isUserSet != isUserSet)
            {
                _isUserSet = isUserSet;
                OnPropertyChanged(nameof(IsUserSet));
            }

            if (couldPick != CanPick)
            {
                OnPropertyChanged(nameof(CanPick));
            }

            RefreshOptions();
        }

        /// <summary>Automatic (when the user paired the row), None, then the achievements the search matches.</summary>
        protected override IReadOnlyList<MatchOption> BuildOptions(string search)
        {
            if (_pairing == null)
            {
                return Array.Empty<MatchOption>();
            }

            var options = new List<MatchOption>();
            if (_isUserSet)
            {
                options.Add(_pairing.Automatic);
            }

            options.Add(_pairing.None);
            options.AddRange(_pairing.Achievements.Where(achievement => MatchesSearch(achievement, search)));
            return options;
        }

        public override void Pick(MatchOption option)
        {
            if (option == null || _pairing == null)
            {
                return;
            }

            IsPickerOpen = false;
            _pairing.Pair?.Invoke(_rowKey, option.Key);
        }
    }

    /// <summary>
    /// One of the game's achievements that no file row pairs with, as the unlinked list shows it:
    /// masked per the display settings until revealed.
    /// </summary>
    public sealed class LinkedFileUnlinkedItem
    {
        public LinkedFileUnlinkedItem(string apiName, string name, string description, string icon, bool canReveal, bool isRevealed)
        {
            ApiName = apiName;
            Name = name;
            Description = description;
            Icon = icon;
            CanReveal = canReveal;
            IsRevealed = isRevealed;
        }

        public string ApiName { get; }

        public string Name { get; }

        public string Description { get; }

        public string Icon { get; }

        /// <summary>Whether the display settings mask the achievement, so clicking it reveals or masks it.</summary>
        public bool CanReveal { get; }

        public bool IsRevealed { get; }
    }
}
