using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop.Shared
{
    /// <summary>
    /// Badge item for display in the RarityBadgesControl.
    /// </summary>
    public class BadgeItem : INotifyPropertyChanged
    {
        private bool _isVisible = true;

        public ImageSource BadgeIcon { get; set; }
        public int Count { get; set; }
        public int Total { get; set; }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Horizontal stack of rarity badge icons with counts.
    /// Reusable component for displaying achievement rarity breakdown.
    /// </summary>
    public partial class RarityBadgesControl : UserControl
    {
        public ObservableCollection<BadgeItem> BadgeItems { get; } = new ObservableCollection<BadgeItem>();

        public static readonly DependencyProperty ShowZeroCountsProperty =
            DependencyProperty.Register(nameof(ShowZeroCounts), typeof(bool), typeof(RarityBadgesControl),
                new PropertyMetadata(false, OnShowZeroCountsChanged));

        public static readonly DependencyProperty BadgeSizeProperty =
            DependencyProperty.Register(nameof(BadgeSize), typeof(double), typeof(RarityBadgesControl),
                new PropertyMetadata(18.0));

        public static readonly DependencyProperty DisplayModeProperty =
            DependencyProperty.Register(nameof(DisplayMode), typeof(BadgesDisplayMode), typeof(RarityBadgesControl),
                new PropertyMetadata(BadgesDisplayMode.Unlocked));

        public bool ShowZeroCounts
        {
            get => (bool)GetValue(ShowZeroCountsProperty);
            set => SetValue(ShowZeroCountsProperty, value);
        }

        public double BadgeSize
        {
            get => (double)GetValue(BadgeSizeProperty);
            set => SetValue(BadgeSizeProperty, value);
        }

        public BadgesDisplayMode DisplayMode
        {
            get => (BadgesDisplayMode)GetValue(DisplayModeProperty);
            set => SetValue(DisplayModeProperty, value);
        }

        public RarityBadgesControl()
        {
            InitializeComponent();
        }

        private static void OnShowZeroCountsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (RarityBadgesControl)d;
            control.UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            foreach (var item in BadgeItems)
            {
                item.IsVisible = ShowZeroCounts || item.Total > 0;
            }
        }

        /// <summary>
        /// Updates badge items with rarity statistics from the parent control's data context.
        /// </summary>
        public void UpdateFromThemeData(Models.ThemeIntegration.ModernThemeBindings theme)
        {
            if (theme == null)
            {
                BadgeItems.Clear();
                return;
            }

            UpdateFromRarityStats(theme.UltraRare, theme.Rare, theme.Uncommon, theme.Common);
        }

        /// <summary>
        /// Updates badge items directly from rarity stats.
        /// </summary>
        public void UpdateFromRarityStats(
            Models.Achievements.AchievementRarityStats ultraRare,
            Models.Achievements.AchievementRarityStats rare,
            Models.Achievements.AchievementRarityStats uncommon,
            Models.Achievements.AchievementRarityStats common)
        {
            BadgeItems.Clear();

            var badges = new (ImageSource Icon, int Count, int Total)[]
            {
                ((ImageSource)Application.Current.FindResource("BadgePlatinumHexagon"), GetCount(ultraRare), ultraRare.Total),
                ((ImageSource)Application.Current.FindResource("BadgeGoldPentagon"), GetCount(rare), rare.Total),
                ((ImageSource)Application.Current.FindResource("BadgeSilverSquare"), GetCount(uncommon), uncommon.Total),
                ((ImageSource)Application.Current.FindResource("BadgeBronzeTriangle"), GetCount(common), common.Total)
            };

            foreach (var (icon, count, total) in badges)
            {
                var item = new BadgeItem
                {
                    BadgeIcon = icon,
                    Count = count,
                    Total = total,
                    IsVisible = ShowZeroCounts || total > 0
                };
                BadgeItems.Add(item);
            }
        }

        private int GetCount(AchievementRarityStats stats)
        {
            return DisplayMode switch
            {
                BadgesDisplayMode.Unlocked => stats.Unlocked,
                BadgesDisplayMode.Locked => stats.Locked,
                BadgesDisplayMode.Total => stats.Total,
                _ => stats.Unlocked
            };
        }
    }

    /// <summary>
    /// Specifies what count to display for each rarity badge.
    /// </summary>
    public enum BadgesDisplayMode
    {
        Unlocked,
        Locked,
        Total
    }
}

