using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

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

        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<ChartControlBarAdapter> _controlBarSlot;
        private GridControlBarViewModel _controlBar;

        public TimelineWidgetViewModel()
        {
            _timeline.PropertyChanged += Timeline_PropertyChanged;
            _controlBarSlot = new ShowcaseControlBarSlot<ChartControlBarAdapter>(FeedCounts);
        }

        public TimelineViewModel Timeline => _timeline;

        /// <summary>The platform, game and progress filters the widget's settings show; null when they show none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            private set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

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

        // The projection carries the unfiltered unlock counts, and the per-platform series only
        // when the saved option asked for them; filtered games, completions and a split the
        // toggle turned on before a reprojection are counted here from the snapshot.
        private void FeedCounts()
        {
            var source = Projection?.Snapshot ?? new OverviewDataSnapshot();
            var snapshot = ApplyControlBarFilter(source);
            var unfiltered = ReferenceEquals(snapshot, source);
            var completions = ShowcaseTimelineOptions.GetCount(Projection?.Instance) == ShowcaseTimelineCount.Completions;
            if (_splitByPlatform)
            {
                _timeline.SetSeriesCounts(completions
                    ? TimelinePlatformSeries.FromCounts(snapshot, TimelinePlatformSeries.CompletionCountsByGame(snapshot))
                    : (unfiltered ? Projection?.TimelineByPlatform : null) ?? TimelinePlatformSeries.FromSnapshot(snapshot));
                return;
            }

            // The chart itself shows the empty caption when the window holds nothing.
            var counts = completions
                ? TimelinePlatformSeries.Sum(TimelinePlatformSeries.CompletionCountsByGame(snapshot))
                : (unfiltered ? Projection?.Timeline : null) ?? snapshot.GlobalUnlockCountsByDate ?? new Dictionary<DateTime, int>();
            _timeline.SetCounts(counts.ToDictionary(pair => pair.Key, pair => Math.Max(0, pair.Value)));
        }

        /// <summary>The snapshot narrowed to the games the control bar keeps; the snapshot itself when it keeps every one.</summary>
        private OverviewDataSnapshot ApplyControlBarFilter(OverviewDataSnapshot snapshot)
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.UpdateShownBar(Projection?.Instance);
            ControlBar = _controlBarSlot.ShownBar;

            var adapter = _controlBarSlot.Adapter;
            var games = (snapshot.GameSummaries ?? new List<GameSummaryItem>())
                .Where(game => game != null)
                .ToList();
            adapter.UpdateOptions(games);
            var kept = adapter.Apply(games);
            return kept.Count == games.Count ? snapshot : OverviewLinkedSnapshots.NarrowToGames(snapshot, kept);
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
