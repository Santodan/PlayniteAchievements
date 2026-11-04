using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    [DoNotParallelize]
    public class ScoreCardTypeTests
    {
        [DataTestMethod]
        [DataRow("Collection", "Collection")]
        [DataRow("Prestige", "Prestige")]
        [DataRow("Gamerscore", "Points:Xbox")]
        [DataRow("EpicXp", "Points:Epic")]
        [DataRow("RetroPoints", "Points:RetroAchievements")]
        [DataRow("2", "Points:Xbox")]
        [DataRow("Points:PSN", "Points:PSN")]
        [DataRow("Points:RPCS3", "Points:PSN")]
        [DataRow("points:xenia", "Points:Xbox")]
        [DataRow("Points:Custom:abc", "Points:Custom:abc")]
        public void TryParse_ReadsKeysAndLegacyEnumValues(string raw, string expectedKey)
        {
            Assert.IsTrue(ScoreCardType.TryParse(raw, out var type));
            Assert.AreEqual(expectedKey, type.Key);
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("Points:")]
        [DataRow("Points:Custom")]
        [DataRow("Bogus")]
        [DataRow("9")]
        public void TryParse_RejectsUnknownValues(string raw)
        {
            Assert.IsFalse(ScoreCardType.TryParse(raw, out var type));
            Assert.AreEqual(ScoreCardType.Collection, type);
        }

        [DataTestMethod]
        [DataRow("None", null)]
        [DataRow("0", null)]
        [DataRow("1", "Collection")]
        [DataRow("3", "Points:Xbox")]
        [DataRow("5", "Points:RetroAchievements")]
        [DataRow("Gamerscore", "Points:Xbox")]
        [DataRow("Points:Steam", "Points:Steam")]
        public void Slots_ReadLegacySlotNumbersAndNames(string slot, string expectedKey)
        {
            var hasCard = ScoreCardSlots.TryGetCardType(slot, out var type);

            Assert.AreEqual(expectedKey != null, hasCard);
            if (hasCard)
            {
                Assert.AreEqual(expectedKey, type.Key);
            }

            Assert.AreEqual(expectedKey ?? ScoreCardSlots.NoneKey, ScoreCardSlots.Normalize(slot));
        }

        [DataTestMethod]
        [DataRow("Xbox", "Xbox")]
        [DataRow("Xenia", "Xbox")]
        [DataRow("ShadPS4", "PSN")]
        [DataRow("Steam", "Steam")]
        [DataRow("Custom:abc", "Custom:abc")]
        [DataRow("Custom", null)]
        [DataRow("Unknown", null)]
        [DataRow("  ", null)]
        public void FromProviderKey_FoldsAliases(string providerKey, string expected)
        {
            Assert.AreEqual(expected, PointsFamilies.FromProviderKey(providerKey));
        }

        [TestMethod]
        public void Choices_ListPublishedFamiliesAndTheCurrentCard()
        {
            var totals = new PlatformScoreTotals();
            totals.Add("PSN", 90, 300);
            ScoreCardChoices.PublishFamiliesWithPoints(totals);
            try
            {
                var keys = ScoreCardChoices.Build(ScoreCardType.Points("GOG")).Select(card => card.Key).ToList();

                CollectionAssert.AreEqual(
                    new[] { "Collection", "Prestige", "Points:PSN", "Points:GOG" },
                    keys);
            }
            finally
            {
                ScoreCardChoices.PublishFamiliesWithPoints(null);
            }
        }
    }
}
