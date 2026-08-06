using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Manual.Tests
{
    [TestClass]
    public class ManualDisplayPlatformResolverTests
    {
        // Each test establishes its own registry so the selectable-key set is deterministic
        // regardless of which other test class last constructed one (the ctor sets the singleton).
        [TestInitialize]
        public void SetUpRegistry()
        {
            var registry = new ProviderRegistry(new PlayniteAchievementsSettings(), new[] { "Steam", "PSN" });
            RegisterProvider(registry, new FakePlatformProvider("Steam"));
            RegisterProvider(registry, new FakePlatformProvider("PSN"));
        }

        [TestMethod]
        public void Override_TakesPrecedenceOverSourceDerivedPlatform()
        {
            var link = CreateLink("shogun-showdown-steam", displayPlatformOverride: "PSN");

            var resolved = ManualDisplayPlatformResolver.Resolve(new FakeManualSource("Steam"), link);

            Assert.AreEqual("PSN", resolved, "A user override must win over the source-derived platform.");
        }

        [TestMethod]
        public void Override_IsCaseInsensitiveAndPreservesStoredCasing()
        {
            var link = CreateLink("shogun-showdown", displayPlatformOverride: "psn");

            var resolved = ManualDisplayPlatformResolver.Resolve(new FakeManualSource(null), link);

            Assert.AreEqual("psn", resolved, "Validation is case-insensitive; the stored value is returned as-is.");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void BlankOverride_FallsBackToSourceDerivedPlatform(string blankOverride)
        {
            var link = CreateLink("shogun-showdown-steam", blankOverride);

            var resolved = ManualDisplayPlatformResolver.Resolve(new FakeManualSource("Steam"), link);

            Assert.AreEqual("Steam", resolved);
        }

        [TestMethod]
        public void UnregisteredOverride_FallsBackToSourceDerivedPlatform()
        {
            // A key from a provider that no longer exists must not reach the UI, where it would
            // resolve to neither a localized name nor an icon.
            var link = CreateLink("shogun-showdown-steam", displayPlatformOverride: "RemovedProvider");

            var resolved = ManualDisplayPlatformResolver.Resolve(new FakeManualSource("Steam"), link);

            Assert.AreEqual("Steam", resolved);
        }

        [TestMethod]
        public void UnresolvableSourceWithNoOverride_ResolvesNull()
        {
            var link = CreateLink("shogun-showdown", displayPlatformOverride: null);

            var resolved = ManualDisplayPlatformResolver.Resolve(new FakeManualSource(null), link);

            Assert.IsNull(resolved, "Unresolvable and un-overridden links keep displaying as Manual.");
        }

        [TestMethod]
        public void NullLink_ResolvesNull()
        {
            Assert.IsNull(ManualDisplayPlatformResolver.Resolve(new FakeManualSource("Steam"), null));
        }

        [TestMethod]
        public void SelectablePlatformKeys_ComeFromTheRegistry()
        {
            CollectionAssert.AreEquivalent(
                new[] { "Steam", "PSN" },
                (System.Collections.ICollection)ManualDisplayPlatformResolver.GetSelectablePlatformKeys());

            Assert.IsTrue(ManualDisplayPlatformResolver.IsSelectablePlatformKey("psn"));
            Assert.IsFalse(ManualDisplayPlatformResolver.IsSelectablePlatformKey("RemovedProvider"));
            Assert.IsFalse(ManualDisplayPlatformResolver.IsSelectablePlatformKey(null));
        }

        [TestMethod]
        public void Clone_RoundTripsTheOverride()
        {
            var link = CreateLink("shogun-showdown", displayPlatformOverride: "PSN");

            Assert.AreEqual("PSN", link.Clone().DisplayPlatformKeyOverride);
        }

        private static ManualAchievementLink CreateLink(string sourceGameId, string displayPlatformOverride)
            => new ManualAchievementLink
            {
                SourceKey = "Exophase",
                SourceGameId = sourceGameId,
                DisplayPlatformKeyOverride = displayPlatformOverride
            };

        private static void RegisterProvider(ProviderRegistry registry, IDataProvider provider)
        {
            var registerMethod = typeof(ProviderRegistry).GetMethod(
                "RegisterProviderInternals",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(registerMethod, "ProviderRegistry.RegisterProviderInternals was not found.");
            registerMethod.Invoke(registry, new object[] { provider });
        }

        /// <summary>
        /// Manual source that reports a fixed platform, standing in for Steam (always resolves)
        /// or Exophase with a slug carrying no platform token (resolves null).
        /// </summary>
        private sealed class FakeManualSource : IManualSource
        {
            private readonly string _platformKey;

            public FakeManualSource(string platformKey)
            {
                _platformKey = platformKey;
            }

            public string SourceKey => "Exophase";

            public string SourceName => "Exophase";

            public bool IsAuthenticated => true;

            public ISessionManager AuthSession => null;

            public Task<List<ManualGameSearchResult>> SearchGamesAsync(
                string query, string language, CancellationToken ct)
                => Task.FromResult(new List<ManualGameSearchResult>());

            public Task<List<AchievementDetail>> GetAchievementsAsync(
                string sourceGameId, string language, CancellationToken ct)
                => Task.FromResult(new List<AchievementDetail>());

            public string ResolveProviderPlatformKey(string sourceGameId) => _platformKey;
        }

        private sealed class FakePlatformProvider : IDataProvider
        {
            public FakePlatformProvider(string providerKey)
            {
                ProviderKey = providerKey;
            }

            public string ProviderName => ProviderKey;

            public string ProviderKey { get; }

            public string ProviderIconKey => $"ProviderIcon{ProviderKey}";

            public string ProviderColorHex => "#92C83E";

            public bool IsAuthenticated => true;

            public ISessionManager AuthSession => null;

            public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

            public bool IsCapable(Game game) => true;

            public Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Game> gamesToRefresh,
                Action<Game> onGameStarting,
                Func<Game, GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
                => Task.FromResult(new RebuildPayload());

            public IProviderSettings GetSettings() => null;

            public void ApplySettings(IProviderSettings settings)
            {
            }

            public ProviderSettingsViewBase CreateSettingsView() => null;
        }
    }
}
