using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamWebApiTokenResolverTests
    {
        [TestMethod]
        public async Task ResolveAsync_DoesNotCallTokenDelegate_WhenSessionProbeFails()
        {
            var tokenCalls = 0;
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager { ProbeResult = AuthProbeResult.NotAuthenticated() },
                _ =>
                {
                    tokenCalls++;
                    return Task.FromResult("store-token");
                },
                logger: null);

            var result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(0, tokenCalls);
        }

        [TestMethod]
        public async Task ResolveAsync_ReturnsTrimmedToken_WhenSessionAndTokenSucceed()
        {
            var tokenCalls = 0;
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager { ProbeResult = AuthProbeResult.AlreadyAuthenticated("76561198000000000") },
                _ =>
                {
                    tokenCalls++;
                    return Task.FromResult("  store-token  ");
                },
                logger: null);

            var result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual("76561198000000000", result.UserId);
            Assert.AreEqual("store-token", result.Token);
            Assert.AreEqual(1, tokenCalls);
        }

        [TestMethod]
        public async Task ResolveAsync_FailsWhenTokenMissing()
        {
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager { ProbeResult = AuthProbeResult.AlreadyAuthenticated("76561198000000000") },
                _ => Task.FromResult<string>(null),
                logger: null);

            var result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Token));
        }

        [TestMethod]
        public void SteamAsyncConfigResponse_DeserializesNestedWebApiToken()
        {
            const string json = "{ \"success\": 1, \"data\": { \"webapi_token\": \"store-token\" } }";

            var response = JsonConvert.DeserializeObject<SteamAsyncConfigResponse>(json);

            Assert.IsNotNull(response);
            Assert.AreEqual(1, response.Success);
            Assert.IsNotNull(response.Data);
            Assert.AreEqual("store-token", response.Data.WebApiToken);
        }

        private sealed class FakeSessionManager : ISessionManager
        {
            public string ProviderKey => "Steam";

            public AuthProbeResult ProbeResult { get; set; } = AuthProbeResult.NotAuthenticated();

            public Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
            {
                return Task.FromResult(ProbeResult);
            }

            public Task<AuthProbeResult> AuthenticateInteractiveAsync(bool forceInteractive, CancellationToken ct, IProgress<AuthProgressStep> progress = null)
            {
                return Task.FromResult(ProbeResult);
            }

            public void ClearSession()
            {
            }
        }
    }
}
