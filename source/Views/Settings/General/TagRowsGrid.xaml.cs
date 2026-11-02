using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PlayniteAchievements.Models.Tagging;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// A grid of tag rows: a label column and four tag columns. Set the headers to name the
    /// layers; leave them empty for rows whose tags name themselves.
    /// </summary>
    public partial class TagRowsGrid : UserControl
    {
        public static readonly DependencyProperty FirstHeaderProperty = HeaderProperty(nameof(FirstHeader), grid => grid.FirstColumn);
        public static readonly DependencyProperty SecondHeaderProperty = HeaderProperty(nameof(SecondHeader), grid => grid.SecondColumn);
        public static readonly DependencyProperty ThirdHeaderProperty = HeaderProperty(nameof(ThirdHeader), grid => grid.ThirdColumn);
        public static readonly DependencyProperty FourthHeaderProperty = HeaderProperty(nameof(FourthHeader), grid => grid.FourthColumn);

        public static readonly DependencyProperty ShowHeadersProperty = DependencyProperty.Register(
            nameof(ShowHeaders),
            typeof(bool),
            typeof(TagRowsGrid),
            new PropertyMetadata(true, (d, e) =>
            {
                if (d is TagRowsGrid grid)
                {
                    grid.RowsGrid.HeadersVisibility = (bool)e.NewValue ? DataGridHeadersVisibility.Column : DataGridHeadersVisibility.None;
                }
            }));

        public static readonly DependencyProperty TagColumnsProperty = DependencyProperty.Register(
            nameof(TagColumns),
            typeof(int),
            typeof(TagRowsGrid),
            new PropertyMetadata(4, (d, e) =>
            {
                if (d is TagRowsGrid grid)
                {
                    var count = (int)e.NewValue;
                    grid.ThirdColumn.Visibility = count >= 3 ? Visibility.Visible : Visibility.Collapsed;
                    grid.FourthColumn.Visibility = count >= 4 ? Visibility.Visible : Visibility.Collapsed;
                }
            }));

        public TagRowsGrid()
        {
            InitializeComponent();
        }

        /// <summary>
        /// How many tag columns the rows use; the rest are hidden rather than shown as dashes, for
        /// lists whose rows never fill them, such as the status pairs.
        /// </summary>
        public int TagColumns
        {
            get => (int)GetValue(TagColumnsProperty);
            set => SetValue(TagColumnsProperty, value);
        }

        /// <summary>False for rows whose tags name themselves, such as the status pairs.</summary>
        public bool ShowHeaders
        {
            get => (bool)GetValue(ShowHeadersProperty);
            set => SetValue(ShowHeadersProperty, value);
        }

        public string FirstHeader
        {
            get => (string)GetValue(FirstHeaderProperty);
            set => SetValue(FirstHeaderProperty, value);
        }

        public string SecondHeader
        {
            get => (string)GetValue(SecondHeaderProperty);
            set => SetValue(SecondHeaderProperty, value);
        }

        public string ThirdHeader
        {
            get => (string)GetValue(ThirdHeaderProperty);
            set => SetValue(ThirdHeaderProperty, value);
        }

        public string FourthHeader
        {
            get => (string)GetValue(FourthHeaderProperty);
            set => SetValue(FourthHeaderProperty, value);
        }

        private static DependencyProperty HeaderProperty(string name, System.Func<TagRowsGrid, DataGridColumn> column)
        {
            return DependencyProperty.Register(
                name,
                typeof(string),
                typeof(TagRowsGrid),
                new PropertyMetadata(null, (d, e) =>
                {
                    if (d is TagRowsGrid grid)
                    {
                        column(grid).Header = e.NewValue;
                    }
                }));
        }

        /// <summary>The toggle is bound one-way, so its click sets the tag's enabled state.</summary>
        private void TagEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle && toggle.DataContext is TagConfig config)
            {
                config.IsEnabled = toggle.IsChecked == true;
            }
        }

        /// <summary>
        /// The grid scrolls only sideways, but its ScrollViewer still marks every wheel event
        /// handled; the wheel is passed on so the settings page keeps scrolling.
        /// </summary>
        private void RowsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || !(VisualTreeHelper.GetParent(this) is UIElement parent))
            {
                return;
            }

            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = this
            });
        }
    }
}
