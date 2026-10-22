using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Playnite.SDK;

namespace PlayniteAchievements.ViewModels.Items
{
    public sealed class GridControlBarViewModel : PlayniteAchievements.Common.ObservableObject
    {
        private GridSearchControl _search;

        public GridControlBarViewModel()
        {
            Items = new ObservableCollection<GridControlBarItem>();
            LeadingItems = new ObservableCollection<GridControlBarItem>();
        }

        public GridSearchControl Search
        {
            get => _search;
            set => SetValue(ref _search, value);
        }

        public ObservableCollection<GridControlBarItem> Items { get; }

        // Items rendered to the left of the search box (e.g. the category-mode Back and toggle while
        // in category mode). Empty on most surfaces, so the leading zone collapses to nothing.
        public ObservableCollection<GridControlBarItem> LeadingItems { get; }

        public void Refresh()
        {
            Search?.Refresh();
            foreach (var item in LeadingItems)
            {
                item?.Refresh();
            }

            foreach (var item in Items)
            {
                item?.Refresh();
            }
        }
    }

    // Serializable snapshot of the adapter's filter state. Used to persist/restore the
    // grid control bar (e.g. ViewAchievementsViewModel's per-game session snapshot)
    // without threading each field through callers.
    public sealed class GridControlBarFilterState
    {
        public string SearchText { get; set; } = string.Empty;
        public bool ShowUnlocked { get; set; } = true;
        public bool ShowLocked { get; set; } = true;
        public bool ShowHidden { get; set; } = true;
        public List<string> CategoryTypeFilters { get; set; } = new List<string>();
        public List<string> CategoryLabelFilters { get; set; } = new List<string>();
    }

    /// <summary>
    /// A control bar entry a widget can show or hide on its own: the search box or an item.
    /// <see cref="Id"/> names its visibility option; <see cref="LabelKey"/> labels its row in
    /// the widget settings.
    /// </summary>
    public interface IGridControlBarEntry
    {
        string Id { get; }

        string LabelKey { get; }

        /// <summary>Resets the entry to applying no filter: no text, no selection, no range.</summary>
        void ClearSelection();
    }

    public abstract class GridControlBarItem : PlayniteAchievements.Common.ObservableObject, IGridControlBarEntry
    {
        private bool _isVisible = true;
        private bool _autoHideWhenUnavailable = true;
        private double _width = double.NaN;
        private double _minWidth = 0d;
        private string _toolTip;

        /// <summary>Null for an item no widget shows or hides on its own.</summary>
        public string Id { get; set; }

        public string LabelKey { get; set; }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (SetValueAndReturn(ref _isVisible, value))
                {
                    OnPropertyChanged(nameof(EffectiveIsVisible));
                }
            }
        }

        public bool AutoHideWhenUnavailable
        {
            get => _autoHideWhenUnavailable;
            set
            {
                if (SetValueAndReturn(ref _autoHideWhenUnavailable, value))
                {
                    OnPropertyChanged(nameof(EffectiveIsVisible));
                }
            }
        }

        public bool EffectiveIsVisible => IsVisible && (!AutoHideWhenUnavailable || HasAvailableAction);

        public double Width
        {
            get => _width;
            set => SetValue(ref _width, value);
        }

        public double MinWidth
        {
            get => _minWidth;
            set => SetValue(ref _minWidth, value);
        }

        public string ToolTip
        {
            get => _toolTip;
            set => SetValue(ref _toolTip, value);
        }

        public virtual void Refresh()
        {
            OnPropertyChanged(string.Empty);
            OnPropertyChanged(nameof(EffectiveIsVisible));
        }

        /// <summary>Resets the item to applying no filter; items that filter nothing do nothing.</summary>
        public virtual void ClearSelection()
        {
        }

        protected virtual bool HasAvailableAction => true;
    }

    public sealed class GridSearchControl : PlayniteAchievements.Common.ObservableObject, IGridControlBarEntry
    {
        private readonly Func<string> _getText;
        private readonly Action<string> _setText;
        private readonly Action _clear;
        private string _text;
        private double _minWidth = 120d;

        public GridSearchControl(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            Func<string> getText,
            Action<string> setText,
            string placeholder,
            Action clear = null)
        {
            _getText = getText;
            _setText = setText;
            _clear = clear;
            Placeholder = placeholder;
            Subscribe(source, sourcePropertyName, Refresh);
        }

        public string Text
        {
            get => _getText != null ? _getText() ?? string.Empty : _text ?? string.Empty;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(Text, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                if (_setText != null)
                {
                    _setText(normalized);
                }
                else
                {
                    _text = normalized;
                }

                Refresh();
            }
        }

        public string Placeholder { get; }

        public string Id { get; set; }

        public string LabelKey { get; set; }

        public double MinWidth
        {
            get => _minWidth;
            set => SetValue(ref _minWidth, value);
        }

        public void Clear()
        {
            if (_clear != null)
            {
                _clear();
            }
            else
            {
                Text = string.Empty;
            }

            Refresh();
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(Text));
        }

        public void ClearSelection() => Clear();

        internal static void Subscribe(INotifyPropertyChanged source, string propertyName, Action refresh)
        {
            if (source == null || refresh == null)
            {
                return;
            }

            source.PropertyChanged += (sender, e) =>
            {
                if (string.IsNullOrEmpty(propertyName) ||
                    string.IsNullOrEmpty(e?.PropertyName) ||
                    string.Equals(e.PropertyName, propertyName, StringComparison.Ordinal))
                {
                    refresh();
                }
            };
        }
    }

    public sealed class GridMultiSelectFilter : GridControlBarItem
    {
        private readonly Func<string> _getDisplayText;
        private readonly Func<IEnumerable<string>> _getOptions;
        private readonly IEnumerable<string> _options;
        private readonly Func<string, bool> _isSelected;
        private readonly Action<string, bool> _setSelection;
        private readonly Func<string, string> _getDisplayLabel;
        private readonly Func<bool> _hasAvailableAction;
        private readonly Func<string, bool> _isMarked;

        public GridMultiSelectFilter(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            Func<string> getDisplayText,
            IEnumerable<string> options,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection,
            Func<string, string> getDisplayLabel = null,
            Func<bool> hasAvailableAction = null,
            Func<string, bool> isMarked = null)
        {
            _getDisplayText = getDisplayText;
            _options = options;
            _isSelected = isSelected;
            _setSelection = setSelection;
            _getDisplayLabel = getDisplayLabel;
            _hasAvailableAction = hasAvailableAction;
            _isMarked = isMarked;
            Subscribe(source, sourcePropertyName);
        }

        public GridMultiSelectFilter(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            Func<string> getDisplayText,
            Func<IEnumerable<string>> getOptions,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection,
            Func<string, string> getDisplayLabel = null,
            Func<bool> hasAvailableAction = null,
            Func<string, bool> isMarked = null)
        {
            _getDisplayText = getDisplayText;
            _getOptions = getOptions;
            _isSelected = isSelected;
            _setSelection = setSelection;
            _getDisplayLabel = getDisplayLabel;
            _hasAvailableAction = hasAvailableAction;
            _isMarked = isMarked;
            Subscribe(source, sourcePropertyName);
        }

        private bool _connectedRight;

        public string DisplayText => _getDisplayText?.Invoke() ?? string.Empty;

        public IEnumerable<string> Options => _getOptions?.Invoke() ?? _options;

        // When true, this dropdown scopes the category set (the Type/Label filters) rather than the
        // achievement rows, so category mode hides it whenever grouping is in effect and the mode
        // toggle attaches to it as a segmented unit.
        public bool IsCategoryFilter { get; set; }

        // When true, the options are category paths (CategoryPathHelper form) and the dropdown
        // renders them as the tree they describe: one row per node showing its leaf name, with
        // tree connectors standing in for the path, rather than one row per full path.
        public bool RendersCategoryTree { get; set; }

        // When true, this dropdown renders as the left half of a segmented unit (flat right edge)
        // so an adjacent control on its right reads as one bordered group.
        public bool ConnectedRight
        {
            get => _connectedRight;
            set => SetValue(ref _connectedRight, value);
        }

        // Default availability requires a real choice (2+ options) or an active selection;
        // callers with different semantics (e.g. the single-select compare dropdown, useful
        // with one option) supply their own predicate.
        protected override bool HasAvailableAction =>
            _hasAvailableAction?.Invoke() ?? (CountUsableOptions() > 1 || HasSelectedOption());

        public bool IsSelected(string option)
        {
            return _isSelected?.Invoke(option) == true;
        }

        public void SetSelected(string option, bool isSelected)
        {
            _setSelection?.Invoke(option, isSelected);
            Refresh();
        }

        public string GetDisplayLabel(string option)
        {
            var label = _getDisplayLabel?.Invoke(option);
            return string.IsNullOrWhiteSpace(label) ? option : label;
        }

        // When true, options render with a leading marker gutter (e.g. a favorite star) so a
        // per-option flag can be shown while keeping labels aligned. Filters without a marker
        // predicate render plain string labels unchanged.
        public bool HasMarker => _isMarked != null;

        public bool IsMarked(string option)
        {
            return _isMarked?.Invoke(option) == true;
        }

        /// <summary>
        /// The image resource key of an option's badge, or null for none. When set, the dropdown
        /// shows each option's badge before its label, and the button shows the picked options'
        /// badges in place of their names.
        /// </summary>
        public Func<string, string> IconKeyOf { get; set; }

        public bool HasIcons => IconKeyOf != null;

        /// <summary>
        /// The badges of the picked options, in option order; none when any picked option has no
        /// badge, so the button names them instead of leaving one out.
        /// </summary>
        public IReadOnlyList<string> SelectedIconKeys
        {
            get
            {
                if (IconKeyOf == null)
                {
                    return Array.Empty<string>();
                }

                var keys = (Options ?? Array.Empty<string>())
                    .Where(IsSelected)
                    .Select(IconKeyOf)
                    .ToList();
                return keys.Any(string.IsNullOrWhiteSpace) ? (IReadOnlyList<string>)Array.Empty<string>() : keys;
            }
        }

        /// <summary>Whether the button shows badges rather than text.</summary>
        public bool ShowsSelectedIcons => SelectedIconKeys.Count > 0;

        /// <summary>The button's text: the selection's names, or nothing while badges stand in for them.</summary>
        public string ButtonText => ShowsSelectedIcons ? string.Empty : DisplayText;

        /// <summary>The button's tooltip, which names the picked options while badges stand in for them.</summary>
        public string ButtonToolTip => ShowsSelectedIcons ? DisplayText : ToolTip;

        /// <summary>
        /// Clears every selection, for a filter that keeps selections its options do not list;
        /// null unpicks the listed options.
        /// </summary>
        public Action ClearAll { get; set; }

        public override void ClearSelection()
        {
            if (ClearAll != null)
            {
                ClearAll();
            }
            else
            {
                foreach (var option in (Options ?? Array.Empty<string>()).Where(IsSelected).ToList())
                {
                    _setSelection?.Invoke(option, false);
                }
            }

            Refresh();
        }

        public override void Refresh()
        {
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(Options));
            OnPropertyChanged(nameof(EffectiveIsVisible));
            OnPropertyChanged(nameof(SelectedIconKeys));
            OnPropertyChanged(nameof(ShowsSelectedIcons));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(ButtonToolTip));
        }

        private void Subscribe(INotifyPropertyChanged source, string propertyName)
        {
            GridSearchControl.Subscribe(source, propertyName, Refresh);
        }

        private int CountUsableOptions()
        {
            return (Options ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count();
        }

        private bool HasSelectedOption()
        {
            return (Options ?? Array.Empty<string>())
                .Any(option => !string.IsNullOrWhiteSpace(option) && IsSelected(option));
        }
    }

    public enum GridToggleFilterIcon
    {
        None,
        Unlocked,
        Locked,
        Hidden,
        Favorite,
    }

    public sealed class GridToggleFilter : GridControlBarItem
    {
        private readonly Func<bool> _getIsChecked;
        private readonly Action<bool> _setIsChecked;
        private readonly Func<bool> _getIsAvailable;
        private readonly string _content;

        public GridToggleFilter(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            string content,
            Func<bool> getIsChecked,
            Action<bool> setIsChecked,
            GridToggleFilterIcon icon = GridToggleFilterIcon.None,
            Func<bool> getIsAvailable = null)
        {
            _content = content;
            _getIsChecked = getIsChecked;
            _setIsChecked = setIsChecked;
            _getIsAvailable = getIsAvailable;
            Icon = icon;
            GridSearchControl.Subscribe(source, sourcePropertyName, Refresh);
        }

        public string Content => _content;

        // When set, the toggle renders a glyph (matching the grid Status column) instead
        // of the text Content; Content is surfaced as the tooltip. None keeps text rendering.
        public GridToggleFilterIcon Icon { get; }

        protected override bool HasAvailableAction => (_getIsAvailable?.Invoke() ?? true) || !IsChecked;

        /// <summary>The state that filters nothing, which clearing the toggle restores; null leaves it as is.</summary>
        public bool? ClearedValue { get; set; }

        public override void ClearSelection()
        {
            if (ClearedValue.HasValue)
            {
                IsChecked = ClearedValue.Value;
            }
        }

        public bool IsChecked
        {
            get => _getIsChecked?.Invoke() == true;
            set
            {
                if (IsChecked == value)
                {
                    return;
                }

                _setIsChecked?.Invoke(value);
                Refresh();
            }
        }

        public override void Refresh()
        {
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(EffectiveIsVisible));
        }
    }

    // A toolbar toggle rendered as a ToggleButton (distinct from GridToggleFilter, which is a
    // filter checkbox). Used to switch the achievement grid into category-summaries mode.
    public sealed class GridModeToggle : GridControlBarItem
    {
        private readonly Func<bool> _getIsChecked;
        private readonly Action<bool> _setIsChecked;
        private readonly Func<bool> _getIsAvailable;
        private readonly Func<bool> _getHasMultipleCategories;
        private bool _connected;

        public GridModeToggle(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            string content,
            Func<bool> getIsChecked,
            Action<bool> setIsChecked,
            string toolTip = null,
            Func<bool> getIsAvailable = null,
            Func<bool> getHasMultipleCategories = null)
        {
            Content = content;
            _getIsChecked = getIsChecked;
            _setIsChecked = setIsChecked;
            _getIsAvailable = getIsAvailable;
            _getHasMultipleCategories = getHasMultipleCategories;
            ToolTip = toolTip;
            GridSearchControl.Subscribe(source, sourcePropertyName, Refresh);
        }

        public string Content { get; }

        // When true, the category dropdown is shown immediately to the left, so the toggle renders
        // as the right half of a segmented unit (flat left edge, overlapping the dropdown's border).
        // False whenever the toggle stands alone, including when the dropdown auto-hides.
        public bool Connected
        {
            get => _connected;
            set => SetValue(ref _connected, value);
        }

        // Mirrors the category dropdowns' auto-hide: the toggle disappears when there is nothing to
        // group by. Stays visible while active so the user can always switch back out of the mode,
        // but only if the current game could ever offer multiple categories - otherwise a game with
        // just one category would keep showing a toggle left over from a previously selected game.
        protected override bool HasAvailableAction =>
            (_getIsAvailable?.Invoke() ?? true) || (IsChecked && (_getHasMultipleCategories?.Invoke() ?? true));

        public bool IsChecked
        {
            get => _getIsChecked?.Invoke() == true;
            set
            {
                if (IsChecked == value)
                {
                    return;
                }

                _setIsChecked?.Invoke(value);
                Refresh();
            }
        }

        public override void Refresh()
        {
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(EffectiveIsVisible));
        }
    }

    // A from-to day range in the control bar, always shown: a dropdown button like the other
    // filters, reading the range (or the placeholder while there is none), whose popup holds the
    // two date pickers. Either end may be left empty for an open range. The owner holds the
    // dates, so a range set elsewhere (a timeline column clicked in the overview's
    // mini-showcase) shows here too.
    public sealed class GridDateRangeFilter : GridControlBarItem
    {
        private readonly Func<DateTime?> _getFrom;
        private readonly Func<DateTime?> _getTo;
        private readonly Action<DateTime?, DateTime?> _set;
        private readonly string _placeholder;

        public GridDateRangeFilter(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            Func<DateTime?> getFrom,
            Func<DateTime?> getTo,
            Action<DateTime?, DateTime?> set,
            string placeholder)
        {
            _getFrom = getFrom;
            _getTo = getTo;
            _set = set;
            _placeholder = placeholder;
            GridSearchControl.Subscribe(source, sourcePropertyName, Refresh);
        }

        public string DisplayText
        {
            get
            {
                var from = From;
                var to = To;
                if (!from.HasValue && !to.HasValue)
                {
                    return _placeholder;
                }

                var culture = PlayniteAchievements.Common.FormattingCulture.Current;
                if (from.HasValue && from == to)
                {
                    return from.Value.ToString("d", culture);
                }

                // One-way ranges read as such: everything from a day on, or everything up to one.
                if (!to.HasValue)
                {
                    return string.Format(culture, ResourceProvider.GetString("LOCPlayAch_Filter_SinceDate"), from.Value.ToString("d", culture));
                }

                if (!from.HasValue)
                {
                    return string.Format(culture, ResourceProvider.GetString("LOCPlayAch_Filter_UntilDate"), to.Value.ToString("d", culture));
                }

                return from.Value.ToString("d", culture) + " – " + to.Value.ToString("d", culture);
            }
        }

        public override void ClearSelection()
        {
            _set?.Invoke(null, null);
            Refresh();
        }

        public bool HasFrom => From.HasValue;

        public bool HasTo => To.HasValue;

        public void ClearFrom() => From = null;

        public void ClearTo() => To = null;

        public DateTime? From
        {
            get => _getFrom?.Invoke();
            set
            {
                if (Nullable.Equals(From, value?.Date))
                {
                    return;
                }

                _set?.Invoke(value?.Date, To);
                Refresh();
            }
        }

        public DateTime? To
        {
            get => _getTo?.Invoke();
            set
            {
                if (Nullable.Equals(To, value?.Date))
                {
                    return;
                }

                _set?.Invoke(From, value?.Date);
                Refresh();
            }
        }

        public bool HasRange => From.HasValue || To.HasValue;

        public void Clear()
        {
            _set?.Invoke(null, null);
            Refresh();
        }

        public override void Refresh()
        {
            OnPropertyChanged(nameof(From));
            OnPropertyChanged(nameof(To));
            OnPropertyChanged(nameof(HasRange));
            OnPropertyChanged(nameof(HasFrom));
            OnPropertyChanged(nameof(HasTo));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(EffectiveIsVisible));
        }
    }

    // A momentary action button in the control bar (e.g. the category-mode Back button).
    public sealed class GridActionButton : GridControlBarItem
    {
        private readonly Action _action;

        public GridActionButton(string content, Action action, string toolTip = null)
        {
            Content = content;
            _action = action;
            ToolTip = toolTip;
        }

        public string Content { get; }

        public void Invoke()
        {
            _action?.Invoke();
        }
    }

    public sealed class GridProviderPlatformFilter : GridControlBarItem
    {
        private readonly Func<string> _getDisplayText;
        private readonly Func<ObservableCollection<ProviderFilterGroup>> _getGroups;
        private readonly ObservableCollection<ProviderFilterGroup> _groups;
        private readonly Action _closed;

        public GridProviderPlatformFilter(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            Func<string> getDisplayText,
            ObservableCollection<ProviderFilterGroup> groups,
            Action closed = null)
        {
            _getDisplayText = getDisplayText;
            _groups = groups;
            _closed = closed;
            GridSearchControl.Subscribe(source, sourcePropertyName, Refresh);
        }

        public GridProviderPlatformFilter(
            INotifyPropertyChanged source,
            string sourcePropertyName,
            Func<string> getDisplayText,
            Func<ObservableCollection<ProviderFilterGroup>> getGroups,
            Action closed = null)
        {
            _getDisplayText = getDisplayText;
            _getGroups = getGroups;
            _closed = closed;
            GridSearchControl.Subscribe(source, sourcePropertyName, Refresh);
        }

        public string DisplayText => _getDisplayText?.Invoke() ?? string.Empty;

        public ObservableCollection<ProviderFilterGroup> Groups => _getGroups?.Invoke() ?? _groups;

        protected override bool HasAvailableAction => CountSelectableOptions() > 1 || HasSelectedOption();

        public void OnClosed()
        {
            _closed?.Invoke();
        }

        public override void ClearSelection()
        {
            foreach (var group in (Groups ?? new ObservableCollection<ProviderFilterGroup>())
                         .Where(group => group?.HasAnySelected == true)
                         .ToList())
            {
                group.SetAll(false);
            }

            Refresh();
        }

        public override void Refresh()
        {
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(Groups));
            OnPropertyChanged(nameof(EffectiveIsVisible));
        }

        private int CountSelectableOptions()
        {
            return (Groups ?? new ObservableCollection<ProviderFilterGroup>())
                .Where(group => group != null)
                .SelectMany(group => (group.Platforms ?? new ObservableCollection<PlatformFilterOption>())
                    .Where(option => !string.IsNullOrWhiteSpace(option?.PlatformName))
                    .Select(option => $"{group.ProviderKey}|{option.PlatformName.Trim()}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count();
        }

        private bool HasSelectedOption()
        {
            return (Groups ?? new ObservableCollection<ProviderFilterGroup>())
                .Any(group => group?.HasAnySelected == true);
        }
    }

    internal static class GridControlBarText
    {
        public static string Get(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
