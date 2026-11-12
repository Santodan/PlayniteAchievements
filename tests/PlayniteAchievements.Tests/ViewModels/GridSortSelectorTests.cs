using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class GridSortSelectorTests
    {
        private static GridSortSelector Selector(List<(string Key, ListSortDirection? Direction)> applied)
        {
            return new GridSortSelector
            {
                Options = new[]
                {
                    new GridSortOption("GameName", "Name"),
                    new GridSortOption("Progression", "Progress")
                },
                Apply = (key, direction) => applied.Add((key, direction))
            };
        }

        [TestMethod]
        public void Defaults_ToTheSortItemIdAndNoSelection()
        {
            var selector = new GridSortSelector();

            Assert.AreEqual(ShowcaseControlBarItems.Sort, selector.Id);
            Assert.IsFalse(selector.HasSelection);
            Assert.IsFalse(selector.EffectiveIsVisible, "Without options there is nothing to sort by.");
        }

        [TestMethod]
        public void Select_AppliesTheKeyInTheShownDirection()
        {
            var applied = new List<(string, ListSortDirection?)>();
            var selector = Selector(applied);
            selector.ShowState("GameName", ListSortDirection.Descending);

            selector.Select("Progression");

            CollectionAssert.AreEqual(
                new[] { ("Progression", (ListSortDirection?)ListSortDirection.Descending) },
                applied);
        }

        [TestMethod]
        public void ToggleDirection_FlipsTheChosenSort()
        {
            var applied = new List<(string, ListSortDirection?)>();
            var selector = Selector(applied);
            selector.ShowState("GameName", ListSortDirection.Ascending);

            selector.ToggleDirection();

            CollectionAssert.AreEqual(
                new[] { ("GameName", (ListSortDirection?)ListSortDirection.Descending) },
                applied);
        }

        [TestMethod]
        public void ToggleDirection_DoesNothingOnTheDefaultSort()
        {
            var applied = new List<(string, ListSortDirection?)>();
            var selector = Selector(applied);

            selector.ToggleDirection();

            Assert.AreEqual(0, applied.Count);
        }

        [TestMethod]
        public void Select_TheSortedKeyAgain_Unsorts()
        {
            var applied = new List<(string, ListSortDirection?)>();
            var selector = Selector(applied);
            selector.ShowState("GameName", ListSortDirection.Ascending);

            selector.Select("GameName");

            CollectionAssert.AreEqual(
                new[] { ((string)null, (ListSortDirection?)null) },
                applied);
        }

        [TestMethod]
        public void ClearSelectionAndSelectNull_Unsort()
        {
            var applied = new List<(string, ListSortDirection?)>();
            var selector = Selector(applied);
            selector.ShowState("GameName", ListSortDirection.Ascending);

            selector.ClearSelection();
            selector.Select(null);

            CollectionAssert.AreEqual(
                new[] { ((string)null, (ListSortDirection?)null), ((string)null, (ListSortDirection?)null) },
                applied);
        }

        [TestMethod]
        public void ShowState_ShowsTheSortWithoutApplying()
        {
            var applied = new List<(string, ListSortDirection?)>();
            var selector = Selector(applied);

            selector.ShowState("Progression", ListSortDirection.Descending);

            Assert.AreEqual("Progression", selector.SelectedKey);
            Assert.AreEqual("Progress", selector.ButtonText);
            Assert.IsTrue(selector.IsDescending);
            Assert.AreEqual(0, applied.Count);

            selector.ShowState(null, null);

            Assert.IsFalse(selector.HasSelection);
        }
    }

    [TestClass]
    public class GridControlBarSurfaceStateTests
    {
        private static readonly IList<string> Activity = new[] { "Played", "Unplayed" };

        [TestMethod]
        public void ProgressAndActivity_RoundTrip()
        {
            var state = GridControlBarSurfaceState.Capture(
                new[] { "InProgress", "Complete" },
                Activity,
                new HashSet<string> { "Unplayed" });

            var restored = GridControlBarSurfaceState.Deserialize(GridControlBarSurfaceState.Serialize(state));

            CollectionAssert.AreEquivalent(new[] { "InProgress", "Complete" }, restored.ProgressKeys);
            CollectionAssert.AreEqual(new[] { "Unplayed" }, GridControlBarSurfaceState.RestoreActivity(restored, Activity));
        }

        [TestMethod]
        public void DefaultProgress_IsNotSaved()
        {
            var state = GridControlBarSurfaceState.Capture(null, Activity, new HashSet<string> { "Played" });

            var restored = GridControlBarSurfaceState.Deserialize(GridControlBarSurfaceState.Serialize(state));

            Assert.IsNull(restored.ProgressKeys, "No Progress keys means the bar keeps its default picks.");
            CollectionAssert.AreEqual(new[] { "Played" }, GridControlBarSurfaceState.RestoreActivity(restored, Activity));
        }

        [TestMethod]
        public void EmptyProgressPick_IsSavedAsEmpty()
        {
            var state = GridControlBarSurfaceState.Capture(new string[0], Activity, new HashSet<string>());

            var restored = GridControlBarSurfaceState.Deserialize(GridControlBarSurfaceState.Serialize(state));

            Assert.IsNotNull(restored.ProgressKeys);
            Assert.AreEqual(0, restored.ProgressKeys.Count);
        }

        [TestMethod]
        public void NothingSet_SavesNothing()
        {
            var state = GridControlBarSurfaceState.Capture(null, Activity, new HashSet<string>());

            Assert.IsNull(GridControlBarSurfaceState.Serialize(state));
        }

        [TestMethod]
        public void OnlyProgressAndActivity_AreSaved()
        {
            var state = new ControlBarFilterState
            {
                SearchText = "zelda",
                ProgressKeys = new List<string> { "Complete" }
            };

            var restored = GridControlBarSurfaceState.Deserialize(GridControlBarSurfaceState.Serialize(
                GridControlBarSurfaceState.Capture(state.ProgressKeys, Activity, new HashSet<string>())));

            Assert.IsNull(restored.SearchText);
        }

        [TestMethod]
        public void UnreadableState_RestoresNothing()
        {
            Assert.IsNull(GridControlBarSurfaceState.Deserialize("{not json"));
            Assert.IsNull(GridControlBarSurfaceState.Deserialize(null));
        }
    }
}
