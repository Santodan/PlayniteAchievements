using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Static helper methods for column width normalization algorithms.
    /// Used by controls that need custom synchronization logic.
    /// </summary>
    public static class ColumnWidthNormalization
    {
        private const double MinimumColumnWidthRatio = 0.1;
        private const double EmergencyColumnMinimumWidth = 1d;

        private static readonly ConditionalWeakTable<DataGridColumn, ConfiguredColumnMinWidth> ConfiguredMinWidths =
            new ConditionalWeakTable<DataGridColumn, ConfiguredColumnMinWidth>();

        public static bool IsValidWidth(double width)
        {
            return !double.IsNaN(width) && !double.IsInfinity(width) && width > 0;
        }

        public static double RoundPixelWidth(double width)
        {
            if (!IsValidWidth(width))
            {
                return 0;
            }

            return Math.Max(1, Math.Round(width, MidpointRounding.AwayFromZero));
        }

        public static string GetColumnKey(DataGridColumn column)
        {
            if (column == null)
            {
                return null;
            }

            var key = ColumnVisibilityHelper.GetColumnKey(column);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }

            if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
            {
                return column.SortMemberPath;
            }

            return null;
        }

        public static double GetCurrentWidth(DataGridColumn column)
        {
            if (column == null)
            {
                return 0;
            }

            if (IsValidWidth(column.ActualWidth))
            {
                return column.ActualWidth;
            }

            var display = column.Width.DisplayValue;
            return IsValidWidth(display) ? display : 0;
        }

        public static double GetGridAvailableWidth(DataGrid grid)
        {
            var width = grid?.ActualWidth ?? 0;
            if (!IsValidWidth(width))
            {
                return 0;
            }

            var chrome = grid.BorderThickness.Left + grid.BorderThickness.Right + grid.Padding.Left + grid.Padding.Right;
            var availableWidth = Math.Max(0, width - chrome);
            var scrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(grid);
            if (scrollViewer != null && IsValidWidth(scrollViewer.ActualWidth))
            {
                availableWidth = Math.Min(availableWidth, scrollViewer.ActualWidth);
            }

            var viewportWidth = scrollViewer?.ViewportWidth ?? 0;
            if (IsValidWidth(viewportWidth))
            {
                return Math.Max(0, Math.Min(viewportWidth, availableWidth));
            }

            return Math.Max(0, availableWidth);
        }

        public static double GetContainerRelativeMinimumColumnWidth(double availableWidth)
        {
            if (!IsValidWidth(availableWidth))
            {
                return 1;
            }

            return RoundPixelWidth(availableWidth * MinimumColumnWidthRatio);
        }

        public static double ResolveResizableMinimumColumnWidth(
            IReadOnlyList<DataGridColumn> visibleColumns,
            double preferredMinimumWidth,
            double availableWidth)
        {
            if (!IsValidWidth(preferredMinimumWidth) ||
                !IsValidWidth(availableWidth) ||
                visibleColumns == null ||
                visibleColumns.Count == 0)
            {
                return Math.Max(1, preferredMinimumWidth);
            }

            var resizableColumns = visibleColumns
                .Where(column =>
                    column != null &&
                    column.CanUserResize &&
                    !string.IsNullOrWhiteSpace(GetColumnKey(column)))
                .ToList();
            if (resizableColumns.Count == 0)
            {
                return Math.Max(1, preferredMinimumWidth);
            }

            var fixedWidth = visibleColumns
                .Where(column => column == null || !column.CanUserResize || string.IsNullOrWhiteSpace(GetColumnKey(column)))
                .Sum(GetCurrentWidth);
            var availableForResizable = Math.Max(1, availableWidth - fixedWidth);
            var maxFittableMinimum = Math.Max(1, availableForResizable / resizableColumns.Count);
            return Math.Max(1, Math.Min(preferredMinimumWidth, maxFittableMinimum));
        }

        public static Dictionary<DataGridColumn, double> ApplyMinimumColumnWidth(
            IReadOnlyList<DataGridColumn> columns,
            double minimumColumnWidth)
        {
            var result = new Dictionary<DataGridColumn, double>();
            if (columns == null || !IsValidWidth(minimumColumnWidth))
            {
                return result;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                if (column == null)
                {
                    continue;
                }

                var resolvedMinWidth = ResolveColumnMinimumWidth(column, minimumColumnWidth);
                result[column] = resolvedMinWidth;

                if (Math.Abs(column.MinWidth - resolvedMinWidth) > 0.2)
                {
                    column.MinWidth = resolvedMinWidth;
                }

                if (!column.CanUserResize)
                {
                    var resolvedMaxWidth = ResolveFixedColumnMaximumWidth(column, resolvedMinWidth);
                    if (Math.Abs(column.MaxWidth - resolvedMaxWidth) > 0.2)
                    {
                        column.MaxWidth = resolvedMaxWidth;
                    }
                }
            }

            return result;
        }

        public static double ResolveColumnMinimumWidth(DataGridColumn column, double fallbackMinWidth)
        {
            if (column == null)
            {
                return fallbackMinWidth;
            }

            var configuredMinWidth = GetConfiguredMinimumWidth(column);
            if (configuredMinWidth.HasValue)
            {
                return configuredMinWidth.Value;
            }

            if (!column.CanUserResize)
            {
                var currentWidth = GetCurrentWidth(column);
                if (IsValidWidth(currentWidth))
                {
                    return currentWidth;
                }
            }

            return fallbackMinWidth;
        }

        private static double? GetConfiguredMinimumWidth(DataGridColumn column)
        {
            if (column == null)
            {
                return null;
            }

            if (!ConfiguredMinWidths.TryGetValue(column, out var stored))
            {
                var localValue = column.ReadLocalValue(DataGridColumn.MinWidthProperty);
                var configured = localValue is double width && IsValidWidth(width)
                    ? width
                    : double.NaN;

                stored = new ConfiguredColumnMinWidth { Value = configured };
                ConfiguredMinWidths.Add(column, stored);
            }

            return IsValidWidth(stored.Value) ? stored.Value : (double?)null;
        }

        private sealed class ConfiguredColumnMinWidth
        {
            public double Value { get; set; }
        }

        public static double ResolveFixedColumnMaximumWidth(DataGridColumn column, double fallbackWidth)
        {
            if (column != null && IsValidWidth(column.MaxWidth))
            {
                return column.MaxWidth;
            }

            var currentWidth = GetCurrentWidth(column);
            return IsValidWidth(currentWidth) ? currentWidth : fallbackWidth;
        }

        public static double GetColumnMinimumWidth(
            Dictionary<DataGridColumn, double> minimumColumnWidths,
            DataGridColumn column,
            double fallbackMinWidth)
        {
            if (minimumColumnWidths != null &&
                column != null &&
                minimumColumnWidths.TryGetValue(column, out var resolvedWidth) &&
                IsValidWidth(resolvedWidth))
            {
                return resolvedWidth;
            }

            return fallbackMinWidth;
        }

        public static double ResolveSeedWidth(
            string key,
            DataGridColumn column,
            IReadOnlyDictionary<string, double> preferredWidthsByKey,
            double fallbackMinimumWidth)
        {
            if (!string.IsNullOrWhiteSpace(key) &&
                preferredWidthsByKey != null &&
                preferredWidthsByKey.TryGetValue(key, out var preferredWidth) &&
                IsValidWidth(preferredWidth))
            {
                return preferredWidth;
            }

            var currentWidth = GetCurrentWidth(column);
            if (IsValidWidth(currentWidth))
            {
                return currentWidth;
            }

            return fallbackMinimumWidth;
        }

        public static void RescaleWidthsProportionally(
            IList<double> widths,
            IReadOnlyList<double> floorWidths,
            double targetWidth)
        {
            ColumnSizingPlanner.RescaleProportionally(widths, floorWidths, targetWidth);
        }

        public static List<int> BuildAbsorberOrder(
            IReadOnlyList<string> keys,
            string protectedColumnKey,
            string preferredAbsorberKey = null)
        {
            return ColumnSizingPlanner.BuildAbsorberOrder(keys, protectedColumnKey, preferredAbsorberKey);
        }

        public static bool KeysEqual(string a, string b)
        {
            return ColumnSizingPlanner.KeysEqual(a, b);
        }

        public static void DistributeDelta(
            IList<double> widths,
            IReadOnlyList<double> floorWidths,
            IReadOnlyList<string> keys,
            string protectedKey,
            string preferredAbsorberKey,
            double delta,
            double targetWidth)
        {
            var absorberOrder = BuildAbsorberOrder(keys, protectedKey, preferredAbsorberKey);
            if (absorberOrder.Count == 0)
            {
                absorberOrder.Add(keys.Count - 1);
            }

            if (delta > 0)
            {
                widths[absorberOrder[0]] += delta;
            }
            else
            {
                foreach (var index in absorberOrder)
                {
                    var capacity = widths[index] - floorWidths[index];
                    if (capacity <= 0)
                    {
                        continue;
                    }

                    var take = Math.Min(capacity, -delta);
                    widths[index] -= take;
                    delta += take;
                    if (delta >= -0.2)
                    {
                        break;
                    }
                }

                if (delta < -0.2)
                {
                    var fallback = absorberOrder[0];
                    var before = widths[fallback];
                    widths[fallback] = Math.Max(floorWidths[fallback], widths[fallback] + delta);
                    delta += widths[fallback] - before;
                }

                if (delta < -0.2)
                {
                    var protectedIndex = -1;
                    for (var i = 0; i < keys.Count; i++)
                    {
                        if (KeysEqual(keys[i], protectedKey))
                        {
                            protectedIndex = i;
                            break;
                        }
                    }

                    if (protectedIndex >= 0)
                    {
                        var capacity = widths[protectedIndex] - floorWidths[protectedIndex];
                        if (capacity > 0)
                        {
                            var take = Math.Min(capacity, -delta);
                            widths[protectedIndex] -= take;
                            delta += take;
                        }
                    }
                }

                if (delta < -0.2)
                {
                    RescaleWidthsProportionally(widths, floorWidths, targetWidth);
                }
            }
        }

        public static bool TryBuildNormalizedWidths(
            IReadOnlyList<string> keys,
            IReadOnlyList<double> seedWidths,
            IReadOnlyList<double> floorWidths,
            string protectedKey,
            bool rescaleAll,
            double targetWidth,
            out Dictionary<string, double> normalized)
        {
            return TryBuildNormalizedWidths(
                keys,
                seedWidths,
                floorWidths,
                protectedKey,
                preferredAbsorberKey: null,
                rescaleAll,
                targetWidth,
                out normalized);
        }

        public static bool TryBuildNormalizedWidths(
            IReadOnlyList<string> keys,
            IReadOnlyList<double> seedWidths,
            IReadOnlyList<double> floorWidths,
            string protectedKey,
            string preferredAbsorberKey,
            bool rescaleAll,
            double targetWidth,
            out Dictionary<string, double> normalized)
        {
            return ColumnSizingPlanner.TryPlan(
                keys,
                seedWidths,
                floorWidths,
                protectedKey,
                preferredAbsorberKey,
                rescaleAll,
                targetWidth,
                out normalized);
        }

        private static Dictionary<string, double> RoundWidthsToTarget(
            IReadOnlyList<string> keys,
            IReadOnlyList<double> widths,
            IReadOnlyList<double> floorWidths,
            string protectedKey,
            string preferredAbsorberKey,
            double targetWidth)
        {
            var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var roundedWidths = new List<double>(keys.Count);
            var roundedFloors = new List<double>(keys.Count);

            for (var i = 0; i < keys.Count; i++)
            {
                var roundedFloor = RoundPixelWidth(floorWidths[i]);
                roundedFloors.Add(roundedFloor);
                roundedWidths.Add(Math.Max(roundedFloor, RoundPixelWidth(widths[i])));
            }

            var roundedTarget = RoundPixelWidth(targetWidth);
            var delta = roundedTarget - roundedWidths.Sum();
            if (Math.Abs(delta) > 0.2)
            {
                DistributeRoundedDelta(roundedWidths, roundedFloors, keys, protectedKey, preferredAbsorberKey, delta);
            }

            for (var i = 0; i < keys.Count; i++)
            {
                normalized[keys[i]] = Math.Max(roundedFloors[i], roundedWidths[i]);
            }

            return normalized;
        }

        private static void DistributeRoundedDelta(
            IList<double> widths,
            IReadOnlyList<double> floorWidths,
            IReadOnlyList<string> keys,
            string protectedKey,
            string preferredAbsorberKey,
            double delta)
        {
            var absorberOrder = BuildAbsorberOrder(keys, protectedKey, preferredAbsorberKey);
            if (absorberOrder.Count == 0)
            {
                return;
            }

            if (delta > 0)
            {
                widths[absorberOrder[0]] += delta;
                return;
            }

            foreach (var index in absorberOrder)
            {
                var capacity = widths[index] - floorWidths[index];
                if (capacity <= 0)
                {
                    continue;
                }

                var take = Math.Min(capacity, -delta);
                widths[index] -= take;
                delta += take;
                if (delta >= -0.2)
                {
                    return;
                }
            }

            for (var i = 0; i < widths.Count && delta < -0.2; i++)
            {
                if (absorberOrder.Contains(i))
                {
                    continue;
                }

                var capacity = widths[i] - floorWidths[i];
                if (capacity <= 0)
                {
                    continue;
                }

                var take = Math.Min(capacity, -delta);
                widths[i] -= take;
                delta += take;
            }
        }

        public static bool TryBuildNormalizedWidths(
            DataGrid grid,
            string protectedKey,
            bool rescaleAll,
            IReadOnlyDictionary<string, double> preferredWidthsByKey,
            double fallbackAvailableWidth,
            out Dictionary<string, double> normalized)
        {
            return TryBuildNormalizedWidths(
                grid,
                protectedKey,
                preferredAbsorberKey: null,
                rescaleAll,
                preferredWidthsByKey,
                fallbackAvailableWidth,
                useEqualWidthForMissing: false,
                out normalized);
        }

        public static bool TryBuildNormalizedWidths(
            DataGrid grid,
            string protectedKey,
            string preferredAbsorberKey,
            bool rescaleAll,
            IReadOnlyDictionary<string, double> preferredWidthsByKey,
            double fallbackAvailableWidth,
            bool useEqualWidthForMissing,
            out Dictionary<string, double> normalized)
        {
            normalized = null;
            if (grid == null || grid.Columns == null || grid.Columns.Count == 0)
            {
                return false;
            }

            var visibleColumns = grid.Columns
                .Where(c => c != null && c.Visibility == Visibility.Visible)
                .OrderBy(c => c.DisplayIndex)
                .ToList();
            if (visibleColumns.Count == 0)
            {
                return false;
            }

            var availableWidth = GetGridAvailableWidth(grid);
            if (!IsValidWidth(availableWidth))
            {
                availableWidth = fallbackAvailableWidth;
            }
            if (!IsValidWidth(availableWidth))
            {
                return false;
            }

            var minimumWidth = ResolveResizableMinimumColumnWidth(visibleColumns,
                GetContainerRelativeMinimumColumnWidth(availableWidth), availableWidth);
            var minimumWidths = ApplyMinimumColumnWidth(visibleColumns, minimumWidth);

            var keyColumns = visibleColumns
                .Select(c => new
                {
                    Column = c,
                    Key = GetColumnKey(c),
                    MinWidth = GetColumnMinimumWidth(minimumWidths, c, minimumWidth),
                    IsResizable = c.CanUserResize
                })
                .Where(e => !string.IsNullOrWhiteSpace(e.Key) && e.IsResizable)
                .ToList();

            if (keyColumns.Count == 0)
            {
                return false;
            }

            var fixedWidth = visibleColumns
                .Where(c => string.IsNullOrWhiteSpace(GetColumnKey(c)) || !c.CanUserResize)
                .Sum(c => Math.Max(GetColumnMinimumWidth(minimumWidths, c, minimumWidth), GetCurrentWidth(c)));

            var targetWidth = Math.Max(0, availableWidth - fixedWidth);
            if (targetWidth <= 0)
            {
                return false;
            }

            var keys = keyColumns.Select(e => e.Key).ToList();
            var floorWidths = keyColumns.Select(e => e.MinWidth).ToList();
            ApplyEmergencyMinimumWidths(
                keyColumns.Select(e => e.Column).ToList(),
                floorWidths,
                targetWidth);

            var equalWidth = targetWidth / keyColumns.Count;
            var seedWidths = keyColumns
                .Select(e => ResolveNormalizationSeedWidth(
                    e.Key,
                    e.Column,
                    preferredWidthsByKey,
                    e.MinWidth,
                    useEqualWidthForMissing ? equalWidth : (double?)null))
                .ToList();

            return TryBuildNormalizedWidths(
                keys,
                seedWidths,
                floorWidths,
                protectedKey,
                preferredAbsorberKey,
                rescaleAll,
                targetWidth,
                out normalized);
        }

        private static void ApplyEmergencyMinimumWidths(
            IReadOnlyList<DataGridColumn> columns,
            IList<double> floorWidths,
            double targetWidth)
        {
            if (columns == null ||
                floorWidths == null ||
                columns.Count == 0 ||
                columns.Count != floorWidths.Count ||
                !IsValidWidth(targetWidth))
            {
                return;
            }

            var currentTotal = floorWidths.Sum();
            if (currentTotal <= targetWidth)
            {
                return;
            }

            var scale = targetWidth / currentTotal;
            for (var i = 0; i < floorWidths.Count; i++)
            {
                floorWidths[i] = Math.Max(EmergencyColumnMinimumWidth, floorWidths[i] * scale);
            }

            var excess = floorWidths.Sum() - targetWidth;
            for (var i = floorWidths.Count - 1; i >= 0 && excess > 0.2d; i--)
            {
                var capacity = floorWidths[i] - EmergencyColumnMinimumWidth;
                if (capacity <= 0)
                {
                    continue;
                }

                var take = Math.Min(capacity, excess);
                floorWidths[i] -= take;
                excess -= take;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                if (column == null)
                {
                    continue;
                }

                var width = Math.Max(EmergencyColumnMinimumWidth, floorWidths[i]);
                if (Math.Abs(column.MinWidth - width) > 0.2d)
                {
                    column.MinWidth = width;
                }
            }
        }

        public static bool TryBuildNormalizedWidths(
            DataGrid grid,
            string protectedKey,
            bool rescaleAll,
            IReadOnlyDictionary<string, double> preferredWidthsByKey,
            double fallbackAvailableWidth,
            bool useEqualWidthForMissing,
            out Dictionary<string, double> normalized)
        {
            return TryBuildNormalizedWidths(
                grid,
                protectedKey,
                preferredAbsorberKey: null,
                rescaleAll,
                preferredWidthsByKey,
                fallbackAvailableWidth,
                useEqualWidthForMissing,
                out normalized);
        }

        private static double ResolveNormalizationSeedWidth(
            string key,
            DataGridColumn column,
            IReadOnlyDictionary<string, double> preferredWidthsByKey,
            double fallbackMinimumWidth,
            double? missingPreferredWidth)
        {
            if (!string.IsNullOrWhiteSpace(key) &&
                preferredWidthsByKey != null &&
                preferredWidthsByKey.TryGetValue(key, out var preferredWidth) &&
                IsValidWidth(preferredWidth))
            {
                return preferredWidth;
            }

            if (missingPreferredWidth.HasValue && IsValidWidth(missingPreferredWidth.Value))
            {
                return missingPreferredWidth.Value;
            }

            return ResolveSeedWidth(key, column, preferredWidthsByKey, fallbackMinimumWidth);
        }

        public static void ApplyWidthsByKey(
            DataGrid grid,
            Dictionary<string, double> widthsByKey,
            ref bool isApplyingWidths)
        {
            if (grid == null || widthsByKey == null || widthsByKey.Count == 0)
            {
                return;
            }

            isApplyingWidths = true;
            try
            {
                foreach (var column in grid.Columns)
                {
                    if (column == null || !column.CanUserResize)
                    {
                        continue;
                    }

                    var key = GetColumnKey(column);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!widthsByKey.TryGetValue(key, out var width) || !IsValidWidth(width))
                    {
                        continue;
                    }

                    if (Math.Abs(GetCurrentWidth(column) - width) <= 0.2)
                    {
                        continue;
                    }

                    column.Width = new DataGridLength(RoundPixelWidth(width), DataGridLengthUnitType.Pixel);
                }
            }
            finally
            {
                isApplyingWidths = false;
            }
        }
    }
}
