using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Ffxiv;

namespace PlayniteAchievements.Ffxiv.Tests
{
    [TestClass]
    public class FfxivParsingTests
    {
        [DataTestMethod]
        [DataRow("62541805")]
        [DataRow("  62541805  ")]
        [DataRow("https://na.finalfantasyxiv.com/lodestone/character/62541805/")]
        [DataRow("https://eu.finalfantasyxiv.com/lodestone/character/62541805")]
        [DataRow("na.finalfantasyxiv.com/lodestone/character/62541805/achievement/")]
        [DataRow("https://ffxivcollect.com/characters/62541805")]
        [DataRow("https://ffxivcollect.com/characters/62541805/achievements?x=1")]
        public void ParseCharacterId_ReadsIdFromUrlOrBareId(string input)
        {
            Assert.AreEqual(62541805L, FfxivParsing.ParseCharacterId(input));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("Aeron Riusone")]
        [DataRow("0")]
        [DataRow("-5")]
        [DataRow("https://ffxivcollect.com/characters")]
        [DataRow("https://ffxivcollect.com/characters/62541805abc")]
        public void ParseCharacterId_ReturnsNull_WithoutAnId(string input)
        {
            Assert.IsNull(FfxivParsing.ParseCharacterId(input));
        }

        [DataTestMethod]
        [DataRow("FINAL FANTASY XIV Online")]
        [DataRow("FINAL FANTASY® XIV Online")]
        [DataRow("Final Fantasy 14")]
        [DataRow("FFXIV")]
        [DataRow("FF 14")]
        public void IsFinalFantasyXivTitle_RecognizesCommonStoreTitles(string title)
        {
            Assert.IsTrue(FfxivParsing.IsFinalFantasyXivTitle(title));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("Final Fantasy XV")]
        [DataRow("Fantasy XIV")]
        [DataRow("Final Fantasy VII Remake")]
        public void IsFinalFantasyXivTitle_RejectsOtherTitles(string title)
        {
            Assert.IsFalse(FfxivParsing.IsFinalFantasyXivTitle(title));
        }

        [DataTestMethod]
        [DataRow("98%", 98.0)]
        [DataRow("0%", 0.0)]
        [DataRow("12.5%", 12.5)]
        [DataRow("100%", 100.0)]
        public void ParseOwnedPercent_ParsesPercentStrings(string input, double expected)
        {
            var value = FfxivParsing.ParseOwnedPercent(input);
            Assert.IsTrue(value.HasValue);
            Assert.AreEqual(expected, value.Value, 1e-9);
        }

        [TestMethod]
        public void ParseOwnedPercent_ClampsAboveOneHundred()
        {
            Assert.AreEqual(100.0, FfxivParsing.ParseOwnedPercent("150%").Value, 1e-9);
        }

        [TestMethod]
        public void ParseOwnedPercent_ReturnsNull_ForMissingOrUnparseable()
        {
            Assert.IsNull(FfxivParsing.ParseOwnedPercent(null));
            Assert.IsNull(FfxivParsing.ParseOwnedPercent(""));
            Assert.IsNull(FfxivParsing.ParseOwnedPercent("n/a"));
        }

        [TestMethod]
        public void NormalizeIconUrl_RewritesWebpToPng()
        {
            const string webp = "https://v2.xivapi.com/api/asset?format=webp&path=ui/icon/000000/000317_hr1.tex";
            const string png = "https://v2.xivapi.com/api/asset?format=png&path=ui/icon/000000/000317_hr1.tex";
            Assert.AreEqual(png, FfxivParsing.NormalizeIconUrl(webp));
        }

        [TestMethod]
        public void NormalizeIconUrl_LeavesOtherValuesUnchanged()
        {
            Assert.IsNull(FfxivParsing.NormalizeIconUrl(null));
            Assert.AreEqual("https://example.com/a.png", FfxivParsing.NormalizeIconUrl("https://example.com/a.png"));
        }

        [TestMethod]
        public void ResolveCategoryType_MarksSeasonalEventsMissable()
        {
            Assert.AreEqual("Missable", FfxivParsing.ResolveTag("Seasonal Events"));
            Assert.AreEqual("Missable", FfxivParsing.ResolveTag("seasonal events"));
        }

        [TestMethod]
        public void ResolveCategoryType_ReturnsNull_ForRegularCategories()
        {
            Assert.IsNull(FfxivParsing.ResolveTag("General"));
            Assert.IsNull(FfxivParsing.ResolveTag("Raids"));
            Assert.IsNull(FfxivParsing.ResolveTag(null));
            Assert.IsNull(FfxivParsing.ResolveTag(""));
        }
    }
}
