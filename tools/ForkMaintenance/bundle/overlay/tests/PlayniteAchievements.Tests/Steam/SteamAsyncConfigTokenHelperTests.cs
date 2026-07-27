using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamAsyncConfigTokenHelperTests
    {
        [TestMethod]
        public void ParseResponse_ReturnsToken_WhenWebApiTokenPresent()
        {
            const string json = "{ \"success\": 1, \"data\": { \"webapi_token\": \" store-token \" } }";

            var result = SteamAsyncConfigTokenHelper.ParseResponse(json);

            Assert.AreEqual(SteamAsyncConfigResponseState.Success, result.State);
            Assert.AreEqual("store-token", result.Token);
            Assert.IsTrue(result.HasToken);
        }

        [TestMethod]
        public void ParseResponse_ClassifiesArrayDataAsNeedsWarmup()
        {
            const string json = "{ \"success\": 1, \"data\": [] }";

            var result = SteamAsyncConfigTokenHelper.ParseResponse(json);

            Assert.AreEqual(SteamAsyncConfigResponseState.NeedsWarmup, result.State);
            Assert.AreEqual("array_data", result.Detail);
            Assert.IsFalse(result.HasToken);
        }

        [TestMethod]
        public void ParseResponse_ClassifiesMissingOrBlankTokenPayloadsAsNeedsWarmup()
        {
            var payloads = new[]
            {
                "{ \"success\": 1 }",
                "{ \"success\": 1, \"data\": null }",
                "{ \"success\": 1, \"data\": {} }",
                "{ \"success\": 1, \"data\": { \"webapi_token\": \"   \" } }"
            };

            foreach (var payload in payloads)
            {
                var result = SteamAsyncConfigTokenHelper.ParseResponse(payload);

                Assert.AreEqual(SteamAsyncConfigResponseState.NeedsWarmup, result.State, payload);
                Assert.IsFalse(result.HasToken, payload);
            }
        }

        [TestMethod]
        public void ParseResponse_TreatsMalformedJsonAsHardFailure()
        {
            var result = SteamAsyncConfigTokenHelper.ParseResponse("{ not-json");

            Assert.AreEqual(SteamAsyncConfigResponseState.HardFailure, result.State);
            Assert.IsNotNull(result.Exception);
            Assert.IsFalse(result.HasToken);
        }

        [TestMethod]
        public async Task ResolveTokenAsync_WarmsOnceAndRetries_WhenInitialResponseNeedsWarmup()
        {
            var responses = new Queue<SteamAsyncConfigRequestResult>(new[]
            {
                SteamAsyncConfigRequestResult.Success(200, "OK", "{ \"success\": 1, \"data\": [] }"),
                SteamAsyncConfigRequestResult.Success(200, "OK", "{ \"success\": 1, \"data\": { \"webapi_token\": \"store-token\" } }")
            });
            var requestCalls = 0;
            var warmCalls = 0;
            var syncCalls = 0;

            var token = await SteamAsyncConfigTokenHelper.ResolveTokenAsync(
                _ =>
                {
                    requestCalls++;
                    return Task.FromResult(responses.Dequeue());
                },
                _ =>
                {
                    warmCalls++;
                    return Task.FromResult(true);
                },
                () => syncCalls++,
                logger: null,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("store-token", token);
            Assert.AreEqual(2, requestCalls);
            Assert.AreEqual(1, warmCalls);
            Assert.AreEqual(1, syncCalls);
        }

        [TestMethod]
        public async Task ResolveTokenAsync_DoesNotWarm_WhenResponseIsMalformed()
        {
            var requestCalls = 0;
            var warmCalls = 0;

            var token = await SteamAsyncConfigTokenHelper.ResolveTokenAsync(
                _ =>
                {
                    requestCalls++;
                    return Task.FromResult(SteamAsyncConfigRequestResult.Success(200, "OK", "{ not-json"));
                },
                _ =>
                {
                    warmCalls++;
                    return Task.FromResult(true);
                },
                syncCookiesAction: null,
                logger: null,
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(token);
            Assert.AreEqual(1, requestCalls);
            Assert.AreEqual(0, warmCalls);
        }

        [TestMethod]
        public async Task ResolveTokenAsync_OnlyRetriesOnce()
        {
            var responses = new Queue<SteamAsyncConfigRequestResult>(new[]
            {
                SteamAsyncConfigRequestResult.Success(200, "OK", "{ \"success\": 1, \"data\": [] }"),
                SteamAsyncConfigRequestResult.Success(200, "OK", "{ \"success\": 1, \"data\": [] }"),
                SteamAsyncConfigRequestResult.Success(200, "OK", "{ \"success\": 1, \"data\": { \"webapi_token\": \"late-token\" } }")
            });
            var requestCalls = 0;
            var warmCalls = 0;

            var token = await SteamAsyncConfigTokenHelper.ResolveTokenAsync(
                _ =>
                {
                    requestCalls++;
                    return Task.FromResult(responses.Dequeue());
                },
                _ =>
                {
                    warmCalls++;
                    return Task.FromResult(true);
                },
                syncCookiesAction: null,
                logger: null,
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(token);
            Assert.AreEqual(2, requestCalls);
            Assert.AreEqual(1, warmCalls);
            Assert.AreEqual(1, responses.Count);
        }
    }
}
