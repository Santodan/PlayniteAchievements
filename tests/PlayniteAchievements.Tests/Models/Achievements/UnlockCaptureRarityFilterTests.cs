using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.Tests.Achievements
{
    [TestClass]
    public class UnlockCaptureRarityFilterTests
    {
        [DataTestMethod]
        [DataRow(RarityTier.Common, RaritySelection.Common, true)]
        [DataRow(RarityTier.Common, RaritySelection.UltraRare, false)]
        [DataRow(RarityTier.Rare, RaritySelection.Rare | RaritySelection.UltraRare, true)]
        [DataRow(RarityTier.Uncommon, RaritySelection.Rare | RaritySelection.UltraRare, false)]
        [DataRow(RarityTier.UltraRare, RaritySelection.All, true)]
        [DataRow(RarityTier.Common, RaritySelection.None, false)]
        public void ShouldCapture_UsesSetMembership(
            RarityTier rarity,
            RaritySelection selectedRarities,
            bool expected)
        {
            var actual = UnlockCaptureRarityFilter.ShouldCapture(
                rarity,
                isCompletionUnlock: false,
                selectedRarities,
                alwaysCaptureCompletion: true);

            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void ShouldCapture_EmptySelectionCapturesNothingByRarity()
        {
            foreach (RarityTier tier in System.Enum.GetValues(typeof(RarityTier)))
            {
                Assert.IsFalse(UnlockCaptureRarityFilter.ShouldCapture(
                    tier,
                    isCompletionUnlock: false,
                    RaritySelection.None,
                    alwaysCaptureCompletion: false));
            }
        }

        [DataTestMethod]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(false, false, true)]
        public void ShouldCapture_CompletionFlagBypassesEmptySetWhenEnabled(
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

            // Even with no rarities selected, a completion unlock is captured when the bypass is on.
            Assert.IsTrue(UnlockCaptureRarityFilter.ShouldCapture(
                args,
                RaritySelection.None,
                alwaysCaptureCompletion: true));
        }

        [TestMethod]
        public void ShouldCapture_CompletionDoesNotBypassSetWhenDisabled()
        {
            var args = new AchievementUnlockedEventArgs
            {
                RarityTier = RarityTier.Common.ToString(),
                IsCompletionAchievement = true
            };

            Assert.IsFalse(UnlockCaptureRarityFilter.ShouldCapture(
                args,
                RaritySelection.UltraRare,
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
                RaritySelection.Common,
                alwaysCaptureCompletion: false));
            Assert.IsFalse(UnlockCaptureRarityFilter.ShouldCapture(
                args,
                RaritySelection.Uncommon,
                alwaysCaptureCompletion: false));
        }

        [TestMethod]
        public void ShouldCapture_NullEventArgsReturnsFalse()
        {
            Assert.IsFalse(UnlockCaptureRarityFilter.ShouldCapture(
                args: null,
                RaritySelection.All,
                alwaysCaptureCompletion: true));
        }

        [DataTestMethod]
        [DataRow(RarityTier.Common, RaritySelection.Common)]
        [DataRow(RarityTier.Uncommon, RaritySelection.Uncommon)]
        [DataRow(RarityTier.Rare, RaritySelection.Rare)]
        [DataRow(RarityTier.UltraRare, RaritySelection.UltraRare)]
        public void ToFlag_MapsEachTierToItsBit(RarityTier tier, RaritySelection expected)
        {
            Assert.AreEqual(expected, tier.ToFlag());
            Assert.IsTrue(expected.Contains(tier));
        }
    }
}
