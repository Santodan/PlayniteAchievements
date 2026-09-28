using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Timeline widget: the shared unlocks-over-time chart via TimelineViewModel plus
    /// the time window picker. The picker's chips and custom range write the instance's option
    /// and commit; a compact cell shows chips only and opens the settings dialog for the rest.
    /// </summary>
    public sealed class TimelineWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly TimelineViewModel _timeline = new TimelineViewModel();
        private TimeWindow _window = ShowcaseTimelineOptions.DefaultWindow;
        private bool _isCompactViewport;
        private bool _refreshing;

        public TimelineWidgetViewModel()
        {
            OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        }

        public TimelineViewModel Timeline => _timeline;

        public IReadOnlyList<TimelineRange> Presets => TimeWindow.Presets;

        /// <summary>The instance's window; setting it from the picker persists and re-projects.</summary>
        public TimeWindow Window
        {
            get => _window;
            set
            {
                if (value == null || !SetValueAndReturn(ref _window, value))
                {
                    return;
                }

                // Move the chart at once; the commit below re-projects and lands on the same value.
                _timeline.Window = value;
                if (_refreshing)
                {
                    return;
                }

                ShowcaseTimelineOptions.SetWindow(Projection?.Instance, value);
                ShowcaseConfigurationCommit.Commit();
            }
        }

        public bool IsCompactViewport
        {
            get => _isCompactViewport;
            private set => SetValue(ref _isCompactViewport, value);
        }

        public RelayCommand OpenSettingsCommand { get; }

        protected override void Refresh()
        {
            var instance = Projection?.Instance;
            var counts = Projection?.Timeline ?? new Dictionary<DateTime, int>();
            _refreshing = true;
            try
            {
                Window = ShowcaseTimelineOptions.GetWindow(instance);
                _timeline.Window = Window;
                _timeline.Granularity = ShowcaseTimelineOptions.GetGranularity(instance);
                IsCompactViewport = Density == WidgetViewportDensity.Compact;
            }
            finally
            {
                _refreshing = false;
            }

            // The chart itself shows the empty caption when the window holds no unlocks.
            _timeline.SetCounts(counts.ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        private void OpenSettings()
        {
            var instance = Projection?.Instance;
            if (instance != null && Views.Showcase.ShowcaseWidgetSettingsDialog.Show(instance))
            {
                ShowcaseConfigurationCommit.Commit();
            }
        }
    }
}
