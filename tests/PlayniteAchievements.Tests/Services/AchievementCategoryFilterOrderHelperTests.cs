using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementCategoryFilterOrderHelperTests
    {
        [TestMethod]
        public void BuildOrderedCategoryLabels_PreservesFirstSeenOrder()
        {
            var items = new[]
            {
                new AchievementDisplayItem { CategoryLabel = "Original Game" },
                new AchievementDisplayItem { CategoryLabel = "DLC 1: Revenge" },
                new AchievementDisplayItem { CategoryLabel = "Original Game" },
                new AchievementDisplayItem { CategoryLabel = "DLC 2: Redemption" }
            };

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                items,
                item => item?.CategoryLabel);

            CollectionAssert.AreEqual(
                new[] { "Original Game", "DLC 1: Revenge", "DLC 2: Redemption" },
                ordered);
        }

        [TestMethod]
        public void BuildOrderedCategoryLabels_NormalizesBlankValuesToDefault_AndDedupesCaseInsensitively()
        {
            var items = new[]
            {
                new AchievementDisplayItem { CategoryLabel = null },
                new AchievementDisplayItem { CategoryLabel = " default " },
                new AchievementDisplayItem { CategoryLabel = "DLC 1" },
                new AchievementDisplayItem { CategoryLabel = "dlc 1" },
                new AchievementDisplayItem { CategoryLabel = "" }
            };

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                items,
                item => item?.CategoryLabel);

            CollectionAssert.AreEqual(
                new[] { "Default", "DLC 1" },
                ordered);
        }

        [TestMethod]
        public void BuildOrderedCategoryLabels_AppliesPreferredOrderBeforeFirstSeenRemainder()
        {
            var items = new[]
            {
                new AchievementDisplayItem { CategoryLabel = "Base Game" },
                new AchievementDisplayItem { CategoryLabel = "DLC 2" },
                new AchievementDisplayItem { CategoryLabel = "DLC 1" },
                new AchievementDisplayItem { CategoryLabel = "Event" }
            };

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                items,
                item => item?.CategoryLabel,
                new[] { "dlc 1", "Missing", "base game", "DLC 1" });

            CollectionAssert.AreEqual(
                new[] { "DLC 1", "Base Game", "DLC 2", "Event" },
                ordered);
        }

        [TestMethod]
        public void BuildSelectedGameFilterOptions_UsesCanonicalCategoryOrder_AndPrunesInvalidSelections()
        {
            var selectedCategoryFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DLC 1: Revenge",
                "Missing"
            };

            var options = OverviewAchievementFilters.BuildSelectedGameFilterOptions(
                new[]
                {
                    new AchievementDisplayItem { CategoryType = "Default", CategoryLabel = "Original Game" },
                    new AchievementDisplayItem { CategoryType = "DLC", CategoryLabel = "DLC 1: Revenge" },
                    new AchievementDisplayItem { CategoryType = "DLC", CategoryLabel = "DLC 2: Redemption" },
                    new AchievementDisplayItem { CategoryType = "DLC", CategoryLabel = "Original Game" }
                },
                selectedTypeFilters: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                selectedCategoryFilters: selectedCategoryFilters);

            CollectionAssert.AreEqual(
                new[] { "Original Game", "DLC 1: Revenge", "DLC 2: Redemption" },
                options.CategoryOptions);
            Assert.IsTrue(options.CategorySelectionPruned);
            CollectionAssert.AreEquivalent(
                new[] { "DLC 1: Revenge" },
                selectedCategoryFilters.ToList());
        }

        [TestMethod]
        public void BuildOrderedCategoryTree_LeavesFlatLabelsInTheirExistingOrder()
        {
            var flat = new[] { "Multiplayer", "DLC", "Story" };

            CollectionAssert.AreEqual(
                new[] { "Multiplayer", "DLC", "Story" },
                AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(flat, null).ToArray());

            CollectionAssert.AreEqual(
                new[] { "Story", "DLC", "Multiplayer" },
                AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(flat, new[] { "Story", "DLC" }).ToArray());
        }

        [TestMethod]
        public void BuildOrderedCategoryTree_KeepsSiblingsContiguousUnderTheirParent()
        {
            CollectionAssert.AreEqual(
                new[] { "DLC", "DLC::Winter", "DLC::Summer", "Multiplayer" },
                AchievementCategoryFilterOrderHelper
                    .BuildOrderedCategoryTree(new[] { "DLC::Winter", "Multiplayer", "DLC::Summer" }, null)
                    .ToArray());

            CollectionAssert.AreEqual(
                new[] { "A", "A::B", "A::B::C", "A::B::D", "A::E", "F" },
                AchievementCategoryFilterOrderHelper
                    .BuildOrderedCategoryTree(new[] { "A::B::C", "A::B::D", "A::E", "F" }, null)
                    .ToArray());
        }

        [TestMethod]
        public void BuildOrderedCategoryTree_IncludesIntermediateNodesHoldingNothing()
        {
            CollectionAssert.AreEqual(
                new[] { "DLC", "DLC::Winter", "DLC::Winter::Week1" },
                AchievementCategoryFilterOrderHelper
                    .BuildOrderedCategoryTree(new[] { "DLC::Winter::Week1" }, null)
                    .ToArray());
        }

        [TestMethod]
        public void BuildOrderedCategoryTree_RendersAnInterleavedStoredOrderWellFormed()
        {
            // The stored order need not be well-formed: a legacy or hand-edited list that splits a
            // subtree still renders contiguous, and the next persist writes it back tidy.
            CollectionAssert.AreEqual(
                new[] { "A", "A::X", "A::Y", "B" },
                AchievementCategoryFilterOrderHelper
                    .BuildOrderedCategoryTree(new[] { "A::X", "A::Y", "B" }, new[] { "A::X", "B", "A::Y" })
                    .ToArray());
        }

        [TestMethod]
        public void BuildOrderedCategoryTree_HonoursAnExplicitParentEntry()
        {
            CollectionAssert.AreEqual(
                new[] { "B", "A", "A::X" },
                AchievementCategoryFilterOrderHelper
                    .BuildOrderedCategoryTree(new[] { "A::X", "B" }, new[] { "B", "A" })
                    .ToArray());
        }

        [TestMethod]
        public void ResolveCategoryOrderIndexForSubtree_TakesTheEarliestIndexBeneathTheNode()
        {
            var labels = new[] { "A::X", "A::Y", "B" };
            var order = new[] { "B", "A::Y", "A::X" };

            Assert.AreEqual(1, AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndexForSubtree("A", labels, order));
            Assert.AreEqual(0, AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndexForSubtree("B", labels, order));
            Assert.AreEqual(
                0,
                AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndexForSubtree("A", labels, new[] { "A", "A::X" }),
                "the node's own entry wins when it is earlier");
        }

        [TestMethod]
        public void ResolveCategoryOrderIndexForSubtree_IsMaxValueWhenNothingInTheSubtreeIsOrdered()
        {
            var labels = new[] { "A::X", "B" };

            Assert.AreEqual(
                int.MaxValue,
                AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndexForSubtree("Z", labels, new[] { "B" }));
            Assert.AreEqual(
                int.MaxValue,
                AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndexForSubtree("A", labels, null));
            Assert.AreEqual(
                int.MaxValue,
                AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndexForSubtree("A", new[] { "AB" }, new[] { "AB" }),
                "'AB' is not beneath 'A'");
        }
    }
}
