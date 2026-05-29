using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Steam;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamWebApiTokenResolverTests
    {
        [TestMethod]
        public async Task ResolveAsync_ReturnsToken_WhenWebSessionIsComplete()
        {
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager(),
                _ => Task.FromResult(new SteamWebAuthSession("76561198000000000", " store-token ", true)),
                logger: null);

            var result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual("76561198000000000", result.UserId);
            Assert.AreEqual("store-token", result.Token);
        }

        [TestMethod]
        public async Task ResolveAsync_FailsWithUserId_WhenTokenMissing()
        {
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager(),
                _ => Task.FromResult(new SteamWebAuthSession("76561198000000000", null, true)),
                logger: null);

            var result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("76561198000000000", result.UserId);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Token));
        }

        [TestMethod]
        public async Task ResolveAsync_FailsWithoutUserId_WhenSessionMissing()
        {
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager(),
                _ => Task.FromResult(SteamWebAuthSession.Empty()),
                logger: null);

            var result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.UserId));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Token));
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
