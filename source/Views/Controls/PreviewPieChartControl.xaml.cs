using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LiveCharts;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Preview control for pie chart display.
    /// Uses the actual PieChartWithRadialIcons control with mock data.
    /// </summary>
    public partial class PreviewPieChartControl : UserControl
    {
        private readonly PieChartViewModel _viewModel = new PieChartViewModel();

        public SeriesCollection PieSeries => _viewModel.PieSeries;
        public ObservableCollection<LegendItem> LegendItems => _viewModel.LegendItems;

        public static readonly DependencyProperty UltraRareUnlockedProperty =
            DependencyProperty.Register(nameof(UltraRareUnlocked), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(1, OnDataChanged));

        public int UltraRareUnlocked
        {
            get => (int)GetValue(UltraRareUnlockedProperty);
            set => SetValue(UltraRareUnlockedProperty, value);
        }

        public static readonly DependencyProperty UltraRareTotalProperty =
            DependencyProperty.Register(nameof(UltraRareTotal), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(1, OnDataChanged));

        public int UltraRareTotal
        {
            get => (int)GetValue(UltraRareTotalProperty);
            set => SetValue(UltraRareTotalProperty, value);
        }

        public static readonly DependencyProperty RareUnlockedProperty =
            DependencyProperty.Register(nameof(RareUnlocked), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(1, OnDataChanged));

        public int RareUnlocked
        {
            get => (int)GetValue(RareUnlockedProperty);
            set => SetValue(RareUnlockedProperty, value);
        }

        public static readonly DependencyProperty RareTotalProperty =
            DependencyProperty.Register(nameof(RareTotal), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(1, OnDataChanged));

        public int RareTotal
        {
            get => (int)GetValue(RareTotalProperty);
            set => SetValue(RareTotalProperty, value);
        }

        public static readonly DependencyProperty UncommonUnlockedProperty =
            DependencyProperty.Register(nameof(UncommonUnlocked), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(0, OnDataChanged));

        public int UncommonUnlocked
        {
            get => (int)GetValue(UncommonUnlockedProperty);
            set => SetValue(UncommonUnlockedProperty, value);
        }

        public static readonly DependencyProperty UncommonTotalProperty =
            DependencyProperty.Register(nameof(UncommonTotal), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(1, OnDataChanged));

        public int UncommonTotal
        {
            get => (int)GetValue(UncommonTotalProperty);
            set => SetValue(UncommonTotalProperty, value);
        }

        public static readonly DependencyProperty CommonUnlockedProperty =
            DependencyProperty.Register(nameof(CommonUnlocked), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(0, OnDataChanged));

        public int CommonUnlocked
        {
            get => (int)GetValue(CommonUnlockedProperty);
            set => SetValue(CommonUnlockedProperty, value);
        }

        public static readonly DependencyProperty CommonTotalProperty =
            DependencyProperty.Register(nameof(CommonTotal), typeof(int),
                typeof(PreviewPieChartControl), new PropertyMetadata(2, OnDataChanged));

        public int CommonTotal
        {
            get => (int)GetValue(CommonTotalProperty);
            set => SetValue(CommonTotalProperty, value);
        }

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PreviewPieChartControl)d;
            control.UpdateData();
        }

        public PreviewPieChartControl()
        {
            InitializeComponent();
            UpdateData();
        }

        private void UpdateData()
        {
            _viewModel.SetRarityData(
                CommonUnlocked, UncommonUnlocked, RareUnlocked, UltraRareUnlocked,
                CalculateLocked(),
                CommonTotal, UncommonTotal, RareTotal, UltraRareTotal,
                "Common", "Uncommon", "Rare", "Ultra Rare", "Locked");
        }

        private int CalculateLocked()
        {
            return (UltraRareTotal - UltraRareUnlocked) +
                   (RareTotal - RareUnlocked) +
                   (UncommonTotal - UncommonUnlocked) +
                   (CommonTotal - CommonUnlocked);
        }
    }
}
