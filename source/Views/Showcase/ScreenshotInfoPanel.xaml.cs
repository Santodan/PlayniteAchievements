using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Achievement details for the capture the Screenshot Slideshow is showing. The data all
    /// arrives through the DataContext; the only thing this owns is which of the two layouts the
    /// panel is in, since a tall side panel and a short wide strip want opposite arrangements.
    /// </summary>
    public partial class ScreenshotInfoPanel : UserControl
    {
        private const double WideHeaderMaxWidth = 300;
        private const double WideIconGutter = 66;

        private bool? _wide;

        public ScreenshotInfoPanel()
        {
            InitializeComponent();
            SetWideLayout(false);
        }

        /// <summary>
        /// Wide lays the panel out for a bottom strip: the icon and title in their own column, then
        /// the fields across two equal columns beside them. Otherwise it is one tall column for a
        /// side panel.
        /// </summary>
        public void SetWideLayout(bool wide)
        {
            if (_wide == wide)
            {
                return;
            }

            _wide = wide;
            RootGrid.ColumnDefinitions.Clear();
            RootGrid.RowDefinitions.Clear();

            if (wide)
            {
                // Star widths on the two field columns are what keeps their rows lined up; the
                // header takes only what it needs.
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Place(HeaderStack, 0, 0);
                Place(DetailsPrimary, 0, 1);
                Place(DetailsSecondary, 0, 2);

                HeaderStack.Orientation = Orientation.Horizontal;
                HeaderStack.MaxWidth = WideHeaderMaxWidth;
                TitleStack.Margin = new Thickness(10, 0, 0, 0);
                TitleStack.VerticalAlignment = VerticalAlignment.Top;
                // A horizontal StackPanel measures its children with unbounded width, so the
                // wrapping title and description need a width of their own or they are clipped by
                // the header's MaxWidth instead of wrapping inside it.
                TitleStack.MaxWidth = WideHeaderMaxWidth - WideIconGutter;
                DetailsPrimary.Margin = new Thickness(20, 0, 0, 0);
                DetailsSecondary.Margin = new Thickness(20, 0, 0, 0);
            }
            else
            {
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Place(HeaderStack, 0, 0);
                Place(DetailsPrimary, 1, 0);
                Place(DetailsSecondary, 2, 0);

                HeaderStack.Orientation = Orientation.Vertical;
                HeaderStack.MaxWidth = double.PositiveInfinity;
                TitleStack.Margin = new Thickness(0, 8, 0, 0);
                TitleStack.VerticalAlignment = VerticalAlignment.Stretch;
                TitleStack.MaxWidth = double.PositiveInfinity;
                // The two field stacks read as one continuous column here, so only the first
                // carries the gap under the title.
                DetailsPrimary.Margin = new Thickness(0, 8, 0, 0);
                DetailsSecondary.Margin = new Thickness(0);
            }
        }

        private static void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
        }
    }
}
