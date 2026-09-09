using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class CategoryPickerResolverTests
    {
        [TestMethod]
        public void BuildOptions_EmitsTreeOrder_WithSubtreesContiguous()
        {
            var options = CategoryPickerResolver.BuildOptions(new[]
            {
                "Story::Chapter 1",
                "Multiplayer",
                "Story",
                "Story::Chapter 2"
            });

            CollectionAssert.AreEqual(
                new[] { "Story", "Story::Chapter 1", "Story::Chapter 2", "Multiplayer" },
                options.Select(option => option.Label).ToList());
        }

        [TestMethod]
        public void BuildOptions_ShowsLeavesAndKeepsFullPathForHover()
        {
            var options = CategoryPickerResolver.BuildOptions(new[] { "Story::Chapter 1" });

            var leaf = options.Single(option => option.Label == "Story::Chapter 1");
            Assert.AreEqual("Chapter 1", leaf.LeafDisplay);
            Assert.AreEqual("Story > Chapter 1", leaf.PathDisplay);
        }

        [TestMethod]
        public void BuildOptions_SynthesizesAMissingAncestor()
        {
            var options = CategoryPickerResolver.BuildOptions(new[] { "Story::Chapter 1" });

            CollectionAssert.AreEqual(
                new[] { "Story", "Story::Chapter 1" },
                options.Select(option => option.Label).ToList());
        }

        [TestMethod]
        public void BuildOptions_MarksSynthesizedAncestorsUnselectable_WhenAskedTo()
        {
            var options = CategoryPickerResolver.BuildOptions(
                new[] { "Story::Chapter 1" },
                synthesizedAreSelectable: false);

            Assert.IsFalse(options.Single(option => option.Label == "Story").IsSelectable);
            Assert.IsTrue(options.Single(option => option.Label == "Story::Chapter 1").IsSelectable);
        }

        [TestMethod]
        public void BuildOptions_KeepsSynthesizedAncestorsSelectable_ByDefault()
        {
            var options = CategoryPickerResolver.BuildOptions(new[] { "Story::Chapter 1" });

            Assert.IsTrue(options.All(option => option.IsSelectable));
        }

        [TestMethod]
        public void BuildOptions_ShapesPlaceEachRowInTheTree()
        {
            var options = CategoryPickerResolver.BuildOptions(new[]
            {
                "Story",
                "Story::Chapter 1",
                "Story::Chapter 2"
            });

            var root = options.Single(option => option.Label == "Story");
            Assert.AreEqual(1, root.TreeShape.Depth);
            Assert.IsTrue(root.TreeShape.HasChildren);
            Assert.IsTrue(root.TreeShape.IsLastSibling);

            var first = options.Single(option => option.Label == "Story::Chapter 1");
            Assert.AreEqual(2, first.TreeShape.Depth);
            Assert.IsFalse(first.TreeShape.HasChildren);
            Assert.IsFalse(first.TreeShape.IsLastSibling);

            var last = options.Single(option => option.Label == "Story::Chapter 2");
            Assert.IsTrue(last.TreeShape.IsLastSibling);
        }

        [TestMethod]
        public void BuildOptions_LeavesShapesNull_WhenNothingNests()
        {
            var options = CategoryPickerResolver.BuildOptions(new[] { "Story", "Multiplayer" });

            Assert.AreEqual(2, options.Count);
            Assert.IsTrue(options.All(option => option.TreeShape == null));
        }

        [TestMethod]
        public void BuildOptions_DedupesCaseInsensitively()
        {
            var options = CategoryPickerResolver.BuildOptions(new[] { "Story", "STORY", "  " });

            Assert.AreEqual(1, options.Count);
        }

        [TestMethod]
        public void Resolve_AdoptsTheOnlyCategoryWithThatLeaf()
        {
            var options = CategoryPickerResolver.BuildOptions(new[] { "Story::Chapter 1" });

            Assert.AreEqual(
                "Story::Chapter 1",
                CategoryPickerResolver.Resolve("Chapter 1", null, options));
        }

        [TestMethod]
        public void Resolve_NeverReturnsAStructuralRow()
        {
            // Only the leaf is a real category; "Story" and "Story::Act 1" are synthesised to
            // carry the tree, so neither is a target this picker can resolve to.
            var options = CategoryPickerResolver.BuildOptions(
                new[] { "Story::Act 1::Chapter 1" },
                synthesizedAreSelectable: false);
            var structural = options.Single(option => option.Label == "Story::Act 1");

            // Typed text matching a structural row's leaf names a new root instead of adopting it.
            Assert.AreEqual("Act 1", CategoryPickerResolver.Resolve("Act 1", null, options));

            // Nor can one be picked outright.
            Assert.AreEqual("Act 1", CategoryPickerResolver.Resolve("Act 1", structural, options));
        }

        [TestMethod]
        public void Resolve_PickedRowWinsOverAnAmbiguousLeaf()
        {
            var options = CategoryPickerResolver.BuildOptions(new[]
            {
                "Story::Chapter 1",
                "Multiplayer::Chapter 1"
            });
            var picked = options.Single(option => option.Label == "Multiplayer::Chapter 1");

            Assert.AreEqual(
                "Multiplayer::Chapter 1",
                CategoryPickerResolver.Resolve("Chapter 1", picked, options));
        }

        [TestMethod]
        public void Resolve_EmptyTextSelectsNothing()
        {
            var options = CategoryPickerResolver.BuildOptions(new[] { "Story" });

            Assert.IsNull(CategoryPickerResolver.Resolve("   ", null, options));
        }
    }
}
