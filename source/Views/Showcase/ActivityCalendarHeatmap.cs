using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Cell and label sizes shared by the heatmap and its weekday gutter, derived from the
    /// element's inherited font size so the calendar scales with the plugin's text sizing
    /// instead of stretching to fill the widget.
    /// </summary>
    internal readonly struct ActivityCalendarMetrics
    {
        public ActivityCalendarMetrics(double fontSize)
        {
            var baseline = Math.Max(8, double.IsNaN(fontSize) ? 12 : fontSize);
            CellBox = Math.Ceiling(baseline) + 2;
            LabelFontSize = Math.Max(7, Math.Round(baseline * 0.75));
            MonthBandHeight = Math.Ceiling(LabelFontSize) + 5;
        }

        /// <summary>Cell box size including margins; also the weekday label row height.</summary>
        public double CellBox { get; }

        public double LabelFontSize { get; }

        public double MonthBandHeight { get; }

        public static ActivityCalendarMetrics For(FrameworkElement element)
        {
            return new ActivityCalendarMetrics(TextElement.GetFontSize(element));
        }
    }

    /// <summary>
    /// Draws the activity heatmap as a single visual instead of one element per day cell.
    /// A year (or more) of cells as retained Border elements makes page switches and
    /// scrolling drag; OnRender keeps the whole calendar one drawing and serves tooltips
    /// and the day popup (the day's unlocks, opened by clicking a cell) from mouse hit tests.
    /// </summary>
    public sealed class ActivityCalendarHeatmap : FrameworkElement
    {
        private const double CellCornerRadius = 2;
        private const double CellMargin = 1;
        private const string DayPopupTemplateKey = "ShowcaseActivityDayPopupTemplate";
        private static readonly double[] IntensityOpacity = { 0.30, 0.55, 0.78, 1.0 };

        public static readonly DependencyProperty WeeksProperty =
            DependencyProperty.Register(
                nameof(Weeks),
                typeof(IReadOnlyList<ActivityCalendarWeekViewModel>),
                typeof(ActivityCalendarHeatmap),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.AffectsMeasure |
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, __) => ((ActivityCalendarHeatmap)d).OnWeeksChanged()));

        public IReadOnlyList<ActivityCalendarWeekViewModel> Weeks
        {
            get => (IReadOnlyList<ActivityCalendarWeekViewModel>)GetValue(WeeksProperty);
            set => SetValue(WeeksProperty, value);
        }

        public static readonly DependencyProperty ShowMonthLabelsProperty =
            DependencyProperty.Register(
                nameof(ShowMonthLabels),
                typeof(bool),
                typeof(ActivityCalendarHeatmap),
                new FrameworkPropertyMetadata(
                    true,
                    FrameworkPropertyMetadataOptions.AffectsMeasure |
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public bool ShowMonthLabels
        {
            get => (bool)GetValue(ShowMonthLabelsProperty);
            set => SetValue(ShowMonthLabelsProperty, value);
        }

        public ActivityCalendarHeatmap()
        {
            AddVisualChild(_hoverVisual);
            // Loaded re-fires when the widget host is re-parented during dashboard rebuilds,
            // without a guaranteed intervening Unloaded; the hooked guard keeps the static
            // event from stacking handlers that would root this control permanently.
            Loaded += (_, __) =>
            {
                if (!_appearanceHooked)
                {
                    _appearanceHooked = true;
                    PlayniteAchievements.Models.Achievements.RarityAppearanceHelper
                        .AppearanceChanged += OnAppearanceChanged;
                }
            };
            Unloaded += (_, __) =>
            {
                if (_appearanceHooked)
                {
                    _appearanceHooked = false;
                    PlayniteAchievements.Models.Achievements.RarityAppearanceHelper
                        .AppearanceChanged -= OnAppearanceChanged;
                }

                CloseToolTip();
                CloseDayPopup();
            };
        }

        private bool _appearanceHooked;

        /// <summary>Overlay for the hover outline; visual children draw above OnRender content.</summary>
        private readonly DrawingVisual _hoverVisual = new DrawingVisual();

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _hoverVisual;
        }

        /// <summary>
        /// The day popup is a logical child so its content resolves resources (the popup
        /// template's brushes, the row menu's styles) through the widget template tree rather
        /// than depending on Popup's placement-target inheritance.
        /// </summary>
        protected override IEnumerator LogicalChildren =>
            _popup == null ? base.LogicalChildren : EnumeratePopup();

        private IEnumerator EnumeratePopup()
        {
            yield return _popup;
        }

        /// <summary>A replaced calendar invalidates both the hovered and the opened cell index.</summary>
        private void OnWeeksChanged()
        {
            ResetHover();
            CloseDayPopup();
        }

        private void OnAppearanceChanged(object sender, EventArgs e)
        {
            _intensityBrushes = null;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var metrics = ActivityCalendarMetrics.For(this);
            var width = (Weeks?.Count ?? 0) * metrics.CellBox;
            var height = (ShowMonthLabels ? metrics.MonthBandHeight : 0) + 7 * metrics.CellBox;
            return new Size(Math.Max(0, width), Math.Max(0, height));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var weeks = Weeks;
            if (weeks == null || weeks.Count == 0)
            {
                return;
            }

            // A transparent backdrop keeps mouse hit testing alive over the gaps between cells.
            drawingContext.DrawRectangle(
                Brushes.Transparent,
                null,
                new Rect(0, 0, RenderSize.Width, RenderSize.Height));

            var metrics = ActivityCalendarMetrics.For(this);
            var cellBox = metrics.CellBox;
            var cell = Math.Max(1, cellBox - CellMargin * 2);
            var top = ShowMonthLabels ? metrics.MonthBandHeight : 0;

            EnsureRenderResources();
            var empty = _emptyBrush;
            var text = _textBrush;
            var intensityBrushes = _intensityBrushes;
            var typeface = _typeface;
            var pixelsPerDip = _pixelsPerDip;

            for (var weekIndex = 0; weekIndex < weeks.Count; weekIndex++)
            {
                var week = weeks[weekIndex];
                if (week == null)
                {
                    continue;
                }

                var x = weekIndex * cellBox;
                if (ShowMonthLabels && !string.IsNullOrEmpty(week.MonthLabel))
                {
                    var label = new FormattedText(
                        week.MonthLabel,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        metrics.LabelFontSize,
                        text,
                        pixelsPerDip);
                    drawingContext.PushOpacity(0.7);
                    drawingContext.DrawText(label, new Point(x + CellMargin, 1));
                    drawingContext.Pop();
                }

                var days = week.Days;
                if (days == null)
                {
                    continue;
                }

                for (var dayIndex = 0; dayIndex < days.Count && dayIndex < 7; dayIndex++)
                {
                    var day = days[dayIndex];
                    if (day == null || day.Intensity < 0)
                    {
                        continue;
                    }

                    var rect = new Rect(
                        x + CellMargin,
                        top + dayIndex * cellBox + CellMargin,
                        cell,
                        cell);
                    var brush = day.Intensity == 0
                        ? empty
                        : intensityBrushes[Math.Min(day.Intensity, 4) - 1];
                    drawingContext.DrawRoundedRectangle(brush, null, rect, CellCornerRadius, CellCornerRadius);
                }
            }
        }

        private Brush _emptyBrush;
        private Brush _textBrush;
        private Brush[] _intensityBrushes;
        private Pen _hoverPen;
        private Typeface _typeface;
        private double _pixelsPerDip;

        /// <summary>
        /// Resolves the theme brushes, the derived intensity ramp, and the typeface once instead
        /// of on every render pass. Dropped when the appearance changes so a recolor is picked up.
        /// </summary>
        private void EnsureRenderResources()
        {
            if (_intensityBrushes != null)
            {
                return;
            }

            var accent = TryFindResource("PlayAch.Brush.Accent") as Brush ?? Brushes.SteelBlue;
            _emptyBrush = TryFindResource("PlayAch.Brush.Overlay.Tint.08") as Brush ??
                new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            _textBrush = TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.Gray;
            _intensityBrushes = BuildIntensityBrushes(accent);
            // The text brush contrasts both the empty tint and every accent intensity, so the
            // outline stays visible on any cell in either theme.
            _hoverPen = new Pen(_textBrush, 1);
            if (_hoverPen.CanFreeze)
            {
                _hoverPen.Freeze();
            }

            var fontFamily = TextElement.GetFontFamily(this) ?? new FontFamily("Segoe UI");
            _typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _intensityBrushes = null;
            InvalidateVisual();
        }

        private ToolTip _toolTip;
        private int _hoverWeekIndex = -1;
        private int _hoverDayIndex = -1;

        /// <summary>
        /// The day popup is this element's logical child, so mouse events raised inside it (and,
        /// while it holds capture, anywhere) bubble here with a position that can land on a cell.
        /// Only events that originated on the calendar itself, with no popup open, drive the hover.
        /// </summary>
        private bool IsOwnMouseEvent(RoutedEventArgs e) =>
            ReferenceEquals(e.OriginalSource, this);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!IsOwnMouseEvent(e) || _popup?.IsOpen == true)
            {
                return;
            }

            UpdateHoverToolTip(e.GetPosition(this));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            ResetHover();
        }

        /// <summary>Clears the hover outline, cursor, and tooltip; also runs when Weeks is replaced,
        /// because a stale outline would point at whatever cell now occupies the old index.</summary>
        private void ResetHover()
        {
            _hoverWeekIndex = -1;
            _hoverDayIndex = -1;
            _pressWeekIndex = -1;
            _pressDayIndex = -1;
            Cursor = null;
            RenderHoverIndicator();
            CloseToolTip();
        }

        /// <summary>
        /// Resolves the day cell under <paramref name="position"/>: a real (non-placeholder) cell
        /// with a tooltip, its week and day indices, and its box rect in this element's coordinates.
        /// </summary>
        private bool TryHitTestCell(
            Point position,
            out int weekIndex,
            out int dayIndex,
            out ActivityCalendarDayViewModel day,
            out Rect cellRect)
        {
            weekIndex = -1;
            dayIndex = -1;
            day = null;
            cellRect = Rect.Empty;

            var weeks = Weeks;
            if (weeks == null || weeks.Count == 0)
            {
                return false;
            }

            var metrics = ActivityCalendarMetrics.For(this);
            var cellBox = metrics.CellBox;
            var top = ShowMonthLabels ? metrics.MonthBandHeight : 0;
            if (position.Y < top || position.X < 0)
            {
                return false;
            }

            var week = (int)(position.X / cellBox);
            var dayOfWeek = (int)((position.Y - top) / cellBox);
            if (week >= weeks.Count || dayOfWeek >= 7)
            {
                return false;
            }

            var days = weeks[week]?.Days;
            var cell = days != null && dayOfWeek < days.Count ? days[dayOfWeek] : null;
            if (cell == null || !cell.IsDay)
            {
                return false;
            }

            weekIndex = week;
            dayIndex = dayOfWeek;
            day = cell;
            cellRect = new Rect(week * cellBox, top + dayOfWeek * cellBox, cellBox, cellBox);
            return true;
        }

        /// <summary>
        /// Serves the tooltip for the day cell under the pointer. The tooltip service positions
        /// its popup once at open, so over this single-element calendar it would sit still while
        /// the pointer crosses cells; instead the element owns one ToolTip and reopens it against
        /// the hovered cell's rect on every cell change, so the popup tracks the pointer.
        /// </summary>
        private void UpdateHoverToolTip(Point position)
        {
            if (Weeks == null || Weeks.Count == 0)
            {
                return;
            }

            if (!TryHitTestCell(position, out var weekIndex, out var dayIndex, out var day, out var cellRect))
            {
                ResetHover();
                return;
            }

            if (weekIndex == _hoverWeekIndex && dayIndex == _hoverDayIndex)
            {
                return;
            }

            _hoverWeekIndex = weekIndex;
            _hoverDayIndex = dayIndex;
            // Only cells that open the day popup read as clickable.
            Cursor = day.HasUnlocks ? Cursors.Hand : null;
            RenderHoverIndicator();
            ShowToolTip(day.Tooltip, cellRect);
        }

        private Popup _popup;
        private ContentPresenter _popupContent;
        private IInputElement _focusBeforePopup;
        private int _popupWeekIndex = -1;
        private int _popupDayIndex = -1;
        private int _pressWeekIndex = -1;
        private int _pressDayIndex = -1;
        private bool _pressClosedPopup;

        /// <summary>
        /// A click is a press and release on the same cell. The popup opens on the release rather
        /// than the press because a Popup with StaysOpen false also closes on a button release
        /// outside its capture, so one opened on the press would close on the same click.
        /// </summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _pressWeekIndex = -1;
            _pressDayIndex = -1;
            if (e.Handled || !IsOwnMouseEvent(e) || e.ClickCount != 1 ||
                !TryHitTestCell(e.GetPosition(this), out var weekIndex, out var dayIndex, out var day, out _) ||
                !day.HasUnlocks)
            {
                _pressClosedPopup = false;
                return;
            }

            // The press is left unhandled so the dashboard's own press bookkeeping still sees it;
            // only the release that completes the click is claimed.
            _pressWeekIndex = weekIndex;
            _pressDayIndex = dayIndex;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            var pressWeekIndex = _pressWeekIndex;
            var pressDayIndex = _pressDayIndex;
            var pressClosedPopup = _pressClosedPopup;
            _pressWeekIndex = -1;
            _pressDayIndex = -1;
            _pressClosedPopup = false;
            if (e.Handled || !IsOwnMouseEvent(e) || pressWeekIndex < 0 ||
                !TryHitTestCell(e.GetPosition(this), out var weekIndex, out var dayIndex, out var day, out var cellRect) ||
                weekIndex != pressWeekIndex || dayIndex != pressDayIndex || !day.HasUnlocks)
            {
                return;
            }

            e.Handled = true;
            if (pressClosedPopup)
            {
                // The press that started this click closed the popup on this same cell, so the
                // click toggles it off instead of reopening it.
                return;
            }

            CloseToolTip();
            ShowDayPopup(day, weekIndex, dayIndex, cellRect);
        }

        /// <summary>
        /// Opens the day popup under <paramref name="cellRect"/>. The popup owns its content
        /// presenter; the template comes from the widget resources so the list markup stays in
        /// XAML beside the calendar template.
        /// </summary>
        private void ShowDayPopup(ActivityCalendarDayViewModel day, int weekIndex, int dayIndex, Rect cellRect)
        {
            if (_popup == null)
            {
                _popupContent = new ContentPresenter { Focusable = true };
                _popupContent.PreviewKeyDown += OnPopupPreviewKeyDown;
                _popup = new Popup
                {
                    PlacementTarget = this,
                    Placement = PlacementMode.Bottom,
                    VerticalOffset = 4,
                    StaysOpen = false,
                    AllowsTransparency = true,
                    PopupAnimation = PopupAnimation.Fade,
                    Child = _popupContent
                };
                _popup.Opened += OnPopupOpened;
                _popup.Closed += OnPopupClosed;
                AddLogicalChild(_popup);
            }

            // Like the tooltip, a popup's position is fixed at open time, so moving to another
            // cell closes and reopens it against the new rect.
            _popup.IsOpen = false;
            _popupContent.ContentTemplate = TryFindResource(DayPopupTemplateKey) as DataTemplate;
            _popupContent.Content = day;
            _popup.PlacementRectangle = cellRect;
            _popupWeekIndex = weekIndex;
            _popupDayIndex = dayIndex;
            _focusBeforePopup = Keyboard.FocusedElement;
            _popup.IsOpen = true;
        }

        private void CloseDayPopup()
        {
            if (_popup != null)
            {
                _popup.IsOpen = false;
            }
        }

        /// <summary>Takes keyboard focus so Escape closes the popup; restored on close.</summary>
        private void OnPopupOpened(object sender, EventArgs e)
        {
            Keyboard.Focus(_popupContent);
        }

        private void OnPopupClosed(object sender, EventArgs e)
        {
            var weekIndex = _popupWeekIndex;
            var dayIndex = _popupDayIndex;
            _popupWeekIndex = -1;
            _popupDayIndex = -1;

            // Closed by a press on the cell it was opened for: remember that so the release that
            // completes the click does not reopen it. A press anywhere else clears the flag.
            _pressClosedPopup = Mouse.LeftButton == MouseButtonState.Pressed &&
                TryHitTestCell(Mouse.GetPosition(this), out var pressWeek, out var pressDay, out _, out _) &&
                pressWeek == weekIndex && pressDay == dayIndex;

            // Hand focus back the way a context menu does; by now the popup window may already
            // have released it, leaving nothing focused, so the restore is not gated on it.
            if (_focusBeforePopup != null &&
                (Keyboard.FocusedElement == null || _popupContent.IsKeyboardFocusWithin))
            {
                Keyboard.Focus(_focusBeforePopup);
            }

            _focusBeforePopup = null;
            _popupContent.Content = null;
        }

        private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseDayPopup();
            }
        }

        /// <summary>
        /// Draws the outline around the hovered day cell into the dedicated overlay visual, so
        /// pointer movement never re-renders the whole calendar drawing.
        /// </summary>
        private void RenderHoverIndicator()
        {
            using (var context = _hoverVisual.RenderOpen())
            {
                if (_hoverWeekIndex < 0 || _hoverDayIndex < 0)
                {
                    return; // an empty drawing clears the previous outline
                }

                EnsureRenderResources();
                var metrics = ActivityCalendarMetrics.For(this);
                var cellBox = metrics.CellBox;
                var cell = Math.Max(1, cellBox - CellMargin * 2);
                var top = ShowMonthLabels ? metrics.MonthBandHeight : 0;
                var rect = new Rect(
                    _hoverWeekIndex * cellBox + CellMargin,
                    top + _hoverDayIndex * cellBox + CellMargin,
                    cell,
                    cell);
                context.DrawRoundedRectangle(null, _hoverPen, rect, CellCornerRadius, CellCornerRadius);
            }
        }

        private void ShowToolTip(string content, Rect cellRect)
        {
            if (_toolTip == null)
            {
                // Above the cell: the pointer's cursor extends down from the hotspot and would
                // cover a tooltip placed below. WPF flips it below when there is no room above.
                _toolTip = new ToolTip
                {
                    PlacementTarget = this,
                    Placement = PlacementMode.Top,
                    VerticalOffset = -4
                };
            }

            // An open popup's position is fixed at open time, so a cell change closes and
            // reopens it against the new cell's rect; within one cell it stays untouched.
            _toolTip.IsOpen = false;
            _toolTip.Content = content;
            _toolTip.PlacementRectangle = cellRect;
            _toolTip.IsOpen = true;
        }

        private void CloseToolTip()
        {
            if (_toolTip != null)
            {
                _toolTip.IsOpen = false;
            }
        }

        private static Brush[] BuildIntensityBrushes(Brush accent)
        {
            var brushes = new Brush[IntensityOpacity.Length];
            var solid = accent as SolidColorBrush;
            for (var index = 0; index < IntensityOpacity.Length; index++)
            {
                Brush clone;
                if (solid != null)
                {
                    clone = new SolidColorBrush(solid.Color) { Opacity = IntensityOpacity[index] };
                }
                else
                {
                    clone = accent.CloneCurrentValue();
                    clone.Opacity = IntensityOpacity[index];
                }

                if (clone.CanFreeze)
                {
                    clone.Freeze();
                }

                brushes[index] = clone;
            }

            return brushes;
        }
    }

    /// <summary>
    /// Draws the weekday labels beside the heatmap using the same font-derived metrics, so
    /// the label rows line up with the cell rows. A separate element lets the labels sit
    /// outside the horizontal scroll area and stay visible while the calendar scrolls.
    /// </summary>
    public sealed class ActivityCalendarDayGutter : FrameworkElement
    {
        private const double RightPadding = 4;

        public static readonly DependencyProperty LabelsProperty =
            DependencyProperty.Register(
                nameof(Labels),
                typeof(IReadOnlyList<string>),
                typeof(ActivityCalendarDayGutter),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.AffectsMeasure |
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<string> Labels
        {
            get => (IReadOnlyList<string>)GetValue(LabelsProperty);
            set => SetValue(LabelsProperty, value);
        }

        /// <summary>Reserves the heatmap's month-label band so the first rows align.</summary>
        public static readonly DependencyProperty ShowMonthBandProperty =
            DependencyProperty.Register(
                nameof(ShowMonthBand),
                typeof(bool),
                typeof(ActivityCalendarDayGutter),
                new FrameworkPropertyMetadata(
                    true,
                    FrameworkPropertyMetadataOptions.AffectsMeasure |
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public bool ShowMonthBand
        {
            get => (bool)GetValue(ShowMonthBandProperty);
            set => SetValue(ShowMonthBandProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var labels = Labels;
            if (labels == null || labels.Count == 0)
            {
                return new Size(0, 0);
            }

            var metrics = ActivityCalendarMetrics.For(this);
            var width = 0d;
            foreach (var text in BuildLabelTexts(metrics))
            {
                width = Math.Max(width, text.Width);
            }

            return new Size(
                width + RightPadding,
                (ShowMonthBand ? metrics.MonthBandHeight : 0) + 7 * metrics.CellBox);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var labels = Labels;
            if (labels == null || labels.Count == 0)
            {
                return;
            }

            var metrics = ActivityCalendarMetrics.For(this);
            var top = ShowMonthBand ? metrics.MonthBandHeight : 0;
            var index = 0;
            drawingContext.PushOpacity(0.7);
            foreach (var text in BuildLabelTexts(metrics))
            {
                var y = top + index * metrics.CellBox + (metrics.CellBox - text.Height) / 2;
                drawingContext.DrawText(text, new Point(0, y));
                index++;
            }

            drawingContext.Pop();
        }

        private IEnumerable<FormattedText> BuildLabelTexts(ActivityCalendarMetrics metrics)
        {
            var labels = Labels;
            var textBrush = TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.Gray;
            var fontFamily = TextElement.GetFontFamily(this) ?? new FontFamily("Segoe UI");
            var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (var index = 0; index < labels.Count && index < 7; index++)
            {
                yield return new FormattedText(
                    labels[index] ?? string.Empty,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    metrics.LabelFontSize,
                    textBrush,
                    pixelsPerDip);
            }
        }
    }
}
