using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Ffxiv;
using System.Linq;

namespace PlayniteAchievements.Ffxiv.Tests
{
    [TestClass]
    public class FfxivModelsTests
    {
        // Trimmed real responses from https://ffxivcollect.com/api.
        private const string CatalogJson =
            "{\"count\":1,\"results\":[{\"id\":1,\"name\":\"To Crush Your Enemies I\"," +
            "\"description\":\"Defeat 100 enemies.\",\"points\":5,\"order\":1,\"patch\":\"2.0\"," +
            "\"owned\":\"98%\"," +
            "\"icon\":\"https://v2.xivapi.com/api/asset?format=webp&path=ui/icon/002000/002565_hr1.tex\"," +
            "\"category\":{\"id\":1,\"name\":\"General\"},\"type\":{\"id\":1,\"name\":\"Battle\"}}]}";

        private const string CharacterJson =
            "{\"id\":7660136,\"name\":\"Raelys Skyborn\",\"server\":\"Behemoth\",\"data_center\":\"Primal\"," +
            "\"achievements\":{\"count\":2,\"total\":3542,\"public\":true,\"obtained\":[" +
            "{\"id\":1,\"time\":\"2014-05-17T15:11:04.000Z\"}," +
            "{\"id\":2,\"time\":\"2014-05-18T12:38:33.000Z\"}]}}";

        private const string PrivateCharacterJson =
            "{\"id\":1,\"name\":\"Hidden One\",\"server\":\"Behemoth\",\"data_center\":\"Primal\"," +
            "\"achievements\":{\"count\":0,\"total\":3542,\"public\":false}}";

        [TestMethod]
        public void Catalog_DeserializesAchievementFields()
        {
            var response = JsonConvert.DeserializeObject<FfxivAchievementsResponse>(CatalogJson);

            Assert.IsNotNull(response?.Results);
            Assert.AreEqual(1, response.Results.Count);

            var achievement = response.Results[0];
            Assert.AreEqual(1, achievement.Id);
            Assert.AreEqual("To Crush Your Enemies I", achievement.Name);
            Assert.AreEqual(5, achievement.Points);
            Assert.AreEqual("98%", achievement.Owned);
            Assert.AreEqual("General", achievement.Category?.Name);
            Assert.AreEqual("Battle", achievement.Type?.Name);
            StringAssert.Contains(achievement.Icon, "format=webp");
        }

        [TestMethod]
        public void Character_DeserializesObtainedIdsAndTimes()
        {
            var character = JsonConvert.DeserializeObject<FfxivCharacter>(CharacterJson);

            Assert.AreEqual(7660136L, character.Id);
            Assert.AreEqual("Behemoth", character.Server);
            Assert.AreEqual("Primal", character.DataCenter);

            Assert.IsNotNull(character.Achievements);
            Assert.IsTrue(character.Achievements.Public);
            Assert.AreEqual(3542, character.Achievements.Total);

            var obtained = character.Achievements.Obtained;
            Assert.AreEqual(2, obtained.Count);

            var first = obtained.Single(o => o.Id == 1);
            Assert.IsTrue(first.Time.HasValue);
            Assert.AreEqual(2014, first.Time.Value.ToUniversalTime().Year);
            Assert.AreEqual(5, first.Time.Value.ToUniversalTime().Month);
        }

        [TestMethod]
        public void Character_PrivateProfile_HasNoObtainedList()
        {
            var character = JsonConvert.DeserializeObject<FfxivCharacter>(PrivateCharacterJson);

            Assert.IsNotNull(character.Achievements);
            Assert.IsFalse(character.Achievements.Public);
            Assert.IsNull(character.Achievements.Obtained);
        }
    }
}
