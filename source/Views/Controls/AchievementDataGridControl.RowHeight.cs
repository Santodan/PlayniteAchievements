using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    // Every row has one height: the user's Row Height, or else the tallest any visible cell can be
    // under the current settings and column widths. It never depends on a row's data, so rows do
    // not change height as text wraps or art loads, and the virtualizing panel need not measure
    // every row to place the ones it shows. Text cells fit it, trimmed where they reach the bottom.
    public partial class AchievementDataGridControl
    {
        // AchievementCellStyle pads cells 6 top and bottom; rows are never shorter than 88.
        private const double TagCellVerticalPadding = 12;
        private const double AutoRowMinHeight = 88;
        private const double CoverAspect = 1.5;

        // The columns whose art grows with their width: the space beside and above the art (the
        // cell padding plus the template's margin), and whether the art is a 2:3 cover while the
        // grid shows covers.
        private static readonly Dictionary<string, (double Across, double Down, bool Cover)> ArtColumns =
            new Dictionary<string, (double Across, double Down, bool Cover)>(StringComparer.Ordinal)
            {
                ["Status"] = (4, 16, false),
                ["Icon"] = (16, 16, false),
                ["Avatar"] = (12, 4, false),
                ["Game"] = (4, 4, true),
                ["CategoryIcon"] = (4, 4, true)
            };

        private double _autoRowHeight = AutoRowMinHeight;

        // Bound to the art columns' widths and visibility and to UseCoverImages, so the auto height
        // follows each change of them and of nothing else.
        private static readonly DependencyProperty ArtLayoutProperty =
            DependencyProperty.Register("ArtLayout", typeof(string), typeof(AchievementDataGridControl),
                new PropertyMetadata(null, (d, e) => ((AchievementDataGridControl)d).UpdateAutoRowHeight()));

        private static readonly DependencyPropertyKey TagAreaHeightPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagAreaHeight), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(AutoRowMinHeight - TagCellVerticalPadding));

        /// <summary>Identifies the TagAreaHeight dependency property: the height a Tags cell's tags may take.</summary>
        public static readonly DependencyProperty TagAreaHeightProperty = TagAreaHeightPropertyKey.DependencyProperty;

        public double TagAreaHeight => (double)GetValue(TagAreaHeightProperty);

        private double RowHeight => ResolveFixedRowHeight() ?? _autoRowHeight;

        private void UpdateTagAreaHeight() =>
            SetValue(TagAreaHeightPropertyKey, RowHeight - TagCellVerticalPadding);

        private void TrackArtColumns()
        {
            var layout = new MultiBinding { Converter = JoinValues.Instance };
            layout.Bindings.Add(new Binding(nameof(UseCoverImages)) { Source = this });
            foreach (var column in AchievementsDataGrid.Columns.Where(column =>
                ArtColumns.ContainsKey(ColumnVisibilityHelper.GetColumnKey(column) ?? string.Empty)))
            {
                layout.Bindings.Add(new Binding(nameof(DataGridColumn.ActualWidth)) { Source = column });
                layout.Bindings.Add(new Binding(nameof(DataGridColumn.Visibility)) { Source = column });
            }

            SetBinding(ArtLayoutProperty, layout);
        }

        private void UpdateAutoRowHeight()
        {
            var height = AutoRowMinHeight;
            foreach (var column in AchievementsDataGrid.Columns)
            {
                if (column.Visibility == Visibility.Visible &&
                    ArtColumns.TryGetValue(ColumnVisibilityHelper.GetColumnKey(column) ?? string.Empty, out var art))
                {
                    var across = Math.Max(0, column.ActualWidth - art.Across);
                    height = Math.Max(height, across * (art.Cover && UseCoverImages ? CoverAspect : 1) + art.Down);
                }
            }

            height = Math.Ceiling(height);
            if (height == _autoRowHeight)
            {
                return;
            }

            _autoRowHeight = height;
            UpdateRealizedRowHeights();
            UpdateTagAreaHeight();
        }

        private void ApplyRowHeight(DataGridRow row)
        {
            if (row != null)
            {
                row.Height = RowHeight;
                row.MinHeight = RowHeight;
            }
        }

        private double? ResolveFixedRowHeight()
        {
            var height = FixedRowHeight;
            if (!height.HasValue ||
                double.IsNaN(height.Value) ||
                double.IsInfinity(height.Value) ||
                height.Value <= 0)
            {
                return null;
            }

            return Math.Max(PersistedSettings.MinimumGridRowHeight, height.Value);
        }

        private sealed class JoinValues : IMultiValueConverter
        {
            public static readonly JoinValues Instance = new JoinValues();

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
                string.Join("|", values);

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
