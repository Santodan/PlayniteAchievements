using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CategoryNestPlannerTests
    {
        private static readonly IReadOnlyList<string> Snapshot = new[]
        {
            "A", "A::B", "A::B::C", "D", "E"
        };

        [TestMethod]
        public void PlanNestMoves_NestsRootUnderSiblingRoot()
        {
            var moves = CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "D" }, "E");

            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("D", moves[0].Key);
            Assert.AreEqual("E::D", moves[0].Value);
        }

        [TestMethod]
        public void PlanNestMoves_RejectsSelfAndDescendantTargets()
        {
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A" }, "A").Count);
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A" }, "A::B").Count);
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A" }, "A::B::C").Count);

            // The whole batch is rejected, not just the offending label: a target inside any
            // moving subtree would vanish under the move.
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A", "D" }, "A::B").Count);
        }

        [TestMethod]
        public void PlanNestMoves_RejectsDefaultTargets()
        {
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "D" }, "Default").Count);
            // A Default-rooted path collapses to the root sentinel during normalization, so it is
            // the same rejection.
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "D" }, "Default::X").Count);
        }

        [TestMethod]
        public void PlanNestMoves_DropsDefaultAndUnknownMovingLabels()
        {
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "Default" }, "E").Count);
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "Nope" }, "E").Count);
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { " ", null }, "E").Count);
        }

        [TestMethod]
        public void PlanNestMoves_DescendantOfMovingAncestorIsCarriedNotPlanned()
        {
            var moves = CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A", "A::B" }, "D");

            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("A", moves[0].Key);
            Assert.AreEqual("D::A", moves[0].Value);
        }

        [TestMethod]
        public void PlanNestMoves_SkipsCurrentParentNoOpButKeepsRestOfBatch()
        {
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A::B" }, "A").Count);

            var moves = CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A::B", "D" }, "A");
            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("D", moves[0].Key);
            Assert.AreEqual("A::D", moves[0].Value);
        }

        [TestMethod]
        public void PlanNestMoves_SkipsMovesThatWouldExceedMaxDepth()
        {
            var deepTarget = string.Join("::", Enumerable.Range(1, 7).Select(i => $"T{i}"));
            var snapshot = new List<string> { "X", "X::Y", "Z" };
            snapshot.AddRange(CategoryPathHelperSelfAndAncestors(deepTarget));

            // Depth 7 target + height-2 subtree = 9 > 8: skipped rather than folded.
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(snapshot, new[] { "X" }, deepTarget).Count);

            // Depth 7 target + leaf = exactly 8: allowed.
            var moves = CategoryNestPlanner.PlanNestMoves(snapshot, new[] { "Z" }, deepTarget);
            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual($"{deepTarget}::Z", moves[0].Value);
        }

        [TestMethod]
        public void PlanNestMoves_NullTargetPromotesNestedAndSkipsRoots()
        {
            var moves = CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "A::B" }, null);
            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("A::B", moves[0].Key);
            Assert.AreEqual("B", moves[0].Value);

            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "D" }, null).Count);
        }

        [TestMethod]
        public void PlanNestMoves_MatchesCaseInsensitively()
        {
            var moves = CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "d", "D" }, "e");

            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("E::D", moves[0].Value);
        }

        [TestMethod]
        public void PlanNestMoves_OrdersDeepestFirst()
        {
            var moves = CategoryNestPlanner.PlanNestMoves(Snapshot, new[] { "D", "A::B" }, "E");

            Assert.AreEqual(2, moves.Count);
            Assert.AreEqual("A::B", moves[0].Key);
            Assert.AreEqual("D", moves[1].Key);
        }

        [TestMethod]
        public void PlanNestMoves_SkipsMovesWhoseResultWouldMergeIntoAnExistingLabel()
        {
            var snapshot = new[] { "A", "A::B", "C", "C::B" };

            // A::B under C would land on the existing C::B - a silent merge nobody asked for.
            Assert.AreEqual(0, CategoryNestPlanner.PlanNestMoves(snapshot, new[] { "A::B" }, "C").Count);
        }

        [TestMethod]
        public void PlanNestMoves_SkipsMovesWhoseResultsWouldCollideWithEachOther()
        {
            var snapshot = new[] { "A", "A::X", "B", "B::X", "T" };

            var moves = CategoryNestPlanner.PlanNestMoves(snapshot, new[] { "A::X", "B::X" }, "T");

            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("T::X", moves[0].Value);
        }

        [TestMethod]
        public void PlanGapOrder_SplicesTheDroppedRootBetweenNestedSiblings()
        {
            var rendered = new[] { "A", "A::B", "A::C", "D", "Default" };
            var moves = CategoryNestPlanner.PlanNestMoves(
                new[] { "A", "A::B", "A::C", "D" }, new[] { "D" }, "A");

            var plan = CategoryNestPlanner.PlanGapOrder(rendered, new[] { "D" }, moves, "A::C");

            CollectionAssert.AreEqual(
                new[] { "A", "A::B", "A::D", "A::C", "Default" },
                plan.Order);
            CollectionAssert.AreEqual(new[] { "A::D" }, plan.SelectionRoots);
        }

        [TestMethod]
        public void PlanGapOrder_CarriesTheSubtreeAndAppendsAtEndForNullGap()
        {
            var rendered = new[] { "A", "A::B", "A::B::C", "D" };
            var moves = CategoryNestPlanner.PlanNestMoves(
                new[] { "A", "A::B", "A::B::C", "D" }, new[] { "A::B" }, null);

            var plan = CategoryNestPlanner.PlanGapOrder(rendered, new[] { "A::B" }, moves, null);

            CollectionAssert.AreEqual(new[] { "A", "D", "B", "B::C" }, plan.Order);
            CollectionAssert.AreEqual(new[] { "B" }, plan.SelectionRoots);
        }

        [TestMethod]
        public void PlanGapOrder_KeepsUnmovedDraggedRootsInTheSplicedBlocks()
        {
            // E already sits at the gap's level, so it has no move - but it still travels to the
            // gap with the rest of the drag.
            var rendered = new[] { "A", "A::B", "A::E", "D" };
            var moves = CategoryNestPlanner.PlanNestMoves(
                new[] { "A", "A::B", "A::E", "D" }, new[] { "D", "A::E" }, "A");

            var plan = CategoryNestPlanner.PlanGapOrder(rendered, new[] { "D", "A::E" }, moves, "A::B");

            CollectionAssert.AreEqual(new[] { "A", "A::E", "A::D", "A::B" }, plan.Order);
            CollectionAssert.AreEqual(new[] { "A::E", "A::D" }, plan.SelectionRoots);
        }

        [TestMethod]
        public void PlanStructureResetMoves_ReturnsRowsToProviderParentsKeepingLeafRenames()
        {
            var moves = CategoryNestPlanner.PlanStructureResetMoves(new[]
            {
                // Nested by the user, leaf renamed: goes back under the provider parent as the
                // renamed leaf.
                new KeyValuePair<string, string>("Y::Custom", "P::X"),
                // Leaf renamed in place: structure matches, the name reset owns the rest.
                new KeyValuePair<string, string>("P::Renamed", "P::Z"),
                // User-created: provider identity tracks the label, so it stays put.
                new KeyValuePair<string, string>("Y::New Category", "Y::New Category"),
                new KeyValuePair<string, string>("Default", "Default")
            });

            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("Y::Custom", moves[0].Key);
            Assert.AreEqual("P::Custom", moves[0].Value);
        }

        [TestMethod]
        public void PlanStructureResetMoves_OrdersDeepestFirstAndResolvesIndependently()
        {
            // The user pulled A (with its child) out to the root; both rows deviate and each
            // returns to its own provider parent, child first.
            var moves = CategoryNestPlanner.PlanStructureResetMoves(new[]
            {
                new KeyValuePair<string, string>("A", "P::A"),
                new KeyValuePair<string, string>("A::B", "P::A::B")
            });

            Assert.AreEqual(2, moves.Count);
            Assert.AreEqual("A::B", moves[0].Key);
            Assert.AreEqual("P::A::B", moves[0].Value);
            Assert.AreEqual("A", moves[1].Key);
            Assert.AreEqual("P::A", moves[1].Value);
        }

        [TestMethod]
        public void PlanStructureResetMoves_NeverMergesIntoAStayingOrPlannedLabel()
        {
            var moves = CategoryNestPlanner.PlanStructureResetMoves(new[]
            {
                // Target P::X already exists as a row that is not moving: skipped.
                new KeyValuePair<string, string>("Y::X", "P::X"),
                new KeyValuePair<string, string>("P::X", "P::X"),
                // Two rows resolving to the same result: only the first survives.
                new KeyValuePair<string, string>("Y::W", "Q::W"),
                new KeyValuePair<string, string>("Z::W", "Q::W")
            });

            Assert.AreEqual(1, moves.Count);
            Assert.AreEqual("Q::W", moves[0].Value);
        }

        [TestMethod]
        public void GetSubtreeHeight_CountsDeepestDescendantDistance()
        {
            Assert.AreEqual(1, CategoryNestPlanner.GetSubtreeHeight(Snapshot, "D"));
            Assert.AreEqual(2, CategoryNestPlanner.GetSubtreeHeight(Snapshot, "A::B"));
            Assert.AreEqual(3, CategoryNestPlanner.GetSubtreeHeight(Snapshot, "A"));
        }

        private static IEnumerable<string> CategoryPathHelperSelfAndAncestors(string path)
        {
            return CategoryPathHelper.EnumerateSelfAndAncestors(path);
        }
    }
}
