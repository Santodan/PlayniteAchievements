using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.ThemeIntegration.Base;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    public partial class AchievementFriendAchievementsGridControl : ThemeControlBase
    {
        public static readonly DependencyProperty DisplayItemsProperty =
            DependencyProperty.Register(
                nameof(DisplayItems),
                typeof(IEnumerable<AchievementDisplayItem>),
                typeof(AchievementFriendAchievementsGridControl),
                new PropertyMetadata(null));

        public IEnumerable<AchievementDisplayItem> DisplayItems
        {
            get => (IEnumerable<AchievementDisplayItem>)GetValue(DisplayItemsProperty);
            private set => SetValue(DisplayItemsProperty, value);
        }

        public AchievementFriendAchievementsGridControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        protected override bool EnableAutomaticThemeDataUpdates => true;
        protected override bool UsesThemeBindings => true;
        protected override WheelScrollAxis? WheelClaimAxis => WheelScrollAxis.Vertical;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadData();
        }

        protected override void OnThemeDataUpdated()
        {
            LoadData();
        }

        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return string.IsNullOrWhiteSpace(propertyName) ||
                   propertyName == nameof(ModernThemeBindings.DynamicFriendAchievements) ||
                   propertyName == nameof(ModernThemeBindings.DynamicFriendAchievementsSortKey) ||
                   propertyName == nameof(ModernThemeBindings.DynamicFriendAchievementsSortDirectionKey);
        }

        // A header click sorts the list through the theme's sort key and direction, which its
        // query reads; a column no sort key orders stays unsorted.
        private void RowsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var theme = EffectiveTheme;
            var sort = AchievementSortHelper.ResolveDynamicAchievementHeaderSort(
                e.Column?.SortMemberPath,
                theme?.DynamicFriendAchievementsSortKey,
                theme?.DynamicFriendAchievementsSortDirectionKey,
                e.Column?.SortDirection,
                e);
            if (sort.HasValue && DynamicThemeOptionGroups.LibraryAchievementSortKeyMap.ContainsKey(sort.Value.Key))
            {
                ThemeBindings?.SortDynamicFriendAchievementsCommand?.Execute(sort.Value.Key);
                ThemeBindings?.SetDynamicFriendAchievementsSortDirectionCommand?.Execute(sort.Value.Direction);
            }
            else if (sort.HasValue)
            {
                // A column no theme sort key names, such as Friend or Type.
                PlayniteAchievementsPlugin.Instance?.ThemeIntegrationService?.SortFriendAchievements(sort.Value.Key, sort.Value.Direction);
            }

            ShowSort();
        }

        // The arrow on the column the theme's sort key orders by.
        private void ShowSort()
        {
            var theme = EffectiveTheme;
            var sort = AchievementQuery.SortOfColumn(AchievementSortHelper.GetDynamicAchievementSortMemberPath(theme?.DynamicFriendAchievementsSortKey));
            var column = RowsGrid.InternalDataGrid?.Columns.FirstOrDefault(candidate =>
                sort != null && AchievementQuery.SortOfColumn(candidate.SortMemberPath) == sort);
            RowsGrid.SetSortIndicator(
                column?.SortMemberPath,
                column == null ? (ListSortDirection?)null
                    : theme.DynamicFriendAchievementsSortDirectionKey == DynamicThemeViewKeys.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending);
        }

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.ShowFriendsOverviewAchievementsGridColumnHeaders) ||
                   propertyName == nameof(PersistedSettings.FriendsOverviewAchievementsGridRowHeight);
        }

        private void LoadData()
        {
            // The list itself, which loads the rows the grid shows.
            DisplayItems = EffectiveTheme?.DynamicFriendAchievements ?? System.Array.Empty<FriendAchievementDisplayItem>();
            ShowSort();
        }
    }
}
