using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Refresh.Tests
{
    /// <summary>
    /// The in-game monitor resolves a provider synchronously from each provider's
    /// <c>IsAuthenticated</c> snapshot. When that snapshot lags (an expired token that persisted
    /// credentials would renew), a non-interactive probe of the capable providers must be enough
    /// to make the provider resolvable again, and unrelated providers must not be probed.
    /// </summary>
    [TestClass]
    public class RefreshRuntimeInGameProviderTests
    {
        [TestMethod]
        public async Task ResolveInGameProviderWithProbeAsync_StaleSnapshot_ProbeRenewsAndResolves()
        {
            var game = new Game("Atari Mania");
            var epic = new FakeProvider("Epic", isCapable: g => g.Id == game.Id, authenticated: false);
            epic.Session.OnProbe = () =>
            {
                epic.Authenticated = true;
                return AuthProbeResult.AlreadyAuthenticated();
            };
            var runtime = CreateRuntime(epic);

            Assert.IsNull(runtime.ResolveInGameProvider(game), "snapshot alone must not resolve a stale session");

            var resolved = await runtime.ResolveInGameProviderWithProbeAsync(game).ConfigureAwait(false);

            Assert.AreSame(epic, resolved);
            Assert.AreEqual(1, epic.Session.ProbeCalls);
        }

        [TestMethod]
        public async Task ResolveInGameProviderWithProbeAsync_ProbeStaysUnauthenticated_ReturnsNull()
        {
            var game = new Game("Atari Mania");
            var epic = new FakeProvider("Epic", isCapable: _ => true, authenticated: false);
            epic.Session.OnProbe = AuthProbeResult.NotAuthenticated;
            var runtime = CreateRuntime(epic);

            var resolved = await runtime.ResolveInGameProviderWithProbeAsync(game).ConfigureAwait(false);

            Assert.IsNull(resolved);
            Assert.AreEqual(1, epic.Session.ProbeCalls);
        }

        [TestMethod]
        public async Task ResolveInGameProviderWithProbeAsync_AuthenticatedSnapshot_DoesNotProbe()
        {
            var game = new Game("Atari Mania");
            var epic = new FakeProvider("Epic", isCapable: _ => true, authenticated: true);
            var runtime = CreateRuntime(epic);

            var resolved = await runtime.ResolveInGameProviderWithProbeAsync(game).ConfigureAwait(false);

            Assert.AreSame(epic, resolved);
            Assert.AreEqual(0, epic.Session.ProbeCalls);
        }

        [TestMethod]
        public async Task ResolveInGameProviderWithProbeAsync_ProbesOnlyCapableProviders()
        {
            var game = new Game("Atari Mania");
            var epic = new FakeProvider("Epic", isCapable: _ => true, authenticated: false);
            epic.Session.OnProbe = () =>
            {
                epic.Authenticated = true;
                return AuthProbeResult.AlreadyAuthenticated();
            };
            var steam = new FakeProvider("Steam", isCapable: _ => false, authenticated: false);
            steam.Session.OnProbe = AuthProbeResult.NotAuthenticated;
            var runtime = CreateRuntime(steam, epic);

            var resolved = await runtime.ResolveInGameProviderWithProbeAsync(game).ConfigureAwait(false);

            Assert.AreSame(epic, resolved);
            Assert.AreEqual(1, epic.Session.ProbeCalls);
            Assert.AreEqual(0, steam.Session.ProbeCalls, "a provider that cannot service the game must not be probed");
        }

        [TestMethod]
        public async Task ResolveInGameProviderWithProbeAsync_NullGame_ReturnsNullWithoutProbing()
        {
            var epic = new FakeProvider("Epic", isCapable: _ => true, authenticated: false);
            epic.Session.OnProbe = AuthProbeResult.NotAuthenticated;
            var runtime = CreateRuntime(epic);

            Assert.IsNull(await runtime.ResolveInGameProviderWithProbeAsync(null).ConfigureAwait(false));
            Assert.AreEqual(0, epic.Session.ProbeCalls);
        }

        private static RefreshRuntime CreateRuntime(params IDataProvider[] providers)
        {
            var settings = new PlayniteAchievementsSettings();
            return new RefreshRuntime(
                new RefreshRuntime.TestRuntimeCache(),
                settings,
                api: new FakePlayniteApi(),
                providers: providers);
        }

        private sealed class FakeSession : ISessionManager
        {
            public FakeSession(string providerKey)
            {
                ProviderKey = providerKey;
            }

            public string ProviderKey { get; }
            public int ProbeCalls { get; private set; }
            public Func<AuthProbeResult> OnProbe { get; set; } = AuthProbeResult.NotAuthenticated;

            public Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
            {
                ProbeCalls++;
                return Task.FromResult(OnProbe());
            }

            public Task<AuthProbeResult> AuthenticateInteractiveAsync(
                bool forceInteractive,
                CancellationToken ct,
                IProgress<AuthProgressStep> progress = null)
            {
                throw new InvalidOperationException("The in-game probe must never open interactive auth.");
            }

            public void ClearSession()
            {
            }
        }

        private sealed class FakeProvider : IDataProvider
        {
            private readonly Func<Game, bool> _isCapable;

            public FakeProvider(string providerKey, Func<Game, bool> isCapable, bool authenticated)
            {
                ProviderKey = providerKey;
                _isCapable = isCapable;
                Authenticated = authenticated;
                Session = new FakeSession(providerKey);
            }

            public FakeSession Session { get; }
            public bool Authenticated { get; set; }

            public string ProviderName => ProviderKey;
            public string ProviderKey { get; }
            public string ProviderIconKey => ProviderKey;
            public string ProviderColorHex => "#000000";
            public bool IsAuthenticated => Authenticated;
            public ISessionManager AuthSession => Session;
            public IFriendsProvider Friends => null;
            public bool IsCapable(Game game) => _isCapable(game);

            public Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Game> gamesToRefresh,
                Action<Game> onGameStarting,
                Func<Game, GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
            {
                return Task.FromResult(new RebuildPayload { Summary = new RebuildSummary() });
            }

            public IProviderSettings GetSettings() => null;
            public void ApplySettings(IProviderSettings settings) { }
            public ProviderSettingsViewBase CreateSettingsView() => null;
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

            public string ExpandGameVariables(Game game, string source) => source;
            public string ExpandGameVariables(Game game, string source, string fallbackValue) => source ?? fallbackValue;
            public GameAction ExpandGameVariables(Game game, GameAction source) => source;
            public void StartGame(Guid id) { }
            public void InstallGame(Guid id) { }
            public void UninstallGame(Guid id) { }
            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args) { }
            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args) { }
            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args) { }
        }
    }
}
