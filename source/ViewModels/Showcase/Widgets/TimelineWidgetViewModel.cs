using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Timeline widget: the shared unlocks-over-time chart via TimelineViewModel. The
    /// window and granularity are widget settings (Range and Granularity in the settings dialog),
    /// so the widget surface carries no controls of its own.
    /// </summary>
    public sealed class TimelineWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly TimelineViewModel _timeline = new TimelineViewModel();

        public TimelineViewModel Timeline => _timeline;

        protected override void Refresh()
        {
            var instance = Projection?.Instance;
            var counts = Projection?.Timeline ?? new Dictionary<DateTime, int>();
            _timeline.Window = ShowcaseTimelineOptions.GetWindow(instance);
            _timeline.Granularity = ShowcaseTimelineOptions.GetGranularity(instance);
            // The chart itself shows the empty caption when the window holds no unlocks.
            _timeline.SetCounts(counts.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }
}
