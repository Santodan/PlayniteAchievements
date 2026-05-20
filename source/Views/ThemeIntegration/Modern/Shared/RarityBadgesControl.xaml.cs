using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern.Shared
{
    /// <summary>
    /// Badge item for display in the RarityBadgesControl.
    /// </summary>
    public class BadgeItem : INotifyPropertyChanged
    {
        private bool _isVisible = true;
        private ImageSource _badgeIcon;

        public RarityTier Tier { get; set; }
        public ImageSource BadgeIcon
        {
            get => _badgeIcon;
            set
            {
                if (!ReferenceEquals(_badgeIcon, value))
                {
                    _badgeIcon = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeIcon)));
                }
            }
        }

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
        private static readonly Uri BadgeResourcesUri =
            new Uri("pack://application:,,,/PlayniteAchievements;component/Resources/RarityBadges.xaml", UriKind.Absolute);
        private static readonly Lazy<ResourceDictionary> FallbackBadgeResources =
            new Lazy<ResourceDictionary>(CreateFallbackBadgeResources);

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

        public static readonly DependencyProperty UseUniformRarityBadgesProperty =
            DependencyProperty.Register(nameof(UseUniformRarityBadges), typeof(bool), typeof(RarityBadgesControl),
                new PropertyMetadata(false, OnUseUniformRarityBadgesChanged));

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

        public bool UseUniformRarityBadges
        {
            get => (bool)GetValue(UseUniformRarityBadgesProperty);
            set => SetValue(UseUniformRarityBadgesProperty, value);
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

        private static void OnUseUniformRarityBadgesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (RarityBadgesControl)d;
            control.UpdateBadgeIcons();
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
            ultraRare = ultraRare ?? new AchievementRarityStats();
            rare = rare ?? new AchievementRarityStats();
            uncommon = uncommon ?? new AchievementRarityStats();
            common = common ?? new AchievementRarityStats();

            BadgeItems.Clear();

            var badges = new (RarityTier Tier, ImageSource Icon, int Count, int Total)[]
            {
                (RarityTier.UltraRare, GetBadgeIcon(RarityTier.UltraRare), GetCount(ultraRare), ultraRare.Total),
                (RarityTier.Rare, GetBadgeIcon(RarityTier.Rare), GetCount(rare), rare.Total),
                (RarityTier.Uncommon, GetBadgeIcon(RarityTier.Uncommon), GetCount(uncommon), uncommon.Total),
                (RarityTier.Common, GetBadgeIcon(RarityTier.Common), GetCount(common), common.Total)
            };

            foreach (var (tier, icon, count, total) in badges)
            {
                var item = new BadgeItem
                {
                    Tier = tier,
                    BadgeIcon = icon,
                    Count = count,
                    Total = total,
                    IsVisible = ShowZeroCounts || total > 0
                };
                BadgeItems.Add(item);
            }
        }

        private void UpdateBadgeIcons()
        {
            foreach (var item in BadgeItems)
            {
                item.BadgeIcon = GetBadgeIcon(item.Tier);
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

        private ImageSource GetBadgeIcon(RarityTier tier)
        {
            var resourceKey = tier.ToIconKey(UseUniformRarityBadges);
            return TryGetImageSource(Resources, resourceKey) ??
                   TryGetApplicationImageSource(resourceKey) ??
                   TryGetImageSource(FallbackBadgeResources.Value, resourceKey);
        }

        private static ImageSource TryGetImageSource(ResourceDictionary resources, string resourceKey)
        {
            if (resources == null || string.IsNullOrWhiteSpace(resourceKey))
            {
                return null;
            }

            try
            {
                return resources[resourceKey] as ImageSource;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource TryGetApplicationImageSource(string resourceKey)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return null;
            }

            try
            {
                return Application.Current?.TryFindResource(resourceKey) as ImageSource;
            }
            catch
            {
                return null;
            }
        }

        private static ResourceDictionary CreateFallbackBadgeResources()
        {
            try
            {
                return new ResourceDictionary
                {
                    Source = BadgeResourcesUri
                };
            }
            catch
            {
                return new ResourceDictionary();
            }
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


