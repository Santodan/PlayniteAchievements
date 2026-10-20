using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Views.Dialogs;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.Tags
{
    /// <summary>
    /// An editable tag list: the global one in settings or a game's own. Every edit is committed
    /// at once through the callback the host passes, the same instant-persist rule the Manage
    /// Achievements tabs follow.
    /// </summary>
    public sealed class AchievementTagListViewModel : ObservableObject
    {
        private readonly Action<IReadOnlyList<AchievementTagDefinition>> _commit;
        private readonly Func<string, string> _pickColor;
        private readonly Action<AchievementTagDefinition> _makeGlobal;

        /// <param name="definitions">The list to edit.</param>
        /// <param name="commit">Stores the edited list.</param>
        /// <param name="pickColor">Opens the color picker on a #RRGGBB value; null when cancelled.</param>
        /// <param name="makeGlobal">Moves one of a game's tags into the global list; null hides the action.</param>
        public AchievementTagListViewModel(
            IEnumerable<AchievementTagDefinition> definitions,
            Action<IReadOnlyList<AchievementTagDefinition>> commit,
            Func<string, string> pickColor,
            Action<AchievementTagDefinition> makeGlobal = null)
        {
            _commit = commit ?? throw new ArgumentNullException(nameof(commit));
            _pickColor = pickColor;
            _makeGlobal = makeGlobal;
            Rows = new ObservableCollection<AchievementTagEditorRow>(
                (definitions ?? Enumerable.Empty<AchievementTagDefinition>())
                    .Where(definition => definition != null)
                    .Select(definition => new AchievementTagEditorRow(this, definition)));
            AddCommand = new RelayCommand(_ => Add());
        }

        public ObservableCollection<AchievementTagEditorRow> Rows { get; }

        public ICommand AddCommand { get; }

        public bool CanMakeGlobal => _makeGlobal != null;

        internal void Commit() => _commit(Rows.Select(row => row.ToDefinition()).ToList());

        internal string PickColor(string current) => _pickColor?.Invoke(current);

        internal void Delete(AchievementTagEditorRow row)
        {
            if (row != null && !row.IsSystem && Rows.Remove(row))
            {
                Commit();
            }
        }

        internal void MakeGlobal(AchievementTagEditorRow row)
        {
            if (row == null || _makeGlobal == null || !Rows.Remove(row))
            {
                return;
            }

            _makeGlobal(row.ToDefinition());
            Commit();
        }

        internal void Move(AchievementTagEditorRow row, int offset)
        {
            var index = Rows.IndexOf(row);
            var target = index + offset;
            if (index < 0 || target < 0 || target >= Rows.Count)
            {
                return;
            }

            Rows.Move(index, target);
            Commit();
        }

        private void Add()
        {
            var row = new AchievementTagEditorRow(
                this,
                new AchievementTagDefinition
                {
                    Key = AchievementTagHelper.NewUserKey(),
                    Name = Playnite.SDK.ResourceProvider.GetString("LOCTagLabel")
                });
            Rows.Add(row);
            Commit();
        }
    }

    /// <summary>One editable tag: its name, color and icon.</summary>
    public sealed class AchievementTagEditorRow : ObservableObject
    {
        private readonly AchievementTagListViewModel _owner;
        private string _name;
        private string _color;
        private string _icon;

        internal AchievementTagEditorRow(AchievementTagListViewModel owner, AchievementTagDefinition definition)
        {
            _owner = owner;
            Key = definition.Key;
            _name = definition.Name;
            _color = AchievementTagHelper.NormalizeColor(definition.Color);
            _icon = AchievementTagHelper.NormalizeIcon(definition.Icon);
            PickColorCommand = new RelayCommand(_ => PickColor());
            ClearColorCommand = new RelayCommand(_ => Color = null);
            PickIconCommand = new RelayCommand(_ => PickIcon());
            DeleteCommand = new RelayCommand(_ => _owner.Delete(this), _ => !IsSystem);
            MakeGlobalCommand = new RelayCommand(_ => _owner.MakeGlobal(this));
            MoveUpCommand = new RelayCommand(_ => _owner.Move(this, -1));
            MoveDownCommand = new RelayCommand(_ => _owner.Move(this, 1));
        }

        public string Key { get; }

        /// <summary>The localized default name of a built-in tag, shown while its name is blank.</summary>
        public string DefaultName => AchievementTagHelper.GetBuiltInName(Key);

        public bool IsSystem => AchievementTagHelper.IsSystemTag(Key);

        public bool CanMakeGlobal => _owner.CanMakeGlobal;

        /// <summary>The user's name, or a built-in tag's default name until it is renamed; clearing it restores the default.</summary>
        public string Name
        {
            get => _name ?? DefaultName ?? string.Empty;
            set
            {
                var trimmed = (value ?? string.Empty).Trim();
                var stored = trimmed.Length == 0 || string.Equals(trimmed, DefaultName, StringComparison.Ordinal)
                    ? null
                    : trimmed;
                if (string.Equals(_name, stored, StringComparison.Ordinal))
                {
                    return;
                }

                _name = stored;
                OnPropertyChanged();
                _owner.Commit();
            }
        }

        public string Color
        {
            get => _color;
            set
            {
                var normalized = AchievementTagHelper.NormalizeColor(value);
                if (string.Equals(_color, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _color = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Brush));
                OnPropertyChanged(nameof(HasColor));
                _owner.Commit();
            }
        }

        public Brush Brush => ToBrush(_color);

        public bool HasColor => _color != null;

        public string Icon
        {
            get => _icon;
            set
            {
                var normalized = AchievementTagHelper.NormalizeIcon(value);
                if (string.Equals(_icon, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _icon = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(HasGlyph));
                _owner.Commit();
            }
        }

        public string Glyph => AchievementTagHelper.IconGlyph(_icon);

        public bool HasGlyph => _icon != null;

        public ICommand PickColorCommand { get; }

        public ICommand ClearColorCommand { get; }

        public ICommand PickIconCommand { get; }

        public ICommand DeleteCommand { get; }

        public ICommand MakeGlobalCommand { get; }

        public ICommand MoveUpCommand { get; }

        public ICommand MoveDownCommand { get; }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> BrushByColor =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        // One frozen brush per color for the swatch.
        private static Brush ToBrush(string color)
        {
            var normalized = AchievementTagHelper.NormalizeColor(color);
            if (normalized == null)
            {
                return null;
            }

            return BrushByColor.GetOrAdd(normalized, value =>
            {
                var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
                brush.Freeze();
                return brush;
            });
        }

        internal AchievementTagDefinition ToDefinition() =>
            new AchievementTagDefinition { Key = Key, Name = _name, Color = _color, Icon = _icon };

        private void PickColor()
        {
            var picked = _owner.PickColor(_color ?? "#FFFFFF");
            if (picked != null)
            {
                Color = picked;
            }
        }

        private void PickIcon()
        {
            if (IcoFontPickerDialog.TryPick(_icon, out var code))
            {
                Icon = code;
            }
        }
    }
}
