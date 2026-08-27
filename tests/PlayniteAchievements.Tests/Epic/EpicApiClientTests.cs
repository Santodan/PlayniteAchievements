using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Epic;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Epic.Tests
{
    [TestClass]
    public class EpicApiClientTests
    {
        [TestMethod]
        public void IsTransientError_ReturnsTrue_ForTimeoutAndNetworkCases()
        {
            Assert.IsTrue(EpicApiClient.IsTransientError(new TimeoutException()));
            Assert.IsTrue(EpicApiClient.IsTransientError(new System.Net.Http.HttpRequestException("network")));
            Assert.IsTrue(EpicApiClient.IsTransientError(new EpicTransientException("transient")));
        }

        [TestMethod]
        public void IsTransientError_ReturnsTrue_For429And5xx()
        {
            Assert.IsTrue(EpicApiClient.IsTransientError(new EpicApiHttpException((HttpStatusCode)429, "429")));
            Assert.IsTrue(EpicApiClient.IsTransientError(new EpicApiHttpException(HttpStatusCode.BadGateway, "502")));
        }

        [TestMethod]
        public void IsTransientError_ReturnsFalse_For4xxNonRetryable()
        {
            Assert.IsFalse(EpicApiClient.IsTransientError(new EpicApiHttpException(HttpStatusCode.BadRequest, "400")));
            Assert.IsFalse(EpicApiClient.IsTransientError(new Exception("other")));
            Assert.IsFalse(EpicApiClient.IsTransientError(null));
        }

        [TestMethod]
        public void EpicAuthResult_IsSuccess_ForAuthenticatedOutcomesOnly()
        {
            var ok = EpicAuthResult.Create(EpicAuthOutcome.Authenticated, "k");
            var already = EpicAuthResult.Create(EpicAuthOutcome.AlreadyAuthenticated, "k");
            var fail = EpicAuthResult.Create(EpicAuthOutcome.Failed, "k");

            Assert.IsTrue(ok.IsSuccess);
            Assert.IsTrue(already.IsSuccess);
            Assert.IsFalse(fail.IsSuccess);
        }

        [TestMethod]
        public async Task GetAchievementsAsync_RequeriesSchemaWhenLanguageChanges()
        {
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.GlobalLanguage = "english";

            var session = new EpicSessionManager
            {
                AccessToken = "stub-token",
                AccountId = "stub-account"
            };
            var handler = new EpicLocalizationStubHandler();

            using (var httpClient = new HttpClient(handler))
            {
                var api = new EpicApiClient(httpClient, logger: null, sessionManager: session, settings: settings);

                var english = await api.GetAchievementsAsync("test-game", "stub-account", CancellationToken.None).ConfigureAwait(false);

                settings.Persisted.GlobalLanguage = "german";
                var german = await api.GetAchievementsAsync("test-game", "stub-account", CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(1, english.Count);
                Assert.AreEqual(1, german.Count);
                Assert.AreEqual("Title-en-US", english[0].Title);
                Assert.AreEqual("Title-de-DE", german[0].Title);
                Assert.AreEqual(2, handler.SchemaCallCount);
                CollectionAssert.AreEqual(new List<string> { "en-US", "de-DE" }, handler.SchemaLocales);
            }
        }

        private sealed class EpicLocalizationStubHandler : HttpMessageHandler
        {
            public int SchemaCallCount { get; private set; }
            public List<string> SchemaLocales { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri?.AbsoluteUri ?? string.Empty;

                if (url.IndexOf("library-service.live.use1a.on.epicgames.com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    const string assetsJson = "{ \"responseMetadata\": { }, \"records\": [ { \"namespace\": \"sandbox-test\", \"appName\": \"test-game\" } ] }";
                    return JsonResponse(HttpStatusCode.OK, assetsJson);
                }

                if (url.IndexOf("launcher.store.epicgames.com/graphql", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var payload = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (payload.IndexOf("productAchievementsRecordBySandbox", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var parsed = JObject.Parse(payload);
                        var locale = parsed["variables"]?["Locale"]?.ToString() ?? "unknown";
                        SchemaCallCount++;
                        SchemaLocales.Add(locale);

                        var schemaJson =
                            "{ \"data\": { \"Achievement\": { \"productAchievementsRecordBySandbox\": { \"productId\": \"product-test\", \"achievements\": [ { \"achievement\": { \"name\": \"ach-1\", \"hidden\": false, \"unlockedDisplayName\": \"Title-" + locale + "\", \"unlockedDescription\": \"Desc-" + locale + "\", \"unlockedIconLink\": \"https://example.com/u.png\", \"lockedIconLink\": \"https://example.com/l.png\", \"XP\": 10, \"rarity\": { \"percent\": 1.5 } } } ] } } } }";
                        return JsonResponse(HttpStatusCode.OK, schemaJson);
                    }

                    if (payload.IndexOf("playerProfileAchievementsByProductId", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        const string progressJson = "{ \"data\": { \"PlayerProfile\": { \"playerProfile\": { \"productAchievements\": { \"data\": { \"playerAchievements\": [] } } } } } }";
                        return JsonResponse(HttpStatusCode.OK, progressJson);
                    }

                    return JsonResponse(HttpStatusCode.BadRequest, "{ \"error\": \"unknown query\" }");
                }

                return JsonResponse(HttpStatusCode.NotFound, "{ \"error\": \"not found\" }");
            }

            private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
            {
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
