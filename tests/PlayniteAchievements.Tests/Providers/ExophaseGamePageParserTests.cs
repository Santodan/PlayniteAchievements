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
