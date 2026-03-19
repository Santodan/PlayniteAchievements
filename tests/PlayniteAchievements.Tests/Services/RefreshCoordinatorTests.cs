using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class RefreshCoordinatorTests
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
        public async Task ExecuteAsync_CustomMode_PreservesCustomOptions()
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
                CustomOptions = customOptions
            }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ExecuteCallCount);
            Assert.AreEqual(RefreshModeType.Custom, manager.LastRequest.Mode);
            Assert.IsNotNull(manager.LastRequest.CustomOptions);
            CollectionAssert.AreEquivalent(
                new[] { "Steam", "Epic" },
                manager.LastRequest.CustomOptions.ProviderKeys.ToList());
            CollectionAssert.AreEquivalent(
                new[] { gameId },
                manager.LastRequest.CustomOptions.IncludeGameIds.ToList());
            Assert.AreEqual(CustomGameScope.Explicit, manager.LastRequest.CustomOptions.Scope);
            Assert.IsFalse(manager.LastRequest.CustomOptions.RespectUserExclusions);
            Assert.AreEqual(false, manager.LastRequest.CustomOptions.RunProvidersInParallelOverride);
        }

        [TestMethod]
        public async Task ExecuteAsync_StopsWhenValidationFails()
        {
            var manager = new FakeAchievementService { ValidateResult = false };
            var coordinator = CreateCoordinator(manager);

            await coordinator.ExecuteAsync(
                new RefreshRequest { Mode = RefreshModeType.Full },
                new RefreshExecutionPolicy { ValidateAuthentication = true }).ConfigureAwait(false);

            Assert.AreEqual(1, manager.ValidateCallCount);
            Assert.AreEqual(0, manager.ExecuteCallCount);
        }

        [TestMethod]
        public async Task ExecuteAsync_ProgressWindowDefersExecutionToCallback()
        {
            var manager = new FakeAchievementService();
            Func<Task> capturedRefreshTask = null;
            Guid? capturedSingleGameId = null;
            var callbackCount = 0;
            var singleGameId = Guid.NewGuid();
            var coordinator = new RefreshCoordinator(
                manager,
                logger: null,
                providerRegistry: new ProviderRegistry(),
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

        private static RefreshCoordinator CreateCoordinator(AchievementService manager)
        {
            return new RefreshCoordinator(manager, logger: null, providerRegistry: new ProviderRegistry());
        }

        private sealed class FakeAchievementService : AchievementService
        {
            public bool ValidateResult { get; set; } = true;

            public int ValidateCallCount { get; private set; }
            public int ExecuteCallCount { get; private set; }
            public RefreshRequest LastRequest { get; private set; }

            public override bool ValidateCanStartRefresh()
            {
                ValidateCallCount++;
                return ValidateResult;
            }

            public override Task ExecuteRefreshAsync(RefreshRequest request)
            {
                ExecuteCallCount++;
                LastRequest = request == null
                    ? null
                    : new RefreshRequest
                    {
                        Mode = request.Mode,
                        ModeKey = request.ModeKey,
                        SingleGameId = request.SingleGameId,
                        GameIds = request.GameIds?.ToList(),
                        CustomOptions = request.CustomOptions?.Clone()
                    };
                return Task.CompletedTask;
            }
        }
    }
}


