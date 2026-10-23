using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Controls
{
    // The Tags column's icon size: one size for every row, as large as the column width and row
    // height let the row with the most crowded tags fit.
    public partial class AchievementDataGridControl
    {
        // AchievementCellStyle pads cells 10,6; the row style's MinHeight is 88.
        private const double TagCellHorizontalPadding = 20;
        private const double TagCellVerticalPadding = 12;
        private const double AutoRowMinHeight = 88;

        private static readonly DependencyPropertyKey TagIconSizePropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagIconSize), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(16.0));

        /// <summary>Identifies the TagIconSize dependency property: the Tags column's icon size.</summary>
        public static readonly DependencyProperty TagIconSizeProperty = TagIconSizePropertyKey.DependencyProperty;

        public double TagIconSize => (double)GetValue(TagIconSizeProperty);

        private static readonly DependencyProperty TagsColumnWidthProperty =
            DependencyProperty.Register("TagsColumnWidth", typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(0.0, OnTagIconLayoutChanged));

        private static readonly DependencyProperty TagsColumnVisibilityProperty =
            DependencyProperty.Register("TagsColumnVisibility", typeof(Visibility),
                typeof(AchievementDataGridControl), new PropertyMetadata(Visibility.Visible, OnTagIconLayoutChanged));

        private bool _tagIconFitQueued;

        private static void OnTagIconLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((AchievementDataGridControl)d).RequestTagIconFit();

        private void ObserveTagIconLayout()
        {
            SetBinding(TagsColumnWidthProperty, new Binding(nameof(DataGridColumn.ActualWidth)) { Source = TagsColumn });
            SetBinding(TagsColumnVisibilityProperty, new Binding(nameof(DataGridColumn.Visibility)) { Source = TagsColumn });
            ((INotifyCollectionChanged)AchievementsDataGrid.Items).CollectionChanged += (s, e) => RequestTagIconFit();
            AddHandler(TagChipsPanel.ChipsChangedEvent, new RoutedEventHandler((s, e) => RequestTagIconFit()));
        }

        // Column resizes and collection batches arrive many at a time; one pass after them all.
        private void RequestTagIconFit()
        {
            if (_tagIconFitQueued)
            {
                return;
            }

            _tagIconFitQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _tagIconFitQueued = false;
                UpdateTagIconSize();
            }), DispatcherPriority.Background);
        }

        private void UpdateTagIconSize()
        {
            var display = TagDisplay;
            if ((display & AchievementTagDisplay.Icons) == 0 ||
                TagsColumn.Visibility != Visibility.Visible ||
                TagsColumn.ActualWidth <= 0)
            {
                return;
            }

            SetValue(TagIconSizePropertyKey, TagChipsPanel.FitIconSize(
                AchievementsDataGrid.Items.OfType<AchievementDisplayItem>().Select(item => item.TagChips),
                display,
                TagsColumn.ActualWidth - TagCellHorizontalPadding,
                (ResolveFixedRowHeight() ?? AutoRowMinHeight) - TagCellVerticalPadding,
                this));
        }
    }
}
