using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.ManageAchievements
{
    // The height a Tags cell's tags may take. Rows size themselves, so it comes from what sets
    // their height: the icon cell, square to the Icon column's width, or the row style's minimum.
    public partial class ManageAchievementsEditorTab
    {
        // The tags sit in the cell's 6 top and bottom padding and the panel's 3 top and bottom margin.
        private const double RowMinHeight = 34;
        private const double TagAreaVerticalInset = 12 + 6;

        private static readonly DependencyPropertyKey TagAreaHeightPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagAreaHeight), typeof(double),
                typeof(ManageAchievementsEditorTab), new PropertyMetadata(RowMinHeight - TagAreaVerticalInset));

        /// <summary>Identifies the TagAreaHeight dependency property: the height a Tags cell's tags may take.</summary>
        public static readonly DependencyProperty TagAreaHeightProperty = TagAreaHeightPropertyKey.DependencyProperty;

        public double TagAreaHeight => (double)GetValue(TagAreaHeightProperty);

        private static readonly DependencyProperty TagLayoutKeyProperty =
            DependencyProperty.Register("TagLayoutKey", typeof(object),
                typeof(ManageAchievementsEditorTab), new PropertyMetadata(null, OnTagLayoutChanged));

        private static void OnTagLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((ManageAchievementsEditorTab)d).UpdateTagAreaHeight();

        private void ObserveTagIconLayout()
        {
            // A change to the Icon column's width or visibility changes the row height.
            var layout = new MultiBinding { Converter = TagLayoutKey.Instance };
            layout.Bindings.Add(new Binding(nameof(DataGridColumn.ActualWidth)) { Source = EditorIconColumn });
            layout.Bindings.Add(new Binding(nameof(DataGridColumn.Visibility)) { Source = EditorIconColumn });
            SetBinding(TagLayoutKeyProperty, layout);
        }

        private void UpdateTagAreaHeight()
        {
            var rowHeight = EditorIconColumn.Visibility == Visibility.Visible
                ? Math.Max(RowMinHeight, EditorIconColumn.ActualWidth)
                : RowMinHeight;
            SetValue(TagAreaHeightPropertyKey, rowHeight - TagAreaVerticalInset);
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
