using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements list control for theme integration.
    /// Displays achievements in a DataGrid with sorting and virtualization.
    /// Clones items to maintain independent reveal state per control instance.
    /// </summary>
    public partial class AchievementListControl : ThemeControlBase
    {
        // Cache the source reference to avoid unnecessary cloning when data hasn't changed
        private List<AchievementDisplayItem> _lastSourceItems;

        // Sort state tracking
        private string _currentSortPath;
        private ListSortDirection? _currentSortDirection;

        /// <summary>
        /// Identifies the DisplayItems dependency property.
        /// </summary>
        public static readonly DependencyProperty DisplayItemsProperty =
            DependencyProperty.Register(nameof(DisplayItems), typeof(ObservableCollection<AchievementDisplayItem>),
                typeof(AchievementListControl), new PropertyMetadata(null));

        /// <summary>
        /// Gets the display items for the list.
        /// </summary>
        public ObservableCollection<AchievementDisplayItem> DisplayItems
        {
            get => (ObservableCollection<AchievementDisplayItem>)GetValue(DisplayItemsProperty);
            private set => SetValue(DisplayItemsProperty, value);
        }

        public AchievementListControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateMaxHeight();
            LoadData();
        }

        private void UpdateMaxHeight()
        {
            var settings = Plugin?.Settings?.Persisted;
            if (settings == null || AchievementsGrid == null)
            {
                return;
            }

            // Convert nullable double to double (null = NaN means unlimited)
            AchievementsGrid.MaxHeight = settings.AchievementListMaxHeight ?? double.NaN;
        }

        private void LoadData()
        {
            var sourceItems = Plugin?.Settings?.Theme?.AllAchievementDisplayItems;
            if (sourceItems == null)
            {
                _lastSourceItems = null;
                if (DisplayItems == null)
                {
                    DisplayItems = new ObservableCollection<AchievementDisplayItem>();
                }
                else
                {
                    DisplayItems.Clear();
                }
                return;
            }

            // Skip cloning if the source reference hasn't changed
            if (ReferenceEquals(sourceItems, _lastSourceItems))
            {
                return;
            }

            _lastSourceItems = sourceItems;
            var clonedItems = sourceItems.Select(item => item.Clone()).ToList();

            // Reapply current sort if active
            if (!string.IsNullOrWhiteSpace(_currentSortPath) && _currentSortDirection.HasValue)
            {
                AchievementDisplayItemSorter.SortItems(
                    clonedItems,
                    _currentSortPath,
                    _currentSortDirection.Value,
                    ref _currentSortPath,
                    ref _currentSortDirection);
            }

            // Initialize or synchronize collection
            if (DisplayItems == null)
            {
                DisplayItems = new ObservableCollection<AchievementDisplayItem>(clonedItems);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(DisplayItems, clonedItems);
            }
        }

        /// <summary>
        /// Determines whether a change raised from ThemeData should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return propertyName == nameof(Models.ThemeIntegration.ThemeData.AllAchievementDisplayItems);
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
            if (IsLoaded)
            {
                LoadData();
            }
        }

        private void AchievementsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            // Handle sort indicators
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e, AchievementsGrid?.InternalDataGrid);
            if (sortDirection == null) return;

            // Perform the actual sorting
            ApplySorting(e.Column.SortMemberPath, sortDirection.Value);

            // Mark as handled to prevent DataGrid's default sorting
            e.Handled = true;
        }

        private void ApplySorting(string sortMemberPath, ListSortDirection direction)
        {
            if (DisplayItems == null || DisplayItems.Count == 0) return;

            // Sort to a new list
            var items = DisplayItems.ToList();
            AchievementDisplayItemSorter.SortItems(
                items,
                sortMemberPath,
                direction,
                ref _currentSortPath,
                ref _currentSortDirection);

            // Synchronize in place to trigger efficient UI updates
            CollectionHelper.SynchronizeCollection(DisplayItems, items);
        }
    }
}
