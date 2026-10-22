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
    public class AchievementTagTallyTests
    {
        private static Dictionary<string, (int Unlocked, int Total)> AsMap(IEnumerable<AchievementTagTally> tallies) =>
            (tallies ?? Enumerable.Empty<AchievementTagTally>())
                .ToDictionary(tally => tally.Key, tally => (tally.Unlocked, tally.Total), StringComparer.OrdinalIgnoreCase);

        [TestMethod]
        public void Builder_CountsEachTagOnceAndSkipsDerivedModes()
        {
            var builder = new AchievementTagTallyBuilder();
            builder.Add(AchievementTagHelper.Missable + "|" + AchievementTagHelper.Softcore, unlocked: true);
            builder.Add(AchievementTagHelper.Missable, unlocked: false);
            builder.Add("Collectable", 2, 5);

            var tallies = AsMap(builder.Build());

            Assert.AreEqual((1, 2), tallies[AchievementTagHelper.Missable]);
            Assert.AreEqual((2, 5), tallies["Collectable"]);
            Assert.IsFalse(tallies.ContainsKey(AchievementTagHelper.Softcore));
        }

        [TestMethod]
        public void Builder_BuildsNullWhenNothingIsTagged()
        {
            var builder = new AchievementTagTallyBuilder();
            builder.Add(null, unlocked: true);
            builder.Add("Collectable", unlocked: false);
            builder.Remove("Collectable", unlocked: false);

            Assert.IsNull(builder.Build());
        }

        [TestMethod]
        public void ApplyEdits_MovesEditedAchievementsFromProviderTagsToTheirOwn()
        {
            var provider = new[]
            {
                new AchievementTagTally("Collectable", 1, 3),
                new AchievementTagTally(AchievementTagHelper.Missable, 0, 1)
            };
            var states = new Dictionary<string, (string Tags, bool Unlocked)>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = ("Collectable", true),
                ["b"] = (AchievementTagHelper.Missable, false),
                ["c"] = (null, false)
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

            var tallies = AsMap(AchievementTagTallyBuilder.ApplyEdits(provider, states, overrides));

            Assert.AreEqual((0, 3), tallies["Collectable"]);
            Assert.AreEqual((1, 1), tallies["Progression"]);
            Assert.IsFalse(tallies.ContainsKey(AchievementTagHelper.Missable));
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
            var builder = new AchievementTagTallyBuilder();
            builder.Add(AchievementTagHelper.Missable + "|Collectable", unlocked: true);
            builder.Add(AchievementTagHelper.Missable, unlocked: false);
            builder.Add("Collectable", unlocked: false);
            var tallies = builder.Build();

            var both = AchievementTagTallyBuilder.CountShown(tallies, new[] { AchievementTagHelper.Missable, "Collectable" });
            Assert.AreEqual((1, 2), both.PerTag[AchievementTagHelper.Missable]);
            Assert.AreEqual((1, 2), both.PerTag["Collectable"]);
            Assert.AreEqual(1, both.Unlocked);
            Assert.AreEqual(3, both.Total);

            var one = AchievementTagTallyBuilder.CountShown(tallies, new[] { "Collectable" });
            Assert.AreEqual(1, one.Unlocked);
            Assert.AreEqual(2, one.Total);
        }

        [TestMethod]
        public void EffectiveTags_KeepsTheProvidersDerivedModes()
        {
            var effective = AchievementTagTallyBuilder.EffectiveTags(
                AchievementTagHelper.Missable + "|" + AchievementTagHelper.Hardcore,
                new AchievementOverride { Tags = "Progression" });

            Assert.IsTrue(AchievementTagHelper.Contains(effective, "Progression"));
            Assert.IsTrue(AchievementTagHelper.Contains(effective, AchievementTagHelper.Hardcore));
            Assert.IsFalse(AchievementTagHelper.Contains(effective, AchievementTagHelper.Missable));
        }
    }
}
