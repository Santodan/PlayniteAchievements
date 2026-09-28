using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>One preset chip of a <see cref="TimeWindowPicker"/>.</summary>
    public sealed class TimeWindowPresetChip : INotifyPropertyChanged
    {
        private bool _isSelected;

        public TimeWindowPresetChip(TimelineRange preset, string label)
        {
            Preset = preset;
            Label = label;
        }

        public TimelineRange Preset { get; }

        public string Label { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>One entry of the granularity dropdown.</summary>
    public sealed class TimelineGranularityChoice
    {
        public TimelineGranularityChoice(TimelineGranularity value, string label)
        {
            Value = value;
            Label = label;
        }

        public TimelineGranularity Value { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>
    /// Picks a <see cref="TimeWindow"/>: preset chips (7D, 1M, 3M, 1Y, All) plus a Custom chip that
    /// reveals From/To date pickers. A blank To means "until now"; a blank From means from the
    /// earliest data. Optionally shows the chart granularity override. In compact mode only the
    /// chips render, and the Custom chip marks a custom window and runs <see cref="MoreCommand"/>
    /// (the widget settings dialog) instead of expanding inline.
    /// </summary>
    /// <remarks>
    /// The template lives in Themes/Generic.xaml. Every gesture assigns a new immutable
    /// <see cref="TimeWindow"/> so hosts observe one atomic change per edit.
    /// </remarks>
    [TemplatePart(Name = PartPresets, Type = typeof(ItemsControl))]
    [TemplatePart(Name = PartCustomChip, Type = typeof(RadioButton))]
    [TemplatePart(Name = PartFrom, Type = typeof(DatePicker))]
    [TemplatePart(Name = PartTo, Type = typeof(DatePicker))]
    [TemplatePart(Name = PartClear, Type = typeof(Button))]
    [TemplatePart(Name = PartGranularity, Type = typeof(ComboBox))]
    public class TimeWindowPicker : Control
    {
        private const string PartPresets = "PART_Presets";
        private const string PartCustomChip = "PART_CustomChip";
        private const string PartFrom = "PART_From";
        private const string PartTo = "PART_To";
        private const string PartClear = "PART_Clear";
        private const string PartGranularity = "PART_Granularity";

        static TimeWindowPicker()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(TimeWindowPicker),
                new FrameworkPropertyMetadata(typeof(TimeWindowPicker)));
        }

        public static readonly DependencyProperty WindowProperty = DependencyProperty.Register(
            nameof(Window),
            typeof(TimeWindow),
            typeof(TimeWindowPicker),
            new FrameworkPropertyMetadata(
                TimeWindow.All,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((TimeWindowPicker)d).OnWindowChanged()));

        public static readonly DependencyProperty PresetsProperty = DependencyProperty.Register(
            nameof(Presets),
            typeof(IReadOnlyList<TimelineRange>),
            typeof(TimeWindowPicker),
            new PropertyMetadata(null, (d, e) => ((TimeWindowPicker)d).RebuildChips()));

        public static readonly DependencyProperty GranularityProperty = DependencyProperty.Register(
            nameof(Granularity),
            typeof(TimelineGranularity),
            typeof(TimeWindowPicker),
            new FrameworkPropertyMetadata(
                TimelineGranularity.Auto,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((TimeWindowPicker)d).OnGranularityChanged()));

        public static readonly DependencyProperty ShowGranularityProperty = DependencyProperty.Register(
            nameof(ShowGranularity), typeof(bool), typeof(TimeWindowPicker), new PropertyMetadata(false));

        public static readonly DependencyProperty MaxDateProperty = DependencyProperty.Register(
            nameof(MaxDate),
            typeof(DateTime?),
            typeof(TimeWindowPicker),
            new PropertyMetadata(null, (d, e) => ((TimeWindowPicker)d).ApplyDateLimits()));

        public static readonly DependencyProperty MinDateProperty = DependencyProperty.Register(
            nameof(MinDate),
            typeof(DateTime?),
            typeof(TimeWindowPicker),
            new PropertyMetadata(null, (d, e) => ((TimeWindowPicker)d).ApplyDateLimits()));

        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
            nameof(IsCompact),
            typeof(bool),
            typeof(TimeWindowPicker),
            new PropertyMetadata(false, (d, e) => ((TimeWindowPicker)d).SyncFromWindow()));

        public static readonly DependencyProperty MoreCommandProperty = DependencyProperty.Register(
            nameof(MoreCommand), typeof(ICommand), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty IsCustomExpandedProperty = DependencyProperty.Register(
            nameof(IsCustomExpanded), typeof(bool), typeof(TimeWindowPicker), new PropertyMetadata(false));

        public static readonly DependencyProperty ChipStyleProperty = DependencyProperty.Register(
            nameof(ChipStyle), typeof(Style), typeof(TimeWindowPicker), new PropertyMetadata(null));

        private static readonly DependencyPropertyKey HasErrorPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(HasError), typeof(bool), typeof(TimeWindowPicker), new PropertyMetadata(false));

        public static readonly DependencyProperty HasErrorProperty = HasErrorPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey ErrorTextPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(ErrorText), typeof(string), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty ErrorTextProperty = ErrorTextPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey IsToBlankPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsToBlank), typeof(bool), typeof(TimeWindowPicker), new PropertyMetadata(true));

        public static readonly DependencyProperty IsToBlankProperty = IsToBlankPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey PresetItemsPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(PresetItems), typeof(ObservableCollection<TimeWindowPresetChip>), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty PresetItemsProperty = PresetItemsPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey GranularityItemsPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(GranularityItems), typeof(IReadOnlyList<TimelineGranularityChoice>), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty GranularityItemsProperty = GranularityItemsPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey ChipGroupNamePropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(ChipGroupName), typeof(string), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty ChipGroupNameProperty = ChipGroupNamePropertyKey.DependencyProperty;

        private ItemsControl _presets;
        private RadioButton _customChip;
        private DatePicker _from;
        private DatePicker _to;
        private Button _clear;
        private ComboBox _granularity;
        private TextBox _toTextBox;
        private bool _syncing;

        public TimeWindowPicker()
        {
            Focusable = false;
            SetValue(PresetItemsPropertyKey, new ObservableCollection<TimeWindowPresetChip>());
            // Each picker is its own radio group; the overview instantiates two strips on one view model.
            SetValue(ChipGroupNamePropertyKey, "PlayAch.TimeWindow." + Guid.NewGuid().ToString("N"));
            SetValue(GranularityItemsPropertyKey, new[]
            {
                new TimelineGranularityChoice(TimelineGranularity.Auto, ResourceProvider.GetString("LOCAutomatic")),
                new TimelineGranularityChoice(TimelineGranularity.Day, ResourceProvider.GetString("LOCPlayAch_Granularity_Day")),
                new TimelineGranularityChoice(TimelineGranularity.Week, ResourceProvider.GetString("LOCPlayAch_Granularity_Week")),
                new TimelineGranularityChoice(TimelineGranularity.Month, ResourceProvider.GetString("LOCPlayAch_Granularity_Month"))
            });
            RebuildChips();
        }

        /// <summary>Raised after <see cref="Window"/> changes, from any source.</summary>
        public event EventHandler WindowChanged;

        /// <summary>Raised after <see cref="Granularity"/> changes, from any source.</summary>
        public event EventHandler GranularityChanged;

        public TimeWindow Window
        {
            get => (TimeWindow)GetValue(WindowProperty);
            set => SetValue(WindowProperty, value);
        }

        /// <summary>Presets offered as chips; null falls back to <see cref="TimeWindow.Presets"/>.</summary>
        public IReadOnlyList<TimelineRange> Presets
        {
            get => (IReadOnlyList<TimelineRange>)GetValue(PresetsProperty);
            set => SetValue(PresetsProperty, value);
        }

        public TimelineGranularity Granularity
        {
            get => (TimelineGranularity)GetValue(GranularityProperty);
            set => SetValue(GranularityProperty, value);
        }

        public bool ShowGranularity
        {
            get => (bool)GetValue(ShowGranularityProperty);
            set => SetValue(ShowGranularityProperty, value);
        }

        /// <summary>Latest selectable date; null means today. Later dates are blacked out.</summary>
        public DateTime? MaxDate
        {
            get => (DateTime?)GetValue(MaxDateProperty);
            set => SetValue(MaxDateProperty, value);
        }

        /// <summary>Earliest data date, when known; the calendars open no earlier than this.</summary>
        public DateTime? MinDate
        {
            get => (DateTime?)GetValue(MinDateProperty);
            set => SetValue(MinDateProperty, value);
        }

        /// <summary>Chips only; the Custom chip indicates a custom window and runs <see cref="MoreCommand"/>.</summary>
        public bool IsCompact
        {
            get => (bool)GetValue(IsCompactProperty);
            set => SetValue(IsCompactProperty, value);
        }

        public ICommand MoreCommand
        {
            get => (ICommand)GetValue(MoreCommandProperty);
            set => SetValue(MoreCommandProperty, value);
        }

        /// <summary>Whether the From/To row is shown; forced on while the window is custom.</summary>
        public bool IsCustomExpanded
        {
            get => (bool)GetValue(IsCustomExpandedProperty);
            set => SetValue(IsCustomExpandedProperty, value);
        }

        public Style ChipStyle
        {
            get => (Style)GetValue(ChipStyleProperty);
            set => SetValue(ChipStyleProperty, value);
        }

        public bool HasError => (bool)GetValue(HasErrorProperty);

        public string ErrorText => (string)GetValue(ErrorTextProperty);

        /// <summary>True while the To picker holds neither a date nor typed text (shows "Until now").</summary>
        public bool IsToBlank => (bool)GetValue(IsToBlankProperty);

        public ObservableCollection<TimeWindowPresetChip> PresetItems =>
            (ObservableCollection<TimeWindowPresetChip>)GetValue(PresetItemsProperty);

        public IReadOnlyList<TimelineGranularityChoice> GranularityItems =>
            (IReadOnlyList<TimelineGranularityChoice>)GetValue(GranularityItemsProperty);

        public string ChipGroupName => (string)GetValue(ChipGroupNameProperty);

        /// <summary>The focusable pieces in reading order, for fullscreen controller navigation.</summary>
        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>();
            if (_presets != null)
            {
                elements.AddRange(VisualTreeHelpers.FindVisualChildren<RadioButton>(_presets).Where(IsAvailable));
            }

            foreach (var element in new UIElement[] { _customChip, _from, _to, _clear, _granularity })
            {
                if (IsAvailable(element))
                {
                    elements.Add(element);
                }
            }

            return elements;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            DetachParts();

            _presets = GetTemplateChild(PartPresets) as ItemsControl;
            _customChip = GetTemplateChild(PartCustomChip) as RadioButton;
            _from = GetTemplateChild(PartFrom) as DatePicker;
            _to = GetTemplateChild(PartTo) as DatePicker;
            _clear = GetTemplateChild(PartClear) as Button;
            _granularity = GetTemplateChild(PartGranularity) as ComboBox;

            if (_customChip != null)
            {
                _customChip.Click += CustomChip_Click;
            }

            if (_from != null)
            {
                _from.SelectedDateChanged += DatePicker_SelectedDateChanged;
                _from.DateValidationError += DatePicker_DateValidationError;
            }

            if (_to != null)
            {
                _to.SelectedDateChanged += DatePicker_SelectedDateChanged;
                _to.DateValidationError += DatePicker_DateValidationError;
                _to.Loaded += ToPicker_Loaded;
            }

            if (_clear != null)
            {
                _clear.Click += Clear_Click;
            }

            if (_granularity != null)
            {
                _granularity.SelectionChanged += Granularity_SelectionChanged;
            }

            ApplyDateLimits();
            SyncFromWindow();
            SyncGranularity();
        }

        private void DetachParts()
        {
            if (_customChip != null)
            {
                _customChip.Click -= CustomChip_Click;
            }

            if (_from != null)
            {
                _from.SelectedDateChanged -= DatePicker_SelectedDateChanged;
                _from.DateValidationError -= DatePicker_DateValidationError;
            }

            if (_to != null)
            {
                _to.SelectedDateChanged -= DatePicker_SelectedDateChanged;
                _to.DateValidationError -= DatePicker_DateValidationError;
                _to.Loaded -= ToPicker_Loaded;
            }

            if (_toTextBox != null)
            {
                _toTextBox.TextChanged -= ToTextBox_TextChanged;
                _toTextBox = null;
            }

            if (_clear != null)
            {
                _clear.Click -= Clear_Click;
            }

            if (_granularity != null)
            {
                _granularity.SelectionChanged -= Granularity_SelectionChanged;
            }
        }

        private void RebuildChips()
        {
            var items = PresetItems;
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                item.PropertyChanged -= Chip_PropertyChanged;
            }

            items.Clear();
            foreach (var preset in Presets ?? TimeWindow.Presets)
            {
                var chip = new TimeWindowPresetChip(preset, TimelineRangeText.Describe(preset));
                chip.PropertyChanged += Chip_PropertyChanged;
                items.Add(chip);
            }

            SyncFromWindow();
        }

        private void Chip_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_syncing || e.PropertyName != nameof(TimeWindowPresetChip.IsSelected))
            {
                return;
            }

            if (sender is TimeWindowPresetChip chip && chip.IsSelected)
            {
                ClearError();
                Window = TimeWindow.FromPreset(chip.Preset);
            }
        }

        private void OnWindowChanged()
        {
            SyncFromWindow();
            WindowChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnGranularityChanged()
        {
            SyncGranularity();
            GranularityChanged?.Invoke(this, EventArgs.Empty);
        }

        // Pushes the current window into the chips and pickers without committing anything back.
        private void SyncFromWindow()
        {
            if (_syncing)
            {
                return;
            }

            var window = Window ?? TimeWindow.All;
            _syncing = true;
            try
            {
                var items = PresetItems;
                if (items != null)
                {
                    foreach (var chip in items)
                    {
                        chip.IsSelected = window.Preset.HasValue && chip.Preset == window.Preset.Value;
                    }
                }

                if (_customChip != null)
                {
                    _customChip.IsChecked = window.IsCustom;
                    _customChip.ToolTip = window.IsCustom && IsCompact
                        ? TimeWindowText.Describe(window, MinDate)
                        : null;
                }

                if (window.IsCustom)
                {
                    IsCustomExpanded = true;
                    if (_from != null)
                    {
                        _from.SelectedDate = window.From;
                    }

                    if (_to != null)
                    {
                        _to.SelectedDate = window.To;
                    }
                }
                else
                {
                    if (_from != null)
                    {
                        _from.SelectedDate = null;
                    }

                    if (_to != null)
                    {
                        _to.SelectedDate = null;
                    }
                }

                UpdateIsToBlank();
            }
            finally
            {
                _syncing = false;
            }
        }

        private void SyncGranularity()
        {
            if (_granularity == null)
            {
                return;
            }

            _syncing = true;
            try
            {
                _granularity.SelectedItem = GranularityItems?.FirstOrDefault(item => item.Value == Granularity);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void CustomChip_Click(object sender, RoutedEventArgs e)
        {
            if (IsCompact)
            {
                // The chip only indicates; the dialog owns the editing. Restore its checked state.
                var command = MoreCommand;
                SyncFromWindow();
                if (command != null && command.CanExecute(null))
                {
                    command.Execute(null);
                }

                return;
            }

            IsCustomExpanded = true;
            if (Window?.IsCustom == true)
            {
                return;
            }

            // Prefill From with the preset's resolved start so the first edit freezes what the
            // user was looking at, but commit nothing until a date actually changes: clicking
            // Custom and walking away must not turn a rolling preset into a fixed range.
            var range = (Window ?? TimeWindow.All).Resolve(DateTime.Today, MinDate);
            _syncing = true;
            try
            {
                if (_from != null)
                {
                    _from.SelectedDate = (Window ?? TimeWindow.All).IsUnbounded ? (DateTime?)null : range.Start;
                }

                if (_to != null)
                {
                    _to.SelectedDate = null;
                }

                UpdateIsToBlank();
            }
            finally
            {
                _syncing = false;
            }

            _from?.Focus();
        }

        private void DatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateIsToBlank();
            if (_syncing)
            {
                return;
            }

            CommitCustom();
        }

        private void CommitCustom()
        {
            var from = _from?.SelectedDate;
            var to = _to?.SelectedDate;
            if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            {
                SetError(ResourceProvider.GetString("LOCPlayAch_Common_Validation_DateRangeOrder"));
                return;
            }

            ClearError();
            var next = TimeWindow.Custom(from, to);
            if (!Equals(next, Window))
            {
                Window = next;
            }
            else
            {
                SyncFromWindow();
            }
        }

        private void DatePicker_DateValidationError(object sender, DatePickerDateValidationErrorEventArgs e)
        {
            e.ThrowException = false;
            SetError(ResourceProvider.GetString("LOCPlayAch_Common_Validation_InvalidDateTime"));
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            ClearError();
            Window = TimeWindow.All;
        }

        private void Granularity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !(_granularity?.SelectedItem is TimelineGranularityChoice choice))
            {
                return;
            }

            Granularity = choice.Value;
        }

        private void ToPicker_Loaded(object sender, RoutedEventArgs e)
        {
            // The plugin's DatePickerTextBox template has no watermark part, so the "Until now"
            // placeholder is our own and must hide while the user types.
            if (_toTextBox != null || _to == null)
            {
                return;
            }

            _toTextBox = _to.Template?.FindName("PART_TextBox", _to) as TextBox;
            if (_toTextBox != null)
            {
                _toTextBox.TextChanged += ToTextBox_TextChanged;
            }

            UpdateIsToBlank();
        }

        private void ToTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateIsToBlank();

        private void UpdateIsToBlank()
        {
            var blank = _to == null ||
                (!_to.SelectedDate.HasValue && string.IsNullOrEmpty(_toTextBox?.Text ?? _to.Text));
            SetValue(IsToBlankPropertyKey, blank);
        }

        private void ApplyDateLimits()
        {
            foreach (var picker in new[] { _from, _to })
            {
                if (picker == null)
                {
                    continue;
                }

                var max = (MaxDate ?? DateTime.Today).Date;
                picker.DisplayDateEnd = max;
                picker.DisplayDateStart = MinDate?.Date;
                picker.BlackoutDates.Clear();
                if (max < DateTime.MaxValue.Date)
                {
                    picker.BlackoutDates.Add(new CalendarDateRange(max.AddDays(1), DateTime.MaxValue.Date));
                }
            }
        }

        private void SetError(string text)
        {
            SetValue(ErrorTextPropertyKey, text);
            SetValue(HasErrorPropertyKey, !string.IsNullOrWhiteSpace(text));
        }

        private void ClearError() => SetError(null);

        private static bool IsAvailable(UIElement element) =>
            element != null && element.IsVisible && element.IsEnabled;
    }
}
