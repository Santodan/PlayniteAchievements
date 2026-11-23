using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Hosts the shared friend summaries grid inside the friends leaderboard widget. A header
    /// click saves the ranking to the widget's own grid options, which the view model sorts by.
    /// </summary>
    public partial class ShowcaseFriendSummariesGridControl : ShowcaseGridHostBase
    {
        public ShowcaseFriendSummariesGridControl()
        {
            InitializeComponent();
            // The key binds to this control directly: an ancestor binding in XAML only resolves
            // once the grid joins the visual tree, after it has prepared its columns under the
            // default key, writing into that surface's record.
            InnerGrid.SetBinding(
                Controls.SummaryGridControlBase.ColumnSettingsKeyProperty,
                new System.Windows.Data.Binding(nameof(ColumnSettingsKey)) { Source = this });
            InnerGrid.Sorting += InnerGrid_Sorting;
            Loaded += (_, __) => ApplySortIndicator();
        }

        protected override FrameworkElement GridElement => InnerGrid;

        protected override void RefreshGrid() => InnerGrid?.Refresh();

        public override void Dispose() => InnerGrid?.Dispose();

        // Friends are not pinned, so there is nothing to reorder.
        protected override void AppendPinReorderItems(ContextMenu menu, object data)
        {
        }

        private FriendSummaryGridOptions Options =>
            string.IsNullOrWhiteSpace(ColumnSettingsKey)
                ? null
                : PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.GridOptions?.GetFriendSummaries(ColumnSettingsKey);

        // A click on the ranked column flips its direction; another column ranks by it, highest
        // first (names A to Z). A reset ranks by collection score again.
        private void InnerGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            var options = Options;
            var request = GridSortRequestEventArgs.Of(e);
            FriendSummariesSortMode mode;
            if (options == null)
            {
                return;
            }

            if (request?.IsReset == true)
            {
                mode = FriendSummariesSortMode.CollectionScore;
                options.SortDescending = true;
            }
            else if (FriendSummariesSortSpec.TryModeOf(e.Column?.SortMemberPath, out mode))
            {
                options.SortDescending = request?.Direction != null
                    ? request.Direction == ListSortDirection.Descending
                    : options.SortMode == mode
                        ? !options.SortDescending
                        : mode != FriendSummariesSortMode.Alphabetical;
            }
            else
            {
                return;
            }

            e.Handled = true;
            options.SortMode = mode;
            PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi();
            ApplySortIndicator();
        }

        private void ApplySortIndicator()
        {
            var options = Options;
            var grid = InnerGrid?.InternalDataGrid;
            if (options == null || grid == null)
            {
                return;
            }

            var spec = new FriendSummariesSortSpec(
                options.SortMode,
                options.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
            foreach (var column in grid.Columns)
            {
                column.SortDirection = column.SortMemberPath == spec.SortMemberPath
                    ? spec.Direction
                    : (ListSortDirection?)null;
            }
        }
    }
}
