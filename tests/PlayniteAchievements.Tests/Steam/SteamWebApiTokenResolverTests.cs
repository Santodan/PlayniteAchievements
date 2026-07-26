using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;
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

        [TestMethod]
        public async Task ResolveAsync_PreservesTransientFailureOutcome()
        {
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager(),
                _ => Task.FromResult(SteamWebAuthSession.TransientFailure(AuthOutcome.TimedOut)),
                logger: null);

            var result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(AuthOutcome.TimedOut, result.ProbeResult.Outcome);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.UserId));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Token));
        }

        [TestMethod]
        public async Task ResolveAsync_DoesNotCacheSuccessfulResolution()
        {
            var resolveCalls = 0;
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager(),
                _ =>
                {
                    resolveCalls++;
                    return Task.FromResult(new SteamWebAuthSession("76561198000000000", "token-" + resolveCalls, true));
                },
                logger: null);

            var first = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
            var second = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(first.IsSuccess);
            Assert.IsTrue(second.IsSuccess);
            Assert.AreEqual("token-1", first.Token);
            Assert.AreEqual("token-2", second.Token);
            Assert.AreEqual(2, resolveCalls);
        }

        [TestMethod]
        public async Task ResolveAsync_UsesScopedAuthContextSessionWithoutResolvingAgain()
        {
            var resolveCalls = 0;
            var resolver = new SteamWebApiTokenResolver(
                new FakeSessionManager(),
                _ =>
                {
                    resolveCalls++;
                    return Task.FromResult(new SteamWebAuthSession("76561198000000000", "fallback-token", true));
                },
                logger: null);
            var context = new RefreshAuthContext(Guid.NewGuid());
            context.SetProbeResult(
                "Steam",
                AuthProbeResult.AlreadyAuthenticated("76561198000000000"),
                12,
                new SteamWebAuthSession("76561198000000000", "preflight-token", true));

            resolver.BeginRefreshAuthContext(context);
            SteamWebApiTokenResolution result;
            try
            {
                result = await resolver.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                resolver.EndRefreshAuthContext(context);
            }

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual("preflight-token", result.Token);
            Assert.AreEqual(0, resolveCalls);
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
