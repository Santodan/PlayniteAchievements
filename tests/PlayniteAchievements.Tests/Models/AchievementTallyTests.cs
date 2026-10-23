using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class AchievementTallyTests
    {
        private static Dictionary<string, (int Unlocked, int Total)> AsMap(IEnumerable<AchievementTally> tallies) =>
            (tallies ?? Enumerable.Empty<AchievementTally>())
                .ToDictionary(tally => tally.Key, tally => (tally.Unlocked, tally.Total), StringComparer.OrdinalIgnoreCase);

        [TestMethod]
        public void Builder_CountsEachTagOnceAndSkipsDerivedModes()
        {
            var builder = new AchievementTallyBuilder();
            builder.Add(AchievementTagHelper.Missable + "|" + AchievementTagHelper.Softcore, unlocked: true);
            builder.Add(AchievementTagHelper.Missable, unlocked: false);
            builder.Add("Collectable", 2, 5);

            var tallies = AsMap(builder.Build());

            Assert.AreEqual((1, 2), tallies[AchievementTagHelper.Missable]);
            Assert.AreEqual((2, 5), tallies["Collectable"]);
            Assert.IsFalse(tallies.ContainsKey(AchievementTagHelper.Softcore));
        }

        [TestMethod]
        public void Builder_CountsUntaggedUnderTheEmptySetAndBuildsNullWhenEmpty()
        {
            var builder = new AchievementTallyBuilder();
            builder.Add(null, unlocked: true);
            Assert.AreEqual((1, 1), AsMap(builder.Build())[string.Empty]);

            builder.Remove(null, unlocked: true);
            builder.Add("Collectable", unlocked: false);
            builder.Remove("Collectable", unlocked: false);
            Assert.IsNull(builder.Build());
        }

        [TestMethod]
        public void Builder_KeepsTierGradeAndCapstoneApart()
        {
            var builder = new AchievementTallyBuilder();
            builder.Add("Collectable", RarityTier.Rare, "Gold", isCapstone: false, unlocked: true);
            builder.Add("Collectable", RarityTier.Rare, "gold", isCapstone: false, unlocked: false);
            builder.Add("Collectable", RarityTier.Rare, "gold", isCapstone: true, unlocked: false);
            builder.Add("Collectable", null, "none", isCapstone: false, unlocked: false);

            var tallies = builder.Build();
            Assert.AreEqual(3, tallies.Length);
            var rareGold = tallies.Single(tally => tally.Rarity == RarityTier.Rare && tally.Trophy == "gold" && !tally.IsCapstone);
            Assert.AreEqual(1, rareGold.Unlocked);
            Assert.AreEqual(2, rareGold.Total);
            Assert.AreEqual(1, tallies.Single(tally => tally.IsCapstone).Total);
            var unknown = tallies.Single(tally => !tally.Rarity.HasValue);
            Assert.IsNull(unknown.Trophy);
        }

        [TestMethod]
        public void ApplyEdits_MovesAchievementsIntoAndOutOfAnEditedCapstoneSet()
        {
            var provider = new[]
            {
                new AchievementTally(string.Empty, RarityTier.Rare, "platinum", true, 0, 1),
                new AchievementTally(string.Empty, RarityTier.Common, null, false, 1, 3)
            };
            var states = new Dictionary<string, AchievementTallyState>(StringComparer.OrdinalIgnoreCase)
            {
                // The provider's capstone, which the user's set leaves out.
                ["plat"] = new AchievementTallyState(null, RarityTier.Rare, "platinum", isCapstone: true, unlocked: false),
                // The user's capstone instead.
                ["final"] = new AchievementTallyState(null, RarityTier.Common, null, isCapstone: false, unlocked: true)
            };

            var tallies = AchievementTallyBuilder.ApplyEdits(
                provider,
                states,
                new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "final" });

            var capstone = tallies.Single(tally => tally.IsCapstone);
            Assert.AreEqual(RarityTier.Common, capstone.Rarity);
            Assert.AreEqual((1, 1), (capstone.Unlocked, capstone.Total));
            Assert.AreEqual((0, 1), tallies
                .Where(tally => tally.Trophy == "platinum")
                .Select(tally => (tally.Unlocked, tally.Total))
                .Single());
            Assert.AreEqual(4, tallies.Sum(tally => tally.Total));
        }

        [TestMethod]
        public void ApplyEdits_MovesEditedAchievementsFromProviderTagsToTheirOwn()
        {
            var provider = new[]
            {
                new AchievementTally("Collectable", 1, 3),
                new AchievementTally(AchievementTagHelper.Missable, 0, 1),
                new AchievementTally(string.Empty, 0, 1)
            };
            var states = new Dictionary<string, AchievementTallyState>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = new AchievementTallyState("Collectable", RarityTier.Common, null, false, true),
                ["b"] = new AchievementTallyState(AchievementTagHelper.Missable, RarityTier.Common, null, false, false),
                ["c"] = new AchievementTallyState(null, RarityTier.Common, null, false, false)
            };
            var overrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
            {
                // Retagged.
                ["a"] = new AchievementOverride { Tags = "Progression" },
                // Cleared.
                ["b"] = new AchievementOverride { ClearTags = true },
                // Tagged where the provider gave none.
                ["c"] = new AchievementOverride { Tags = "Collectable" },
                // Not tagged by the user, so left alone.
                ["d"] = new AchievementOverride { Points = 5 }
            };

            var tallies = AsMap(AchievementTallyBuilder.ApplyEdits(provider, states, overrides));

            Assert.AreEqual((0, 3), tallies["Collectable"]);
            Assert.AreEqual((1, 1), tallies["Progression"]);
            Assert.IsFalse(tallies.ContainsKey(AchievementTagHelper.Missable));
            // The cleared one is untagged now, in place of the one the user tagged.
            Assert.AreEqual((0, 1), tallies[string.Empty]);
        }

        [TestMethod]
        public void TagScope_OffersGlobalTagsThenAGamesOwnThenSharedOnes()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var present = new Dictionary<Guid, string[]>
            {
                [first] = new[] { "first-only", "both-games" },
                [second] = new[] { "both-games" }
            };

            CollectionAssert.AreEqual(
                AchievementTagRegistry.GetAssignableKeys(null).ToArray(),
                AchievementTagScope.OptionKeys(Array.Empty<Guid>(), id => present[id]).ToArray());

            var one = AchievementTagScope.OptionKeys(new[] { first }, id => present[id]);
            CollectionAssert.Contains(one.ToArray(), "first-only");
            CollectionAssert.Contains(one.ToArray(), AchievementTagHelper.Missable);

            var both = AchievementTagScope.OptionKeys(new[] { first, second }, id => present[id]);
            CollectionAssert.DoesNotContain(both.ToArray(), "first-only");
            CollectionAssert.Contains(both.ToArray(), "both-games");
            CollectionAssert.Contains(both.ToArray(), AchievementTagHelper.Missable);

            Assert.AreEqual(first, AchievementTagScope.NamingGame(new[] { first }));
            Assert.IsNull(AchievementTagScope.NamingGame(new[] { first, second }));
        }

        [TestMethod]
        public void CountShown_CountsAnAchievementWithSeveralShownTagsOnce()
        {
            // X carries both tags and is unlocked; Y and Z carry one each and are locked.
            var builder = new AchievementTallyBuilder();
            builder.Add(AchievementTagHelper.Missable + "|Collectable", unlocked: true);
            builder.Add(AchievementTagHelper.Missable, unlocked: false);
            builder.Add("Collectable", unlocked: false);
            var tallies = builder.Build();

            var both = AchievementTallyBuilder.CountShown(tallies, new[] { AchievementTagHelper.Missable, "Collectable" });
            Assert.AreEqual((1, 2), both.PerTag[AchievementTagHelper.Missable]);
            Assert.AreEqual((1, 2), both.PerTag["Collectable"]);
            Assert.AreEqual(1, both.Unlocked);
            Assert.AreEqual(3, both.Total);

            var one = AchievementTallyBuilder.CountShown(tallies, new[] { "Collectable" });
            Assert.AreEqual(1, one.Unlocked);
            Assert.AreEqual(2, one.Total);
        }

        [TestMethod]
        public void EffectiveTags_KeepsTheProvidersDerivedModes()
        {
            var effective = AchievementTallyBuilder.EffectiveTags(
                AchievementTagHelper.Missable + "|" + AchievementTagHelper.Hardcore,
                new AchievementOverride { Tags = "Progression" });

            Assert.IsTrue(AchievementTagHelper.Contains(effective, "Progression"));
            Assert.IsTrue(AchievementTagHelper.Contains(effective, AchievementTagHelper.Hardcore));
            Assert.IsFalse(AchievementTagHelper.Contains(effective, AchievementTagHelper.Missable));
        }
    }
}
