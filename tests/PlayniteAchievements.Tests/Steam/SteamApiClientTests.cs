using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamApiClientTests
    {
        [TestMethod]
        public async Task GetGameHasAchievementsAsync_UsesGetGameAchievementsEndpoint()
        {
            Uri capturedUri = null;
            using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            {
                capturedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{ \"response\": { \"achievements\": [] } }",
                        Encoding.UTF8,
                        "application/json")
                };
            }));

            var client = new SteamApiClient(httpClient, logger: null);
            var result = await client.GetGameHasAchievementsAsync("store-token", 123, "english", CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Value);
            Assert.IsNotNull(capturedUri);
            Assert.AreEqual("/IPlayerService/GetGameAchievements/v1/", capturedUri.AbsolutePath);
            StringAssert.Contains(capturedUri.Query, "key=store-token");
        }

        [TestMethod]
        public async Task GetGameHasAchievementsAsync_ReturnsTrue_WhenAchievementsExist()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{ \"response\": { \"achievements\": [ { \"internal_name\": \"ach_1\" } ] } }",
                        Encoding.UTF8,
                        "application/json")
                }));

            var client = new SteamApiClient(httpClient, logger: null);
            var result = await client.GetGameHasAchievementsAsync("store-token", 123, "english", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(true, result);
        }

        [TestMethod]
        public async Task GetGameHasAchievementsAsync_ReturnsNull_OnNonSuccessResponse()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(string.Empty)
                }));

            var client = new SteamApiClient(httpClient, logger: null);
            var result = await client.GetGameHasAchievementsAsync("store-token", 123, "english", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task GetSchemaForGameDetailedAsync_TrimsLocalizedText_AndBuildsIconUrls()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{ \"response\": { \"achievements\": [ { " +
                        "\"internal_name\": \" E1FINISHSTORY \", " +
                        "\"localized_name\": \"TLAD: Easy Rider \", " +
                        "\"localized_desc\": \"Finish the story. (The Lost and Damned) \", " +
                        "\"icon\": \" 8a72778b9ede9c31a444cdf493666457b8238718.jpg \", " +
                        "\"icon_gray\": \" 8a72778b9ede9c31a444cdf493666457b8238718.jpg \", " +
                        "\"hidden\": false, " +
                        "\"player_percent_unlocked\": \"5.1\" } ] } }",
                        Encoding.UTF8,
                        "application/json")
                }));

            var client = new SteamApiClient(httpClient, logger: null);
            var result = await client.GetSchemaForGameDetailedAsync("store-token", 12210, "english", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Achievements.Count);

            var achievement = result.Achievements.Single();
            Assert.AreEqual("E1FINISHSTORY", achievement.Name);
            Assert.AreEqual("TLAD: Easy Rider", achievement.DisplayName);
            Assert.AreEqual("Finish the story. (The Lost and Damned)", achievement.Description);
            Assert.AreEqual(
                "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/12210/8a72778b9ede9c31a444cdf493666457b8238718.jpg",
                achievement.Icon);
            Assert.AreEqual(
                "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/12210/8a72778b9ede9c31a444cdf493666457b8238718.jpg",
                achievement.IconGray);
            Assert.AreEqual(5.1d, result.GlobalPercentages["E1FINISHSTORY"]);
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

            public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            {
                _responseFactory = responseFactory ?? throw new ArgumentNullException(nameof(responseFactory));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responseFactory(request));
            }
        }
    }
}
