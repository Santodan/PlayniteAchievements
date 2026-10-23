using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Controls;

namespace PlayniteAchievements.Views.ManageAchievements
{
    // The Tags column's icon size: one size for every row, as large as the column width and row
    // height let the row with the most crowded tags fit.
    public partial class ManageAchievementsEditorTab
    {
        // The editor draws tag icons in their colors; it has no tag display option of its own.
        internal const AchievementTagDisplay EditorTagDisplay = AchievementTagDisplay.Icons | AchievementTagDisplay.Colors;

        // A row is as tall as its icon cell, which is square to the Icon column's width, and at
        // least the row style's MinHeight. The tags sit in the cell's 10,6 padding, the button's
        // 4,0 margin and the panel's 11,3,21,3 margin, which clears the button's arrow.
        private const double RowMinHeight = 34;
        private const double TagAreaHorizontalInset = 20 + 8 + 32;
        private const double TagAreaVerticalInset = 12 + 6;

        private static readonly DependencyPropertyKey TagIconSizePropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagIconSize), typeof(double),
                typeof(ManageAchievementsEditorTab), new PropertyMetadata(16.0));

        /// <summary>Identifies the TagIconSize dependency property: the Tags column's icon size.</summary>
        public static readonly DependencyProperty TagIconSizeProperty = TagIconSizePropertyKey.DependencyProperty;

        public double TagIconSize => (double)GetValue(TagIconSizeProperty);

        private static readonly DependencyProperty TagLayoutWidthsProperty =
            DependencyProperty.Register("TagLayoutWidths", typeof(object),
                typeof(ManageAchievementsEditorTab), new PropertyMetadata(null, OnTagIconLayoutChanged));

        private bool _tagIconFitQueued;

        private static void OnTagIconLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((ManageAchievementsEditorTab)d).RequestTagIconFit();

        private void ObserveTagIconLayout()
        {
            // A change to either column's width or visibility re-runs the fit.
            var widths = new MultiBinding { Converter = TagLayoutKey.Instance };
            widths.Bindings.Add(new Binding(nameof(DataGridColumn.ActualWidth)) { Source = EditorTagsColumn });
            widths.Bindings.Add(new Binding(nameof(DataGridColumn.Visibility)) { Source = EditorTagsColumn });
            widths.Bindings.Add(new Binding(nameof(DataGridColumn.ActualWidth)) { Source = EditorIconColumn });
            widths.Bindings.Add(new Binding(nameof(DataGridColumn.Visibility)) { Source = EditorIconColumn });
            SetBinding(TagLayoutWidthsProperty, widths);

            ((INotifyCollectionChanged)CustomAchievementsGrid.Items).CollectionChanged += (s, e) => RequestTagIconFit();
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
            if (EditorTagsColumn.Visibility != Visibility.Visible || EditorTagsColumn.ActualWidth <= 0)
            {
                return;
            }

            var rowHeight = EditorIconColumn.Visibility == Visibility.Visible
                ? Math.Max(RowMinHeight, EditorIconColumn.ActualWidth)
                : RowMinHeight;
            SetValue(TagIconSizePropertyKey, TagChipsPanel.FitIconSize(
                CustomAchievementsGrid.Items.OfType<AchievementEditorRow>().Select(row => row.TagChips),
                EditorTagDisplay,
                EditorTagsColumn.ActualWidth - TagAreaHorizontalInset,
                rowHeight - TagAreaVerticalInset,
                this));
        }

        // A new value for every change of its inputs, so the property callback always runs.
        private sealed class TagLayoutKey : IMultiValueConverter
        {
            public static readonly TagLayoutKey Instance = new TagLayoutKey();

            public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                string.Join("|", values.Select(value => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
