using PlayniteAchievements.Models.Settings;
using System;
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
    /// One preview row: its cells by column index, and what the row pairs with, so the preview
    /// shows the match beside the file's own columns.
    /// </summary>
    public sealed class LinkedFilePreviewRow
    {
        private readonly string[] _cells;

        public LinkedFilePreviewRow(string[] cells, string match, bool isMatched)
        {
            _cells = cells ?? Array.Empty<string>();
            Match = match;
            IsMatched = isMatched;
        }

        public string this[int index] => index >= 0 && index < _cells.Length ? _cells[index] : string.Empty;

        public int Count => _cells.Length;

        /// <summary>The achievement the row pairs with, or why it pairs with none.</summary>
        public string Match { get; }

        public bool IsMatched { get; }
    }

    /// <summary>An achievement the match left unpaired, for the mapping window's list.</summary>
    public sealed class LinkedFileMatchItem
    {
        public LinkedFileMatchItem(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public override string ToString() => Text;
    }
}
