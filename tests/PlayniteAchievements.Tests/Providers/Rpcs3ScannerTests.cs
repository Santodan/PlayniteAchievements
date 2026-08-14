using DiscUtils.Iso9660;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.EmuLibrary;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Tests.Providers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class Rpcs3ScannerTests
    {
        [TestMethod]
        public async Task RefreshAsync_NameFallback_ExactTitleMatchWinsBeforeFuzzyMatch()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR33333_00", "God of War III", "Wrong Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "God of War", "Exact Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "God of War",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Exact Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NpwrOverride_BeatsAutomaticNameMatching()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "Override Game", "Override Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "Detected Game", "Detected Trophy");

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "RPCS3",
                        Value = "npwr11111_00"
                    }
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Detected Game",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Override Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_MissingNpwrOverride_DoesNotFallBackToAutomaticNameMatching()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "Detected Game", "Detected Trophy");

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "RPCS3",
                        Value = "NPWR99999_00"
                    }
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Detected Game",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                // Trophy data for the override was not located: no payload is produced,
                // so previously cached achievements are preserved instead of being wiped.
                Assert.IsNull(data);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_UninstalledEmuLibraryGame_ResolvesTrophySourceFromDecodedPath()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var networkRoot = Path.Combine(tempDir, "network", "PS3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR12345_00", "Single Game", "Single Trophy");
                CreateTrpFile(
                    Path.Combine(networkRoot, "Game Dump", "PS3_GAME", "TROPHY", "TROPHY.TRP"),
                    "NPWR12345_00",
                    "Single Game",
                    "Single Trophy");

                var extensionsDataPath = Path.Combine(tempDir, "ExtensionsData");
                var mappingId = Guid.NewGuid();
                EmuLibraryPathResolverTests.WriteConfig(extensionsDataPath, mappingId, networkRoot);

                // Uninstalled EmuLibrary game: no install directory or roms, only the
                // serialized EmuLibrary game id. The name deliberately does not match the
                // trophy title so only the decoded source path can resolve it.
                var game = EmuLibraryPathResolverTests.BuildEmuLibraryGame(new EmuLibraryMultiFileGameInfo
                {
                    MappingId = mappingId,
                    SourceBaseDir = "Game Dump",
                    SourceFilePath = @"Game Dump\PS3_GAME\USRDIR\EBOOT.BIN"
                });
                game.Id = Guid.NewGuid();
                game.Name = "Renamed Library Entry";

                var provider = CreateProvider(rpcs3Root, extensionsDataPath);

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Single Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_UnlocatableGame_ReturnsNoPayloadSoCacheIsPreserved()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                // Trophy data exists for some other game so the scan is not skipped outright.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "Detected Game", "Detected Trophy");

                var provider = CreateProvider(rpcs3Root);

                // Uninstalled game: no install directory, roms, or matching title name.
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Completely Unrelated Title",
                    InstallDirectory = null
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_FolderCollectionRoot_AggregatesSubgameTrophiesByNpwr()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var collectionRoot = Path.Combine(tempDir, "Sly Collection");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01341_00", "Sly Minigames", "Minigame Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01433_00", "Sly 2", "Sly 2 Trophy");

                CreateFolderCollection(
                    collectionRoot,
                    ("PS3_GAME", "NPWR01341_00", "Ignored Param Minigames"),
                    ("PS3_GM01", "NPWR01435_00", "Ignored Param Sly 1"),
                    ("PS3_GM02", "NPWR01433_00", "Ignored Param Sly 2"));

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = collectionRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(3, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01341_00:0", "NPWR01435_00:0", "NPWR01433_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "Sly Minigames", "Sly 1", "Sly 2" },
                    data.Achievements.Select(achievement => achievement.Category).ToArray());
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_FolderCollectionSubgamePath_DiscoversSiblingSubgames()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var collectionRoot = Path.Combine(tempDir, "Sly Collection");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01433_00", "Sly 2", "Sly 2 Trophy");

                CreateFolderCollection(
                    collectionRoot,
                    ("PS3_GAME", "NPWR01435_00", "Sly 1"),
                    ("PS3_GM01", "NPWR01433_00", "Sly 2"));

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = Path.Combine(collectionRoot, "PS3_GM01", "USRDIR")
                };
                Directory.CreateDirectory(game.InstallDirectory);

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(2, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01435_00:0", "NPWR01433_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_EmptyTrophyCache_UsesTrpFallbackForFolderCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var collectionRoot = Path.Combine(tempDir, "Sly Collection");

            try
            {
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });
                CreateFolderCollection(
                    collectionRoot,
                    ("PS3_GAME", "NPWR01435_00", "Sly 1"),
                    ("PS3_GM01", "NPWR01433_00", "Sly 2"));

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = collectionRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(2, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01435_00:0", "NPWR01433_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
                Assert.IsTrue(data.Achievements.All(achievement => !achievement.Unlocked));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_MultiRegionTropdir_PrefersTrophySetInRpcs3Cache()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Demons Souls");

            try
            {
                // Only the EUR trophy set exists in the RPCS3 trophy cache.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR00033_00", "Demon's Souls", "EUR Cache Trophy");

                // Multi-region dump: TROPDIR carries a trophy set per region and the
                // set that is not in the cache enumerates first.
                CreateTrpFile(
                    Path.Combine(gameRoot, "TROPDIR", "NPWR00011_00", "TROPHY.TRP"),
                    "NPWR00011_00",
                    "Demon's Souls",
                    "JAP Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "TROPDIR", "NPWR00033_00", "TROPHY.TRP"),
                    "NPWR00033_00",
                    "Demon's Souls",
                    "EUR Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Demon's Souls",
                    InstallDirectory = gameRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("EUR Cache Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SinglePs3GameTropdirMultiSet_AggregatesAsCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Sly Collection");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01341_00", "Sly Minigames", "Minigame Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01433_00", "Sly 2", "Sly 2 Trophy");

                // Single-executable collection: one PS3_GAME whose TROPDIR carries all
                // trophy sets (no PS3_GMxx sub-game directories).
                Directory.CreateDirectory(gameRoot);
                File.WriteAllText(Path.Combine(gameRoot, "PS3_DISC.SFB"), "SFB");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01341_00", "TROPHY.TRP"),
                    "NPWR01341_00",
                    "Sly Minigames",
                    "Minigame Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01435_00", "TROPHY.TRP"),
                    "NPWR01435_00",
                    "Sly 1",
                    "Sly 1 Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01433_00", "TROPHY.TRP"),
                    "NPWR01433_00",
                    "Sly 2",
                    "Sly 2 Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = gameRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(3, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01341_00:0", "NPWR01435_00:0", "NPWR01433_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "Sly Minigames", "Sly 1", "Sly 2" },
                    data.Achievements.Select(achievement => achievement.Category).ToArray());
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SinglePs3GameTropdirUsrdirCandidate_AggregatesAsCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Sly Collection");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01341_00", "Sly Minigames", "Minigame Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");

                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01341_00", "TROPHY.TRP"),
                    "NPWR01341_00",
                    "Sly Minigames",
                    "Minigame Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01435_00", "TROPHY.TRP"),
                    "NPWR01435_00",
                    "Sly 1",
                    "Sly 1 Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = Path.Combine(gameRoot, "PS3_GAME", "USRDIR")
                };
                Directory.CreateDirectory(game.InstallDirectory);

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(2, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01341_00:0", "NPWR01435_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "Sly Minigames", "Sly 1" },
                    data.Achievements.Select(achievement => achievement.Category).ToArray());
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_TropdirMultiSet_NeverBooted_AggregatesAsCollectionFromTrps()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Jak Trilogy");

            try
            {
                // Empty trophy cache: no sub-game has ever been booted in RPCS3.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01818_00", "TROPHY.TRP"),
                    "NPWR01818_00",
                    "Jak and Daxter: The Precursor Legacy",
                    "Jak 1 Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01819_00", "TROPHY.TRP"),
                    "NPWR01819_00",
                    "Jak II",
                    "Jak 2 Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01820_00", "TROPHY.TRP"),
                    "NPWR01820_00",
                    "Jak 3",
                    "Jak 3 Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Jak and Daxter Trilogy",
                    InstallDirectory = gameRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(3, data.Achievements.Count);
                Assert.AreEqual("NPWR01818_00+NPWR01819_00+NPWR01820_00", data.ProviderGameKey);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01818_00:0", "NPWR01819_00:0", "NPWR01820_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "Jak and Daxter: The Precursor Legacy", "Jak II", "Jak 3" },
                    data.Achievements.Select(achievement => achievement.Category).ToArray());
                Assert.IsTrue(data.Achievements.All(achievement => !achievement.Unlocked));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_TropdirMultiSet_PartiallyBooted_AggregatesAsCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Jak Trilogy");

            try
            {
                // Only the first sub-game has been booted in RPCS3.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01818_00", "Jak and Daxter: The Precursor Legacy", "Jak 1 Cache Trophy");

                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01818_00", "TROPHY.TRP"),
                    "NPWR01818_00",
                    "Jak and Daxter: The Precursor Legacy",
                    "Jak 1 Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01819_00", "TROPHY.TRP"),
                    "NPWR01819_00",
                    "Jak II",
                    "Jak 2 Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPDIR", "NPWR01820_00", "TROPHY.TRP"),
                    "NPWR01820_00",
                    "Jak 3",
                    "Jak 3 Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Jak and Daxter Trilogy",
                    InstallDirectory = gameRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(3, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01818_00:0", "NPWR01819_00:0", "NPWR01820_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());

                // The booted sub-game reads from the RPCS3 trophy folder, the never-booted
                // ones fall back to their on-disk TROPHY.TRP.
                var bootedTrophy = data.Achievements.Single(achievement => achievement.ApiName == "NPWR01818_00:0");
                Assert.AreEqual("Jak 1 Cache Trophy", bootedTrophy.DisplayName);
                CollectionAssert.AreEquivalent(
                    new[] { "Jak 2 Disc Trophy", "Jak 3 Disc Trophy" },
                    data.Achievements
                        .Where(achievement => achievement.ApiName != "NPWR01818_00:0")
                        .Select(achievement => achievement.DisplayName)
                        .ToArray());
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_MultiRegionTropdir_NeverBooted_RemainsUnmatched()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Demons Souls");

            try
            {
                // Empty trophy cache: same-title region variants stay ambiguous because
                // no trophy folder identifies which region the user actually plays.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                CreateTrpFile(
                    Path.Combine(gameRoot, "TROPDIR", "NPWR00011_00", "TROPHY.TRP"),
                    "NPWR00011_00",
                    "Demon's Souls",
                    "JAP Disc Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "TROPDIR", "NPWR00033_00", "TROPHY.TRP"),
                    "NPWR00033_00",
                    "Demon's Souls",
                    "EUR Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Demon's Souls",
                    InstallDirectory = gameRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NpwrOverride_DisablesCollectionExpansion()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var collectionRoot = Path.Combine(tempDir, "Sly Collection");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01433_00", "Sly 2", "Sly 2 Trophy");
                CreateFolderCollection(
                    collectionRoot,
                    ("PS3_GAME", "NPWR01435_00", "Sly 1"),
                    ("PS3_GM01", "NPWR01433_00", "Sly 2"));

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "RPCS3",
                        Value = "NPWR01433_00"
                    }
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = gameId,
                    Name = "The Sly Collection",
                    InstallDirectory = collectionRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("0", data.Achievements[0].ApiName);
                Assert.AreEqual("Sly 2 Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SingleGameKeepsExistingApiNames()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Single Game");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR12345_00", "Single Game", "Single Trophy");
                CreateTrpFile(Path.Combine(gameRoot, "PS3_GAME", "TROPHY", "TROPHY.TRP"), "NPWR12345_00", "Single Game", "Single Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Single Game",
                    InstallDirectory = gameRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("0", data.Achievements[0].ApiName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_ExplicitRomPath_IsPreferredOverSharedInstallDirectory()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var romRoot = Path.Combine(tempDir, "roms", "PS3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR05636_00", "Minecraft", "Minecraft Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly Trophy");

                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "0-Minecraft.iso"), "NPWR05636_00");
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "1-Sly.iso"), "NPWR01435_00");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = romRoot,
                    Roms = new ObservableCollection<GameRom>
                    {
                        new GameRom
                        {
                            Name = "Sly",
                            Path = @"{InstallDir}\1-Sly.iso"
                        }
                    }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("Sly Trophy", data.Achievements[0].DisplayName);
                Assert.IsFalse(data.Achievements.Any(achievement => achievement.DisplayName == "Minecraft Trophy"));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_RebuildsTrophyFolderCacheAtRefreshStart()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var romRoot = Path.Combine(tempDir, "roms", "PS3");
            var emptyInstallDir = Path.Combine(tempDir, "empty-install");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR05636_00", "Minecraft", "Minecraft Trophy");
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "Minecraft.iso"), "NPWR05636_00");
                Directory.CreateDirectory(emptyInstallDir);

                var provider = CreateProvider(rpcs3Root);
                provider.GetOrBuildTrophyFolderCache();

                var slyIso = Path.Combine(romRoot, "Sly.iso");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly Trophy");
                CreateRawIsoWithNpCommIds(slyIso, "NPWR01435_00");

                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = emptyInstallDir,
                    Roms = new ObservableCollection<GameRom>
                    {
                        new GameRom
                        {
                            Name = "Sly",
                            Path = slyIso
                        }
                    }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("Sly Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SharedIsoDirectory_PicksIsoMatchingGameName()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var romRoot = Path.Combine(tempDir, "roms", "PS3");

            try
            {
                // TROPCONF titles deliberately differ from the game name so only the
                // ISO filename gate can resolve the match (not the name fallback).
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "DI Internal Title", "Dante Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "FNC Internal Title", "Fight Night Trophy");

                // Alphabetically first ISO belongs to the wrong game.
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "Dantes Inferno.iso"), "NPWR11111_00");
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "Fight Night Champion.iso"), "NPWR22222_00");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Fight Night Champion",
                    InstallDirectory = romRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("Fight Night Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SharedIsoDirectory_MultipleSingleNpwrIsos_DoesNotMergeAsCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var romRoot = Path.Combine(tempDir, "roms", "PS3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "DI Internal Title", "Dante Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "FNC Internal Title", "Fight Night Trophy");

                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "Dantes Inferno.iso"), "NPWR11111_00");
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "Fight Night Champion.iso"), "NPWR22222_00");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Fight Night Champion",
                    InstallDirectory = romRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("0", data.Achievements[0].ApiName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SharedIsoDirectory_NoFilenameMatch_FallsBackToTropconfTitleMatch()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var romRoot = Path.Combine(tempDir, "roms", "PS3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "Dantes Inferno", "Dante Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "Fight Night Champion", "Fight Night Trophy");

                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "disc1.iso"), "NPWR11111_00");
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "disc2.iso"), "NPWR22222_00");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Fight Night Champion",
                    InstallDirectory = romRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("Fight Night Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SingleIsoInDirectory_StillMatchesWithoutNameGate()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var romRoot = Path.Combine(tempDir, "roms", "PS3");

            try
            {
                // Neither the ISO filename nor the TROPCONF title matches the game name;
                // a lone ISO in the directory must still resolve via its embedded NPWR id.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "Some Title", "Solo Trophy");
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "randomname.iso"), "NPWR11111_00");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Totally Different Game",
                    InstallDirectory = romRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Solo Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_MultiIsoDirectory_CollectionIsoStillExpandsWhenNameMatched()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var romRoot = Path.Combine(tempDir, "roms", "PS3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01433_00", "Sly 2", "Sly 2 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR05636_00", "Minecraft", "Minecraft Trophy");

                CreateRawIsoWithNpCommIds(
                    Path.Combine(romRoot, "The Sly Collection.iso"),
                    "NPWR01435_00",
                    "NPWR01433_00");
                CreateRawIsoWithNpCommIds(Path.Combine(romRoot, "Unrelated Game.iso"), "NPWR05636_00");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = romRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(2, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01435_00:0", "NPWR01433_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NameFallback_AmbiguousPrefixMatches_ReturnsNoMatch()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "God of War III", "GoW3 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "God of War Ascension", "Ascension Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "God of War",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                // Two distinct titles tie at the prefix score; picking either would be a
                // guess, so no payload is produced and cached achievements are preserved.
                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NameFallback_JaroWinklerNearMiss_NoLongerMatches()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "Uncharted 3", "Uncharted 3 Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Uncharted 2",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NameFallback_MultipleExactTitles_IsRejectedAsAmbiguous()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                // Regional duplicates share the same title. Selecting the lowest
                // NPWR is deterministic but still risks selecting the wrong region.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR00033_00", "Demon's Souls", "EUR Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR00011_00", "Demon's Souls", "JAP Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Demon's Souls",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_RomPathPointsAtEbootBin_FindsSiblingTrophyFolder()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var gameRoot = Path.Combine(tempDir, "Installed Game");

            try
            {
                // Neither the game name nor any candidate directory matches the title;
                // only the TROPHY.TRP next to the rom's USRDIR can resolve the match.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR12345_00", "Actual Title", "Cache Trophy");
                CreateTrpFile(
                    Path.Combine(gameRoot, "PS3_GAME", "TROPHY", "TROPHY.TRP"),
                    "NPWR12345_00",
                    "Actual Title",
                    "Disc Trophy");

                var ebootPath = Path.Combine(gameRoot, "PS3_GAME", "USRDIR", "EBOOT.BIN");
                Directory.CreateDirectory(Path.GetDirectoryName(ebootPath));
                File.WriteAllBytes(ebootPath, new byte[] { 0 });

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Library Entry",
                    Roms = new ObservableCollection<GameRom>
                    {
                        new GameRom
                        {
                            Name = "Installed Game",
                            Path = ebootPath
                        }
                    }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Cache Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SingleSource_SetsProviderGameKeyToNpwr()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR12345_00", "Single Game", "Single Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Single Game",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("NPWR12345_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_Collection_SetsProviderGameKeyToSortedJoinedNpwrs()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var collectionRoot = Path.Combine(tempDir, "Sly Collection");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01341_00", "Sly Minigames", "Minigame Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01433_00", "Sly 2", "Sly 2 Trophy");

                CreateFolderCollection(
                    collectionRoot,
                    ("PS3_GAME", "NPWR01341_00", "Sly Minigames"),
                    ("PS3_GM01", "NPWR01435_00", "Sly 1"),
                    ("PS3_GM02", "NPWR01433_00", "Sly 2"));

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Sly Collection",
                    InstallDirectory = collectionRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("NPWR01341_00+NPWR01433_00+NPWR01435_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_ResolvesPkgInstallUnderDevHdd0Game()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                // Game name and TROPCONF title deliberately differ; only the serial in
                // the install path and RPCS3's own dev_hdd0\game install can resolve it.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR00001_00", "Bridge Internal Title", "Bridge Trophy");
                CreateTrpFile(
                    Path.Combine(rpcs3Root, "dev_hdd0", "game", "NPUB30042", "TROPDIR", "NPWR00001_00", "TROPHY.TRP"),
                    "NPWR00001_00",
                    "Bridge Internal Title",
                    "Bridge Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed PKG Game",
                    InstallDirectory = Path.Combine(tempDir, "pkg", "NPUB30042")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Bridge Trophy", data.Achievements[0].DisplayName);
                Assert.AreEqual("NPWR00001_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_PkgMultiSetTropdir_AggregatesAsCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                // PKG multipack under RPCS3's own install root: TROPDIR carries one
                // trophy set per sub-game and only the first was ever booted.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01818_00", "Jak and Daxter: The Precursor Legacy", "Jak 1 Cache Trophy");

                var installedTropdir = Path.Combine(rpcs3Root, "dev_hdd0", "game", "NPUA80643", "TROPDIR");
                CreateTrpFile(
                    Path.Combine(installedTropdir, "NPWR01818_00", "TROPHY.TRP"),
                    "NPWR01818_00",
                    "Jak and Daxter: The Precursor Legacy",
                    "Jak 1 Disc Trophy");
                CreateTrpFile(
                    Path.Combine(installedTropdir, "NPWR01819_00", "TROPHY.TRP"),
                    "NPWR01819_00",
                    "Jak II",
                    "Jak 2 Disc Trophy");
                CreateTrpFile(
                    Path.Combine(installedTropdir, "NPWR01820_00", "TROPHY.TRP"),
                    "NPWR01820_00",
                    "Jak 3",
                    "Jak 3 Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed PKG Trilogy",
                    InstallDirectory = Path.Combine(tempDir, "pkg", "NPUA80643")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(3, data.Achievements.Count);
                Assert.AreEqual("NPWR01818_00+NPWR01819_00+NPWR01820_00", data.ProviderGameKey);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01818_00:0", "NPWR01819_00:0", "NPWR01820_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "Jak and Daxter: The Precursor Legacy", "Jak II", "Jak 3" },
                    data.Achievements.Select(achievement => achievement.Category).ToArray());
                Assert.AreEqual(
                    "Jak 1 Cache Trophy",
                    data.Achievements.Single(achievement => achievement.ApiName == "NPWR01818_00:0").DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_PkgSameTitleRegionSets_PrefersBootedSet()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                // Multi-region PKG layout: two same-title trophy sets, only one booted.
                // The booted region must win and stay a single (unprefixed) set.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR00200_00", "Region Game", "EUR Cache Trophy");

                var installedTropdir = Path.Combine(rpcs3Root, "dev_hdd0", "game", "NPUA80644", "TROPDIR");
                CreateTrpFile(
                    Path.Combine(installedTropdir, "NPWR00100_00", "TROPHY.TRP"),
                    "NPWR00100_00",
                    "Region Game",
                    "JAP Disc Trophy");
                CreateTrpFile(
                    Path.Combine(installedTropdir, "NPWR00200_00", "TROPHY.TRP"),
                    "NPWR00200_00",
                    "Region Game",
                    "EUR Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Region Game",
                    InstallDirectory = Path.Combine(tempDir, "pkg", "NPUA80644")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("0", data.Achievements[0].ApiName);
                Assert.AreEqual("EUR Cache Trophy", data.Achievements[0].DisplayName);
                Assert.AreEqual("NPWR00200_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_ResolvesRenamedIsoViaGamesYml()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var isoRoot = Path.Combine(tempDir, "isos");

            try
            {
                // Renamed ISOs in a shared folder: filenames and TROPCONF titles match
                // nothing, so only the games.yml registration can resolve the game.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "DI Internal Title", "Dante Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "FNC Internal Title", "Fight Night Trophy");
                CreateRawIsoWithNpCommIds(Path.Combine(isoRoot, "disc1.iso"), "NPWR11111_00");
                CreateRawIsoWithNpCommIds(Path.Combine(isoRoot, "disc2.iso"), "NPWR22222_00");
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "games.yml"),
                    $"BLES01039: \"{Path.Combine(isoRoot, "disc2.iso")}\"");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Fight Night Champion Renamed",
                    InstallDirectory = Path.Combine(tempDir, "lib", "BLES01039")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(1, data.Achievements.Count);
                Assert.AreEqual("Fight Night Trophy", data.Achievements[0].DisplayName);
                Assert.AreEqual("NPWR22222_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_DiscoversSerialFromParamSfoTitleId()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var installDir = Path.Combine(tempDir, "installed", "mygame");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR33333_00", "Yml Internal Title", "Yml Trophy");
                CreateRawIsoWithNpCommIds(Path.Combine(tempDir, "isos", "weird.iso"), "NPWR33333_00");
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "games.yml"),
                    $"BCUS98246: \"{Path.Combine(tempDir, "isos", "weird.iso")}\"");

                Directory.CreateDirectory(installDir);
                CreateParamSfo(Path.Combine(installDir, "PARAM.SFO"), "Some Param Title", "BCUS98246");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Disc Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Yml Trophy", data.Achievements[0].DisplayName);
                Assert.AreEqual("NPWR33333_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_ConflictingSerials_ReturnsNoMatch()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var isoRoot = Path.Combine(tempDir, "isos");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR11111_00", "First Internal Title", "First Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR22222_00", "Second Internal Title", "Second Trophy");
                CreateRawIsoWithNpCommIds(Path.Combine(isoRoot, "a.iso"), "NPWR11111_00");
                CreateRawIsoWithNpCommIds(Path.Combine(isoRoot, "b.iso"), "NPWR22222_00");
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "games.yml"),
                    $"BLES01111: \"{Path.Combine(isoRoot, "a.iso")}\"\nBLES02222: \"{Path.Combine(isoRoot, "b.iso")}\"");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Conflicted Game",
                    InstallDirectory = Path.Combine(tempDir, "lib", "BLES01111-BLES02222")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                // Two serials resolving to different trophy sets is ambiguous; no
                // payload is produced so cached achievements are preserved.
                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_PrelaunchTropdirTrp_ReturnsLockedTrophies()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                // The trophy set exists only as a dev_hdd0\game TROPDIR TRP; RPCS3 has
                // not booted the game yet so no trophy cache folder exists.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });
                CreateTrpFile(
                    Path.Combine(rpcs3Root, "dev_hdd0", "game", "NPUB30099", "TROPDIR", "NPWR00055_00", "TROPHY.TRP"),
                    "NPWR00055_00",
                    "Prelaunch Game",
                    "Prelaunch Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Prelaunch Game",
                    InstallDirectory = Path.Combine(tempDir, "pkg", "NPUB30099")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Prelaunch Trophy", data.Achievements[0].DisplayName);
                Assert.IsTrue(data.Achievements.All(achievement => !achievement.Unlocked));
                Assert.AreEqual("NPWR00055_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_InstalledTrophyTrpTakesPrecedence()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var installDir = Path.Combine(tempDir, "games", "BLES03333");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR44444_00", "Installed Internal Title", "Installed Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR55555_00", "Bridge Internal Title", "Bridge Trophy");

                // The game directory carries its own trophy set; the serial's games.yml
                // registration points at a different one and must not win.
                CreateTrpFile(
                    Path.Combine(installDir, "TROPHY", "TROPHY.TRP"),
                    "NPWR44444_00",
                    "Installed Internal Title",
                    "Installed Disc Trophy");
                CreateRawIsoWithNpCommIds(Path.Combine(tempDir, "isos", "other.iso"), "NPWR55555_00");
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "games.yml"),
                    $"BLES03333: \"{Path.Combine(tempDir, "isos", "other.iso")}\"");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Installed Game",
                    InstallDirectory = installDir
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Installed Trophy", data.Achievements[0].DisplayName);
                Assert.AreEqual("NPWR44444_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_MultiNpwrYmlIso_AggregatesAsCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01435_00", "Sly 1", "Sly 1 Trophy");
                CreateRpcs3TrophyData(rpcs3Root, "NPWR01433_00", "Sly 2", "Sly 2 Trophy");
                CreateRawIsoWithNpCommIds(
                    Path.Combine(tempDir, "isos", "collection.iso"),
                    "NPWR01435_00",
                    "NPWR01433_00");
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "games.yml"),
                    $"BLES04444: \"{Path.Combine(tempDir, "isos", "collection.iso")}\"");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "My Renamed Collection",
                    InstallDirectory = Path.Combine(tempDir, "lib", "BLES04444")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(2, data.Achievements.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "NPWR01435_00:0", "NPWR01433_00:0" },
                    data.Achievements.Select(achievement => achievement.ApiName).ToArray());
                Assert.AreEqual("NPWR01433_00+NPWR01435_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_UnresolvableSerialToken_FallsThroughToNameMatch()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                CreateRpcs3TrophyData(rpcs3Root, "NPWR66666_00", "Exact Match Game", "Exact Trophy");

                var provider = CreateProvider(rpcs3Root);

                // SAVE12345 is serial-shaped but has no dev_hdd0\game install and no
                // games.yml entry; it must not interfere with name-based matching.
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Exact Match Game",
                    InstallDirectory = Path.Combine(tempDir, "saves", "SAVE12345")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Exact Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void SerialBridge_TryNormalizeSerial_AcceptsOnlyTitleIdShapes()
        {
            Assert.IsTrue(Rpcs3SerialNpwrBridge.TryNormalizeSerial("bles01039", out var lowercase));
            Assert.AreEqual("BLES01039", lowercase);

            Assert.IsTrue(Rpcs3SerialNpwrBridge.TryNormalizeSerial(" NPUB30042 ", out var padded));
            Assert.AreEqual("NPUB30042", padded);

            Assert.IsFalse(Rpcs3SerialNpwrBridge.TryNormalizeSerial("AB12345", out _));
            Assert.IsFalse(Rpcs3SerialNpwrBridge.TryNormalizeSerial("BLES0103", out _));
            Assert.IsFalse(Rpcs3SerialNpwrBridge.TryNormalizeSerial("BLES010399", out _));
            Assert.IsFalse(Rpcs3SerialNpwrBridge.TryNormalizeSerial("NPWR00476_00", out _));
            Assert.IsFalse(Rpcs3SerialNpwrBridge.TryNormalizeSerial(null, out _));
            Assert.IsFalse(Rpcs3SerialNpwrBridge.TryNormalizeSerial("   ", out _));
        }

        [TestMethod]
        public void SerialBridge_ExtractSerials_FindsNormalizedTokensInPaths()
        {
            var serials = Rpcs3SerialNpwrBridge
                .ExtractSerials(@"D:\PS3\BLES01039\USRDIR\EBOOT.BIN and npub30042 but not SAVES123 or NPWR00476_00")
                .ToList();

            CollectionAssert.AreEqual(new[] { "BLES01039", "NPUB30042" }, serials);
        }

        [TestMethod]
        public void Scanner_ReadsConfigGamesYmlPath()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");

            try
            {
                Directory.CreateDirectory(Path.Combine(rpcs3Root, "config"));
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "config", "games.yml"),
                    "BCUS98246: C:/Games/Sly.iso");

                var scanner = new Rpcs3Scanner(
                    new FakeLogger(),
                    new PlayniteAchievementsSettings(),
                    new Rpcs3Settings { ExecutablePath = Path.Combine(rpcs3Root, "rpcs3.exe") });
                var method = typeof(Rpcs3Scanner).GetMethod(
                    "ReadRpcs3GamesYmlTitlePathMap",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                var map = (IReadOnlyDictionary<string, string>)method.Invoke(scanner, new object[] { rpcs3Root });

                Assert.AreEqual(1, map.Count);
                Assert.AreEqual("C:/Games/Sly.iso", map["BCUS98246"]);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void GamesYmlReader_ParsesQuotedWindowsIsoPaths()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gamesYml = Path.Combine(tempDir, "games.yml");
                File.WriteAllText(
                    gamesYml,
                    @"# comment
BCUS98198: ""C:\Games\The Sly Collection.iso""
BCUS98246: 'D:\RPCS3\Other Collection.iso' # trailing comment
");

                var map = Rpcs3GamesYmlReader.ReadTitlePathMap(gamesYml);

                Assert.AreEqual(2, map.Count);
                Assert.AreEqual(@"C:\Games\The Sly Collection.iso", map["BCUS98198"]);
                Assert.AreEqual(@"D:\RPCS3\Other Collection.iso", map["BCUS98246"]);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void VfsYmlReader_MissingFile_FallsBackToDefaultDevHdd0()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var resolved = Rpcs3VfsYmlReader.ResolveDevHdd0Root(tempDir);

                Assert.AreEqual(Path.Combine(tempDir, "dev_hdd0"), resolved);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void VfsYmlReader_DefaultMapping_ResolvesExeRelativeDevHdd0()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "config"));
                File.WriteAllText(
                    Path.Combine(tempDir, "config", "vfs.yml"),
                    "$(EmulatorDir): \"\"\n" +
                    "/dev_hdd0/: $(EmulatorDir)dev_hdd0/\n" +
                    "/dev_flash/: $(EmulatorDir)dev_flash/\n" +
                    "Devices:\n" +
                    "  /dev_usb000/: $(EmulatorDir)dev_usb000/\n");

                var resolved = Rpcs3VfsYmlReader.ResolveDevHdd0Root(tempDir);

                Assert.AreEqual(Path.Combine(tempDir, "dev_hdd0"), resolved);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void VfsYmlReader_QuotedRelocatedMapping_ResolvesCustomPath()
        {
            var tempDir = CreateTempDirectory();
            var relocated = Path.Combine(tempDir, "big drive", "ps3 storage", "dev_hdd0");

            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "config"));
                File.WriteAllText(
                    Path.Combine(tempDir, "config", "vfs.yml"),
                    "$(EmulatorDir): \"\"\n" +
                    $"/dev_hdd0/: \"{relocated.Replace('\\', '/')}/\"\n");

                var resolved = Rpcs3VfsYmlReader.ResolveDevHdd0Root(tempDir);

                Assert.AreEqual(relocated, resolved);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void VfsYmlReader_EmulatorDirOverride_RebasesRelativeMapping()
        {
            var tempDir = CreateTempDirectory();
            var overrideRoot = Path.Combine(tempDir, "override root");

            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "config"));
                File.WriteAllText(
                    Path.Combine(tempDir, "config", "vfs.yml"),
                    $"$(EmulatorDir): \"{overrideRoot.Replace('\\', '/')}/\"\n" +
                    "/dev_hdd0/: $(EmulatorDir)dev_hdd0/\n");

                var resolved = Rpcs3VfsYmlReader.ResolveDevHdd0Root(tempDir);

                Assert.AreEqual(Path.Combine(overrideRoot, "dev_hdd0"), resolved);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_VfsRelocatedDevHdd0_ResolvesTrophyData()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var relocatedDevHdd0 = Path.Combine(tempDir, "storage", "dev_hdd0");

            try
            {
                // The emulator root carries no dev_hdd0 of its own; trophies live
                // in the relocated dev_hdd0 referenced by vfs.yml.
                Directory.CreateDirectory(rpcs3Root);
                File.WriteAllBytes(Path.Combine(rpcs3Root, "rpcs3.exe"), new byte[] { 0 });
                Directory.CreateDirectory(Path.Combine(rpcs3Root, "config"));
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "config", "vfs.yml"),
                    "$(EmulatorDir): \"\"\n" +
                    $"/dev_hdd0/: \"{relocatedDevHdd0.Replace('\\', '/')}/\"\n");
                CreateTrophyDataInDevHdd0(relocatedDevHdd0, "NPWR12345_00", "Relocated Game", "Relocated Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Relocated Game",
                    InstallDirectory = Path.Combine(tempDir, "game-without-id")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Relocated Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_SerialBridge_DiscoversSerialFromGamesYmlReverseLookup()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var isoPath = Path.Combine(tempDir, "isos", "renamed disc dump.iso");

            try
            {
                // Neither the rom path nor its contents expose a serial or NPWR id;
                // only RPCS3's own games.yml registration ties the ISO to BLES01039,
                // whose dev_hdd0\game entry carries the trophy TRP.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });
                Directory.CreateDirectory(Path.GetDirectoryName(isoPath));
                File.WriteAllText(isoPath, "no ids in here");
                File.WriteAllText(
                    Path.Combine(rpcs3Root, "games.yml"),
                    $"BLES01039: \"{isoPath}\"");
                CreateTrpFile(
                    Path.Combine(rpcs3Root, "dev_hdd0", "game", "BLES01039", "TROPDIR", "NPWR09999_00", "TROPHY.TRP"),
                    "NPWR09999_00",
                    "Reverse Lookup Game",
                    "Reverse Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Totally Renamed In Playnite",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Reverse Trophy", data.Achievements[0].DisplayName);
                Assert.AreEqual("NPWR09999_00", data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_TrpFallback_ExtractsTrophyIcons()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");

            try
            {
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03 };
                var trpBytes = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00042_00", "Icon Game", "Icon Trophy"))),
                    ("TROP000.PNG", pngBytes));
                var trpPath = Path.Combine(
                    rpcs3Root, "dev_hdd0", "game", "NPUB30042", "TROPDIR", "NPWR00042_00", "TROPHY.TRP");
                Directory.CreateDirectory(Path.GetDirectoryName(trpPath));
                File.WriteAllBytes(trpPath, trpBytes);

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Icon Game",
                    InstallDirectory = Path.Combine(tempDir, "pkg", "NPUB30042")
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                var iconPath = data.Achievements[0].UnlockedIconPath;
                Assert.IsNotNull(iconPath);
                StringAssert.Contains(iconPath, Path.Combine("icon_cache", "rpcs3", "NPWR00042_00"));
                Assert.IsTrue(File.Exists(iconPath));
                CollectionAssert.AreEqual(pngBytes, File.ReadAllBytes(iconPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_IsoEmbeddedTrp_MaterializesLockedListWithIcons()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var isoPath = Path.Combine(tempDir, "isos", "never booted.iso");

            try
            {
                // No trophy folder and no dev_hdd0\game entry exist for this set;
                // the ISO's embedded TROPHY.TRP is the only trophy artifact.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0A, 0x0B };
                var trpBytes = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00777_00", "Never Booted Game", "Embedded Trophy"))),
                    ("TROP000.PNG", pngBytes));
                CreateIso9660WithFiles(isoPath, (@"PS3_GAME\TROPHY\TROPHY.TRP", trpBytes));

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Never Booted Game",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual("Embedded Trophy", data.Achievements[0].DisplayName);
                Assert.IsTrue(data.Achievements.All(achievement => !achievement.Unlocked));
                Assert.AreEqual("NPWR00777_00", data.ProviderGameKey);
                Assert.IsTrue(File.Exists(Path.Combine(pluginDataPath, "icon_cache", "rpcs3", "NPWR00777_00", "TROPHY.TRP")));
                var iconPath = data.Achievements[0].UnlockedIconPath;
                Assert.IsNotNull(iconPath);
                Assert.IsTrue(File.Exists(iconPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_IsoWithSameTitleTrophySets_IsAmbiguousAndUnmatched()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var isoPath = Path.Combine(tempDir, "isos", "multi region.iso");

            try
            {
                // Two trophy sets with the same title inside one image are region
                // variants of one game; selecting either risks wrong trophy data.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                var europeTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00100_00", "Same Game", "Europe Trophy"))));
                var americaTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00200_00", "Same Game", "America Trophy"))));
                CreateIso9660WithFiles(
                    isoPath,
                    (@"PS3_GAME\TROPHY\TROPHY.TRP", europeTrp),
                    (@"PS3_GM01\TROPHY\TROPHY.TRP", americaTrp));

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Multi Region Game",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_IsoWithDistinctTitleTrophySets_SurfacesCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var isoPath = Path.Combine(tempDir, "isos", "trilogy.iso");

            try
            {
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                var firstTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00300_00", "Trilogy Part One", "Part One Trophy"))));
                var secondTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00400_00", "Trilogy Part Two", "Part Two Trophy"))));
                CreateIso9660WithFiles(
                    isoPath,
                    (@"PS3_GAME\TROPHY\TROPHY.TRP", firstTrp),
                    (@"PS3_GM01\TROPHY\TROPHY.TRP", secondTrp));

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Trilogy",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.IsTrue(data.HasAchievements);
                Assert.AreEqual(2, data.Achievements.Count);
                Assert.IsTrue(data.Achievements.All(achievement => !achievement.Unlocked));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_IsoTropdirTrophySet_ResolvesAgainstTrophyFolder()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var isoPath = Path.Combine(tempDir, "roms", "Deadpool.iso");

            try
            {
                // A PS3 disc keeps its trophy sets in PS3_GAME\TROPDIR\<npcommid>,
                // one directory per set, not in a bare TROPHY folder.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR04072_00", "Deadpool", "Folder Trophy");

                var trpBytes = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR04072_00", "Deadpool", "Disc Trophy"))));
                CreateIso9660WithFiles(isoPath, (@"PS3_GAME\TROPDIR\NPWR04072_00\TROPHY.TRP", trpBytes));

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Deadpool",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("NPWR04072_00", data.ProviderGameKey);
                Assert.AreEqual("Folder Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_IsoTropdirTrophySet_NeverBooted_MaterializesLockedList()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var isoPath = Path.Combine(tempDir, "roms", "Deadpool.iso");

            try
            {
                // Never booted in RPCS3, so no trophy folder exists: the set is
                // readable only from the TROPDIR entry inside the image.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                var trpBytes = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR04072_00", "Deadpool", "Disc Trophy"))));
                CreateIso9660WithFiles(isoPath, (@"PS3_GAME\TROPDIR\NPWR04072_00\TROPHY.TRP", trpBytes));

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Deadpool",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("NPWR04072_00", data.ProviderGameKey);
                Assert.AreEqual("Disc Trophy", data.Achievements[0].DisplayName);
                Assert.IsTrue(data.Achievements.All(achievement => !achievement.Unlocked));
                Assert.IsTrue(File.Exists(Path.Combine(pluginDataPath, "icon_cache", "rpcs3", "NPWR04072_00", "TROPHY.TRP")));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_IsoTropdirWithDistinctTitleSets_SurfacesCollection()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var isoPath = Path.Combine(tempDir, "roms", "collection.iso");

            try
            {
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                var firstTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00300_00", "Collection Part One", "Part One Trophy"))));
                var secondTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR00400_00", "Collection Part Two", "Part Two Trophy"))));
                CreateIso9660WithFiles(
                    isoPath,
                    (@"PS3_GAME\TROPDIR\NPWR00300_00\TROPHY.TRP", firstTrp),
                    (@"PS3_GAME\TROPDIR\NPWR00400_00\TROPHY.TRP", secondTrp));

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "The Collection",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual(2, data.Achievements.Count);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_EmuLibraryRomIso_ResolvesTropdirSetFromRomPath()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var romRoot = Path.Combine(tempDir, "Roms", "PS3");

            try
            {
                // EmuLibrary points the install directory at the shared ROM folder
                // and carries the actual image in the rom list.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR04072_00", "Deadpool", "Deadpool Trophy");
                CreateTrophyDataInDevHdd0(Path.Combine(rpcs3Root, "dev_hdd0"), "NPWR05555_00", "Other Game", "Other Trophy");

                var deadpoolTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR04072_00", "Deadpool", "Disc Trophy"))));
                var otherTrp = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR05555_00", "Other Game", "Other Disc Trophy"))));
                CreateIso9660WithFiles(
                    Path.Combine(romRoot, "Deadpool.iso"),
                    (@"PS3_GAME\TROPDIR\NPWR04072_00\TROPHY.TRP", deadpoolTrp));
                CreateIso9660WithFiles(
                    Path.Combine(romRoot, "Other Game.iso"),
                    (@"PS3_GAME\TROPDIR\NPWR05555_00\TROPHY.TRP", otherTrp));

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Deadpool",
                    InstallDirectory = romRoot,
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", Path.Combine(romRoot, "Deadpool.iso")) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("NPWR04072_00", data.ProviderGameKey);
                Assert.AreEqual("Deadpool Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_ExtractedDiscDump_ResolvesPs3GameTropdirSet()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var dumpRoot = Path.Combine(tempDir, "dumps", "Deadpool");

            try
            {
                // A manually extracted dump keeps TROPDIR under PS3_GAME, and
                // Playnite points at the dump root rather than inside PS3_GAME.
                CreateRpcs3TrophyData(rpcs3Root, "NPWR04072_00", "Deadpool", "Folder Trophy");

                File.WriteAllText(Path.Combine(Directory.CreateDirectory(dumpRoot).FullName, "PS3_DISC.SFB"), "SFB");
                CreateTrpFile(
                    Path.Combine(dumpRoot, "PS3_GAME", "TROPDIR", "NPWR04072_00", "TROPHY.TRP"),
                    "NPWR04072_00",
                    "Deadpool",
                    "Disc Trophy");

                var provider = CreateProvider(rpcs3Root);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Renamed Deadpool",
                    InstallDirectory = dumpRoot
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("NPWR04072_00", data.ProviderGameKey);
                Assert.AreEqual("Folder Trophy", data.Achievements[0].DisplayName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NpwrOverrideWithoutTrophyFolder_UsesIsoEmbeddedTrp()
        {
            var tempDir = CreateTempDirectory();
            var rpcs3Root = Path.Combine(tempDir, "rpcs3");
            var pluginDataPath = Path.Combine(tempDir, "plugin-data");
            var isoPath = Path.Combine(tempDir, "roms", "Deadpool.iso");
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                // An override for a game RPCS3 has never booted still has trophy
                // definitions available inside the image.
                File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

                var trpBytes = Rpcs3TrophyParserTrpTests.BuildBinaryTrp(
                    2,
                    ("TROPCONF.SFM", Encoding.UTF8.GetBytes(BuildTropconfXml("NPWR04072_00", "Deadpool", "Disc Trophy"))));
                CreateIso9660WithFiles(isoPath, (@"PS3_GAME\TROPDIR\NPWR04072_00\TROPHY.TRP", trpBytes));

                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "RPCS3",
                        Value = "NPWR04072_00"
                    }
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var provider = CreateProvider(rpcs3Root, pluginUserDataPath: pluginDataPath);
                var game = new Game
                {
                    Id = gameId,
                    Name = "Deadpool",
                    Roms = new ObservableCollection<GameRom> { new GameRom("Disc", isoPath) }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("NPWR04072_00", data.ProviderGameKey);
                Assert.AreEqual("Disc Trophy", data.Achievements[0].DisplayName);
                Assert.IsTrue(data.Achievements.All(achievement => !achievement.Unlocked));
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        private static void CreateIso9660WithFiles(string isoPath, params (string PathInIso, byte[] Data)[] files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(isoPath));

            var builder = new CDBuilder { UseJoliet = true };
            foreach (var file in files)
            {
                builder.AddFile(file.PathInIso, file.Data);
            }

            builder.Build(isoPath);
        }

        [TestMethod]
        public void NpCommIdExtractor_RawScan_ReturnsDistinctNpwrIds()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var rawFile = Path.Combine(tempDir, "collection.iso");
                File.WriteAllText(
                    rawFile,
                    "<npcommid>NPWR01435_00</npcommid> filler <npcommid>NPWR01433_00</npcommid> <npcommid>NPWR01435_00</npcommid>");

                var ids = Rpcs3NpCommIdExtractor.ExtractNpCommIdsFromRawFile(rawFile);

                CollectionAssert.AreEqual(
                    new[] { "NPWR01435_00", "NPWR01433_00" },
                    ids.ToArray());
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ParamSfoReader_ReadsTitleAndTitleId()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var paramSfo = Path.Combine(tempDir, "PARAM.SFO");
                CreateParamSfo(paramSfo, "Sly 1", "BCUS00001");

                var values = Rpcs3ParamSfoReader.ReadStringValues(paramSfo);

                Assert.AreEqual("Sly 1", values["TITLE"]);
                Assert.AreEqual("BCUS00001", values["TITLE_ID"]);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static async Task<GameAchievementData> RefreshSingleGameAsync(Rpcs3DataProvider provider, Game game)
        {
            GameAchievementData captured = null;

            await provider.RefreshAsync(
                new[] { game },
                onGameStarting: null,
                onGameCompleted: (completedGame, data) =>
                {
                    captured = data;
                    return Task.CompletedTask;
                },
                cancel: CancellationToken.None).ConfigureAwait(false);

            return captured;
        }

        private static Rpcs3DataProvider CreateProvider(string rpcs3Root, string extensionsDataPath = null, string pluginUserDataPath = null)
        {
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "RPCS3" });
            var providerSettings = registry.GetSettings<Rpcs3Settings>();
            providerSettings.ExecutablePath = Path.Combine(rpcs3Root, "rpcs3.exe");
            registry.Save(providerSettings);

            return new Rpcs3DataProvider(
                new FakeLogger(),
                settings,
                new FakePlayniteApi(extensionsDataPath),
                pluginUserDataPath ?? string.Empty);
        }

        private static void CreateRpcs3TrophyData(string rpcs3Root, string npCommId, string titleName, string trophyName)
        {
            File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });
            CreateTrophyDataInDevHdd0(Path.Combine(rpcs3Root, "dev_hdd0"), npCommId, titleName, trophyName);
        }

        private static void CreateTrophyDataInDevHdd0(string devHdd0Root, string npCommId, string titleName, string trophyName)
        {
            var trophyDir = Path.Combine(devHdd0Root, "home", "00000001", "trophy", npCommId);
            Directory.CreateDirectory(trophyDir);
            File.WriteAllText(
                Path.Combine(trophyDir, "TROPCONF.SFM"),
                BuildTropconfXml(npCommId, titleName, trophyName));
        }

        private static string CreateRpcs3Root(string rpcs3Root)
        {
            Directory.CreateDirectory(rpcs3Root);
            Directory.CreateDirectory(Path.Combine(rpcs3Root, "dev_hdd0", "home", "00000001", "trophy"));
            return rpcs3Root;
        }

        private static string BuildTropconfXml(string npCommId, string titleName, string trophyName)
        {
            return $@"<trophyconf>
  <npcommid>{npCommId}</npcommid>
  <title-name>{titleName}</title-name>
  <trophy id=""0"" ttype=""B"" hidden=""no"">
    <name>{trophyName}</name>
    <detail>Description</detail>
  </trophy>
</trophyconf>";
        }

        private static void CreateFolderCollection(
            string collectionRoot,
            params (string SubdirectoryName, string NpCommId, string Title)[] subgames)
        {
            Directory.CreateDirectory(collectionRoot);
            File.WriteAllText(Path.Combine(collectionRoot, "PS3_DISC.SFB"), "SFB");

            foreach (var subgame in subgames)
            {
                var subgameRoot = Path.Combine(collectionRoot, subgame.SubdirectoryName);
                Directory.CreateDirectory(subgameRoot);
                CreateParamSfo(Path.Combine(subgameRoot, "PARAM.SFO"), subgame.Title, "BCUS00000");
                CreateTrpFile(
                    Path.Combine(subgameRoot, "TROPHY", "TROPHY.TRP"),
                    subgame.NpCommId,
                    subgame.Title,
                    $"{subgame.Title} Trophy");
            }
        }

        private static void CreateTrpFile(string trpPath, string npCommId, string titleName, string trophyName)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trpPath));
            File.WriteAllText(trpPath, BuildTropconfXml(npCommId, titleName, trophyName));
        }

        private static void CreateRawIsoWithNpCommIds(string isoPath, params string[] npCommIds)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(isoPath));
            File.WriteAllText(
                isoPath,
                string.Join(" filler ", npCommIds.Select(npCommId => $"<npcommid>{npCommId}</npcommid>")));
        }

        private static void CreateParamSfo(string path, string title, string titleId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var entries = new Dictionary<string, string>
            {
                ["TITLE"] = title,
                ["TITLE_ID"] = titleId
            };

            var keys = entries.Keys.ToArray();
            var keyTable = new List<byte>();
            var dataTable = new List<byte>();
            var entryData = new List<Tuple<ushort, ushort, uint, uint, uint>>();

            foreach (var key in keys)
            {
                var keyOffset = (ushort)keyTable.Count;
                keyTable.AddRange(Encoding.ASCII.GetBytes(key));
                keyTable.Add(0);

                var valueOffset = (uint)dataTable.Count;
                var valueBytes = Encoding.UTF8.GetBytes(entries[key] ?? string.Empty);
                dataTable.AddRange(valueBytes);
                dataTable.Add(0);

                entryData.Add(Tuple.Create(
                    keyOffset,
                    (ushort)0x0204,
                    (uint)(valueBytes.Length + 1),
                    (uint)(valueBytes.Length + 1),
                    valueOffset));
            }

            var keyTableOffset = 20 + (entryData.Count * 16);
            var dataTableOffset = keyTableOffset + keyTable.Count;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(0x46535000u);
                writer.Write(0x00000101u);
                writer.Write((uint)keyTableOffset);
                writer.Write((uint)dataTableOffset);
                writer.Write((uint)entryData.Count);

                foreach (var entry in entryData)
                {
                    writer.Write(entry.Item1);
                    writer.Write(entry.Item2);
                    writer.Write(entry.Item3);
                    writer.Write(entry.Item4);
                    writer.Write(entry.Item5);
                }

                writer.Write(keyTable.ToArray());
                writer.Write(dataTable.ToArray());
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(Rpcs3ScannerTests),
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private sealed class FakeLogger : ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public FakePlayniteApi(string extensionsDataPath = null)
            {
                Paths = extensionsDataPath == null ? null : new FakePathsApi(extensionsDataPath);
            }

            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database => null;
            public IDialogsFactory Dialogs => null;
            public IPlaynitePathsAPI Paths { get; }
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

        private sealed class FakePathsApi : IPlaynitePathsAPI
        {
            public FakePathsApi(string extensionsDataPath)
            {
                ExtensionsDataPath = extensionsDataPath;
            }

            public bool IsPortable => false;
            public string ApplicationPath => null;
            public string ConfigurationPath => null;
            public string ExtensionsDataPath { get; }
        }
    }
}
