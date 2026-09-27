using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Covers what the auto capstone works out from the achievements it stands for. These are the
    /// rules both the editor and the post-refresh maintenance run, so a drift between the two would
    /// show up as one of these failing.
    /// </summary>
    [TestClass]
    public class AutoCapstoneCalculatorTests
    {
        [TestMethod]
        public void Derive_NoAchievements_YieldsNothingToStandFor()
        {
            Assert.IsNull(AutoCapstoneCalculator.Derive(null));
            Assert.IsNull(AutoCapstoneCalculator.Derive(new List<AchievementDetail>()));
        }

        [TestMethod]
        public void Derive_RarityTakesTheRarestAchievement()
        {
            // Finishing a game is at least as hard as its hardest single step.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", percent: 48.5),
                Achievement("b", percent: 3.2),
                Achievement("c", percent: 71)
            });

            Assert.AreEqual(3.2, derived.GlobalPercentUnlocked);
            Assert.AreEqual(RarityTier.UltraRare.ToString(), derived.Rarity);
        }

        [TestMethod]
        public void Derive_IgnoresPercentagesNoProviderReported()
        {
            // A zero reads as "never filled in" rather than "nobody has it".
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", percent: 0),
                Achievement("b", percent: 40)
            });

            Assert.AreEqual(40, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void Derive_LocksWhileAnythingItStandsForIsLocked()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", unlocked: true, unlockTimeUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Achievement("b")
            });

            Assert.IsFalse(derived.Unlocked);
            Assert.IsNull(derived.UnlockTimeUtc);
        }

        [TestMethod]
        public void Derive_UnlocksWithTheGameAtTheMomentItWasFinished()
        {
            var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var last = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);

            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", unlocked: true, unlockTimeUtc: first),
                Achievement("b", unlocked: true, unlockTimeUtc: last)
            });

            Assert.IsTrue(derived.Unlocked);
            Assert.AreEqual(last, derived.UnlockTimeUtc);
        }

        [TestMethod]
        public void Derive_DlcNeitherHoldsTheCapstoneBackNorSetsItsRarity()
        {
            // A platinum is not withheld for DLC, so neither is this.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("base", unlocked: true, percent: 30, categoryType: "Base",
                    unlockTimeUtc: new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)),
                Achievement("dlc", unlocked: false, percent: 1, categoryType: "DLC")
            });

            Assert.IsTrue(derived.Unlocked);
            Assert.AreEqual(30, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void Derive_UngroupedAchievementsAllCount()
        {
            // A provider that does not group its achievements leaves every one of them in scope.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", unlocked: true, percent: 30),
                Achievement("b", unlocked: false, percent: 1)
            });

            Assert.IsFalse(derived.Unlocked);
            Assert.AreEqual(1, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void Derive_DlcMarkedByHandStillDropsOut()
        {
            // No provider grouped this game, so nothing is marked as the base game -- but the user
            // marked the DLC, and that has to be worth something.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", unlocked: true, percent: 30,
                    unlockTimeUtc: new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)),
                Achievement("dlc", unlocked: false, percent: 1, categoryType: "DLC")
            });

            Assert.IsTrue(derived.Unlocked);
            Assert.AreEqual(30, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void Derive_SubsetsDropOutTheSameWay()
        {
            // RetroAchievements types its extra sets Subset rather than DLC.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", unlocked: true, percent: 30,
                    unlockTimeUtc: new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)),
                Achievement("bonus", unlocked: false, percent: 1, categoryType: "Subset")
            });

            Assert.IsTrue(derived.Unlocked);
            Assert.AreEqual(30, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void Derive_BaseGameUpdatesAreStillTheBaseGame()
        {
            // SteamHunters pairs Update with whichever group owns it.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", unlocked: true, percent: 30, categoryType: "Base"),
                Achievement("patch", unlocked: false, percent: 8, categoryType: "Base|Update")
            });

            Assert.IsFalse(derived.Unlocked);
            Assert.AreEqual(8, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void Derive_EverythingMarkedElsewhereFallsBackToTheWholeList()
        {
            // Nothing is left to stand for otherwise, and no capstone at all is the worse answer.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("dlc1", unlocked: true, percent: 12, categoryType: "DLC",
                    unlockTimeUtc: new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc)),
                Achievement("dlc2", unlocked: true, percent: 40, categoryType: "DLC",
                    unlockTimeUtc: new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc))
            });

            Assert.IsTrue(derived.Unlocked);
            Assert.AreEqual(12, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void Derive_InheritsTheOneCategoryTheyAllSitIn()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", category: "Story"),
                Achievement("b", category: "Story")
            });

            Assert.AreEqual("Story", derived.Category);
        }

        [TestMethod]
        public void Derive_InheritsTheBaseGameCategoryRatherThanTheDlcOne()
        {
            // The scope is the base game's, so the category it agrees on is the base game's too.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", categoryType: "Base", category: "Base Game"),
                Achievement("dlc", categoryType: "DLC", category: "Expansion")
            });

            Assert.AreEqual("Base Game", derived.Category);
        }

        [TestMethod]
        public void Derive_InheritsNothingWhenTheyAreSpreadAround()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", category: "Story"),
                Achievement("b", category: "Combat")
            });

            Assert.IsNull(derived.Category);
        }

        [TestMethod]
        public void Derive_InheritsNothingFromTheDefaultBucket()
        {
            // Everything unsorted is not a category to inherit.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a"),
                Achievement("b")
            });

            Assert.IsNull(derived.Category);
        }

        [TestMethod]
        public void Derive_LeavesFilteredAchievementsOut()
        {
            // A filtered achievement is out of the counts completion reads, so the capstone must
            // not wait on it either.
            var filtered = Achievement("b");
            filtered.IsFiltered = true;
            var summaryFiltered = Achievement("c", percent: 0.5);
            summaryFiltered.IsFilteredFromSummaries = true;

            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", unlocked: true, percent: 20, unlockTimeUtc: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
                filtered,
                summaryFiltered
            });

            Assert.IsTrue(derived.Unlocked);
            Assert.AreEqual(20, derived.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void DeriveForCapstone_CategoryCapstoneReadsItsScopeFromItsOwnRow()
        {
            // The definition only ever carries the default category; the filing the user chose is
            // on the hydrated row, and that is the category the capstone stands for.
            var capstone = Achievement("custom:auto-capstone", category: "Story");
            var derived = AutoCapstoneCalculator.DeriveForCapstone(
                new[]
                {
                    Achievement("story", unlocked: true, category: "Story"),
                    Achievement("extra", category: "Extras"),
                    capstone
                },
                "custom:auto-capstone",
                isWholeGame: false,
                storedCategory: "Default");

            Assert.IsNotNull(derived);
            Assert.IsTrue(derived.Unlocked);
        }

        [TestMethod]
        public void DeriveForCapstone_WholeGameCapstoneIgnoresWhereItIsFiled()
        {
            var derived = AutoCapstoneCalculator.DeriveForCapstone(
                new[]
                {
                    Achievement("story", unlocked: true, category: "Story"),
                    Achievement("extra", category: "Extras"),
                    Achievement("custom:auto-capstone", category: "Story")
                },
                "custom:auto-capstone",
                isWholeGame: true);

            Assert.IsNotNull(derived);
            Assert.IsFalse(derived.Unlocked);
        }

        [TestMethod]
        public void DeriveForCapstone_NeverWaitsOnItself()
        {
            var derived = AutoCapstoneCalculator.DeriveForCapstone(
                new[]
                {
                    Achievement("a", unlocked: true),
                    Achievement("custom:auto-capstone")
                },
                "custom:auto-capstone",
                isWholeGame: true);

            Assert.IsTrue(derived.Unlocked);
        }

        private static AchievementDetail Achievement(
            string apiName,
            bool unlocked = false,
            double? percent = null,
            string categoryType = null,
            DateTime? unlockTimeUtc = null,
            string category = null)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = apiName,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTimeUtc,
                GlobalPercentUnlocked = percent,
                CategoryType = categoryType,
                Category = category
            };
        }
    }
}
