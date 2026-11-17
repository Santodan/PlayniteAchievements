using System.ComponentModel;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// A Sort By request sets the sort outright, wherever a header click would step the cycle.
    /// </summary>
    [TestClass]
    public class GridSortRequestTests
    {
        private static DataGridColumn Column(string sortMemberPath) =>
            new DataGridTextColumn { SortMemberPath = sortMemberPath };

        [TestMethod]
        public void AchievementResolve_Request_AppliesDirectionWithoutCycling()
        {
            var settings = new PersistedSettings
            {
                SingleGameGridSortColumn = GridSortColumns.FromAchievementMode(CompactListSortMode.None),
                SingleGameGridSortDescending = false
            };

            // A header click on an unsorted column would start ascending.
            var action = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                currentSortPath: null,
                currentSortDirection: null,
                settings,
                AchievementSortSurface.SingleGame,
                sortEvent: new GridSortRequestEventArgs(Column("RaritySortValue"), ListSortDirection.Descending));

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, action.Kind);
            Assert.AreEqual("RaritySortValue", action.SortMemberPath);
            Assert.AreEqual(ListSortDirection.Descending, action.Direction);
        }

        [TestMethod]
        public void AchievementResolve_RequestOnSortedColumn_KeepsRequestedDirection()
        {
            // A header click on a column sorted descending would reset; the request keeps it.
            var action = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                "RaritySortValue",
                ListSortDirection.Descending,
                new PersistedSettings(),
                AchievementSortSurface.SingleGame,
                sortEvent: new GridSortRequestEventArgs(Column("RaritySortValue"), ListSortDirection.Descending));

            Assert.AreEqual(AchievementGridSortActionKind.ApplySort, action.Kind);
            Assert.AreEqual(ListSortDirection.Descending, action.Direction);
        }

        [TestMethod]
        public void AchievementResolve_ResetRequest_ResetsWithoutAColumn()
        {
            var action = AchievementSortHelper.ResolveGridSortAction(
                null,
                "RaritySortValue",
                ListSortDirection.Ascending,
                new PersistedSettings(),
                AchievementSortSurface.SingleGame,
                sortEvent: new GridSortRequestEventArgs(null, null));

            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, action.Kind);
        }

        [TestMethod]
        public void GameSummariesResolve_Request_AppliesDirection()
        {
            var action = GameSummariesSortHelper.ResolveGridSortAction(
                "GameName",
                currentSortPath: null,
                currentSortDirection: null,
                new PersistedSettings(),
                GameSummariesSortSurface.Overview,
                new GridSortRequestEventArgs(Column("GameName"), ListSortDirection.Descending));

            Assert.AreEqual(GameSummariesGridSortActionKind.ApplySort, action.Kind);
            Assert.AreEqual("GameName", action.SortMemberPath);
            Assert.AreEqual(ListSortDirection.Descending, action.Direction);
        }

        [TestMethod]
        public void GameSummariesResolve_ResetRequest_Resets()
        {
            var action = GameSummariesSortHelper.ResolveGridSortAction(
                null,
                "GameName",
                ListSortDirection.Ascending,
                new PersistedSettings(),
                GameSummariesSortSurface.Overview,
                new GridSortRequestEventArgs(null, null));

            Assert.AreEqual(GameSummariesGridSortActionKind.ResetToDefault, action.Kind);
        }

        [TestMethod]
        public void FriendSummariesResolve_Request_AppliesDirection()
        {
            var action = FriendSummarySortHelper.ResolveGridSortAction(
                "DisplayName",
                currentSortPath: null,
                currentSortDirection: null,
                new PersistedSettings(),
                new GridSortRequestEventArgs(Column("DisplayName"), ListSortDirection.Descending));

            Assert.AreEqual(FriendSummariesGridSortActionKind.ApplySort, action.Kind);
            Assert.AreEqual(ListSortDirection.Descending, action.Direction);
        }

        [TestMethod]
        public void FriendSummariesResolve_ResetRequest_Resets()
        {
            var action = FriendSummarySortHelper.ResolveGridSortAction(
                null,
                "DisplayName",
                ListSortDirection.Ascending,
                new PersistedSettings(),
                new GridSortRequestEventArgs(null, null));

            Assert.AreEqual(FriendSummariesGridSortActionKind.ResetToDefault, action.Kind);
        }

        [TestMethod]
        public void HandleSorting_Request_SetsRequestedDirection()
        {
            var column = Column("GameName");
            column.SortDirection = ListSortDirection.Descending;

            // A header click here would clear the sort (descending is the cycle's last step).
            var direction = DataGridSortingHelper.HandleSorting(
                null,
                new GridSortRequestEventArgs(column, ListSortDirection.Descending));

            Assert.AreEqual(ListSortDirection.Descending, direction);
            Assert.AreEqual(ListSortDirection.Descending, column.SortDirection);
        }

        [TestMethod]
        public void HandleSorting_ResetRequest_ClearsTheSort()
        {
            var direction = DataGridSortingHelper.HandleSorting(null, new GridSortRequestEventArgs(null, null));

            Assert.IsNull(direction);
        }

        [TestMethod]
        public void HeaderClickThatResets_IsMarkedAsUnsorted()
        {
            var settings = new PersistedSettings
            {
                SingleGameGridSortColumn = GridSortColumns.FromAchievementMode(CompactListSortMode.None),
                SingleGameGridSortDescending = false
            };
            var click = new DataGridSortingEventArgs(Column("RaritySortValue"));

            // Descending is the cycle's last step, so this click removes the sort.
            var action = AchievementSortHelper.ResolveGridSortAction(
                "RaritySortValue",
                "RaritySortValue",
                ListSortDirection.Descending,
                settings,
                AchievementSortSurface.SingleGame,
                sortEvent: click);

            Assert.AreEqual(AchievementGridSortActionKind.ResetToDefault, action.Kind);
            Assert.IsTrue(GridSortRequestEventArgs.WasReset(click));
        }

        [TestMethod]
        public void HeaderClickThatSorts_IsNotMarkedAsUnsorted()
        {
            var click = new DataGridSortingEventArgs(Column("GameName"));

            GameSummariesSortHelper.ResolveGridSortAction(
                "GameName",
                currentSortPath: null,
                currentSortDirection: null,
                new PersistedSettings(),
                GameSummariesSortSurface.Overview,
                click);

            Assert.IsFalse(GridSortRequestEventArgs.WasReset(click));
        }

        [TestMethod]
        public void HandleSorting_CycleEnd_IsMarkedAsUnsorted()
        {
            var column = Column("GameName");
            column.SortDirection = ListSortDirection.Descending;
            var click = new DataGridSortingEventArgs(column);

            Assert.IsNull(DataGridSortingHelper.HandleSorting(null, click));
            Assert.IsTrue(GridSortRequestEventArgs.WasReset(click));
        }

        [TestMethod]
        public void HandleSorting_HeaderClick_StillCycles()
        {
            var column = Column("GameName");
            column.SortDirection = ListSortDirection.Ascending;

            var direction = DataGridSortingHelper.HandleSorting(null, new DataGridSortingEventArgs(column));

            Assert.AreEqual(ListSortDirection.Descending, direction);
        }
    }
}
