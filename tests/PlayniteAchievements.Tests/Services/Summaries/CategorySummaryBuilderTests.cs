using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services.Summaries
{
    [TestClass]
    public class CategorySummaryBuilderTests
    {
        [TestMethod]
        public void Build_GroupsByLabelInFirstSeenOrderWithAggregatedStats()
        {
            var items = new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true),
                DisplayItem("Base", unlocked: false),
                DisplayItem("DLC", unlocked: true)
            };

            var result = CategorySummaryBuilder.Build(items);

            Assert.AreEqual(2, result.Count);
            var dlc = (CategorySummaryItem)result[0];
            var baseCat = (CategorySummaryItem)result[1];

            // First-seen ordering: DLC appears before Base.
            Assert.AreEqual("DLC", dlc.CategoryLabel);
            Assert.AreEqual("Base", baseCat.CategoryLabel);

            // A non-default label is shown verbatim as the row name.
            Assert.AreEqual("DLC", dlc.GameName);

            Assert.AreEqual(2, dlc.TotalAchievements);
            Assert.AreEqual(2, dlc.UnlockedAchievements);
            Assert.AreEqual(1, baseCat.TotalAchievements);
            Assert.AreEqual(0, baseCat.UnlockedAchievements);
        }

        [TestMethod]
        public void Build_SharedCategoryArtFillsBothImageSlots()
        {
            var shared = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, categoryArt: "art.png"),
                DisplayItem("DLC", unlocked: false, categoryArt: "art.png")
            });
            var sharedCategory = (CategorySummaryItem)shared.Single();
            Assert.AreEqual("art.png", sharedCategory.GameLogo);
            Assert.AreEqual("art.png", sharedCategory.GameCoverPath);

            var mixed = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, categoryArt: "a.png"),
                DisplayItem("DLC", unlocked: false, categoryArt: "b.png")
            });
            var mixedCategory = (CategorySummaryItem)mixed.Single();
            Assert.IsNull(mixedCategory.GameLogo);
            Assert.IsNull(mixedCategory.GameCoverPath);
        }

        [TestMethod]
        public void Build_ArtlessCategoriesFallBackToSharedGameImages()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, gameIcon: "game-icon.png", gameCover: "game-cover.png"),
                DisplayItem("DLC", unlocked: false, gameIcon: "game-icon.png", gameCover: "game-cover.png")
            });
            var category = (CategorySummaryItem)result.Single();
            Assert.AreEqual("game-icon.png", category.GameLogo);
            Assert.AreEqual("game-cover.png", category.GameCoverPath);
        }

        [TestMethod]
        public void Build_SharedCategoryArtBeatsGameImages()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, categoryArt: "art.png", gameIcon: "game-icon.png", gameCover: "game-cover.png")
            });
            var category = (CategorySummaryItem)result.Single();
            Assert.AreEqual("art.png", category.GameLogo);
            Assert.AreEqual("art.png", category.GameCoverPath);
        }

        [TestMethod]
        public void Build_SetsSharedGameIdOnlyWhenCategoryBucketAgrees()
        {
            var gameId = Guid.NewGuid();
            var shared = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, playniteGameId: gameId),
                DisplayItem("DLC", unlocked: false, playniteGameId: gameId)
            });
            Assert.AreEqual(gameId, shared.Single().PlayniteGameId);

            var mixed = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, playniteGameId: gameId),
                DisplayItem("DLC", unlocked: false, playniteGameId: Guid.NewGuid())
            });
            Assert.IsNull(mixed.Single().PlayniteGameId);
        }

        [TestMethod]
        public void Build_UsesCategoryOrderIndexBeforeFirstSeenRemainder()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: false, categoryOrderIndex: 1),
                DisplayItem("Event", unlocked: false),
                DisplayItem("DLC", unlocked: true, categoryOrderIndex: 0)
            });

            CollectionAssert.AreEqual(
                new[] { "DLC", "Base", "Event" },
                result.Cast<CategorySummaryItem>().Select(item => item.CategoryLabel).ToList());
        }

        [TestMethod]
        public void Build_BlankLabelsCollapseToASingleDefaultCategory()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem(null, unlocked: true),
                DisplayItem("", unlocked: false)
            });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(2, result[0].TotalAchievements);
        }

        [TestMethod]
        public void Build_NullOrEmptyReturnsEmptyList()
        {
            Assert.AreEqual(0, CategorySummaryBuilder.Build(null).Count);
            Assert.AreEqual(0, CategorySummaryBuilder.Build(new List<AchievementDisplayItem>()).Count);
        }

        [TestMethod]
        public void Build_FullyUnlockedCategoryIsCompleted()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true),
                DisplayItem("DLC", unlocked: true)
            });

            Assert.IsTrue(result.Single().IsCompleted);
        }

        [TestMethod]
        public void Build_UnlockedCapstoneCompletesPartialCategory()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: true, isCapstone: true),
                DisplayItem("Base", unlocked: false)
            });

            Assert.IsTrue(result.Single().IsCompleted);
        }

        [TestMethod]
        public void Build_LockedCapstoneOrPartialUnlocksStayIncomplete()
        {
            var lockedCapstone = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: false, isCapstone: true),
                DisplayItem("Base", unlocked: true)
            });
            Assert.IsFalse(lockedCapstone.Single().IsCompleted);

            var partial = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: true),
                DisplayItem("Base", unlocked: false)
            });
            Assert.IsFalse(partial.Single().IsCompleted);
        }

        [TestMethod]
        public void Build_CapstoneOnlyCompletesItsOwnCategory()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: true, isCapstone: true),
                DisplayItem("DLC", unlocked: false)
            });

            var byLabel = result.Cast<CategorySummaryItem>().ToDictionary(item => item.CategoryLabel);
            Assert.IsTrue(byLabel["Base"].IsCompleted);
            Assert.IsFalse(byLabel["DLC"].IsCompleted);
        }

        [TestMethod]
        public void Build_DefaultBadgeModeAllowsEveryCompletedCategory()
        {
            var result = CategorySummaryBuilder.Build(CompletedCategoriesInConfiguredOrder());

            Assert.IsTrue(result.Cast<CategorySummaryItem>().All(c => c.AllowCompletionBadge));
            Assert.IsTrue(result.All(c => c.ShowCompletionBadge));
        }

        [TestMethod]
        public void Build_NoneBadgeModeSuppressesEveryCategory()
        {
            var result = CategorySummaryBuilder.Build(
                CompletedCategoriesInConfiguredOrder(),
                CategoryCompletionBadgeMode.None);

            Assert.IsTrue(result.Cast<CategorySummaryItem>().All(c => !c.AllowCompletionBadge));
            // The rows are still completed; only the badge is gated.
            Assert.IsTrue(result.All(c => c.IsCompleted));
            Assert.IsTrue(result.All(c => !c.ShowCompletionBadge));
        }

        [TestMethod]
        public void Build_FirstBadgeModeAllowsOnlyTheConfiguredFirstCategory()
        {
            var result = CategorySummaryBuilder.Build(
                CompletedCategoriesInConfiguredOrder(),
                CategoryCompletionBadgeMode.First);

            Assert.AreEqual(3, result.Count);
            // "Base" carries CategoryOrderIndex 0 but is supplied last, so this also proves the gate
            // follows the configured order rather than first-seen order.
            Assert.AreEqual("Base", ((CategorySummaryItem)result[0]).CategoryLabel);

            Assert.IsTrue(result[0].ShowCompletionBadge);
            Assert.IsFalse(result[1].ShowCompletionBadge);
            Assert.IsFalse(result[2].ShowCompletionBadge);
        }

        [TestMethod]
        public void Build_FirstBadgeModeLeavesListBadgeFreeWhenFirstCategoryIsIncomplete()
        {
            var items = new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, categoryOrderIndex: 1),
                DisplayItem("Base", unlocked: true, categoryOrderIndex: 0),
                DisplayItem("Base", unlocked: false, categoryOrderIndex: 0)
            };

            var result = CategorySummaryBuilder.Build(items, CategoryCompletionBadgeMode.First);

            var first = (CategorySummaryItem)result[0];
            Assert.AreEqual("Base", first.CategoryLabel);
            // Permitted, but not completed, so nothing renders anywhere.
            Assert.IsTrue(first.AllowCompletionBadge);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsTrue(result.All(c => !c.ShowCompletionBadge));
        }

        [TestMethod]
        public void AllowCompletionBadge_RaisesShowCompletionBadgeChange()
        {
            var item = (CategorySummaryItem)CategorySummaryBuilder
                .Build(CompletedCategoriesInConfiguredOrder())[0];
            var raised = new List<string>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            item.AllowCompletionBadge = false;

            CollectionAssert.Contains(raised, nameof(CategorySummaryItem.ShowCompletionBadge));
            Assert.IsFalse(item.ShowCompletionBadge);
        }

        /// <summary>
        /// Three fully completed categories whose configured order (Base, DLC, Extra) is the reverse
        /// of the order they are supplied in.
        /// </summary>
        private static List<AchievementDisplayItem> CompletedCategoriesInConfiguredOrder()
        {
            return new List<AchievementDisplayItem>
            {
                DisplayItem("Extra", unlocked: true, categoryOrderIndex: 2),
                DisplayItem("DLC", unlocked: true, categoryOrderIndex: 1),
                DisplayItem("Base", unlocked: true, categoryOrderIndex: 0)
            };
        }

        private static AchievementDisplayItem DisplayItem(
            string label,
            bool unlocked,
            string categoryArt = null,
            string gameIcon = null,
            string gameCover = null,
            int categoryOrderIndex = int.MaxValue,
            Guid? playniteGameId = null,
            bool isCapstone = false)
        {
            return new AchievementDisplayItem
            {
                Rarity = RarityTier.Common,
                PlayniteGameId = playniteGameId,
                CategoryLabel = label,
                Unlocked = unlocked,
                IsCapstone = isCapstone,
                CategoryArtPath = categoryArt,
                GameIconPath = gameIcon,
                GameCoverPath = gameCover,
                CategoryOrderIndex = categoryOrderIndex
            };
        }

        private static AchievementDisplayItem NestedItem(
            string label,
            bool unlocked = false,
            params string[] ancestorArt)
        {
            var item = DisplayItem(label, unlocked);
            if (ancestorArt != null && ancestorArt.Length > 0)
            {
                item.CategoryAncestorArtPaths = ancestorArt;
                item.CategoryArtPath = ancestorArt.LastOrDefault(a => !string.IsNullOrEmpty(a))
                    ?? ancestorArt.FirstOrDefault(a => !string.IsNullOrEmpty(a));
            }

            return item;
        }

        // ---- BuildLevel

        [TestMethod]
        public void BuildLevel_EmitsOneRowPerImmediateChildCountingItsOwnMembers()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true),
                NestedItem("DLC::Winter::Week1", unlocked: true),
                NestedItem("DLC::Summer"),
                NestedItem("Multiplayer")
            };

            var roots = CategorySummaryBuilder.BuildLevel(items, null).Cast<CategorySummaryItem>().ToList();

            CollectionAssert.AreEqual(new[] { "DLC", "Multiplayer" }, roots.Select(r => r.CategoryPath).ToArray());

            var dlc = roots[0];
            Assert.AreEqual(0, dlc.TotalAchievements, "DLC holds none of its own");
            Assert.AreEqual(0, dlc.UnlockedAchievements);
            Assert.AreEqual(2, dlc.ChildCategoryCount, "it still knows it has children");
            Assert.AreEqual(0, dlc.DirectAchievementCount);
            Assert.AreEqual(1, dlc.CategoryDepth);
        }

        [TestMethod]
        public void BuildLevel_CountsANodeWithoutItsDescendants()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC"),
                NestedItem("DLC::Winter"),
                NestedItem("DLC::Winter::Week1"),
                NestedItem("DLC::Summer"),
                NestedItem("DLC::Summer::Wave2")
            };

            var children = CategorySummaryBuilder.BuildLevel(items, "DLC").Cast<CategorySummaryItem>().ToList();
            var parent = CategorySummaryBuilder.BuildLevel(items, null).Cast<CategorySummaryItem>().Single();

            // A row reports what it holds, not what hangs beneath it, so no total on this screen is
            // ever a sum of another one.
            Assert.AreEqual(1, parent.TotalAchievements, "only the achievement labelled DLC itself");
            Assert.AreEqual(1, parent.DirectAchievementCount);
            CollectionAssert.AreEqual(
                new[] { 1, 1 },
                children.Select(c => c.TotalAchievements).ToArray(),
                "each child counts itself, not its own grandchild");
        }

        [TestMethod]
        public void BuildLevel_TitlesRowsWithTheLeafNameByDefault()
        {
            var items = new List<AchievementDisplayItem> { NestedItem("DLC::Winter") };

            var leafNamed = CategorySummaryBuilder.BuildLevel(items, "DLC").Cast<CategorySummaryItem>().Single();
            Assert.AreEqual("Winter", leafNamed.GameName);
            Assert.AreEqual("Winter", leafNamed.CategoryLeafName);
            Assert.AreEqual("DLC::Winter", leafNamed.CategoryPath);
            Assert.AreEqual(2, leafNamed.CategoryDepth);

            var fullyNamed = CategorySummaryBuilder
                .BuildLevel(items, "DLC", CategoryCompletionBadgeMode.All, useLeafNames: false)
                .Cast<CategorySummaryItem>()
                .Single();
            Assert.AreEqual("DLC > Winter", fullyNamed.GameName);
        }

        [TestMethod]
        public void BuildLevel_ReportsAMixedNodeAsHavingBothChildrenAndOwnAchievements()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC"),
                NestedItem("DLC"),
                NestedItem("DLC::Winter")
            };

            var dlc = CategorySummaryBuilder.BuildLevel(items, null).Cast<CategorySummaryItem>().Single();

            Assert.IsTrue(dlc.HasChildCategories);
            Assert.AreEqual(1, dlc.ChildCategoryCount);
            Assert.AreEqual(2, dlc.DirectAchievementCount);
            Assert.AreEqual(2, dlc.TotalAchievements, "its own two, not the child's");
        }

        [TestMethod]
        public void BuildLevel_UsesTheNodesOwnArtRatherThanADescendants()
        {
            // Members of a subtree disagree on CategoryArtPath, so the shared-image resolution a
            // leaf row uses returns null for a parent. The per-level art carries the answer.
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", false, "dlc.png", "winter.png"),
                NestedItem("DLC::Summer", false, "dlc.png", "summer.png")
            };

            var dlc = CategorySummaryBuilder.BuildLevel(items, null).Cast<CategorySummaryItem>().Single();

            Assert.AreEqual("dlc.png", dlc.GameLogo);
            Assert.AreEqual("dlc.png", dlc.GameCoverPath);
        }

        [TestMethod]
        public void BuildLevel_FallsBackToSharedArtWhenTheNodeHasNoneOfItsOwn()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", false, null, "shared.png"),
                NestedItem("DLC::Summer", false, null, "shared.png")
            };

            var dlc = CategorySummaryBuilder.BuildLevel(items, null).Cast<CategorySummaryItem>().Single();

            Assert.AreEqual("shared.png", dlc.GameLogo);
        }

        [TestMethod]
        public void BuildLevel_AppliesTheFirstBadgeToTheFirstRowOfTheLevelShown()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true),
                NestedItem("DLC::Summer", unlocked: true)
            };

            var children = CategorySummaryBuilder
                .BuildLevel(items, "DLC", CategoryCompletionBadgeMode.First)
                .Cast<CategorySummaryItem>()
                .ToList();

            Assert.IsTrue(children[0].AllowCompletionBadge);
            Assert.IsFalse(children[1].AllowCompletionBadge);
        }

        [TestMethod]
        public void BuildLevel_IsEmptyForALeafOrAnUnknownParent()
        {
            var items = new List<AchievementDisplayItem> { NestedItem("DLC::Winter") };

            Assert.AreEqual(0, CategorySummaryBuilder.BuildLevel(items, "DLC::Winter").Count);
            Assert.AreEqual(0, CategorySummaryBuilder.BuildLevel(items, "Nope").Count);
        }

        // ---- BuildTree

        [TestMethod]
        public void BuildTree_WalksEveryNodePreOrder()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter"),
                NestedItem("DLC::Summer"),
                NestedItem("Multiplayer")
            };

            var tree = CategorySummaryBuilder.BuildTree(items).Cast<CategorySummaryItem>().ToList();

            CollectionAssert.AreEqual(
                new[] { "DLC", "DLC::Winter", "DLC::Summer", "Multiplayer" },
                tree.Select(r => r.CategoryPath).ToArray());
            Assert.AreEqual("DLC > Winter", tree[1].GameName, "tree rows carry the full display path");
        }

        [TestMethod]
        public void BuildTree_TitlesRowsWithLeafNamesAndGuidesThemWhenAsked()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter::Frost"),
                NestedItem("Multiplayer")
            };

            var tree = CategorySummaryBuilder
                .BuildTree(items, CategoryCompletionBadgeMode.All, useLeafNames: true)
                .Cast<CategorySummaryItem>()
                .ToList();

            CollectionAssert.AreEqual(
                new[] { "DLC", "Winter", "Frost", "Multiplayer" },
                tree.Select(r => r.GameName).ToArray(),
                "an indented list titles its rows with the leaf name");

            // Depth is the builder's contract; how a surface draws it is not. Asserting the guide
            // geometry here pinned pixel math to the summary builder and would have to be rewritten
            // alongside any change to how nesting is marked on screen.
            CollectionAssert.AreEqual(
                new[] { 1, 2, 3, 1 },
                tree.Select(r => r.CategoryDepth).ToArray(),
                "each row reports its own depth");
        }

        [TestMethod]
        public void BuildTree_CountsEachNodeOnItsOwnMembersByDefault()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true),
                NestedItem("DLC::Winter::Frost"),
                NestedItem("DLC::Summer", unlocked: true),
                NestedItem("Multiplayer")
            };

            var tree = CategorySummaryBuilder.BuildTree(items).Cast<CategorySummaryItem>().ToList();
            var byPath = tree.ToDictionary(r => r.CategoryPath, r => r, StringComparer.OrdinalIgnoreCase);

            // The default mode partitions the set: a parent reports only what is labelled exactly
            // itself, so a node that is purely a folder reads 0/0 rather than restating its
            // children's counts - and no self rows are emitted.
            Assert.IsFalse(tree.Any(r => r.IsSelfRow));
            Assert.AreEqual(0, byPath["DLC"].TotalAchievements, "a folder-only parent holds none of its own");
            Assert.AreEqual(1, byPath["DLC::Winter"].TotalAchievements);
            Assert.AreEqual(1, byPath["DLC::Winter"].UnlockedAchievements);
            Assert.AreEqual(1, byPath["DLC::Winter::Frost"].TotalAchievements);
            Assert.AreEqual(items.Count, tree.Sum(r => r.TotalAchievements), "the rows partition the set");
        }

        [TestMethod]
        public void BuildTree_RollsEveryNodeUpToItsSubtreeInCombinedModes()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true),
                NestedItem("DLC::Winter::Frost"),
                NestedItem("DLC::Summer", unlocked: true),
                NestedItem("Multiplayer")
            };

            var tree = CategorySummaryBuilder
                .BuildTree(items, progressMode: CategoryProgressMode.CombinedWithOwnRows)
                .Cast<CategorySummaryItem>()
                .ToList();

            // A row's numbers describe everything drilling into it reveals, so a folder-only
            // parent reads as the sum of its descendants rather than 0/0.
            var dlc = tree.Single(r => r.CategoryPath == "DLC");
            Assert.AreEqual(3, dlc.TotalAchievements, "a folder-only parent sums its subtree");
            Assert.AreEqual(2, dlc.UnlockedAchievements);

            var winter = tree.Single(r => r.CategoryPath == "DLC::Winter" && !r.IsSelfRow);
            Assert.AreEqual(2, winter.TotalAchievements, "a mixed node's own row is the subtree rollup");
            Assert.AreEqual(1, winter.UnlockedAchievements);

            var frost = tree.Single(r => r.CategoryPath == "DLC::Winter::Frost");
            Assert.AreEqual(1, frost.TotalAchievements);
            Assert.AreEqual(0, frost.UnlockedAchievements);
            Assert.AreEqual(1, tree.Single(r => r.CategoryPath == "Multiplayer").TotalAchievements);

            // Leaf rows - real leaves plus the mixed node's self row - still partition the set.
            var leaves = tree.Where(r => r.IsSelfRow || r.ChildCategoryCount == 0).ToList();
            Assert.AreEqual(items.Count, leaves.Sum(r => r.TotalAchievements), "the leaf rows partition the set");
        }

        [TestMethod]
        public void BuildTree_EmitsASelfRowDirectlyUnderAMixedNode()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true, null, "winter.png"),
                NestedItem("DLC::Winter", unlocked: false, null, "winter.png"),
                NestedItem("DLC::Winter::Frost", unlocked: true, null, "winter.png", "frost.png")
            };

            var tree = CategorySummaryBuilder
                .BuildTree(items, CategoryCompletionBadgeMode.All, useLeafNames: true, CategoryProgressMode.CombinedWithOwnRows)
                .Cast<CategorySummaryItem>()
                .ToList();

            CollectionAssert.AreEqual(
                new[] { false, false, true, false },
                tree.Select(r => r.IsSelfRow).ToArray(),
                "the self row sits directly under its own category, before the child categories");

            var winter = tree[1];
            var self = tree[2];

            Assert.AreEqual("DLC::Winter", self.CategoryPath, "the self row keeps its category's path");
            Assert.AreEqual(winter.CategoryDepth + 1, self.CategoryDepth, "the self row sits one level below");
            Assert.AreEqual(string.Empty, self.GameName, "unlabeled: it sits under a row already carrying the name");
            Assert.AreEqual(winter.SortingName, self.SortingName, "a name sort holds the pair together");
            Assert.AreEqual(2, self.TotalAchievements, "the self row counts only the direct achievements");
            Assert.AreEqual(1, self.UnlockedAchievements);
            Assert.AreEqual(2, self.DirectAchievementCount);
            Assert.AreEqual(0, self.ChildCategoryCount);
            Assert.AreEqual(winter.GameLogo, self.GameLogo, "the self row carries its category's art");
            Assert.AreEqual(winter.GameCoverPath, self.GameCoverPath);

            Assert.AreEqual(3, winter.TotalAchievements, "the category row above stays the subtree rollup");
            Assert.AreEqual(2, winter.UnlockedAchievements);
        }

        [TestMethod]
        public void BuildTree_SkipsTheSelfRowWhenTheSubtreeAddsNothing()
        {
            // A leaf holds only direct achievements, so its row already is the direct reading.
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true),
                NestedItem("Multiplayer")
            };

            var tree = CategorySummaryBuilder
                .BuildTree(items, progressMode: CategoryProgressMode.CombinedWithOwnRows)
                .Cast<CategorySummaryItem>()
                .ToList();

            Assert.IsFalse(tree.Any(r => r.IsSelfRow));
        }

        [TestMethod]
        public void BuildTree_CombinedOnlyRollsUpWithoutSelfRows()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true),
                NestedItem("DLC::Winter::Frost")
            };

            var tree = CategorySummaryBuilder
                .BuildTree(items, progressMode: CategoryProgressMode.CombinedOnly)
                .Cast<CategorySummaryItem>()
                .ToList();

            Assert.IsFalse(tree.Any(r => r.IsSelfRow), "combined-only shows the totals without breakdown rows");
            Assert.AreEqual(2, tree.Single(r => r.CategoryPath == "DLC::Winter").TotalAchievements);
        }

        [TestMethod]
        public void BuildTree_CompletesAFolderOnlyParentWhenItsWholeSubtreeIs()
        {
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter", unlocked: true),
                NestedItem("DLC::Summer", unlocked: true)
            };

            var tree = CategorySummaryBuilder
                .BuildTree(items, progressMode: CategoryProgressMode.CombinedWithOwnRows)
                .Cast<CategorySummaryItem>()
                .ToList();

            Assert.IsTrue(tree.Single(r => r.CategoryPath == "DLC").IsCompleted);
        }

        [TestMethod]
        public void BuildTree_StillEmitsNodesSeveralLevelsDownAsSubtreeSums()
        {
            // Achievements only ever sit on the deepest labels, so every node above them has an
            // empty bucket. Those nodes still get a row - the subtree is what decides a node
            // exists - and each reports its subtree's sum in the combined modes.
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("A::B::C::D", unlocked: true),
                NestedItem("A::B::C::D"),
                NestedItem("A::B::C::E", unlocked: true),
                NestedItem("A::B::F")
            };

            var tree = CategorySummaryBuilder
                .BuildTree(items, progressMode: CategoryProgressMode.CombinedWithOwnRows)
                .Cast<CategorySummaryItem>()
                .ToList();
            Assert.IsFalse(tree.Any(r => r.IsSelfRow), "folder-only chains emit no self rows");

            var byPath = tree.ToDictionary(r => r.CategoryPath, r => r, StringComparer.OrdinalIgnoreCase);
            Assert.AreEqual(4, byPath["A"].TotalAchievements);
            Assert.AreEqual(2, byPath["A"].UnlockedAchievements);
            Assert.AreEqual(4, byPath["A::B"].TotalAchievements);
            Assert.AreEqual(3, byPath["A::B::C"].TotalAchievements);
            Assert.AreEqual(2, byPath["A::B::C::D"].TotalAchievements);
            Assert.AreEqual(1, byPath["A::B::C::D"].UnlockedAchievements);
            Assert.AreEqual(1, byPath["A::B::C::E"].TotalAchievements);
            Assert.AreEqual(1, byPath["A::B::F"].TotalAchievements);
        }

        [TestMethod]
        public void BuildTree_LeavesFlatInputIdenticalToBuild()
        {
            var items = new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true),
                DisplayItem("Base", unlocked: false)
            };

            var flat = CategorySummaryBuilder.Build(items).Cast<CategorySummaryItem>().ToList();
            var tree = CategorySummaryBuilder.BuildTree(items).Cast<CategorySummaryItem>().ToList();

            CollectionAssert.AreEqual(
                flat.Select(r => r.CategoryLabel).ToArray(),
                tree.Select(r => r.CategoryLabel).ToArray());
            CollectionAssert.AreEqual(
                flat.Select(r => r.TotalAchievements).ToArray(),
                tree.Select(r => r.TotalAchievements).ToArray());
        }

        [TestMethod]
        public void Build_StillEmitsOneRowPerLabelWithNestedInput()
        {
            // The theme surface publishes this shape: leaf rows only, so a theme summing the
            // published rows still counts each achievement exactly once.
            var items = new List<AchievementDisplayItem>
            {
                NestedItem("DLC::Winter"),
                NestedItem("DLC::Summer")
            };

            var rows = CategorySummaryBuilder.Build(items).Cast<CategorySummaryItem>().ToList();

            CollectionAssert.AreEqual(
                new[] { "DLC::Winter", "DLC::Summer" },
                rows.Select(r => r.CategoryPath).ToArray());
            Assert.AreEqual(2, rows.Sum(r => r.TotalAchievements));
        }
    }
}
