using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public sealed class AchievementPageLinkResolverTests
    {
        [TestMethod]
        public async Task ResolveUrlAsync_UsesCachedProviderKey_NotEffectiveProviderKey()
        {
            var resolver = new AchievementPageLinkResolver(new IDataProvider[]
            {
                new LinkProvider("Steam", "https://steam.example"),
                new LinkProvider("Exophase", "https://exophase.example")
            });
            var context = new AchievementPageLinkContext(
                new Game(),
                new GameAchievementData { ProviderKey = "Exophase", ProviderPlatformKey = "Steam" },
                null,
                null);

            var url = await resolver.ResolveUrlAsync(context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("https://exophase.example", url);
        }

        [TestMethod]
        public async Task ResolveUrlAsync_ManualCachedProviderDelegatesToManualSourceKey()
        {
            var resolver = new AchievementPageLinkResolver(new IDataProvider[]
            {
                new LinkProvider("Steam", "https://steam.example"),
                new LinkProvider("Manual", "https://manual.example")
            });
            var context = new AchievementPageLinkContext(
                new Game(),
                new GameAchievementData { ProviderKey = "Manual" },
                null,
                new ManualAchievementLink { SourceKey = "Steam", SourceGameId = "123" });

            var url = await resolver.ResolveUrlAsync(context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("https://steam.example", url);
        }

        [TestMethod]
        public async Task ResolveUrlAsync_NoCachedDataUsesFirstCapableLinkProvider()
        {
            var resolver = new AchievementPageLinkResolver(new IDataProvider[]
            {
                new LinkProvider("Steam", "https://steam.example") { Capable = false },
                new LinkProvider("GOG", "https://gog.example")
            });
            var context = new AchievementPageLinkContext(new Game(), null, null, null);

            var url = await resolver.ResolveUrlAsync(context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("https://gog.example", url);
        }

        [TestMethod]
        public async Task ResolveUrlAsync_NoCachedDataUsesManualSourceKeyWhenPresent()
        {
            var resolver = new AchievementPageLinkResolver(new IDataProvider[]
            {
                new LinkProvider("Steam", "https://steam.example"),
                new LinkProvider("GOG", "https://gog.example")
            });
            var context = new AchievementPageLinkContext(
                new Game(),
                null,
                null,
                new ManualAchievementLink { SourceKey = "GOG", SourceGameId = "game-slug" });

            var url = await resolver.ResolveUrlAsync(context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("https://gog.example", url);
        }

        private sealed class LinkProvider : IDataProvider, IAchievementPageLinkProvider
        {
            private readonly string _url;

            public LinkProvider(string providerKey, string url)
            {
                ProviderKey = providerKey;
                _url = url;
            }

            public bool Capable { get; set; } = true;

            public string ProviderName => ProviderKey;

            public string ProviderKey { get; }

            public string ProviderIconKey => null;

            public string ProviderColorHex => null;

            public bool IsAuthenticated => true;

            public ISessionManager AuthSession => null;
            public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

            public bool IsCapable(Game game) => Capable;

            public bool CanResolveAchievementPageUrl(AchievementPageLinkContext context) => true;

            public Task<string> GetAchievementPageUrlAsync(
                AchievementPageLinkContext context,
                CancellationToken cancel)
            {
                return Task.FromResult(_url);
            }

            public Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Game> gamesToRefresh,
                Action<Game> onGameStarting,
                Func<Game, GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
            {
                return Task.FromResult<RebuildPayload>(null);
            }

            public IProviderSettings GetSettings() => null;

            public void ApplySettings(IProviderSettings settings)
            {
            }

            public ProviderSettingsViewBase CreateSettingsView() => null;
        }
    }
}
