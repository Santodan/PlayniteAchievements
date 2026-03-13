using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using LiveCharts;
using LiveCharts.Wpf;
using PlayniteAchievements.Models;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    public class PieChartViewModel : ObservableObject
    {
        /// <summary>
        /// Internal data class for pie slice processing.
        /// Contains both the slice color (for rendering) and the legend color hex (for display).
        /// </summary>
        private sealed class PieSliceData
        {
            public string Label { get; set; }
            public int Count { get; set; }
            public string IconKey { get; set; }
            public Color Color { get; set; }
            public string ColorHex { get; set; }
            public double ChartValue { get; set; }
            public int UnlockedCount { get; set; }
            public int TotalCount { get; set; }
            public bool IsLocked { get; set; }
        }

        public SeriesCollection PieSeries { get; } = new SeriesCollection();
        public ObservableCollection<LegendItem> LegendItems { get; } = new ObservableCollection<LegendItem>();

        private ObservableCollection<string> _highlightedLabels = new ObservableCollection<string>();

        // Maps display labels to provider keys for provider pie chart slice click handling
        private Dictionary<string, string> _labelToProviderKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public ObservableCollection<string> HighlightedLabels
        {
            get => _highlightedLabels;
            private set => SetValue(ref _highlightedLabels, value);
        }

        // Consistent transparent locked color for all pie charts
        private static readonly Color LockedTransparent = Color.FromArgb(0, 102, 102, 102);
        private const string LockedLegendColor = "#666666";
        private static readonly Color UltraRarePieColor = Color.FromRgb(135, 206, 250);
        private static readonly Color RarePieColor = Color.FromRgb(255, 193, 7);
        private static readonly Color UncommonPieColor = Color.FromRgb(158, 158, 158);
        private static readonly Color CommonPieColor = Color.FromRgb(139, 69, 19);

        public void SetSelectedLabels(IEnumerable<string> labels)
        {
            var normalizedLabels = new HashSet<string>(
                (labels ?? Enumerable.Empty<string>())
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label.Trim()),
                StringComparer.OrdinalIgnoreCase);

            HighlightedLabels = new ObservableCollection<string>(
                normalizedLabels.OrderBy(label => label, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets the provider key from a display label.
        /// Used to translate pie chart slice labels back to stable provider keys for filtering.
        /// </summary>
        /// <param name="label">The display label from a clicked pie slice</param>
        /// <returns>The provider key, or null if not found</returns>
        public string GetProviderKeyFromLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            return _labelToProviderKey.TryGetValue(label, out var key) ? key : null;
        }

        /// <summary>
        /// Sets the pie chart data for Games completion (Completed vs Incomplete).
        /// </summary>
        public void SetGameData(int totalGames, int completedGames, string completedLabel, string incompleteLabel)
        {
            if (totalGames == 0)
            {
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var incomplete = totalGames - completedGames;

            var dataPoints = new List<(string Label, int Count, string IconKey, Color Color, string OriginalColorHex, int UnlockedCount, int TotalCount, bool IsLocked)>
            {
                (completedLabel, completedGames, "BadgeCompletedGame", Color.FromRgb(33, 150, 243), string.Empty, completedGames, completedGames, false)
            };

            dataPoints.Add((incompleteLabel, incomplete, "BadgeLocked", LockedTransparent, string.Empty, incomplete, incomplete, true));

            ApplyMinimumVisibilityRule(dataPoints);
        }

        /// <summary>
        /// Sets the pie chart data for Rarity distribution (Ultra Rare, Rare, Uncommon, Common, Locked).
        /// </summary>
        public void SetRarityData(
            int commonUnlocked, int uncommonUnlocked, int rareUnlocked, int ultraRareUnlocked, int locked,
            int commonTotal, int uncommonTotal, int rareTotal, int ultraRareTotal,
            string commonLabel, string uncommonLabel, string rareLabel, string ultraRareLabel, string lockedLabel)
        {
            var dataPoints = new List<(string Label, int Count, string IconKey, Color Color, string OriginalColorHex, int UnlockedCount, int TotalCount, bool IsLocked)>();

            if (ultraRareUnlocked > 0 || ultraRareTotal > 0)
            {
                dataPoints.Add((ultraRareLabel, ultraRareUnlocked, "BadgePlatinumHexagon", UltraRarePieColor, string.Empty, ultraRareUnlocked, ultraRareTotal, false));
            }

            if (rareUnlocked > 0 || rareTotal > 0)
            {
                dataPoints.Add((rareLabel, rareUnlocked, "BadgeGoldPentagon", RarePieColor, string.Empty, rareUnlocked, rareTotal, false));
            }

            if (uncommonUnlocked > 0 || uncommonTotal > 0)
            {
                dataPoints.Add((uncommonLabel, uncommonUnlocked, "BadgeSilverSquare", UncommonPieColor, string.Empty, uncommonUnlocked, uncommonTotal, false));
            }

            if (commonUnlocked > 0 || commonTotal > 0)
            {
                dataPoints.Add((commonLabel, commonUnlocked, "BadgeBronzeTriangle", CommonPieColor, string.Empty, commonUnlocked, commonTotal, false));
            }

            dataPoints.Add((lockedLabel, locked, "BadgeLocked", LockedTransparent, string.Empty, locked, locked, true));

            ApplyMinimumVisibilityRule(dataPoints);
        }

        /// <summary>
        /// Sets the pie chart data for Provider distribution.
        /// </summary>
        /// <param name="unlockedByProvider">Dictionary of provider key to unlocked count</param>
        /// <param name="totalByProvider">Dictionary of provider key to total count (including locked)</param>
        /// <param name="totalLocked">Total locked achievements</param>
        /// <param name="lockedLabel">Localized label for locked achievements</param>
        /// <param name="providerLookup">Dictionary of provider key to (iconKey, colorHex) tuple</param>
        /// <param name="providerDisplayNames">Dictionary of provider key to localized display name</param>
        public void SetProviderData(
            Dictionary<string, int> unlockedByProvider,
            Dictionary<string, int> totalByProvider,
            int totalLocked,
            string lockedLabel,
            Dictionary<string, (string iconKey, string colorHex)> providerLookup,
            Dictionary<string, string> providerDisplayNames = null)
        {
            // Clear previous mappings
            _labelToProviderKey.Clear();

            if (unlockedByProvider == null || !unlockedByProvider.Any())
            {
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var dataPoints = new List<(string Label, int Count, string IconKey, Color Color, string OriginalColorHex, int UnlockedCount, int TotalCount, bool IsLocked)>();

            // Sort providers by count descending
            foreach (var provider in unlockedByProvider.OrderByDescending(p => p.Value))
            {
                var providerKey = provider.Key;
                var unlockedCount = provider.Value;
                var totalCount = totalByProvider != null && totalByProvider.TryGetValue(providerKey, out var total) ? total : unlockedCount;

                // Get display name for the provider key
                var displayLabel = providerKey;
                if (providerDisplayNames != null && providerDisplayNames.TryGetValue(providerKey, out var displayName) && !string.IsNullOrWhiteSpace(displayName))
                {
                    displayLabel = displayName;
                }

                // Store mapping from display label to provider key for slice click handling
                _labelToProviderKey[displayLabel] = providerKey;

                // Look up provider icon and color
                string colorHex = "#888888";
                string iconKey = string.Empty;
                if (providerLookup != null && providerLookup.TryGetValue(providerKey, out var metadata))
                {
                    colorHex = metadata.colorHex;
                    iconKey = metadata.iconKey ?? string.Empty;
                }

                // Parse color hex to Color
                if (ColorConverter.ConvertFromString(colorHex) is Color color)
                {
                    dataPoints.Add((displayLabel, unlockedCount, iconKey, color, colorHex, unlockedCount, totalCount, false));
                }
                else
                {
                    dataPoints.Add((displayLabel, unlockedCount, iconKey, Colors.Gray, "#888888", unlockedCount, totalCount, false));
                }
            }

            dataPoints.Add((lockedLabel, totalLocked, "BadgeLocked", LockedTransparent, string.Empty, totalLocked, totalLocked, true));

            ApplyMinimumVisibilityRule(dataPoints);
        }

        /// <summary>
        /// Sets the pie chart data for trophy distribution (Platinum, Gold, Silver, Bronze, Locked).
        /// </summary>
        public void SetTrophyData(
            int platinumUnlocked,
            int goldUnlocked,
            int silverUnlocked,
            int bronzeUnlocked,
            int platinumTotal,
            int goldTotal,
            int silverTotal,
            int bronzeTotal,
            string platinumLabel,
            string goldLabel,
            string silverLabel,
            string bronzeLabel,
            string lockedLabel)
        {
            var totalTrophies = platinumTotal + goldTotal + silverTotal + bronzeTotal;
            if (totalTrophies <= 0)
            {
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var locked = Math.Max(0, totalTrophies - (platinumUnlocked + goldUnlocked + silverUnlocked + bronzeUnlocked));
            var dataPoints = new List<(string Label, int Count, string IconKey, Color Color, string OriginalColorHex, int UnlockedCount, int TotalCount, bool IsLocked)>();

            if (platinumUnlocked > 0 || platinumTotal > 0)
            {
                dataPoints.Add((platinumLabel, platinumUnlocked, "TrophyPlatinum", UltraRarePieColor, string.Empty, platinumUnlocked, platinumTotal, false));
            }

            if (goldUnlocked > 0 || goldTotal > 0)
            {
                dataPoints.Add((goldLabel, goldUnlocked, "TrophyGold", RarePieColor, string.Empty, goldUnlocked, goldTotal, false));
            }

            if (silverUnlocked > 0 || silverTotal > 0)
            {
                dataPoints.Add((silverLabel, silverUnlocked, "TrophySilver", UncommonPieColor, string.Empty, silverUnlocked, silverTotal, false));
            }

            if (bronzeUnlocked > 0 || bronzeTotal > 0)
            {
                dataPoints.Add((bronzeLabel, bronzeUnlocked, "TrophyBronze", CommonPieColor, string.Empty, bronzeUnlocked, bronzeTotal, false));
            }

            dataPoints.Add((lockedLabel, locked, "BadgeLocked", LockedTransparent, string.Empty, locked, locked, true));

            ApplyMinimumVisibilityRule(dataPoints);
        }

        private void ApplyMinimumVisibilityRule(List<(string Label, int Count, string IconKey, Color Color, string OriginalColorHex, int UnlockedCount, int TotalCount, bool IsLocked)> dataPoints)
        {
            if (dataPoints == null || dataPoints.Count == 0)
            {
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            // Filter out locked slices with zero count
            dataPoints = dataPoints
                .Where(d => !d.IsLocked || d.Count > 0)
                .ToList();

            if (dataPoints.Count == 0)
            {
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var totalCount = dataPoints.Sum(d => d.Count);
            var minSlice = totalCount * 0.05;
            var adjustments = new Dictionary<int, double>();
            var totalAdjustment = 0.0;

            // First pass: identify slices below 5% and calculate adjustments
            for (int i = 0; i < dataPoints.Count; i++)
            {
                if (dataPoints[i].Count < minSlice && dataPoints[i].Count > 0)
                {
                    var adjustment = minSlice - dataPoints[i].Count;
                    adjustments[i] = adjustment;
                    totalAdjustment += adjustment;
                }
            }

            // Find the largest slice to subtract from.
            int largestSliceIndex = dataPoints
                .Select((d, i) => (d.Count, i))
                .OrderByDescending(x => x.Item1)
                .First().Item2;

            var slices = new List<PieSliceData>(dataPoints.Count);
            for (int i = 0; i < dataPoints.Count; i++)
            {
                double chartValue;
                if (adjustments.ContainsKey(i))
                {
                    // Small slice: bump to 5%
                    chartValue = minSlice;
                }
                else if (i == largestSliceIndex && totalAdjustment > 0)
                {
                    // Largest slice: subtract the total adjustment
                    chartValue = dataPoints[i].Count - totalAdjustment;
                }
                else
                {
                    // Normal slice: use actual value
                    chartValue = dataPoints[i].Count;
                }

                // Determine legend color: use OriginalColorHex for providers, otherwise generate from Color
                string legendColor;
                if (!string.IsNullOrEmpty(dataPoints[i].OriginalColorHex))
                {
                    // Use original color hex from provider (e.g., "#B0B0B0")
                    legendColor = dataPoints[i].OriginalColorHex;
                }
                else if (dataPoints[i].IconKey == "BadgeLocked")
                {
                    // Locked items use consistent color
                    legendColor = LockedLegendColor;
                }
                else
                {
                    // Generate from Color (for badges)
                    legendColor = $"#{dataPoints[i].Color.R:X2}{dataPoints[i].Color.G:X2}{dataPoints[i].Color.B:X2}";
                }

                slices.Add(new PieSliceData
                {
                    Label = dataPoints[i].Label,
                    Count = dataPoints[i].Count,
                    IconKey = dataPoints[i].IconKey,
                    Color = dataPoints[i].Color,
                    ColorHex = legendColor,
                    ChartValue = chartValue,
                    UnlockedCount = dataPoints[i].UnlockedCount,
                    TotalCount = dataPoints[i].TotalCount,
                    IsLocked = dataPoints[i].IsLocked
                });
            }

            SynchronizePieChartAndLegend(slices);
        }

        private void SynchronizePieChartAndLegend(IReadOnlyList<PieSliceData> slices)
        {
            // Update legend first, then pie series.
            // This ensures when CalculatePositions fires after PieSeries changes,
            // LegendItems already contains matching data.
            SynchronizeLegendItems(slices);
            SynchronizePieSeries(slices);
        }

        private void SynchronizePieSeries(IReadOnlyList<PieSliceData> slices)
        {
            for (int i = 0; i < slices.Count; i++)
            {
                LiveCharts.Wpf.PieSeries series = null;
                if (i < PieSeries.Count)
                {
                    series = PieSeries[i] as LiveCharts.Wpf.PieSeries;
                }

                if (series == null)
                {
                    series = new LiveCharts.Wpf.PieSeries();
                    if (i < PieSeries.Count)
                    {
                        PieSeries[i] = series;
                    }
                    else
                    {
                        PieSeries.Add(series);
                    }
                }

                var slice = slices[i];
                series.Title = slice.Label;
                series.DataLabels = false;

                var existingBrush = series.Fill as SolidColorBrush;
                if (existingBrush == null || existingBrush.Color != slice.Color)
                {
                    series.Fill = new SolidColorBrush(slice.Color);
                }

                var values = series.Values as ChartValues<PieSliceChartData>;
                if (values == null)
                {
                    values = new ChartValues<PieSliceChartData>();
                    series.Values = values;
                }

                var chartData = new PieSliceChartData
                {
                    Label = slice.Label,
                    Count = slice.Count,
                    IconKey = slice.IconKey,
                    ColorHex = slice.ColorHex,
                    ChartValue = slice.ChartValue,
                    UnlockedCount = slice.UnlockedCount,
                    TotalCount = slice.TotalCount,
                    IsLocked = slice.IsLocked
                };

                if (values.Count == 0)
                {
                    values.Add(chartData);
                }
                else
                {
                    values[0] = chartData;
                    while (values.Count > 1)
                    {
                        values.RemoveAt(values.Count - 1);
                    }
                }
            }

            while (PieSeries.Count > slices.Count)
            {
                PieSeries.RemoveAt(PieSeries.Count - 1);
            }
        }

        private void SynchronizeLegendItems(IReadOnlyList<PieSliceData> slices)
        {
            for (int i = 0; i < slices.Count; i++)
            {
                var isNew = i >= LegendItems.Count;
                var item = isNew ? new LegendItem() : LegendItems[i];

                var slice = slices[i];
                item.Label = slice.Label;
                item.Count = slice.Count;
                item.IconKey = slice.IconKey;
                item.ColorHex = slice.ColorHex;

                if (isNew)
                {
                    LegendItems.Add(item);
                }
            }

            while (LegendItems.Count > slices.Count)
            {
                LegendItems.RemoveAt(LegendItems.Count - 1);
            }
        }

    }
}
