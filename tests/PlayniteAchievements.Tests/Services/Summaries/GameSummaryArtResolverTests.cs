using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Summaries;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Tests.Services.Summaries
{
    [TestClass]
    [DoNotParallelize]
    public class GameSummaryArtResolverTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            CategoryDefaultImageResolver.DiskImageServiceAccessor = null;
            GameSummaryArtResolver.ManagedCustomIconServiceAccessor = null;
        }

        [TestMethod]
        public void Resolve_NullSelection_ReturnsNull()
        {
            Assert.IsNull(GameSummaryArtResolver.Resolve(
                Guid.NewGuid(),
                null,
                new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public void Resolve_OverrideArt_BeatsProviderDefault()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                CategoryDefaultImageResolver.DiskImageServiceAccessor = () => diskImageService;
                WritePlaceholderFile(diskImageService.GetDefaultCategoryImagePath(gameId.ToString("D"), "Bonus"));

                var resolved = GameSummaryArtResolver.Resolve(
                    gameId,
                    new GameSummaryCategoryData { Label = "Bonus", ProviderLabel = "Bonus" },
                    new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bonus"] = new CategoryImageOverrideData { Art = "https://example.com/art.png" }
                    });

                Assert.AreEqual("https://example.com/art.png", resolved);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Resolve_OverrideArt_LocalFile_AppliesCacheBust()
        {
            var tempDir = CreateTempDirectory();
            var artPath = Path.Combine(tempDir, "art.png");

            try
            {
                WritePlaceholderFile(artPath);

                var resolved = GameSummaryArtResolver.Resolve(
                    Guid.NewGuid(),
                    new GameSummaryCategoryData { Label = "Bonus", ProviderLabel = "Bonus" },
                    new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bonus"] = new CategoryImageOverrideData { Art = artPath }
                    });

                StringAssert.StartsWith(resolved, "cachebust|");
                StringAssert.EndsWith(resolved, artPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Resolve_NoOverride_UsesProviderLabelKeyedDefault()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                CategoryDefaultImageResolver.DiskImageServiceAccessor = () => diskImageService;
                var defaultPath = diskImageService.GetDefaultCategoryImagePath(gameId.ToString("D"), "Phantom Liberty");
                WritePlaceholderFile(defaultPath);
                diskImageService.InvalidateDefaultCategoryArtSnapshot(gameId.ToString("D"));

                // Overrides are keyed by the renamed display label; provider defaults
                // stay keyed by the provider label, which the selection carries.
                var resolved = GameSummaryArtResolver.Resolve(
                    gameId,
                    new GameSummaryCategoryData { Label = "My Renamed DLC", ProviderLabel = "Phantom Liberty" },
                    new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase));

                Assert.AreEqual(defaultPath, resolved);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Resolve_NoOverrideNoDefault_ReturnsNull()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                CategoryDefaultImageResolver.DiskImageServiceAccessor = () => diskImageService;

                Assert.IsNull(GameSummaryArtResolver.Resolve(
                    Guid.NewGuid(),
                    new GameSummaryCategoryData { Label = "Bonus", ProviderLabel = "Bonus" },
                    imageOverrides: null));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveForGame_ReadsSelectionAndOverridesFromStore()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Update(gameId, customData =>
                {
                    customData.GameSummaryCategory = new GameSummaryCategoryData
                    {
                        Label = "Bonus",
                        ProviderLabel = "Bonus"
                    };
                    customData.AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bonus"] = new CategoryImageOverrideData { Art = "https://example.com/art.png" }
                    };
                });

                Assert.AreEqual(
                    "https://example.com/art.png",
                    GameSummaryArtResolver.ResolveForGame(gameId, store));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveForGame_NoSelection_ReturnsNull()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Update(gameId, customData =>
                {
                    customData.AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bonus"] = new CategoryImageOverrideData { Art = "https://example.com/art.png" }
                    };
                });

                Assert.IsNull(GameSummaryArtResolver.ResolveForGame(gameId, store));
                Assert.IsNull(GameSummaryArtResolver.ResolveForGame(null, store));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void WritePlaceholderFile(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        }
    }
}
