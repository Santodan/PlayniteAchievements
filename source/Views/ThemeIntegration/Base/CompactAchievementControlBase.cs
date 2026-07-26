// --SUCCESSSTORY--
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Views.ThemeIntegration.Legacy;
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

        // Visual children reused across layout passes; reconciled in place by UpdateCompactLayout.
        private readonly List<AchievementImage> _imageChildren = new List<AchievementImage>();
        private Label _moreBadge;

        // Column count computed by the last layout pass (0 when layout has not run with a measured width).
        // Read by RemainingCount so the getter does not re-derive the layout math.
        private int _layoutColumnCount;

        // State of the last layout pass, used to skip redundant passes (e.g. per-pixel SizeChanged).
        private List<AchievementDetail> _lastLayoutAchievements;
        private double _lastLayoutIconHeight;
        private int _lastLayoutRemaining;
        private bool _lastLayoutShowHidden;

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
        /// Whether the achievement images represent locked achievements.
        /// </summary>
        protected virtual bool ImagesAreLocked => false;

        /// <summary>
        /// Binding path (relative to the plugin instance) for the rarity glow setting.
        /// Legacy unlocked compact lists follow the modern unlocked list glow setting.
        /// </summary>
        protected virtual string RarityGlowSettingPath => "Settings.Persisted.ModernUnlockedListShowRarityGlow";

        /// <summary>
        /// Whether hidden locked achievements are obscured with click-to-reveal support.
        /// </summary>
        protected virtual bool SupportsHiddenReveal => false;

        /// <summary>
        /// Creates an achievement image control configured with the per-control constants
        /// (locked flag, glow binding, reveal handler). Per-achievement state is applied
        /// separately by ApplyAchievementToImage so instances can be reused across layouts.
        /// </summary>
        private AchievementImage CreateAchievementImage()
        {
            var image = new AchievementImage
            {
                IsLocked = ImagesAreLocked,
                EnableRaretyIndicator = true,
                DisplayRaretyValue = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Clicking any achievement icon opens the achievements window
                // (or toggles hidden reveal), so always show the hand cursor.
                Cursor = Cursors.Hand
            };

            var glowBinding = new Binding(RarityGlowSettingPath)
            {
                Source = Plugin,
                Mode = BindingMode.OneWay,
                FallbackValue = true
            };
            image.SetBinding(AchievementImage.ShowRarityGlowProperty, glowBinding);

            // The handler gates on the achievement stored in Tag, so it is safe to
            // attach once at creation regardless of which achievement is displayed.
            image.MouseLeftButtonDown += AchievementImage_MouseLeftButtonDown;

            return image;
        }

        /// <summary>
        /// Applies per-achievement state to an image, either freshly created or reused.
        /// </summary>
        private void ApplyAchievementToImage(AchievementImage image, AchievementDetail achievement)
        {
            image.Width = IconHeight;
            image.Height = IconHeight;
            image.Percent = achievement.RarityPercentValue;
            image.HasRarityPercent = achievement.HasRarityPercent;
            image.Rarity = achievement.Rarity;
            image.RarityText = achievement.RarityText;
            image.Tag = achievement; // Store achievement for click handler

            if (SupportsHiddenReveal)
            {
                bool hiddenLocked = achievement.Hidden && !achievement.Unlocked;
                HiddenRevealHelper.SetIsRevealed(image, false);
                ApplyAchievementDisplay(image, achievement, obscured: hiddenLocked && !ShowHiddenIcon);
            }
            else
            {
                ApplyAchievementDisplay(image, achievement, obscured: false);
            }
        }

        /// <summary>
        /// Applies the icon and tooltip for an achievement, obscuring hidden details when requested.
        /// Shared by layout construction and the hidden-reveal click handler.
        /// </summary>
        private static void ApplyAchievementDisplay(AchievementImage image, AchievementDetail achievement, bool obscured)
        {
            if (obscured)
            {
                var defaultIcon = AchievementIconResolver.GetDefaultIcon();
                image.Icon = defaultIcon;
                image.IconCustom = defaultIcon;
                image.ToolTip = ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle");
            }
            else
            {
                image.Icon = achievement.UnlockedIconDisplay;
                image.IconCustom = achievement.LockedIconDisplay;
                image.ToolTip = achievement.DisplayName;
            }
        }

        private void AchievementImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is AchievementImage image) || !(image.Tag is AchievementDetail achievement))
            {
                return;
            }

            if (SupportsHiddenReveal && achievement.Hidden && !achievement.Unlocked)
            {
                bool isRevealed = !HiddenRevealHelper.GetIsRevealed(image);
                HiddenRevealHelper.SetIsRevealed(image, isRevealed);
                ApplyAchievementDisplay(image, achievement, obscured: !isRevealed);

                // Reveal state diverged from the built state; force the next layout pass to reapply.
                _lastLayoutAchievements = null;

                e.Handled = true;
                return;
            }

            e.Handled = true;
            OpenViewAchievementsWindowFocused(null, achievement.ApiName, achievement.DisplayName);
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
            var grid = CompactViewGrid;
            if (grid == null || double.IsNaN(grid.ActualWidth))
            {
                return;
            }

            if (_visibleAchievements.Count == 0)
            {
                grid.Children.Clear();
                grid.ColumnDefinitions.Clear();
                _imageChildren.Clear();
                _moreBadge = null;
                _layoutColumnCount = 0;
                _lastLayoutAchievements = null;
                return;
            }

            double actualWidth = grid.ActualWidth;
            double iconSize = IconHeight + 10;
            double layoutWidth = actualWidth > 0 ? actualWidth : iconSize;
            int nbGrid = Math.Max(1, (int)(layoutWidth / iconSize));
            int remaining = GetRemainingTotalCount() - (nbGrid - 1);

            // Skip the pass when nothing that determines the output has changed
            // (e.g. per-pixel SizeChanged within the same column count).
            if (ReferenceEquals(_lastLayoutAchievements, _visibleAchievements) &&
                _layoutColumnCount == nbGrid &&
                _lastLayoutIconHeight == IconHeight &&
                _lastLayoutRemaining == remaining &&
                _lastLayoutShowHidden == ShowHiddenIcon)
            {
                return;
            }

            while (grid.ColumnDefinitions.Count > nbGrid)
            {
                grid.ColumnDefinitions.RemoveAt(grid.ColumnDefinitions.Count - 1);
            }

            while (grid.ColumnDefinitions.Count < nbGrid)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }

            // Achievement images occupy columns 0..nbGrid-2; the last column is reserved
            // for the "+X more" badge when achievements remain beyond the visible columns.
            int imageCount = Math.Min(_visibleAchievements.Count, nbGrid - 1);

            while (_imageChildren.Count > imageCount)
            {
                int last = _imageChildren.Count - 1;
                grid.Children.Remove(_imageChildren[last]);
                _imageChildren.RemoveAt(last);
            }

            for (int i = 0; i < imageCount; i++)
            {
                AchievementImage image;
                if (i < _imageChildren.Count)
                {
                    image = _imageChildren[i];
                }
                else
                {
                    image = CreateAchievementImage();
                    _imageChildren.Add(image);
                    grid.Children.Add(image);
                }

                ApplyAchievementToImage(image, _visibleAchievements[i]);
                image.SetValue(Grid.ColumnProperty, i);
            }

            if (remaining > 0)
            {
                if (_moreBadge == null)
                {
                    _moreBadge = new Label
                    {
                        FontSize = 16,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    grid.Children.Add(_moreBadge);
                }

                _moreBadge.Content = $"+{remaining}";
                _moreBadge.ToolTip = $"{remaining} more";
                _moreBadge.SetValue(Grid.ColumnProperty, nbGrid - 1);
            }
            else if (_moreBadge != null)
            {
                grid.Children.Remove(_moreBadge);
                _moreBadge = null;
            }

            _layoutColumnCount = actualWidth > 0 ? nbGrid : 0;
            _lastLayoutAchievements = _visibleAchievements;
            _lastLayoutIconHeight = IconHeight;
            _lastLayoutRemaining = remaining;
            _lastLayoutShowHidden = ShowHiddenIcon;
        }

        public List<AchievementDetail> VisibleAchievements => _visibleAchievements;

        public int RemainingCount
        {
            get
            {
                double actualWidth = CompactViewGrid?.ActualWidth ?? 0;
                if (actualWidth <= 0 || IconHeight <= 0) return 0;
                int nbGrid = _layoutColumnCount > 0
                    ? _layoutColumnCount
                    : Math.Max(1, (int)(actualWidth / (IconHeight + 10)));
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
