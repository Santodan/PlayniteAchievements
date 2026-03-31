using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Manual.Tests
{
    [TestClass]
    public class ManualSourceTests
    {
        [TestMethod]
        public void SteamManualSource_IsAuthenticated_OnlyWhenApiKeyConfigured()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not send requests.")));

            var missingKeySource = new SteamManualSource(httpClient, logger: null, getApiKey: () => " ");
            var configuredSource = new SteamManualSource(httpClient, logger: null, getApiKey: () => "steam-api-key");

            Assert.IsFalse(missingKeySource.IsAuthenticated);
            Assert.IsTrue(configuredSource.IsAuthenticated);
            Assert.IsNull(configuredSource.AuthSession);
        }

        [TestMethod]
        public async Task SteamManualSource_GetAchievementsAsync_UsesKoreanaLanguage()
        {
            Uri capturedUri = null;
            var handler = new StubHttpMessageHandler(request =>
            {
                capturedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{ \"response\": { \"achievements\": [ { \"internal_name\": \"ach_1\", \"localized_name\": \"업적\", \"localized_desc\": \"설명\", \"icon\": \"icon\", \"icon_gray\": \"icon_gray\", \"hidden\": false, \"player_percent_unlocked\": \"12.5\" } ] } }",
                        Encoding.UTF8,
                        "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var source = new SteamManualSource(httpClient, logger: null, getApiKey: () => "steam-api-key");

            var achievements = await source.GetAchievementsAsync("123", "ko", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(capturedUri);
            StringAssert.Contains(capturedUri.Query, "language=koreana");
        }

        [TestMethod]
        public async Task ManualSourceAuthentication_UsesSteamApiKeyAuthForSteamSource()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not send requests.")));
            var source = new SteamManualSource(httpClient, logger: null, getApiKey: () => string.Empty);

            var ex = await Assert.ThrowsExceptionAsync<ManualSourceAuthenticationException>(
                () => ManualSourceAuthentication.EnsureAuthenticatedAsync(source, CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("Steam", ex.SourceKey);
            Assert.AreEqual("LOCPlayAch_ManualAchievements_Schema_ApiKeyRequired", ex.MessageKey);
        }

        [TestMethod]
        public async Task ExophaseManualSource_ExposesAuthSessionAndRequiresAuthenticatedSession()
        {
            var sessionManager = new ExophaseSessionManager
            {
                IsAuthenticated = false,
                ProbeResult = AuthProbeResult.NotAuthenticated()
            };
            var source = new ExophaseManualSource(new FakePlayniteApi(), sessionManager, logger: null, getLanguage: () => "german");

            Assert.AreSame(sessionManager, source.AuthSession);
            Assert.IsFalse(source.IsAuthenticated);

            var ex = await Assert.ThrowsExceptionAsync<ManualSourceAuthenticationException>(
                () => ManualSourceAuthentication.EnsureAuthenticatedAsync(source, CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("Exophase", ex.SourceKey);
            Assert.AreEqual("LOCPlayAch_ManualAchievements_ExophaseAuthRequired", ex.MessageKey);
            Assert.AreEqual(1, sessionManager.ProbeCallCount);
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database => null;
            public IDialogsFactory Dialogs => null;
            public IPlaynitePathsAPI Paths => null;
            public INotificationsAPI Notifications => null;
            public IPlayniteInfoAPI ApplicationInfo => null;
            public IWebViewFactory WebViews => null;
            public IResourceProvider Resources => null;
            public IUriHandlerAPI UriHandler => null;
            public IPlayniteSettingsAPI ApplicationSettings => null;
            public IAddons Addons => null;
            public IEmulationAPI Emulation => null;

            public string ExpandGameVariables(Game game, string source)
            {
                return source;
            }

            public string ExpandGameVariables(Game game, string source, string fallbackValue)
            {
                return source ?? fallbackValue;
            }

            public GameAction ExpandGameVariables(Game game, GameAction source)
            {
                return source;
            }

            public void StartGame(Guid id)
            {
            }

            public void InstallGame(Guid id)
            {
            }

            public void UninstallGame(Guid id)
            {
            }

            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args)
            {
            }

            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args)
            {
            }

            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args)
            {
            }
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
