using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers the options behind the editor's category filter drop-down: it renders the shared tree
    /// template, which draws nothing unless the options carry connector shapes.
    /// </summary>
    [TestClass]
    public class EditorCategoryFilterOptionsTests
    {
        [TestMethod]
        public void NestedCategories_CarryTreeShapes()
        {
            var options = CategoryPickerResolver.BuildOptions(
                new[] { "Story::Act One", "Story::Act Two", "Collectibles" },
                preferredOrder: null,
                synthesizedAreSelectable: false);

            Assert.IsTrue(
                options.Any(option => option.TreeShape != null),
                "A nested set must carry shapes or the drop-down draws no tree.");
        }

        [TestMethod]
        public void SynthesizedAncestors_AreDrawnButNotSelectable()
        {
            // "Story" itself holds no achievements, so filtering on it would match nothing; it
            // exists only so its children have a parent to hang off.
            var options = CategoryPickerResolver.BuildOptions(
                new[] { "Story::Act One", "Story::Act Two" },
                preferredOrder: null,
                synthesizedAreSelectable: false);

            var story = options.SingleOrDefault(option => option.Label == "Story");
            Assert.IsNotNull(story, "The ancestor must still be emitted so the tree reads.");
            Assert.IsFalse(story.IsSelectable);
        }

        [TestMethod]
        public void FlatCategories_CarryNoShapes()
        {
            // Documented behaviour: with no nesting the guide collapses to zero width, so a flat
            // list looks exactly as it did before there was a tree to draw.
            var options = CategoryPickerResolver.BuildOptions(
                new[] { "Story", "Collectibles", "Multiplayer" },
                preferredOrder: null,
                synthesizedAreSelectable: false);

            Assert.IsTrue(options.All(option => option.TreeShape == null));
        }
    }
}
