using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>A selectable timeline range button.</summary>
    public sealed class TimelineRangeOptionViewModel
    {
        public TimelineRangeOptionViewModel(string label, TimelineRange value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public TimelineRange Value { get; }
    }

    /// <summary>
    /// Backs the Timeline widget: the reused LiveCharts column chart via TimelineViewModel with
    /// range buttons, identical at every size.
    /// </summary>
    public sealed class TimelineWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly TimelineViewModel _timeline = new TimelineViewModel();

        public TimelineWidgetViewModel()
        {
            SetRangeCommand = new RelayCommand(SetRange);
            Ranges = new[]
            {
                Option(TimelineRange.OneMonth, "LOCPlayAch_TimeRange_1M"),
                Option(TimelineRange.ThreeMonths, "LOCPlayAch_TimeRange_3M"),
                Option(TimelineRange.OneYear, "LOCPlayAch_TimeRange_1Y"),
                Option(TimelineRange.All, "LOCPlayAch_Common_All")
            };
        }

        public TimelineViewModel Timeline => _timeline;

        public IReadOnlyList<TimelineRangeOptionViewModel> Ranges { get; }

        public RelayCommand SetRangeCommand { get; }

        protected override void Refresh()
        {
            var counts = Projection?.Timeline ?? new Dictionary<DateTime, int>();
            _timeline.Window = ShowcaseTimelineOptions.GetWindow(Projection?.Instance);
            _timeline.Granularity = ShowcaseTimelineOptions.GetGranularity(Projection?.Instance);
            // The chart itself shows the empty caption when the window holds no unlocks.
            _timeline.SetCounts(counts.ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        private void SetRange(object parameter)
        {
            if (parameter is TimelineRange range)
            {
                ShowcaseTimelineOptions.SetWindow(Projection?.Instance, TimeWindow.FromPreset(range));
                ShowcaseConfigurationCommit.Commit();
            }
        }

        private static TimelineRangeOptionViewModel Option(TimelineRange value, string labelKey) =>
            new TimelineRangeOptionViewModel(ResourceProvider.GetString(labelKey), value);
    }
}
