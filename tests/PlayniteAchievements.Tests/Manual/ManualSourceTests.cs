using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Steam;
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
        public void SteamManualSource_ExposesSteamAuthSession()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not send requests.")));
            var sessionManager = new FakeSessionManager
            {
                ProbeResult = AuthProbeResult.AlreadyAuthenticated("76561198000000000")
            };
            var source = CreateSteamSource(
                httpClient,
                sessionManager,
                _ => Task.FromResult("store-token"));

            Assert.AreSame(sessionManager, source.AuthSession);
            Assert.IsFalse(source.IsAuthenticated);
        }

        [TestMethod]
        public async Task SteamManualSource_SearchGamesAsync_DoesNotRequireSteamAuth()
        {
            var sessionManager = new FakeSessionManager
            {
                ProbeResult = AuthProbeResult.NotAuthenticated()
            };

            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            {
                if (_.RequestUri.AbsoluteUri.IndexOf("appdetails", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new HttpRequestException("AppDetails unavailable");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{ \"items\": [ { \"id\": 400, \"name\": \"Portal\", \"tiny_image\": \"tiny\", \"header_image\": \"hdr\" } ], \"total\": 1 }",
                        Encoding.UTF8,
                        "application/json")
                };
            }));

            var source = CreateSteamSource(
                httpClient,
                sessionManager,
                _ => Task.FromResult("unused-token"));

            var results = await source.SearchGamesAsync("portal", "english", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(0, sessionManager.ProbeCallCount);
        }

        [TestMethod]
        public async Task SteamManualSource_GetAchievementsAsync_UsesKoreanaLanguage()
        {
            Uri capturedUri = null;
            var sessionManager = new FakeSessionManager
            {
                ProbeResult = AuthProbeResult.AlreadyAuthenticated("76561198000000000")
            };
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
            var source = CreateSteamSource(
                httpClient,
                sessionManager,
                _ => Task.FromResult("store-token"));

            var achievements = await source.GetAchievementsAsync("123", "ko", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(capturedUri);
            StringAssert.Contains(capturedUri.Query, "access_token=store-token");
            StringAssert.Contains(capturedUri.Query, "language=koreana");
            Assert.IsNotNull(achievements);
        }

        [TestMethod]
        public async Task SteamManualSource_GetAchievementsAsync_ThrowsWhenTokenCannotBeResolved()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not send requests.")));
            var sessionManager = new FakeSessionManager
            {
                ProbeResult = AuthProbeResult.AlreadyAuthenticated("76561198000000000")
            };
            var source = CreateSteamSource(
                httpClient,
                sessionManager,
                _ => Task.FromResult<string>(null));

            var ex = await Assert.ThrowsExceptionAsync<ManualSourceAuthenticationException>(
                () => source.GetAchievementsAsync("123", "english", CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("Steam", ex.SourceKey);
            Assert.AreEqual("LOCPlayAch_ManualAchievements_Schema_SteamAuthRequired", ex.MessageKey);
        }

        [TestMethod]
        public async Task ManualSourceAuthentication_UsesSteamSessionAuthForSteamSource()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not send requests.")));
            var sessionManager = new FakeSessionManager
            {
                ProbeResult = AuthProbeResult.NotAuthenticated()
            };
            var source = CreateSteamSource(
                httpClient,
                sessionManager,
                _ => Task.FromResult("store-token"));

            var ex = await Assert.ThrowsExceptionAsync<ManualSourceAuthenticationException>(
                () => ManualSourceAuthentication.EnsureAuthenticatedAsync(source, CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("Steam", ex.SourceKey);
            Assert.AreEqual("LOCPlayAch_ManualAchievements_Schema_SteamAuthRequired", ex.MessageKey);
            Assert.AreEqual(1, sessionManager.ProbeCallCount);
        }

        [TestMethod]
        public async Task ManualSourceAuthentication_AllowsSteamWhenSessionProbeSucceeds()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not send requests.")));
            var sessionManager = new FakeSessionManager
            {
                ProbeResult = AuthProbeResult.AlreadyAuthenticated("76561198000000000")
            };
            var source = CreateSteamSource(
                httpClient,
                sessionManager,
                _ => Task.FromResult("store-token"));

            await ManualSourceAuthentication
                .EnsureAuthenticatedAsync(source, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(1, sessionManager.ProbeCallCount);
        }

        [TestMethod]
        public async Task ExophaseManualSource_ExposesAuthSessionAndRequiresAuthenticatedSession()
        {
            var sessionManager = new ExophaseSessionManager
            {
                IsAuthenticated = false,
                ProbeResult = AuthProbeResult.NotAuthenticated()
            };
            var source = new ExophaseManualSource(
                new FakePlayniteApi(),
                sessionManager,
                logger: null,
                getLanguage: () => "german",
                requireExophaseAuthentication: () => true);

            Assert.AreSame(sessionManager, source.AuthSession);
            Assert.IsFalse(source.IsAuthenticated);

            var ex = await Assert.ThrowsExceptionAsync<ManualSourceAuthenticationException>(
                () => ManualSourceAuthentication.EnsureAuthenticatedAsync(source, CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("Exophase", ex.SourceKey);
            Assert.AreEqual("LOCPlayAch_ManualAchievements_ExophaseAuthRequired", ex.MessageKey);
            Assert.AreEqual(1, sessionManager.ProbeCallCount);
        }

        [TestMethod]
        public async Task ManualSourceAuthentication_AllowsExophaseWhenAuthRequirementDisabled()
        {
            var sessionManager = new ExophaseSessionManager
            {
                IsAuthenticated = false,
                ProbeResult = AuthProbeResult.NotAuthenticated()
            };

            var source = new ExophaseManualSource(
                new FakePlayniteApi(),
                sessionManager,
                logger: null,
                getLanguage: () => "english",
                requireExophaseAuthentication: () => false);

            await ManualSourceAuthentication
                .EnsureAuthenticatedIfRequiredAsync(source, requireExophaseAuthentication: false, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(0, sessionManager.ProbeCallCount);
        }

        [TestMethod]
        public async Task ManualSourceAuthentication_AllowsExophaseWhenLinkAllowsUnauthenticatedSchemaFetch()
        {
            var sessionManager = new ExophaseSessionManager
            {
                IsAuthenticated = false,
                ProbeResult = AuthProbeResult.NotAuthenticated()
            };

            var source = new ExophaseManualSource(
                new FakePlayniteApi(),
                sessionManager,
                logger: null,
                getLanguage: () => "english",
                requireExophaseAuthentication: () => true);

            var link = new ManualAchievementLink
            {
                SourceKey = "Exophase",
                SourceGameId = "test-game",
                AllowUnauthenticatedSchemaFetch = true
            };

            await ManualSourceAuthentication
                .EnsureAuthenticatedIfRequiredAsync(source, requireExophaseAuthentication: true, link, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(0, sessionManager.ProbeCallCount);
        }

        [TestMethod]
        public async Task ManualSourceAuthentication_AllowsLegacyExophaseLinkWithoutSchemaFetchFlag()
        {
            var sessionManager = new ExophaseSessionManager
            {
                IsAuthenticated = false,
                ProbeResult = AuthProbeResult.NotAuthenticated()
            };

            var source = new ExophaseManualSource(
                new FakePlayniteApi(),
                sessionManager,
                logger: null,
                getLanguage: () => "english",
                requireExophaseAuthentication: () => true);

            await ManualSourceAuthentication
                .EnsureAuthenticatedIfRequiredAsync(
                    source,
                    requireExophaseAuthentication: true,
                    new ManualAchievementLink { SourceKey = "Exophase", SourceGameId = "legacy-game" },
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(0, sessionManager.ProbeCallCount);
        }

        [TestMethod]
        public async Task ManualSourceAuthentication_StillRequiresSteamWhenExophaseRequirementDisabled()
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not send requests.")));
            var source = CreateSteamSource(
                httpClient,
                new FakeSessionManager { ProbeResult = AuthProbeResult.NotAuthenticated() },
                _ => Task.FromResult("store-token"));

            var ex = await Assert.ThrowsExceptionAsync<ManualSourceAuthenticationException>(
                () => ManualSourceAuthentication.EnsureAuthenticatedIfRequiredAsync(
                    source,
                    requireExophaseAuthentication: false,
                    CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("Steam", ex.SourceKey);
            Assert.AreEqual("LOCPlayAch_ManualAchievements_Schema_SteamAuthRequired", ex.MessageKey);
        }

        [TestMethod]
        public void ExophaseApiClient_NormalizeLegacyManualApiName_HandlesDisplayAndPrefixedKeys()
        {
            Assert.AreEqual(
                "exophase_a_fateful_sausage",
                ExophaseApiClient.NormalizeLegacyManualApiName("A Fateful Sausage"));

            Assert.AreEqual(
                "exophase_a_fateful_sausage",
                ExophaseApiClient.NormalizeLegacyManualApiName(" exophase_A_Fateful_Sausage "));

            Assert.AreEqual(
                "exophase_a_fateful_sausage",
                ExophaseApiClient.NormalizeLegacyManualApiName("a-fateful sausage"));

            Assert.IsNull(ExophaseApiClient.NormalizeLegacyManualApiName("   "));
        }

        [TestMethod]
        public void ManualLinkTransientStore_RegisterRestore_RestoresPreviousFallback()
        {
            var gameId = Guid.NewGuid();
            var settings = new ManualSettings();
            var previous = new ManualAchievementLink
            {
                SourceKey = "Steam",
                SourceGameId = "old-game",
                UnlockStates = new Dictionary<string, bool> { ["ACH_OLD"] = true },
                UnlockTimes = new Dictionary<string, DateTime?> { ["ACH_OLD"] = DateTime.UtcNow.AddDays(-1) },
                CreatedUtc = DateTime.UtcNow.AddDays(-2),
                LastModifiedUtc = DateTime.UtcNow.AddDays(-1)
            };
            var replacement = new ManualAchievementLink
            {
                SourceKey = "Steam",
                SourceGameId = "new-game",
                UnlockStates = new Dictionary<string, bool> { ["ACH_NEW"] = true },
                CreatedUtc = DateTime.UtcNow,
                LastModifiedUtc = DateTime.UtcNow
            };
            settings.AchievementLinks[gameId] = previous;

            var snapshot = ManualLinkTransientStore.Register(gameId, replacement, settings);

            replacement.SourceGameId = "mutated-new-game";
            previous.SourceGameId = "mutated-old-game";
            Assert.AreEqual("new-game", settings.AchievementLinks[gameId].SourceGameId);

            ManualLinkTransientStore.Restore(gameId, snapshot, settings);

            Assert.AreEqual("old-game", settings.AchievementLinks[gameId].SourceGameId);
            Assert.AreNotSame(previous, settings.AchievementLinks[gameId]);
            Assert.IsTrue(settings.AchievementLinks[gameId].UnlockStates["ACH_OLD"]);
        }

        [TestMethod]
        public void ManualLinkTransientStore_RegisterRestore_RemovesWhenNoPreviousFallback()
        {
            var gameId = Guid.NewGuid();
            var settings = new ManualSettings();
            var replacement = new ManualAchievementLink
            {
                SourceKey = "Steam",
                SourceGameId = "new-game",
                CreatedUtc = DateTime.UtcNow,
                LastModifiedUtc = DateTime.UtcNow
            };

            var snapshot = ManualLinkTransientStore.Register(gameId, replacement, settings);

            Assert.IsTrue(settings.AchievementLinks.ContainsKey(gameId));

            ManualLinkTransientStore.Restore(gameId, snapshot, settings);

            Assert.IsFalse(settings.AchievementLinks.ContainsKey(gameId));
        }

        private static SteamManualSource CreateSteamSource(
            HttpClient httpClient,
            ISessionManager sessionManager,
            Func<CancellationToken, Task<string>> resolveTokenAsync)
        {
            return new SteamManualSource(
                httpClient,
                logger: null,
                new SteamWebApiTokenResolver(
                    sessionManager,
                    async ct =>
                    {
                        var probeResult = await sessionManager.ProbeAuthStateAsync(ct).ConfigureAwait(false);
                        var token = await resolveTokenAsync(ct).ConfigureAwait(false);
                        return new SteamWebAuthSession(
                            probeResult?.UserId,
                            token,
                            hasSteamSessionCookies: probeResult?.IsSuccess == true);
                    },
                    logger: null));
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

        private sealed class FakeSessionManager : ISessionManager
        {
            public string ProviderKey => "Steam";

            public int ProbeCallCount { get; private set; }

            public AuthProbeResult ProbeResult { get; set; } = AuthProbeResult.NotAuthenticated();

            public Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
            {
                ProbeCallCount++;
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
