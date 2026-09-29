using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Search;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers the text an editor row is matched on when the grid is narrowed. The editor builds its
    /// index from the same builder the other achievement lists use, so a row is findable by name,
    /// description or ApiName.
    /// </summary>
    [TestClass]
    public class AchievementEditorFilterTests
    {
        private static bool Matches(string displayName, string description, string apiName, string filter)
        {
            var index = new SearchTextIndex<string>(key => SearchTextBuilder.ForManualEdit(
                displayName,
                description,
                apiName));

            return index.Matches("row", SearchQuery.From(filter));
        }

        [TestMethod]
        public void BlankFilter_MatchesEverything()
        {
            Assert.IsTrue(Matches("A Concrete Example", "Build foundations", "BUILD_FOUNDATIONS", null));
            Assert.IsTrue(Matches("A Concrete Example", "Build foundations", "BUILD_FOUNDATIONS", "   "));
        }

        [TestMethod]
        public void MatchesOnDisplayName_CaseInsensitively()
        {
            Assert.IsTrue(Matches("A Concrete Example", "Build foundations", "BUILD_FOUNDATIONS", "concrete"));
        }

        [TestMethod]
        public void MatchesOnDescription()
        {
            Assert.IsTrue(Matches("A Concrete Example", "Build foundations", "BUILD_FOUNDATIONS", "foundations"));
        }

        [TestMethod]
        public void MatchesOnApiName()
        {
            // The ApiName is what a user has to search by when two achievements share a title.
            Assert.IsTrue(Matches("A Concrete Example", "Build foundations", "BUILD_FOUNDATIONS", "BUILD_"));
        }

        [TestMethod]
        public void NonMatchingText_IsExcluded()
        {
            Assert.IsFalse(Matches("A Concrete Example", "Build foundations", "BUILD_FOUNDATIONS", "platinum"));
        }
    }
}
