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
        /// <param name="fetchedDefinitions">
        /// A game's tags that only its achievements' data defines (a source's keys no list holds).
        /// Listed after the stored ones and never deleted; renaming, recoloring or giving one an icon
        /// stores it as the game's own.
        /// </param>
        public AchievementTagListViewModel(
            IEnumerable<AchievementTagDefinition> definitions,
            Action<IReadOnlyList<AchievementTagDefinition>> commit,
            Func<string, string> pickColor,
            Action<AchievementTagDefinition> makeGlobal = null,
            IEnumerable<AchievementTagDefinition> fetchedDefinitions = null)
        {
            _commit = commit ?? throw new ArgumentNullException(nameof(commit));
            _pickColor = pickColor;
            _makeGlobal = makeGlobal;
            Rows = new ObservableCollection<AchievementTagEditorRow>(
                (definitions ?? Enumerable.Empty<AchievementTagDefinition>())
                    .Where(definition => definition != null)
                    .Select(definition => new AchievementTagEditorRow(this, definition))
                    .Concat((fetchedDefinitions ?? Enumerable.Empty<AchievementTagDefinition>())
                        .Where(definition => definition != null)
                        .Select(definition => new AchievementTagEditorRow(this, definition) { IsFetched = true })));
            AddCommand = new RelayCommand(_ => Add());
        }

        public ObservableCollection<AchievementTagEditorRow> Rows { get; }

        public ICommand AddCommand { get; }

        public bool CanMakeGlobal => _makeGlobal != null;

        internal void Commit() => _commit(Rows.Where(row => !row.IsFetched).Select(row => row.ToDefinition()).ToList());

        internal string PickColor(string current) => _pickColor?.Invoke(current);

        internal void Delete(AchievementTagEditorRow row)
        {
            if (row != null && row.CanDelete && Rows.Remove(row))
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

        /// <summary>
        /// Moves the rows with <paramref name="keys"/> before or after the row with
        /// <paramref name="targetKey"/>, keeping their order; a drag-and-drop drop. True when
        /// anything moved.
        /// </summary>
        public bool MoveRows(IReadOnlyList<string> keys, string targetKey, bool insertAfter)
        {
            var moving = Rows.Where(row => keys?.Contains(row.Key, StringComparer.OrdinalIgnoreCase) == true).ToList();
            if (moving.Count == 0 || moving.Any(row => string.Equals(row.Key, targetKey, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var remaining = Rows.Except(moving).ToList();
            var targetIndex = remaining.FindIndex(row => string.Equals(row.Key, targetKey, StringComparison.OrdinalIgnoreCase));
            if (targetIndex < 0)
            {
                return false;
            }

            remaining.InsertRange(insertAfter ? targetIndex + 1 : targetIndex, moving);
            return Reorder(remaining);
        }

        /// <summary>Moves the rows with <paramref name="keys"/> to the end of the list.</summary>
        public bool MoveRowsToEnd(IReadOnlyList<string> keys)
        {
            var moving = Rows.Where(row => keys?.Contains(row.Key, StringComparer.OrdinalIgnoreCase) == true).ToList();
            return moving.Count > 0 && Reorder(Rows.Except(moving).Concat(moving).ToList());
        }

        private bool Reorder(IList<AchievementTagEditorRow> ordered)
        {
            if (ordered.SequenceEqual(Rows))
            {
                return false;
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var current = Rows.IndexOf(ordered[i]);
                if (current != i)
                {
                    Rows.Move(current, i);
                }
            }

            Commit();
            return true;
        }

        private void Add()
        {
            var row = new AchievementTagEditorRow(
                this,
                new AchievementTagDefinition
                {
                    Key = AchievementTagHelper.NewUserKey(),
                    Name = Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_Common_Tag")
                });
            Rows.Add(row);
            Commit();
        }
    }

    /// <summary>One editable tag: its name, color, icon and visibility.</summary>
    public sealed class AchievementTagEditorRow : ObservableObject
    {
        private readonly AchievementTagListViewModel _owner;
        private string _name;
        private string _color;
        private string _icon;
        private bool _hidden;
        private bool _isFetched;

        internal AchievementTagEditorRow(AchievementTagListViewModel owner, AchievementTagDefinition definition)
        {
            _owner = owner;
            Key = definition.Key;
            _name = definition.Name;
            _color = AchievementTagHelper.NormalizeColor(definition.Color);
            _icon = AchievementTagHelper.NormalizeIcon(definition.Icon);
            _hidden = definition.Hidden;
            ToggleHiddenCommand = new RelayCommand(_ => IsHidden = !IsHidden);
            PickColorCommand = new RelayCommand(_ => PickColor());
            ClearColorCommand = new RelayCommand(_ => Color = null);
            PickIconCommand = new RelayCommand(_ => PickIcon());
            DeleteCommand = new RelayCommand(_ => _owner.Delete(this), _ => CanDelete);
            MakeGlobalCommand = new RelayCommand(_ => _owner.MakeGlobal(this));
        }

        public string Key { get; }

        /// <summary>
        /// The name shown while the tag's own is blank: a built-in tag's localized name, or a
        /// source's key spelled out. User tags have none.
        /// </summary>
        public string DefaultName => Key.StartsWith(AchievementTagHelper.UserKeyPrefix, StringComparison.OrdinalIgnoreCase)
            ? null
            : AchievementTagHelper.GetDefaultName(Key);

        public bool IsSystem => AchievementTagHelper.IsSystemTag(Key);

        /// <summary>A system tag's built-in name (Missable, Unobtainable); null for any other tag.</summary>
        public string SystemName => IsSystem ? AchievementTagHelper.GetBuiltInName(Key) : null;

        /// <summary>A renamed system tag's built-in name, so it can still be told apart; null otherwise.</summary>
        public string RenamedSystemName => IsSystem && _name != null ? SystemName : null;

        /// <summary>
        /// A tag only the game's achievement data defines. Not stored until edited, and never
        /// deleted: the data keeps putting it on achievements.
        /// </summary>
        public bool IsFetched
        {
            get => _isFetched;
            internal set
            {
                if (_isFetched != value)
                {
                    _isFetched = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanDelete));
                }
            }
        }

        public bool CanDelete => !IsSystem && !IsFetched;

        // An edit makes a fetched tag the game's own, so the commit that follows stores it.
        private void Materialize() => IsFetched = false;

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
                OnPropertyChanged(nameof(RenamedSystemName));
                Materialize();
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
                OnPropertyChanged(nameof(ColorText));
                OnPropertyChanged(nameof(Brush));
                OnPropertyChanged(nameof(HasColor));
                Materialize();
                _owner.Commit();
            }
        }

        /// <summary>
        /// The color as typed in the hex box: blank for the default; text that is not a color leaves
        /// the color as it was.
        /// </summary>
        public string ColorText
        {
            get => _color ?? string.Empty;
            set
            {
                var text = (value ?? string.Empty).Trim();
                if (text.Length > 0 && !text.StartsWith("#", StringComparison.Ordinal))
                {
                    text = "#" + text;
                }

                if (text.Length == 0)
                {
                    Color = null;
                }
                else if (AchievementTagHelper.NormalizeColor(text) != null)
                {
                    Color = text;
                }
                else
                {
                    OnPropertyChanged();
                }
            }
        }

        public Brush Brush => AchievementTagPresentation.ToBrush(_color);

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
                Materialize();
                _owner.Commit();
            }
        }

        public string Glyph => AchievementTagHelper.IconGlyph(_icon);

        public bool HasGlyph => _icon != null;

        /// <summary>
        /// Left out of every view but the Manage Achievements editor; the achievements keep the tag.
        /// </summary>
        public bool IsHidden
        {
            get => _hidden;
            set
            {
                if (_hidden == value)
                {
                    return;
                }

                _hidden = value;
                OnPropertyChanged();
                Materialize();
                _owner.Commit();
            }
        }

        /// <summary>
        /// Flips <see cref="IsHidden"/>. The row's toggle runs this rather than binding two-way:
        /// the tag list's DataGrid holds a cell's two-way update in its row binding group, so a
        /// bound toggle never reached the row.
        /// </summary>
        public ICommand ToggleHiddenCommand { get; }

        public ICommand PickColorCommand { get; }

        public ICommand ClearColorCommand { get; }

        public ICommand PickIconCommand { get; }

        public ICommand DeleteCommand { get; }

        public ICommand MakeGlobalCommand { get; }

        internal AchievementTagDefinition ToDefinition() =>
            new AchievementTagDefinition { Key = Key, Name = _name, Color = _color, Icon = _icon, Hidden = _hidden };

        private void PickColor()
        {
            var picked = _owner.PickColor(_color ?? DefaultColorHex());
            if (picked != null)
            {
                Color = picked;
            }
        }

        // A tag without a color uses the theme's text color, so the picker opens on it.
        private static string DefaultColorHex()
        {
            var brush = System.Windows.Application.Current?.TryFindResource("PlayAch.Brush.Text") as SolidColorBrush;
            return brush == null
                ? "#FFFFFF"
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", brush.Color.R, brush.Color.G, brush.Color.B);
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

namespace PlayniteAchievements.ViewModels.Tags
{
    /// <summary>A tag shown but not edited: the global list beside a game's own tags.</summary>
    public sealed class AchievementTagPreviewRow
    {
        public AchievementTagPreviewRow(AchievementTagDefinition definition)
        {
            Name = AchievementTagRegistry.GetName(definition);
            Glyph = AchievementTagHelper.IconGlyph(definition?.Icon);
            Color = AchievementTagHelper.NormalizeColor(definition?.Color);
        }

        public string Name { get; }

        public string Glyph { get; }

        public bool HasGlyph => Glyph != null;

        public string Color { get; }

        public Brush Brush => AchievementTagPresentation.ToBrush(Color);

        public bool HasColor => Color != null;
    }
}
