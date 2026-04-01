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
        private static readonly Uri BadgeResourcesUri =
            new Uri("pack://application:,,,/PlayniteAchievements;component/Resources/RarityBadges.xaml", UriKind.Absolute);
        private static readonly object BadgeResourcesLock = new object();

        private static ResourceDictionary _fallbackBadgeResources;

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
            ultraRare = ultraRare ?? new AchievementRarityStats();
            rare = rare ?? new AchievementRarityStats();
            uncommon = uncommon ?? new AchievementRarityStats();
            common = common ?? new AchievementRarityStats();

            BadgeItems.Clear();

            var badges = new (ImageSource Icon, int Count, int Total)[]
            {
                (GetBadgeIcon("BadgePlatinumHexagon"), GetCount(ultraRare), ultraRare.Total),
                (GetBadgeIcon("BadgeGoldPentagon"), GetCount(rare), rare.Total),
                (GetBadgeIcon("BadgeSilverSquare"), GetCount(uncommon), uncommon.Total),
                (GetBadgeIcon("BadgeBronzeTriangle"), GetCount(common), common.Total)
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

        private ImageSource GetBadgeIcon(string resourceKey)
        {
            return TryGetImageSource(Resources, resourceKey) ??
                   TryGetImageSource(Application.Current?.Resources, resourceKey) ??
                   TryGetImageSource(GetFallbackBadgeResources(), resourceKey);
        }

        private static ImageSource TryGetImageSource(ResourceDictionary resources, string resourceKey)
        {
            try
            {
                return TryGetResource(resources, resourceKey) as ImageSource;
            }
            catch
            {
                return null;
            }
        }

        private static object TryGetResource(ResourceDictionary resources, object resourceKey)
        {
            if (resources == null || resourceKey == null)
            {
                return null;
            }

            if (resources.Contains(resourceKey))
            {
                return resources[resourceKey];
            }

            for (int i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var value = TryGetResource(resources.MergedDictionaries[i], resourceKey);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static ResourceDictionary GetFallbackBadgeResources()
        {
            if (_fallbackBadgeResources != null)
            {
                return _fallbackBadgeResources;
            }

            lock (BadgeResourcesLock)
            {
                if (_fallbackBadgeResources == null)
                {
                    try
                    {
                        _fallbackBadgeResources = new ResourceDictionary
                        {
                            Source = BadgeResourcesUri
                        };
                    }
                    catch
                    {
                        _fallbackBadgeResources = new ResourceDictionary();
                    }
                }
            }

            return _fallbackBadgeResources;
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


