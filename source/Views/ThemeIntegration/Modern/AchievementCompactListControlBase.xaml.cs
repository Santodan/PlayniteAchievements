using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Base class for compact list controls that get data from modern theme bindings.
    /// Provides filtering by unlock state and overflow limiting.
    /// </summary>
    public abstract class AchievementCompactListControlBase : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;
        protected override bool UsesThemeBindings => true;

        #region Dependency Properties

        /// <summary>
        /// Identifies the IconSize dependency property.
        /// </summary>
        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(AchievementCompactListControlBase),
                new PropertyMetadata(48.0));

        /// <summary>
        /// Gets or sets the size of each achievement icon.
        /// Default is 48 to match legacy SuccessStory styling.
        /// </summary>
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        #endregion

        #region VisibleCount Property

        /// <summary>
        /// Identifies the VisibleCount dependency property.
        /// </summary>
        public static readonly DependencyProperty VisibleCountProperty =
            DependencyProperty.Register(nameof(VisibleCount), typeof(int), typeof(AchievementCompactListControlBase),
                new PropertyMetadata(0, OnVisibleCountChanged));

        /// <summary>
        /// Gets or sets the maximum number of items to display.
        /// Default is 0 (show all). When greater than 0, limits visible items and shows overflow badge.
        /// </summary>
        public int VisibleCount
        {
            get => (int)GetValue(VisibleCountProperty);
            set => SetValue(VisibleCountProperty, value);
        }

        private static void OnVisibleCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementCompactListControlBase control && control._isLoaded)
            {
                control.LoadData();
            }
        }

        #endregion

        #region Display Properties

        // Cache source references to avoid unnecessary cloning when data hasn't changed
        private List<AchievementDisplayItem> _lastAllItems;
        private List<AchievementDetail> _lastAllAchievements;
        private List<AchievementDetail> _lastSourceAchievements;

        private List<AchievementDisplayItem> _displayItems = new List<AchievementDisplayItem>();
        /// <summary>
        /// Gets or sets the display items for the list.
        /// </summary>
        public List<AchievementDisplayItem> DisplayItems
        {
            get => _displayItems;
            protected set
            {
                _displayItems = value ?? new List<AchievementDisplayItem>();
            }
        }

        private int _overflowCount;
        /// <summary>
        /// Gets the count of items that exceed VisibleCount.
        /// </summary>
        public int OverflowCount
        {
            get => _overflowCount;
            protected set
            {
                if (_overflowCount != value)
                {
                    _overflowCount = value;
                    OnPropertyChanged(new DependencyPropertyChangedEventArgs(
                        OverflowCountProperty, value, value));
                }
            }
        }

        /// <summary>
        /// Identifies the OverflowCount dependency property for binding.
        /// </summary>
        public static readonly DependencyProperty OverflowCountProperty =
            DependencyProperty.Register(nameof(OverflowCount), typeof(int), typeof(AchievementCompactListControlBase),
                new PropertyMetadata(0));

        private bool _hasOverflow;
        /// <summary>
        /// Gets a value indicating whether there are more items than VisibleCount.
        /// </summary>
        public bool HasOverflow
        {
            get => _hasOverflow;
            protected set
            {
                if (_hasOverflow != value)
                {
                    _hasOverflow = value;
                }
            }
        }

        #endregion

        private bool _isLoaded;

        protected AchievementCompactListControlBase()
        {
            DataContext = this;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            LoadData();
            // Attach mouse wheel handler for horizontal scrolling
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            PreviewMouseWheel -= OnPreviewMouseWheel;
        }

        /// <summary>
        /// Called when ThemeDataOverride changes. Clears caches to force data refresh.
        /// </summary>
        protected override void OnThemeDataOverrideChangedInternal()
        {
            _lastAllItems = null;
            _lastAllAchievements = null;
            _lastSourceAchievements = null;
            base.OnThemeDataOverrideChangedInternal();
        }

        /// <summary>
        /// Filters achievements for display. Override to provide custom filtering.
        /// Default returns true (show all achievements).
        /// </summary>
        protected virtual bool FilterAchievement(AchievementDetail achievement) => true;

        /// <summary>
        /// Gets the ordered achievement source that should drive the compact list.
        /// Defaults to the provider/source order list.
        /// </summary>
        protected virtual List<AchievementDetail> GetOrderedAchievements(ModernThemeBindings theme)
        {
            return theme?.AllAchievements ?? new List<AchievementDetail>();
        }

        /// <summary>
        /// Gets the modern theme property name that backs <see cref="GetOrderedAchievements"/>.
        /// </summary>
        protected virtual string GetOrderedAchievementsPropertyName()
        {
            return nameof(ModernThemeBindings.AllAchievements);
        }

        /// <summary>
        /// Loads data from modern theme bindings and applies filtering.
        /// </summary>
        protected virtual void LoadData()
        {
            var theme = EffectiveTheme;
            if (theme == null || !theme.HasAchievements)
            {
                _lastAllItems = null;
                _lastAllAchievements = null;
                _lastSourceAchievements = null;
                ClearItems();
                return;
            }

            var allItems = theme.AllAchievementDisplayItems ?? new List<AchievementDisplayItem>();
            var allAchievements = theme.AllAchievements ?? new List<AchievementDetail>();
            var sourceAchievements = GetOrderedAchievements(theme) ?? new List<AchievementDetail>();

            // Skip work if source references haven't changed
            if (ReferenceEquals(allItems, _lastAllItems) &&
                ReferenceEquals(allAchievements, _lastAllAchievements) &&
                ReferenceEquals(sourceAchievements, _lastSourceAchievements))
            {
                return;
            }

            _lastAllItems = allItems;
            _lastAllAchievements = allAchievements;
            _lastSourceAchievements = sourceAchievements;
            var revealedKeys = GetRevealedKeys(DisplayItems);
            var displayItemByAchievement = BuildDisplayItemMap(allAchievements, allItems);

            // Build filtered display items
            var displayItems = new List<AchievementDisplayItem>();

            for (int i = 0; i < sourceAchievements.Count; i++)
            {
                var achievement = sourceAchievements[i];
                if (achievement == null || !FilterAchievement(achievement))
                {
                    continue;
                }

                if (!displayItemByAchievement.TryGetValue(achievement, out var sourceItem) || sourceItem == null)
                {
                    continue;
                }

                var clonedItem = sourceItem.Clone();
                var key = GetRevealKey(clonedItem);
                if (revealedKeys?.Contains(key) == true)
                {
                    clonedItem.IsRevealed = true;
                }

                displayItems.Add(clonedItem);
            }

            // Apply VisibleCount limit
            if (VisibleCount > 0 && displayItems.Count > VisibleCount)
            {
                DisplayItems = displayItems.Take(VisibleCount).ToList();
                OverflowCount = displayItems.Count - VisibleCount;
                HasOverflow = true;
            }
            else
            {
                DisplayItems = displayItems;
                OverflowCount = 0;
                HasOverflow = false;
            }

            // Refresh the ItemsControl binding
            RefreshItemsSource();
        }

        /// <summary>
        /// Clears all items and resets overflow state.
        /// </summary>
        protected void ClearItems()
        {
            _lastAllItems = null;
            _lastAllAchievements = null;
            _lastSourceAchievements = null;
            DisplayItems = new List<AchievementDisplayItem>();
            OverflowCount = 0;
            HasOverflow = false;
            RefreshItemsSource();
        }

        /// <summary>
        /// Refreshes the ItemsControl ItemsSource binding. Override to provide control-specific implementation.
        /// </summary>
        protected virtual void RefreshItemsSource()
        {
            // Derived classes should override this to refresh their ItemsControl
        }

        private static HashSet<string> GetRevealedKeys(IEnumerable<AchievementDisplayItem> items)
        {
            if (items == null)
            {
                return null;
            }

            var revealedKeys = new HashSet<string>(
                items
                .Where(item => item?.IsRevealed == true)
                .Select(GetRevealKey)
                .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);

            return revealedKeys.Count > 0 ? revealedKeys : null;
        }

        private static string GetRevealKey(AchievementDisplayItem item)
        {
            if (item == null)
            {
                return null;
            }

            return $"{item.PlayniteGameId:N}|{item.ApiName}|{item.DisplayName}|{item.GameName}";
        }

        private static Dictionary<AchievementDetail, AchievementDisplayItem> BuildDisplayItemMap(
            IList<AchievementDetail> achievements,
            IList<AchievementDisplayItem> items)
        {
            var map = new Dictionary<AchievementDetail, AchievementDisplayItem>();
            if (achievements == null || items == null)
            {
                return map;
            }

            var count = Math.Min(achievements.Count, items.Count);
            for (int i = 0; i < count; i++)
            {
                var achievement = achievements[i];
                var item = items[i];
                if (achievement == null || item == null || map.ContainsKey(achievement))
                {
                    continue;
                }

                map.Add(achievement, item);
            }

            return map;
        }

        /// <summary>
        /// Handles mouse wheel scrolling, preferring horizontal movement for compact list hosts.
        /// </summary>
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0)
            {
                var scrollViewer = FindScrollViewer(this);
                if (scrollViewer != null)
                {
                    if (scrollViewer.ScrollableWidth > 0)
                    {
                        e.Handled = true;
                        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - (e.Delta / 3.0));
                    }
                    else if (scrollViewer.ScrollableHeight > 0)
                    {
                        e.Handled = true;
                        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3.0));
                    }
                }
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            if (parent == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Determines whether a change raised from modern theme bindings should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            // Refresh when achievement data changes
            return propertyName == nameof(ModernThemeBindings.AllAchievementDisplayItems) ||
                   propertyName == nameof(ModernThemeBindings.AllAchievements) ||
                   propertyName == GetOrderedAchievementsPropertyName();
        }

        /// <summary>
        /// Determines whether a settings change should trigger a refresh.
        /// Responds to sort mode and direction changes so the list reorders live.
        /// </summary>
        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.CompactListSortMode) ||
                   propertyName == nameof(PersistedSettings.CompactListSortDescending) ||
                   propertyName == nameof(PersistedSettings.CompactUnlockedListSortMode) ||
                   propertyName == nameof(PersistedSettings.CompactUnlockedListSortDescending) ||
                   propertyName == nameof(PersistedSettings.CompactLockedListSortMode) ||
                   propertyName == nameof(PersistedSettings.CompactLockedListSortDescending);
        }

        /// <summary>
        /// Called when theme data changes and the list should be refreshed.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            LoadData();
        }

        /// <summary>
        /// Called when the game context changes for this control.
        /// </summary>
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // Modern theme bindings are already populated by OnGameSelected in the plugin
            if (_isLoaded)
            {
                LoadData();
            }
        }
    }
}


