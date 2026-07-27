using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamImageUrlsTests
    {
        [TestMethod]
        public void Cover_BuildsLibraryPortraitUrl()
        {
            Assert.AreEqual(
                "https://cdn.akamai.steamstatic.com/steam/apps/620/library_600x900.jpg",
                SteamImageUrls.Cover(620));
        }

        [TestMethod]
        public void Icon_BuildsCapsuleUrl()
        {
            Assert.AreEqual(
                "https://cdn.akamai.steamstatic.com/steam/apps/620/capsule_231x87.jpg",
                SteamImageUrls.Icon(620));
        }

        [TestMethod]
        public void Header_BuildsHeaderUrl()
        {
            Assert.AreEqual(
                "https://cdn.akamai.steamstatic.com/steam/apps/620/header.jpg",
                SteamImageUrls.Header(620));
        }

        [TestMethod]
        public void LibraryHero_BuildsHeroUrl()
        {
            Assert.AreEqual(
                "https://cdn.akamai.steamstatic.com/steam/apps/620/library_hero.jpg",
                SteamImageUrls.LibraryHero(620));
        }

        [TestMethod]
        public void ParseStoreFallback_UsesHashedStoreUrls()
        {
            var json =
                @"{
                    ""3768760"": {
                        ""success"": true,
                        ""data"": {
                            ""header_image"": ""https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3768760/hash/header.jpg?t=1"",
                            ""capsule_image"": ""https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3768760/hash/capsule_231x87.jpg?t=1"",
                            ""capsule_imagev5"": ""https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3768760/hash/capsule_184x69.jpg?t=1""
                        }
                    }
                }";

            var result = SteamImageUrls.ParseStoreFallback(3768760, json);

            Assert.IsNotNull(result);
            Assert.AreEqual(
                "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3768760/hash/capsule_231x87.jpg?t=1",
                result.IconUrl);
            Assert.AreEqual(
                "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3768760/hash/header.jpg?t=1",
                result.CoverUrl);
        }

        [TestMethod]
        public void ParseStoreFallback_ReturnsNull_WhenAppMissing()
        {
            var json = @"{ ""620"": { ""success"": true, ""data"": { ""header_image"": ""https://example/header.jpg"" } } }";

            Assert.IsNull(SteamImageUrls.ParseStoreFallback(10, json));
        }

        [TestMethod]
        public void Cover_ReturnsNull_ForInvalidAppId()
        {
            Assert.IsNull(SteamImageUrls.Cover(0));
            Assert.IsNull(SteamImageUrls.Icon(-1));
            Assert.IsNull(SteamImageUrls.Header(0));
            Assert.IsNull(SteamImageUrls.LibraryHero(-1));
        }
    }
}
