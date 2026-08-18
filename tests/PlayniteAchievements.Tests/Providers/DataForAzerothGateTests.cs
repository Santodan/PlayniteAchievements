using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.BattleNet;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Tests.Providers
{
    /// <summary>
    /// Covers the Data for Azeroth bot check: telling a gated site apart from a broken one, replaying
    /// the cookie the user earned in a browser, and never persisting an absence as a fact.
    /// </summary>
    [TestClass]
    public class DataForAzerothGateTests
    {
        private const string IndexUrl = "https://dataforazeroth.com/dynamic/index.json";
        private const string RarityUrl = "https://dataforazeroth.com/dynamic/achievementsrarity.hash.json";

        private const string IndexJson =
            @"{""achievements"":""/dynamic/achievements.a.json"",""achievementsrarity"":""/dynamic/achievementsrarity.hash.json""}";

        private const string RarityJson =
            @"{""achievements"":{""6"":100,""157"":4.3049,""158"":22.5}}";

        // The real interstitial: a 405 carrying an HTML page whose script writes the gate cookie.
        private const string InterstitialHtml =
            "<!DOCTYPE html><html><head><title>Data for Azeroth</title></head><body>" +
            "Due to an increase in bot traffic, I need to ask visitors to please check the box below" +
            "<script>document.cookie = \"dfa-captcha=token-dfa-\" + Date.now();</script></body></html>";

        [TestMethod]
        public async Task LoadRarity_ResolvesRarityDocumentFromTheDynamicIndex()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, HttpStatusCode.OK, "application/json", IndexJson),
                (RarityUrl, HttpStatusCode.OK, "application/json", RarityJson));

            using (var client = NewClient(handler))
            {
                var result = await client.LoadRarityAsync(CancellationToken.None);

                Assert.AreEqual(DataForAzerothStatus.Ok, result.Status);
                Assert.AreEqual(3, result.Rarity.Count);
                Assert.AreEqual(100d, result.Rarity["6"]);
                Assert.AreEqual(4.3049, result.Rarity["157"]);
            }

            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual(IndexUrl, handler.Requests[0].Url);
            Assert.AreEqual(RarityUrl, handler.Requests[1].Url);
        }

        [TestMethod]
        public async Task LoadRarity_SendsTheSiteCookieOnEveryRequest()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, HttpStatusCode.OK, "application/json", IndexJson),
                (RarityUrl, HttpStatusCode.OK, "application/json", RarityJson));

            using (var client = NewClient(handler, Cookie("dfa-captcha", "token-dfa-123")))
            {
                await client.LoadRarityAsync(CancellationToken.None);
            }

            Assert.AreEqual(2, handler.Requests.Count);
            CollectionAssert.AreEqual(
                new[] { "dfa-captcha=token-dfa-123", "dfa-captcha=token-dfa-123" },
                handler.Requests.Select(r => r.CookieHeader).ToArray());
        }

        [TestMethod]
        public async Task LoadRarity_SendsNoCookieHeaderWhenTheStoreHoldsNone()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, HttpStatusCode.OK, "application/json", IndexJson),
                (RarityUrl, HttpStatusCode.OK, "application/json", RarityJson));

            using (var client = NewClient(handler))
            {
                await client.LoadRarityAsync(CancellationToken.None);
            }

            Assert.IsTrue(handler.Requests.All(r => r.CookieHeader == null));
        }

        [TestMethod]
        public async Task LoadRarity_ReportsGatedAndDoesNotRetryTheBotCheck()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, (HttpStatusCode)405, "text/html", InterstitialHtml));

            using (var client = NewClient(handler))
            {
                var result = await client.LoadRarityAsync(CancellationToken.None);

                Assert.AreEqual(DataForAzerothStatus.Gated, result.Status);
                Assert.AreEqual(0, result.Rarity.Count);
            }

            // A gated site is a standing condition, so the backoff ladder must not run: one request,
            // not four. This is the regression fence for the retry storm.
            Assert.AreEqual(1, handler.Requests.Count);
        }

        [TestMethod]
        public async Task LoadRarity_ReportsGatedWhenTheInterstitialArrivesWithSuccessStatus()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, HttpStatusCode.OK, "text/html", InterstitialHtml));

            using (var client = NewClient(handler))
            {
                var result = await client.LoadRarityAsync(CancellationToken.None);

                Assert.AreEqual(DataForAzerothStatus.Gated, result.Status);
            }
        }

        [TestMethod]
        public async Task LoadRarity_TreatsAGenuineNotFoundAsFailureRatherThanAGate()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, HttpStatusCode.NotFound, "text/html", "<html>nothing here</html>"));

            using (var client = NewClient(handler))
            {
                var result = await client.LoadRarityAsync(CancellationToken.None);

                Assert.AreEqual(DataForAzerothStatus.Failed, result.Status);
            }
        }

        [TestMethod]
        public async Task LoadRarity_RefusesAnImplausiblySmallRarityMap()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, HttpStatusCode.OK, "application/json", IndexJson),
                (RarityUrl, HttpStatusCode.OK, "application/json", RarityJson));

            using (var client = NewClient(handler))
            {
                // Leave the production floor in place: three entries is a truncated or substituted
                // payload, not the tens of thousands the live document carries.
                client.MinimumPlausibleRarityEntries = 1000;

                var result = await client.LoadRarityAsync(CancellationToken.None);

                Assert.AreEqual(DataForAzerothStatus.Failed, result.Status);
                Assert.AreEqual(0, result.Rarity.Count);
            }
        }

        [TestMethod]
        public async Task LoadRarity_SkipsTheNetworkWhileTheGateBackoffStands()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, (HttpStatusCode)405, "text/html", InterstitialHtml));

            using (var client = NewClient(handler))
            {
                await client.LoadRarityAsync(CancellationToken.None);
                var second = await client.LoadRarityAsync(CancellationToken.None);

                Assert.AreEqual(DataForAzerothStatus.Gated, second.Status);
            }

            Assert.AreEqual(1, handler.Requests.Count);
        }

        [TestMethod]
        public async Task LoadRarity_RetriesImmediatelyAfterTheGateStateIsReset()
        {
            var handler = ScriptedHandler.Serving(
                (IndexUrl, (HttpStatusCode)405, "text/html", InterstitialHtml));

            using (var client = NewClient(handler))
            {
                await client.LoadRarityAsync(CancellationToken.None);

                // Clearing the check in a browser must take effect on the next refresh, not after a
                // Playnite restart.
                client.ResetGateState();
                await client.LoadRarityAsync(CancellationToken.None);
            }

            Assert.AreEqual(2, handler.Requests.Count);
        }

        [TestMethod]
        public async Task ProbeGate_DistinguishesClearedFromGated()
        {
            var cleared = ScriptedHandler.Serving(
                (IndexUrl, HttpStatusCode.OK, "application/json", IndexJson));
            using (var client = NewClient(cleared))
            {
                Assert.AreEqual(DataForAzerothStatus.Ok, await client.ProbeGateAsync(CancellationToken.None));
            }

            var gated = ScriptedHandler.Serving(
                (IndexUrl, (HttpStatusCode)405, "text/html", InterstitialHtml));
            using (var client = NewClient(gated))
            {
                Assert.AreEqual(DataForAzerothStatus.Gated, await client.ProbeGateAsync(CancellationToken.None));
            }
        }

        [TestMethod]
        public void IsTransientError_TreatsTheBotCheckAsPermanentAndRealFailuresAsRetryable()
        {
            var gated = new DataForAzerothGatedException("gated", 405, cookieSent: false, cookieNames: "<none>");

            Assert.IsFalse(BattleNetApiClient.IsTransientError(gated));
            Assert.IsTrue(BattleNetApiClient.IsTransientError(new BattleNetTransientException("HTTP 503")));
        }

        [TestMethod]
        public void IsGatedResponse_MatchesTheGateWithoutClaimingEveryErrorPage()
        {
            Assert.IsTrue(DataForAzerothClient.IsGatedResponse(405, "text/html"));
            Assert.IsTrue(DataForAzerothClient.IsGatedResponse(405, null));
            Assert.IsTrue(DataForAzerothClient.IsGatedResponse(200, "text/html"));
            Assert.IsTrue(DataForAzerothClient.IsGatedResponse(503, "text/html"));

            Assert.IsFalse(DataForAzerothClient.IsGatedResponse(200, "application/json"));
            Assert.IsFalse(DataForAzerothClient.IsGatedResponse(404, "text/html"));
            Assert.IsFalse(DataForAzerothClient.IsGatedResponse(500, "application/json"));
        }

        [TestMethod]
        public void CookieReader_SelectsByDomainRatherThanCookieName()
        {
            var cookies = new List<HttpCookie>
            {
                Cookie("dfa-captcha", "token-dfa-1"),
                Cookie("_ga", "analytics", ".dataforazeroth.com"),
                Cookie("session", "abc", "www.dataforazeroth.com"),
                Cookie("steamLoginSecure", "nope", "steamcommunity.com")
            };

            var filtered = CefCookieReader.Filter(cookies, DataForAzerothClient.SiteDomain);

            // Name-based filtering would drop the analytics and session cookies, so a gate that starts
            // checking a differently named cookie would silently stop working.
            CollectionAssert.AreEquivalent(
                new[] { "dfa-captcha", "_ga", "session" },
                filtered.Select(c => c.Name).ToArray());
        }

        [TestMethod]
        public void CookieReader_BuildsAHeaderAndDropsWhatCannotBeSent()
        {
            var header = CefCookieReader.BuildCookieHeader(new List<HttpCookie>
            {
                Cookie("a", "one"),
                Cookie("b", "tw\r\no;two"),
                Cookie("empty", string.Empty)
            });

            Assert.AreEqual("a=one; b=twotwo", header);
            Assert.IsNull(CefCookieReader.BuildCookieHeader(new List<HttpCookie>()));
        }

        [TestMethod]
        public void KeepStoredRarity_ProtectsRealPercentagesFromAnUnknownPayload()
        {
            // The gated case: no percent and the enum default, over a stored percentage.
            Assert.IsTrue(SqlNadoCacheBehavior.ShouldKeepStoredRarity(
                null, RarityTier.Common, 12.5, "Rare"));

            // A stored tier with no percent is still real data worth keeping.
            Assert.IsTrue(SqlNadoCacheBehavior.ShouldKeepStoredRarity(
                null, RarityTier.Common, null, "Uncommon"));

            // A payload that knows the percentage always wins.
            Assert.IsFalse(SqlNadoCacheBehavior.ShouldKeepStoredRarity(
                4.2, RarityTier.Rare, 12.5, "Rare"));

            // A provider asserting a non-default tier without a percent (GameJolt reads it from trophy
            // difficulty) must keep overwriting.
            Assert.IsFalse(SqlNadoCacheBehavior.ShouldKeepStoredRarity(
                null, RarityTier.Rare, 12.5, "Common"));

            // Nothing stored worth protecting.
            Assert.IsFalse(SqlNadoCacheBehavior.ShouldKeepStoredRarity(
                null, RarityTier.Common, null, "Common"));
            Assert.IsFalse(SqlNadoCacheBehavior.ShouldKeepStoredRarity(
                null, RarityTier.Common, null, null));
        }

        private static DataForAzerothClient NewClient(ScriptedHandler handler, params HttpCookie[] cookies)
        {
            var client = new DataForAzerothClient(
                null,
                handler,
                ct => Task.FromResult(cookies.ToList()));

            // The fixtures carry a handful of entries rather than the live document's tens of
            // thousands; the plausibility floor is exercised by its own test.
            client.MinimumPlausibleRarityEntries = 1;
            return client;
        }

        private static HttpCookie Cookie(string name, string value, string domain = "dataforazeroth.com")
        {
            return new HttpCookie
            {
                Name = name,
                Value = value,
                Domain = domain,
                Path = "/"
            };
        }

        private sealed class ScriptedRequest
        {
            public string Url { get; set; }
            public string CookieHeader { get; set; }
        }

        /// <summary>
        /// Serves a fixed response per URL and records what was asked for, including the Cookie header
        /// - the point of the whole exercise.
        /// </summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)> _responses =
                new Dictionary<string, (HttpStatusCode, string, string)>(StringComparer.OrdinalIgnoreCase);

            public List<ScriptedRequest> Requests { get; } = new List<ScriptedRequest>();

            public static ScriptedHandler Serving(
                params (string Url, HttpStatusCode Status, string ContentType, string Body)[] responses)
            {
                var handler = new ScriptedHandler();
                foreach (var response in responses)
                {
                    handler._responses[response.Url] = (response.Status, response.ContentType, response.Body);
                }

                return handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var url = request.RequestUri.ToString();
                Requests.Add(new ScriptedRequest
                {
                    Url = url,
                    CookieHeader = request.Headers.TryGetValues("Cookie", out var values)
                        ? string.Join("; ", values)
                        : null
                });

                if (!_responses.TryGetValue(url, out var scripted))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(string.Empty)
                    });
                }

                var content = new StringContent(scripted.Body);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(scripted.ContentType);

                return Task.FromResult(new HttpResponseMessage(scripted.Status) { Content = content });
            }
        }
    }
}
