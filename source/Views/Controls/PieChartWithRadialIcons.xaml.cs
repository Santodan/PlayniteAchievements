using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LiveCharts;
using LiveCharts.Wpf;
using LiveCharts.Wpf.Points;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// A pie chart with icons positioned at the midpoint of each slice on a circle around the chart.
    /// </summary>
    public partial class PieChartWithRadialIcons : UserControl
    {
        private static readonly double IconSize = 18.0;
        private const double IconCollisionPadding = 4.0;
        private const double SliceHighlightOffset = 5.0;
        private const double LiveChartsRotationOffset = 45.0;
        private static readonly Duration SliceAnimationDuration = new Duration(TimeSpan.FromMilliseconds(150));
        private static readonly PropertyInfo PiePointViewSliceProperty =
            typeof(PieSlice).Assembly
                .GetType("LiveCharts.Wpf.Points.PiePointView")?
                .GetProperty("Slice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly IEasingFunction SliceAnimationEasing = new QuadraticEase
        {
            EasingMode = EasingMode.EaseOut
        };
        private readonly List<PieSeries> subscribedSeries = new List<PieSeries>();
        private readonly List<INotifyPropertyChanged> subscribedLegendItems = new List<INotifyPropertyChanged>();
        private readonly List<INotifyPropertyChanged> subscribedSliceItems = new List<INotifyPropertyChanged>();
        private bool calculationScheduled;
        private string hoveredSliceLabel;

        private sealed class IconCandidate
        {
            public int Sequence { get; set; }
            public PieIconPosition Position { get; set; }
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public int Count { get; set; }
            public bool IsHighlighted { get; set; }
            public bool SuppressOnCollision { get; set; }
        }

        /// <summary>
        /// Event raised when a pie slice is clicked.
        /// Provides the label of the clicked slice.
        /// </summary>
        public event EventHandler<string> SliceClick;

        public static readonly DependencyProperty PieSeriesProperty =
            DependencyProperty.Register(nameof(PieSeries), typeof(SeriesCollection), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(null, OnPieSeriesChanged));

        public static readonly DependencyProperty LegendItemsProperty =
            DependencyProperty.Register(nameof(LegendItems), typeof(ObservableCollection<LegendItem>), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(null, OnLegendItemsChanged));

        public static readonly DependencyProperty IconOffsetProperty =
            DependencyProperty.Register(nameof(IconOffset), typeof(double), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(12.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty HighlightedLabelsProperty =
            DependencyProperty.Register(nameof(HighlightedLabels), typeof(ObservableCollection<string>), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(null, OnHighlightedLabelsChanged));

        public static readonly DependencyProperty ExactUnlockedCountProperty =
            DependencyProperty.Register(nameof(ExactUnlockedCount), typeof(int), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(-1, OnCenterPercentageSourceChanged));

        public static readonly DependencyProperty ExactTotalCountProperty =
            DependencyProperty.Register(nameof(ExactTotalCount), typeof(int), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(-1, OnCenterPercentageSourceChanged));

        public static readonly DependencyProperty ShowCenterPercentageProperty =
            DependencyProperty.Register(nameof(ShowCenterPercentage), typeof(bool), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(true, OnShowCenterPercentageChanged));

        private static readonly DependencyPropertyKey CenterPercentageTextPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CenterPercentageText), typeof(string), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty CenterPercentageTextProperty = CenterPercentageTextPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey CenterPercentageFontSizePropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CenterPercentageFontSize), typeof(double), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(11.0));

        public static readonly DependencyProperty CenterPercentageFontSizeProperty = CenterPercentageFontSizePropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey CenterPercentageVisibilityPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CenterPercentageVisibility), typeof(Visibility), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(Visibility.Collapsed));

        public static readonly DependencyProperty CenterPercentageVisibilityProperty = CenterPercentageVisibilityPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey CenterPercentageOffsetXPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CenterPercentageOffsetX), typeof(double), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty CenterPercentageOffsetXProperty = CenterPercentageOffsetXPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey CenterPercentageOffsetYPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CenterPercentageOffsetY), typeof(double), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty CenterPercentageOffsetYProperty = CenterPercentageOffsetYPropertyKey.DependencyProperty;

        public SeriesCollection PieSeries
        {
            get => (SeriesCollection)GetValue(PieSeriesProperty);
            set => SetValue(PieSeriesProperty, value);
        }

        public ObservableCollection<LegendItem> LegendItems
        {
            get => (ObservableCollection<LegendItem>)GetValue(LegendItemsProperty);
            set => SetValue(LegendItemsProperty, value);
        }

        public double IconOffset
        {
            get => (double)GetValue(IconOffsetProperty);
            set => SetValue(IconOffsetProperty, value);
        }

        public ObservableCollection<string> HighlightedLabels
        {
            get => (ObservableCollection<string>)GetValue(HighlightedLabelsProperty);
            set => SetValue(HighlightedLabelsProperty, value);
        }

        public int ExactUnlockedCount
        {
            get => (int)GetValue(ExactUnlockedCountProperty);
            set => SetValue(ExactUnlockedCountProperty, value);
        }

        public int ExactTotalCount
        {
            get => (int)GetValue(ExactTotalCountProperty);
            set => SetValue(ExactTotalCountProperty, value);
        }

        public bool ShowCenterPercentage
        {
            get => (bool)GetValue(ShowCenterPercentageProperty);
            set => SetValue(ShowCenterPercentageProperty, value);
        }

        public string CenterPercentageText
        {
            get => (string)GetValue(CenterPercentageTextProperty);
            private set => SetValue(CenterPercentageTextPropertyKey, value);
        }

        public double CenterPercentageFontSize
        {
            get => (double)GetValue(CenterPercentageFontSizeProperty);
            private set => SetValue(CenterPercentageFontSizePropertyKey, value);
        }

        public Visibility CenterPercentageVisibility
        {
            get => (Visibility)GetValue(CenterPercentageVisibilityProperty);
            private set => SetValue(CenterPercentageVisibilityPropertyKey, value);
        }

        public double CenterPercentageOffsetX
        {
            get => (double)GetValue(CenterPercentageOffsetXProperty);
            private set => SetValue(CenterPercentageOffsetXPropertyKey, value);
        }

        public double CenterPercentageOffsetY
        {
            get => (double)GetValue(CenterPercentageOffsetYProperty);
            private set => SetValue(CenterPercentageOffsetYPropertyKey, value);
        }

        public ObservableCollection<PieIconPosition> IconPositions { get; } = new ObservableCollection<PieIconPosition>();

        public PieChartWithRadialIcons()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        /// <summary>
        /// Schedule position calculation to run after LiveCharts completes rendering.
        /// Multiple calls are deduplicated to a single calculation.
        /// </summary>
        private void ScheduleCalculation()
        {
            if (calculationScheduled)
            {
                return;
            }
            calculationScheduled = true;
            // Use ContextIdle to ensure we run after LiveCharts render pass completes
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
            {
                calculationScheduled = false;
                CalculatePositions();
            }));
        }

        private static void OnPieSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PieChartWithRadialIcons)d;
            control.UnsubscribeFromSeries();
            if (e.OldValue is SeriesCollection oldSeries)
            {
                oldSeries.CollectionChanged -= control.OnSeriesCollectionChanged;
            }
            if (control.IsLoaded && e.NewValue is SeriesCollection newSeries)
            {
                newSeries.CollectionChanged += control.OnSeriesCollectionChanged;
                control.SubscribeToSeries(newSeries);
            }
            control.ScheduleCalculation();
        }

        private void UnsubscribeFromSeries()
        {
            foreach (var series in subscribedSeries)
            {
                if (series is INotifyPropertyChanged notify)
                {
                    notify.PropertyChanged -= OnSeriesPropertyChanged;
                }
                if (series.Values is INotifyCollectionChanged chartValues)
                {
                    chartValues.CollectionChanged -= OnChartValuesChanged;
                }
            }

            UnsubscribeFromSliceDataItems();
            subscribedSeries.Clear();
        }

        private void SubscribeToSeries(SeriesCollection collection)
        {
            foreach (var series in collection.OfType<PieSeries>())
            {
                if (series is INotifyPropertyChanged notify)
                {
                    notify.PropertyChanged += OnSeriesPropertyChanged;
                }
                if (series.Values is INotifyCollectionChanged chartValues)
                {
                    chartValues.CollectionChanged += OnChartValuesChanged;
                }
                subscribedSeries.Add(series);
            }

            RefreshSliceDataSubscriptions();
        }

        private void OnSeriesPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Values")
            {
                RefreshSliceDataSubscriptions();
                ScheduleCalculation();
            }
        }

        private void OnChartValuesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshSliceDataSubscriptions();
            ScheduleCalculation();
        }

        private static void OnLegendItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PieChartWithRadialIcons)d;
            if (e.OldValue is ObservableCollection<LegendItem> oldItems)
            {
                oldItems.CollectionChanged -= control.OnLegendItemsCollectionChanged;
            }
            if (control.IsLoaded && e.NewValue is ObservableCollection<LegendItem> newItems)
            {
                newItems.CollectionChanged += control.OnLegendItemsCollectionChanged;
            }
            if (control.IsLoaded)
            {
                control.RefreshLegendItemSubscriptions();
            }
            else
            {
                control.UnsubscribeFromLegendItems();
            }
            control.ScheduleCalculation();
        }

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PieChartWithRadialIcons)d).ScheduleCalculation();
        }

        private static void OnHighlightedLabelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PieChartWithRadialIcons)d;
            if (e.OldValue is ObservableCollection<string> oldLabels)
            {
                oldLabels.CollectionChanged -= control.OnHighlightedLabelsCollectionChanged;
            }
            if (control.IsLoaded && e.NewValue is ObservableCollection<string> newLabels)
            {
                newLabels.CollectionChanged += control.OnHighlightedLabelsCollectionChanged;
            }
            control.ScheduleCalculation();
        }

        private static void OnCenterPercentageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PieChartWithRadialIcons)d).ScheduleCalculation();
        }

        private static void OnShowCenterPercentageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PieChartWithRadialIcons)d;
            if (e.NewValue is bool showCenterPercentage && !showCenterPercentage)
            {
                control.ClearCenterPercentage();
                return;
            }

            control.ScheduleCalculation();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachCurrentSources();
            ScheduleCalculation();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DetachCurrentSources();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleCalculation();
        }

        private void AttachCurrentSources()
        {
            if (PieSeries != null)
            {
                PieSeries.CollectionChanged -= OnSeriesCollectionChanged;
                PieSeries.CollectionChanged += OnSeriesCollectionChanged;
                UnsubscribeFromSeries();
                SubscribeToSeries(PieSeries);
            }
            else
            {
                UnsubscribeFromSeries();
            }

            if (LegendItems != null)
            {
                LegendItems.CollectionChanged -= OnLegendItemsCollectionChanged;
                LegendItems.CollectionChanged += OnLegendItemsCollectionChanged;
            }

            RefreshLegendItemSubscriptions();

            if (HighlightedLabels != null)
            {
                HighlightedLabels.CollectionChanged -= OnHighlightedLabelsCollectionChanged;
                HighlightedLabels.CollectionChanged += OnHighlightedLabelsCollectionChanged;
            }
        }

        private void DetachCurrentSources()
        {
            if (PieSeries != null)
            {
                PieSeries.CollectionChanged -= OnSeriesCollectionChanged;
            }

            if (LegendItems != null)
            {
                LegendItems.CollectionChanged -= OnLegendItemsCollectionChanged;
            }

            if (HighlightedLabels != null)
            {
                HighlightedLabels.CollectionChanged -= OnHighlightedLabelsCollectionChanged;
            }

            UnsubscribeFromSeries();
            UnsubscribeFromLegendItems();
        }

        private void OnSeriesCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UnsubscribeFromSeries();
            if (PieSeries != null)
            {
                SubscribeToSeries(PieSeries);
            }
            ScheduleCalculation();
        }

        private void OnLegendItemsCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RefreshLegendItemSubscriptions();
            ScheduleCalculation();
        }

        private void OnHighlightedLabelsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            ScheduleCalculation();
        }

        private void RefreshSliceDataSubscriptions()
        {
            UnsubscribeFromSliceDataItems();

            if (PieSeries == null)
            {
                return;
            }

            foreach (var sliceData in PieSeries
                .OfType<PieSeries>()
                .Where(series => series?.Values != null)
                .SelectMany(series => series.Values.OfType<INotifyPropertyChanged>()))
            {
                sliceData.PropertyChanged += OnSliceDataPropertyChanged;
                subscribedSliceItems.Add(sliceData);
            }
        }

        private void UnsubscribeFromSliceDataItems()
        {
            foreach (var sliceData in subscribedSliceItems)
            {
                sliceData.PropertyChanged -= OnSliceDataPropertyChanged;
            }

            subscribedSliceItems.Clear();
        }

        private void OnSliceDataPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ScheduleCalculation();
        }

        private void RefreshLegendItemSubscriptions()
        {
            UnsubscribeFromLegendItems();

            if (LegendItems == null)
            {
                return;
            }

            foreach (var item in LegendItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged += OnLegendItemPropertyChanged;
                subscribedLegendItems.Add(item);
            }
        }

        private void UnsubscribeFromLegendItems()
        {
            foreach (var item in subscribedLegendItems)
            {
                item.PropertyChanged -= OnLegendItemPropertyChanged;
            }

            subscribedLegendItems.Clear();
        }

        private void OnLegendItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ScheduleCalculation();
        }

        private void CalculatePositions()
        {
            var margin = Chart?.Margin ?? new Thickness(0);
            double availableWidth = Math.Max(0, ActualWidth - margin.Left - margin.Right);
            double availableHeight = Math.Max(0, ActualHeight - margin.Top - margin.Bottom);
            double controlSize = Math.Min(availableWidth, availableHeight);
            if (controlSize <= 0)
            {
                IconPositions.Clear();
                ClearSliceTransforms();
                ClearCenterPercentage();
                ClearCenterPercentageOffset();
                return;
            }

            var seriesList = PieSeries?.OfType<PieSeries>().ToList() ?? new List<PieSeries>();
            var sliceData = seriesList
                .Select(s =>
                {
                    var values = s.Values as ChartValues<PieSliceChartData>;
                    return values?.Count > 0 ? values[0] : null;
                })
                .ToList();

            UpdateCenterPercentageOffset(seriesList);
            UpdateCenterPercentage(sliceData, controlSize);

            if (seriesList.Count == 0 || LegendItems == null || LegendItems.Count == 0)
            {
                IconPositions.Clear();
                ClearSliceTransforms();
                return;
            }

            var chartValues = sliceData.Select(data => data?.ChartValue ?? 0).ToList();
            if (chartValues.Count == 0 || chartValues.All(v => v == 0))
            {
                IconPositions.Clear();
                ClearSliceTransforms();
                return;
            }

            double totalValue = chartValues.Sum();
            double pieRadius = controlSize / 2.0;
            double iconRadius = pieRadius + IconOffset;
            double centerX = margin.Left + (availableWidth / 2.0);
            double centerY = margin.Top + (availableHeight / 2.0);

            // Build highlight set for computing offsets in single pass
            var highlighted = new HashSet<string>(
                (HighlightedLabels ?? Enumerable.Empty<string>())
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(hoveredSliceLabel))
            {
                highlighted.Add(hoveredSliceLabel);
            }

            double currentAngle = LiveChartsRotationOffset;
            var iconCandidates = new List<IconCandidate>();
            var sliceTransformData = new List<(PieSlice Slice, double OffsetX, double OffsetY)>();

            for (int i = 0; i < chartValues.Count && i < LegendItems.Count; i++)
            {
                double sliceArc = (chartValues[i] / totalValue) * 360.0;
                double midpointAngle = currentAngle + (sliceArc / 2.0);
                double angleRadians = midpointAngle * Math.PI / 180.0;

                var legend = LegendItems[i];
                var series = seriesList[i];
                var isHighlighted = !string.IsNullOrWhiteSpace(series?.Title) && highlighted.Contains(series.Title);
                var highlightOffset = isHighlighted ? SliceHighlightOffset : 0.0;
                var offsetX = highlightOffset * Math.Sin(angleRadians);
                var offsetY = -highlightOffset * Math.Cos(angleRadians);

                // Collect slice transform data for batch application
                var slice = GetPieSlice(series);
                if (slice != null)
                {
                    sliceTransformData.Add((slice, offsetX, offsetY));
                }

                // Only show icon if count > 0
                var shouldShowRadialIcon = legend.Count > 0 &&
                    (i >= sliceData.Count || sliceData[i]?.ShowRadialIcon != false);
                if (shouldShowRadialIcon)
                {
                    var data = i < sliceData.Count ? sliceData[i] : null;
                    // Clockwise from top: x = sin(θ), y = -cos(θ)
                    double x = centerX + iconRadius * Math.Sin(angleRadians) - IconSize / 2.0;
                    double y = centerY - iconRadius * Math.Cos(angleRadians) - IconSize / 2.0;

                    iconCandidates.Add(new IconCandidate
                    {
                        Sequence = i,
                        Count = legend.Count,
                        IsHighlighted = isHighlighted,
                        SuppressOnCollision = data?.SuppressRadialIconOnCollision == true,
                        CenterX = x + (IconSize / 2.0) + offsetX,
                        CenterY = y + (IconSize / 2.0) + offsetY,
                        Position = new PieIconPosition
                        {
                            Label = legend.Label,
                            IconKey = legend.IconKey,
                            ColorHex = legend.ColorHex,
                            Count = legend.Count,
                            X = x,
                            Y = y,
                            OffsetX = offsetX,
                            OffsetY = offsetY
                        }
                    });
                }

                currentAngle += sliceArc;
            }

            // Apply all updates in sequence (but computed in single pass above)
            SynchronizePositions(ResolveIconPositions(iconCandidates));
            ApplySliceTransformsBatch(sliceTransformData);
        }

        private void UpdateCenterPercentage(IReadOnlyList<PieSliceChartData> sliceData, double controlSize)
        {
            if (!ShowCenterPercentage)
            {
                ClearCenterPercentage();
                return;
            }

            if (TryGetExactCounts(out var exactUnlockedCount, out var exactTotalCount))
            {
                UpdateCenterPercentage(exactUnlockedCount, exactTotalCount, controlSize);
                return;
            }

            if (sliceData == null || sliceData.Count == 0)
            {
                ClearCenterPercentage();
                return;
            }

            var totalCount = sliceData
                .Where(data => data != null)
                .Sum(data => Math.Max(0, data.Count));
            var unlockedCount = sliceData
                .Where(data => data != null && !data.IsLocked)
                .Sum(data => Math.Max(0, data.Count));
            UpdateCenterPercentage(unlockedCount, totalCount, controlSize);
        }

        private void UpdateCenterPercentage(int unlockedCount, int totalCount, double controlSize)
        {
            if (totalCount <= 0)
            {
                ClearCenterPercentage();
                return;
            }

            unlockedCount = Math.Max(0, Math.Min(unlockedCount, totalCount));
            var roundedPercent = (int)Math.Round(unlockedCount * 100d / totalCount, MidpointRounding.AwayFromZero);

            CenterPercentageText = $"{roundedPercent}%";
            CenterPercentageFontSize = Math.Max(11, Math.Min(18, controlSize * 0.13));
            CenterPercentageVisibility = Visibility.Visible;
        }

        private bool TryGetExactCounts(out int unlockedCount, out int totalCount)
        {
            totalCount = ExactTotalCount;
            unlockedCount = ExactUnlockedCount;
            return totalCount >= 0;
        }

        private void ClearCenterPercentage()
        {
            CenterPercentageText = string.Empty;
            CenterPercentageFontSize = 11;
            CenterPercentageVisibility = Visibility.Collapsed;
        }

        private void UpdateCenterPercentageOffset(IReadOnlyList<PieSeries> seriesList)
        {
            if (!TryGetRenderedPieBounds(seriesList, out var pieBounds))
            {
                ClearCenterPercentageOffset();
                return;
            }

            CenterPercentageOffsetX = (pieBounds.Left + (pieBounds.Width / 2.0)) - (ActualWidth / 2.0);
            CenterPercentageOffsetY = (pieBounds.Top + (pieBounds.Height / 2.0)) - (ActualHeight / 2.0);
        }

        private void ClearCenterPercentageOffset()
        {
            CenterPercentageOffsetX = 0;
            CenterPercentageOffsetY = 0;
        }

        private bool TryGetRenderedPieBounds(IReadOnlyList<PieSeries> seriesList, out Rect pieBounds)
        {
            pieBounds = Rect.Empty;
            if (seriesList == null || seriesList.Count == 0)
            {
                return false;
            }

            foreach (var series in seriesList)
            {
                var slice = GetPieSlice(series);
                if (slice == null)
                {
                    continue;
                }

                Rect bounds;
                try
                {
                    var localBounds = VisualTreeHelper.GetDescendantBounds(slice);
                    if (localBounds.IsEmpty)
                    {
                        continue;
                    }

                    bounds = slice.TransformToAncestor(this).TransformBounds(localBounds);
                }
                catch
                {
                    continue;
                }

                if (slice.RenderTransform is TranslateTransform translate)
                {
                    bounds.Offset(-translate.X, -translate.Y);
                }

                pieBounds = pieBounds.IsEmpty ? bounds : Rect.Union(pieBounds, bounds);
            }

            return !pieBounds.IsEmpty;
        }

        private void ApplySliceTransformsBatch(List<(PieSlice Slice, double OffsetX, double OffsetY)> transformData)
        {
            foreach (var (slice, offsetX, offsetY) in transformData)
            {
                SetSliceTransform(slice, offsetX, offsetY);
            }
        }

        private void SynchronizePositions(IReadOnlyList<PieIconPosition> positions)
        {
            // Remove extra positions from the end
            while (IconPositions.Count > positions.Count)
            {
                IconPositions.RemoveAt(IconPositions.Count - 1);
            }

            // Update existing or add new positions
            for (int i = 0; i < positions.Count; i++)
            {
                var newPos = positions[i];
                if (i < IconPositions.Count)
                {
                    // Update existing position in-place
                    var existing = IconPositions[i];
                    existing.Label = newPos.Label;
                    existing.IconKey = newPos.IconKey;
                    existing.ColorHex = newPos.ColorHex;
                    existing.Count = newPos.Count;
                    existing.X = newPos.X;
                    existing.Y = newPos.Y;
                    existing.OffsetX = newPos.OffsetX;
                    existing.OffsetY = newPos.OffsetY;
                }
                else
                {
                    // Add new position
                    IconPositions.Add(newPos);
                }
            }
        }

        private void OnPieChartDataClick(object sender, ChartPoint chartPoint)
        {
            // Only respond to left clicks on actual data points
            if (chartPoint == null || Mouse.LeftButton != MouseButtonState.Pressed) return;

            // Get the label from the PieSeries that was clicked
            if (chartPoint.SeriesView is PieSeries series && !string.IsNullOrEmpty(series.Title))
            {
                SliceClick?.Invoke(this, series.Title);
            }
        }

        private void OnPieChartDataHover(object sender, ChartPoint chartPoint)
        {
            var nextHoveredSliceLabel = (chartPoint?.SeriesView as PieSeries)?.Title;
            if (string.Equals(hoveredSliceLabel, nextHoveredSliceLabel, StringComparison.Ordinal))
            {
                return;
            }

            hoveredSliceLabel = nextHoveredSliceLabel;
            ApplySliceTransforms();
        }

        private void OnPieChartMouseLeave(object sender, MouseEventArgs e)
        {
            if (string.IsNullOrEmpty(hoveredSliceLabel))
            {
                return;
            }

            hoveredSliceLabel = null;
            ApplySliceTransforms();
        }

        private static List<PieIconPosition> ResolveIconPositions(IReadOnlyList<IconCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return new List<PieIconPosition>();
            }

            if (!candidates.Any(candidate => candidate.SuppressOnCollision))
            {
                return candidates.Select(candidate => candidate.Position).ToList();
            }

            var visible = Enumerable.Repeat(true, candidates.Count).ToArray();
            bool removedCandidate;
            do
            {
                removedCandidate = false;
                var visibleIndices = Enumerable.Range(0, candidates.Count)
                    .Where(index => visible[index])
                    .ToList();

                if (visibleIndices.Count < 2)
                {
                    break;
                }

                for (int i = 0; i < visibleIndices.Count; i++)
                {
                    var currentIndex = visibleIndices[i];
                    var nextIndex = visibleIndices[(i + 1) % visibleIndices.Count];
                    if (currentIndex == nextIndex || !IconsOverlap(candidates[currentIndex], candidates[nextIndex]))
                    {
                        continue;
                    }

                    var indexToRemove = ChooseCollisionRemovalIndex(candidates, currentIndex, nextIndex);
                    if (indexToRemove < 0 || !visible[indexToRemove])
                    {
                        continue;
                    }

                    visible[indexToRemove] = false;
                    removedCandidate = true;
                    break;
                }
            }
            while (removedCandidate);

            return Enumerable.Range(0, candidates.Count)
                .Where(index => visible[index])
                .Select(index => candidates[index].Position)
                .ToList();
        }

        private static bool IconsOverlap(IconCandidate first, IconCandidate second)
        {
            var minimumDistance = IconSize + IconCollisionPadding;
            var minimumDistanceSquared = minimumDistance * minimumDistance;
            var deltaX = first.CenterX - second.CenterX;
            var deltaY = first.CenterY - second.CenterY;
            return (deltaX * deltaX) + (deltaY * deltaY) < minimumDistanceSquared;
        }

        private static int ChooseCollisionRemovalIndex(IReadOnlyList<IconCandidate> candidates, int firstIndex, int secondIndex)
        {
            var first = candidates[firstIndex];
            var second = candidates[secondIndex];

            if (!first.SuppressOnCollision && !second.SuppressOnCollision)
            {
                return -1;
            }

            if (!first.SuppressOnCollision)
            {
                return second.SuppressOnCollision ? secondIndex : -1;
            }

            if (!second.SuppressOnCollision)
            {
                return firstIndex;
            }

            if (first.IsHighlighted != second.IsHighlighted)
            {
                return first.IsHighlighted ? secondIndex : firstIndex;
            }

            if (first.Count != second.Count)
            {
                return first.Count < second.Count ? firstIndex : secondIndex;
            }

            return first.Sequence > second.Sequence ? firstIndex : secondIndex;
        }

        private void ApplySliceTransforms(IReadOnlyList<double> chartValues = null)
        {
            var seriesList = PieSeries?.OfType<PieSeries>().ToList();
            if (seriesList == null || seriesList.Count == 0)
            {
                return;
            }

            chartValues = chartValues ?? seriesList
                .Select(series =>
                {
                    var values = series.Values as ChartValues<PieSliceChartData>;
                    return values?.Count > 0 ? values[0].ChartValue : 0;
                })
                .ToList();

            if (chartValues.Count == 0 || chartValues.All(value => value == 0))
            {
                ClearSliceTransforms();
                return;
            }

            var highlighted = new HashSet<string>(
                (HighlightedLabels ?? Enumerable.Empty<string>())
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(hoveredSliceLabel))
            {
                highlighted.Add(hoveredSliceLabel);
            }

            var totalValue = chartValues.Sum();
            if (totalValue <= 0)
            {
                ClearSliceTransforms();
                return;
            }

            double currentAngle = LiveChartsRotationOffset;
            var iconOffsets = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < seriesList.Count; i++)
            {
                var series = seriesList[i];
                var sliceValue = i < chartValues.Count ? chartValues[i] : 0;
                var sliceArc = (sliceValue / totalValue) * 360.0;
                var midpointAngle = currentAngle + (sliceArc / 2.0);
                var angleRadians = midpointAngle * Math.PI / 180.0;
                var isHighlighted = !string.IsNullOrWhiteSpace(series.Title) && highlighted.Contains(series.Title);
                var offset = isHighlighted ? SliceHighlightOffset : 0.0;
                var slice = GetPieSlice(series);
                var offsetX = offset * Math.Sin(angleRadians);
                var offsetY = -offset * Math.Cos(angleRadians);

                if (!string.IsNullOrWhiteSpace(series.Title))
                {
                    iconOffsets[series.Title] = (offsetX, offsetY);
                }

                SetSliceTransform(
                    slice,
                    offsetX,
                    offsetY);

                currentAngle += sliceArc;
            }

            ApplyIconOffsets(iconOffsets);
        }

        private void ClearSliceTransforms()
        {
            if (PieSeries == null)
            {
                return;
            }

            foreach (var series in PieSeries.OfType<PieSeries>())
            {
                SetSliceTransform(GetPieSlice(series), 0, 0);
            }

            ClearIconOffsets();
        }

        private static PieSlice GetPieSlice(PieSeries series)
        {
            if (series == null)
            {
                return null;
            }

            var chartPoint = series.ChartPoints?.FirstOrDefault();
            if (chartPoint == null)
            {
                return null;
            }

            var pointView = chartPoint.View;
            if (pointView == null || PiePointViewSliceProperty == null)
            {
                return null;
            }

            return PiePointViewSliceProperty?.GetValue(pointView) as PieSlice;
        }

        private void ApplyIconOffsets(IReadOnlyDictionary<string, (double X, double Y)> iconOffsets)
        {
            if (IconPositions.Count == 0)
            {
                return;
            }

            foreach (var iconPosition in IconPositions)
            {
                var offset = !string.IsNullOrWhiteSpace(iconPosition.Label) &&
                             iconOffsets != null &&
                             iconOffsets.TryGetValue(iconPosition.Label, out var resolvedOffset)
                    ? resolvedOffset
                    : (0.0, 0.0);

                iconPosition.OffsetX = offset.Item1;
                iconPosition.OffsetY = offset.Item2;
            }
        }

        private void ClearIconOffsets()
        {
            if (IconPositions.Count == 0)
            {
                return;
            }

            foreach (var iconPosition in IconPositions)
            {
                iconPosition.OffsetX = 0;
                iconPosition.OffsetY = 0;
            }
        }

        private static void SetSliceTransform(PieSlice slice, double x, double y)
        {
            if (slice == null)
            {
                return;
            }

            if (!(slice.RenderTransform is TranslateTransform transform))
            {
                transform = new TranslateTransform();
                slice.RenderTransform = transform;
            }

            AnimateTransformAxis(transform, TranslateTransform.XProperty, x);
            AnimateTransformAxis(transform, TranslateTransform.YProperty, y);
        }

        private static void AnimateTransformAxis(TranslateTransform transform, DependencyProperty property, double target)
        {
            var current = property == TranslateTransform.XProperty ? transform.X : transform.Y;
            if (Math.Abs(current - target) < 0.01)
            {
                if (property == TranslateTransform.XProperty)
                {
                    transform.X = target;
                }
                else
                {
                    transform.Y = target;
                }
                return;
            }

            var animation = new DoubleAnimation
            {
                To = target,
                Duration = SliceAnimationDuration,
                EasingFunction = SliceAnimationEasing,
                FillBehavior = FillBehavior.HoldEnd
            };

            transform.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
