using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class RefreshEntryPointTests
    {
        [TestMethod]
        public async Task ExecuteAsync_UsesExplicitGameIdsBeforeModes()
        {
            var manager = new FakeAchievementService();
            var coordinator = CreateCoordinator(manager);
            var gameA = Guid.NewGuid();
            var gameB = Guid.NewGuid();

            await coordinator.ExecuteAsync(new RefreshRequest
            {
                GameIds = new[] { gameA, gameB, gameA },
                Mode = RefreshModeType.Full,
                ModeKey = RefreshModeType.Recent.ToString()
            }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            CollectionAssert.AreEquivalent(new[] { gameA, gameB }, manager.LastRequest.GameIds.ToList());
            Assert.IsFalse(manager.LastRequest.Mode.HasValue);
            Assert.IsTrue(string.IsNullOrWhiteSpace(manager.LastRequest.ModeKey));
        }

        [TestMethod]
        public async Task ExecuteAsync_UsesEnumModeWhenProvided()
        {
            var manager = new FakeAchievementService();
            var coordinator = CreateCoordinator(manager);
            var singleGameId = Guid.NewGuid();

            await coordinator.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Single,
                SingleGameId = singleGameId
            }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.AreEqual(RefreshModeType.Single, manager.LastRequest.Mode);
            Assert.AreEqual(singleGameId, manager.LastRequest.SingleGameId);
            Assert.IsTrue(string.IsNullOrWhiteSpace(manager.LastRequest.ModeKey));
            Assert.IsNull(manager.LastRequest.GameIds);
        }

        [TestMethod]
        public async Task ExecuteAsync_UsesModeKeyWhenEnumMissing()
        {
            var manager = new FakeAchievementService();
            var coordinator = CreateCoordinator(manager);

            await coordinator.ExecuteAsync(new RefreshRequest
            {
                ModeKey = RefreshModeType.Missing.ToString()
            }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.AreEqual(RefreshModeType.Missing.ToString(), manager.LastRequest.ModeKey);
            Assert.IsFalse(manager.LastRequest.Mode.HasValue);
        }

        [TestMethod]
        public async Task ExecuteAsync_DefaultsToRecentWhenRequestHasNoTargets()
        {
            var manager = new FakeAchievementService();
            var coordinator = CreateCoordinator(manager);

            await coordinator.ExecuteAsync(new RefreshRequest()).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.AreEqual(RefreshModeType.Recent, manager.LastRequest.Mode);
        }

        [TestMethod]
        public async Task ExecuteAsync_CustomMode_PreservesUnifiedOptions()
        {
            var manager = new FakeAchievementService();
            var coordinator = CreateCoordinator(manager);
            var gameId = Guid.NewGuid();

            var customOptions = new CustomRefreshOptions
            {
                ProviderKeys = new[] { "Steam", "Epic" },
                Scope = CustomGameScope.Explicit,
                IncludeGameIds = new[] { gameId },
                RespectUserExclusions = false,
                RunProvidersInParallelOverride = false
            };

            await coordinator.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Custom,
                Options = RefreshOptions.FromCustom(customOptions)
            }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.AreEqual(RefreshModeType.Custom, manager.LastRequest.Mode);
            Assert.IsNotNull(manager.LastRequest.Options);
            CollectionAssert.AreEquivalent(
                new[] { "Steam", "Epic" },
                manager.LastRequest.Options.ProviderKeys.ToList());
            CollectionAssert.AreEquivalent(
                new[] { gameId },
                manager.LastRequest.Options.PlayniteGameIds.ToList());
            Assert.AreEqual(RefreshGameScope.Explicit, manager.LastRequest.Options.Scope);
            Assert.IsFalse(manager.LastRequest.Options.RespectUserExclusions);
            Assert.AreEqual(false, manager.LastRequest.Options.RunProvidersInParallelOverride);
        }

        [TestMethod]
        public async Task ExecuteAsync_UnifiedOptions_PreservesCurrentAndFriendOptions()
        {
            var manager = new FakeAchievementService();
            var coordinator = CreateCoordinator(manager);
            var currentGameId = Guid.NewGuid();
            var friendGameId = Guid.NewGuid();

            await coordinator.ExecuteAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Custom,
                Options = new RefreshOptions
                {
                    Subjects = RefreshSubjects.All,
                    ProviderKeys = new[] { "Steam" },
                    Scope = RefreshGameScope.SelectedGame,
                    PlayniteGameIds = new[] { currentGameId, friendGameId },
                    ForceIconRefresh = true
                }
            }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.AreEqual(RefreshModeType.Custom, manager.LastRequest.Mode);
            Assert.IsNotNull(manager.LastRequest.Options);
            Assert.IsTrue(manager.LastRequest.Options.ForceIconRefresh);
            Assert.AreEqual(RefreshSubjects.All, manager.LastRequest.Options.Subjects);
            CollectionAssert.AreEquivalent(
                new[] { currentGameId, friendGameId },
                manager.LastRequest.Options.PlayniteGameIds.ToList());
        }

        [TestMethod]
        public async Task ExecuteAsync_StopsWhenValidationFails()
        {
            var manager = new FakeAchievementService
            {
                AuthenticatedProvidersToReturn = new List<IDataProvider>()
            };
            var coordinator = CreateCoordinator(manager);

            await coordinator.ExecuteAsync(
                new RefreshRequest { Mode = RefreshModeType.Full },
                new RefreshExecutionPolicy { ValidateAuthentication = true }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ValidateCallCount);
            Assert.AreEqual(0, manager.ExecuteCallCount);
        }

        [TestMethod]
        public async Task ExecuteAsync_PassesValidatedAuthContextToRuntime()
        {
            var provider = new StubDataProvider("Steam");
            var manager = new FakeAchievementService
            {
                AuthenticatedProvidersToReturn = new List<IDataProvider> { provider }
            };
            var coordinator = CreateCoordinator(manager);

            await coordinator.ExecuteAsync(
                new RefreshRequest { Mode = RefreshModeType.Recent },
                new RefreshExecutionPolicy { ValidateAuthentication = true }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ValidateCallCount);
            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.IsNotNull(manager.LastAuthenticatedProviders);
            Assert.AreEqual(1, manager.LastAuthenticatedProviders.Count);
            Assert.AreSame(provider, manager.LastAuthenticatedProviders[0]);
        }

        [TestMethod]
        public async Task ExecuteAsync_ProgressWindowDefersExecutionToCallback()
        {
            var manager = new FakeAchievementService();
            Func<Task> capturedRefreshTask = null;
            Guid? capturedSingleGameId = null;
            var callbackCount = 0;
            var singleGameId = Guid.NewGuid();
            var coordinator = new RefreshEntryPoint(
                manager,
                logger: null,
                runWithProgressWindow: (refreshTask, gameId) =>
                {
                    callbackCount++;
                    capturedRefreshTask = refreshTask;
                    capturedSingleGameId = gameId;
                });

            await coordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = singleGameId
                },
                RefreshExecutionPolicy.ProgressWindow(singleGameId)).ConfigureAwait(false);

            Assert.AreEqual(1, callbackCount);
            Assert.AreEqual(singleGameId, capturedSingleGameId);
            Assert.AreEqual(0, manager.ExecuteCallCount);

            await capturedRefreshTask().ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.AreEqual(RefreshModeType.Single, manager.LastRequest.Mode);
            Assert.AreEqual(singleGameId, manager.LastRequest.SingleGameId);
        }

        private static RefreshEntryPoint CreateCoordinator(RefreshRuntime manager)
        {
            return new RefreshEntryPoint(manager, logger: null);
        }

        private sealed class FakeAchievementService : RefreshRuntime
        {
            public IReadOnlyList<IDataProvider> AuthenticatedProvidersToReturn { get; set; } =
                new List<IDataProvider> { new StubDataProvider("Steam") };

            public int ValidateCallCount { get; private set; }
            public int ExecuteCallCount { get; private set; }
            public RefreshRequest LastRequest { get; private set; }
            public IReadOnlyList<IDataProvider> LastAuthenticatedProviders { get; private set; }

            internal override Task<RefreshAuthContext> GetRefreshAuthContextOrShowDialogAsync(
                RefreshRequest request,
                CancellationToken externalToken = default)
            {
                ValidateCallCount++;
                return Task.FromResult(RefreshAuthContext.FromAuthenticatedProviders(
                    AuthenticatedProvidersToReturn ?? (IReadOnlyList<IDataProvider>)Array.Empty<IDataProvider>()));
            }

            internal override Task ExecuteRefreshAsync(
                RefreshRequest request,
                RefreshAuthContext authContext,
                CancellationToken externalToken = default)
            {
                ExecuteCallCount++;
                LastAuthenticatedProviders = authContext?.AuthenticatedProviders;
                LastRequest = CloneRequest(request);
                return Task.CompletedTask;
            }

            public override Task ExecuteRefreshAsync(RefreshRequest request, CancellationToken externalToken = default)
            {
                ExecuteCallCount++;
                LastRequest = CloneRequest(request);
                return Task.CompletedTask;
            }

            private static RefreshRequest CloneRequest(RefreshRequest request)
            {
                return request == null
                    ? null
                    : new RefreshRequest
                    {
                        Mode = request.Mode,
                        ModeKey = request.ModeKey,
                        SingleGameId = request.SingleGameId,
                        GameIds = request.GameIds?.ToList(),
                        Options = request.Options?.Clone()
                    };
            }
        }

        private sealed class StubDataProvider : IDataProvider
        {
            public StubDataProvider(string providerKey)
            {
                ProviderKey = providerKey;
            }

            public string ProviderName => ProviderKey;

            public string ProviderKey { get; }

            public string ProviderIconKey => ProviderKey;

            public string ProviderColorHex => "#000000";

            public bool IsCapable(Playnite.SDK.Models.Game game) => true;

            public bool IsAuthenticated => true;

            public ISessionManager AuthSession => null;
            public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

            public Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Playnite.SDK.Models.Game> gamesToRefresh,
                Action<Playnite.SDK.Models.Game> onGameStarting,
                Func<Playnite.SDK.Models.Game, PlayniteAchievements.Models.Achievements.GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
            {
                return Task.FromResult(new RebuildPayload());
            }

            public PlayniteAchievements.Providers.Settings.IProviderSettings GetSettings() => null;

            public void ApplySettings(PlayniteAchievements.Providers.Settings.IProviderSettings settings)
            {
            }

            public PlayniteAchievements.Providers.Settings.ProviderSettingsViewBase CreateSettingsView() => null;
        }
    }
}


