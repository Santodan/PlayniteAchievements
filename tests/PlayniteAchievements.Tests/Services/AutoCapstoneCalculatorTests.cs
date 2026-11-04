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
        public void Derive_FilesInTheFirstCategoryWhenTheyAreSpreadAround()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", category: "Story"),
                Achievement("b", category: "Combat")
            });

            Assert.AreEqual("Story", derived.Category);
        }

        [TestMethod]
        public void Derive_FirstCategoryFollowsTheGamesCategoryOrder()
        {
            var derived = AutoCapstoneCalculator.Derive(
                new[]
                {
                    Achievement("a", categoryType: "Base", category: "Story"),
                    Achievement("b", categoryType: "Base", category: "Combat"),
                    Achievement("dlc", categoryType: "DLC", category: "Expansion")
                },
                categoryOrder: new[] { "Expansion", "Combat", "Story" });

            Assert.AreEqual("Combat", derived.Category);
        }

        [TestMethod]
        public void Derive_StaysInDefaultWhenTheDefaultBucketComesFirst()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a"),
                Achievement("b", category: "Story")
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

        [TestMethod]
        public void Derive_FilesWithTheMainGameWhenUpdatesHaveTheirOwnCategories()
        {
            // SteamHunters files each post-launch update group under its own label, typed
            // Base|Update, so the base game spans several categories. The capstone belongs with
            // the rows that are the base game and not an update, not in an empty default bucket.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", categoryType: "Base", category: "Terraria"),
                Achievement("b", categoryType: "Base", category: "Terraria"),
                Achievement("c", categoryType: "Base|Update", category: "Journey's End"),
                Achievement("d", categoryType: "DLC", category: "Expansion")
            });

            Assert.AreEqual("Terraria", derived.Category);
        }

        [TestMethod]
        public void DeriveForCapstone_CategoryCapstoneAloneInItsCategoryStandsForTheWholeGame()
        {
            // Authored before the scope was stored, then left behind in the default category when
            // a provider moved everything else into named categories.
            var achievements = new[]
            {
                Achievement("a", unlocked: true, category: "Main"),
                Achievement("b", category: "Main"),
                Achievement("custom:old", category: "Default")
            };

            var derived = AutoCapstoneCalculator.DeriveForCapstone(
                achievements,
                "custom:old",
                isWholeGame: false,
                storedCategory: "Default");

            Assert.IsNotNull(derived);
            Assert.IsFalse(derived.Unlocked);
        }

        [TestMethod]
        public void DeriveForCapstone_FinishedCategoryBesideUnfinishedUntypedOne_DependsOnScope()
        {
            // The reported shape: a capstone sitting in a finished "Base Game" next to an unfinished
            // category the user drew up for DLC, with no group type to tell it apart. Standing for the
            // whole game it waits on the DLC; standing for its category it is earned.
            var achievements = new[]
            {
                Achievement("a", unlocked: true, category: "Base Game"),
                Achievement("b", unlocked: true, category: "Base Game"),
                Achievement("c", category: "DLC"),
                Achievement("custom:auto-capstone", category: "Base Game")
            };

            var wholeGame = AutoCapstoneCalculator.DeriveForCapstone(
                achievements,
                "custom:auto-capstone",
                isWholeGame: true);
            var category = AutoCapstoneCalculator.DeriveForCapstone(
                achievements,
                "custom:auto-capstone",
                isWholeGame: false);

            Assert.IsFalse(wholeGame.Unlocked);
            Assert.IsTrue(category.Unlocked);
        }

        [TestMethod]
        public void Derive_TakesTheBaseGameKind()
        {
            // A PSN game gives its base trophies the kind Base; the capstone shares it.
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", categoryType: "Base"),
                Achievement("b", categoryType: "Base|Missable"),
                Achievement("dlc", categoryType: "DLC")
            });

            Assert.AreEqual("Base", derived.CategoryKind);
        }

        [TestMethod]
        public void Derive_KindLeavesOutUpdate()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", categoryType: "Base|Update"),
                Achievement("b", categoryType: "Base|Update"),
                Achievement("c", categoryType: "Base")
            });

            Assert.AreEqual("Base", derived.CategoryKind);
        }

        [TestMethod]
        public void Derive_DlcCategoryTakesTheDlcKind()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a", categoryType: "Base", category: "Main"),
                Achievement("dlc1", categoryType: "DLC", category: "Expansion"),
                Achievement("dlc2", categoryType: "DLC", category: "Expansion")
            }, "Expansion");

            Assert.AreEqual("DLC", derived.CategoryKind);
        }

        [TestMethod]
        public void Derive_UngroupedGameHasNoKind()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Achievement("a"),
                Achievement("b", categoryType: "Missable")
            });

            Assert.IsNull(derived.CategoryKind);
        }

        [TestMethod]
        public void Derive_TagsAreTheUnionOfWhatItStandsFor()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Tagged("a", "Missable|Collectable"),
                Tagged("b", "collectible|Challenge"),
                Tagged("c", null)
            });

            CollectionAssert.AreEquivalent(
                new[] { "Missable", "Collectable", "Challenge" },
                new List<string>(AchievementTagHelper.GetComponents(derived.Tags)));
        }

        [TestMethod]
        public void Derive_TagsLeaveOutDerivedKeys()
        {
            var derived = AutoCapstoneCalculator.Derive(new[]
            {
                Tagged("a", "Hardcore|Challenge"),
                Tagged("b", "Softcore")
            });

            Assert.AreEqual("Challenge", derived.Tags);
        }

        [TestMethod]
        public void Derive_NoTagsYieldsNull()
        {
            Assert.IsNull(AutoCapstoneCalculator.Derive(new[] { Achievement("a"), Achievement("b") }).Tags);
        }

        [TestMethod]
        public void Derive_TagsComeOnlyFromTheScope()
        {
            var dlc = Tagged("dlc", "Multiplayer");
            dlc.CategoryKind = CategoryKindHelper.Dlc;
            var filtered = Tagged("filtered", "Miscellaneous");
            filtered.IsFiltered = true;

            var derived = AutoCapstoneCalculator.Derive(new[] { Tagged("a", "Challenge"), dlc, filtered });

            Assert.AreEqual("Challenge", derived.Tags);
        }

        [TestMethod]
        public void Derive_CategoryCapstoneTakesOnlyItsCategorysTags()
        {
            var main = Tagged("a", "Challenge");
            main.Category = "Main";
            var extra = Tagged("b", "PostGame");
            extra.Category = "Extra";

            Assert.AreEqual("PostGame", AutoCapstoneCalculator.Derive(new[] { main, extra }, "Extra").Tags);
        }

        [TestMethod]
        public void DeriveForCapstone_IgnoresTheCapstonesOwnTags()
        {
            var derived = AutoCapstoneCalculator.DeriveForCapstone(
                new[] { Tagged("a", "Challenge"), Tagged("capstone", "Missable") },
                "capstone",
                isWholeGame: true);

            Assert.AreEqual("Challenge", derived.Tags);
        }

        private static AchievementDetail Tagged(string apiName, string tags)
        {
            var achievement = Achievement(apiName);
            achievement.Tags = tags;
            return achievement;
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
                CategoryKind = PlayniteAchievements.Services.Achievements.LegacyCategoryTypes.SplitKind(categoryType),
                Tags = PlayniteAchievements.Services.Achievements.AchievementTagHelper.Normalize(categoryType),
                Category = category
            };
        }
    }
}
