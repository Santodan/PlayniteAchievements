using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.PSN;
using PlayniteAchievements.Providers.PSN.Models;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class PsnTrophySetResolutionHelperTests
    {
        private static PsnTrophyTitleLookup Lookup(params PsnTrophyTitleEntry[] entries)
        {
            return new PsnTrophyTitleLookup
            {
                Titles = new List<PsnTitleContainer>
                {
                    new PsnTitleContainer { TrophyTitles = entries.ToList() }
                }
            };
        }

        private static PsnTrophyTitleEntry Entry(string id, string name = null, string icon = null)
        {
            return new PsnTrophyTitleEntry
            {
                NpCommunicationId = id,
                TrophyTitleName = name,
                TrophyTitleIconUrl = icon
            };
        }

        [TestMethod]
        public void ExtractSets_MultipleEntries_ReturnsAllInApiOrder()
        {
            // A compilation lists one trophy set per included game; every set must survive.
            var sets = PsnTrophySetResolutionHelper.ExtractSets(Lookup(
                Entry("NPWR11111_00", "Spyro the Dragon", "https://example/1.png"),
                Entry("NPWR22222_00", "Spyro 2: Ripto's Rage!"),
                Entry("NPWR33333_00", "Spyro: Year of the Dragon")));

            CollectionAssert.AreEqual(
                new[] { "NPWR11111_00", "NPWR22222_00", "NPWR33333_00" },
                sets.Select(s => s.NpCommunicationId).ToArray());
            Assert.AreEqual("Spyro the Dragon", sets[0].Title);
            Assert.AreEqual("https://example/1.png", sets[0].IconUrl);
            Assert.IsNull(sets[1].IconUrl);
        }

        [TestMethod]
        public void ExtractSets_TrimsTitleAndIconAndBlanksBecomeNull()
        {
            var sets = PsnTrophySetResolutionHelper.ExtractSets(Lookup(
                Entry("  NPWR11111_00  ", "  Spyro the Dragon  ", "   ")));

            Assert.AreEqual("NPWR11111_00", sets[0].NpCommunicationId);
            Assert.AreEqual("Spyro the Dragon", sets[0].Title);
            Assert.IsNull(sets[0].IconUrl);
        }

        [TestMethod]
        public void ExtractSets_DuplicateNpCommIds_DeduplicatesCaseInsensitive()
        {
            var sets = PsnTrophySetResolutionHelper.ExtractSets(Lookup(
                Entry("NPWR11111_00", "First"),
                Entry("npwr11111_00", "Duplicate"),
                Entry("NPWR22222_00", "Second")));

            Assert.AreEqual(2, sets.Count);
            Assert.AreEqual("First", sets[0].Title);
            Assert.AreEqual("NPWR22222_00", sets[1].NpCommunicationId);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void ExtractSets_BlankNpCommIdEntries_Skipped(string blankId)
        {
            var sets = PsnTrophySetResolutionHelper.ExtractSets(Lookup(
                Entry(blankId, "Ignored"),
                Entry("NPWR11111_00", "Kept")));

            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual("NPWR11111_00", sets[0].NpCommunicationId);
        }

        [TestMethod]
        public void ExtractSets_NullEntryInList_Skipped()
        {
            var sets = PsnTrophySetResolutionHelper.ExtractSets(Lookup(null, Entry("NPWR11111_00")));

            Assert.AreEqual(1, sets.Count);
        }

        [TestMethod]
        public void ExtractSets_NullOrEmptyLookup_ReturnsEmpty()
        {
            Assert.AreEqual(0, PsnTrophySetResolutionHelper.ExtractSets(null).Count);
            Assert.AreEqual(
                0,
                PsnTrophySetResolutionHelper.ExtractSets(new PsnTrophyTitleLookup()).Count);
            Assert.AreEqual(
                0,
                PsnTrophySetResolutionHelper.ExtractSets(new PsnTrophyTitleLookup
                {
                    Titles = new List<PsnTitleContainer>()
                }).Count);
            Assert.AreEqual(
                0,
                PsnTrophySetResolutionHelper.ExtractSets(new PsnTrophyTitleLookup
                {
                    Titles = new List<PsnTitleContainer> { new PsnTitleContainer() }
                }).Count);
        }

        [TestMethod]
        public void ExtractSets_OnlyFirstTitleContainerIsUsed()
        {
            var lookup = new PsnTrophyTitleLookup
            {
                Titles = new List<PsnTitleContainer>
                {
                    new PsnTitleContainer { TrophyTitles = new List<PsnTrophyTitleEntry> { Entry("NPWR11111_00") } },
                    new PsnTitleContainer { TrophyTitles = new List<PsnTrophyTitleEntry> { Entry("NPWR99999_00") } }
                }
            };

            var sets = PsnTrophySetResolutionHelper.ExtractSets(lookup);

            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual("NPWR11111_00", sets[0].NpCommunicationId);
        }

        [TestMethod]
        public void ParseOverrideSets_SingleId_NormalizesToUpper()
        {
            var sets = PsnTrophySetResolutionHelper.ParseOverrideSets("  npwr12345_00 ");

            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual("NPWR12345_00", sets[0].NpCommunicationId);
        }

        [DataTestMethod]
        [DataRow("NPWR11111_00+npwr22222_00")]
        [DataRow("NPWR11111_00,npwr22222_00")]
        [DataRow(" NPWR11111_00 , npwr22222_00 ")]
        public void ParseOverrideSets_PlusAndCommaJoined_ReturnsAllInOrder(string raw)
        {
            var sets = PsnTrophySetResolutionHelper.ParseOverrideSets(raw);

            CollectionAssert.AreEqual(
                new[] { "NPWR11111_00", "NPWR22222_00" },
                sets.Select(s => s.NpCommunicationId).ToArray());
        }

        [TestMethod]
        public void ParseOverrideSets_DuplicateIds_Deduplicates()
        {
            var sets = PsnTrophySetResolutionHelper.ParseOverrideSets("NPWR11111_00+npwr11111_00");

            Assert.AreEqual(1, sets.Count);
        }

        [DataTestMethod]
        [DataRow("NPWR11111_00+garbage")]
        [DataRow("garbage+NPWR11111_00")]
        [DataRow("garbage")]
        [DataRow("NPWR1_00")]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void ParseOverrideSets_InvalidValue_ReturnsEmpty(string raw)
        {
            // Any invalid token invalidates the whole value, matching the override dialog.
            Assert.AreEqual(0, PsnTrophySetResolutionHelper.ParseOverrideSets(raw).Count);
        }

        [TestMethod]
        public void BuildCanonicalOverrideValue_JoinsInUserOrder()
        {
            var sets = PsnTrophySetResolutionHelper.ParseOverrideSets("npwr22222_00+npwr11111_00");

            Assert.AreEqual(
                "NPWR22222_00+NPWR11111_00",
                PsnTrophySetResolutionHelper.BuildCanonicalOverrideValue(sets));
        }

        [TestMethod]
        public void BuildApiName_SingleSet_KeepsBareTrophyKey()
        {
            // Single-set games must keep their stored keys so ApiName-keyed user data survives.
            Assert.AreEqual(
                "default:1",
                PsnTrophySetResolutionHelper.BuildApiName(false, "NPWR11111_00", "default:1"));
        }

        [TestMethod]
        public void BuildApiName_Collection_PrefixesNpCommId()
        {
            Assert.AreEqual(
                "NPWR11111_00:default:1",
                PsnTrophySetResolutionHelper.BuildApiName(true, "NPWR11111_00", "default:1"));
        }

        [TestMethod]
        public void BuildApiName_Collection_KeepsSameTrophyKeyDistinctPerSet()
        {
            var first = PsnTrophySetResolutionHelper.BuildApiName(true, "NPWR11111_00", "default:1");
            var second = PsnTrophySetResolutionHelper.BuildApiName(true, "NPWR22222_00", "default:1");

            Assert.AreNotEqual(first, second);
        }

        [TestMethod]
        public void BuildProviderGameKey_SortsAndJoins()
        {
            var sets = new List<PsnResolvedTrophySet>
            {
                new PsnResolvedTrophySet { NpCommunicationId = "NPWR33333_00" },
                new PsnResolvedTrophySet { NpCommunicationId = "NPWR11111_00" },
                new PsnResolvedTrophySet { NpCommunicationId = "NPWR22222_00" }
            };

            Assert.AreEqual(
                "NPWR11111_00+NPWR22222_00+NPWR33333_00",
                PsnTrophySetResolutionHelper.BuildProviderGameKey(sets));
        }

        [TestMethod]
        public void BuildProviderGameKey_SingleSet_ReturnsBareId()
        {
            var sets = new List<PsnResolvedTrophySet>
            {
                new PsnResolvedTrophySet { NpCommunicationId = "NPWR11111_00" }
            };

            Assert.AreEqual("NPWR11111_00", PsnTrophySetResolutionHelper.BuildProviderGameKey(sets));
        }

        [TestMethod]
        public void BuildProviderGameKey_NullOrBlankEntries_AreIgnored()
        {
            var sets = new List<PsnResolvedTrophySet>
            {
                null,
                new PsnResolvedTrophySet { NpCommunicationId = "   " },
                new PsnResolvedTrophySet { NpCommunicationId = "NPWR11111_00" }
            };

            Assert.AreEqual("NPWR11111_00", PsnTrophySetResolutionHelper.BuildProviderGameKey(sets));
            Assert.AreEqual(string.Empty, PsnTrophySetResolutionHelper.BuildProviderGameKey(null));
        }
    }
}
