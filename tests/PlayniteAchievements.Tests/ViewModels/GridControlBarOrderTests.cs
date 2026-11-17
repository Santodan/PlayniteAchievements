using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class GridControlBarOrderTests
    {
        private static List<string> Apply(string[] defaults, params string[] saved) =>
            GridControlBarOrder.Apply(defaults, id => id, saved);

        private static GridMultiSelectFilter Filter(string id, bool staysOnBar = false) =>
            new GridMultiSelectFilter(
                null,
                null,
                () => id,
                new[] { "A", "B" },
                _ => false,
                (_, __) => { })
            {
                Id = id,
                StaysOnBar = staysOnBar
            };

        private static GridActionButton Button(string id) => new GridActionButton(id, null) { Id = id };

        [TestMethod]
        public void Apply_NoSavedOrder_KeepsTheDefault()
        {
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Apply(new[] { "a", "b", "c" }));
            CollectionAssert.AreEqual(
                new[] { "a", "b", "c" },
                GridControlBarOrder.Apply(new[] { "a", "b", "c" }, id => id, null));
        }

        [TestMethod]
        public void Apply_SavedOrder_RearrangesWithoutRegardToCase()
        {
            CollectionAssert.AreEqual(new[] { "c", "a", "b" }, Apply(new[] { "a", "b", "c" }, "C", "A", "B"));
        }

        [TestMethod]
        public void Apply_UnlistedItem_FollowsItsDefaultPredecessor()
        {
            CollectionAssert.AreEqual(
                new[] { "c", "a", "new", "b" },
                Apply(new[] { "a", "new", "b", "c" }, "c", "a", "b"));
            CollectionAssert.AreEqual(
                new[] { "new", "b", "a" },
                Apply(new[] { "new", "a", "b" }, "b", "a"),
                "An unlisted item with no predecessor stays first.");
        }

        [TestMethod]
        public void Move_SwapsWithTheNeighbour_AndStopsAtTheEnds()
        {
            var ids = new[] { "a", "b", "c" };

            CollectionAssert.AreEqual(new[] { "b", "a", "c" }, GridControlBarOrder.Move(ids, "b", -1));
            CollectionAssert.AreEqual(new[] { "a", "c", "b" }, GridControlBarOrder.Move(ids, "b", 1));
            Assert.IsNull(GridControlBarOrder.Move(ids, "a", -1));
            Assert.IsNull(GridControlBarOrder.Move(ids, "c", 1));
            Assert.IsNull(GridControlBarOrder.Move(ids, "missing", 1));
        }

        [TestMethod]
        public void Move_WithGroups_PassesItemsOfTheOtherGroup()
        {
            var ids = new[] { "bar1", "panel1", "bar2", "panel2" };

            CollectionAssert.AreEqual(
                new[] { "panel1", "bar2", "bar1", "panel2" },
                GridControlBarOrder.Move(ids, "bar1", 1, id => id.StartsWith("bar")));
            Assert.IsNull(GridControlBarOrder.Move(ids, "bar2", 1, id => id.StartsWith("bar")));
        }

        [TestMethod]
        public void Arrange_Default_PutsSortThenFiltersLast_AndKeepsCompareOnTheBar()
        {
            var filters = new GridFiltersButton();
            var sort = new GridSortSelector();
            var tag = Filter(ShowcaseControlBarItems.Tag);
            var compare = Filter(ShowcaseControlBarItems.Compare, staysOnBar: true);
            var expand = Button(ShowcaseControlBarItems.ExpandAll);

            GridControlBarOrder.Arrange(
                new GridControlBarItem[] { sort, tag, compare, expand },
                filters,
                collapsesFilters: true,
                saved: null,
                out var bar,
                out var panel);

            CollectionAssert.AreEqual(new GridControlBarItem[] { compare, expand, sort, filters }, bar);
            CollectionAssert.AreEqual(new GridControlBarItem[] { tag }, panel);
        }

        [TestMethod]
        public void Arrange_FiltersHidden_ShowsTheFiltersOnTheBar()
        {
            var filters = new GridFiltersButton();
            var tag = Filter(ShowcaseControlBarItems.Tag);
            var expand = Button(ShowcaseControlBarItems.ExpandAll);

            GridControlBarOrder.Arrange(
                new GridControlBarItem[] { tag, expand },
                filters,
                collapsesFilters: false,
                saved: null,
                out var bar,
                out var panel);

            CollectionAssert.AreEqual(new GridControlBarItem[] { tag, expand }, bar);
            Assert.AreEqual(0, panel.Count);
        }

        [TestMethod]
        public void Arrange_SavedOrder_OrdersTheBarAndThePanel()
        {
            var filters = new GridFiltersButton();
            var sort = new GridSortSelector();
            var tag = Filter(ShowcaseControlBarItems.Tag);
            var rarity = Filter(ShowcaseControlBarItems.Rarity);
            var expand = Button(ShowcaseControlBarItems.ExpandAll);

            GridControlBarOrder.Arrange(
                new GridControlBarItem[] { tag, rarity, expand, sort },
                filters,
                collapsesFilters: true,
                saved: new[]
                {
                    ShowcaseControlBarItems.Filters,
                    ShowcaseControlBarItems.Sort,
                    ShowcaseControlBarItems.ExpandAll,
                    ShowcaseControlBarItems.Rarity,
                    ShowcaseControlBarItems.Tag
                },
                out var bar,
                out var panel);

            CollectionAssert.AreEqual(new GridControlBarItem[] { filters, sort, expand }, bar);
            CollectionAssert.AreEqual(new GridControlBarItem[] { rarity, tag }, panel);
        }

        [TestMethod]
        public void FiltersButton_HasAnIdToHideItBy()
        {
            Assert.AreEqual(ShowcaseControlBarItems.Filters, new GridFiltersButton().Id);
            Assert.IsFalse(Filter(ShowcaseControlBarItems.Compare, staysOnBar: true).IsPanelFilter);
        }
    }
}
