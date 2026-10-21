using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Timeline widget: the shared unlocks-over-time chart via TimelineViewModel. The
    /// window, granularity and platform split are widget settings; with Show Control Bar on, the
    /// widget also sets them itself from a picker and a split toggle above the chart, and saves
    /// what is picked back into its settings.
    /// </summary>
    public sealed class TimelineWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly TimelineViewModel _timeline = new TimelineViewModel();
        private bool _showControls;
        private bool _showTimeWindowControl;
        private bool _showSplitControl;
        private bool _splitByPlatform;

        // Set while Refresh pushes the settings into the chart, so those writes are not saved
        // back as if the user had picked them.
        private bool _applying;

        public TimelineWidgetViewModel()
        {
            _timeline.PropertyChanged += Timeline_PropertyChanged;
        }

        public TimelineViewModel Timeline => _timeline;

        /// <summary>Whether the widget's settings show either control above the chart.</summary>
        public bool ShowControls
        {
            get => _showControls;
            private set => SetValue(ref _showControls, value);
        }

        public bool ShowTimeWindowControl
        {
            get => _showTimeWindowControl;
            private set => SetValue(ref _showTimeWindowControl, value);
        }

        public bool ShowSplitControl
        {
            get => _showSplitControl;
            private set => SetValue(ref _showSplitControl, value);
        }

        /// <summary>The split toggle in the controls: one stacked segment per platform.</summary>
        public bool SplitByPlatform
        {
            get => _splitByPlatform;
            set
            {
                if (_splitByPlatform == value)
                {
                    return;
                }

                SetValue(ref _splitByPlatform, value);
                if (!_applying)
                {
                    Save(ShowcaseTimelineOptions.SplitByPlatformOption, Convert.ToString(value, CultureInfo.InvariantCulture));
                    FeedCounts();
                }
            }
        }

        protected override void Refresh()
        {
            var instance = Projection?.Instance;
            _applying = true;
            try
            {
                ShowTimeWindowControl = ShowcaseWidgetOptions.GetControlBarItemVisible(
                    instance,
                    ShowcaseControlBarItems.TimeWindow);
                ShowSplitControl = ShowcaseWidgetOptions.GetControlBarItemVisible(
                    instance,
                    ShowcaseControlBarItems.SplitByPlatform);
                ShowControls = ShowTimeWindowControl || ShowSplitControl;
                SplitByPlatform = ShowcaseTimelineOptions.GetSplitByPlatform(instance);
                _timeline.Window = ShowcaseTimelineOptions.GetWindow(instance);
                _timeline.Granularity = ShowcaseTimelineOptions.GetGranularity(instance);
                _timeline.HighlightedSpan = Projection?.HighlightedSpan;
            }
            finally
            {
                _applying = false;
            }

            FeedCounts();
        }

        // The projection carries the per-platform series only when the saved option asked for
        // them; the toggle can turn the split on before a reprojection, so the series are built
        // here from the same snapshot when they are missing.
        private void FeedCounts()
        {
            if (_splitByPlatform)
            {
                _timeline.SetSeriesCounts(Projection?.TimelineByPlatform ??
                    TimelinePlatformSeries.FromSnapshot(Projection?.Snapshot));
                return;
            }

            // The chart itself shows the empty caption when the window holds no unlocks.
            var counts = Projection?.Timeline ?? new Dictionary<DateTime, int>();
            _timeline.SetCounts(counts.ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        private void Timeline_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_applying)
            {
                return;
            }

            if (e.PropertyName == nameof(TimelineViewModel.Window))
            {
                Save(ShowcaseTimelineOptions.RangeOption, _timeline.Window?.ToKey());
            }
            else if (e.PropertyName == nameof(TimelineViewModel.Granularity))
            {
                Save(ShowcaseTimelineOptions.GranularityOption, _timeline.Granularity.ToString());
            }
        }

        private void Save(string key, string value)
        {
            var instanceId = Projection?.Instance?.InstanceId;
            if (!string.IsNullOrWhiteSpace(instanceId) && value != null)
            {
                ShowcaseControlBarStates.Store?.Save(instanceId, key, value);
            }
        }
    }
}
