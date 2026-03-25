using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class AchievementRarityResolverTests
    {
        [TestMethod]
        public void GetDisplayText_UsesStoredPercentWithoutNormalization()
        {
            var result = AchievementRarityResolver.GetDisplayText(4.25, RarityTier.Common);

            Assert.AreEqual("4.3%", result);
        }

        [TestMethod]
        public void GetDisplayText_UsesRarityWhenPercentMissing()
        {
            var result = AchievementRarityResolver.GetDisplayText(null, RarityTier.Rare);

            Assert.AreEqual("Rare", result);
        }

        [TestMethod]
        public void GetSortValue_UsesPercentBandWhenPresent()
        {
            var result = AchievementRarityResolver.GetSortValue(1.5, RarityTier.UltraRare);

            Assert.AreEqual(1500d, result);
        }

        [TestMethod]
        public void GetSortValue_UsesFallbackWithinBandWhenPercentMissing()
        {
            var result = AchievementRarityResolver.GetSortValue(null, RarityTier.Uncommon);

            Assert.AreEqual(2_999_999d, result);
        }
    }
}
