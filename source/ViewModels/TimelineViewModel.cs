using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using LiveCharts;
using LiveCharts.Wpf;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.Overview;
using Playnite.SDK;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The unlocks-over-time column chart shared by the overview, the single-game window, the
    /// Showcase Timeline widget, and the Modern theme bar chart. Counts arrive keyed by local day;
    /// the window, the bar unit, the axis ticks, and the Y scale come from the pure timeline engine.
    /// </summary>
    public class TimelineViewModel : ObservableObject
    {
        private readonly ILogger _logger = PluginLogger.GetLogger(nameof(TimelineViewModel));

        private readonly object _sync = new object();
        private Dictionary<DateTime, int> _countsByDate = new Dictionary<DateTime, int>();

        // Null draws one accent column per bar; otherwise one stacked segment per series.
        private IReadOnlyList<TimelineSeriesCounts> _series;
        private List<string> _appliedSeriesKeys;
        private bool _hasCounts;
        private int _updateVersion;

        // UI-thread state of the last applied pass, kept so a tick-count change re-plans labels
        // without another background pass.
        private TimelineBucketPlan _plan;

        private TimeWindow _window = TimeWindow.FromPreset(TimelineRange.OneYear);
        private TimelineGranularity _granularity = TimelineGranularity.Auto;
        private TimelineBucketUnit _effectiveUnit = TimelineBucketUnit.Day;
        private IReadOnlyList<TimelineGranularity> _unavailableGranularities = new TimelineGranularity[0];
        private DateTime? _earliestDate;
        private double _xAxisMax = 1;
        private double _yAxisMax = 1;
        private double _yAxisStep = 1;
        private bool _isEmpty = true;
        private int _maxTickCount = TimelineAxisTicks.DefaultMaxTicks;
        private int _maxBarCount = TimelineBucketing.MaxOverrideBarCount;
        private int _renderRevision;

        public TimelineViewModel()
        {
            SetTimeRangeCommand = new RelayCommand(param =>
            {
                if (TimeWindow.TryParse(param?.ToString(), out var window))
                {
                    Window = window;
                }
            });
        }

        /// <summary>Replaces the per-day counts (keys are local calendar days) and recomputes.</summary>
        /// <remarks>
        /// Counts equal to the ones already shown are dropped. Hosts re-feed the counts on every
        /// custom-data change, most of which move no unlock, and each pass ends in a forced
        /// synchronous chart redraw on the UI thread.
        /// </remarks>
        public void SetCounts(IDictionary<DateTime, int> countsByDate)
        {
            var next = countsByDate != null
                ? new Dictionary<DateTime, int>(countsByDate)
                : new Dictionary<DateTime, int>();
            if (TryStore(next, null))
            {
                ScheduleOrDefer();
            }
        }

        /// <summary>
        /// Replaces the counts with one stacked series per platform. Each bar splits into colored
        /// segments whose heights sum to the bar <see cref="SetCounts"/> would draw.
        /// </summary>
        public void SetSeriesCounts(IReadOnlyList<TimelineSeriesCounts> series)
        {
            var nextSeries = (series ?? Array.Empty<TimelineSeriesCounts>())
                .Where(item => item != null)
                .Select(item => new TimelineSeriesCounts(
                    item.Key,
                    item.Title,
                    item.ColorHex,
                    item.CountsByDate.ToDictionary(pair => pair.Key, pair => pair.Value)))
                .ToList();
            var total = new Dictionary<DateTime, int>();
            foreach (var item in nextSeries)
            {
                foreach (var pair in item.CountsByDate)
                {
                    total[pair.Key] = total.TryGetValue(pair.Key, out var existing) ? existing + pair.Value : pair.Value;
                }
            }

            if (TryStore(total, nextSeries))
            {
                ScheduleOrDefer();
            }
        }

        private bool TryStore(Dictionary<DateTime, int> total, IReadOnlyList<TimelineSeriesCounts> series)
        {
            lock (_sync)
            {
                if (_hasCounts && HasSameCounts(_countsByDate, total) && HasSameSeries(_series, series))
                {
                    return false;
                }

                _countsByDate = total;
                _series = series;
                _hasCounts = true;
                return true;
            }
        }

        private void ScheduleOrDefer()
        {
            // A chart that has not drawn yet (one opened while the editor is up) draws now rather
            // than sitting blank until the editor closes.
            if (_hasScheduled && OpenEditorRegistry.IsAnyOpen)
            {
                DeferUntilEditorsClose();
                return;
            }

            ScheduleUpdate();
        }

        // Charts whose counts changed while a Manage editor was open. Every editor save re-feeds
        // the counts of each chart on screen, and each pass ends in a forced synchronous redraw on
        // the UI thread the editor is typing on, so the counts are kept and drawn once when the
        // last editor closes. Weak, so a chart closed meanwhile is not kept alive.
        private static readonly object DeferredSync = new object();
        private static readonly List<WeakReference<TimelineViewModel>> Deferred =
            new List<WeakReference<TimelineViewModel>>();
        private bool _updateDeferred;
        private volatile bool _hasScheduled;

        static TimelineViewModel()
        {
            OpenEditorRegistry.AllClosed += FlushDeferred;
        }

        private void DeferUntilEditorsClose()
        {
            lock (DeferredSync)
            {
                if (!_updateDeferred)
                {
                    _updateDeferred = true;
                    Deferred.Add(new WeakReference<TimelineViewModel>(this));
                }
            }

            // The last editor may have closed between the check and the enqueue.
            if (!OpenEditorRegistry.IsAnyOpen)
            {
                FlushDeferred();
            }
        }

        private static void FlushDeferred()
        {
            List<WeakReference<TimelineViewModel>> pending;
            lock (DeferredSync)
            {
                pending = new List<WeakReference<TimelineViewModel>>(Deferred);
                Deferred.Clear();
            }

            foreach (var reference in pending)
            {
                if (!reference.TryGetTarget(out var timeline))
                {
                    continue;
                }

                bool stillDeferred;
                lock (DeferredSync)
                {
                    stillDeferred = timeline._updateDeferred;
                    timeline._updateDeferred = false;
                }

                // A window or granularity change made while the editor was open already drew
                // the current counts.
                if (stillDeferred)
                {
                    timeline.ScheduleUpdate();
                }
            }
        }

        private static bool HasSameSeries(IReadOnlyList<TimelineSeriesCounts> current, IReadOnlyList<TimelineSeriesCounts> next)
        {
            if (current == null || next == null)
            {
                return current == null && next == null;
            }

            if (current.Count != next.Count)
            {
                return false;
            }

            for (var i = 0; i < current.Count; i++)
            {
                var a = current[i];
                var b = next[i];
                if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal) ||
                    !string.Equals(a.Title, b.Title, StringComparison.Ordinal) ||
                    !string.Equals(a.ColorHex, b.ColorHex, StringComparison.OrdinalIgnoreCase) ||
                    !HasSameCounts(a.CountsByDate, b.CountsByDate))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasSameCounts(IReadOnlyDictionary<DateTime, int> current, IReadOnlyDictionary<DateTime, int> next)
        {
            if (current.Count != next.Count)
            {
                return false;
            }

            foreach (var pair in next)
            {
                if (!current.TryGetValue(pair.Key, out var count) || count != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The window shown: a rolling preset or a custom range.</summary>
        public TimeWindow Window
        {
            get => _window;
            set
            {
                var next = value ?? TimeWindow.FromPreset(TimelineRange.OneYear);
                if (SetValueAndReturn(ref _window, next))
                {
                    OnPropertyChanged(nameof(TimelineRange));
                    ScheduleUpdate();
                }
            }
        }

        /// <summary>
        /// Preset view of <see cref="Window"/> for hosts that bind range buttons to the enum
        /// (the Modern theme control). A custom window reports the default preset.
        /// </summary>
        public TimelineRange TimelineRange
        {
            get => _window.Preset ?? TimelineRange.ThreeMonths;
            set => Window = TimeWindow.FromPreset(value);
        }

        /// <summary>Bar-width override; Auto picks the unit from the window span.</summary>
        public TimelineGranularity Granularity
        {
            get => _granularity;
            set
            {
                if (SetValueAndReturn(ref _granularity, value))
                {
                    ScheduleUpdate();
                }
            }
        }

        /// <summary>The unit the bars were built with after automatic selection or escalation.</summary>
        public TimelineBucketUnit EffectiveUnit
        {
            get => _effectiveUnit;
            private set => SetValue(ref _effectiveUnit, value);
        }

        /// <summary>
        /// Granularity overrides the current window cannot honor within the host's bar cap (Day on
        /// a multi-year window, say). The picker disables them; a selected one falls back to Auto.
        /// </summary>
        public IReadOnlyList<TimelineGranularity> UnavailableGranularities
        {
            get => _unavailableGranularities;
            private set => SetValue(ref _unavailableGranularities, value);
        }

        /// <summary>Earliest local day with an unlock, or null; the lower bound an open custom range uses.</summary>
        public DateTime? EarliestDate
        {
            get => _earliestDate;
            private set => SetValue(ref _earliestDate, value);
        }

        public SeriesCollection TimelineSeries { get; } = new SeriesCollection();

        /// <summary>Axis labels, one per bar; blank where no tick is drawn.</summary>
        public ObservableCollection<string> TimelineLabels { get; } = new ObservableCollection<string>();

        /// <summary>Tooltip headers, one per bar; always the bar's full date or range.</summary>
        public ObservableCollection<string> TooltipLabels { get; } = new ObservableCollection<string>();

        /// <summary>Bar count; bars occupy [i, i + 1) so this closes the X axis without a trailing gap.</summary>
        public double XAxisMax
        {
            get => _xAxisMax;
            private set => SetValue(ref _xAxisMax, value);
        }

        /// <summary>Nice integer ceiling of the Y axis, at least 1.</summary>
        public double YAxisMax
        {
            get => _yAxisMax;
            private set => SetValue(ref _yAxisMax, value);
        }

        /// <summary>Y gridline step matching <see cref="YAxisMax"/>.</summary>
        public double YAxisStep
        {
            get => _yAxisStep;
            private set => SetValue(ref _yAxisStep, value);
        }

        /// <summary>True when no unlock falls inside the window.</summary>
        public bool IsEmpty
        {
            get => _isEmpty;
            private set => SetValue(ref _isEmpty, value);
        }

        /// <summary>Most axis labels the host has room for; the chart control sets it from its width.</summary>
        public int MaxTickCount
        {
            get => _maxTickCount;
            set
            {
                var clamped = Math.Max(1, value);
                if (SetValueAndReturn(ref _maxTickCount, clamped))
                {
                    ReplanTicks();
                }
            }
        }

        /// <summary>
        /// Increments once every property of a pass has been applied. The chart control forces one
        /// synchronous LiveCharts redraw on it instead of waiting for the library's coalescing timer.
        /// </summary>
        public int RenderRevision
        {
            get => _renderRevision;
            private set => SetValue(ref _renderRevision, value);
        }

        /// <summary>
        /// Most bars the host can draw; the chart control sets it from its width. A finer unit
        /// than fits (Day on a year, say) escalates to the next one rather than rendering nothing.
        /// </summary>
        public int MaxBarCount
        {
            get => _maxBarCount;
            set
            {
                var clamped = Math.Max(1, value);
                if (SetValueAndReturn(ref _maxBarCount, clamped))
                {
                    ScheduleUpdate();
                }
            }
        }

        public Func<double, string> YAxisFormatter { get; } = value => value.ToString("N0", FormattingCulture.Current);

        public ICommand SetTimeRangeCommand { get; }

        /// <summary>Recomputes against the current local day; used at day rollover.</summary>
        public void UpdateTimelineData() => ScheduleUpdate();

        private void ScheduleUpdate()
        {
            lock (DeferredSync)
            {
                _updateDeferred = false;
            }

            _hasScheduled = true;
            var version = Interlocked.Increment(ref _updateVersion);

            Dictionary<DateTime, int> counts;
            IReadOnlyList<TimelineSeriesCounts> series;
            lock (_sync)
            {
                counts = _countsByDate;
                series = _series;
            }

            // Snapshot every input on the calling thread; the pass below never reads the properties.
            var window = _window;
            var granularity = _granularity;
            var maxTicks = _maxTickCount;
            var maxBars = _maxBarCount;
            var culture = FormattingCulture.Current;

            _ = Task.Run(() =>
            {
                try
                {
                    var today = DateTime.Now.Date;
                    var earliest = UnlockDayCounts.Earliest(counts);
                    var range = window.Resolve(today, earliest);
                    var plan = TimelineBucketing.Build(range.Start, range.End, counts, granularity, maxBars);
                    var labels = TimelineAxisTicks.Plan(plan.Buckets, plan.Unit, maxTicks, culture);
                    var scale = NiceScale.ForMax(plan.Max);
                    var segments = BuildSegments(plan, series);
                    var unavailable = new[] { TimelineGranularity.Day, TimelineGranularity.Week, TimelineGranularity.Month }
                        .Where(candidate => !TimelineBucketing.Fits(candidate, range.Start, range.End, maxBars))
                        .ToList();

                    System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
                    {
                        try
                        {
                            if (version != _updateVersion)
                            {
                                return;
                            }

                            using (PerfScope.Start(_logger, "Timeline.Apply", thresholdMs: 16))
                            {
                                Apply(plan, segments, labels, scale, earliest);
                            }

                            UnavailableGranularities = unavailable;
                            if (unavailable.Contains(Granularity))
                            {
                                // The chart already escalated; make the stored choice say so.
                                Granularity = TimelineGranularity.Auto;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn(ex, "Timeline UI update failed.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Timeline update failed.");
                }
            });
        }

        /// <summary>One stacked segment, bucketed against the total plan.</summary>
        private sealed class TimelineSegment
        {
            public TimelineSegment(TimelineSeriesCounts source, int[] values)
            {
                Source = source;
                Values = values;
            }

            public TimelineSeriesCounts Source { get; }

            public int[] Values { get; }
        }

        // Largest window total first, so it stacks at the bottom of every bar. Platforms with
        // nothing in the window are left out of the stack and the tooltip.
        private static List<TimelineSegment> BuildSegments(TimelineBucketPlan plan, IReadOnlyList<TimelineSeriesCounts> series)
        {
            if (series == null)
            {
                return null;
            }

            return series
                .Select(item => new TimelineSegment(item, TimelineBucketing.SumIntoBuckets(plan, item.CountsByDate)))
                .Select(segment => new { Segment = segment, Total = segment.Values.Sum() })
                .Where(entry => entry.Total > 0)
                .OrderBy(entry => entry.Segment.Source.Order ?? int.MaxValue)
                .ThenByDescending(entry => entry.Total)
                .ThenBy(entry => entry.Segment.Source.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => entry.Segment)
                .ToList();
        }

        private void Apply(
            TimelineBucketPlan plan,
            List<TimelineSegment> segments,
            TimelineAxisLabels labels,
            NiceScaleResult scale,
            DateTime? earliest)
        {
            if (segments == null)
            {
                ApplySingle(plan);
            }
            else
            {
                ApplyStacked(segments);
            }

            _plan = plan;
            UpdateHighlightedIndex();
            CollectionHelper.SynchronizeValueCollection(TimelineLabels, labels.AxisLabels.ToList());
            CollectionHelper.SynchronizeValueCollection(TooltipLabels, labels.TooltipLabels.ToList());
            XAxisMax = Math.Max(1, plan.Buckets.Count);
            YAxisMax = scale.Max;
            YAxisStep = scale.Step;
            IsEmpty = plan.Total == 0;
            EffectiveUnit = plan.Unit;
            EarliestDate = earliest;
            RenderRevision = unchecked(_renderRevision + 1);
        }

        private void ApplySingle(TimelineBucketPlan plan)
        {
            if (_appliedSeriesKeys != null)
            {
                TimelineSeries.Clear();
                _appliedSeriesKeys = null;
            }

            if (TimelineSeries.Count == 0)
            {
                var single = new ColumnSeries
                {
                    Title = ResourceProvider.GetString("LOCPlayAch_Achievements"),
                    Values = new ChartValues<int>()
                };
                // The accent with the chart gloss; without the resource the chart's style fill stays.
                if (System.Windows.Application.Current?.TryFindResource("PlayAch.Brush.Accent") is SolidColorBrush accent)
                {
                    single.Fill = ChartGloss.Create(accent.Color);
                    single.Stroke = accent;
                }

                TimelineSeries.Add(single);
            }

            var values = plan.Buckets.Select(bucket => bucket.Count).ToList();
            if (TimelineSeries[0].Values is ChartValues<int> chartValues)
            {
                CollectionHelper.SynchronizeValueCollection(chartValues, values);
            }
            else
            {
                TimelineSeries[0].Values = new ChartValues<int>(values);
            }
        }

        // Updates the stack in place while the platforms and their order are unchanged; any other
        // change rebuilds the series once, because stack order is collection order.
        private void ApplyStacked(List<TimelineSegment> segments)
        {
            var keys = segments.Select(segment => segment.Source.Key).ToList();
            if (_appliedSeriesKeys == null || !_appliedSeriesKeys.SequenceEqual(keys, StringComparer.Ordinal))
            {
                TimelineSeries.Clear();
                foreach (var segment in segments)
                {
                    TimelineSeries.Add(new StackedColumnSeries
                    {
                        Values = new ChartValues<int>(segment.Values)
                    });
                }

                _appliedSeriesKeys = keys;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var chartSeries = (StackedColumnSeries)TimelineSeries[i];
                chartSeries.Title = segment.Source.Title;
                var brush = SegmentBrush(segment.Source.ColorHex);
                if (!ChartGloss.IsGlossOf(chartSeries.Fill, brush.Color))
                {
                    // Stroke too, flat: LiveCharts assigns a palette stroke to a series without
                    // one, and the tooltip swatch reads the stroke first.
                    chartSeries.Fill = ChartGloss.Create(brush.Color);
                    chartSeries.Stroke = brush;
                }

                if (chartSeries.Values is ChartValues<int> chartValues)
                {
                    CollectionHelper.SynchronizeValueCollection(chartValues, segment.Values.ToList());
                }
            }
        }

        private static SolidColorBrush SegmentBrush(string colorHex)
        {
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(
                    string.IsNullOrWhiteSpace(colorHex) ? ProviderRegistry.FallbackProviderColorHex : colorHex);
            }
            catch (FormatException)
            {
                color = (Color)ColorConverter.ConvertFromString(ProviderRegistry.FallbackProviderColorHex);
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private UnlockDaySpan? _highlightedSpan;
        private int _highlightedIndex = -1;

        /// <summary>
        /// Days to show as selected. The column covering exactly these days is shaded; a span
        /// no column covers (the bar unit changed since it was picked) shades nothing.
        /// </summary>
        public UnlockDaySpan? HighlightedSpan
        {
            get => _highlightedSpan;
            set
            {
                if (Nullable.Equals(_highlightedSpan, value))
                {
                    return;
                }

                _highlightedSpan = value;
                UpdateHighlightedIndex();
            }
        }

        /// <summary>The shaded column's index, or -1.</summary>
        public int HighlightedIndex
        {
            get => _highlightedIndex;
            private set => SetValue(ref _highlightedIndex, value);
        }

        /// <summary>The days the column at <paramref name="index"/> covers, or null off the axis.</summary>
        public UnlockDaySpan? SpanAt(int index)
        {
            var buckets = _plan?.Buckets;
            if (buckets == null || index < 0 || index >= buckets.Count)
            {
                return null;
            }

            return new UnlockDaySpan(buckets[index].Start, buckets[index].End);
        }

        private void UpdateHighlightedIndex()
        {
            var span = _highlightedSpan;
            var buckets = _plan?.Buckets;
            var index = -1;
            if (span.HasValue && buckets != null)
            {
                for (var i = 0; i < buckets.Count; i++)
                {
                    if (buckets[i].Start == span.Value.Start && buckets[i].End == span.Value.End)
                    {
                        index = i;
                        break;
                    }
                }
            }

            HighlightedIndex = index;
        }

        private void ReplanTicks()
        {
            var plan = _plan;
            if (plan == null)
            {
                return;
            }

            try
            {
                var labels = TimelineAxisTicks.Plan(plan.Buckets, plan.Unit, _maxTickCount, FormattingCulture.Current);
                CollectionHelper.SynchronizeValueCollection(TimelineLabels, labels.AxisLabels.ToList());
                CollectionHelper.SynchronizeValueCollection(TooltipLabels, labels.TooltipLabels.ToList());
                RenderRevision = unchecked(_renderRevision + 1);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Timeline tick replan failed.");
            }
        }
    }
}
