using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CategoryNameGeneratorTests
    {
        [TestMethod]
        public void GenerateUniqueLabel_UsesBareBaseNameWhenFree()
        {
            var label = CategoryNameGenerator.GenerateUniqueLabel(
                new[] { "DLC", "Base Game" },
                parentPath: null,
                baseLeafName: "New Category");

            Assert.AreEqual("New Category", label);
        }

        [TestMethod]
        public void GenerateUniqueLabel_SuffixesOnCollision()
        {
            var label = CategoryNameGenerator.GenerateUniqueLabel(
                new[] { "New Category", "New Category (2)" },
                parentPath: null,
                baseLeafName: "New Category");

            Assert.AreEqual("New Category (3)", label);
        }

        [TestMethod]
        public void GenerateUniqueLabel_CollisionsAreCaseInsensitive()
        {
            var label = CategoryNameGenerator.GenerateUniqueLabel(
                new[] { "new category" },
                parentPath: null,
                baseLeafName: "New Category");

            Assert.AreEqual("New Category (2)", label);
        }

        [TestMethod]
        public void GenerateUniqueLabel_UniquenessIsScopedToFullPaths()
        {
            // "DLC" exists at the root; under a parent the bare leaf is still free.
            var nested = CategoryNameGenerator.GenerateUniqueLabel(
                new[] { "DLC", "P" },
                parentPath: "P",
                baseLeafName: "DLC");
            Assert.AreEqual("P::DLC", nested);

            // The sibling copy of an existing nested node gets the suffix.
            var sibling = CategoryNameGenerator.GenerateUniqueLabel(
                new[] { "P", "P::DLC" },
                parentPath: "P",
                baseLeafName: "DLC");
            Assert.AreEqual("P::DLC (2)", sibling);
        }

        [TestMethod]
        public void GenerateUniqueLabel_SanitizesSeparatorBearingBaseNames()
        {
            var label = CategoryNameGenerator.GenerateUniqueLabel(
                new string[0],
                parentPath: null,
                baseLeafName: "A::B");

            Assert.AreEqual("A:B", label);
        }

        [TestMethod]
        public void GenerateUniqueLabel_ReturnsNullForBlankBaseNames()
        {
            Assert.IsNull(CategoryNameGenerator.GenerateUniqueLabel(new string[0], null, null));
            Assert.IsNull(CategoryNameGenerator.GenerateUniqueLabel(new string[0], null, "   "));
        }
    }
}
