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
    public sealed class TimelineGranularityChoice : INotifyPropertyChanged
    {
        private bool _isEnabled = true;

        public TimelineGranularityChoice(TimelineGranularity value, string label)
        {
            Value = value;
            Label = label;
        }

        public TimelineGranularity Value { get; }

        public string Label { get; }

        /// <summary>False when the current window cannot honor this unit within the bar cap.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public override string ToString() => Label;
    }

    /// <summary>
    /// Picks a <see cref="TimeWindow"/> on one chip-height line: preset chips (7D, 1M, 3M, 1Y, All)
    /// plus a Custom chip that opens a popup holding the From and To date pickers, so the strip
    /// never grows and nothing below it shifts. A blank To means "until now"; a blank From means
    /// from the earliest data. While a custom range is active the chip shows the range itself.
    /// Optionally shows the chart granularity override at the right edge.
    /// </summary>
    /// <remarks>
    /// The template lives in Themes/Generic.xaml. Every gesture assigns a new immutable
    /// <see cref="TimeWindow"/> so hosts observe one atomic change per edit.
    /// </remarks>
    [TemplatePart(Name = PartPresets, Type = typeof(ItemsControl))]
    [TemplatePart(Name = PartCustomChip, Type = typeof(RadioButton))]
    [TemplatePart(Name = PartCustomPopup, Type = typeof(Popup))]
    [TemplatePart(Name = PartFrom, Type = typeof(DatePicker))]
    [TemplatePart(Name = PartTo, Type = typeof(DatePicker))]
    [TemplatePart(Name = PartClear, Type = typeof(Button))]
    [TemplatePart(Name = PartInlineFrom, Type = typeof(DatePicker))]
    [TemplatePart(Name = PartInlineTo, Type = typeof(DatePicker))]
    [TemplatePart(Name = PartInlineClear, Type = typeof(Button))]
    [TemplatePart(Name = PartGranularity, Type = typeof(ComboBox))]
    public class TimeWindowPicker : Control
    {
        private const string PartPresets = "PART_Presets";
        private const string PartCustomChip = "PART_CustomChip";
        private const string PartCustomPopup = "PART_CustomPopup";
        private const string PartFrom = "PART_From";
        private const string PartTo = "PART_To";
        private const string PartClear = "PART_Clear";
        private const string PartInlineFrom = "PART_InlineFrom";
        private const string PartInlineTo = "PART_InlineTo";
        private const string PartInlineClear = "PART_InlineClear";
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

        /// <summary>Granularity entries to disable because the window cannot honor them.</summary>
        public static readonly DependencyProperty UnavailableGranularitiesProperty = DependencyProperty.Register(
            nameof(UnavailableGranularities),
            typeof(IReadOnlyList<TimelineGranularity>),
            typeof(TimeWindowPicker),
            new PropertyMetadata(null, (d, e) => ((TimeWindowPicker)d).ApplyGranularityAvailability()));

        public static readonly DependencyProperty MaxDateProperty = DependencyProperty.Register(
            nameof(MaxDate),
            typeof(DateTime?),
            typeof(TimeWindowPicker),
            new PropertyMetadata(null, (d, e) => ((TimeWindowPicker)d).ApplyDateLimits()));

        public static readonly DependencyProperty MinDateProperty = DependencyProperty.Register(
            nameof(MinDate),
            typeof(DateTime?),
            typeof(TimeWindowPicker),
            new PropertyMetadata(null, (d, e) => ((TimeWindowPicker)d).OnMinDateChanged()));

        public static readonly DependencyProperty ChipStyleProperty = DependencyProperty.Register(
            nameof(ChipStyle), typeof(Style), typeof(TimeWindowPicker), new PropertyMetadata(null));

        /// <summary>
        /// True shows the From/To pickers on their own line under the chips, always visible, and
        /// hides the Custom chip (the widget settings rows); false keeps them in a popup under the
        /// Custom chip so the strip never grows (the chart strips).
        /// </summary>
        public static readonly DependencyProperty InlineCustomEditorProperty = DependencyProperty.Register(
            nameof(InlineCustomEditor),
            typeof(bool),
            typeof(TimeWindowPicker),
            new PropertyMetadata(false, (d, e) => ((TimeWindowPicker)d).OnInlineCustomEditorChanged()));

        private static readonly DependencyPropertyKey IsCustomOpenPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsCustomOpen), typeof(bool), typeof(TimeWindowPicker), new PropertyMetadata(false));

        public static readonly DependencyProperty IsCustomOpenProperty = IsCustomOpenPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey CustomChipTextPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(CustomChipText), typeof(string), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty CustomChipTextProperty = CustomChipTextPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey CustomChipToolTipPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(CustomChipToolTip), typeof(string), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty CustomChipToolTipProperty = CustomChipToolTipPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey HasErrorPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(HasError), typeof(bool), typeof(TimeWindowPicker), new PropertyMetadata(false));

        public static readonly DependencyProperty HasErrorProperty = HasErrorPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey ErrorTextPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(ErrorText), typeof(string), typeof(TimeWindowPicker), new PropertyMetadata(null));

        public static readonly DependencyProperty ErrorTextProperty = ErrorTextPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey IsFromBlankPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsFromBlank), typeof(bool), typeof(TimeWindowPicker), new PropertyMetadata(true));

        public static readonly DependencyProperty IsFromBlankProperty = IsFromBlankPropertyKey.DependencyProperty;

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
        private Popup _customPopup;
        private DatePicker _from;
        private DatePicker _to;
        private Button _clear;
        private ComboBox _granularity;
        private TextBox _fromTextBox;
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

        public IReadOnlyList<TimelineGranularity> UnavailableGranularities
        {
            get => (IReadOnlyList<TimelineGranularity>)GetValue(UnavailableGranularitiesProperty);
            set => SetValue(UnavailableGranularitiesProperty, value);
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

        public Style ChipStyle
        {
            get => (Style)GetValue(ChipStyleProperty);
            set => SetValue(ChipStyleProperty, value);
        }

        public bool InlineCustomEditor
        {
            get => (bool)GetValue(InlineCustomEditorProperty);
            set => SetValue(InlineCustomEditorProperty, value);
        }

        /// <summary>Whether the custom-range popup is open.</summary>
        public bool IsCustomOpen => (bool)GetValue(IsCustomOpenProperty);

        /// <summary>The Custom chip's caption: the range while one is active, otherwise "Custom".</summary>
        public string CustomChipText => (string)GetValue(CustomChipTextProperty);

        public string CustomChipToolTip => (string)GetValue(CustomChipToolTipProperty);

        public bool HasError => (bool)GetValue(HasErrorProperty);

        public string ErrorText => (string)GetValue(ErrorTextProperty);

        /// <summary>True while the From picker holds neither a date nor typed text (shows the blank marker).</summary>
        public bool IsFromBlank => (bool)GetValue(IsFromBlankProperty);

        /// <summary>True while the To picker holds neither a date nor typed text (shows the blank marker).</summary>
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
            _customPopup = GetTemplateChild(PartCustomPopup) as Popup;
            // The template holds both editor sets; only the one for the current mode is wired.
            var inline = InlineCustomEditor;
            _from = GetTemplateChild(inline ? PartInlineFrom : PartFrom) as DatePicker;
            _to = GetTemplateChild(inline ? PartInlineTo : PartTo) as DatePicker;
            _clear = GetTemplateChild(inline ? PartInlineClear : PartClear) as Button;
            _granularity = GetTemplateChild(PartGranularity) as ComboBox;

            if (_customChip != null)
            {
                _customChip.Click += CustomChip_Click;
            }

            if (_customPopup != null)
            {
                _customPopup.Opened += CustomPopup_Opened;
                _customPopup.Closed += CustomPopup_Closed;
                _customPopup.PreviewKeyDown += CustomPopup_PreviewKeyDown;
            }

            foreach (var picker in new[] { _from, _to })
            {
                if (picker == null)
                {
                    continue;
                }

                picker.SelectedDateChanged += DatePicker_SelectedDateChanged;
                picker.DateValidationError += DatePicker_DateValidationError;
                picker.Loaded += DatePicker_Loaded;
                picker.GotKeyboardFocus += DatePicker_GotKeyboardFocus;
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

        private void OnInlineCustomEditorChanged()
        {
            ClosePopup();
            if (Template != null)
            {
                // Re-wire onto the other editor set.
                OnApplyTemplate();
            }
        }

        private void DetachParts()
        {
            if (_customChip != null)
            {
                _customChip.Click -= CustomChip_Click;
            }

            if (_customPopup != null)
            {
                _customPopup.Opened -= CustomPopup_Opened;
                _customPopup.Closed -= CustomPopup_Closed;
                _customPopup.PreviewKeyDown -= CustomPopup_PreviewKeyDown;
            }

            foreach (var picker in new[] { _from, _to })
            {
                if (picker == null)
                {
                    continue;
                }

                picker.SelectedDateChanged -= DatePicker_SelectedDateChanged;
                picker.DateValidationError -= DatePicker_DateValidationError;
                picker.Loaded -= DatePicker_Loaded;
                picker.GotKeyboardFocus -= DatePicker_GotKeyboardFocus;
            }

            if (_fromTextBox != null)
            {
                _fromTextBox.TextChanged -= PickerTextBox_TextChanged;
                _fromTextBox = null;
            }

            if (_toTextBox != null)
            {
                _toTextBox.TextChanged -= PickerTextBox_TextChanged;
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
                ClosePopup();
                var next = TimeWindow.FromPreset(chip.Preset);
                if (Equals(next, Window))
                {
                    SyncFromWindow();
                }
                else
                {
                    Window = next;
                }
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

        private void OnMinDateChanged()
        {
            ApplyDateLimits();
            UpdateCustomChipText();
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
                    _customChip.IsChecked = window.IsCustom || IsCustomOpen;
                }

                // While the popup is open, or an inline preset prefill is being edited, the
                // pickers belong to the user; they are re-seeded from the window otherwise.
                var editing = IsCustomOpen ||
                    (InlineCustomEditor && !window.IsCustom && (_from?.IsKeyboardFocusWithin == true || _to?.IsKeyboardFocusWithin == true));
                if (!editing || window.IsCustom)
                {
                    if (_from != null)
                    {
                        _from.SelectedDate = window.IsCustom ? window.From : null;
                    }

                    if (_to != null)
                    {
                        _to.SelectedDate = window.IsCustom ? window.To : null;
                    }
                }

                UpdateBlankMarkers();
                UpdateCustomChipText();
            }
            finally
            {
                _syncing = false;
            }
        }

        private void UpdateCustomChipText()
        {
            var window = Window ?? TimeWindow.All;
            var customLabel = ResourceProvider.GetString("LOCPlayAch_Common_Custom");
            if (window.IsCustom)
            {
                var range = TimeWindowText.Describe(window, MinDate);
                SetValue(CustomChipTextPropertyKey, range);
                SetValue(CustomChipToolTipPropertyKey, customLabel);
            }
            else
            {
                SetValue(CustomChipTextPropertyKey, customLabel);
                SetValue(CustomChipToolTipPropertyKey, null);
            }
        }

        private void ApplyGranularityAvailability()
        {
            var unavailable = UnavailableGranularities;
            foreach (var choice in GranularityItems ?? new TimelineGranularityChoice[0])
            {
                choice.IsEnabled = unavailable == null || !unavailable.Contains(choice.Value);
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
            if (_customPopup == null)
            {
                return;
            }

            if (_customPopup.IsOpen)
            {
                SyncFromWindow();
                return;
            }

            PrefillFromPreset();
            _customPopup.IsOpen = true;
        }

        // Prefills From with the preset's resolved start so the first edit freezes what the user
        // was looking at, but commits nothing until a date actually changes: opening the editor
        // and walking away must not turn a rolling preset into a fixed range.
        private void PrefillFromPreset()
        {
            var window = Window ?? TimeWindow.All;
            if (window.IsCustom)
            {
                return;
            }

            var range = window.Resolve(DateTime.Today, MinDate);
            _syncing = true;
            try
            {
                if (_from != null)
                {
                    _from.SelectedDate = window.IsUnbounded ? (DateTime?)null : range.Start;
                }

                if (_to != null)
                {
                    _to.SelectedDate = null;
                }

                UpdateBlankMarkers();
            }
            finally
            {
                _syncing = false;
            }
        }

        // The inline editor has no open gesture, so focusing a blank picker while a preset is
        // active plays the same prefill role as opening the popup.
        private void DatePicker_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!InlineCustomEditor || _syncing || Window?.IsCustom == true)
            {
                return;
            }

            if (_from?.SelectedDate == null && _to?.SelectedDate == null)
            {
                PrefillFromPreset();
            }
        }

        private void CustomPopup_Opened(object sender, EventArgs e)
        {
            SetValue(IsCustomOpenPropertyKey, true);
            if (_customChip != null)
            {
                _customChip.IsChecked = true;
            }

            UpdateBlankMarkers();
            _from?.Focus();
        }

        private void CustomPopup_Closed(object sender, EventArgs e)
        {
            SetValue(IsCustomOpenPropertyKey, false);
            ClearError();
            // Whatever was typed but not committed is dropped; the chips show the real window.
            SyncFromWindow();
        }

        private void CustomPopup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClosePopup();
                e.Handled = true;
            }
        }

        private void ClosePopup()
        {
            if (_customPopup != null && _customPopup.IsOpen)
            {
                _customPopup.IsOpen = false;
            }
        }

        private void DatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBlankMarkers();
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
            ClosePopup();
            if (Equals(Window, TimeWindow.All))
            {
                SyncFromWindow();
            }
            else
            {
                Window = TimeWindow.All;
            }
        }

        private void Granularity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !(_granularity?.SelectedItem is TimelineGranularityChoice choice))
            {
                return;
            }

            Granularity = choice.Value;
        }

        // The plugin's DatePickerTextBox template has no watermark part, so the blank marker is
        // our own overlay and must hide while the user types.
        private void DatePicker_Loaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, _from) && _fromTextBox == null)
            {
                _fromTextBox = _from.Template?.FindName("PART_TextBox", _from) as TextBox;
                if (_fromTextBox != null)
                {
                    _fromTextBox.TextChanged += PickerTextBox_TextChanged;
                }
            }
            else if (ReferenceEquals(sender, _to) && _toTextBox == null)
            {
                _toTextBox = _to.Template?.FindName("PART_TextBox", _to) as TextBox;
                if (_toTextBox != null)
                {
                    _toTextBox.TextChanged += PickerTextBox_TextChanged;
                }
            }

            UpdateBlankMarkers();
        }

        private void PickerTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateBlankMarkers();

        private void UpdateBlankMarkers()
        {
            SetValue(IsFromBlankPropertyKey, IsBlank(_from, _fromTextBox));
            SetValue(IsToBlankPropertyKey, IsBlank(_to, _toTextBox));
        }

        private static bool IsBlank(DatePicker picker, TextBox textBox)
        {
            return picker == null ||
                (!picker.SelectedDate.HasValue && string.IsNullOrEmpty(textBox?.Text ?? picker.Text));
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
