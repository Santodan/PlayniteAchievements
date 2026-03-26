using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using LiveCharts;
using LiveCharts.Wpf;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Custom tooltip for pie chart slices that displays the provider/badge icon and count.
    /// </summary>
    public partial class PieChartTooltip : UserControl, IChartTooltip
    {
        public PieChartTooltip()
        {
            InitializeComponent();
            DataContext = this;
            Focusable = false;
            IsHitTestVisible = false;
            IsTabStop = false;
        }

        public TooltipSelectionMode? SelectionMode { get; set; } = TooltipSelectionMode.OnlySender;

        private TooltipData _data;
        public TooltipData Data
        {
            get => _data;
            set
            {
                _data = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
