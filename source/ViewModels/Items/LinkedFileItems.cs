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

        public LinkedFileColumnItem(
            int index,
            string columnName,
            LinkedFileOption<LinkedFileField> selectedField,
            string displayName = null)
        {
            Index = index;
            ColumnName = columnName;
            DisplayName = string.IsNullOrEmpty(displayName) ? columnName : displayName;
            _selectedField = selectedField;
        }

        public int Index { get; }

        public string ColumnName { get; }

        /// <summary>What the header calls the column; the position column is named for its field.</summary>
        public string DisplayName { get; }

        /// <summary>The position column, whose cells are the plugin's own row numbers.</summary>
        public bool IsPosition => string.Equals(ColumnName, LinkedFileSchema.PositionColumn, StringComparison.Ordinal);

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
        public override bool CanPick => _pairing != null;

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
    /// One way to match the file's rows, as the window's match dropdown lists it: the column, how
    /// its cells are read, and how many rows that pairs, each in a column of its own.
    /// </summary>
    public sealed class LinkedFileMatchChoice
    {
        public LinkedFileMatchChoice(string column, LinkedFileMatchBy by, string columnText, string readingText, string countText)
        {
            Column = column;
            By = by;
            ColumnText = columnText;
            ReadingText = readingText;
            CountText = countText;
            Name = string.IsNullOrEmpty(readingText) ? columnText : columnText + ", " + readingText;
        }

        public string Column { get; }

        public LinkedFileMatchBy By { get; }

        /// <summary>The file's column name, or the row order for the position column.</summary>
        public string ColumnText { get; }

        /// <summary>How the cells are read; empty for the row order.</summary>
        public string ReadingText { get; }

        public string CountText { get; }

        /// <summary>Column and reading as one phrase, for screen readers and search.</summary>
        public string Name { get; }

        public bool Is(string column, LinkedFileMatchBy by) =>
            by == By && string.Equals(column, Column, StringComparison.OrdinalIgnoreCase);

        public override string ToString() => Name;
    }

    /// <summary>
    /// A column of the achievements list beside each achievement's name: the unlock status, or
    /// a field a file column is mapped to.
    /// </summary>
    public sealed class LinkedFileValueColumn
    {
        public LinkedFileValueColumn(LinkedFileField field, string header)
        {
            Field = field;
            Header = header;
        }

        /// <summary>The field shown; Unlocked is the status column, shown whether or not a column feeds it.</summary>
        public LinkedFileField Field { get; }

        public string Header { get; }
    }

    /// <summary>
    /// What an achievement row's dropdown offers and where a pick goes: the file's rows, the
    /// Automatic and None choices, and the window's handler for a pick (the achievement's API
    /// name, then the row key, "" for none, or null for automatic).
    /// </summary>
    public sealed class LinkedFileRowPickContext
    {
        public LinkedFileRowPickContext(MatchOption automatic, MatchOption none, Action<string, string> pick)
        {
            Automatic = automatic;
            None = none;
            Pick = pick;
        }

        public MatchOption Automatic { get; }

        public MatchOption None { get; }

        public Action<string, string> Pick { get; }

        public IReadOnlyList<MatchOption> Rows { get; set; } = Array.Empty<MatchOption>();
    }

    /// <summary>
    /// One of the game's achievements in the achievements list: masked per the display settings,
    /// the file row it pairs with, and what that row says. Its row cell is a dropdown that pairs
    /// it with a row by hand.
    /// </summary>
    public sealed class LinkedFileAchievementRow : MatchPickerItem
    {
        private string _name;
        private string _description;
        private string _icon;
        private string _rowText;
        private bool _isMatched;
        private bool _isUserSet;
        private string[] _values = Array.Empty<string>();
        private LinkedFileRowPickContext _pick;

        public LinkedFileAchievementRow(string apiName)
        {
            ApiName = apiName;
        }

        public string ApiName { get; }

        public string Name => _name;

        public string Description => _description;

        public string Icon => _icon;

        /// <summary>The row it pairs with, or that it pairs with none.</summary>
        public string RowText => _rowText;

        public bool IsMatched => _isMatched;

        /// <summary>Whether the user paired it by hand.</summary>
        public bool IsUserSet => _isUserSet;

        public override bool CanPick => _pick != null;

        /// <summary>What the paired row says, by the window's value column index.</summary>
        public string this[int index] => index >= 0 && index < _values.Length ? _values[index] : string.Empty;

        internal void Set(string name, string description, string icon, string rowText, bool isMatched, bool isUserSet, string[] values, LinkedFileRowPickContext pick)
        {
            _pick = pick;
            SetField(ref _name, name, nameof(Name));
            SetField(ref _description, description, nameof(Description));
            SetField(ref _icon, icon, nameof(Icon));
            SetField(ref _rowText, rowText, nameof(RowText));
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

            values = values ?? Array.Empty<string>();
            if (!values.SequenceEqual(_values))
            {
                _values = values;
                OnPropertyChanged("Item[]");
            }

            OnPropertyChanged(nameof(CanPick));
            RefreshOptions();
        }

        private void SetField(ref string field, string value, string propertyName)
        {
            if (!string.Equals(field, value, StringComparison.Ordinal))
            {
                field = value;
                OnPropertyChanged(propertyName);
            }
        }

        /// <summary>Automatic (when paired by hand), None, then the rows the search matches.</summary>
        protected override IReadOnlyList<MatchOption> BuildOptions(string search)
        {
            if (_pick == null)
            {
                return Array.Empty<MatchOption>();
            }

            var options = new List<MatchOption>();
            if (_isUserSet)
            {
                options.Add(_pick.Automatic);
            }

            options.Add(_pick.None);
            options.AddRange(_pick.Rows.Where(row => MatchesSearch(row, search)));
            return options;
        }

        public override void Pick(MatchOption option)
        {
            if (option == null || _pick == null)
            {
                return;
            }

            IsPickerOpen = false;
            _pick.Pick?.Invoke(ApiName, option.Key);
        }
    }
}
