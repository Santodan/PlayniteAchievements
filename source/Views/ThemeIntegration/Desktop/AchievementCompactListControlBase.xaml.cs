using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Base class for compact list controls that get data from ThemeData.
    /// Provides filtering by unlock state and overflow limiting.
    /// </summary>
    public abstract class AchievementCompactListControlBase : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

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
            base.OnThemeDataOverrideChangedInternal();
        }

        /// <summary>
        /// Filters achievements for display. Override to provide custom filtering.
        /// Default returns true (show all achievements).
        /// </summary>
        protected virtual bool FilterAchievement(AchievementDetail achievement) => true;

        /// <summary>
        /// Loads data from ThemeData and applies filtering.
        /// </summary>
        protected virtual void LoadData()
        {
            var theme = ThemeDataOverride ?? Plugin?.Settings?.Theme;
            if (theme == null || !theme.HasAchievements)
            {
                _lastAllItems = null;
                _lastAllAchievements = null;
                ClearItems();
                return;
            }

            var allItems = theme.AllAchievementDisplayItems ?? new List<AchievementDisplayItem>();
            var allAchievements = theme.AllAchievements ?? new List<AchievementDetail>();

            // Skip work if source references haven't changed
            if (ReferenceEquals(allItems, _lastAllItems) && ReferenceEquals(allAchievements, _lastAllAchievements))
            {
                return;
            }

            _lastAllItems = allItems;
            _lastAllAchievements = allAchievements;

            // Build filtered display items
            var displayItems = new List<AchievementDisplayItem>();

            for (int i = 0; i < allAchievements.Count && i < allItems.Count; i++)
            {
                if (FilterAchievement(allAchievements[i]))
                {
                    displayItems.Add(allItems[i].Clone());
                }
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

        /// <summary>
        /// Handles mouse wheel for horizontal scrolling.
        /// </summary>
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0)
            {
                var scrollViewer = FindScrollViewer(this);
                if (scrollViewer != null)
                {
                    e.Handled = true;
                    scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
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
        /// Determines whether a change raised from ThemeData should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            // Refresh when achievement data changes
            return propertyName == nameof(Models.ThemeIntegration.ThemeData.AllAchievementDisplayItems) ||
                   propertyName == nameof(Models.ThemeIntegration.ThemeData.AllAchievements);
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
            // ThemeData is already populated by OnGameSelected in the plugin
            if (_isLoaded)
            {
                LoadData();
            }
        }
    }
}
