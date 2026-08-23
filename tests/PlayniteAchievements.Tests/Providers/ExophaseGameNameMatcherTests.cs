using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Exophase;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class ExophaseGameNameMatcherTests
    {
        [TestMethod]
        public void NormalizeGameName_StripsDeluxeEditionSuffix()
        {
            Assert.AreEqual("Titanfall 2", ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 Deluxe Edition"));
            Assert.AreEqual("Titanfall 2", ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 - Deluxe Edition"));
            Assert.AreEqual("Titanfall 2", ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 (Deluxe Edition)"));
        }

        [TestMethod]
        public void NormalizeGameName_StripsDirectorsCutSuffix()
        {
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima DIRECTOR'S CUT"));
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima - Director's Cut"));
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima (Director's Cut)"));
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima Directors Cut"));
        }

        [TestMethod]
        public void NormalizeGameName_LeavesUnlistedEditionWordsIntact()
        {
            // "Legendary Edition" is not a stripped suffix, so both the friend title and the
            // library game keep it and still normalize identically.
            Assert.AreEqual(
                "Mass Effect Legendary Edition",
                ExophaseGameNameMatcher.NormalizeGameName("Mass Effect Legendary Edition"));
        }

        [TestMethod]
        public void ComputeMatchScore_TitanfallDeluxeMatchesFriendTitanfall2Exactly()
        {
            var library = ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 Deluxe Edition");
            var friend = ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2");

            Assert.AreEqual(
                ExophaseGameNameMatcher.ExactMatchScore,
                ExophaseGameNameMatcher.ComputeMatchScore(friend, library));
        }

        [TestMethod]
        public void ComputeMatchScore_MassEffectLegendaryEditionMatchesExactly()
        {
            var library = ExophaseGameNameMatcher.NormalizeGameName("Mass Effect Legendary Edition");
            var friend = ExophaseGameNameMatcher.NormalizeGameName("Mass Effect Legendary Edition");

            Assert.AreEqual(
                ExophaseGameNameMatcher.ExactMatchScore,
                ExophaseGameNameMatcher.ComputeMatchScore(friend, library));
        }

        [TestMethod]
        public void ComputeMatchScore_SequelIsNotAnExactMatch()
        {
            // A friend's "Titanfall" must not auto-map onto a library "Titanfall 2":
            // it scores as a prefix (80), never an exact (100) match.
            var score = ExophaseGameNameMatcher.ComputeMatchScore(
                ExophaseGameNameMatcher.NormalizeGameName("Titanfall"),
                ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2"));

            Assert.AreNotEqual(ExophaseGameNameMatcher.ExactMatchScore, score);
            Assert.IsTrue(score < ExophaseGameNameMatcher.ExactMatchScore);
        }

        [TestMethod]
        public void ComputeMatchScore_IsCaseInsensitive()
        {
            Assert.AreEqual(
                ExophaseGameNameMatcher.ExactMatchScore,
                ExophaseGameNameMatcher.ComputeMatchScore("titanfall 2", "Titanfall 2"));
        }

        [TestMethod]
        public void NormalizeGameNameForSlug_StripsEditionAndHyphenates()
        {
            Assert.AreEqual("titanfall-2", ExophaseGameNameMatcher.NormalizeGameNameForSlug("Titanfall 2 Deluxe Edition"));
        }

        [TestMethod]
        public void SelectBestSearchMatch_RegionalVariants_PicksBaseSlug()
        {
            // Exophase lists one PS3 game once per region under the same title,
            // with the base region on the shortest slug.
            var games = new[]
            {
                SearchRow("Demon's Souls", "demons-souls-3", "ps3"),
                SearchRow("Demon's Souls", "demons-souls-2", "ps3"),
                SearchRow("Demon's Souls", "demons-souls", "ps3")
            };

            var match = ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3");

            Assert.IsNotNull(match);
            StringAssert.Contains(match.EndpointAwards, "/game/demons-souls/");
        }

        [TestMethod]
        public void SelectBestSearchMatch_FiltersOtherPlatformsBeforeTieBreak()
        {
            // A same-title release on another platform must not win the shortest-slug
            // tie-break over the target platform's own entry.
            var games = new[]
            {
                SearchRow("Demon's Souls", "demons-souls-remake", "ps5"),
                SearchRow("Demon's Souls", "demons-souls-2", "ps3")
            };

            var match = ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3");

            Assert.IsNotNull(match);
            StringAssert.Contains(match.EndpointAwards, "/game/demons-souls-2/");
        }

        [TestMethod]
        public void SelectBestSearchMatch_OnlyOtherPlatformRows_ReturnsNull()
        {
            var games = new[]
            {
                SearchRow("Demon's Souls", "demons-souls-remake", "ps5"),
                SearchRow("Demon's Souls", "demons-souls-ps4", "ps4")
            };

            Assert.IsNull(ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3"));
        }

        [TestMethod]
        public void SelectBestSearchMatch_NoPlatformData_TieYieldsNull()
        {
            var games = new[]
            {
                SearchRow("Demon's Souls", "demons-souls"),
                SearchRow("Demon's Souls", "demons-souls-2")
            };

            Assert.IsNull(ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3"));
        }

        [TestMethod]
        public void SelectBestSearchMatch_NoPlatformData_UrlSuffixStillMatches()
        {
            var games = new[]
            {
                SearchRow("Halo 3", "halo-3-xbox-360"),
                SearchRow("Halo 3", "halo-3-mcc")
            };

            var match = ExophaseGameNameMatcher.SelectBestSearchMatch("Halo 3", games, "xbox-360");

            Assert.IsNotNull(match);
            StringAssert.Contains(match.EndpointAwards, "/game/halo-3-xbox-360/");
        }

        [TestMethod]
        public void SelectBestSearchMatch_BelowThreshold_ReturnsNull()
        {
            var games = new[] { SearchRow("Completely Different Title", "completely-different-title", "ps3") };

            Assert.IsNull(ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3"));
        }

        [TestMethod]
        public void SelectBestSearchMatch_RegionHint_PicksMatchingRegionOverBaseSlug()
        {
            var games = new[]
            {
                RegionRow("Demon's Souls", "demons-souls", "NA", "ps3"),
                RegionRow("Demon's Souls", "demons-souls-2", "EU", "ps3"),
                RegionRow("Demon's Souls", "demons-souls-3", "AS", "ps3")
            };

            var match = ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3", "eu");

            Assert.IsNotNull(match);
            StringAssert.Contains(match.EndpointAwards, "/game/demons-souls-2/");
        }

        [TestMethod]
        public void SelectBestSearchMatch_RegionHint_TreatsUsAndNaAsEquivalent()
        {
            var games = new[]
            {
                RegionRow("Demon's Souls", "demons-souls-2", "EU", "ps3"),
                RegionRow("Demon's Souls", "demons-souls", "US", "ps3")
            };

            var match = ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3", "na");

            Assert.IsNotNull(match);
            StringAssert.Contains(match.EndpointAwards, "/game/demons-souls/");
        }

        [TestMethod]
        public void SelectBestSearchMatch_RegionHintAbsentFromRows_FallsBackToBaseSlug()
        {
            // Rows without region data keep the shortest-slug (base region) pick.
            var games = new[]
            {
                SearchRow("Demon's Souls", "demons-souls-2", "ps3"),
                SearchRow("Demon's Souls", "demons-souls", "ps3")
            };

            var match = ExophaseGameNameMatcher.SelectBestSearchMatch("Demon's Souls", games, "ps3", "jp");

            Assert.IsNotNull(match);
            StringAssert.Contains(match.EndpointAwards, "/game/demons-souls/");
        }

        [TestMethod]
        public void MapPsnSerialToRegionHint_MapsRegionLetter()
        {
            Assert.AreEqual("eu", ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BLES01234"));
            Assert.AreEqual("eu", ExophaseGameNameMatcher.MapPsnSerialToRegionHint("NPEB00033"));
            Assert.AreEqual("na", ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BLUS30443"));
            Assert.AreEqual("na", ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BCUS98246"));
            Assert.AreEqual("jp", ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BLJS10012"));
            Assert.AreEqual("as", ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BCAS20120"));
            Assert.AreEqual("kr", ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BLKS20345"));
            Assert.IsNull(ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BLQS00000"));
            Assert.IsNull(ExophaseGameNameMatcher.MapPsnSerialToRegionHint(null));
            Assert.IsNull(ExophaseGameNameMatcher.MapPsnSerialToRegionHint("BL"));
        }

        [TestMethod]
        public void MapPsnContentIdToRegionHint_MapsPrefixLetter()
        {
            Assert.AreEqual("na", ExophaseGameNameMatcher.MapPsnContentIdToRegionHint("UP9000-NPWR05784_00"));
            Assert.AreEqual("eu", ExophaseGameNameMatcher.MapPsnContentIdToRegionHint("EP9000-CUSA00552_00"));
            Assert.AreEqual("jp", ExophaseGameNameMatcher.MapPsnContentIdToRegionHint("JP0082-NPWR12345_00"));
            Assert.AreEqual("as", ExophaseGameNameMatcher.MapPsnContentIdToRegionHint("HP9000-NPWR12345_00"));
            Assert.IsNull(ExophaseGameNameMatcher.MapPsnContentIdToRegionHint("XX9000-NPWR12345_00"));
            Assert.IsNull(ExophaseGameNameMatcher.MapPsnContentIdToRegionHint(null));
        }

        private static ExophaseGame RegionRow(string title, string slug, string region, params string[] platformSlugs)
        {
            var row = SearchRow(title, slug, platformSlugs);
            row.Region = region;
            return row;
        }

        private static ExophaseGame SearchRow(string title, string slug, params string[] platformSlugs)
        {
            return new ExophaseGame
            {
                Title = title,
                EndpointAwards = $"https://www.exophase.com/game/{slug}/trophies/",
                Platforms = platformSlugs == null || platformSlugs.Length == 0
                    ? null
                    : platformSlugs.Select(s => new ExophasePlatform { Name = s, Slug = s }).ToList()
            };
        }
    }
}
