using HtmlAgilityPack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Exophase;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ExophaseApiClientTests
    {
        [TestMethod]
        public void ResolveAchievementIconUrl_BlizzardAwardImage_UsesDataNormal()
        {
            var node = LoadNode(@"
<li class=""col-12 locked t1 award visible"" data-earned=""0"">
    <div class=""box image hidden-toggle"">
        <img data-tippy-content=""Guardian""
             class=""award-image trophy-image visible""
             data-normal=""https://m.exophase.com/blizzard/awards/s/7de866e.png?8d203856ce4b449e702586e55c94fef2""
             width=""64""
             height=""64"" />
    </div>
</li>");

            var iconUrl = ExophaseApiClient.ResolveAchievementIconUrl(node);

            Assert.AreEqual(
                "https://m.exophase.com/blizzard/awards/s/7de866e.png?8d203856ce4b449e702586e55c94fef2",
                iconUrl);
        }

        [TestMethod]
        public void ResolveAchievementIconUrl_WithSrcOnly_FallsBackToSrc()
        {
            var node = LoadNode(@"
<li class=""col-12 award visible"">
    <img class=""award-image"" src=""https://m.exophase.com/steam/awards/s/example.png"" />
</li>");

            var iconUrl = ExophaseApiClient.ResolveAchievementIconUrl(node);

            Assert.AreEqual("https://m.exophase.com/steam/awards/s/example.png", iconUrl);
        }

        [TestMethod]
        public void ResolveAchievementIconUrl_WithProtocolRelativeUrl_NormalizesToHttps()
        {
            var node = LoadNode(@"
<li class=""col-12 award visible"">
    <img class=""award-image"" data-normal=""//m.exophase.com/blizzard/awards/s/example.png"" />
</li>");

            var iconUrl = ExophaseApiClient.ResolveAchievementIconUrl(node);

            Assert.AreEqual("https://m.exophase.com/blizzard/awards/s/example.png", iconUrl);
        }

        [TestMethod]
        public void ResolveImageUrl_WithLazyDataSrc_UsesDataSrc()
        {
            var node = LoadImageNode(@"
<div class=""col-image"">
    <img class=""lazyload""
         src=""data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==""
         data-src=""https://m.exophase.com/origin/games/s/titanfall-2.jpg?123"" />
</div>");

            var imageUrl = ExophaseApiClient.ResolveImageUrl(node);

            Assert.AreEqual("https://m.exophase.com/origin/games/s/titanfall-2.jpg?123", imageUrl);
        }

        [TestMethod]
        public void ResolveImageUrl_WithSrcSet_UsesLargestCandidate()
        {
            var node = LoadImageNode(@"
<div class=""col-image"">
    <img srcset=""https://m.exophase.com/origin/games/s/titanfall-2-small.jpg 1x,
                 //m.exophase.com/origin/games/l/titanfall-2-large.jpg 2x"" />
</div>");

            var imageUrl = ExophaseApiClient.ResolveImageUrl(node);

            Assert.AreEqual("https://m.exophase.com/origin/games/l/titanfall-2-large.jpg", imageUrl);
        }

        [TestMethod]
        public void ResolveImageUrl_WithRelativeGameCdnPath_MakesAbsolute()
        {
            var node = LoadImageNode(@"
<div class=""col-image"">
    <img data-src=""/origin/games/s/titanfall-2.jpg?123"" />
</div>");

            var imageUrl = ExophaseApiClient.ResolveImageUrl(node);

            Assert.AreEqual("https://m.exophase.com/origin/games/s/titanfall-2.jpg?123", imageUrl);
        }

        [TestMethod]
        public void BuildSearchUrl_Ps3_RoutesThroughPsnArchiveWithPlatformId()
        {
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/psn?q=demon%20souls&sort=added&platforms=7",
                ExophaseApiClient.BuildSearchUrl("demon souls", "ps3"));
        }

        [TestMethod]
        public void BuildSearchUrl_Ps4_RoutesThroughPsnArchiveWithPlatformId()
        {
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/psn?q=game&sort=added&platforms=8",
                ExophaseApiClient.BuildSearchUrl("game", "ps4"));
        }

        [TestMethod]
        public void BuildSearchUrl_OtherPsnPlatforms_RouteThroughPsnArchiveUnfiltered()
        {
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/psn?q=game&sort=added",
                ExophaseApiClient.BuildSearchUrl("game", "psvita"));
        }

        [TestMethod]
        public void BuildSearchUrl_Xbox360_RoutesThroughXboxArchiveWithPlatformId()
        {
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/xbox?q=game&sort=added&platforms=41",
                ExophaseApiClient.BuildSearchUrl("game", "xbox-360"));
        }

        [TestMethod]
        public void BuildSearchUrl_ModernXbox_RoutesThroughXboxArchiveUnfiltered()
        {
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/xbox?q=game&sort=added",
                ExophaseApiClient.BuildSearchUrl("game", "xbox"));
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/xbox?q=game&sort=added",
                ExophaseApiClient.BuildSearchUrl("game", "xbox-one"));
        }

        [TestMethod]
        public void BuildSearchUrl_NonMappedPlatform_KeepsGenericEndpoint()
        {
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/games?q=game&sort=added&platform=origin",
                ExophaseApiClient.BuildSearchUrl("game", "origin"));
            Assert.AreEqual(
                "https://api.exophase.com/public/archive/games?q=game&sort=added",
                ExophaseApiClient.BuildSearchUrl("game", null));
        }

        [TestMethod]
        public void ExtractSlugFromUrl_ProfileAchievementLinkWithFragment_ReturnsSlug()
        {
            var slug = ExophaseApiClient.ExtractSlugFromUrl(
                "https://www.exophase.com/game/titanfall-2-origin/achievements/#4768201");

            Assert.AreEqual("titanfall-2-origin", slug);
        }

        [TestMethod]
        public void ExtractSlugFromUrl_ProfileAchievementLinkWithQuery_ReturnsSlug()
        {
            var slug = ExophaseApiClient.ExtractSlugFromUrl(
                "https://www.exophase.com/game/titanfall-2-origin/achievements/?foo=bar");

            Assert.AreEqual("titanfall-2-origin", slug);
        }

        [TestMethod]
        public void BuildUrlFromSlug_PlayStationHint_UsesTrophiesEndpoint()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/final-fantasy-vii-rebirth/trophies/",
                ExophaseApiClient.BuildUrlFromSlug("final-fantasy-vii-rebirth", "ps5"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_PsnHint_UsesTrophiesEndpoint()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/some-game/trophies/",
                ExophaseApiClient.BuildUrlFromSlug("some-game", "psn"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_UbisoftHint_UsesChallengesEndpoint()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/prince-of-persia/challenges/",
                ExophaseApiClient.BuildUrlFromSlug("prince-of-persia", "ubisoft"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_SteamHint_UsesAchievementsEndpoint()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/shogun-showdown/achievements/",
                ExophaseApiClient.BuildUrlFromSlug("shogun-showdown", "steam"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_HintOverridesSuffixlessSlug()
        {
            // The slug carries no platform suffix, so only the hint can route it to /trophies/.
            Assert.AreEqual(
                "https://www.exophase.com/game/god-of-war/trophies/",
                ExophaseApiClient.BuildUrlFromSlug("god-of-war", "ps4"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_NoHint_FallsBackToPsnSuffix()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/shogun-showdown-psn/trophies/",
                ExophaseApiClient.BuildUrlFromSlug("shogun-showdown-psn"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_NoHint_FallsBackToUplaySuffix()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/prince-of-persia-the-lost-crown-uplay/challenges/",
                ExophaseApiClient.BuildUrlFromSlug("prince-of-persia-the-lost-crown-uplay"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_NoHint_NonPlatformSlugUsesAchievements()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/shogun-showdown-steam/achievements/",
                ExophaseApiClient.BuildUrlFromSlug("shogun-showdown-steam"));
        }

        [TestMethod]
        public void BuildUrlFromSlug_FullTrophiesUrl_PreservesEndpoint()
        {
            Assert.AreEqual(
                "https://www.exophase.com/game/shogun-showdown-psn/trophies/",
                ExophaseApiClient.BuildUrlFromSlug("https://www.exophase.com/game/shogun-showdown-psn/trophies/"));
        }

        [TestMethod]
        public void TryNormalizeSlugInput_BareSlug_ReturnsTrimmedSlug()
        {
            Assert.IsTrue(ExophaseApiClient.TryNormalizeSlugInput("  guitar-hero-hits-xbox-360 ", out var slug));
            Assert.AreEqual("guitar-hero-hits-xbox-360", slug);
        }

        [TestMethod]
        public void TryNormalizeSlugInput_FullUrls_ExtractSlug()
        {
            var urls = new[]
            {
                "https://www.exophase.com/game/guitar-hero-hits-xbox-360/achievements/",
                "https://www.exophase.com/game/guitar-hero-hits-xbox-360/trophies/",
                "https://www.exophase.com/game/guitar-hero-hits-xbox-360/challenges/",
                "https://www.exophase.com/game/guitar-hero-hits-xbox-360/achievements/#4768201",
                "www.exophase.com/game/guitar-hero-hits-xbox-360/achievements/"
            };

            foreach (var url in urls)
            {
                Assert.IsTrue(ExophaseApiClient.TryNormalizeSlugInput(url, out var slug), url);
                Assert.AreEqual("guitar-hero-hits-xbox-360", slug, url);
            }
        }

        [TestMethod]
        public void TryNormalizeSlugInput_InvalidInput_ReturnsFalse()
        {
            var invalid = new[]
            {
                null,
                "",
                "   ",
                "https://www.exophase.com/user/somebody/",
                "two words",
                "guitar hero smash hits"
            };

            foreach (var input in invalid)
            {
                Assert.IsFalse(ExophaseApiClient.TryNormalizeSlugInput(input, out _), input ?? "<null>");
            }
        }

        private static HtmlNode LoadNode(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc.DocumentNode.SelectSingleNode("//li");
        }

        private static HtmlNode LoadImageNode(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc.DocumentNode.SelectSingleNode("//div");
        }
    }
}
