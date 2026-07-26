using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsSubsetTitleResolverTests
    {
        [TestMethod]
        public void ExtractBaseTitle_StripsSubsetSuffix()
        {
            var title = "Pokemon FireRed Version | Pokemon LeafGreen Version [Subset - Shiny Pokemon+]";

            var baseTitle = RetroAchievementsSubsetTitleResolver.ExtractBaseTitle(title);

            Assert.AreEqual("Pokemon FireRed Version | Pokemon LeafGreen Version", baseTitle);
        }

        [TestMethod]
        public void ExtractBaseTitle_StripsPlainSubsetSuffix()
        {
            var title = "Pokemon FireRed Version | Pokemon LeafGreen Version Subset Shiny Pokemon+";

            var baseTitle = RetroAchievementsSubsetTitleResolver.ExtractBaseTitle(title);

            Assert.AreEqual("Pokemon FireRed Version | Pokemon LeafGreen Version", baseTitle);
        }

        [TestMethod]
        public void IsSubsetLikeTitle_DetectsPlainSubsetSuffix()
        {
            Assert.IsTrue(RetroAchievementsSubsetTitleResolver.IsSubsetLikeTitle(
                "Pokemon FireRed Version | Pokemon LeafGreen Version Subset Shiny Pokemon+"));
        }

        [TestMethod]
        public void ExtractAlternateBaseTitleCandidates_SplitsPipeSeparatedAliases()
        {
            var baseTitle = "Pokemon FireRed Version | Pokemon LeafGreen Version";

            var candidates = RetroAchievementsSubsetTitleResolver.ExtractAlternateBaseTitleCandidates(baseTitle);

            CollectionAssert.AreEqual(
                new[]
                {
                    "Pokemon FireRed Version",
                    "Pokemon LeafGreen Version"
                },
                (System.Collections.ICollection)candidates);
        }

        [TestMethod]
        public void ExtractAlternateBaseTitleCandidates_PropagatesSharedSeriesPrefix()
        {
            var baseTitle = "The Legend of Zelda: Oracle of Ages | Oracle of Seasons";

            var candidates = RetroAchievementsSubsetTitleResolver.ExtractAlternateBaseTitleCandidates(baseTitle);

            CollectionAssert.AreEqual(
                new[]
                {
                    "The Legend of Zelda: Oracle of Ages",
                    "Oracle of Seasons",
                    "The Legend of Zelda: Oracle of Seasons"
                },
                (System.Collections.ICollection)candidates);
        }

        [TestMethod]
        public void ExtractAlternateBaseTitleCandidates_ReturnsEmptyForSingleTitle()
        {
            var candidates = RetroAchievementsSubsetTitleResolver.ExtractAlternateBaseTitleCandidates("Pokemon LeafGreen Version");

            Assert.AreEqual(0, candidates.Count);
        }
    }
}
