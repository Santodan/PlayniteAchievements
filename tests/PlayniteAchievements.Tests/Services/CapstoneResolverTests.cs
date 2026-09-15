using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class CapstoneResolverTests
    {
        private static AchievementDetail Achievement(
            string apiName,
            string category = null,
            string categoryType = null,
            bool isCapstone = false,
            bool unlocked = false)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = apiName,
                Category = category,
                CategoryType = categoryType,
                IsCapstone = isCapstone,
                Unlocked = unlocked
            };
        }

        private static CapstoneAssignment Assignment(string apiName)
        {
            return new CapstoneAssignment { ApiName = apiName };
        }

        [TestMethod]
        public void Resolve_Untouched_SeedsFromProviderFlags()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base", "Base", isCapstone: true),
                Achievement("ach_one", "Base", "Base")
            };

            var resolver = CapstoneResolver.Resolve(achievements, null, false);

            Assert.AreEqual(1, resolver.Count);
            Assert.IsTrue(resolver.IsCapstone("plat"));
            Assert.AreEqual("plat", resolver.ResolveForCategory("Base"));
        }

        [TestMethod]
        public void Resolve_Materialized_IgnoresProviderFlags()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base", "Base", isCapstone: true),
                Achievement("boss", "Base", "Base")
            };

            var resolver = CapstoneResolver.Resolve(
                achievements,
                new[] { Assignment("boss") },
                true);

            Assert.AreEqual(1, resolver.Count);
            Assert.IsTrue(resolver.IsCapstone("boss"));
            Assert.IsFalse(resolver.IsCapstone("plat"));
        }

        [TestMethod]
        public void Resolve_MaterializedEmpty_LeavesNoCapstones()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base", "Base", isCapstone: true)
            };

            var resolver = CapstoneResolver.Resolve(achievements, new CapstoneAssignment[0], true);

            Assert.AreEqual(0, resolver.Count);
            Assert.IsFalse(resolver.IsCapstone("plat"));
        }

        [TestMethod]
        public void Seed_FilesEveryProviderCapstoneUnderItsOwnCategory()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("base_mastery", "Base", "Base", isCapstone: true),
                Achievement("subset_mastery", "Hardcore", "Subset", isCapstone: true),
                Achievement("dlc_mastery", "Winter", "DLC", isCapstone: true)
            };

            var resolver = CapstoneResolver.Resolve(achievements, null, false);

            Assert.AreEqual(3, resolver.Count);
            Assert.AreEqual("base_mastery", resolver.ResolveForCategory("Base"));
            Assert.AreEqual("subset_mastery", resolver.ResolveForCategory("Hardcore"));
            Assert.AreEqual("dlc_mastery", resolver.ResolveForCategory("Winter"));
        }

        [TestMethod]
        public void ResolveForCategory_InheritsFromNearestAncestor()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("dlc_cap", "DLC", "DLC", isCapstone: true),
                Achievement("summer_cap", "DLC::Summer", "DLC", isCapstone: true),
                Achievement("winter_ach", "DLC::Winter", "DLC")
            };

            var resolver = CapstoneResolver.Resolve(achievements, null, false);

            // Its own beats the ancestor.
            Assert.AreEqual("summer_cap", resolver.ResolveForCategory("DLC::Summer"));
            // Nothing of its own, so the nearest ancestor answers.
            Assert.AreEqual("dlc_cap", resolver.ResolveForCategory("DLC::Winter"));
        }

        [TestMethod]
        public void ResolveForCategory_UnrelatedCategory_StandsOnNothing()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base", "Base", isCapstone: true),
                Achievement("side", "Side Quests", "Base")
            };

            var resolver = CapstoneResolver.Resolve(achievements, null, false);

            // No whole-game fallback: completion counts the capstones a category actually holds,
            // so reporting an unrelated one as covering it would contradict the rollup.
            Assert.IsNull(resolver.ResolveForCategory("Side Quests"));
            Assert.IsFalse(resolver.HasOwnCapstone("Side Quests"));
        }

        [TestMethod]
        public void ResolveForCategory_EachCapstoneAnswersOnlyItsOwnCategory()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base"),
                Achievement("dlc_cap", "Winter")
            };

            var resolver = CapstoneResolver.Resolve(
                achievements,
                new[] { Assignment("plat"), Assignment("dlc_cap") },
                true);

            Assert.AreEqual("dlc_cap", resolver.ResolveForCategory("Winter"));
            Assert.AreEqual("plat", resolver.ResolveForCategory("Base"));
            Assert.IsNull(resolver.ResolveForCategory("Anything Else"));
        }

        [TestMethod]
        public void Resolve_OneCapstone_AnswersOnlyItsOwnCategory()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base"),
                Achievement("a", "One"),
                Achievement("b", "Two")
            };

            var resolver = CapstoneResolver.Resolve(achievements, new[] { Assignment("plat") }, true);

            Assert.AreEqual(1, resolver.Count);
            Assert.AreEqual("plat", resolver.ResolveForCategory("Base"));
            Assert.IsNull(resolver.ResolveForCategory("One"));
            Assert.IsNull(resolver.ResolveForCategory("Two"));
        }

        [TestMethod]
        public void Resolve_TwoCapstonesInOneCategory_LaterEntryWins()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("first", "Winter"),
                Achievement("second", "Winter")
            };

            var resolver = CapstoneResolver.Resolve(
                achievements,
                new[] { Assignment("first"), Assignment("second") },
                true);

            Assert.AreEqual("second", resolver.ResolveForCategory("Winter"));
        }

        [TestMethod]
        public void Resolve_CapstoneTheProviderNoLongerSends_IsNotCounted()
        {
            var achievements = new List<AchievementDetail> { Achievement("still_here", "Base") };

            var resolver = CapstoneResolver.Resolve(
                achievements,
                new[] { Assignment("still_here"), Assignment("vanished") },
                true);

            Assert.AreEqual(1, resolver.Count);
            Assert.IsFalse(resolver.IsCapstone("vanished"));
        }

        [TestMethod]
        public void Materialize_CapturesTheProviderSeed()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base", "Base", isCapstone: true),
                Achievement("subset", "Hardcore", "Subset", isCapstone: true),
                Achievement("ordinary", "Base", "Base")
            };

            var seeded = CapstoneResolver.Materialize(achievements);

            Assert.AreEqual(2, seeded.Count);
            CollectionAssert.AreEquivalent(
                new[] { "plat", "subset" },
                seeded.Select(entry => entry.ApiName).ToList());
        }
    }

    [TestClass]
    public class CapstoneCompletionTests
    {
        private static AchievementDetail Achievement(string apiName, bool isCapstone, bool unlocked)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                IsCapstone = isCapstone,
                Unlocked = unlocked
            };
        }

        [TestMethod]
        public void PlatinumEarnedWithDlcStillOpen_IsNotCompleted()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Achievement("plat", true, true),
                Achievement("dlc_mastery", true, false),
                Achievement("ordinary", false, false)
            });

            Assert.IsFalse(counts.IsCompleted);
            Assert.AreEqual(1, counts.Completions);
            Assert.AreEqual(2, counts.Total);
        }

        [TestMethod]
        public void EveryCapstoneEarned_IsCompleted()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Achievement("plat", true, true),
                Achievement("dlc_mastery", true, true),
                Achievement("ordinary", false, false)
            });

            Assert.IsTrue(counts.IsCompleted);
            Assert.AreEqual(2, counts.Completions);
        }

        [TestMethod]
        public void NoCapstonesAndFullyUnlocked_CountsAsOneCompletion()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Achievement("a", false, true),
                Achievement("b", false, true)
            });

            Assert.IsTrue(counts.IsCompleted);
            Assert.AreEqual(1, counts.Completions);
        }

        [TestMethod]
        public void NoCapstonesAndPartlyUnlocked_CountsAsNone()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Achievement("a", false, true),
                Achievement("b", false, false)
            });

            Assert.IsFalse(counts.IsCompleted);
            Assert.AreEqual(0, counts.Completions);
        }

        [TestMethod]
        public void EveryAchievementUnlocked_IsCompletedEvenWithCapstones()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Achievement("plat", true, true),
                Achievement("ordinary", false, true)
            });

            Assert.IsTrue(counts.IsCompleted);
        }

        private static AchievementDetail Trophy(string apiName, bool isCapstone, string trophyType)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                IsCapstone = isCapstone,
                TrophyType = trophyType
            };
        }

        [TestMethod]
        public void PlatinumIsTheOnlyCapstone_MatchesPlatinums()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Trophy("plat", true, "Platinum"),
                Trophy("gold", false, "Gold")
            });

            Assert.IsTrue(counts.CapstonesMatchPlatinums);
        }

        [TestMethod]
        public void NoCapstonesAtAll_MatchesPlatinums()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Trophy("plat", false, "Platinum"),
                Trophy("gold", false, "Gold")
            });

            Assert.IsTrue(counts.CapstonesMatchPlatinums);
        }

        [TestMethod]
        public void CapstoneBesidesThePlatinum_DoesNotMatchPlatinums()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Trophy("plat", true, "Platinum"),
                Trophy("dlc_mastery", true, null)
            });

            Assert.IsFalse(counts.CapstonesMatchPlatinums);
        }

        [TestMethod]
        public void PlatinumThatIsNotTheCapstone_DoesNotMatchPlatinums()
        {
            // Same count on each side, different achievements: two finish lines, not one.
            var counts = CapstoneCompletion.Count(new[]
            {
                Trophy("plat", false, "Platinum"),
                Trophy("mastery", true, null)
            });

            Assert.IsFalse(counts.CapstonesMatchPlatinums);
        }

        [TestMethod]
        public void NoTrophiesAndOneCapstone_DoesNotMatchPlatinums()
        {
            var counts = CapstoneCompletion.Count(new[]
            {
                Trophy("mastery", true, null),
                Trophy("ordinary", false, null)
            });

            Assert.IsFalse(counts.CapstonesMatchPlatinums);
        }
    }
}
