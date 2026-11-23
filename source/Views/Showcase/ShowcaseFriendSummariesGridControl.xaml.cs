using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>Hosts the shared friend summaries grid inside the friends leaderboard widget.</summary>
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
        }

        protected override FrameworkElement GridElement => InnerGrid;

        protected override void RefreshGrid() => InnerGrid?.Refresh();

        public override void Dispose() => InnerGrid?.Dispose();

        // Friends are not pinned, so there is nothing to reorder.
        protected override void AppendPinReorderItems(ContextMenu menu, object data)
        {
        }
    }
}
