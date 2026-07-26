using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    [DoNotParallelize]
    public class AchievementRarityResolverTests
    {
        // Percent output depends on the plugin formatting culture; pin it for determinism.
        [TestInitialize]
        public void PinFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
        }

        [TestCleanup]
        public void ResetRounding()
        {
            AchievementRarityResolver.RoundDisplayPercentages = false;
            FormattingCulture.Initialize(() => "english");
        }

        private static void WithRounding(Action assertions)
        {
            AchievementRarityResolver.RoundDisplayPercentages = true;
            try
            {
                assertions();
            }
            finally
            {
                AchievementRarityResolver.RoundDisplayPercentages = false;
            }
        }

        private static void WithGerman(Action assertions)
        {
            FormattingCulture.Initialize(() => "german");
            try
            {
                assertions();
            }
            finally
            {
                FormattingCulture.Initialize(() => "english");
            }
        }

        [TestMethod]
        public void GetDisplayText_UsesStoredPercentWithoutNormalization()
        {
            var result = AchievementRarityResolver.GetDisplayText(4.25, RarityTier.Common);

            Assert.AreEqual("4.3%", result);
        }

        [TestMethod]
        public void FormatPercent_DisabledKeepsOneDecimalPlace()
        {
            Assert.AreEqual("12.3%", AchievementRarityResolver.FormatPercent(12.34));
            Assert.AreEqual("0.4%", AchievementRarityResolver.FormatPercent(0.44));
        }

        [TestMethod]
        public void FormatPercent_EnabledRoundsToNearestWholePercent()
        {
            WithRounding(() =>
            {
                Assert.AreEqual("12%", AchievementRarityResolver.FormatPercent(12.34));
                Assert.AreEqual("13%", AchievementRarityResolver.FormatPercent(12.5));
                Assert.AreEqual("87%", AchievementRarityResolver.FormatPercent(86.7));
                Assert.AreEqual("1%", AchievementRarityResolver.FormatPercent(1.0));
            });
        }

        [TestMethod]
        public void FormatPercent_EnabledShowsUnderOnePercentAsLessThanOne()
        {
            WithRounding(() =>
            {
                Assert.AreEqual("<1%", AchievementRarityResolver.FormatPercent(0.99));
                Assert.AreEqual("<1%", AchievementRarityResolver.FormatPercent(0.5));
                Assert.AreEqual("<1%", AchievementRarityResolver.FormatPercent(0.0));
            });
        }

        [TestMethod]
        public void FormatWholePercent_DisabledKeepsWholePercentFormat()
        {
            Assert.AreEqual("12%", AchievementRarityResolver.FormatWholePercent(12.34));
            Assert.AreEqual("0%", AchievementRarityResolver.FormatWholePercent(0.4));
        }

        [TestMethod]
        public void FormatWholePercent_EnabledShowsUnderOnePercentAsLessThanOne()
        {
            WithRounding(() =>
            {
                Assert.AreEqual("<1%", AchievementRarityResolver.FormatWholePercent(0.4));
                Assert.AreEqual("12%", AchievementRarityResolver.FormatWholePercent(12.34));
            });
        }

        [TestMethod]
        public void GetDisplayText_EnabledRoundsAndUsesLessThanOnePercent()
        {
            WithRounding(() =>
            {
                Assert.AreEqual("12%", AchievementRarityResolver.GetDisplayText(12.34, RarityTier.Common));
                Assert.AreEqual("<1%", AchievementRarityResolver.GetDisplayText(0.42, RarityTier.UltraRare));
            });
        }

        [TestMethod]
        public void GetDetailText_EnabledRoundsPercentAndKeepsTierName()
        {
            WithRounding(() =>
            {
                Assert.AreEqual("<1% - Ultra Rare", AchievementRarityResolver.GetDetailText(0.42, RarityTier.UltraRare));
                Assert.AreEqual("12% - Rare", AchievementRarityResolver.GetDetailText(12.34, RarityTier.Rare));
            });
        }

        [TestMethod]
        public void FormatPercent_GermanUsesCommaAndSpacedPercentSign()
        {
            WithGerman(() =>
            {
                Assert.AreEqual("12,3 %", AchievementRarityResolver.FormatPercent(12.34));
                Assert.AreEqual("100,0 %", AchievementRarityResolver.FormatPercent(100.0));
            });
        }

        [TestMethod]
        public void FormatPercent_GermanRoundedShowsSpacedLessThanOne()
        {
            WithGerman(() => WithRounding(() =>
            {
                Assert.AreEqual("< 1 %", AchievementRarityResolver.FormatPercent(0.5));
                Assert.AreEqual("12 %", AchievementRarityResolver.FormatPercent(12.34));
            }));
        }

        [TestMethod]
        public void GetSortValue_IgnoresDisplayRounding()
        {
            WithRounding(() =>
            {
                Assert.AreEqual(1500d, AchievementRarityResolver.GetSortValue(1.5, RarityTier.UltraRare));
            });
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
