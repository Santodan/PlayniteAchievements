using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.ManageAchievements
{
    // The Tags column's icon size and the height its tags may take: as large as the row height
    // and column width allow, the same in every row. A row with more tags than fit shows "+N".
    public partial class ManageAchievementsEditorTab
    {
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

        private static readonly DependencyPropertyKey TagAreaHeightPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagAreaHeight), typeof(double),
                typeof(ManageAchievementsEditorTab), new PropertyMetadata(double.PositiveInfinity));

        /// <summary>Identifies the TagAreaHeight dependency property: the height a Tags cell's tags may take.</summary>
        public static readonly DependencyProperty TagAreaHeightProperty = TagAreaHeightPropertyKey.DependencyProperty;

        public double TagAreaHeight => (double)GetValue(TagAreaHeightProperty);

        private static readonly DependencyProperty TagLayoutKeyProperty =
            DependencyProperty.Register("TagLayoutKey", typeof(object),
                typeof(ManageAchievementsEditorTab), new PropertyMetadata(null, OnTagIconLayoutChanged));

        private static void OnTagIconLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((ManageAchievementsEditorTab)d).UpdateTagIconSize();

        private void ObserveTagIconLayout()
        {
            // A change to either column's width, or to the Icon column's visibility, re-sizes the icons.
            var layout = new MultiBinding { Converter = TagLayoutKey.Instance };
            layout.Bindings.Add(new Binding(nameof(DataGridColumn.ActualWidth)) { Source = EditorTagsColumn });
            layout.Bindings.Add(new Binding(nameof(DataGridColumn.ActualWidth)) { Source = EditorIconColumn });
            layout.Bindings.Add(new Binding(nameof(DataGridColumn.Visibility)) { Source = EditorIconColumn });
            SetBinding(TagLayoutKeyProperty, layout);
        }

        private void UpdateTagIconSize()
        {
            if (EditorTagsColumn.ActualWidth <= 0)
            {
                return;
            }

            var rowHeight = EditorIconColumn.Visibility == Visibility.Visible
                ? Math.Max(RowMinHeight, EditorIconColumn.ActualWidth)
                : RowMinHeight;

            // One pixel of slack on each axis absorbs layout rounding.
            var width = EditorTagsColumn.ActualWidth - TagAreaHorizontalInset - 1;
            var height = rowHeight - TagAreaVerticalInset - 1;
            var floor = TryFindResource("PlayAch.FontSize.Body") is double fontSize && fontSize > 0
                ? Math.Ceiling(fontSize)
                : Math.Ceiling(SystemFonts.MessageFontSize);
            SetValue(TagIconSizePropertyKey, Math.Max(floor, Math.Floor(Math.Min(width, height))));
            SetValue(TagAreaHeightPropertyKey, Math.Max(floor, height));
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
