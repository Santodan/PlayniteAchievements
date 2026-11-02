using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Exophase;
using System.Linq;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ExophaseGamePageParserTests
    {
        private const string GameInfoHtml = @"
<aside class=""group"">
    <dl class=""details"">
        <dt>Also on:</dt>
        <dd>
            <a href=""https://www.exophase.com/game/god-of-war-ragnarok-psn-2/trophies/"">PS4</a>,
            <a href=""https://www.exophase.com/game/god-of-war-ragnarok-steam/achievements/"">Steam</a>
        </dd>
        <dt>Current Price:</dt>
        <dd><a href=""https://www.exophase.com/game/god-of-war-ragnarok-psn/prices/"">$29.40</a></dd>
        <dt class=""languages"">Languages:</dt>
        <dd><a href=""https://www.exophase.com/game/god-of-war-ragnarok-psn/trophies/jp/"">Japanese</a></dd>
    </dl>
</aside>";

        [TestMethod]
        public void ParseRelatedPages_ReadsOtherPlatformAwardPagesOnly()
        {
            var pages = ExophaseGamePageParser.ParseRelatedPages(GameInfoHtml);

            CollectionAssert.AreEqual(
                new[] { "PS4:god-of-war-ragnarok-psn-2", "Steam:god-of-war-ragnarok-steam" },
                pages.Select(page => page.Platform + ":" + page.Slug).ToArray());
        }

        [TestMethod]
        public void ParseRelatedPages_EmptyForPagesWithoutGameInfo()
        {
            Assert.AreEqual(0, ExophaseGamePageParser.ParseRelatedPages("<html><body></body></html>").Count);
            Assert.AreEqual(0, ExophaseGamePageParser.ParseRelatedPages(null).Count);
        }

        [TestMethod]
        public void GameArt_IsThePagesShareImage()
        {
            Assert.AreEqual(
                "https://m.exophase.com/psn/games/l/abc.png?1",
                ExophaseGamePageParser.GameArt("<html><head><meta property=\"og:image\" content=\"https://m.exophase.com/psn/games/l/abc.png?1\"></head></html>"));
            Assert.IsNull(ExophaseGamePageParser.GameArt("<html><head></head></html>"));
            Assert.IsNull(ExophaseGamePageParser.GameArt(null));
        }

        [TestMethod]
        public void TrophyLanguageCode_MapsPluginLanguagesToExophasePaths()
        {
            Assert.AreEqual("jp", ExophaseGamePageParser.TrophyLanguageCode("japanese"));
            Assert.AreEqual("kr", ExophaseGamePageParser.TrophyLanguageCode("koreana"));
            Assert.AreEqual("pt-BR", ExophaseGamePageParser.TrophyLanguageCode("brazilian"));
            Assert.AreEqual("zh-CN", ExophaseGamePageParser.TrophyLanguageCode("schinese"));
            Assert.IsNull(ExophaseGamePageParser.TrophyLanguageCode("english"));
            Assert.IsNull(ExophaseGamePageParser.TrophyLanguageCode("vietnamese"));
        }

        [TestMethod]
        public void ParseAwardSections_FilesAwardsUnderTheirDlcAndKeepsOnlyDlcArt()
        {
            const string html = @"<html><head>
<meta property=""og:image"" content=""https://m.exophase.com/psn/games/o/abc123.png?x"" /></head><body>
<ul class=""list-unordered-base psn trophy"">
  <li data-master=""11""></li><li id=""12""></li><li></li>
</ul>
<h3 class=""dlc-add-on""><span>Automatron Trophies</span></h3>
<style>h3.dlc-add-on::before { background-image: url('https://m.exophase.com/psn/games/l/abc123.png?y'); }</style>
<ul class=""list-unordered-base psn trophy""><li data-master=""21""></li></ul>
<h3 class=""dlc-add-on""><span>Far Harbor Trophies</span></h3>
<style>h3.dlc-add-on::before { background-image: url('https://m.exophase.com/psn/dlc/l/far.png'); }</style>
<ul class=""list-unordered-base psn trophy""><li data-master=""31""></li></ul>
</body></html>";

            var sections = ExophaseGamePageParser.ParseAwardSections(html);

            Assert.AreEqual(3, sections.Count);
            Assert.IsNull(sections[0].Title);
            CollectionAssert.AreEqual(new[] { "exophase:11", "exophase:12" }, sections[0].ApiNames.ToList());
            Assert.AreEqual("Automatron", sections[1].Title);
            Assert.IsNull(sections[1].ArtUrl, "A DLC shown with the game's own image has no art of its own.");
            Assert.AreEqual("Far Harbor", sections[2].Title);
            Assert.AreEqual("https://m.exophase.com/psn/dlc/l/far.png", sections[2].ArtUrl);
            CollectionAssert.AreEqual(new[] { "exophase:31" }, sections[2].ApiNames.ToList());
        }

        [TestMethod]
        public void DocumentOverloads_ReadTheSameAsTheStringParsers()
        {
            var html = "<html><head><meta property=\"og:image\" content=\"https://m.exophase.com/psn/games/o/abc.png\" /></head><body>" +
                       GameInfoHtml +
                       "<ul class=\"list-unordered-base psn trophy\"><li data-master=\"11\"></li></ul></body></html>";
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            var fromDoc = ExophaseGamePageParser.ParseAwardSectionsFrom(doc, out var docArt);
            var fromString = ExophaseGamePageParser.ParseAwardSections(html, out var stringArt);

            Assert.AreEqual(stringArt, docArt);
            CollectionAssert.AreEqual(
                fromString.SelectMany(section => section.ApiNames).ToList(),
                fromDoc.SelectMany(section => section.ApiNames).ToList());
            CollectionAssert.AreEqual(
                ExophaseGamePageParser.ParseRelatedPages(html).Select(page => page.Slug).ToList(),
                ExophaseGamePageParser.ParseRelatedPagesFrom(doc).Select(page => page.Slug).ToList());
        }

        [TestMethod]
        public void ParsePagePlatformFrom_ReadsTheAwardListPlatformClass()
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml("<ul class=\"row gx-0 justify-content-center list-unordered-base psn trophy platform-ps3\"><li data-master=\"11\"></li></ul>");

            Assert.AreEqual("ps3", ExophaseGamePageParser.ParsePagePlatformFrom(doc));
        }

        [TestMethod]
        public void ParsePagePlatformFrom_IsNullWithoutAPlatformClass()
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(GameInfoHtml + "<ul class=\"list-unordered-base psn trophy\"><li data-master=\"11\"></li></ul>");

            Assert.IsNull(ExophaseGamePageParser.ParsePagePlatformFrom(doc));
        }

        [TestMethod]
        public void LocalizeAwardPageUrl_AppendsTheLanguagePathToEitherList()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/god-of-war-ragnarok-psn/trophies/",
                ExophaseGamePageParser.LocalizeAwardPageUrl(ExophaseApiClient.BuildUrlFromSlug("god-of-war-ragnarok-psn", "psn"), null));
            Assert.AreEqual(
                "https://www.exophase.com/game/god-of-war-ragnarok-psn/trophies/fr/",
                ExophaseGamePageParser.LocalizeAwardPageUrl(ExophaseApiClient.BuildUrlFromSlug("god-of-war-ragnarok-psn", "psn"), "fr"));
            Assert.AreEqual(
                "https://www.exophase.com/game/fallout-4-xbox-one/achievements/fr/",
                ExophaseGamePageParser.LocalizeAwardPageUrl(ExophaseApiClient.BuildUrlFromSlug("fallout-4-xbox-one", "xbox"), "fr"));
        }
    }
}
