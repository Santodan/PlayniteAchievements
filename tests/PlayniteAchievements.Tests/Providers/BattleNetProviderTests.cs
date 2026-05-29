using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.BattleNet;
using PlayniteAchievements.Providers.BattleNet.Models;

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
            Assert.AreEqual("Characters", unlocked.Category);
            Assert.IsTrue(unlocked.Unlocked);
            Assert.AreEqual(DateTimeKind.Utc, unlocked.UnlockTimeUtc.Value.Kind);

            var locked = achievements.Single(item => item.ApiName == "2");
            Assert.IsFalse(locked.Unlocked);
            Assert.IsFalse(locked.UnlockTimeUtc.HasValue);
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

            Assert.AreEqual("https://eu.api.blizzard.com/sc2/legacy/data/achievements/2?locale=de_DE", handler.Requests[1].RequestUri.ToString());
            Assert.AreEqual("Bearer", handler.Requests[1].AuthorizationScheme);
            Assert.AreEqual("token-value", handler.Requests[1].AuthorizationParameter);

            Assert.AreEqual("https://eu.api.blizzard.com/sc2/legacy/profile/2/1/987654?locale=de_DE", handler.Requests[2].RequestUri.ToString());
            Assert.AreEqual("Bearer", handler.Requests[2].AuthorizationScheme);
            Assert.AreEqual("token-value", handler.Requests[2].AuthorizationParameter);
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

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

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
                    request.RequestUri.ToString() == "https://eu.api.blizzard.com/sc2/legacy/data/achievements/2?locale=de_DE")
                {
                    return JsonResponse(@"{""achievements"":[{""id"":""ach-1"",""title"":""Win"",""description"":""Win a match"",""points"":10,""categoryId"":""cat-1""}],""categories"":[{""id"":""cat-1"",""name"":""General""}]}");
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri.ToString() == "https://eu.api.blizzard.com/sc2/legacy/profile/2/1/987654?locale=de_DE")
                {
                    return JsonResponse(@"{""summary"":{""id"":""987654"",""displayName"":""Tester"",""totalAchievementPoints"":10},""earnedAchievements"":[{""achievementId"":""ach-1"",""completionDate"":""1710000000"",""isComplete"":true}]}");
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
