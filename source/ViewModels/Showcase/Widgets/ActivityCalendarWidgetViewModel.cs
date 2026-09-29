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

        protected override void Refresh()
        {
            var calendar = Projection?.ActivityCalendar ?? new ShowcaseActivityCalendar();
            ShowEmpty = calendar.TotalCount == 0;
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
