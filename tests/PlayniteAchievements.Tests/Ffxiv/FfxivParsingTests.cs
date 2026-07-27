using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Ffxiv;

namespace PlayniteAchievements.Ffxiv.Tests
{
    [TestClass]
    public class FfxivParsingTests
    {
        // The Lodestone search is a partial match: searching "Mal Reynolds" returns
        // "Malynor Reynolds" as the first result, then the exact "Mal Reynolds".
        // The "{NBSP}" placeholder stands in for the non-breaking space the Lodestone
        // inserts between the world and data center.
        private const string SearchHtmlTemplate = @"
<html><body>
  <div class=""entry"">
    <a href=""/lodestone/character/4602717/"" class=""entry__link"">
      <p class=""entry__name"">Malynor Reynolds</p>
      <p class=""entry__world""><i class=""xiv-lds""></i>Gilgamesh [Aether]</p>
    </a>
  </div>
  <div class=""entry"">
    <a href=""/lodestone/character/1681647/"" class=""entry__link"">
      <p class=""entry__name"">Mal Reynolds</p>
      <p class=""entry__world""><i class=""xiv-lds""></i>Gilgamesh{NBSP}[Aether]</p>
    </a>
  </div>
</body></html>";

        private static string SearchHtml => SearchHtmlTemplate.Replace("{NBSP}", " ");

        [TestMethod]
        public void ParseLodestoneCharacterId_PicksExactNameMatch_NotFirstPartialResult()
        {
            var id = FfxivParsing.ParseLodestoneCharacterId(SearchHtml, "Mal Reynolds", "Gilgamesh");
            Assert.AreEqual(1681647L, id);
        }

        [TestMethod]
        public void ParseLodestoneCharacterId_IsCaseInsensitive_AndHandlesNonBreakingSpaceWorld()
        {
            var id = FfxivParsing.ParseLodestoneCharacterId(SearchHtml, "mal reynolds", "gilgamesh");
            Assert.AreEqual(1681647L, id);
        }

        [TestMethod]
        public void ParseLodestoneCharacterId_ReturnsNull_WhenWorldDoesNotMatch()
        {
            var id = FfxivParsing.ParseLodestoneCharacterId(SearchHtml, "Mal Reynolds", "Faerie");
            Assert.IsNull(id);
        }

        [TestMethod]
        public void ParseLodestoneCharacterId_ReturnsNull_WhenNameIsOnlyAPartialMatch()
        {
            var id = FfxivParsing.ParseLodestoneCharacterId(SearchHtml, "Mal", "Gilgamesh");
            Assert.IsNull(id);
        }

        [TestMethod]
        public void ParseLodestoneCharacterId_ReturnsNull_ForEmptyOrMissingInput()
        {
            Assert.IsNull(FfxivParsing.ParseLodestoneCharacterId(string.Empty, "Mal Reynolds", "Gilgamesh"));
            Assert.IsNull(FfxivParsing.ParseLodestoneCharacterId(SearchHtml, "", "Gilgamesh"));
            Assert.IsNull(FfxivParsing.ParseLodestoneCharacterId("<html></html>", "Mal Reynolds", "Gilgamesh"));
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
            Assert.AreEqual("Missable", FfxivParsing.ResolveCategoryType("Seasonal Events"));
            Assert.AreEqual("Missable", FfxivParsing.ResolveCategoryType("seasonal events"));
        }

        [TestMethod]
        public void ResolveCategoryType_ReturnsNull_ForRegularCategories()
        {
            Assert.IsNull(FfxivParsing.ResolveCategoryType("General"));
            Assert.IsNull(FfxivParsing.ResolveCategoryType("Raids"));
            Assert.IsNull(FfxivParsing.ResolveCategoryType(null));
            Assert.IsNull(FfxivParsing.ResolveCategoryType(""));
        }
    }
}
