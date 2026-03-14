using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Models;
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
            DependencyProperty.Register(nameof(DisplayItems), typeof(List<AchievementDisplayItem>),
                typeof(AchievementListControl), new PropertyMetadata(new List<AchievementDisplayItem>()));

        /// <summary>
        /// Gets the display items for the list.
        /// </summary>
        public List<AchievementDisplayItem> DisplayItems
        {
            get => (List<AchievementDisplayItem>)GetValue(DisplayItemsProperty);
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
            LoadData();
        }

        private void LoadData()
        {
            var sourceItems = Plugin?.Settings?.Theme?.AllAchievementDisplayItems;
            if (sourceItems == null)
            {
                _lastSourceItems = null;
                DisplayItems = new List<AchievementDisplayItem>();
                return;
            }

            // Skip cloning if the source reference hasn't changed
            if (ReferenceEquals(sourceItems, _lastSourceItems))
            {
                return;
            }

            _lastSourceItems = sourceItems;
            DisplayItems = sourceItems.Select(item => item.Clone()).ToList();

            // Reapply current sort if active
            if (!string.IsNullOrWhiteSpace(_currentSortPath) && _currentSortDirection.HasValue)
            {
                ApplySorting(_currentSortPath, _currentSortDirection.Value);
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
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e);
            if (sortDirection == null) return;

            // Update sort indicator via the control
            AchievementsGrid?.SetSortIndicator(e.Column.SortMemberPath, sortDirection.Value);

            // Perform the actual sorting
            ApplySorting(e.Column.SortMemberPath, sortDirection.Value);
        }

        private void ApplySorting(string sortMemberPath, ListSortDirection direction)
        {
            var items = DisplayItems;
            if (items == null || items.Count == 0) return;

            AchievementDisplayItemSorter.SortItems(
                items,
                sortMemberPath,
                direction,
                ref _currentSortPath,
                ref _currentSortDirection);

            // Trigger property change notification
            DisplayItems = new List<AchievementDisplayItem>(items);
        }
    }
}
