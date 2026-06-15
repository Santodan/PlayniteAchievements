using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LiveCharts;
using LiveCharts.Wpf;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public class TimelineViewModel : ObservableObject
    {
        private readonly ILogger _logger = PluginLogger.GetLogger(nameof(TimelineViewModel));

        private readonly object _sync = new object();
        private Dictionary<DateTime, int> _countsByDate = new Dictionary<DateTime, int>();
        private int _updateVersion;

        public TimelineViewModel()
        {
            SetTimeRangeCommand = new RelayCommand(param =>
            {
                if (Enum.TryParse<TimelineRange>(param?.ToString(), out var range))
                    TimelineRange = range;
            });
        }

        public void SetCounts(IDictionary<DateTime, int> countsByDate)
        {
            lock (_sync)
            {
                _countsByDate = countsByDate != null
                    ? new Dictionary<DateTime, int>(countsByDate)
                    : new Dictionary<DateTime, int>();
            }

            UpdateTimelineData();
        }

        private TimelineRange _timelineRange = TimelineRange.OneYear;
        public TimelineRange TimelineRange
        {
            get => _timelineRange;
            set
            {
                if (SetValueAndReturn(ref _timelineRange, value))
                {
                    UpdateTimelineData();
                }
            }
        }

        public SeriesCollection TimelineSeries { get; } = new SeriesCollection();
        public ObservableCollection<string> TimelineLabels { get; } = new ObservableCollection<string>();
        public Func<double, string> YAxisFormatter { get; } = value => value.ToString("N0");
        public ICommand SetTimeRangeCommand { get; }

        public void UpdateTimelineData()
        {
            var version = Interlocked.Increment(ref _updateVersion);

            _ = Task.Run(() =>
            {
                try
                {
                    Dictionary<DateTime, int> localCounts;
                    lock (_sync)
                    {
                        localCounts = _countsByDate;
                    }

                    var endDate = DateTime.UtcNow.Date;
                    localCounts = NormalizeCounts(localCounts, endDate);
                    var startDate = GetStartDateForRange(TimelineRange, localCounts);

                    var values = new List<int>();
                    var labels = new List<string>();

                    if (TimelineRange == TimelineRange.OneYear)
                    {
                        AddMonthlyBuckets(localCounts, startDate, endDate, values, labels);
                    }
                    else if (TimelineRange == TimelineRange.All)
                    {
                        AddAllTimeBuckets(localCounts, endDate, values, labels);
                    }
                    else if (TimelineRange == TimelineRange.ThreeMonths)
                    {
                        // Daily bars, axis ticks every 2 weeks
                        var currentDate = startDate.Date;
                        var nextLabelDate = currentDate;
                        while (currentDate <= endDate.Date)
                        {
                            values.Add(localCounts.TryGetValue(currentDate, out var count) ? count : 0);
                            if (currentDate >= nextLabelDate)
                            {
                                labels.Add(currentDate.ToString("M/d"));
                                nextLabelDate = currentDate.AddDays(14);
                            }
                            else
                            {
                                labels.Add(string.Empty);
                            }
                            currentDate = currentDate.AddDays(1);
                        }
                    }
                    else
                    {
                        // Daily bars and labels for other short ranges
                        var currentDate = startDate.Date;
                        while (currentDate <= endDate.Date)
                        {
                            values.Add(localCounts.TryGetValue(currentDate, out var count) ? count : 0);
                            labels.Add(currentDate.ToString("M/d"));
                            currentDate = currentDate.AddDays(1);
                        }
                    }

                    if (values.Count == 0)
                    {
                        values.Add(0);
                        labels.Add(string.Empty);
                    }

                    System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
                    {
                        try
                        {
                            if (version != _updateVersion)
                            {
                                return;
                            }

                            if (TimelineSeries.Count == 0)
                            {
                                TimelineSeries.Add(new ColumnSeries
                                {
                                    Title = "Achievements",
                                    Values = new ChartValues<int>()
                                });
                            }

                            if (TimelineSeries[0].Values is ChartValues<int> chartValues)
                            {
                                CollectionHelper.SynchronizeValueCollection(chartValues, values);
                            }
                            else
                            {
                                TimelineSeries[0].Values = new ChartValues<int>(values);
                            }

                            CollectionHelper.SynchronizeValueCollection(TimelineLabels, labels);
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

        private static DateTime GetStartDateForRange(TimelineRange range, IDictionary<DateTime, int> counts)
        {
            var now = DateTime.UtcNow.Date;
            return range switch
            {
                TimelineRange.SevenDays => now.AddDays(-7),
                TimelineRange.FourteenDays => now.AddDays(-14),
                TimelineRange.OneMonth => now.AddMonths(-1),
                TimelineRange.ThreeMonths => now.AddMonths(-3),
                TimelineRange.OneYear => now.AddYears(-1),
                TimelineRange.All =>
                    counts != null && counts.Count > 0
                        ? counts.Keys.Min().Date
                        : now.Date,
                _ => now.AddDays(-14)
            };
        }

        private static void AddMonthlyBuckets(
            IDictionary<DateTime, int> counts,
            DateTime startDate,
            DateTime endDate,
            IList<int> values,
            IList<string> labels)
        {
            var monthlyData = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kvp in counts)
            {
                var monthKey = kvp.Key.ToString("yyyy-MM");
                monthlyData[monthKey] = monthlyData.TryGetValue(monthKey, out var existing)
                    ? existing + kvp.Value
                    : kvp.Value;
            }

            var currentMonth = new DateTime(startDate.Year, startDate.Month, 1);
            while (currentMonth <= endDate)
            {
                var monthKey = currentMonth.ToString("yyyy-MM");
                values.Add(monthlyData.TryGetValue(monthKey, out var count) ? count : 0);
                labels.Add(currentMonth.ToString("MMM yy"));
                currentMonth = currentMonth.AddMonths(1);
            }
        }

        private static void AddAllTimeBuckets(
            IDictionary<DateTime, int> counts,
            DateTime endDate,
            IList<int> values,
            IList<string> labels)
        {
            if (counts == null || counts.Count == 0)
            {
                return;
            }

            var firstDate = counts.Keys.Min().Date;
            var totalMonths = GetInclusiveMonthCount(firstDate, endDate);
            if (totalMonths <= 36)
            {
                AddMonthlyBuckets(counts, firstDate, endDate, values, labels);
                return;
            }

            if (totalMonths <= 96)
            {
                AddQuarterlyBuckets(counts, firstDate, endDate, values, labels);
                return;
            }

            AddYearlyBuckets(counts, firstDate, endDate, values, labels);
        }

        private static void AddQuarterlyBuckets(
            IDictionary<DateTime, int> counts,
            DateTime startDate,
            DateTime endDate,
            IList<int> values,
            IList<string> labels)
        {
            var quarterlyData = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kvp in counts)
            {
                var quarterKey = GetQuarterKey(kvp.Key);
                quarterlyData[quarterKey] = quarterlyData.TryGetValue(quarterKey, out var existing)
                    ? existing + kvp.Value
                    : kvp.Value;
            }

            var currentQuarter = GetQuarterStart(startDate);
            var endQuarter = GetQuarterStart(endDate);
            while (currentQuarter <= endQuarter)
            {
                var quarterKey = GetQuarterKey(currentQuarter);
                values.Add(quarterlyData.TryGetValue(quarterKey, out var count) ? count : 0);
                labels.Add($"Q{GetQuarter(currentQuarter)} {currentQuarter:yy}");
                currentQuarter = currentQuarter.AddMonths(3);
            }
        }

        private static void AddYearlyBuckets(
            IDictionary<DateTime, int> counts,
            DateTime startDate,
            DateTime endDate,
            IList<int> values,
            IList<string> labels)
        {
            var yearlyData = new Dictionary<int, int>();
            foreach (var kvp in counts)
            {
                var year = kvp.Key.Year;
                yearlyData[year] = yearlyData.TryGetValue(year, out var existing)
                    ? existing + kvp.Value
                    : kvp.Value;
            }

            for (var year = startDate.Year; year <= endDate.Year; year++)
            {
                values.Add(yearlyData.TryGetValue(year, out var count) ? count : 0);
                labels.Add(year.ToString());
            }
        }

        private static int GetInclusiveMonthCount(DateTime startDate, DateTime endDate)
        {
            return ((endDate.Year - startDate.Year) * 12) + endDate.Month - startDate.Month + 1;
        }

        private static DateTime GetQuarterStart(DateTime date)
        {
            var firstMonth = ((date.Month - 1) / 3 * 3) + 1;
            return new DateTime(date.Year, firstMonth, 1);
        }

        private static int GetQuarter(DateTime date)
        {
            return ((date.Month - 1) / 3) + 1;
        }

        private static string GetQuarterKey(DateTime date)
        {
            return $"{date.Year}-Q{GetQuarter(date)}";
        }

        private static Dictionary<DateTime, int> NormalizeCounts(
            IDictionary<DateTime, int> counts,
            DateTime endDate)
        {
            var normalized = new Dictionary<DateTime, int>();
            if (counts == null)
            {
                return normalized;
            }

            var earliestSupportedDate = new DateTime(1970, 1, 1);
            foreach (var kvp in counts)
            {
                var date = kvp.Key.Date;
                if (date < earliestSupportedDate || date > endDate || kvp.Value <= 0)
                {
                    continue;
                }

                normalized[date] = normalized.TryGetValue(date, out var existing)
                    ? existing + kvp.Value
                    : kvp.Value;
            }

            return normalized;
        }
    }
}
