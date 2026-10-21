using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// A single heatmap cell. Intensity -1 marks a placeholder that pads a partial week.
    /// The tooltip and the popup header are formatted on demand (only the hovered or clicked
    /// cell needs one), so a year of cells costs no string work.
    /// </summary>
    public sealed class ActivityCalendarDayViewModel
    {
        private static readonly ActivityCalendarDayViewModel PlaceholderCell =
            new ActivityCalendarDayViewModel(-1, default(DateTime), 0);

        private ActivityCalendarDayViewModel(
            int intensity,
            DateTime date,
            int count,
            IReadOnlyList<AchievementDisplayItem> unlocks = null)
        {
            Intensity = intensity;
            Date = date;
            Count = count;
            Unlocks = unlocks;
        }

        public static ActivityCalendarDayViewModel Placeholder => PlaceholderCell;

        public static ActivityCalendarDayViewModel ForDay(ShowcaseActivityDay day) =>
            new ActivityCalendarDayViewModel(day.Intensity, day.Date, day.Count, day.Unlocks);

        public static ActivityCalendarDayViewModel ForLegend(int intensity) =>
            new ActivityCalendarDayViewModel(intensity, default(DateTime), 0);

        public int Intensity { get; }

        public DateTime Date { get; }

        public int Count { get; }

        /// <summary>The day's unlocks in unlock order, or null; shown by the day popup.</summary>
        public IReadOnlyList<AchievementDisplayItem> Unlocks { get; }

        public bool HasUnlocks => Unlocks != null && Unlocks.Count > 0;

        /// <summary>False for the placeholder and legend cells, which have no date.</summary>
        public bool IsDay => Intensity >= 0 && Date != default(DateTime);

        public string Tooltip => FormatSummary("d");

        /// <summary>The popup header: the same summary as the tooltip with the full date.</summary>
        public string Header => FormatSummary("D");

        private string FormatSummary(string dateFormat)
        {
            if (!IsDay)
            {
                return null;
            }

            var culture = FormattingCulture.Current;
            return string.Format(
                culture,
                ResourceProvider.GetString("LOCPlayAch_Showcase_ActivityTooltipFormat"),
                Date.ToString(dateFormat, culture),
                Count);
        }
    }

    /// <summary>One Sunday-first week column with an optional month label.</summary>
    public sealed class ActivityCalendarWeekViewModel
    {
        public ActivityCalendarWeekViewModel(
            string monthLabel,
            IReadOnlyList<ActivityCalendarDayViewModel> days)
        {
            MonthLabel = monthLabel ?? string.Empty;
            Days = days;
        }

        public string MonthLabel { get; }

        public IReadOnlyList<ActivityCalendarDayViewModel> Days { get; }
    }

    /// <summary>
    /// Backs the ActivityCalendar widget: a contributions-style heatmap of unlocks per
    /// day, with its labels and legend at every size; the projection owns the window,
    /// the counts, and the intensity bucketing.
    /// </summary>
    public sealed class ActivityCalendarWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private IReadOnlyList<ActivityCalendarWeekViewModel> _weeks =
            Array.Empty<ActivityCalendarWeekViewModel>();
        private bool _showEmpty;

        // The calendar the week columns were built from; rebuilding them re-renders the whole
        // heatmap, so an unrelated refresh must leave them alone.
        private ShowcaseActivityCalendar _builtCalendar;

        public ActivityCalendarWidgetViewModel()
        {
            LegendCells = Enumerable.Range(0, 5)
                .Select(ActivityCalendarDayViewModel.ForLegend)
                .ToList();

            var dayNames = FormattingCulture.Current.DateTimeFormat.AbbreviatedDayNames;
            WeekdayLabels = new[]
            {
                dayNames[(int)DayOfWeek.Sunday],
                dayNames[(int)DayOfWeek.Monday],
                dayNames[(int)DayOfWeek.Tuesday],
                dayNames[(int)DayOfWeek.Wednesday],
                dayNames[(int)DayOfWeek.Thursday],
                dayNames[(int)DayOfWeek.Friday],
                dayNames[(int)DayOfWeek.Saturday]
            };
        }

        /// <summary>
        /// Replaced wholesale on refresh so the custom-drawn heatmap re-renders on the
        /// property change (it draws the list itself rather than templating items).
        /// </summary>
        public IReadOnlyList<ActivityCalendarWeekViewModel> Weeks
        {
            get => _weeks;
            private set => SetValue(ref _weeks, value);
        }

        /// <summary>The five intensity swatches of the Less-to-More legend; never changes.</summary>
        public IReadOnlyList<ActivityCalendarDayViewModel> LegendCells { get; }

        /// <summary>Sunday-first weekday labels, one per row; never changes.</summary>
        public IReadOnlyList<string> WeekdayLabels { get; }

        public bool ShowEmpty
        {
            get => _showEmpty;
            private set => SetValue(ref _showEmpty, value);
        }

        private bool _isLinked;
        private DateTime? _highlightedDay;

        /// <summary>Linked to the overview: a day click filters the overview instead of listing the day.</summary>
        public bool IsLinked
        {
            get => _isLinked;
            private set => SetValue(ref _isLinked, value);
        }

        /// <summary>The overview's single-day unlock filter, drawn as the selected day.</summary>
        public DateTime? HighlightedDay
        {
            get => _highlightedDay;
            private set => SetValue(ref _highlightedDay, value);
        }

        private bool _showControls;
        private TimeWindow _window = ShowcaseTimelineOptions.DefaultWindow;

        public bool ShowControls
        {
            get => _showControls;
            private set => SetValue(ref _showControls, value);
        }

        /// <summary>
        /// The window the picker in the controls shows. Picking one saves it to the widget and
        /// redraws the calendar from the snapshot already in hand.
        /// </summary>
        public TimeWindow Window
        {
            get => _window;
            set
            {
                if (value == null || Equals(_window, value))
                {
                    return;
                }

                SetValue(ref _window, value);
                var instance = Projection?.Instance;
                if (instance == null || Equals(ShowcaseTimelineOptions.GetWindow(instance), value))
                {
                    return;
                }

                ShowcaseControlBarStates.Store?.Save(instance.InstanceId, ShowcaseTimelineOptions.RangeOption, value.ToKey());
                BuildWeeks(ShowcaseWidgetProjectionService.BuildActivityCalendar(
                    Projection.Snapshot,
                    instance,
                    DateTime.Now.Date,
                    trimToUnlocks: Projection.IsLinked));
            }
        }

        protected override void Refresh()
        {
            ShowControls = ShowcaseWidgetOptions.GetControlBarItemVisible(
                Projection?.Instance,
                ShowcaseControlBarItems.TimeWindow);
            SetValue(ref _window, ShowcaseTimelineOptions.GetWindow(Projection?.Instance), nameof(Window));
            BuildWeeks(Projection?.ActivityCalendar ?? new ShowcaseActivityCalendar());
        }

        private void BuildWeeks(ShowcaseActivityCalendar calendar)
        {
            calendar = calendar ?? new ShowcaseActivityCalendar();
            ShowEmpty = calendar.TotalCount == 0;
            IsLinked = Projection?.IsLinked == true;
            var span = Projection?.HighlightedSpan;
            HighlightedDay = span.HasValue && span.Value.Start == span.Value.End
                ? span.Value.Start
                : (DateTime?)null;
            if (ReferenceEquals(_builtCalendar, calendar))
            {
                return;
            }

            _builtCalendar = calendar;
            var culture = FormattingCulture.Current;
            var days = calendar.Days ?? Array.Empty<ShowcaseActivityDay>();
            var weeks = new List<ActivityCalendarWeekViewModel>((days.Count / 7) + 1);
            var yearShown = false;
            for (var index = 0; index < days.Count; index += 7)
            {
                var cells = new List<ActivityCalendarDayViewModel>(7);
                string monthLabel = null;
                for (var offset = 0; offset < 7; offset++)
                {
                    var dayIndex = index + offset;
                    if (dayIndex >= days.Count)
                    {
                        cells.Add(ActivityCalendarDayViewModel.Placeholder);
                        continue;
                    }

                    var day = days[dayIndex];
                    if (day.Date.Day == 1)
                    {
                        // The first label of the window and every January carry the year so
                        // long ranges stay readable across year boundaries.
                        var withYear = !yearShown || day.Date.Month == 1;
                        monthLabel = day.Date.ToString(withYear ? "MMM yyyy" : "MMM", culture);
                        yearShown = true;
                    }

                    cells.Add(ActivityCalendarDayViewModel.ForDay(day));
                }

                weeks.Add(new ActivityCalendarWeekViewModel(monthLabel, cells));
            }

            Weeks = weeks;
        }
    }
}
