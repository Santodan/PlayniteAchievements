using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Hoyoverse;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.Providers.Hoyoverse
{
    [TestClass]
    public class HoyoverseScannerTests
    {
        [TestMethod]
        public void TryResolveByName_MatchesExactAliasesOnly()
        {
            Assert.IsTrue(HoyoverseGameCatalog.TryResolveByName("Genshin Impact", out var genshin));
            Assert.AreEqual(HoyoverseGameKind.GenshinImpact, genshin);
            Assert.IsTrue(HoyoverseGameCatalog.TryResolveByName("Honkai: Star Rail", out _));
            Assert.IsTrue(HoyoverseGameCatalog.TryResolveByName("Honkai Star Rail", out _));
            Assert.IsTrue(HoyoverseGameCatalog.TryResolveByName("ZZZ", out _));
            Assert.IsFalse(HoyoverseGameCatalog.TryResolveByName("Genshin Impact Special Edition", out _));
        }

        [TestMethod]
        public async Task RefreshAsync_NoExportPath_CreatesLockedAchievementData()
        {
            var game = new Game { Id = Guid.NewGuid(), Name = "Genshin Impact" };
            var providerSettings = new HoyoverseSettings();
            var scanner = CreateScanner(providerSettings);

            GameAchievementData completed = null;
            var payload = await scanner.RefreshAsync(
                new[] { game },
                null,
                (g, data) =>
                {
                    completed = data;
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.AreEqual(1, payload.Summary.GamesRefreshed);
            Assert.IsNotNull(completed);
            Assert.AreEqual("Hoyoverse", completed.ProviderKey);
            Assert.AreEqual(2, completed.Achievements.Count);
            Assert.IsTrue(completed.Achievements.All(a => !a.Unlocked));
        }

        [TestMethod]
        public async Task RefreshAsync_ConfiguredExportPath_AppliesUnlockedIds()
        {
            var path = WriteTempFile(".json", @"{""achievements"":{""1001"":{""done"":true}}}");
            var game = new Game { Id = Guid.NewGuid(), Name = "Genshin Impact" };
            var providerSettings = new HoyoverseSettings { GenshinExportPath = path };
            var scanner = CreateScanner(providerSettings);

            GameAchievementData completed = null;
            await scanner.RefreshAsync(
                new[] { game },
                null,
                (g, data) =>
                {
                    completed = data;
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.IsTrue(completed.Achievements.Single(a => a.ApiName == "1001").Unlocked);
            Assert.IsFalse(completed.Achievements.Single(a => a.ApiName == "1002").Unlocked);
            Assert.IsNull(completed.Achievements.Single(a => a.ApiName == "1001").UnlockTimeUtc);
        }

        [TestMethod]
        public async Task RefreshAsync_InvalidExportPath_DoesNotFailRefresh()
        {
            var game = new Game { Id = Guid.NewGuid(), Name = "Genshin Impact" };
            var providerSettings = new HoyoverseSettings
            {
                GenshinExportPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json")
            };
            var scanner = CreateScanner(providerSettings);

            GameAchievementData completed = null;
            var payload = await scanner.RefreshAsync(
                new[] { game },
                null,
                (g, data) =>
                {
                    completed = data;
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.AreEqual(1, payload.Summary.GamesRefreshed);
            Assert.IsNotNull(completed);
            Assert.IsTrue(completed.Achievements.All(a => !a.Unlocked));
        }

        private static HoyoverseScanner CreateScanner(HoyoverseSettings providerSettings)
        {
            return new HoyoverseScanner(
                null,
                new PlayniteAchievementsSettings(),
                providerSettings,
                string.Empty,
                new FakeDefinitionClient());
        }

        private static string WriteTempFile(string extension, string content)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayAch_HoyoverseTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "export" + extension);
            File.WriteAllText(path, content);
            return path;
        }

        private sealed class FakeDefinitionClient : IHoyoverseDefinitionClient
        {
            public Task<IReadOnlyList<AchievementDetail>> GetDefinitionsAsync(
                HoyoverseGameKind kind,
                string globalLanguage,
                CancellationToken cancel)
            {
                IReadOnlyList<AchievementDetail> definitions = new List<AchievementDetail>
                {
                    new AchievementDetail
                    {
                        ApiName = "1001",
                        DisplayName = "First",
                        Description = "First description",
                        Points = 5,
                        Rarity = RarityTier.Common
                    },
                    new AchievementDetail
                    {
                        ApiName = "1002",
                        DisplayName = "Second",
                        Description = "Second description",
                        Points = 20,
                        Rarity = RarityTier.Rare
                    }
                };

                return Task.FromResult(definitions);
            }
        }
    }
}
