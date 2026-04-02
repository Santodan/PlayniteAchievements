using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using LiveCharts;
using LiveCharts.Wpf;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using Playnite.SDK;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    public class PieChartViewModel : ObservableObject
    {
        /// <summary>
        /// Internal data class for raw pie slice processing before any small-slice mode is applied.
        /// </summary>
        private sealed class PieSliceInputData
        {
            public string Label { get; set; }
            public int Count { get; set; }
            public string IconKey { get; set; }
            public Color Color { get; set; }
            public string OriginalColorHex { get; set; }
            public int UnlockedCount { get; set; }
            public int TotalCount { get; set; }
            public bool IsLocked { get; set; }
        }

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
            public bool ShowRadialIcon { get; set; } = true;
            public bool SuppressRadialIconOnCollision { get; set; }
            public int PieTotalCount { get; set; }
            public string PrimaryMetricText { get; set; }
            public string SecondaryMetricText { get; set; }
        }

        public SeriesCollection PieSeries { get; } = new SeriesCollection();
        public ObservableCollection<LegendItem> LegendItems { get; } = new ObservableCollection<LegendItem>();

        private ObservableCollection<string> _highlightedLabels = new ObservableCollection<string>();
        private SidebarPieSmallSliceMode _smallSliceMode = SidebarPieSmallSliceMode.Round;
        private int _exactUnlockedCount;
        private int _exactTotalCount;
        private bool _alwaysShowSmallSliceIcons;
        private int _minimumSeriesCount;

        // Maps display labels to provider keys for provider pie chart slice click handling
        private readonly Dictionary<string, string> _labelToProviderKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public ObservableCollection<string> HighlightedLabels
        {
            get => _highlightedLabels;
            private set => SetValue(ref _highlightedLabels, value);
        }

        public SidebarPieSmallSliceMode SmallSliceMode
        {
            get => _smallSliceMode;
            set => SetValue(ref _smallSliceMode, value);
        }

        public int ExactUnlockedCount
        {
            get => _exactUnlockedCount;
            private set => SetValue(ref _exactUnlockedCount, value);
        }

        public int ExactTotalCount
        {
            get => _exactTotalCount;
            private set => SetValue(ref _exactTotalCount, value);
        }

        public bool AlwaysShowSmallSliceIcons
        {
            get => _alwaysShowSmallSliceIcons;
            set => SetValue(ref _alwaysShowSmallSliceIcons, value);
        }

        public int MinimumSeriesCount
        {
            get => _minimumSeriesCount;
            set => SetValue(ref _minimumSeriesCount, Math.Max(0, value));
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
            // Get the set of labels that actually exist in the current pie chart
            var existingLabels = new HashSet<string>(
                LegendItems.Select(li => li.Label),
                StringComparer.OrdinalIgnoreCase);

            var normalizedLabels = new HashSet<string>(
                (labels ?? Enumerable.Empty<string>())
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label.Trim())
                    .Where(label => existingLabels.Contains(label)), // Only include labels that exist in the pie chart
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
                UpdateExactCounts(0, 0);
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var incomplete = totalGames - completedGames;

            var dataPoints = new List<PieSliceInputData>
            {
                new PieSliceInputData
                {
                    Label = completedLabel,
                    Count = completedGames,
                    IconKey = "BadgeCompletedGame",
                    Color = Color.FromRgb(33, 150, 243),
                    OriginalColorHex = string.Empty,
                    UnlockedCount = completedGames,
                    TotalCount = completedGames,
                    IsLocked = false
                },
                new PieSliceInputData
                {
                    Label = incompleteLabel,
                    Count = incomplete,
                    IconKey = "BadgeLocked",
                    Color = LockedTransparent,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = incomplete,
                    TotalCount = incomplete,
                    IsLocked = true
                }
            };

            ApplySmallSliceMode(dataPoints);
        }

        /// <summary>
        /// Sets the pie chart data for Rarity distribution (Ultra Rare, Rare, Uncommon, Common, Locked).
        /// </summary>
        public void SetRarityData(
            int commonUnlocked, int uncommonUnlocked, int rareUnlocked, int ultraRareUnlocked, int locked,
            int commonTotal, int uncommonTotal, int rareTotal, int ultraRareTotal,
            string commonLabel, string uncommonLabel, string rareLabel, string ultraRareLabel, string lockedLabel)
        {
            var dataPoints = new List<PieSliceInputData>();

            if (ultraRareUnlocked > 0 || ultraRareTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = ultraRareLabel,
                    Count = ultraRareUnlocked,
                    IconKey = "BadgePlatinumHexagon",
                    Color = UltraRarePieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = ultraRareUnlocked,
                    TotalCount = ultraRareTotal,
                    IsLocked = false
                });
            }

            if (rareUnlocked > 0 || rareTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = rareLabel,
                    Count = rareUnlocked,
                    IconKey = "BadgeGoldPentagon",
                    Color = RarePieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = rareUnlocked,
                    TotalCount = rareTotal,
                    IsLocked = false
                });
            }

            if (uncommonUnlocked > 0 || uncommonTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = uncommonLabel,
                    Count = uncommonUnlocked,
                    IconKey = "BadgeSilverSquare",
                    Color = UncommonPieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = uncommonUnlocked,
                    TotalCount = uncommonTotal,
                    IsLocked = false
                });
            }

            if (commonUnlocked > 0 || commonTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = commonLabel,
                    Count = commonUnlocked,
                    IconKey = "BadgeBronzeTriangle",
                    Color = CommonPieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = commonUnlocked,
                    TotalCount = commonTotal,
                    IsLocked = false
                });
            }

            dataPoints.Add(new PieSliceInputData
            {
                Label = lockedLabel,
                Count = locked,
                IconKey = "BadgeLocked",
                Color = LockedTransparent,
                OriginalColorHex = string.Empty,
                UnlockedCount = locked,
                TotalCount = locked,
                IsLocked = true
            });

            ApplySmallSliceMode(dataPoints);
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
            _labelToProviderKey.Clear();

            if (unlockedByProvider == null || !unlockedByProvider.Any())
            {
                UpdateExactCounts(0, 0);
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var dataPoints = new List<PieSliceInputData>();

            foreach (var provider in unlockedByProvider.OrderByDescending(p => p.Value))
            {
                var providerKey = provider.Key;
                var unlockedCount = provider.Value;
                var totalCount = totalByProvider != null && totalByProvider.TryGetValue(providerKey, out var total)
                    ? total
                    : unlockedCount;

                var displayLabel = providerKey;
                if (providerDisplayNames != null &&
                    providerDisplayNames.TryGetValue(providerKey, out var displayName) &&
                    !string.IsNullOrWhiteSpace(displayName))
                {
                    displayLabel = displayName;
                }

                _labelToProviderKey[displayLabel] = providerKey;

                string colorHex = "#888888";
                string iconKey = string.Empty;
                if (providerLookup != null && providerLookup.TryGetValue(providerKey, out var metadata))
                {
                    colorHex = metadata.colorHex;
                    iconKey = metadata.iconKey ?? string.Empty;
                }

                Color color;
                if (ColorConverter.ConvertFromString(colorHex) is Color parsedColor)
                {
                    color = parsedColor;
                }
                else
                {
                    color = Colors.Gray;
                    colorHex = "#888888";
                }

                dataPoints.Add(new PieSliceInputData
                {
                    Label = displayLabel,
                    Count = unlockedCount,
                    IconKey = iconKey,
                    Color = color,
                    OriginalColorHex = colorHex,
                    UnlockedCount = unlockedCount,
                    TotalCount = totalCount,
                    IsLocked = false
                });
            }

            dataPoints.Add(new PieSliceInputData
            {
                Label = lockedLabel,
                Count = totalLocked,
                IconKey = "BadgeLocked",
                Color = LockedTransparent,
                OriginalColorHex = string.Empty,
                UnlockedCount = totalLocked,
                TotalCount = totalLocked,
                IsLocked = true
            });

            ApplySmallSliceMode(dataPoints);
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
                UpdateExactCounts(0, 0);
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var locked = Math.Max(0, totalTrophies - (platinumUnlocked + goldUnlocked + silverUnlocked + bronzeUnlocked));
            var dataPoints = new List<PieSliceInputData>();

            if (platinumUnlocked > 0 || platinumTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = platinumLabel,
                    Count = platinumUnlocked,
                    IconKey = "TrophyPlatinum",
                    Color = UltraRarePieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = platinumUnlocked,
                    TotalCount = platinumTotal,
                    IsLocked = false
                });
            }

            if (goldUnlocked > 0 || goldTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = goldLabel,
                    Count = goldUnlocked,
                    IconKey = "TrophyGold",
                    Color = RarePieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = goldUnlocked,
                    TotalCount = goldTotal,
                    IsLocked = false
                });
            }

            if (silverUnlocked > 0 || silverTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = silverLabel,
                    Count = silverUnlocked,
                    IconKey = "TrophySilver",
                    Color = UncommonPieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = silverUnlocked,
                    TotalCount = silverTotal,
                    IsLocked = false
                });
            }

            if (bronzeUnlocked > 0 || bronzeTotal > 0)
            {
                dataPoints.Add(new PieSliceInputData
                {
                    Label = bronzeLabel,
                    Count = bronzeUnlocked,
                    IconKey = "TrophyBronze",
                    Color = CommonPieColor,
                    OriginalColorHex = string.Empty,
                    UnlockedCount = bronzeUnlocked,
                    TotalCount = bronzeTotal,
                    IsLocked = false
                });
            }

            dataPoints.Add(new PieSliceInputData
            {
                Label = lockedLabel,
                Count = locked,
                IconKey = "BadgeLocked",
                Color = LockedTransparent,
                OriginalColorHex = string.Empty,
                UnlockedCount = locked,
                TotalCount = locked,
                IsLocked = true
            });

            ApplySmallSliceMode(dataPoints);
        }

        private void ApplySmallSliceMode(List<PieSliceInputData> dataPoints)
        {
            if (dataPoints == null || dataPoints.Count == 0)
            {
                UpdateExactCounts(0, 0);
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            dataPoints = dataPoints
                .Where(d => d != null && d.Count > 0)
                .ToList();

            UpdateExactCounts(
                dataPoints.Where(d => !d.IsLocked).Sum(d => d.Count),
                dataPoints.Sum(d => d.Count));

            if (dataPoints.Count == 0)
            {
                SynchronizePieChartAndLegend(new List<PieSliceData>());
                return;
            }

            var totalCount = dataPoints.Sum(d => d.Count);
            var minSlice = totalCount * 0.05;
            var slices = BuildSlicesForMode(dataPoints, minSlice, totalCount);

            SynchronizePieChartAndLegend(slices);
        }

        private List<PieSliceData> BuildSlicesForMode(IReadOnlyList<PieSliceInputData> dataPoints, double minSlice, int pieTotalCount)
        {
            switch (SmallSliceMode)
            {
                case SidebarPieSmallSliceMode.Exact:
                    return BuildExactSlices(
                        dataPoints,
                        minSlice,
                        pieTotalCount,
                        hideSmallIcons: false,
                        suppressOnCollision: !AlwaysShowSmallSliceIcons);
                case SidebarPieSmallSliceMode.Hide:
                    return BuildHiddenSlices(dataPoints, minSlice, pieTotalCount);
                case SidebarPieSmallSliceMode.Round:
                default:
                    if (TryBuildRoundedSlices(dataPoints, minSlice, pieTotalCount, out var roundedSlices))
                    {
                        return roundedSlices;
                    }

                    return BuildExactSlices(
                        dataPoints,
                        minSlice,
                        pieTotalCount,
                        hideSmallIcons: true,
                        suppressOnCollision: false);
            }
        }

        private List<PieSliceData> BuildExactSlices(
            IReadOnlyList<PieSliceInputData> dataPoints,
            double minSlice,
            int pieTotalCount,
            bool hideSmallIcons,
            bool suppressOnCollision)
        {
            var slices = new List<PieSliceData>(dataPoints.Count);
            for (int i = 0; i < dataPoints.Count; i++)
            {
                var dataPoint = dataPoints[i];
                var showRadialIcon = !hideSmallIcons || dataPoint.Count >= minSlice;
                slices.Add(CreateSliceData(
                    dataPoint,
                    dataPoint.Count,
                    pieTotalCount,
                    showRadialIcon,
                    suppressOnCollision && showRadialIcon));
            }

            return slices;
        }

        private List<PieSliceData> BuildHiddenSlices(IReadOnlyList<PieSliceInputData> dataPoints, double minSlice, int pieTotalCount)
        {
            var slices = new List<PieSliceData>(dataPoints.Count);
            for (int i = 0; i < dataPoints.Count; i++)
            {
                var dataPoint = dataPoints[i];
                if (dataPoint.Count < minSlice)
                {
                    continue;
                }

                slices.Add(CreateSliceData(dataPoint, dataPoint.Count, pieTotalCount, true, false));
            }

            return slices;
        }

        private bool TryBuildRoundedSlices(IReadOnlyList<PieSliceInputData> dataPoints, double minSlice, int pieTotalCount, out List<PieSliceData> slices)
        {
            var adjustments = new Dictionary<int, double>();
            var totalAdjustment = 0.0;
            var largestNonSmallIndex = -1;
            var largestNonSmallCount = 0;

            for (int i = 0; i < dataPoints.Count; i++)
            {
                var count = dataPoints[i].Count;
                if (count < minSlice)
                {
                    var adjustment = minSlice - count;
                    adjustments[i] = adjustment;
                    totalAdjustment += adjustment;
                    continue;
                }

                if (largestNonSmallIndex < 0 || count > largestNonSmallCount)
                {
                    largestNonSmallIndex = i;
                    largestNonSmallCount = count;
                }
            }

            if (adjustments.Count == 0)
            {
                slices = BuildExactSlices(
                    dataPoints,
                    minSlice,
                    pieTotalCount,
                    hideSmallIcons: false,
                    suppressOnCollision: false);
                return true;
            }

            if (largestNonSmallIndex < 0 || dataPoints[largestNonSmallIndex].Count - totalAdjustment <= 0)
            {
                slices = null;
                return false;
            }

            slices = new List<PieSliceData>(dataPoints.Count);
            for (int i = 0; i < dataPoints.Count; i++)
            {
                double chartValue;
                if (adjustments.ContainsKey(i))
                {
                    chartValue = minSlice;
                }
                else if (i == largestNonSmallIndex)
                {
                    chartValue = dataPoints[i].Count - totalAdjustment;
                }
                else
                {
                    chartValue = dataPoints[i].Count;
                }

                slices.Add(CreateSliceData(dataPoints[i], chartValue, pieTotalCount, true, false));
            }

            return true;
        }

        private PieSliceData CreateSliceData(
            PieSliceInputData dataPoint,
            double chartValue,
            int pieTotalCount,
            bool showRadialIcon,
            bool suppressOnCollision)
        {
            var primaryMetricText = FormatPrimaryMetricText(dataPoint);
            var secondaryMetricText = FormatSecondaryMetricText(dataPoint, pieTotalCount);

            return new PieSliceData
            {
                Label = dataPoint.Label,
                Count = dataPoint.Count,
                IconKey = dataPoint.IconKey,
                Color = dataPoint.Color,
                ColorHex = ResolveLegendColor(dataPoint),
                ChartValue = chartValue,
                UnlockedCount = dataPoint.UnlockedCount,
                TotalCount = dataPoint.TotalCount,
                IsLocked = dataPoint.IsLocked,
                ShowRadialIcon = showRadialIcon,
                SuppressRadialIconOnCollision = suppressOnCollision,
                PieTotalCount = pieTotalCount,
                PrimaryMetricText = primaryMetricText,
                SecondaryMetricText = secondaryMetricText
            };
        }

        private static string FormatPrimaryMetricText(PieSliceInputData dataPoint)
        {
            if (dataPoint == null)
            {
                return string.Empty;
            }

            if (dataPoint.IsLocked || IsCompletedGamesSlice(dataPoint.IconKey))
            {
                return dataPoint.Count.ToString();
            }

            return $"{dataPoint.UnlockedCount} / {dataPoint.TotalCount}";
        }

        private static string FormatSecondaryMetricText(PieSliceInputData dataPoint, int pieTotalCount)
        {
            if (dataPoint == null || pieTotalCount <= 0)
            {
                return string.Empty;
            }

            var totalLabel = ResourceProvider.GetString("LOCPlayAch_Column_Total") ?? "Total";
            var piePercent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(dataPoint.Count, pieTotalCount);

            if (dataPoint.IsLocked || IsCompletedGamesSlice(dataPoint.IconKey))
            {
                return $"{piePercent}% {totalLabel}";
            }

            var unlockedLabel = ResourceProvider.GetString("LOCPlayAch_Common_Unlocked") ?? "Unlocked";
            var categoryPercent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(dataPoint.UnlockedCount, dataPoint.TotalCount);
            return $"{categoryPercent}% {unlockedLabel} ({piePercent}% {totalLabel})";
        }

        private static bool IsCompletedGamesSlice(string iconKey)
        {
            return string.Equals(iconKey, "BadgeCompletedGame", StringComparison.Ordinal);
        }

        private static string ResolveLegendColor(PieSliceInputData dataPoint)
        {
            if (!string.IsNullOrEmpty(dataPoint.OriginalColorHex))
            {
                return dataPoint.OriginalColorHex;
            }

            if (dataPoint.IconKey == "BadgeLocked")
            {
                return LockedLegendColor;
            }

            return $"#{dataPoint.Color.R:X2}{dataPoint.Color.G:X2}{dataPoint.Color.B:X2}";
        }

        private void UpdateExactCounts(int unlockedCount, int totalCount)
        {
            ExactUnlockedCount = Math.Max(0, unlockedCount);
            ExactTotalCount = Math.Max(0, totalCount);
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
            var sliceCount = slices?.Count ?? 0;
            var targetCount = Math.Max(sliceCount, MinimumSeriesCount);
            for (int i = 0; i < targetCount; i++)
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

                var slice = i < sliceCount ? slices[i] : null;
                series.Title = slice?.Label ?? string.Empty;
                series.DataLabels = false;

                var existingBrush = series.Fill as SolidColorBrush;
                var sliceColor = slice?.Color ?? Colors.Transparent;
                if (existingBrush == null || existingBrush.Color != sliceColor)
                {
                    series.Fill = new SolidColorBrush(sliceColor);
                }

                var values = series.Values as ChartValues<PieSliceChartData>;
                if (values == null)
                {
                    values = new ChartValues<PieSliceChartData>();
                    series.Values = values;
                }

                var chartData = values.Count > 0
                    ? values[0]
                    : new PieSliceChartData();
                ApplySliceChartData(chartData, slice);

                if (values.Count == 0)
                {
                    values.Add(chartData);
                }
                else
                {
                    while (values.Count > 1)
                    {
                        values.RemoveAt(values.Count - 1);
                    }
                }
            }

            while (PieSeries.Count > targetCount)
            {
                PieSeries.RemoveAt(PieSeries.Count - 1);
            }
        }

        private static void ApplySliceChartData(PieSliceChartData chartData, PieSliceData slice)
        {
            chartData.Label = slice?.Label ?? string.Empty;
            chartData.Count = slice?.Count ?? 0;
            chartData.IconKey = slice?.IconKey ?? string.Empty;
            chartData.ColorHex = slice?.ColorHex ?? string.Empty;
            chartData.ChartValue = slice?.ChartValue ?? 0;
            chartData.UnlockedCount = slice?.UnlockedCount ?? 0;
            chartData.TotalCount = slice?.TotalCount ?? 0;
            chartData.IsLocked = slice?.IsLocked ?? false;
            chartData.ShowRadialIcon = slice?.ShowRadialIcon ?? false;
            chartData.SuppressRadialIconOnCollision = slice?.SuppressRadialIconOnCollision ?? false;
            chartData.PieTotalCount = slice?.PieTotalCount ?? 0;
            chartData.PrimaryMetricText = slice?.PrimaryMetricText ?? string.Empty;
            chartData.SecondaryMetricText = slice?.SecondaryMetricText ?? string.Empty;
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

