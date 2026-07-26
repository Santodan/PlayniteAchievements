using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.Services.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class LibraryRuntimeStateBuilderSummaryArtTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            CategoryDefaultImageResolver.DiskImageServiceAccessor = null;
            GameSummaryArtResolver.ManagedCustomIconServiceAccessor = null;
        }

        [TestMethod]
        public void Build_SummaryCategoryWithOverrideArt_UsesOverrideAsCoverImage()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var artPath = Path.Combine(tempDir, "art.png");

            try
            {
                WritePlaceholderFile(artPath);

                var allData = new List<GameAchievementData>
                {
                    new GameAchievementData
                    {
                        PlayniteGameId = gameId,
                        Game = new Game { Id = gameId, Name = "Summary Art Game" },
                        HasAchievements = true,
                        Achievements = new List<AchievementDetail>
                        {
                            new AchievementDetail
                            {
                                ApiName = "Locked",
                                DisplayName = "Locked",
                                Unlocked = false
                            }
                        },
                        GameSummaryCategory = new GameSummaryCategoryData
                        {
                            Label = "Bonus",
                            ProviderLabel = "Bonus"
                        },
                        AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Bonus"] = new CategoryImageOverrideData { Art = artPath }
                        }
                    }
                };

                var state = LibraryRuntimeStateBuilder.Build(
                    allData,
                    api: null,
                    token: default,
                    includeHeavyAchievementLists: false);

                var summary = FindSummary(state.AllGamesWithAchievements, gameId);
                StringAssert.StartsWith(summary.CoverImagePath, "cachebust|");
                StringAssert.EndsWith(summary.CoverImagePath, artPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Build_NoSummaryCategory_FallsBackToGamePresentationPath()
        {
            var gameId = Guid.NewGuid();
            var allData = new List<GameAchievementData>
            {
                new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Game = new Game { Id = gameId, Name = "Fallback Game" },
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        new AchievementDetail
                        {
                            ApiName = "Locked",
                            DisplayName = "Locked",
                            Unlocked = false
                        }
                    },
                    GameSummaryCategory = null,
                    AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bonus"] = new CategoryImageOverrideData { Art = "https://example.com/art.png" }
                    }
                }
            };

            var state = LibraryRuntimeStateBuilder.Build(
                allData,
                api: null,
                token: default,
                includeHeavyAchievementLists: false);

            var summary = FindSummary(state.AllGamesWithAchievements, gameId);
            Assert.AreEqual(string.Empty, summary.CoverImagePath);
        }

        [TestMethod]
        public void BuildFromCachedSummary_StoreSelectionWithOverride_ResolvesOverrideArt()
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

                var state = LibraryRuntimeStateBuilder.BuildFromCachedSummary(
                    CreateSummaryData(gameId),
                    api: null,
                    token: default,
                    customDataStore: store);

                var summary = FindSummary(state.AllGamesWithAchievements, gameId);
                Assert.AreEqual("https://example.com/art.png", summary.CoverImagePath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void BuildFromCachedSummary_NoStoreSelection_FallsBackToPresentationPath()
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

                var state = LibraryRuntimeStateBuilder.BuildFromCachedSummary(
                    CreateSummaryData(gameId),
                    api: null,
                    token: default,
                    customDataStore: store);

                var summary = FindSummary(state.AllGamesWithAchievements, gameId);
                Assert.AreEqual(string.Empty, summary.CoverImagePath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static CachedSummaryData CreateSummaryData(Guid gameId)
        {
            return new CachedSummaryData
            {
                Games = new List<CachedGameSummaryData>
                {
                    new CachedGameSummaryData
                    {
                        PlayniteGameId = gameId,
                        ProviderKey = "Steam",
                        GameName = "Cached Summary Art Game",
                        HasAchievements = true,
                        TotalAchievements = 2,
                        UnlockedAchievements = 1,
                        CommonCount = 1,
                        TotalCommonPossible = 2
                    }
                }
            };
        }

        private static GameAchievementSummary FindSummary(IEnumerable<GameAchievementSummary> items, Guid gameId)
        {
            foreach (var item in items)
            {
                if (item != null && item.GameId == gameId)
                {
                    return item;
                }
            }

            Assert.Fail($"Expected summary for game {gameId}.");
            return null;
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
