using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.Tests.Achievements
{
    [TestClass]
    public class UnlockCaptureRarityFilterTests
    {
        [DataTestMethod]
        [DataRow(RarityTier.Common, RarityTier.Common, true)]
        [DataRow(RarityTier.Common, RarityTier.Uncommon, false)]
        [DataRow(RarityTier.Common, RarityTier.Rare, false)]
        [DataRow(RarityTier.Common, RarityTier.UltraRare, false)]
        [DataRow(RarityTier.Uncommon, RarityTier.Common, true)]
        [DataRow(RarityTier.Uncommon, RarityTier.Uncommon, true)]
        [DataRow(RarityTier.Uncommon, RarityTier.Rare, false)]
        [DataRow(RarityTier.Uncommon, RarityTier.UltraRare, false)]
        [DataRow(RarityTier.Rare, RarityTier.Common, true)]
        [DataRow(RarityTier.Rare, RarityTier.Uncommon, true)]
        [DataRow(RarityTier.Rare, RarityTier.Rare, true)]
        [DataRow(RarityTier.Rare, RarityTier.UltraRare, false)]
        [DataRow(RarityTier.UltraRare, RarityTier.Common, true)]
        [DataRow(RarityTier.UltraRare, RarityTier.Uncommon, true)]
        [DataRow(RarityTier.UltraRare, RarityTier.Rare, true)]
        [DataRow(RarityTier.UltraRare, RarityTier.UltraRare, true)]
        public void ShouldCapture_UsesInclusiveMinimumRarity(
            RarityTier rarity,
            RarityTier minimumRarity,
            bool expected)
        {
            var actual = UnlockCaptureRarityFilter.ShouldCapture(
                rarity,
                isCompletionUnlock: false,
                minimumRarity,
                alwaysCaptureCompletion: true);

            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(false, false, true)]
        public void ShouldCapture_CompletionFlagBypassesThresholdWhenEnabled(
            bool isGameCompleted,
            bool isCompletionAchievement,
            bool isCapstone)
        {
            var args = new AchievementUnlockedEventArgs
            {
                RarityTier = RarityTier.Common.ToString(),
                IsGameCompleted = isGameCompleted,
                IsCompletionAchievement = isCompletionAchievement,
                IsCapstone = isCapstone
            };

            Assert.IsTrue(UnlockCaptureRarityFilter.ShouldCapture(
                args,
                RarityTier.UltraRare,
                alwaysCaptureCompletion: true));
        }

        [TestMethod]
        public void ShouldCapture_CompletionDoesNotBypassThresholdWhenDisabled()
        {
            var args = new AchievementUnlockedEventArgs
            {
                RarityTier = RarityTier.Common.ToString(),
                IsCompletionAchievement = true
            };

            Assert.IsFalse(UnlockCaptureRarityFilter.ShouldCapture(
                args,
                RarityTier.UltraRare,
                alwaysCaptureCompletion: false));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("not-a-tier")]
        public void ShouldCapture_MissingOrInvalidRarityCountsAsCommon(string rarity)
        {
            var args = new AchievementUnlockedEventArgs { RarityTier = rarity };

            Assert.IsTrue(UnlockCaptureRarityFilter.ShouldCapture(
                args,
                RarityTier.Common,
                alwaysCaptureCompletion: false));
            Assert.IsFalse(UnlockCaptureRarityFilter.ShouldCapture(
                args,
                RarityTier.Uncommon,
                alwaysCaptureCompletion: false));
        }

        [TestMethod]
        public void ShouldCapture_NullEventArgsReturnsFalse()
        {
            Assert.IsFalse(UnlockCaptureRarityFilter.ShouldCapture(
                args: null,
                RarityTier.Common,
                alwaysCaptureCompletion: true));
        }
    }
}
