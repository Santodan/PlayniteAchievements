using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.BattleNet;
using PlayniteAchievements.Providers.BattleNet.Models;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class BattleNetProviderTests
    {
        [TestMethod]
        public void LocaleMapper_MapsSteamLanguageKeys()
        {
            Assert.AreEqual("de-de", BattleNetLocaleMapper.ToWowWebLocale("german"));
            Assert.AreEqual("de_DE", BattleNetLocaleMapper.ToApiLocale("german"));
        }

        [TestMethod]
        public void LocaleMapper_NormalizesExplicitLocales()
        {
            Assert.AreEqual("de-de", BattleNetLocaleMapper.ToWowWebLocale("de-DE"));
            Assert.AreEqual("de_DE", BattleNetLocaleMapper.ToApiLocale("de-DE"));
        }

        [TestMethod]
        public void LocaleMapper_FallsBackToEnglish()
        {
            Assert.AreEqual("en-us", BattleNetLocaleMapper.ToWowWebLocale(null));
            Assert.AreEqual("en_US", BattleNetLocaleMapper.ToApiLocale(""));
            Assert.AreEqual("en-us", BattleNetLocaleMapper.ToWowWebLocale("not-a-real-language"));
            Assert.AreEqual("en_US", BattleNetLocaleMapper.ToApiLocale("not-a-real-language"));
            Assert.AreEqual("en-us", BattleNetLocaleMapper.ToWowWebLocale("zz-ZZ"));
            Assert.AreEqual("en_US", BattleNetLocaleMapper.ToApiLocale("zz-ZZ"));
        }

        [TestMethod]
        public void WowParser_ReadsLiveSubcategoryObjectShape()
        {
            var payload = JsonConvert.DeserializeObject<WowAchievementsData>(@"
{
  ""name"": ""Characters"",
  ""category"": ""characters"",
  ""achievementsList"": [
    {
      ""id"": 1,
      ""name"": ""Duplicate top-level item"",
      ""description"": ""Top-level fallback should not duplicate subcategory data."",
      ""point"": 5
    }
  ],
  ""subcategories"": {
    ""global"": {
      ""id"": ""global"",
      ""name"": ""Global"",
      ""achievements"": [
        {
          ""id"": 1,
          ""name"": ""Midnight Epic"",
          ""description"": ""Equip an epic item."",
          ""icon"": { ""url"": ""https://example.test/icon.jpg"" },
          ""point"": 10,
          ""time"": ""2026-03-26T23:21:56Z""
        },
        {
          ""id"": 2,
          ""name"": ""Locked Achievement"",
          ""description"": ""Still locked."",
          ""point"": 0
        }
      ]
    }
  }
}");

            var achievements = WowGameStrategy.ParseAchievements(new[] { payload });

            Assert.AreEqual(2, achievements.Count);
            var unlocked = achievements.Single(item => item.ApiName == "1");
            Assert.AreEqual("Midnight Epic", unlocked.DisplayName);
            Assert.AreEqual("Equip an epic item.", unlocked.Description);
            Assert.AreEqual("https://example.test/icon.jpg", unlocked.UnlockedIconPath);
            Assert.AreEqual(10, unlocked.Points);
            Assert.AreEqual("Characters::Global", unlocked.Category);
            Assert.IsTrue(unlocked.Unlocked);
            Assert.AreEqual(DateTimeKind.Utc, unlocked.UnlockTimeUtc.Value.Kind);

            var locked = achievements.Single(item => item.ApiName == "2");
            Assert.IsFalse(locked.Unlocked);
            Assert.IsFalse(locked.UnlockTimeUtc.HasValue);
        }

        [TestMethod]
        public void WowParser_FallsBackToTheCategoryWhenASubcategoryIsUnnamed()
        {
            var payload = JsonConvert.DeserializeObject<WowAchievementsData>(@"
{
  ""name"": ""Exploration"",
  ""subcategories"": {
    ""blank"": {
      ""id"": ""blank"",
      ""achievements"": [ { ""id"": 40, ""name"": ""Wanderer"" } ]
    }
  }
}");

            var achievements = WowGameStrategy.ParseAchievements(new[] { payload });

            Assert.AreEqual("Exploration", achievements.Single().Category);
        }

        [TestMethod]
        public void WowParser_KeepsASubcategoryNameContainingTheSeparatorInOneSegment()
        {
            var payload = JsonConvert.DeserializeObject<WowAchievementsData>(@"
{
  ""name"": ""Dungeons"",
  ""subcategories"": {
    ""odd"": {
      ""id"": ""odd"",
      ""name"": ""Wing A::Wing B"",
      ""achievements"": [ { ""id"": 41, ""name"": ""Clear It"" } ]
    }
  }
}");

            var category = WowGameStrategy.ParseAchievements(new[] { payload }).Single().Category;

            Assert.AreEqual("Dungeons::Wing A:Wing B", category);
            Assert.AreEqual(2, CategoryPathHelper.Split(category).Count);
        }

        [TestMethod]
        public void WowParser_DoesNotFilterByNameAtParseTime()
        {
            // The public web catalog never fetches the guild category, and guild
            // exclusion is now id-based at the official-merge level, so the public
            // parser keeps every achievement it is given regardless of name.
            var publicCategory = JsonConvert.DeserializeObject<WowAchievementsData>(@"
{
  ""name"": ""World Events"",
  ""achievementsList"": [
    {
      ""id"": 10,
      ""name"": ""Guild Run: Test Dungeon"",
      ""description"": ""Complete the dungeon with your guild.""
    },
    {
      ""id"": 12,
      ""name"": ""The First Rule of Brawler's Guild"",
      ""description"": ""Get invited to brawl.""
    }
  ]
}");

            var achievements = WowGameStrategy.ParseAchievements(new[] { publicCategory });

            Assert.IsTrue(achievements.Any(item => item.ApiName == "10"));
            Assert.IsTrue(achievements.Any(item => item.ApiName == "12"));
        }

        [TestMethod]
        public void IsGuildCategory_MatchesByCategoryId()
        {
            var guildIds = new HashSet<int> { 15076, 15093 };

            Assert.IsTrue(WowGameStrategy.IsGuildCategory(
                new WowOfficialAchievementCategory { Id = 15076, Name = "Guild" }, guildIds));
            Assert.IsTrue(WowGameStrategy.IsGuildCategory(
                new WowOfficialAchievementCategory { Id = 15093, Name = "Gilde" }, guildIds));
            Assert.IsFalse(WowGameStrategy.IsGuildCategory(
                new WowOfficialAchievementCategory { Id = 92, Name = "Quests" }, guildIds));
            Assert.IsFalse(WowGameStrategy.IsGuildCategory(null, guildIds));
            Assert.IsFalse(WowGameStrategy.IsGuildCategory(
                new WowOfficialAchievementCategory { Id = 15076 }, null));
            Assert.IsFalse(WowGameStrategy.IsGuildCategory(
                new WowOfficialAchievementCategory { Id = 0, Name = "Guild" }, guildIds));
        }

        [TestMethod]
        public void Settings_DefaultsDataForAzerothWowRarityOn()
        {
            var settings = new BattleNetSettings();

            Assert.IsTrue(settings.UseDataForAzerothForWowRarity);
        }

        [TestMethod]
        public void Settings_DefaultsToLoopbackHttpRedirectUri()
        {
            var settings = new BattleNetSettings();

            Assert.AreEqual("http://127.0.0.1:55431/", settings.BattleNetRedirectUri);
            Assert.IsTrue(BattleNetSettings.IsLegacyDefaultRedirectUri("https://localhost"));
            Assert.IsTrue(BattleNetSettings.IsLegacyDefaultRedirectUri("https://localhost/"));
            Assert.IsFalse(BattleNetSettings.IsLegacyDefaultRedirectUri(settings.BattleNetRedirectUri));
        }

        [TestMethod]
        public void Settings_SerializesDataForAzerothWowRarityFlag()
        {
            var settings = new BattleNetSettings
            {
                UseDataForAzerothForWowRarity = false
            };

            var json = JObject.Parse(settings.SerializeToJson());

            Assert.IsFalse(json["UseDataForAzerothForWowRarity"].Value<bool>());
        }

        [TestMethod]
        public void Settings_RoundTripsDataForAzerothWowRarityFlag()
        {
            var source = new BattleNetSettings
            {
                UseDataForAzerothForWowRarity = false
            };
            var target = new BattleNetSettings();

            target.DeserializeFromJson(source.SerializeToJson());

            Assert.IsFalse(target.UseDataForAzerothForWowRarity);
        }

        [TestMethod]
        public void WowRarity_AppliesDataForAzerothPercentByAchievementId()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail
                {
                    ApiName = "157",
                    DisplayName = "To All The Squirrels I've Loved Before",
                    Rarity = RarityTier.Common
                },
                new AchievementDetail
                {
                    ApiName = "6",
                    DisplayName = "Level 10",
                    Rarity = RarityTier.UltraRare
                },
                new AchievementDetail
                {
                    ApiName = "missing",
                    DisplayName = "Missing",
                    Rarity = RarityTier.Common
                },
                new AchievementDetail
                {
                    ApiName = " ",
                    DisplayName = "No id",
                    Rarity = RarityTier.Common
                }
            };

            var updated = WowGameStrategy.ApplyDataForAzerothRarity(
                achievements,
                new Dictionary<string, double>
                {
                    { "157", 4.3049 },
                    { "6", 100 }
                });

            Assert.AreEqual(3, updated);
            Assert.AreEqual(4.3049, achievements[0].GlobalPercentUnlocked.Value);
            Assert.AreEqual(RarityTier.UltraRare, achievements[0].Rarity);
            Assert.AreEqual(100d, achievements[1].GlobalPercentUnlocked.Value);
            Assert.AreEqual(RarityTier.Common, achievements[1].Rarity);
            Assert.AreEqual(0d, achievements[2].GlobalPercentUnlocked.Value);
            Assert.AreEqual(RarityTier.UltraRare, achievements[2].Rarity);
            Assert.IsFalse(achievements[3].GlobalPercentUnlocked.HasValue);
            Assert.AreEqual(RarityTier.Common, achievements[3].Rarity);
        }

        [TestMethod]
        public void WowRarity_EmptyDocumentLeavesAchievementsUntouched()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "157", GlobalPercentUnlocked = 12.5, Rarity = RarityTier.Rare }
            };

            var updated = WowGameStrategy.ApplyDataForAzerothRarity(achievements, new Dictionary<string, double>());

            Assert.AreEqual(0, updated);
            Assert.AreEqual(12.5, achievements[0].GlobalPercentUnlocked.Value);
            Assert.AreEqual(RarityTier.Rare, achievements[0].Rarity);
        }

        [TestMethod]
        public void WowRarity_ScalesDataForAzerothRatioValues()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "157", DisplayName = "Ratio Rare" },
                new AchievementDetail { ApiName = "6", DisplayName = "Ratio Common" },
                new AchievementDetail { ApiName = "0", DisplayName = "Zero" }
            };

            var updated = WowGameStrategy.ApplyDataForAzerothRarity(
                achievements,
                new Dictionary<string, double>
                {
                    { "157", 0.043049 },
                    { "6", 1 },
                    { "0", 0 }
                });

            Assert.AreEqual(3, updated);
            Assert.AreEqual(4.3049, achievements[0].GlobalPercentUnlocked.Value, 0.0001);
            Assert.AreEqual(RarityTier.UltraRare, achievements[0].Rarity);
            Assert.AreEqual(100d, achievements[1].GlobalPercentUnlocked.Value);
            Assert.AreEqual(RarityTier.Common, achievements[1].Rarity);
            Assert.AreEqual(0d, achievements[2].GlobalPercentUnlocked.Value);
            Assert.AreEqual(RarityTier.UltraRare, achievements[2].Rarity);
        }

        [TestMethod]
        public void GameSupport_OnlyAllowsWowAndConfiguredSc2()
        {
            var settings = new BattleNetSettings
            {
                BattleNetClientId = "client",
                BattleNetClientSecret = "secret",
                Sc2RegionId = 2,
                Sc2RealmId = 1,
                Sc2ProfileId = 1234
            };

            Assert.IsTrue(BattleNetGameSupport.IsSupported(BattleNetGame("World of Warcraft"), settings));
            Assert.IsTrue(BattleNetGameSupport.IsSupported(BattleNetGame("StarCraft II"), settings));
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("Overwatch 2"), settings));
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("Diablo IV"), settings));
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("Warcraft III: Reforged"), settings));
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("StarCraft: Remastered"), settings));
            Assert.IsFalse(BattleNetGameSupport.IsSupported(new Game { Id = Guid.NewGuid(), Name = "World of Warcraft" }, settings));

            settings.BattleNetClientSecret = null;
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("StarCraft II"), settings));
        }

        [TestMethod]
        public void GameSupport_RoutesClassicAndForeverAwayFromRetail()
        {
            Assert.AreEqual(BattleNetGameTitle.Wow, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("World of Warcraft")));
            Assert.AreEqual(BattleNetGameTitle.Wow, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("WoW")));
            Assert.AreEqual(BattleNetGameTitle.WowClassic, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("World of Warcraft Classic")));
            Assert.AreEqual(BattleNetGameTitle.WowClassic, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("World of Warcraft: Mists of Pandaria Classic")));
            Assert.AreEqual(BattleNetGameTitle.WowClassic, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("WoW Classic")));
            Assert.AreEqual(BattleNetGameTitle.WowForever, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("World of Warcraft: Forever")));
            Assert.AreEqual(BattleNetGameTitle.Sc2, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("StarCraft II")));
            Assert.AreEqual(BattleNetGameTitle.None, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("Diablo IV")));
            Assert.AreEqual(BattleNetGameTitle.None, BattleNetGameSupport.ResolveTitleByName(BattleNetGame("StarCraft: Remastered")));

            var settings = new BattleNetSettings();
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("World of Warcraft Classic"), settings));
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("World of Warcraft: Forever"), settings));

            settings.BattleNetClientId = "id";
            settings.BattleNetClientSecret = "secret";
            Assert.IsTrue(BattleNetGameSupport.IsSupported(BattleNetGame("World of Warcraft Classic"), settings));
            Assert.IsFalse(BattleNetGameSupport.IsSupported(BattleNetGame("World of Warcraft: Forever"), settings));

            settings.BattleNetAccountId = "account";
            settings.BattleNetAccessToken = "token";
            Assert.IsTrue(BattleNetGameSupport.IsSupported(BattleNetGame("World of Warcraft: Forever"), settings));
        }

        [TestMethod]
        public void WowVersion_PutsTheVersionPrefixIntoEveryNamespace()
        {
            Assert.AreSame(WowVersion.Classic, WowVersion.ForTitle(BattleNetGameTitle.WowClassic));
            Assert.AreSame(WowVersion.Forever, WowVersion.ForTitle(BattleNetGameTitle.WowForever));
            Assert.AreSame(WowVersion.Retail, WowVersion.ForTitle(BattleNetGameTitle.Wow));
            Assert.IsNull(WowVersion.ForTitle(BattleNetGameTitle.Sc2));

            Assert.AreEqual(
                "https://eu.api.blizzard.com/data/wow/achievement/index?namespace=static-classic-eu&locale=en_US",
                BattleNetApiClient.BuildWowOfficialAchievementIndexUrl("eu", "en_US", WowVersion.Classic.NamespacePrefix));
            Assert.AreEqual(
                "https://eu.api.blizzard.com/data/wow/achievement-category/15597?namespace=static-forever-eu&locale=en_US",
                BattleNetApiClient.BuildWowAchievementCategoryUrl("eu", 15597, "en_US", WowVersion.Forever.NamespacePrefix));
            Assert.AreEqual(
                "https://eu.api.blizzard.com/profile/wow/character/shekzeer/eternal/achievements?namespace=profile-classic-eu&locale=en_US",
                BattleNetApiClient.BuildWowOfficialCharacterAchievementsUrl("eu", "shekzeer", "Eternal", "en_US", WowVersion.Classic.NamespacePrefix));
            Assert.AreEqual(
                "https://eu.api.blizzard.com/profile/user/wow?namespace=profile-forever-eu&locale=en_US",
                BattleNetApiClient.BuildWowOfficialAccountProfileUrl("eu", "en_US", WowVersion.Forever.NamespacePrefix));
            Assert.AreEqual(
                "https://us.api.blizzard.com/data/wow/achievement/index?namespace=static-us&locale=en_US",
                BattleNetApiClient.BuildWowOfficialAchievementIndexUrl("us", "en_US"));
            Assert.AreEqual(
                "https://eu.api.blizzard.com/data/wow/media/achievement/62041?namespace=static-forever-eu",
                WowGameStrategy.BuildOfficialAchievementMediaHref(null, "eu", 62041, WowVersion.Forever.NamespacePrefix));
        }

        [TestMethod]
        public void WowApiClient_BuildsAchievementsUrlFromACharacterProfileLink()
        {
            Assert.AreEqual(
                "https://eu.api.blizzard.com/profile/wow/character/some-realm/name/achievements?namespace=profile-forever-eu&locale=en_US",
                BattleNetApiClient.BuildCharacterAchievementsUrlFromProfileHref(
                    "https://eu.api.blizzard.com/profile/wow/character/some-realm/name?namespace=profile-forever-eu",
                    "en_US"));
            Assert.IsNull(BattleNetApiClient.BuildCharacterAchievementsUrlFromProfileHref(
                "https://eu.api.blizzard.com/profile/user/wow/protected-character/1-2?namespace=profile-eu",
                "en_US"));
            Assert.IsNull(BattleNetApiClient.BuildCharacterAchievementsUrlFromProfileHref(null, "en_US"));
        }

        [TestMethod]
        public void WowAccountCharacters_ReadNameAndRealmFromTheEntryWithTheLinkAsFallback()
        {
            var profile = JsonConvert.DeserializeObject<WowAccountProfileResponse>(@"{
  ""wow_accounts"": [{
    ""id"": 1,
    ""characters"": [
      {
        ""character"": { ""href"": ""https://eu.api.blizzard.com/profile/wow/character/tarren-mill/cowkiri?namespace=profile-eu"" },
        ""protected_character"": { ""href"": ""https://eu.api.blizzard.com/profile/user/wow/protected-character/1-2?namespace=profile-eu"" },
        ""name"": ""Cowkiri"",
        ""id"": 2,
        ""realm"": { ""name"": ""Tarren Mill"", ""id"": 1084, ""slug"": ""tarren-mill"" },
        ""level"": 90
      },
      {
        ""character"": { ""href"": ""https://eu.api.blizzard.com/profile/wow/character/far-realm/linkonly?namespace=profile-forever-eu"" },
        ""id"": 3
      },
      { ""id"": 4 }
    ]
  }]
}");

            var characters = WowGameStrategy.ExtractAccountCharacters("eu", profile);

            Assert.AreEqual(2, characters.Count);
            Assert.IsTrue(characters[0].HasRealmAndName);
            Assert.AreEqual("tarren-mill", characters[0].RealmSlug);
            Assert.AreEqual("Cowkiri", characters[0].CharacterName);
            Assert.IsFalse(characters[1].HasRealmAndName);
            StringAssert.Contains(characters[1].ProfileHref, "/linkonly?namespace=profile-forever-eu");
        }

        [TestMethod]
        public void WowAccountCharacters_SkipCharactersBelowTheProfileLevel()
        {
            var profile = JsonConvert.DeserializeObject<WowAccountProfileResponse>(@"{
  ""wow_accounts"": [{ ""characters"": [
    { ""name"": ""Alt"", ""realm"": { ""slug"": ""ravencrest"" }, ""level"": 9 },
    { ""name"": ""Main"", ""realm"": { ""slug"": ""ravencrest"" }, ""level"": 10 }
  ] }]
}");

            var characters = WowGameStrategy.ExtractAccountCharacters("eu", profile);

            Assert.AreEqual(1, characters.Count);
            Assert.AreEqual("Main", characters[0].CharacterName);
            Assert.AreEqual(10, characters[0].Level);
        }

        [TestMethod]
        public void WowCharacterSource_ChoosesAccountOrTypedCharacterWithForeverAlwaysAccount()
        {
            var settings = new BattleNetSettings();
            Assert.AreEqual(WowCharacterSource.Account, settings.WowCharacterSource);
            Assert.IsTrue(WowGameStrategy.UsesAccount(settings, WowVersion.Retail));
            Assert.IsTrue(WowGameStrategy.UsesAccount(settings, WowVersion.Classic));

            settings.WowCharacterSource = WowCharacterSource.Character;
            Assert.IsFalse(WowGameStrategy.UsesAccount(settings, WowVersion.Retail));
            Assert.IsFalse(WowGameStrategy.UsesAccount(settings, WowVersion.Classic));
            Assert.IsTrue(WowGameStrategy.UsesAccount(settings, WowVersion.Forever));
        }

        [TestMethod]
        public void WowRealmIndexUrl_UsesTheVersionDynamicNamespace()
        {
            Assert.AreEqual(
                "https://eu.api.blizzard.com/data/wow/realm/index?namespace=dynamic-classic-eu&locale=en_US",
                BattleNetApiClient.BuildWowRealmIndexUrl("eu", null, WowVersion.Classic.NamespacePrefix));
        }

        [TestMethod]
        public void WowRealmSlug_AcceptsTypedRealmNames()
        {
            Assert.AreEqual("tarren-mill", WowGameStrategy.NormalizeRealmSlug("  Tarren Mill "));
            Assert.AreEqual("shekzeer", WowGameStrategy.NormalizeRealmSlug("Shek'zeer"));
            Assert.AreEqual("area-52", WowGameStrategy.NormalizeRealmSlug("area-52"));
            Assert.IsNull(WowGameStrategy.NormalizeRealmSlug(" "));
        }

        [TestMethod]
        public void WowCatalogFromCategories_NestsPathsAndSkipsGuildBranches()
        {
            var resources = JsonConvert.DeserializeObject<List<WowAchievementCategoryResource>>(@"[
  { ""id"": 15568, ""name"": ""Classes"", ""achievements"": [] },
  { ""id"": 15577, ""name"": ""Druid"", ""parent_category"": { ""id"": 15568, ""name"": ""Classes"" },
    ""achievements"": [ { ""id"": 62100, ""name"": ""Shapeshifter"" }, { ""id"": 62101, ""name"": ""Bear Form"", ""points"": 10 } ] },
  { ""id"": 15597, ""name"": ""Ranks"", ""parent_category"": { ""id"": 15590, ""name"": ""Player vs. Player"" },
    ""achievements"": [ { ""id"": 62041, ""name"": ""Rank 3"" }, { ""id"": 62100, ""name"": ""Duplicate"" } ] },
  { ""id"": 15088, ""name"": ""Guild"", ""is_guild_category"": true,
    ""achievements"": [ { ""id"": 5000, ""name"": ""Guild Level"" } ] },
  { ""id"": 15089, ""name"": ""Guild Dungeons"", ""parent_category"": { ""id"": 15088, ""name"": ""Guild"" },
    ""achievements"": [ { ""id"": 5001, ""name"": ""Guild Run"" } ] }
]");
            var guildIds = new HashSet<int>();

            var achievements = WowGameStrategy.BuildCatalogFromCategories(resources, guildIds);

            CollectionAssert.AreEquivalent(new[] { "62041", "62100", "62101" }, achievements.Select(a => a.ApiName).ToList());
            Assert.AreEqual(
                CategoryPathHelper.JoinRaw("Classes", "Druid"),
                achievements.Single(a => a.ApiName == "62100").Category);
            Assert.AreEqual(
                CategoryPathHelper.JoinRaw("Player vs. Player", "Ranks"),
                achievements.Single(a => a.ApiName == "62041").Category);
            Assert.AreEqual(10, achievements.Single(a => a.ApiName == "62101").Points);
            Assert.IsNull(achievements.Single(a => a.ApiName == "62100").Points);
            CollectionAssert.IsSubsetOf(new[] { 15088, 15089 }, guildIds.ToList());
            Assert.IsTrue(achievements.All(a => !a.Unlocked));
        }

        [TestMethod]
        public void Sc2BuildAchievements_MapsStaticCatalogOntoProfile()
        {
            var definitions = JsonConvert.DeserializeObject<Sc2AchievementDefinitionsResponse>(@"{
  ""achievements"": [
    { ""id"": ""91475320766632"", ""title"": ""Victory"", ""description"": ""Win a game."", ""imageUrl"": ""https://example.test/a.jpg"", ""categoryId"": ""4325379"", ""points"": 10 },
    { ""id"": ""91475320766633"", ""title"": ""Still Going"", ""description"": ""In progress."", ""categoryId"": ""4325379"", ""points"": 0 },
    { ""id"": ""91475320766632"", ""title"": ""Duplicate"" },
    { ""title"": ""No id"" }
  ],
  ""categories"": [
    { ""id"": ""4325379"", ""name"": ""Wings of Liberty"", ""parentCategoryId"": ""4325377"" },
    { ""id"": ""4325377"", ""name"": ""Campaign"", ""parentCategoryId"": null },
    { ""id"": ""4325377"", ""name"": ""Campaign duplicate"" }
  ]
}");
            var profile = JsonConvert.DeserializeObject<Sc2ProfileResponse>(@"{
  ""summary"": { ""id"": ""987654"", ""displayName"": ""Tester"", ""totalAchievementPoints"": 10 },
  ""earnedAchievements"": [
    { ""achievementId"": ""91475320766632"", ""completionDate"": 1.710000000E9, ""isComplete"": true, ""inProgress"": false },
    { ""achievementId"": ""91475320766633"", ""completionDate"": 0, ""isComplete"": false, ""inProgress"": true }
  ]
}");

            var achievements = Sc2GameStrategy.BuildAchievements(definitions, profile);

            Assert.AreEqual(2, achievements.Count);
            var won = achievements[0];
            Assert.AreEqual("91475320766632", won.ApiName);
            Assert.AreEqual("Victory", won.DisplayName);
            Assert.AreEqual("https://example.test/a.jpg", won.UnlockedIconPath);
            Assert.AreEqual(10, won.Points);
            Assert.AreEqual(CategoryPathHelper.JoinRaw("Campaign", "Wings of Liberty"), won.Category);
            Assert.IsTrue(won.Unlocked);
            Assert.AreEqual(new DateTime(2024, 3, 9, 16, 0, 0, DateTimeKind.Utc), won.UnlockTimeUtc);

            var pending = achievements[1];
            Assert.IsFalse(pending.Unlocked);
            Assert.IsNull(pending.UnlockTimeUtc);
            Assert.IsNull(pending.Points);
        }

        [TestMethod]
        public void Sc2ChooseProfile_PrefersTheSignInRegion()
        {
            var profiles = new List<Sc2PlayerProfile>
            {
                new Sc2PlayerProfile { RegionId = 1, RealmId = 1, ProfileId = 0 },
                new Sc2PlayerProfile { RegionId = 1, RealmId = 1, ProfileId = 11 },
                new Sc2PlayerProfile { RegionId = 2, RealmId = 1, ProfileId = 22 }
            };

            Assert.AreEqual(22, Sc2GameStrategy.ChooseProfile(profiles, "eu").ProfileId);
            Assert.AreEqual(11, Sc2GameStrategy.ChooseProfile(profiles, "kr").ProfileId);
            Assert.IsNull(Sc2GameStrategy.ChooseProfile(null, "us"));
        }

        [TestMethod]
        public async Task Sc2ApiClient_UsesClientCredentialsTokenAndOfficialEndpoints()
        {
            var handler = new RecordingHandler();
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var definitions = await client.GetSc2AchievementDefinitionsAsync(
                    2,
                    "client-id",
                    "client-secret",
                    "de_DE",
                    CancellationToken.None);
                var profile = await client.GetSc2ProfileAsync(
                    2,
                    1,
                    987654,
                    "client-id",
                    "client-secret",
                    "de_DE",
                    CancellationToken.None);

                Assert.AreEqual(1, definitions.Achievements.Count);
                Assert.AreEqual("987654", profile.Summary.Id);
            }

            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
            Assert.AreEqual("https://eu.battle.net/oauth/token", handler.Requests[0].RequestUri.ToString());
            Assert.AreEqual("Basic", handler.Requests[0].AuthorizationScheme);
            Assert.AreEqual("Y2xpZW50LWlkOmNsaWVudC1zZWNyZXQ=", handler.Requests[0].AuthorizationParameter);
            Assert.AreEqual("grant_type=client_credentials", handler.Requests[0].Body);

            Assert.AreEqual("https://eu.api.blizzard.com/sc2/static/profile/2?locale=de_DE", handler.Requests[1].RequestUri.ToString());
            Assert.AreEqual("Bearer", handler.Requests[1].AuthorizationScheme);
            Assert.AreEqual("token-value", handler.Requests[1].AuthorizationParameter);

            Assert.AreEqual("https://eu.api.blizzard.com/sc2/profile/2/1/987654?locale=de_DE", handler.Requests[2].RequestUri.ToString());
            Assert.AreEqual("Bearer", handler.Requests[2].AuthorizationScheme);
            Assert.AreEqual("token-value", handler.Requests[2].AuthorizationParameter);
        }

        [TestMethod]
        public async Task WowApiClient_LoadsOfficialAchievementCatalogFromIndex()
        {
            var handler = new RecordingHandler();
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var catalog = await client.GetWowOfficialAchievementCatalogAsync(
                    "us",
                    "en_US",
                    "wow-token",
                    CancellationToken.None);

                Assert.AreEqual(2, catalog.Count);
                Assert.AreEqual(1, catalog[0].Id);
                Assert.AreEqual("Account Wide Existing", catalog[0].Name);
                Assert.AreEqual(2, catalog[1].Id);
                Assert.AreEqual(false, catalog[1].IsObtainable);
            }

            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("https://us.api.blizzard.com/data/wow/achievement/index?namespace=static-us&locale=en_US", handler.Requests[0].RequestUri.ToString());
            Assert.AreEqual("Bearer", handler.Requests[0].AuthorizationScheme);
            Assert.AreEqual("wow-token", handler.Requests[0].AuthorizationParameter);
        }

        [TestMethod]
        public void WowCatalogCache_RejectsCacheWhenOfficialIndexSignatureChanges()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new BattleNetWowCatalogCache(tempDirectory, new NullLogger());
                var originalCatalog = new[]
                {
                    new WowOfficialAchievementDefinition { Id = 1, Name = "Original" }
                };
                var changedCatalog = new[]
                {
                    new WowOfficialAchievementDefinition { Id = 1, Name = "Changed" }
                };
                var originalSignature = BattleNetWowCatalogCache.BuildOfficialIndexSignature(originalCatalog);
                var changedSignature = BattleNetWowCatalogCache.BuildOfficialIndexSignature(changedCatalog);

                cache.Save(
                    "us",
                    "en_US",
                    "en-us",
                    originalSignature,
                    1,
                    new[]
                    {
                        new AchievementDetail
                        {
                            ApiName = "1",
                            DisplayName = "Original",
                            Unlocked = true,
                            UnlockTimeUtc = DateTime.UtcNow
                        }
                    });

                Assert.IsTrue(cache.TryLoad("us", "en_US", "en-us", originalSignature, 1, out var loaded, out var hitReason));
                Assert.AreEqual("hit", hitReason);
                Assert.AreEqual(1, loaded.Count);
                Assert.IsFalse(loaded[0].Unlocked);
                Assert.IsFalse(loaded[0].UnlockTimeUtc.HasValue);

                Assert.IsFalse(cache.TryLoad("us", "en_US", "en-us", changedSignature, 1, out _, out var missReason));
                Assert.AreEqual("official-index-changed", missReason);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        [TestMethod]
        public async Task WowApiClient_UsesOfficialCharacterAchievementsEndpoint()
        {
            var handler = new RecordingHandler();
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var response = await client.GetWowOfficialCharacterAchievementsAsync(
                    "us",
                    "Dalaran",
                    "Noshotz",
                    "en_US",
                    "wow-token",
                    CancellationToken.None);

                Assert.AreEqual(3, response.Achievements.Count);
                Assert.AreEqual(1, response.Achievements[0].AchievementId);
                Assert.AreEqual(1710000000000L, response.Achievements[0].CompletedTimestamp);
            }

            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("https://us.api.blizzard.com/profile/wow/character/dalaran/noshotz/achievements?namespace=profile-us&locale=en_US", handler.Requests[0].RequestUri.ToString());
            Assert.AreEqual("Bearer", handler.Requests[0].AuthorizationScheme);
            Assert.AreEqual("wow-token", handler.Requests[0].AuthorizationParameter);
        }

        [TestMethod]
        public async Task WowFetch_UsesOfficialCatalogAndMapsUnobtainableToMissable()
        {
            var settingsRoot = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settingsRoot, new[] { "BattleNet" }, new NullLogger());
            var battleNetSettings = registry.GetSettings<BattleNetSettings>();
            battleNetSettings.WowRegion = "us";
            battleNetSettings.WowRealmSlug = "dalaran";
            battleNetSettings.WowCharacter = "Noshotz";
            battleNetSettings.BattleNetClientId = "client-id";
            battleNetSettings.BattleNetClientSecret = "client-secret";
            registry.Save(battleNetSettings);

            var handler = new RecordingHandler();
            GameAchievementData data;
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var strategy = new WowGameStrategy(client, null, new NullLogger());
                data = await strategy.FetchAchievementsAsync(
                    BattleNetGame("World of Warcraft"),
                    "en-US",
                    CancellationToken.None);
            }

            Assert.IsNotNull(data);
            Assert.IsTrue(data.HasAchievements);

            var completed = data.Achievements.Single(item => item.ApiName == "1");
            Assert.IsTrue(completed.Unlocked);
            Assert.AreEqual("Account Wide Existing", completed.DisplayName);
            Assert.AreEqual("https://render.worldofwarcraft.test/catalog-icon.jpg", completed.UnlockedIconPath);

            var unobtainable = data.Achievements.Single(item => item.ApiName == "2");
            Assert.IsFalse(unobtainable.Unlocked);
            Assert.AreEqual("Unobtainable", unobtainable.Tags);
            Assert.IsTrue(unobtainable.Hidden);
            Assert.AreEqual("Legacy", unobtainable.Category);
        }

        [TestMethod]
        public async Task WowFetch_BackfillsIconForLockedIndexOnlyAchievement()
        {
            var settingsRoot = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settingsRoot, new[] { "BattleNet" }, new NullLogger());
            var battleNetSettings = registry.GetSettings<BattleNetSettings>();
            battleNetSettings.WowRegion = "us";
            battleNetSettings.WowRealmSlug = "dalaran";
            battleNetSettings.WowCharacter = "Noshotz";
            battleNetSettings.BattleNetClientId = "client-id";
            battleNetSettings.BattleNetClientSecret = "client-secret";
            registry.Save(battleNetSettings);

            var handler = new RecordingHandler();
            GameAchievementData data;
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var strategy = new WowGameStrategy(client, null, new NullLogger());
                data = await strategy.FetchAchievementsAsync(
                    BattleNetGame("World of Warcraft"),
                    "en-US",
                    CancellationToken.None);
            }

            Assert.IsNotNull(data);

            // Achievement 2 reaches the list only via the official index (it is absent from
            // the public character page) and is not earned, so it is added as a locked
            // achievement with no icon. The enrichment pass must backfill its icon from the
            // media endpoint rather than leaving it on the placeholder.
            var lockedIndexOnly = data.Achievements.Single(item => item.ApiName == "2");
            Assert.IsFalse(lockedIndexOnly.Unlocked);
            Assert.AreEqual("https://render.worldofwarcraft.test/unobtainable-icon.jpg", lockedIndexOnly.UnlockedIconPath);
            Assert.IsTrue(handler.Requests.Any(request =>
                request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/media/achievement/2?namespace=static-us"));
        }

        [TestMethod]
        public async Task WowFetch_SkipsOfficialGuildAchievementsByCategoryId()
        {
            var settingsRoot = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settingsRoot, new[] { "BattleNet" }, new NullLogger());
            var battleNetSettings = registry.GetSettings<BattleNetSettings>();
            battleNetSettings.WowRegion = "us";
            battleNetSettings.WowRealmSlug = "dalaran";
            battleNetSettings.WowCharacter = "Noshotz";
            battleNetSettings.BattleNetClientId = "client-id";
            battleNetSettings.BattleNetClientSecret = "client-secret";
            registry.Save(battleNetSettings);

            var handler = new RecordingHandler { IncludeOfficialGuildAchievement = true };
            GameAchievementData data;
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var strategy = new WowGameStrategy(client, null, new NullLogger());
                data = await strategy.FetchAchievementsAsync(
                    BattleNetGame("World of Warcraft"),
                    "en-US",
                    CancellationToken.None);
            }

            Assert.IsNotNull(data);
            Assert.IsFalse(data.Achievements.Any(item => item.ApiName == "5"));
        }

        [TestMethod]
        public async Task WowFetch_ReusesPersistedCatalogCacheWhenOfficialIndexUnchanged()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var settingsRoot = new PlayniteAchievementsSettings();
                var registry = new ProviderRegistry(settingsRoot, new[] { "BattleNet" }, new NullLogger());
                var battleNetSettings = registry.GetSettings<BattleNetSettings>();
                battleNetSettings.WowRegion = "us";
                battleNetSettings.WowRealmSlug = "dalaran";
                battleNetSettings.WowCharacter = "Noshotz";
                battleNetSettings.BattleNetClientId = "client-id";
                battleNetSettings.BattleNetClientSecret = "client-secret";
                registry.Save(battleNetSettings);

                var firstHandler = new RecordingHandler();
                using (var httpClient = new HttpClient(firstHandler))
                using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
                {
                    var strategy = new WowGameStrategy(client, null, new NullLogger(), tempDirectory);
                    var data = await strategy.FetchAchievementsAsync(
                        BattleNetGame("World of Warcraft"),
                        "en-US",
                        CancellationToken.None);

                    Assert.IsNotNull(data);
                    Assert.IsTrue(data.HasAchievements);
                }

                Assert.IsTrue(firstHandler.Requests.Any(IsPublicWowCategoryRequest));

                var secondHandler = new RecordingHandler();
                using (var httpClient = new HttpClient(secondHandler))
                using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
                {
                    var strategy = new WowGameStrategy(client, null, new NullLogger(), tempDirectory);
                    var data = await strategy.FetchAchievementsAsync(
                        BattleNetGame("World of Warcraft"),
                        "en-US",
                        CancellationToken.None);

                    Assert.IsNotNull(data);
                    Assert.IsTrue(data.Achievements.Any(item => item.ApiName == "1" && item.Unlocked));
                    Assert.IsTrue(data.Achievements.Any(item => item.ApiName == "2" && item.Tags == "Unobtainable"));
                }

                Assert.IsFalse(secondHandler.Requests.Any(IsPublicWowCategoryRequest));
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        [TestMethod]
        public async Task WowMerge_UsesCompletedTimestampAndAddsEarnedHiddenAchievements()
        {
            var settingsRoot = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settingsRoot, new[] { "BattleNet" }, new NullLogger());
            var battleNetSettings = registry.GetSettings<BattleNetSettings>();
            battleNetSettings.WowRegion = "us";
            battleNetSettings.WowRealmSlug = "dalaran";
            battleNetSettings.WowCharacter = "Noshotz";
            battleNetSettings.BattleNetClientId = "client-id";
            battleNetSettings.BattleNetClientSecret = "client-secret";
            registry.Save(battleNetSettings);

            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail
                {
                    ApiName = "1",
                    DisplayName = "Account Wide Existing",
                    ProviderKey = "BattleNet",
                    Unlocked = false
                }
            };

            var handler = new RecordingHandler();
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var strategy = new WowGameStrategy(client, null, new NullLogger());
                var stats = await strategy.MergeOfficialAchievementDataAsync(
                    achievements,
                    battleNetSettings,
                    "en-US",
                    new HashSet<int>(),
                    CancellationToken.None);

                Assert.AreEqual(1, stats.FetchedCharacters);
                Assert.AreEqual(3, stats.CompletionCount);
                Assert.AreEqual(3, stats.UpdatedCount);
                Assert.AreEqual(2, stats.AddedCount);
            }

            var existing = achievements.Single(item => item.ApiName == "1");
            Assert.IsTrue(existing.Unlocked);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1710000000000L).UtcDateTime, existing.UnlockTimeUtc);

            var hiddenEarned = achievements.Single(item => item.ApiName == "3");
            Assert.IsTrue(hiddenEarned.Unlocked);
            Assert.AreEqual("Hidden Earned", hiddenEarned.DisplayName);
            Assert.AreEqual("Earned but not in the public web list.", hiddenEarned.Description);
            Assert.AreEqual("https://render.worldofwarcraft.test/icon.jpg", hiddenEarned.UnlockedIconPath);
            Assert.AreEqual(10, hiddenEarned.Points);
            Assert.AreEqual("Feats of Strength", hiddenEarned.Category);

            var fallbackIcon = achievements.Single(item => item.ApiName == "4");
            Assert.IsTrue(fallbackIcon.Unlocked);
            Assert.AreEqual("Fallback Icon", fallbackIcon.DisplayName);
            Assert.AreEqual("Media URL is derived.", fallbackIcon.Description);
            Assert.AreEqual("https://render.worldofwarcraft.test/fallback-icon.jpg", fallbackIcon.UnlockedIconPath);

            // An official achievement with no category is left null (no "Earned"/"Default"
            // sentinel); the hydrator localizes the default category at display time.
            Assert.IsNull(fallbackIcon.Category);
        }

        [TestMethod]
        public async Task DataForAzerothApiClient_LoadsAchievementRarityFromDynamicIndex()
        {
            var handler = new RecordingHandler();
            using (var httpClient = new HttpClient(handler))
            using (var client = new BattleNetApiClient(new NullLogger(), httpClient))
            {
                var rarity = await client.GetDataForAzerothWowAchievementRarityAsync(CancellationToken.None);

                Assert.AreEqual(2, rarity.Count);
                Assert.AreEqual(100d, rarity["6"]);
                Assert.AreEqual(4.3049, rarity["157"]);
            }

            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual("https://www.dataforazeroth.com/retail/dynamic/index.json", handler.Requests[0].RequestUri.ToString());
            Assert.AreEqual("https://www.dataforazeroth.com/retail/dynamic/achievementsrarity.hash.json", handler.Requests[1].RequestUri.ToString());
        }

        private static Game BattleNetGame(string name)
        {
            return new Game
            {
                Id = Guid.NewGuid(),
                Name = name,
                PluginId = BattleNetGameSupport.BattleNetPluginId
            };
        }

        private static bool IsPublicWowCategoryRequest(RecordedRequest request)
        {
            return string.Equals(request?.RequestUri?.Host, "worldofwarcraft.blizzard.com", StringComparison.OrdinalIgnoreCase) &&
                request.RequestUri.AbsolutePath.IndexOf("/achievements/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();
            public bool IncludeOfficialGuildAchievement { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var recorded = new RecordedRequest
                {
                    Method = request.Method,
                    RequestUri = request.RequestUri,
                    AuthorizationScheme = request.Headers.Authorization?.Scheme,
                    AuthorizationParameter = request.Headers.Authorization?.Parameter,
                    Body = request.Content == null ? null : await request.Content.ReadAsStringAsync()
                };
                Requests.Add(recorded);

                if (request.Method == HttpMethod.Post &&
                    request.RequestUri.ToString() == "https://eu.battle.net/oauth/token" &&
                    recorded.Body == "grant_type=client_credentials")
                {
                    return JsonResponse(@"{""access_token"":""token-value"",""token_type"":""bearer"",""expires_in"":3600}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://eu.api.blizzard.com/sc2/static/profile/2?locale=de_DE")
                {
                    return JsonResponse(@"{""achievements"":[{""id"":""ach-1"",""title"":""Win"",""description"":""Win a match"",""points"":10,""categoryId"":""cat-1""}],""categories"":[{""id"":""cat-1"",""name"":""General""}]}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://eu.api.blizzard.com/sc2/profile/2/1/987654?locale=de_DE")
                {
                    return JsonResponse(@"{""summary"":{""id"":""987654"",""displayName"":""Tester"",""totalAchievementPoints"":10},""earnedAchievements"":[{""achievementId"":""ach-1"",""completionDate"":1.71E9,""isComplete"":true}]}");
                }

                if (request.Method == HttpMethod.Post &&
                    request.RequestUri.ToString() == "https://us.battle.net/oauth/token" &&
                    recorded.Body == "grant_type=client_credentials")
                {
                    return JsonResponse(@"{""access_token"":""wow-token"",""token_type"":""bearer"",""expires_in"":3600}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://worldofwarcraft.blizzard.com/en-us/character/us/dalaran/Noshotz/achievements/characters/model.json")
                {
                    return JsonResponse(@"
{
  ""name"": ""General"",
  ""achievementsList"": [
    {
      ""id"": 1,
      ""name"": ""Account Wide Existing"",
      ""description"": ""Already earned from the public page."",
      ""icon"": { ""url"": ""https://render.worldofwarcraft.test/catalog-icon.jpg"" },
      ""point"": 10
    }
  ]
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/achievement/index?namespace=static-us&locale=en_US")
                {
                    // The category name is non-English and the achievement name has no
                    // "guild" text, proving exclusion is driven by the category id, not strings.
                    var guildAchievement = IncludeOfficialGuildAchievement
                        ? @",
    {
      ""id"": 5,
      ""name"": ""Cooperative Raider"",
      ""description"": ""Complete the dungeon with a group."",
      ""points"": 10,
      ""category"": { ""id"": 150, ""name"": ""Gilde"" },
      ""key"": { ""href"": ""https://us.api.blizzard.com/data/wow/achievement/5?namespace=static-us"" }
    }"
                        : string.Empty;
                    return JsonResponse(@"
{
  ""achievements"": [
    { ""id"": 1, ""name"": ""Account Wide Existing"", ""key"": { ""href"": ""https://us.api.blizzard.com/data/wow/achievement/1?namespace=static-us"" } },
    {
      ""id"": 2,
      ""name"": ""Unobtainable Legacy"",
      ""description"": ""No longer available."",
      ""points"": 5,
      ""is_hidden"": true,
      ""is_obtainable"": false,
      ""category"": { ""id"": 81, ""name"": ""Legacy"" },
      ""key"": { ""href"": ""https://us.api.blizzard.com/data/wow/achievement/2?namespace=static-us"" }
    }" + guildAchievement + @"
  ]
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/achievement-category/index?namespace=static-us&locale=en_US")
                {
                    return JsonResponse(@"
{
  ""guild_categories"": [
    { ""id"": 150, ""name"": ""Gilde"", ""key"": { ""href"": ""https://us.api.blizzard.com/data/wow/achievement-category/150?namespace=static-us"" } }
  ]
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/achievement-category/150?namespace=static-us&locale=en_US")
                {
                    return JsonResponse(@"{ ""id"": 150, ""subcategories"": [] }");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/achievement/1?namespace=static-us&locale=en_US")
                {
                    return JsonResponse(@"
{
  ""id"": 1,
  ""name"": ""Account Wide Existing"",
  ""description"": ""Already earned from the official profile API."",
  ""points"": 10,
  ""category"": { ""id"": 92, ""name"": ""General"" },
  ""media"": { ""href"": ""https://us.api.blizzard.com/data/wow/media/achievement/1?namespace=static-us"" }
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/media/achievement/1?namespace=static-us")
                {
                    return JsonResponse(@"{""assets"":[{""key"":""icon"",""value"":""https://render.worldofwarcraft.test/catalog-icon.jpg""}]}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/achievement/2?namespace=static-us&locale=en_US")
                {
                    return JsonResponse(@"
{
  ""id"": 2,
  ""name"": ""Unobtainable Legacy"",
  ""description"": ""No longer available."",
  ""points"": 5,
  ""is_hidden"": true,
  ""is_obtainable"": false,
  ""category"": { ""id"": 81, ""name"": ""Legacy"" },
  ""media"": { ""href"": ""https://us.api.blizzard.com/data/wow/media/achievement/2?namespace=static-us"" }
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/media/achievement/2?namespace=static-us")
                {
                    return JsonResponse(@"{""assets"":[{""key"":""icon"",""value"":""https://render.worldofwarcraft.test/unobtainable-icon.jpg""}]}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/profile/wow/character/dalaran/noshotz/achievements?namespace=profile-us&locale=en_US")
                {
                    return JsonResponse(@"
{
  ""achievements"": [
    {
      ""id"": 1,
      ""achievement"": { ""id"": 1, ""name"": ""Account Wide Existing"", ""key"": { ""href"": ""https://us.api.blizzard.com/data/wow/achievement/1?namespace=static-us"" } },
      ""criteria"": { ""id"": 11, ""is_completed"": false },
      ""completed_timestamp"": 1710000000000
    },
    {
      ""id"": 3,
      ""achievement"": { ""id"": 3, ""name"": ""Hidden Earned"", ""key"": { ""href"": ""https://us.api.blizzard.com/data/wow/achievement/3?namespace=static-us"" } },
      ""completed_timestamp"": 1710000100000
    },
    {
      ""id"": 4,
      ""achievement"": { ""id"": 4, ""name"": ""Fallback Icon"", ""key"": { ""href"": ""https://us.api.blizzard.com/data/wow/achievement/4?namespace=static-us"" } },
      ""completed_timestamp"": 1710000200000
    }
  ]
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/achievement/3?namespace=static-us&locale=en_US")
                {
                    return JsonResponse(@"
{
  ""id"": 3,
  ""name"": ""Hidden Earned"",
  ""description"": ""Earned but not in the public web list."",
  ""points"": 10,
  ""category"": { ""id"": 81, ""name"": ""Feats of Strength"" },
  ""media"": { ""href"": ""https://us.api.blizzard.com/data/wow/media/achievement/3?namespace=static-us"" }
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/media/achievement/3?namespace=static-us")
                {
                    return JsonResponse(@"{""assets"":[{""key"":""icon"",""value"":""https://render.worldofwarcraft.test/icon.jpg""}]}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/achievement/4?namespace=static-us&locale=en_US")
                {
                    return JsonResponse(@"
{
  ""id"": 4,
  ""name"": ""Fallback Icon"",
  ""description"": ""Media URL is derived."",
  ""points"": 5
}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://us.api.blizzard.com/data/wow/media/achievement/4?namespace=static-us")
                {
                    return JsonResponse(@"{""assets"":[{""key"":""default"",""value"":""https://render.worldofwarcraft.test/fallback-icon.jpg""}]}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://www.dataforazeroth.com/retail/dynamic/index.json")
                {
                    return JsonResponse(@"{""achievementsrarity"":""/retail/dynamic/achievementsrarity.hash.json""}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://www.dataforazeroth.com/retail/dynamic/achievementsrarity.hash.json")
                {
                    return JsonResponse(@"{""achievements"":{""6"":100,""157"":4.3049}}");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage JsonResponse(string json)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }
        }

        private sealed class RecordedRequest
        {
            public HttpMethod Method { get; set; }
            public Uri RequestUri { get; set; }
            public string AuthorizationScheme { get; set; }
            public string AuthorizationParameter { get; set; }
            public string Body { get; set; }
        }

        private sealed class NullLogger : ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}
