using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
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

                Assert.IsNotNull(data);
                Assert.AreEqual("RPCS3", data.ProviderKey);
                Assert.IsFalse(data.HasAchievements);
                Assert.AreEqual(0, data.Achievements.Count);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
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

        private static Rpcs3DataProvider CreateProvider(string rpcs3Root)
        {
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "RPCS3" });
            var providerSettings = registry.GetSettings<Rpcs3Settings>();
            providerSettings.ExecutablePath = Path.Combine(rpcs3Root, "rpcs3.exe");
            registry.Save(providerSettings);

            return new Rpcs3DataProvider(new FakeLogger(), settings, new FakePlayniteApi());
        }

        private static void CreateRpcs3TrophyData(string rpcs3Root, string npCommId, string titleName, string trophyName)
        {
            File.WriteAllBytes(Path.Combine(CreateRpcs3Root(rpcs3Root), "rpcs3.exe"), new byte[] { 0 });

            var trophyDir = Path.Combine(rpcs3Root, "dev_hdd0", "home", "00000001", "trophy", npCommId);
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
