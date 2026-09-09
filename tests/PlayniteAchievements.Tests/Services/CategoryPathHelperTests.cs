using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CategoryPathHelperTests
    {
        [TestMethod]
        public void NormalizePath_TrimsSegmentsAndDropsEmptyOnes()
        {
            Assert.AreEqual("A", CategoryPathHelper.NormalizePath("A"));
            Assert.AreEqual("A", CategoryPathHelper.NormalizePath(" A "));
            Assert.AreEqual("A::B", CategoryPathHelper.NormalizePath("A::B"));
            Assert.AreEqual("A::B", CategoryPathHelper.NormalizePath("A::::B"));
            Assert.AreEqual("A::B", CategoryPathHelper.NormalizePath("  A  ::  B  "));
            Assert.AreEqual("A", CategoryPathHelper.NormalizePath("::A"));
            Assert.AreEqual("A", CategoryPathHelper.NormalizePath("A::"));
        }

        [TestMethod]
        public void NormalizePath_FallsBackToDefaultWhenNothingRemains()
        {
            Assert.AreEqual("Default", CategoryPathHelper.NormalizePath(null));
            Assert.AreEqual("Default", CategoryPathHelper.NormalizePath(string.Empty));
            Assert.AreEqual("Default", CategoryPathHelper.NormalizePath("   "));
            Assert.AreEqual("Default", CategoryPathHelper.NormalizePath("::"));
        }

        [TestMethod]
        public void NormalizePath_IsIdempotent()
        {
            // The custom-data normalizer runs on both read and write, so a second pass over an
            // already-canonical value must not change it.
            var inputs = new[] { null, "", "A", "A::B", "A::::B", "::A::", "Default::X", "  x :: y " };
            foreach (var input in inputs)
            {
                var once = CategoryPathHelper.NormalizePath(input);
                Assert.AreEqual(once, CategoryPathHelper.NormalizePath(once), $"input [{input ?? "<null>"}]");
            }
        }

        [TestMethod]
        public void NormalizePath_MatchesFlatNormalizationForSeparatorFreeLabels()
        {
            // The degeneracy that makes nesting additive: existing data must be untouched.
            var inputs = new[] { "DLC", " DLC ", "", null, "Trophy Set - Group" };
            foreach (var input in inputs)
            {
                Assert.AreEqual(
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(input),
                    CategoryPathHelper.NormalizePath(input),
                    $"input [{input ?? "<null>"}]");
            }
        }

        [TestMethod]
        public void NormalizePath_CollapsesPathsRootedAtDefault()
        {
            // Default is a root-only sentinel: rename and merge refuse it and it has no provider
            // art, so a child beneath it would be unreachable.
            Assert.AreEqual("Default", CategoryPathHelper.NormalizePath("Default::X"));
            Assert.AreEqual("Default", CategoryPathHelper.NormalizePath("Default::X::Y"));
            Assert.AreEqual("default", CategoryPathHelper.NormalizePath("default::X"), "root casing is preserved");
            Assert.AreEqual("X::Default", CategoryPathHelper.NormalizePath("X::Default"), "only the root is special");
        }

        [TestMethod]
        public void NormalizePath_FoldsOverflowSegmentsRatherThanDroppingThem()
        {
            var deep = string.Join("::", Enumerable.Range(1, 10).Select(i => "s" + i));
            var folded = CategoryPathHelper.NormalizePath(deep);

            Assert.AreEqual(CategoryPathHelper.MaxDepth, CategoryPathHelper.GetDepth(folded));
            Assert.AreEqual("s1", CategoryPathHelper.Split(folded)[0]);
            Assert.AreEqual("s8 - s9 - s10", CategoryPathHelper.GetLeafName(folded));
        }

        [TestMethod]
        public void SplitAndDepth_TreatARootLabelAsOneSegment()
        {
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, CategoryPathHelper.Split("A::B::C").ToArray());
            CollectionAssert.AreEqual(new[] { "A" }, CategoryPathHelper.Split("A").ToArray());
            Assert.AreEqual(1, CategoryPathHelper.GetDepth("A"));
            Assert.AreEqual(3, CategoryPathHelper.GetDepth("A::B::C"));
        }

        [TestMethod]
        public void LeafAndParent_DescribeThePathPosition()
        {
            Assert.AreEqual("C", CategoryPathHelper.GetLeafName("A::B::C"));
            Assert.AreEqual("A", CategoryPathHelper.GetLeafName("A"));
            Assert.AreEqual("A::B", CategoryPathHelper.GetParentPath("A::B::C"));
            Assert.IsNull(CategoryPathHelper.GetParentPath("A"));
        }

        [TestMethod]
        public void AncestorWalks_AreRootFirst()
        {
            CollectionAssert.AreEqual(
                new[] { "A", "A::B", "A::B::C" },
                CategoryPathHelper.EnumerateSelfAndAncestors("A::B::C").ToArray());
            CollectionAssert.AreEqual(new[] { "A" }, CategoryPathHelper.EnumerateSelfAndAncestors("A").ToArray());
            CollectionAssert.AreEqual(new[] { "A", "A::B" }, CategoryPathHelper.EnumerateAncestors("A::B::C").ToArray());
            Assert.AreEqual(0, CategoryPathHelper.EnumerateAncestors("A").Count);
        }

        [TestMethod]
        public void IsDescendantOf_ComparesOnTheSeparatorBoundary()
        {
            // The trap this separator exists to avoid: a plain StartsWith would call both true.
            Assert.IsFalse(CategoryPathHelper.IsDescendantOf("AB", "A"));
            Assert.IsFalse(CategoryPathHelper.IsDescendantOf("A::BC", "A::B"));

            Assert.IsTrue(CategoryPathHelper.IsDescendantOf("A::B", "A"));
            Assert.IsTrue(CategoryPathHelper.IsDescendantOf("A::B::C", "A"));
        }

        [TestMethod]
        public void IsDescendantOf_IsStrictAndCaseInsensitive()
        {
            Assert.IsFalse(CategoryPathHelper.IsDescendantOf("A", "A"));
            Assert.IsTrue(CategoryPathHelper.IsSelfOrDescendantOf("A", "A"));
            Assert.IsTrue(CategoryPathHelper.IsSame("a::b", "A::B"));
            Assert.IsTrue(CategoryPathHelper.IsDescendantOf("a::b", "A"));
        }

        [TestMethod]
        public void RewritePrefix_RepointsAWholeSubtree()
        {
            Assert.AreEqual("Z", CategoryPathHelper.RewritePrefix("A", "A", "Z"));
            Assert.AreEqual("Z::B", CategoryPathHelper.RewritePrefix("A::B", "A", "Z"));
            Assert.AreEqual("Z::B::C", CategoryPathHelper.RewritePrefix("A::B::C", "A", "Z"));
            Assert.AreEqual("A::Q::C", CategoryPathHelper.RewritePrefix("A::B::C", "A::B", "A::Q"));
            Assert.AreEqual("P::Q::B", CategoryPathHelper.RewritePrefix("A::B", "A", "P::Q"));
        }

        [TestMethod]
        public void RewritePrefix_LeavesUnrelatedPathsAlone()
        {
            Assert.AreEqual("X::Y", CategoryPathHelper.RewritePrefix("X::Y", "A", "Z"));
            Assert.AreEqual("AB", CategoryPathHelper.RewritePrefix("AB", "A", "Z"));
        }

        [TestMethod]
        public void Reparent_KeepsTheLeafName()
        {
            Assert.AreEqual("X::B", CategoryPathHelper.Reparent("A::B", "X"));
            Assert.AreEqual("X::Y::C", CategoryPathHelper.Reparent("A::B::C", "X::Y"));
            Assert.AreEqual("B", CategoryPathHelper.Reparent("A::B", null), "a null parent makes it a root");
        }

        [TestMethod]
        public void Join_NormalizesItsParts()
        {
            Assert.AreEqual("A::B", CategoryPathHelper.Join(new[] { "A", "B" }));
            Assert.AreEqual("A::B", CategoryPathHelper.Join(new[] { "A", string.Empty, "B" }));
            Assert.AreEqual("A::B", CategoryPathHelper.Join("A", "B"));
            Assert.AreEqual("B", CategoryPathHelper.Join(null, "B"));
        }

        [TestMethod]
        public void GetChildPaths_ReturnsImmediateChildrenInFirstSeenOrder()
        {
            var labels = new[] { "DLC::Winter", "DLC::Summer", "Multiplayer", "DLC::Winter::Week1", "Default" };

            CollectionAssert.AreEqual(
                new[] { "DLC", "Multiplayer", "Default" },
                CategoryPathHelper.GetChildPaths(labels, null).ToArray());

            CollectionAssert.AreEqual(
                new[] { "DLC::Winter", "DLC::Summer" },
                CategoryPathHelper.GetChildPaths(labels, "DLC").ToArray());

            CollectionAssert.AreEqual(
                new[] { "DLC::Winter::Week1" },
                CategoryPathHelper.GetChildPaths(labels, "DLC::Winter").ToArray());
        }

        [TestMethod]
        public void GetChildPaths_SynthesizesIntermediateNodesWithNoAchievementsOfTheirOwn()
        {
            // "DLC" holds nothing itself; it must still surface as a node.
            var labels = new[] { "DLC::Winter::Week1" };
            CollectionAssert.AreEqual(new[] { "DLC" }, CategoryPathHelper.GetChildPaths(labels, null).ToArray());
            CollectionAssert.AreEqual(new[] { "DLC::Winter" }, CategoryPathHelper.GetChildPaths(labels, "DLC").ToArray());
        }

        [TestMethod]
        public void GetChildPaths_IsEmptyForLeavesUnknownParentsAndNullInput()
        {
            var labels = new[] { "DLC::Winter", "Multiplayer" };
            Assert.AreEqual(0, CategoryPathHelper.GetChildPaths(labels, "Multiplayer").Count);
            Assert.AreEqual(0, CategoryPathHelper.GetChildPaths(labels, "Nope").Count);
            Assert.AreEqual(0, CategoryPathHelper.GetChildPaths(null, null).Count);
        }

        [TestMethod]
        public void ContainsSeparator_DetectsOnlyTheFullSeparator()
        {
            Assert.IsTrue(CategoryPathHelper.ContainsSeparator("A::B"));
            Assert.IsFalse(CategoryPathHelper.ContainsSeparator("A:B"));
            Assert.IsFalse(CategoryPathHelper.ContainsSeparator(null));
        }

        [TestMethod]
        public void DisplayForms_RenderTheSeparatorWithoutParsingItBack()
        {
            Assert.AreEqual("A > B > C", CategoryPathHelper.ToDisplayPath("A::B::C"));
            Assert.AreEqual("A", CategoryPathHelper.ToDisplayPath("A"));
            Assert.AreEqual("B", CategoryPathHelper.ToDisplayLeaf("A::B"));

            // A pre-existing flat label that happens to contain the display form stays flat.
            Assert.AreEqual(1, CategoryPathHelper.GetDepth("Trophy Set > Group"));
        }
    }
}
