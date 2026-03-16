using System;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using LiveCharts;
using LiveCharts.Wpf;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Preview control for bar chart timeline display.
    /// Uses the actual LiveCharts CartesianChart with mock timeline data.
    /// </summary>
    public partial class PreviewBarChartControl : UserControl
    {
        public SeriesCollection TimelineSeries { get; } = new SeriesCollection();
        public ObservableCollection<string> TimelineLabels { get; } = new ObservableCollection<string>();
        public Func<double, string> YAxisFormatter { get; } = value => value.ToString("N0");

        public PreviewBarChartControl()
        {
            InitializeComponent();
            SetMockData();
        }

        private void SetMockData()
        {
            // Mock data for a 7-day timeline
            var values = new ChartValues<int> { 3, 5, 2, 8, 4, 6, 3 };
            var labels = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

            TimelineSeries.Add(new ColumnSeries
            {
                Title = "Achievements",
                Values = values
            });

            foreach (var label in labels)
            {
                TimelineLabels.Add(label);
            }
        }
    }
}
