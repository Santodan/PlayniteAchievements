using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.PSN;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class PsnSessionManagerTests
    {
        [TestMethod]
        public async Task ProbeAuthStateAsync_WithSavedNpssoAndNoCookieFile_DoesNotBootstrap()
        {
            var context = CreateContext("saved-npsso");

            try
            {
                var result = await context.Manager.ProbeAuthStateAsync(CancellationToken.None);

                Assert.AreEqual(AuthOutcome.NotAuthenticated, result.Outcome);
                Assert.AreEqual(0, context.Runtime.BootstrapCalls);
                Assert.AreEqual(0, context.Runtime.ProbeSessionCalls);
            }
            finally
            {
                context.Dispose();
            }
        }

        [TestMethod]
        public async Task ValidateNpssoAsync_PerformsOneBootstrapAttemptAndReturnsAuthenticated()
        {
            var context = CreateContext("valid-npsso");
            context.Runtime.EnqueueBootstrapResult(new PsnBootstrapAttempt(
                AuthOutcome.Authenticated,
                CreateCookieContainer(),
                "https://www.playstation.com/home"));
            context.Runtime.ProbeSessionResponses.Enqueue(true);
            context.Runtime.MobileTokensResponse = CreateMobileTokens();

            try
            {
                var result = await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.ExplicitValidation");

                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual(1, context.Runtime.BootstrapCalls);
                Assert.AreEqual(1, context.Runtime.ProbeSessionCalls);
                Assert.AreEqual(0, context.Runtime.MobileTokenCalls);
                Assert.IsTrue(context.Manager.IsAuthenticated);
            }
            finally
            {
                context.Dispose();
            }
        }

        [TestMethod]
        public async Task ProbeAuthStateAsync_WhenStoreCookiesWorkButNoTokenCanBeMinted_IsNotAuthenticated()
        {
            var context = CreateContext("valid-npsso");
            context.Runtime.EnqueueBootstrapResult(new PsnBootstrapAttempt(
                AuthOutcome.Authenticated,
                CreateCookieContainer(),
                "https://www.playstation.com/home"));
            context.Runtime.ProbeSessionResponses.Enqueue(true);

            try
            {
                await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.ExplicitValidation");

                // Sony's account sign-in lapsed: the store cookies pass, the token step does not.
                context.Runtime.ProbeSessionResponses.Enqueue(true);
                context.Runtime.MobileTokensResponse = null;
                var expired = await context.Manager.ProbeAuthStateAsync(CancellationToken.None);
                Assert.AreEqual(AuthOutcome.NotAuthenticated, expired.Outcome);
                Assert.AreEqual(1, context.Runtime.MobileTokenCalls);

                context.Runtime.ProbeSessionResponses.Enqueue(true);
                context.Runtime.MobileTokensResponse = CreateMobileTokens();
                var renewed = await context.Manager.ProbeAuthStateAsync(CancellationToken.None);
                Assert.IsTrue(renewed.IsSuccess);
            }
            finally
            {
                context.Dispose();
            }
        }

        [TestMethod]
        public async Task ValidateNpssoAsync_ReusesCooldownForSameFailingNpsso()
        {
            var context = CreateContext("failing-npsso");
            context.Runtime.EnqueueBootstrapResult(new PsnBootstrapAttempt(
                AuthOutcome.NotAuthenticated,
                finalAddress: "https://ca.account.sony.com/signin"));

            try
            {
                var first = await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.FirstAttempt");
                var second = await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.SecondAttempt");

                Assert.AreEqual(AuthOutcome.NotAuthenticated, first.Outcome);
                Assert.AreEqual(AuthOutcome.NotAuthenticated, second.Outcome);
                Assert.AreEqual(1, context.Runtime.BootstrapCalls);
            }
            finally
            {
                context.Dispose();
            }
        }

        [TestMethod]
        public async Task ValidateNpssoAsync_AllowsNewAttemptWhenNpssoChanges()
        {
            var context = CreateContext("first-npsso");
            context.Runtime.EnqueueBootstrapResult(new PsnBootstrapAttempt(
                AuthOutcome.NotAuthenticated,
                finalAddress: "https://ca.account.sony.com/signin"));
            context.Runtime.EnqueueBootstrapResult(new PsnBootstrapAttempt(
                AuthOutcome.NotAuthenticated,
                finalAddress: "https://ca.account.sony.com/signin"));

            try
            {
                var first = await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.FirstNpsso");
                context.Settings.Npsso = "second-npsso";
                ProviderRegistry.Write(context.Settings, persistToDisk: true);

                var second = await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.SecondNpsso");

                Assert.AreEqual(AuthOutcome.NotAuthenticated, first.Outcome);
                Assert.AreEqual(AuthOutcome.NotAuthenticated, second.Outcome);
                Assert.AreEqual(2, context.Runtime.BootstrapCalls);
            }
            finally
            {
                context.Dispose();
            }
        }

        [TestMethod]
        public async Task ClearSession_WithClearedNpsso_PreventsImmediateReauthentication()
        {
            var context = CreateContext("clearable-npsso");
            context.Runtime.EnqueueBootstrapResult(new PsnBootstrapAttempt(
                AuthOutcome.Authenticated,
                CreateCookieContainer(),
                "https://www.playstation.com/home"));
            context.Runtime.ProbeSessionResponses.Enqueue(true);

            try
            {
                var initial = await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.BeforeClear");
                Assert.IsTrue(initial.IsSuccess);
                Assert.IsTrue(context.Manager.IsAuthenticated);

                context.Settings.Npsso = string.Empty;
                ProviderRegistry.Write(context.Settings, persistToDisk: true);
                var bootstrapCallsBeforeClearValidation = context.Runtime.BootstrapCalls;

                context.Manager.ClearSession();
                var afterClear = await context.Manager.ValidateNpssoAsync(CancellationToken.None, "Test.AfterClear");

                Assert.AreEqual(AuthOutcome.NotAuthenticated, afterClear.Outcome);
                Assert.IsFalse(context.Manager.IsAuthenticated);
                Assert.AreEqual(string.Empty, context.Settings.Npsso);
                Assert.AreEqual(bootstrapCallsBeforeClearValidation, context.Runtime.BootstrapCalls);
            }
            finally
            {
                context.Dispose();
            }
        }

        private static TestContext CreateContext(string npsso)
        {
            var pluginDataPath = CreateTempDirectory();
            var legacyDataPath = CreateTempDirectory();
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "PSN" });
            var providerSettings = registry.GetSettings<PsnSettings>();
            providerSettings.Npsso = npsso;
            registry.Save(providerSettings, persistToDisk: true);

            var runtime = new FakeRuntime();
            var hooks = new PsnRuntimeHooks
            {
                BootstrapFromNpssoAsync = runtime.BootstrapFromNpssoAsync,
                ClearBrowserCookies = runtime.ClearBrowserCookies,
                LoginInteractiveAsync = runtime.LoginInteractiveAsync,
                ProbeSessionAsync = runtime.ProbeSessionAsync,
                RequestMobileTokensAsync = runtime.RequestMobileTokensAsync,
                UtcNow = () => DateTime.UtcNow
            };
            var manager = new PsnSessionManager(
                api: null,
                logger: new FakeLogger(),
                pluginUserDataPath: pluginDataPath,
                extensionsDataPath: legacyDataPath,
                hooks: hooks);

            return new TestContext(pluginDataPath, legacyDataPath, manager, providerSettings, runtime);
        }

        private static CookieContainer CreateCookieContainer()
        {
            var container = new CookieContainer();
            container.Add(new Uri("https://web.np.playstation.com"), new Cookie("sid", "cookie-value"));
            return container;
        }

        private static MobileTokens CreateMobileTokens()
        {
            return new MobileTokens
            {
                access_token = "mobile-token",
                expires_in = 3600,
                refresh_token = "refresh-token",
                refresh_token_expires_in = 7200,
                scope = "psn:mobile.v2.core",
                token_type = "Bearer"
            };
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(PsnSessionManagerTests),
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);
            return path;
        }

        private sealed class TestContext : IDisposable
        {
            public TestContext(
                string pluginDataPath,
                string legacyDataPath,
                PsnSessionManager manager,
                PsnSettings settings,
                FakeRuntime runtime)
            {
                PluginDataPath = pluginDataPath;
                LegacyDataPath = legacyDataPath;
                Manager = manager;
                Settings = settings;
                Runtime = runtime;
            }

            public string LegacyDataPath { get; }

            public PsnSessionManager Manager { get; }

            public string PluginDataPath { get; }

            public FakeRuntime Runtime { get; }

            public PsnSettings Settings { get; }

            public void Dispose()
            {
                DeleteDirectory(PluginDataPath);
                DeleteDirectory(LegacyDataPath);
            }
        }

        private sealed class FakeRuntime
        {
            private readonly Queue<PsnBootstrapAttempt> _bootstrapResults = new Queue<PsnBootstrapAttempt>();

            public int BootstrapCalls { get; private set; }

            public int ClearCookiesCalls { get; private set; }

            public MobileTokens MobileTokensResponse { get; set; }

            public int MobileTokenCalls { get; private set; }

            public Queue<bool> ProbeSessionResponses { get; } = new Queue<bool>();

            public int ProbeSessionCalls { get; private set; }

            public Task<PsnBootstrapAttempt> BootstrapFromNpssoAsync(string npsso, CancellationToken ct)
            {
                BootstrapCalls++;
                return Task.FromResult(_bootstrapResults.Count > 0
                    ? _bootstrapResults.Dequeue()
                    : new PsnBootstrapAttempt(
                        AuthOutcome.NotAuthenticated,
                        finalAddress: "https://ca.account.sony.com/signin"));
            }

            public void ClearBrowserCookies()
            {
                ClearCookiesCalls++;
            }

            public void EnqueueBootstrapResult(PsnBootstrapAttempt result)
            {
                _bootstrapResults.Enqueue(result);
            }

            public Task<CookieContainer> LoginInteractiveAsync(CancellationToken ct)
            {
                return Task.FromResult<CookieContainer>(null);
            }

            public Task<bool> ProbeSessionAsync(CookieContainer cookieContainer, CancellationToken ct)
            {
                ProbeSessionCalls++;
                return Task.FromResult(ProbeSessionResponses.Count > 0 && ProbeSessionResponses.Dequeue());
            }

            public Task<MobileTokens> RequestMobileTokensAsync(CookieContainer cookieContainer, CancellationToken ct)
            {
                MobileTokenCalls++;
                return Task.FromResult(MobileTokensResponse);
            }
        }

        private sealed class FakeLogger : Playnite.SDK.ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
