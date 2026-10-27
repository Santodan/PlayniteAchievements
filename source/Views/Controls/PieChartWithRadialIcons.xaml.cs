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
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;

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
        private const double RingInnerRadius = 30.0;
        private const double SliceAnimationMilliseconds = 150.0;
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
        private bool legendSyncScheduled;
        private string hoveredSliceLabel;

        // Slices sliding toward a new growth, stepped once per rendered frame.
        private readonly Dictionary<PieSlice, SliceTween> sliceTweens = new Dictionary<PieSlice, SliceTween>();
        private bool renderingHooked;
        private TimeSpan? lastRenderingTime;
        private bool steppingTweens;

        private sealed class SliceTween
        {
            public double From { get; set; }
            public double To { get; set; }
            public double Progress { get; set; }
            public bool Started { get; set; }
        }

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

        /// <summary>The same click as <see cref="SliceClick"/>, bubbled for hosts outside the control.</summary>
        public static readonly RoutedEvent SliceClickedEvent = EventManager.RegisterRoutedEvent(
            "SliceClicked",
            RoutingStrategy.Bubble,
            typeof(ChartClickEventHandler),
            typeof(PieChartWithRadialIcons));

        private void RaiseSliceClick(string label)
        {
            SliceClick?.Invoke(this, label);
            RaiseEvent(new ChartClickEventArgs(SliceClickedEvent, this) { Label = label });
        }

        public static readonly DependencyProperty PieSeriesProperty =
            DependencyProperty.Register(nameof(PieSeries), typeof(SeriesCollection), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(null, OnPieSeriesChanged));

        public static readonly DependencyProperty LegendItemsProperty =
            DependencyProperty.Register(nameof(LegendItems), typeof(ObservableCollection<LegendItem>), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(null, OnLegendItemsChanged));

        public static readonly DependencyProperty IconOffsetProperty =
            DependencyProperty.Register(nameof(IconOffset), typeof(double), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(12.0, OnLayoutPropertyChanged));

        /// <summary>
        /// Space between the control edge and the pie. Hosts that clip to their bounds need enough
        /// here to hold the radial icons, including their hover push.
        /// </summary>
        public static readonly DependencyProperty PieMarginProperty =
            DependencyProperty.Register(nameof(PieMargin), typeof(Thickness), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(new Thickness(10), OnLayoutPropertyChanged));

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

        /// <summary>
        /// When true, draws a full pie instead of a ring, so there is no center to hold the percentage.
        /// </summary>
        public static readonly DependencyProperty IsFilledProperty =
            DependencyProperty.Register(nameof(IsFilled), typeof(bool), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(false, OnIsFilledChanged));

        /// <summary>
        /// When true, draws a legend of up to eight rows beside the pie, on the
        /// <see cref="LegendPosition"/> side.
        /// </summary>
        public static readonly DependencyProperty ShowLegendProperty =
            DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(false, OnLayoutPropertyChanged));

        public static readonly DependencyProperty LegendPositionProperty =
            DependencyProperty.Register(nameof(LegendPosition), typeof(PieLegendPosition), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(PieLegendPosition.Right));

        public static readonly DependencyProperty ShowIconsProperty =
            DependencyProperty.Register(nameof(ShowIcons), typeof(bool), typeof(PieChartWithRadialIcons),
                new PropertyMetadata(true, OnLayoutPropertyChanged));

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

        public Thickness PieMargin
        {
            get => (Thickness)GetValue(PieMarginProperty);
            set => SetValue(PieMarginProperty, value);
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

        public bool IsFilled
        {
            get => (bool)GetValue(IsFilledProperty);
            set => SetValue(IsFilledProperty, value);
        }

        public bool ShowLegend
        {
            get => (bool)GetValue(ShowLegendProperty);
            set => SetValue(ShowLegendProperty, value);
        }

        public PieLegendPosition LegendPosition
        {
            get => (PieLegendPosition)GetValue(LegendPositionProperty);
            set => SetValue(LegendPositionProperty, value);
        }

        public ObservableCollection<PieLegendRowViewModel> LegendRows { get; } = new ObservableCollection<PieLegendRowViewModel>();

        public bool ShowIcons
        {
            get => (bool)GetValue(ShowIconsProperty);
            set => SetValue(ShowIconsProperty, value);
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
            // The pie's square changes size when the legend appears or changes width, even when
            // the control itself keeps its size.
            PieHost.SizeChanged += OnSizeChanged;
            Chart.UpdaterTick += OnChartUpdated;
            UpdateIconOverflowInset();
        }

        /// <summary>
        /// Keeps the radial icons at rest inside this control's width so they do not reach a
        /// neighboring pie's legend. The panel only shrinks the pie for it when the width, not the
        /// height, limits the pie.
        /// </summary>
        private void UpdateIconOverflowInset()
        {
            var margin = PieMargin;
            var overflow = IconOffset + (IconSize / 2.0) - Math.Min(margin.Left, margin.Right);
            LayoutPanel.HorizontalInset = ShowIcons ? Math.Max(0, overflow) : 0;
        }

        /// <summary>
        /// Rebuilds the legend rows ahead of the next layout pass, so the legend's new width and the
        /// pie's new size settle in one pass instead of after the deferred position calculation.
        /// </summary>
        private void ScheduleLegendSync()
        {
            if (legendSyncScheduled)
            {
                return;
            }

            legendSyncScheduled = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
            {
                legendSyncScheduled = false;
                SynchronizeLegendRows();
            }));
        }

        /// <summary>
        /// Schedules the icon, center percentage and slice offset positions. Multiple calls are
        /// deduplicated to a single calculation.
        /// </summary>
        /// <remarks>
        /// Runs at DataBind, ahead of the next render, so new slices, their icons and the center
        /// percentage appear in the same frame. After new values the chart is redrawn first
        /// rather than left to LiveCharts' own timer, because the slice offsets are applied to
        /// the slices it draws. Waiting for an idle pass instead put the icons a frame or more
        /// behind the slices, and behind any filter pass queued meanwhile.
        /// </remarks>
        private void ScheduleCalculation(bool dataChanged = false)
        {
            chartDataChanged |= dataChanged;
            if (calculationScheduled)
            {
                return;
            }
            calculationScheduled = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
            {
                calculationScheduled = false;
                if (chartDataChanged && IsLoaded)
                {
                    chartDataChanged = false;
                    // The legend rows and the layout they cause come first, so the chart draws
                    // at the size it keeps rather than full width and then shrinking.
                    SynchronizeLegendRows();
                    UpdateLayout();
                    Chart.Update(false, true);
                }

                CalculatePositions();
            }));
        }

        private bool chartDataChanged;

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
            control.ScheduleCalculation(dataChanged: true);
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
                ScheduleCalculation(dataChanged: true);
            }
        }

        private void OnChartValuesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshSliceDataSubscriptions();
            ScheduleCalculation(dataChanged: true);
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
            control.ScheduleLegendSync();
            control.ScheduleCalculation();
        }

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PieChartWithRadialIcons)d;
            control.UpdateIconOverflowInset();
            control.ScheduleLegendSync();
            control.ScheduleCalculation();
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

        private static void OnIsFilledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PieChartWithRadialIcons)d;
            control.Chart.InnerRadius = (bool)e.NewValue ? 0 : RingInnerRadius;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachCurrentSources();
            AttachTooltip();
            ScheduleCalculation();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DetachCurrentSources();
            DetachTooltip();
            FinishSliceTweens();
        }

        // The hover tooltip, shown in the window's adorner layer in place of LiveCharts' Popup
        // (see ChartTooltipAdorner). A window without an adorner layer keeps the Popup.
        private PieChartTooltip _tooltip;
        private ChartTooltipAdorner _tooltipAdorner;

        // The layer the adorner was added to. Unloaded can run after the chart has left the
        // tree, when looking the layer up again finds none.
        private System.Windows.Documents.AdornerLayer _tooltipLayer;

        /// <summary>How far LiveCharts sets a pie tooltip off the center toward the slice.</summary>
        private const double TooltipCenterOffset = 15.0;

        private void AttachTooltip()
        {
            if (_tooltipAdorner != null || !(System.Windows.Documents.AdornerLayer.GetAdornerLayer(PieHost) is System.Windows.Documents.AdornerLayer layer))
            {
                return;
            }

            _tooltip = _tooltip ?? new PieChartTooltip();
            _tooltipAdorner = new ChartTooltipAdorner(PieHost, _tooltip) { Visibility = Visibility.Collapsed };
            layer.Add(_tooltipAdorner);
            _tooltipLayer = layer;
            Chart.DataTooltip = null;
        }

        /// <summary>
        /// Removes the adorner and frees the tooltip, which the next Loaded hands to a new adorner:
        /// a chart reloaded in place (switching back to the start page) would otherwise give a
        /// tooltip that still has a parent to a second one, which WPF rejects.
        /// </summary>
        private void DetachTooltip()
        {
            if (_tooltipAdorner == null)
            {
                return;
            }

            _tooltipLayer?.Remove(_tooltipAdorner);
            _tooltipAdorner.ReleaseTooltip();
            _tooltipAdorner = null;
            _tooltipLayer = null;
        }

        /// <summary>
        /// Shows the hovered slice's tooltip where LiveCharts put its own: a little off the pie's
        /// center toward the slice, on the side of the center the slice is on. The content and
        /// the position are set together, so they change in the same frame.
        /// </summary>
        private void ShowTooltip(ChartPoint point)
        {
            if (_tooltipAdorner == null || !(point?.SeriesView is PieSeries series))
            {
                return;
            }

            var slice = GetPieSlice(series);
            if (slice == null || !TryGetPieCenter(new[] { series }, out var center))
            {
                HideTooltip();
                return;
            }

            _tooltip.Points = new List<DataPointViewModel>
            {
                new DataPointViewModel
                {
                    ChartPoint = point,
                    Series = new SeriesViewModel { Title = series.Title, Fill = series.Fill, Stroke = series.Stroke }
                }
            };
            // LiveCharts' own placement (PieChart.GetTooltipPosition), from the pie's center. It
            // shifts by the tooltip's size, so it runs once the new content has been measured.
            var angle = SliceMidAngle(slice, 0);
            var radians = (angle + 180.0) * Math.PI / 180.0;
            _tooltipAdorner.Place(size => new Point(
                center.X + (angle > 0.0 && angle < 180.0 ? -size.Width : 0) + (Math.Sin(radians) * TooltipCenterOffset),
                center.Y + (angle > 90.0 && angle < 270.0 ? -size.Height : 0) - (Math.Cos(radians) * TooltipCenterOffset)));
            _tooltipAdorner.Visibility = Visibility.Visible;
        }

        private void HideTooltip()
        {
            if (_tooltipAdorner != null)
            {
                _tooltipAdorner.Visibility = Visibility.Collapsed;
            }
        }

        // Off the slices (the hole, the gaps, the margin) the tooltip goes, as LiveCharts' did
        // when the mouse left a slice. Over a slice the topmost element is its hover shape.
        private void OnPieChartMouseMove(object sender, MouseEventArgs e)
        {
            if (!(e.OriginalSource is PieSlice))
            {
                HideTooltip();
            }
        }

        // A new size redraws the slices now; LiveCharts' own redraw comes on its timer, frames
        // after the pie's square has already changed.
        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleCalculation(dataChanged: true);
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
            ScheduleCalculation(dataChanged: true);
        }

        private void OnLegendItemsCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RefreshLegendItemSubscriptions();
            ScheduleLegendSync();
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
            ScheduleCalculation(dataChanged: true);
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
            ScheduleLegendSync();
            ScheduleCalculation();
        }

        private void CalculatePositions()
        {
            SynchronizeLegendRows();

            var margin = Chart?.Margin ?? new Thickness(0);
            double availableWidth = Math.Max(0, PieHost.ActualWidth - margin.Left - margin.Right);
            double availableHeight = Math.Max(0, PieHost.ActualHeight - margin.Top - margin.Bottom);
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
                var legend = LegendItems[i];
                var series = seriesList[i];
                var slice = GetPieSlice(series);
                double midpointAngle = SliceMidAngle(slice, currentAngle + (sliceArc / 2.0));
                double angleRadians = midpointAngle * Math.PI / 180.0;

                var isHighlighted = !string.IsNullOrWhiteSpace(series?.Title) && highlighted.Contains(series.Title);
                var highlightOffset = isHighlighted ? SliceHighlightOffset : 0.0;
                var offsetX = highlightOffset * Math.Sin(angleRadians);
                var offsetY = -highlightOffset * Math.Cos(angleRadians);

                // Collect slice transform data for batch application
                if (slice != null)
                {
                    sliceTransformData.Add((slice, offsetX, offsetY));
                }

                // Only show icon if count > 0
                var shouldShowRadialIcon = ShowIcons &&
                    legend.Count > 0 &&
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
                            IconRefreshKey = legend.IconRefreshKey,
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
            var roundedPercent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(unlockedCount, totalCount);

            CenterPercentageText = PercentFormatter.FormatWhole(roundedPercent);
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
            if (!TryGetPieCenter(seriesList, out var center))
            {
                ClearCenterPercentageOffset();
                return;
            }

            CenterPercentageOffsetX = center.X - (PieHost.ActualWidth / 2.0);
            CenterPercentageOffsetY = center.Y - (PieHost.ActualHeight / 2.0);
        }

        /// <summary>
        /// The pie's center in PieHost coordinates: LiveCharts draws every slice around its own
        /// origin and places that origin, via Canvas.Left and Top, at the center. Unlike the
        /// slices' rendered bounds, this holds before the redrawn slices render and is not
        /// pulled toward a popped-out slice.
        /// </summary>
        private bool TryGetPieCenter(IReadOnlyList<PieSeries> seriesList, out Point center)
        {
            center = default(Point);
            foreach (var series in seriesList ?? Array.Empty<PieSeries>())
            {
                var slice = GetPieSlice(series);
                var left = slice != null ? Canvas.GetLeft(slice) : double.NaN;
                var top = slice != null ? Canvas.GetTop(slice) : double.NaN;
                if (double.IsNaN(left) || double.IsNaN(top) || !(VisualTreeHelper.GetParent(slice) is Visual parent))
                {
                    continue;
                }

                try
                {
                    center = parent.TransformToAncestor(PieHost).Transform(new Point(left, top));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // Not under PieHost (between a series swap and the next draw).
                }
            }

            return false;
        }

        private void ClearCenterPercentageOffset()
        {
            CenterPercentageOffsetX = 0;
            CenterPercentageOffsetY = 0;
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
                    existing.IconRefreshKey = newPos.IconRefreshKey;
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
                RaiseSliceClick(series.Title);
            }
        }

        private void OnPieChartDataHover(object sender, ChartPoint chartPoint)
        {
            SetHoveredSlice((chartPoint?.SeriesView as PieSeries)?.Title);
            ShowTooltip(chartPoint);
        }

        private void OnPieChartMouseLeave(object sender, MouseEventArgs e)
        {
            SetHoveredSlice(null);
            HideTooltip();
        }

        private void SetHoveredSlice(string label)
        {
            var nextHoveredSliceLabel = string.IsNullOrEmpty(label) ? null : label;
            if (string.Equals(hoveredSliceLabel, nextHoveredSliceLabel, StringComparison.Ordinal))
            {
                return;
            }

            hoveredSliceLabel = nextHoveredSliceLabel;
            ApplySliceTransforms();
        }

        private void OnLegendRowMouseEnter(object sender, MouseEventArgs e)
        {
            SetHoveredSlice(((sender as FrameworkElement)?.DataContext as PieLegendRowViewModel)?.Label);
        }

        private void OnLegendRowMouseLeave(object sender, MouseEventArgs e)
        {
            SetHoveredSlice(null);
        }

        private void OnLegendRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var label = ((sender as FrameworkElement)?.DataContext as PieLegendRowViewModel)?.Label;
            if (!string.IsNullOrEmpty(label))
            {
                RaiseSliceClick(label);
            }
        }

        /// <summary>
        /// The legend's ScrollViewer marks every wheel event handled; while its rows fit, the wheel
        /// is passed on so the page around the pie keeps scrolling.
        /// </summary>
        private void OnLegendPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled
                || !(sender is ScrollViewer scroller)
                || scroller.ScrollableHeight > 0
                || !(VisualTreeHelper.GetParent(scroller) is UIElement parent))
            {
                return;
            }

            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = scroller
            });
        }

        /// <summary>
        /// Updates the legend rows in place by index, adding or removing only the rows the count
        /// changed by, so a data change neither regenerates row visuals nor drops the row under
        /// the mouse.
        /// </summary>
        private void SynchronizeLegendRows()
        {
            var items = ShowLegend
                ? (LegendItems ?? Enumerable.Empty<LegendItem>()).Where(item => item != null).ToList()
                : new List<LegendItem>();

            while (LegendRows.Count > items.Count)
            {
                LegendRows.RemoveAt(LegendRows.Count - 1);
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (i < LegendRows.Count)
                {
                    LegendRows[i].Update(items[i]);
                }
                else
                {
                    LegendRows.Add(new PieLegendRowViewModel(items[i]));
                }
            }
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
                var slice = GetPieSlice(series);
                var midpointAngle = SliceMidAngle(slice, currentAngle + (sliceArc / 2.0));
                var angleRadians = midpointAngle * Math.PI / 180.0;
                var isHighlighted = !string.IsNullOrWhiteSpace(series.Title) && highlighted.Contains(series.Title);
                var offset = isHighlighted ? SliceHighlightOffset : 0.0;
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

        /// <summary>
        /// The middle of a drawn slice, in degrees clockwise from the top: the slice's own angles
        /// as LiveCharts set them, so a pop-out runs straight out of the slice that is drawn. The
        /// angle worked out from the values stands in until the slice exists.
        /// </summary>
        private static double SliceMidAngle(PieSlice slice, double fallback)
        {
            return slice != null && slice.WedgeAngle > 0
                ? slice.RotationAngle + (slice.WedgeAngle / 2.0)
                : fallback;
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

        /// <summary>
        /// Highlights a slice by moving its outer edge past the ring, and its inner edge away
        /// from the center, by the length of (<paramref name="x"/>, <paramref name="y"/>), the same offset
        /// its icon moves by. The
        /// slice's sides stay on its neighbours' edges, so no slice slides and the separators
        /// stay even whatever the slice's angle.
        /// </summary>
        private void SetSliceTransform(PieSlice slice, double x, double y)
        {
            if (slice == null)
            {
                return;
            }

            var target = Math.Sqrt((x * x) + (y * y));
            if (sliceTweens.TryGetValue(slice, out var running) && Math.Abs(running.To - target) < 0.01)
            {
                return;
            }

            var current = (double)slice.GetValue(SliceGrowthProperty);
            if (Math.Abs(current - target) < 0.01)
            {
                sliceTweens.Remove(slice);
                slice.SetValue(SliceGrowthProperty, target);
                return;
            }

            // A retarget mid-slide starts from where the slice is now.
            sliceTweens[slice] = new SliceTween { From = current, To = target };
            if (!renderingHooked)
            {
                renderingHooked = true;
                lastRenderingTime = null;
                CompositionTarget.Rendering += OnSliceTweenRendering;
            }
        }

        /// <summary>
        /// Steps every sliding slice by one frame. A frame advances the slide by at most one
        /// refresh interval, however long the frame before it took: a selection or filter click
        /// starts the slide just before the pie, its sibling widgets and the overview grid all
        /// redo their layout, and a wall-clock animation spent most of its time in that stall
        /// and drew only its last few positions.
        /// </summary>
        private void OnSliceTweenRendering(object sender, EventArgs e)
        {
            var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
            // Rendering can be raised more than once for the same frame.
            if (lastRenderingTime == now)
            {
                return;
            }

            var elapsedMs = lastRenderingTime.HasValue ? (now - lastRenderingTime.Value).TotalMilliseconds : 0.0;
            lastRenderingTime = now;
            var rate = AnimationFrameRate.Current;
            var maxStepMs = 1000.0 / (rate > 0 ? rate : 60);
            var step = Math.Max(0, Math.Min(elapsedMs, maxStepMs)) / SliceAnimationMilliseconds;

            steppingTweens = true;
            try
            {
                foreach (var pair in sliceTweens.ToList())
                {
                    var tween = pair.Value;
                    if (tween.Started)
                    {
                        tween.Progress = Math.Min(1.0, tween.Progress + step);
                    }
                    else
                    {
                        // The first frame draws the starting pose.
                        tween.Started = true;
                    }

                    var eased = SliceAnimationEasing.Ease(tween.Progress);
                    pair.Key.SetValue(SliceGrowthProperty, tween.From + ((tween.To - tween.From) * eased));
                    if (tween.Progress >= 1.0)
                    {
                        sliceTweens.Remove(pair.Key);
                    }
                }
            }
            finally
            {
                steppingTweens = false;
            }

            UpdateSingleSliceOutline();
            if (sliceTweens.Count == 0)
            {
                UnhookSliceTweenRendering();
            }
        }

        private void FinishSliceTweens()
        {
            foreach (var pair in sliceTweens.ToList())
            {
                pair.Key.SetValue(SliceGrowthProperty, pair.Value.To);
            }

            sliceTweens.Clear();
            UnhookSliceTweenRendering();
        }

        private void UnhookSliceTweenRendering()
        {
            if (renderingHooked)
            {
                renderingHooked = false;
                CompositionTarget.Rendering -= OnSliceTweenRendering;
            }
        }

        /// <summary>How far a slice's outer edge reaches past the pie's radius; animated.</summary>
        private static readonly DependencyProperty SliceGrowthProperty = DependencyProperty.RegisterAttached(
            "SliceGrowth",
            typeof(double),
            typeof(PieChartWithRadialIcons),
            new PropertyMetadata(0.0, (d, e) => ApplySliceGrowth(d as PieSlice)));

        /// <summary>The pie's radius as LiveCharts last drew the slice, before any growth.</summary>
        private static readonly DependencyProperty SliceBaseRadiusProperty = DependencyProperty.RegisterAttached(
            "SliceBaseRadius",
            typeof(double),
            typeof(PieChartWithRadialIcons),
            new PropertyMetadata(double.NaN));

        /// <summary>The ring's inner radius as LiveCharts last drew the slice, before any growth.</summary>
        private static readonly DependencyProperty SliceBaseInnerRadiusProperty = DependencyProperty.RegisterAttached(
            "SliceBaseInnerRadius",
            typeof(double),
            typeof(PieChartWithRadialIcons),
            new PropertyMetadata(double.NaN));

        // Both edges move out by the growth: the outer one past the ring and the inner one away
        // from the center, so the slice lifts off the hole as well.
        /// <summary>The control a slice belongs to, so a growth step can move its outline too.</summary>
        private static readonly DependencyProperty SliceOwnerProperty = DependencyProperty.RegisterAttached(
            "SliceOwner",
            typeof(PieChartWithRadialIcons),
            typeof(PieChartWithRadialIcons),
            new PropertyMetadata(null));

        /// <summary>The separator thickness LiveCharts gives pie slices.</summary>
        private const double SliceOutlineThickness = 2.0;

        /// <summary>
        /// Outlines a lone slice's outer and inner edges, which its own separator would draw
        /// with a seam at its start angle. Each circle's stroke straddles the edge as a slice's
        /// outline does; an ellipse draws its stroke inside its bounds, hence the half-stroke
        /// padding. Hidden whenever two or more slices have values.
        /// </summary>
        private void UpdateSingleSliceOutline()
        {
            var drawn = (PieSeries?.OfType<PieSeries>() ?? Enumerable.Empty<PieSeries>())
                .Where(series => (series.Values as ChartValues<PieSliceChartData>)?.FirstOrDefault()?.ChartValue > 0)
                .Take(2)
                .ToList();
            var slice = drawn.Count == 1 ? GetPieSlice(drawn[0]) : null;
            if (slice == null || slice.Radius <= 0 || !TryGetPieCenter(drawn, out var center))
            {
                SingleSliceOuterOutline.Visibility = Visibility.Collapsed;
                SingleSliceInnerOutline.Visibility = Visibility.Collapsed;
                return;
            }

            PlaceOutline(SingleSliceOuterOutline, center, slice.Radius);
            PlaceOutline(SingleSliceInnerOutline, center, slice.InnerRadius);
        }

        private static void PlaceOutline(System.Windows.Shapes.Ellipse outline, Point center, double radius)
        {
            if (radius <= 0)
            {
                outline.Visibility = Visibility.Collapsed;
                return;
            }

            var reach = radius + (SliceOutlineThickness / 2.0);
            outline.StrokeThickness = SliceOutlineThickness;
            outline.Width = reach * 2;
            outline.Height = reach * 2;
            Canvas.SetLeft(outline, center.X - reach);
            Canvas.SetTop(outline, center.Y - reach);
            outline.Visibility = Visibility.Visible;
        }

        private static void ApplySliceGrowth(PieSlice slice)
        {
            if (slice == null)
            {
                return;
            }

            var baseRadius = (double)slice.GetValue(SliceBaseRadiusProperty);
            if (double.IsNaN(baseRadius))
            {
                baseRadius = slice.Radius;
                slice.SetValue(SliceBaseRadiusProperty, baseRadius);
            }

            var baseInnerRadius = (double)slice.GetValue(SliceBaseInnerRadiusProperty);
            if (double.IsNaN(baseInnerRadius))
            {
                baseInnerRadius = slice.InnerRadius;
                slice.SetValue(SliceBaseInnerRadiusProperty, baseInnerRadius);
            }

            var growth = (double)slice.GetValue(SliceGrowthProperty);
            slice.Radius = baseRadius + growth;
            slice.InnerRadius = baseInnerRadius + growth;
            // A frame step updates the outline once after moving every slice.
            if (slice.GetValue(SliceOwnerProperty) is PieChartWithRadialIcons owner && !owner.steppingTweens)
            {
                owner.UpdateSingleSliceOutline();
            }
        }

        // Every LiveCharts draw sets each slice's radius back to the pie's, so the growth goes
        // back on right after, in the same pass, before the frame renders. A draw can also move
        // the pie's center (LiveCharts redraws on its own after a resize, later than the resize
        // recalculation here), so the center percentage follows it from the same place.
        private void OnChartUpdated(object sender)
        {
            var seriesList = PieSeries?.OfType<PieSeries>().ToList() ?? new List<PieSeries>();
            if (ShowCenterPercentage)
            {
                UpdateCenterPercentageOffset(seriesList);
            }

            foreach (var series in seriesList)
            {
                var slice = GetPieSlice(series);
                if (slice == null)
                {
                    continue;
                }

                slice.SetValue(SliceOwnerProperty, this);
                slice.SetValue(SliceBaseRadiusProperty, slice.Radius);
                slice.SetValue(SliceBaseInnerRadiusProperty, slice.InnerRadius);
                ApplySliceGrowth(slice);
            }

            UpdateSingleSliceOutline();
        }
    }
}
