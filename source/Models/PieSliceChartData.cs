using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Data model for pie chart slices that carries icon metadata alongside the chart value.
    /// Used for custom tooltips that display provider/badge icons with counts.
    /// </summary>
    public class PieSliceChartData : ObservableObject
    {
        private string _label;
        public string Label
        {
            get => _label;
            set => SetValue(ref _label, value);
        }

        private int _count;
        public int Count
        {
            get => _count;
            set => SetValue(ref _count, value);
        }

        private string _iconKey;
        public string IconKey
        {
            get => _iconKey;
            set => SetValue(ref _iconKey, value);
        }

        private string _colorHex;
        public string ColorHex
        {
            get => _colorHex;
            set => SetValue(ref _colorHex, value);
        }

        private double _chartValue;
        public double ChartValue
        {
            get => _chartValue;
            set => SetValue(ref _chartValue, value);
        }

        /// <summary>
        /// Number of unlocked achievements in this category.
        /// For non-locked slices, same as Count.
        /// </summary>
        private int _unlockedCount;
        public int UnlockedCount
        {
            get => _unlockedCount;
            set => SetValue(ref _unlockedCount, value);
        }

        /// <summary>
        /// Total achievements in this category (including locked).
        /// </summary>
        private int _totalCount;
        public int TotalCount
        {
            get => _totalCount;
            set => SetValue(ref _totalCount, value);
        }

        /// <summary>
        /// True if this represents a locked/remaining slice.
        /// Locked slices display just the count, not "unlocked / total".
        /// </summary>
        private bool _isLocked;
        public bool IsLocked
        {
            get => _isLocked;
            set => SetValue(ref _isLocked, value);
        }

        /// <summary>
        /// True when the radial icon should be shown for this slice.
        /// </summary>
        private bool _showRadialIcon = true;
        public bool ShowRadialIcon
        {
            get => _showRadialIcon;
            set => SetValue(ref _showRadialIcon, value);
        }

        /// <summary>
        /// True when the control may hide this icon to resolve collisions with adjacent slice icons.
        /// Used by exact mode after actual icon positions are known.
        /// </summary>
        private bool _suppressRadialIconOnCollision;
        public bool SuppressRadialIconOnCollision
        {
            get => _suppressRadialIconOnCollision;
            set => SetValue(ref _suppressRadialIconOnCollision, value);
        }

        /// <summary>
        /// Exact total count for the full pie, used for tooltip percentages.
        /// </summary>
        private int _pieTotalCount;
        public int PieTotalCount
        {
            get => _pieTotalCount;
            set => SetValue(ref _pieTotalCount, value);
        }

        /// <summary>
        /// Primary tooltip metric text (count or unlocked/total).
        /// </summary>
        private string _primaryMetricText;
        public string PrimaryMetricText
        {
            get => _primaryMetricText;
            set => SetValue(ref _primaryMetricText, value);
        }

        /// <summary>
        /// Secondary tooltip metric text (percentages).
        /// </summary>
        private string _secondaryMetricText;
        public string SecondaryMetricText
        {
            get => _secondaryMetricText;
            set => SetValue(ref _secondaryMetricText, value);
        }
    }
}
