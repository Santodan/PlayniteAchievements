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
            Assert.IsNull(new AchievementTallyBuilder().Build());
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
