using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Hoyoverse;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers.Hoyoverse
{
    [TestClass]
    public class HoyoverseDefinitionClientTests
    {
        [TestMethod]
        public void ParseGenshinDefinitions_FlattensPaimonMoePayload()
        {
            var json = @"{
              ""wonders"": {
                ""name"": ""Wonders of the World"",
                ""order"": 1,
                ""achievements"": {
                  ""1001"": { ""id"": 1001, ""name"": ""Open World"", ""desc"": ""Find the thing."", ""reward"": 5 },
                  ""1002"": { ""id"": 1002, ""name"": ""Hidden Prize"", ""desc"": ""Find the other thing."", ""reward"": 20 }
                }
              }
            }";

            var definitions = HoyoverseDefinitionClient.ParseGenshinDefinitions(json);

            Assert.AreEqual(2, definitions.Count);
            Assert.AreEqual("1001", definitions[0].ApiName);
            Assert.AreEqual("Wonders of the World", definitions[0].Category);
            StringAssert.EndsWith(definitions[0].UnlockedIconPath, Path.Combine("Resources", "Hoyoverse", "GenshinImpact", "0.png"));
            Assert.IsTrue(File.Exists(definitions[0].UnlockedIconPath));
            Assert.AreEqual(RarityTier.Rare, definitions.Single(a => a.ApiName == "1002").Rarity);
        }

        [TestMethod]
        public void ParseHonkaiStarRailDefinitions_ResolvesTextMapAndSeries()
        {
            var achievements = @"[
              {
                ""AchievementID"": 2001,
                ""SeriesID"": 7,
                ""AchievementTitle"": { ""Hash"": ""10001"" },
                ""AchievementDesc"": { ""Hash"": ""10002"" },
                ""ParamList"": [{ ""Value"": ""3"" }],
                ""Rarity"": ""Mid""
              }
            ]";
            var series = @"[{ ""SeriesID"": 7, ""SeriesTitle"": { ""Hash"": ""20001"" }, ""MainIconPath"": ""SpriteOutput/Achievement/BattleAchievementIcon.png"" }]";
            var textMap = @"{
              ""10001"": ""Trailblazer"",
              ""10002"": ""Complete #1[i] missions."",
              ""20001"": ""The Rail Unto the Stars""
            }";

            var definitions = HoyoverseDefinitionClient.ParseHonkaiStarRailDefinitions(achievements, series, textMap);

            Assert.AreEqual(1, definitions.Count);
            Assert.AreEqual("2001", definitions[0].ApiName);
            Assert.AreEqual("Trailblazer", definitions[0].DisplayName);
            Assert.AreEqual("Complete 3 missions.", definitions[0].Description);
            Assert.AreEqual("The Rail Unto the Stars", definitions[0].Category);
            StringAssert.EndsWith(definitions[0].UnlockedIconPath, Path.Combine("Resources", "Hoyoverse", "HonkaiStarRail", "BattleAchievementIcon.png"));
            Assert.IsTrue(File.Exists(definitions[0].UnlockedIconPath));
            Assert.AreEqual(RarityTier.Uncommon, definitions[0].Rarity);
        }

        [TestMethod]
        public void ParseZenlessZoneZeroDefinitions_NormalizesAssetPayload()
        {
            var js = @"const e={100:{n:'Primer',a:[{id:3001,n:'Coffee Time',d:'Drink coffee.',r:5},{id:3002,n:'Rare Find',d:'Find it.',r:20}]}};export{e as default};";

            var definitions = HoyoverseDefinitionClient.ParseZenlessZoneZeroDefinitions(js);

            Assert.AreEqual(2, definitions.Count);
            Assert.AreEqual("3001", definitions[0].ApiName);
            Assert.AreEqual("Primer", definitions[0].Category);
            Assert.IsTrue(File.Exists(definitions[0].UnlockedIconPath));
            Assert.AreEqual(RarityTier.Rare, definitions.Single(a => a.ApiName == "3002").Rarity);
        }

        [TestMethod]
        public void ParseZenlessZoneZeroDefinitions_PreservesApostrophesInLiveSeeliePayload()
        {
            var js = @"const e={1004:{n:""Agent Trust"",o:4,a:[{id:1004001,n:""Movie Lovers Can't be Bad Guys"",d:""Reach Trust Lv. 4 with Anby."",r:5,v:""1.0"",t:1},{id:1004016,n:'""With Friends, You Are Not Lonely""',d:""Reach Trust Lv. 4 with Seth."",r:5,v:""1.1"",t:1},{id:1004019,n:""Don't Make It Bald"",d:""Pet Inky 3 times at the video store."",r:5,v:""1.0"",t:1}]}};export{e as default};";

            var definitions = HoyoverseDefinitionClient.ParseZenlessZoneZeroDefinitions(js);

            Assert.AreEqual(3, definitions.Count);
            Assert.AreEqual("Movie Lovers Can't be Bad Guys", definitions.Single(a => a.ApiName == "1004001").DisplayName);
            Assert.AreEqual(@"""With Friends, You Are Not Lonely""", definitions.Single(a => a.ApiName == "1004016").DisplayName);
            Assert.AreEqual("Don't Make It Bald", definitions.Single(a => a.ApiName == "1004019").DisplayName);
        }

        [TestMethod]
        public void FindZzzAchievementAsset_SupportsRelativeAndAbsoluteLocaleChunks()
        {
            var relativeIndex = @"const a=()=>import(""./locale/achievements-en-fa79791d.js"");";
            var absoluteIndex = @"const a=""/assets/locale/achievements-en-12345678.js"";";
            var rootLocaleIndex = @"const a=""/locale/achievements-en-fa79791d.js"";";

            Assert.AreEqual(
                "/assets/locale/achievements-en-fa79791d.js",
                HoyoverseDefinitionClient.FindZzzAchievementAsset(relativeIndex, "en"));
            Assert.AreEqual(
                "/assets/locale/achievements-en-12345678.js",
                HoyoverseDefinitionClient.FindZzzAchievementAsset(absoluteIndex, "en"));
            Assert.AreEqual(
                "/assets/locale/achievements-en-fa79791d.js",
                HoyoverseDefinitionClient.FindZzzAchievementAsset(rootLocaleIndex, "en"));
        }

        [TestMethod]
        public void MapGlobalLanguageToZzzLocale_UsesSeelieTraditionalChineseChunkName()
        {
            Assert.AreEqual("tw", HoyoverseDefinitionClient.MapGlobalLanguageToZzzLocale("tchinese"));
            Assert.AreEqual("tw", HoyoverseDefinitionClient.MapGlobalLanguageToZzzLocale("zh-tw"));
        }
    }
}
