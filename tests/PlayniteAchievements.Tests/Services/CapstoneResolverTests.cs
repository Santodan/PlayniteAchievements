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

        private static CapstoneAssignment Assignment(string apiName, bool gameWide)
        {
            return new CapstoneAssignment { ApiName = apiName, IsGameWide = gameWide };
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
            Assert.AreEqual("plat", resolver.GameWideApiName);
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
                new[] { Assignment("boss", true) },
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
        public void SeedScope_BaseIsGameWide_SubsetAndDlcAreCategoryScoped()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("base_mastery", "Base", "Base", isCapstone: true),
                Achievement("subset_mastery", "Hardcore", "Subset", isCapstone: true),
                Achievement("dlc_mastery", "Winter", "DLC", isCapstone: true)
            };

            var resolver = CapstoneResolver.Resolve(achievements, null, false);

            Assert.AreEqual(3, resolver.Count);
            Assert.AreEqual("base_mastery", resolver.GameWideApiName);
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
        public void ResolveForCategory_FallsBackToGameWide()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base", "Base", isCapstone: true),
                Achievement("side", "Side Quests", "Base")
            };

            var resolver = CapstoneResolver.Resolve(achievements, null, false);

            Assert.AreEqual("plat", resolver.ResolveForCategory("Side Quests"));
            Assert.IsFalse(resolver.HasOwnCapstone("Side Quests"));
        }

        [TestMethod]
        public void ResolveForCategory_CategoryCapstoneBeatsGameWide()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base"),
                Achievement("dlc_cap", "Winter")
            };

            var resolver = CapstoneResolver.Resolve(
                achievements,
                new[] { Assignment("plat", true), Assignment("dlc_cap", false) },
                true);

            Assert.AreEqual("dlc_cap", resolver.ResolveForCategory("Winter"));
            Assert.AreEqual("plat", resolver.ResolveForCategory("Base"));
            Assert.AreEqual("plat", resolver.ResolveForCategory("Anything Else"));
        }

        [TestMethod]
        public void Resolve_GameWideCapstoneCoveringManyCategories_CountsOnce()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base"),
                Achievement("a", "One"),
                Achievement("b", "Two")
            };

            var resolver = CapstoneResolver.Resolve(achievements, new[] { Assignment("plat", true) }, true);

            Assert.AreEqual(1, resolver.Count);
            Assert.AreEqual("plat", resolver.ResolveForCategory("One"));
            Assert.AreEqual("plat", resolver.ResolveForCategory("Two"));
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
                new[] { Assignment("first", false), Assignment("second", false) },
                true);

            Assert.AreEqual("second", resolver.ResolveForCategory("Winter"));
        }

        [TestMethod]
        public void Resolve_CapstoneTheProviderNoLongerSends_IsNotCounted()
        {
            var achievements = new List<AchievementDetail> { Achievement("still_here", "Base") };

            var resolver = CapstoneResolver.Resolve(
                achievements,
                new[] { Assignment("still_here", true), Assignment("vanished", false) },
                true);

            Assert.AreEqual(1, resolver.Count);
            Assert.IsFalse(resolver.IsCapstone("vanished"));
        }

        [TestMethod]
        public void Materialize_CapturesTheProviderSeedWithItsScopes()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("plat", "Base", "Base", isCapstone: true),
                Achievement("subset", "Hardcore", "Subset", isCapstone: true),
                Achievement("ordinary", "Base", "Base")
            };

            var seeded = CapstoneResolver.Materialize(achievements);

            Assert.AreEqual(2, seeded.Count);
            Assert.IsTrue(seeded.Single(entry => entry.ApiName == "plat").IsGameWide);
            Assert.IsFalse(seeded.Single(entry => entry.ApiName == "subset").IsGameWide);
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
    }
}
