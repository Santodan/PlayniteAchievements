using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Steam;

namespace PlayniteAchievements.Tests.Manual
{
    [TestClass]
    public class ManualSourceGameSearchTests
    {
        [TestMethod]
        public void SteamAppIds_ReadsBareIdsAndAppAddresses()
        {
            Assert.AreEqual(620, SteamAppIds.TryParse("620"));
            Assert.AreEqual(620, SteamAppIds.TryParse(" https://store.steampowered.com/app/620/Portal_2/ "));
            Assert.AreEqual(620, SteamAppIds.TryParse("https://steamcommunity.com/app/620/achievements/"));
            Assert.AreEqual(620, SteamAppIds.TryParse("steamdb.info/app/620/"));
        }

        [TestMethod]
        public void SteamAppIds_RejectsNamesAndNonPositiveIds()
        {
            Assert.IsNull(SteamAppIds.TryParse("Portal 2"));
            Assert.IsNull(SteamAppIds.TryParse("0"));
            Assert.IsNull(SteamAppIds.TryParse("-5"));
            Assert.IsNull(SteamAppIds.TryParse("https://store.steampowered.com/bundle/620/"));
        }

        [TestMethod]
        public void ExophaseSlug_ReadsAddressesWithQueryOrFragment()
        {
            Assert.AreEqual("hades-steam", ExophaseApiClient.TryParseGameSlug("https://www.exophase.com/game/hades-steam/achievements/#123"));
            Assert.AreEqual("hades-psn", ExophaseApiClient.TryParseGameSlug("https://www.exophase.com/game/hades-psn/trophies/?sort=rarity"));
        }

        [TestMethod]
        public void ExophaseSlug_TakesABareSlugOnlyWithAPlatformSuffix()
        {
            Assert.AreEqual("hades-steam", ExophaseApiClient.TryParseGameSlug("Hades-Steam"));
            Assert.IsNull(ExophaseApiClient.TryParseGameSlug("hades"));
            Assert.IsNull(ExophaseApiClient.TryParseGameSlug("Hades"));
            Assert.IsNull(ExophaseApiClient.TryParseGameSlug("dark-souls"));
            Assert.IsNull(ExophaseApiClient.TryParseGameSlug("hades steam"));
        }
    }
}
