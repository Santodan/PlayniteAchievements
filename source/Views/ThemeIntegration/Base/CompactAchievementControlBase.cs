// --SUCCESSSTORY--
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Views.ThemeIntegration.Legacy.Controls;

namespace PlayniteAchievements.Views.ThemeIntegration.Base
{
    /// <summary>
    /// Generic base class for compact achievement controls that display achievements in a horizontal grid.
    /// Programmatically creates grid columns to fit achievements, with "+X more" in the last column.
    /// </summary>
    /// <typeparam name="T">The type of achievement collection (List&lt;AchievementDetail&gt; or similar).</typeparam>
    public abstract class CompactAchievementControlBase : ThemeControlBase, INotifyPropertyChanged
    {
        private static readonly List<AchievementDetail> EmptyAchievements = new List<AchievementDetail>();
        private List<AchievementDetail> _visibleAchievements = EmptyAchievements;

        protected override bool EnableAutomaticThemeDataUpdates => true;
        protected override bool UsesLegacyThemeBindings => true;

        public event PropertyChangedEventHandler PropertyChanged;

        #region IconHeight Property

        public static readonly DependencyProperty IconHeightProperty = DependencyProperty.Register(
            nameof(IconHeight),
            typeof(double),
            typeof(CompactAchievementControlBase),
            new FrameworkPropertyMetadata(48.0));

        public double IconHeight
        {
            get => (double)GetValue(IconHeightProperty);
            set
            {
                SetValue(IconHeightProperty, value);
                UpdateCompactLayout();
            }
        }

        #endregion

        #region ShowHiddenIcon Property

        public static readonly DependencyProperty ShowHiddenIconProperty = DependencyProperty.Register(
            nameof(ShowHiddenIcon),
            typeof(bool),
            typeof(CompactAchievementControlBase),
            new FrameworkPropertyMetadata(false));

        public bool ShowHiddenIcon
        {
            get => (bool)GetValue(ShowHiddenIconProperty);
            set => SetValue(ShowHiddenIconProperty, value);
        }

        #endregion

        /// <summary>
        /// Gets the source collection to display. Derived classes implement this to provide
        /// either the filtered locked achievements or the unlocked achievements.
        /// </summary>
        protected abstract List<AchievementDetail> GetSourceAchievements();

        /// <summary>
        /// Gets the total count of achievements for display calculation.
        /// </summary>
        protected abstract int GetTotalCount();

        /// <summary>
        /// Gets the property name to notify when achievement data changes.
        /// Used for HasX property (e.g., "HasLocked" or "HasUnlocked").
        /// </summary>
        protected abstract string GetHasAchievementPropertyName();

        /// <summary>
        /// Gets the property name to notify when total count changes.
        /// Used for TotalX property (e.g., "TotalLocked" or "TotalUnlocked").
        /// </summary>
        protected abstract string GetTotalCountPropertyName();

        /// <summary>
        /// Gets the property names to watch for settings changes that should trigger an update.
        /// </summary>
        protected abstract string[] GetSettingsPropertyNames();

        protected override bool ShouldHandleLegacyThemeDataChange(string propertyName)
        {
            var watchedProperties = GetSettingsPropertyNames();
            if (watchedProperties == null || watchedProperties.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < watchedProperties.Length; i++)
            {
                if (watchedProperties[i] == propertyName)
                {
                    return true;
                }
            }

            return false;
        }

        protected override void OnThemeDataUpdated()
        {
            UpdateFilteredAchievements();
        }

        /// <summary>
        /// Gets the compact view grid that displays achievements.
        /// Derived classes must implement this to provide access to their XAML-defined grid.
        /// </summary>
        protected abstract Grid CompactViewGrid { get; }

        /// <summary>
        /// Creates an achievement image control for display in the grid.
        /// Derived classes can override to customize icon behavior.
        /// </summary>
        protected virtual AchievementImage CreateAchievementImage(AchievementDetail achievement)
        {
            var image = new AchievementImage
            {
                Width = IconHeight,
                Height = IconHeight,
                ToolTip = achievement.DisplayName,
                Icon = achievement.UnlockedIconDisplay,
                IconCustom = achievement.UnlockedIconDisplay,
                IsLocked = false,
                Percent = achievement.RarityPercentValue,
                HasRarityPercent = achievement.HasRarityPercent,
                Rarity = achievement.Rarity,
                RarityText = achievement.RarityText,
                EnableRaretyIndicator = true,
                DisplayRaretyValue = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Bind ShowRarityGlow directly to settings - automatic updates with no manual sync needed
            var glowBinding = new Binding("Settings.Persisted.ShowRarityGlow")
            {
                Source = Plugin,
                Mode = BindingMode.OneWay,
                FallbackValue = true
            };
            image.SetBinding(AchievementImage.ShowRarityGlowProperty, glowBinding);

            return image;
        }

        protected CompactAchievementControlBase()
        {
            IconHeight = 48.0;
            SizeChanged += OnSizeChanged;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged || e.HeightChanged)
            {
                // When size changes, the element has been rendered, so update immediately
                UpdateCompactLayout();
            }
        }

        private void UpdateFilteredAchievements()
        {
            _visibleAchievements = GetSourceAchievements() ?? EmptyAchievements;
            OnPropertyChanged(nameof(VisibleAchievements));
            OnPropertyChanged(GetHasAchievementPropertyName());
            OnPropertyChanged(GetTotalCountPropertyName());
            OnPropertyChanged(nameof(RemainingCount));

            // Defer layout update to ensure the element has been rendered
            Dispatcher?.BeginInvoke(new Action(() => UpdateCompactLayout()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Gets the actual total count of achievements for remaining calculation.
        /// This can be overridden by derived classes if the visible achievements
        /// are just a preview (e.g., locked achievements).
        /// </summary>
        protected virtual int GetRemainingTotalCount()
        {
            return GetTotalCount();
        }

        private void UpdateCompactLayout()
        {
            if (CompactViewGrid == null || double.IsNaN(CompactViewGrid.ActualWidth))
            {
                return;
            }

            if (_visibleAchievements.Count == 0)
            {
                CompactViewGrid.Children.Clear();
                CompactViewGrid.ColumnDefinitions.Clear();
                return;
            }

            CompactViewGrid.Children.Clear();
            CompactViewGrid.ColumnDefinitions.Clear();

            double actualWidth = CompactViewGrid.ActualWidth;
            if (actualWidth <= 0)
            {
                actualWidth = IconHeight + 10;
            }
            double iconSize = IconHeight + 10;
            int nbGrid = Math.Max(1, (int)(actualWidth / iconSize));

            for (int i = 0; i < nbGrid; i++)
            {
                ColumnDefinition gridCol = new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                };
                CompactViewGrid.ColumnDefinitions.Add(gridCol);

                if (i < _visibleAchievements.Count)
                {
                    if (i < nbGrid - 1)
                    {
                        var achievement = _visibleAchievements[i];
                        var achievementImage = CreateAchievementImage(achievement);
                        achievementImage.SetValue(Grid.ColumnProperty, i);
                        CompactViewGrid.Children.Add(achievementImage);
                    }
                    else
                    {
                        int remaining = GetRemainingTotalCount() - i;
                        if (remaining > 0)
                        {
                            var more = CreateMoreBadge(remaining);
                            more.SetValue(Grid.ColumnProperty, i);
                            CompactViewGrid.Children.Add(more);
                        }
                        break;
                    }
                }
                else if (_visibleAchievements.Count > 0)
                {
                    if (i == nbGrid - 1)
                    {
                        int remaining = GetRemainingTotalCount() - i;
                        if (remaining > 0)
                        {
                            var more = CreateMoreBadge(remaining);
                            more.SetValue(Grid.ColumnProperty, i);
                            CompactViewGrid.Children.Add(more);
                        }
                    }
                }
            }
        }

        private Label CreateMoreBadge(int remaining)
        {
            return new Label
            {
                FontSize = 16,
                Content = $"+{remaining}",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = $"{remaining} more"
            };
        }

        public List<AchievementDetail> VisibleAchievements => _visibleAchievements;

        public int RemainingCount
        {
            get
            {
                double actualWidth = CompactViewGrid?.ActualWidth ?? 0;
                if (actualWidth <= 0 || IconHeight <= 0) return 0;
                int nbGrid = Math.Max(1, (int)(actualWidth / (IconHeight + 10)));
                int total = GetRemainingTotalCount();
                if (nbGrid >= total) return 0;
                return Math.Max(0, total - (nbGrid - 1));
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
