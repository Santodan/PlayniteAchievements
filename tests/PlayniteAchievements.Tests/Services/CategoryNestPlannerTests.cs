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
