using System;
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
        private const string DetailColumnSizeGroup = "InfoDetailColumn";
        private const double WideHeaderMaxWidth = 300;
        private const double WideHeaderWidthShare = 0.35;
        private const double WideIconGutter = 66;
        private const double WideColumnGutter = 20;
        private const double WideColumnMinWidth = 120;

        private bool? _wide;

        public ScreenshotInfoPanel()
        {
            InitializeComponent();
            SetWideLayout(false);
            SizeChanged += OnSizeChanged;
        }

        /// <summary>
        /// Wide lays the panel out for a bottom strip: the icon and title in their own column, then
        /// the fields across two equal columns beside them, the whole block centred under the
        /// image. Otherwise it is one tall column for a side panel.
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
                // Auto rather than star: star columns stretch to fill the strip, which pins the
                // block to the left and defeats centring. Sharing a size group keeps the two
                // field columns equal to each other while both still size to their content.
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = GridLength.Auto,
                    SharedSizeGroup = DetailColumnSizeGroup
                });
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = GridLength.Auto,
                    SharedSizeGroup = DetailColumnSizeGroup
                });
                RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Place(HeaderStack, 0, 0);
                Place(DetailsPrimary, 0, 1);
                Place(DetailsSecondary, 0, 2);

                RootGrid.HorizontalAlignment = HorizontalAlignment.Center;
                HeaderStack.Orientation = Orientation.Horizontal;
                TitleStack.Margin = new Thickness(10, 0, 0, 0);
                TitleStack.VerticalAlignment = VerticalAlignment.Top;
                DetailsPrimary.Margin = new Thickness(WideColumnGutter, 0, 0, 0);
                DetailsSecondary.Margin = new Thickness(WideColumnGutter, 0, 0, 0);
                ApplyWideWidthCaps();
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

                RootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                HeaderStack.Orientation = Orientation.Vertical;
                TitleStack.Margin = new Thickness(0, 8, 0, 0);
                TitleStack.VerticalAlignment = VerticalAlignment.Stretch;
                // The two field stacks read as one continuous column here, so only the first
                // carries the gap under the title.
                DetailsPrimary.Margin = new Thickness(0, 8, 0, 0);
                DetailsSecondary.Margin = new Thickness(0);

                // The panel's own width bounds the text in this layout, so nothing needs capping.
                HeaderStack.MaxWidth = double.PositiveInfinity;
                TitleStack.MaxWidth = double.PositiveInfinity;
                DetailsPrimary.MaxWidth = double.PositiveInfinity;
                DetailsSecondary.MaxWidth = double.PositiveInfinity;
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_wide == true && e.WidthChanged)
            {
                ApplyWideWidthCaps();
            }
        }

        /// <summary>
        /// Bounds each column of the strip layout against the width actually available. Auto
        /// columns take whatever their content asks for, so without a cap a long game name would
        /// push the block past the widget instead of wrapping inside it. The panel stretches to the
        /// strip, so its own width is the budget and capping cannot feed back into it.
        /// </summary>
        private void ApplyWideWidthCaps()
        {
            var padding = DetailsScroll.Padding;
            var available = ActualWidth - padding.Left - padding.Right;
            if (available <= 0)
            {
                return;
            }

            var headerMax = Math.Min(WideHeaderMaxWidth, available * WideHeaderWidthShare);
            var columnMax = Math.Max(
                WideColumnMinWidth,
                (available - headerMax - (WideColumnGutter * 2)) / 2);

            HeaderStack.MaxWidth = headerMax;
            TitleStack.MaxWidth = Math.Max(WideColumnMinWidth, headerMax - WideIconGutter);
            DetailsPrimary.MaxWidth = columnMax;
            DetailsSecondary.MaxWidth = columnMax;
        }

        private static void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
        }
    }
}
