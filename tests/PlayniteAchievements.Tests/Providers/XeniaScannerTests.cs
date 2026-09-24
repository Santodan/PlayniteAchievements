using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.EmuLibrary;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Tests.Providers;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class XeniaScannerTests
    {
        [TestMethod]
        public void ResolveTitleId_ManualOverrideBeatsCacheAndUpdatesCache()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    XeniaTitleIdOverride = "0x4d5307e6"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var cacheDir = Path.Combine(tempDir, "xenia");
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(
                    Path.Combine(cacheDir, "titleID_cache.json"),
                    JsonConvert.SerializeObject(new List<KeyValuePair<Guid, string>>
                    {
                        new KeyValuePair<Guid, string>(gameId, "11111111")
                    }));

                var scanner = new XeniaScanner(
                    logger: null,
                    playniteApi: new FakePlayniteApi(),
                    providerSettings: new XeniaSettings { AccountPaths = new List<string> { tempDir } },
                    pluginUserDataPath: tempDir);

                var resolved = scanner.ResolveTitleID(new Game
                {
                    Id = gameId,
                    Name = "Test Game"
                }, out var titleId);

                Assert.IsTrue(resolved);
                Assert.AreEqual("4D5307E6", titleId);
                Assert.IsTrue(scanner.TryGetCachedTitleId(gameId, out var cachedTitleId));
                Assert.AreEqual("4D5307E6", cachedTitleId);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_UninstalledGameWithTitleIdOverride_ProducesData()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    XeniaTitleIdOverride = "0x4d5307e6"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var fakeApi = new FakePlayniteApi();
                var scanner = new XeniaScanner(
                    logger: new FakeLogger(),
                    playniteApi: fakeApi,
                    providerSettings: new XeniaSettings { AccountPaths = new List<string> { tempDir } },
                    pluginUserDataPath: tempDir);

                GameAchievementData completedData = null;
                var payload = await scanner.RefreshAsync(
                    new List<Game>
                    {
                        new Game
                        {
                            Id = gameId,
                            Name = "Override Game",
                            IsInstalled = false
                        }
                    },
                    onGameStarting: _ => { },
                    onGameCompleted: (game, data) =>
                    {
                        completedData = data;
                        return Task.CompletedTask;
                    },
                    cancel: CancellationToken.None);

                Assert.IsNotNull(completedData);
                Assert.AreEqual(0x4D5307E6, completedData.AppId);
                Assert.IsFalse(completedData.HasAchievements);
                Assert.AreEqual(1, payload?.Summary?.GamesRefreshed ?? 0);

                var hasInstallErrorNotification = fakeApi.TestNotifications.Messages.Any(message =>
                    message != null &&
                    message.Type == NotificationType.Error &&
                    (message.Text ?? string.Empty).IndexOf("isn't installed", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.IsFalse(hasInstallErrorNotification);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveTitleId_UninstalledEmuLibraryGame_ScansDecodedSourceFile()
        {
            var tempDir = CreateTempDirectory();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = new GameCustomDataStore(Path.Combine(tempDir, "store"))
                };

                var sourceRoot = Path.Combine(tempDir, "network", "Xbox360");
                Directory.CreateDirectory(sourceRoot);
                WriteFakeXexWithTitleId(Path.Combine(sourceRoot, "game.xex"), "54441234");

                var extensionsDataPath = Path.Combine(tempDir, "ExtensionsData");
                var mappingId = Guid.NewGuid();
                EmuLibraryPathResolverTests.WriteConfig(extensionsDataPath, mappingId, sourceRoot);

                // Uninstalled EmuLibrary game: no roms, only the serialized game id.
                var game = EmuLibraryPathResolverTests.BuildEmuLibraryGame(new EmuLibrarySingleFileGameInfo
                {
                    MappingId = mappingId,
                    SourcePath = "game.xex"
                });
                game.Id = Guid.NewGuid();
                game.Name = "Uninstalled EmuLibrary Game";

                var scanner = new XeniaScanner(
                    logger: new FakeLogger(),
                    playniteApi: new FakePlayniteApi(extensionsDataPath),
                    providerSettings: new XeniaSettings { AccountPaths = new List<string> { tempDir } },
                    pluginUserDataPath: tempDir);

                var resolved = scanner.ResolveTitleID(game, out var titleId);

                Assert.IsTrue(resolved);
                Assert.AreEqual("54441234", titleId);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveTitleId_ExeMarkerStraddlesReadBoundary_FindsTitleId()
        {
            // The scanner reads in 8 KB blocks; the ".exe" marker starts two bytes
            // before the first block boundary so it only completes in the next read.
            AssertByteScanFindsTitleId(exeMarkerOffset: (8 * 1024) - 2);
        }

        [TestMethod]
        public void ResolveTitleId_ExeMarkerInLaterBlock_FindsTitleId()
        {
            AssertByteScanFindsTitleId(exeMarkerOffset: 20000);
        }

        [TestMethod]
        public void ResolveTitleId_UppercaseIsoExtension_ByteScanStillRuns()
        {
            AssertByteScanFindsTitleId(exeMarkerOffset: 20000, romFileName: "GAME.ISO");
        }

        private static void AssertByteScanFindsTitleId(int exeMarkerOffset, string romFileName = "game.iso")
        {
            var tempDir = CreateTempDirectory();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = new GameCustomDataStore(Path.Combine(tempDir, "store"))
                };

                var romPath = Path.Combine(tempDir, romFileName);
                WriteFakeRomWithTitleIdAtOffset(romPath, "54441234", exeMarkerOffset);

                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Boundary Game",
                    Roms = new ObservableCollection<GameRom> { new GameRom("rom", romPath) }
                };

                var scanner = new XeniaScanner(
                    logger: new FakeLogger(),
                    playniteApi: new FakePlayniteApi(),
                    providerSettings: new XeniaSettings { AccountPaths = new List<string> { tempDir } },
                    pluginUserDataPath: tempDir);

                var resolved = scanner.ResolveTitleID(game, out var titleId);

                Assert.IsTrue(resolved);
                Assert.AreEqual("54441234", titleId);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_TwoAccountFolders_MergesUnlocksWithEarliestTime()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    XeniaTitleIdOverride = "4D5307E6"
                });

                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = store
                };

                var stockAccount = CreateAccountDirectory(tempDir, "stock");
                var canaryAccount = CreateAccountDirectory(tempDir, "canary");
                var missingGpdAccount = CreateAccountDirectory(tempDir, "netplay");

                WriteFakeGpdWithAchievements(Path.Combine(stockAccount, "4D5307E6.gpd"),
                    (1U, true, 200UL),
                    (2U, false, 0UL),
                    (3U, true, 500UL));
                WriteFakeGpdWithAchievements(Path.Combine(canaryAccount, "4D5307E6.gpd"),
                    (1U, true, 100UL),
                    (2U, true, 300UL),
                    (3U, false, 0UL));

                var scanner = new XeniaScanner(
                    logger: new FakeLogger(),
                    playniteApi: new FakePlayniteApi(),
                    providerSettings: new XeniaSettings
                    {
                        AccountPaths = new List<string> { stockAccount, missingGpdAccount, canaryAccount }
                    },
                    pluginUserDataPath: tempDir);

                GameAchievementData completedData = null;
                await scanner.RefreshAsync(
                    new List<Game> { new Game { Id = gameId, Name = "Merged Game" } },
                    onGameStarting: _ => { },
                    onGameCompleted: (game, data) =>
                    {
                        completedData = data;
                        return Task.CompletedTask;
                    },
                    cancel: CancellationToken.None);

                Assert.IsNotNull(completedData);
                Assert.IsTrue(completedData.HasAchievements);
                CollectionAssert.AreEqual(
                    new[] { "1", "2", "3" },
                    completedData.Achievements.Select(a => a.ApiName).ToArray());

                var byId = completedData.Achievements.ToDictionary(a => a.ApiName);
                Assert.IsTrue(byId["1"].Unlocked);
                Assert.AreEqual(DateTime.FromFileTimeUtc(100), byId["1"].UnlockTimeUtc);
                Assert.IsTrue(byId["2"].Unlocked);
                Assert.AreEqual(DateTime.FromFileTimeUtc(300), byId["2"].UnlockTimeUtc);
                Assert.IsTrue(byId["3"].Unlocked);
                Assert.AreEqual(DateTime.FromFileTimeUtc(500), byId["3"].UnlockTimeUtc);
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ResolveTitleId_XexHeader_ReadsExecutionInfoAndCaches()
        {
            AssertHeaderResolution("GAME.XEX", path => WriteFakeXex(path, 0x4D5307E6), expected: "4D5307E6");
        }

        [TestMethod]
        public void ResolveTitleId_IsoHeader_ReadsDefaultXexFromTrimmedImage()
        {
            AssertHeaderResolution("game.iso", path => WriteFakeTrimmedIso(path, 0x4E4D081C, volumeDescriptorOffset: 0x10000), expected: "4E4D081C");
        }

        [TestMethod]
        public void ResolveTitleId_IsoHeader_ReadsFullXgd3DumpAtKnownOffset()
        {
            AssertHeaderResolution("game.iso", path => WriteFakeTrimmedIso(path, 0x4D530AA4, volumeDescriptorOffset: 0x2090000), expected: "4D530AA4");
        }

        [TestMethod]
        public void ResolveTitleId_IsoHeader_SkipsDecoyAndScansForRealDescriptor()
        {
            AssertHeaderResolution("game.iso", path =>
            {
                // Real partition at 0x20000, not a known offset; a decoy descriptor with an
                // empty root directory sits at the trimmed-image spot.
                WriteFakeTrimmedIso(path, 0x445007F7, volumeDescriptorOffset: 0x30000);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    stream.Position = 0x10000;
                    writer.Write(Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA"));
                    writer.Write(0U);
                    writer.Write(0U);
                }
            }, expected: "445007F7");
        }

        [TestMethod]
        public void ResolveTitleId_XexHeaderWithZeroTitleId_FallsThrough()
        {
            AssertHeaderResolution("game.xex", path => WriteFakeXex(path, 0), expected: null);
        }

        private static void AssertHeaderResolution(string fileName, Action<string> writeRom, string expected)
        {
            var tempDir = CreateTempDirectory();
            var previousPlugin = PlayniteAchievementsPlugin.Instance;

            try
            {
                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin
                {
                    GameCustomDataStore = new GameCustomDataStore(Path.Combine(tempDir, "store"))
                };

                var romPath = Path.Combine(tempDir, fileName);
                writeRom(romPath);

                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Header Game",
                    Roms = new ObservableCollection<GameRom> { new GameRom("rom", romPath) }
                };

                var scanner = new XeniaScanner(
                    logger: new FakeLogger(),
                    playniteApi: new FakePlayniteApi(),
                    providerSettings: new XeniaSettings(),
                    pluginUserDataPath: tempDir);

                var resolved = scanner.ResolveTitleID(game, out var titleId);

                Assert.AreEqual(expected != null, resolved);
                if (expected != null)
                {
                    Assert.AreEqual(expected, titleId);
                    Assert.IsTrue(scanner.TryGetCachedTitleId(game.Id, out var cachedTitleId));
                    Assert.AreEqual(expected, cachedTitleId);
                }
            }
            finally
            {
                PlayniteAchievementsPlugin.Instance = previousPlugin;
                DeleteDirectory(tempDir);
            }
        }

        /// <summary>
        /// XEX2 header with one optional header entry pointing at the execution info block.
        /// </summary>
        private static byte[] BuildFakeXex(uint titleId)
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                WriteBigEndian(writer, 0x58455832U); // "XEX2"
                writer.Write(new byte[0x10]);        // module flags .. security offset
                WriteBigEndian(writer, 1U);          // optional header count at 0x14
                WriteBigEndian(writer, 0x00040006U); // execution info id
                WriteBigEndian(writer, 0x28U);       // execution info offset
                writer.Write(new byte[0x28 - 0x20]);
                WriteBigEndian(writer, 0x12345678U); // media id
                WriteBigEndian(writer, 1U);          // version
                WriteBigEndian(writer, 1U);          // base version
                WriteBigEndian(writer, titleId);
                writer.Write(new byte[] { 2, 0, 1, 1 }); // platform, type, disc number, disc count
                WriteBigEndian(writer, 0U);          // save game id
                writer.Flush();
                return buffer.ToArray();
            }
        }

        private static void WriteFakeXex(string path, uint titleId)
        {
            File.WriteAllBytes(path, BuildFakeXex(titleId));
        }

        /// <summary>
        /// XDVDFS image whose volume descriptor sits at <paramref name="volumeDescriptorOffset"/>
        /// (partition start + 0x10000), with a root directory holding only default.xex.
        /// </summary>
        internal static void WriteFakeTrimmedIso(string path, uint titleId, long volumeDescriptorOffset)
        {
            const int sector = 2048;
            var partitionOffset = volumeDescriptorOffset - 0x10000;
            const uint rootDirSector = 34;
            const uint xexSector = 35;
            var xex = BuildFakeXex(titleId);
            var name = Encoding.ASCII.GetBytes("default.xex");

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                stream.SetLength(partitionOffset + (xexSector + 1) * sector);

                stream.Position = volumeDescriptorOffset;
                writer.Write(Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA"));
                writer.Write(rootDirSector);          // little-endian
                writer.Write((uint)sector);           // root directory size

                stream.Position = partitionOffset + rootDirSector * sector;
                writer.Write((ushort)0);              // left
                writer.Write((ushort)0);              // right
                writer.Write(xexSector);
                writer.Write((uint)xex.Length);
                writer.Write((byte)0x20);             // attributes
                writer.Write((byte)name.Length);
                writer.Write(name);

                stream.Position = partitionOffset + xexSector * sector;
                writer.Write(xex);
            }
        }

        [TestMethod]
        public void TryReadTitleString_ReadsSection5String()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gpdPath = Path.Combine(tempDir, "4D5307E6.gpd");
                WriteFakeGpdWithTitleString(gpdPath, "Test Game");

                Assert.IsTrue(GPDResolver.TryReadTitleString(gpdPath, out var title));
                Assert.AreEqual("Test Game", title.Replace("\0", ""));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void TryReadTitleString_NonXdbfFile_ReturnsFalse()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var path = Path.Combine(tempDir, "not-a-gpd.gpd");
                File.WriteAllText(path, "this is not an XDBF file", Encoding.ASCII);

                Assert.IsFalse(GPDResolver.TryReadTitleString(path, out var title));
                Assert.IsNull(title);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        /// <summary>
        /// Writes a minimal fake xex: a known publisher code and title id followed by
        /// enough padding for the ".exe" marker to sit past the scanner's look-back window.
        /// </summary>
        private static void WriteFakeXexWithTitleId(string path, string titleId)
        {
            var content = new StringBuilder();
            content.Append(titleId);
            content.Append(new string('x', 300 - titleId.Length));
            content.Append(".exe");
            File.WriteAllText(path, content.ToString(), Encoding.ASCII);
        }

        /// <summary>
        /// Writes a fake rom of neutral filler with the title id placed at the start of the
        /// scanner's 300-byte look-back window and the ".exe" marker at the given offset.
        /// </summary>
        private static void WriteFakeRomWithTitleIdAtOffset(string path, string titleId, int exeMarkerOffset)
        {
            var content = new byte[exeMarkerOffset + 4 + 512];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (byte)'-';
            }

            var titleBytes = Encoding.ASCII.GetBytes(titleId);
            Array.Copy(titleBytes, 0, content, exeMarkerOffset - 300, titleBytes.Length);

            var marker = Encoding.ASCII.GetBytes(".exe");
            Array.Copy(marker, 0, content, exeMarkerOffset, marker.Length);

            File.WriteAllBytes(path, content);
        }

        /// <summary>
        /// Writes a minimal XDBF/GPD with one icon entry and one section-5 string entry
        /// holding the title encoded as UTF-16 big-endian, matching real gpd files.
        /// </summary>
        private static void WriteFakeGpdWithTitleString(string path, string title)
        {
            var titleBytes = Encoding.BigEndianUnicode.GetBytes(title + "\0");

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                WriteBigEndian(writer, 0x58444246U); // XDBF magic
                WriteBigEndian(writer, 1U);          // version
                WriteBigEndian(writer, 2U);          // entry capacity
                WriteBigEndian(writer, 2U);          // entries used
                WriteBigEndian(writer, 0U);          // free capacity
                WriteBigEndian(writer, 0U);          // free used

                // Entry 0: icon data
                WriteBigEndian(writer, (ushort)2);
                WriteBigEndian(writer, 1UL);
                WriteBigEndian(writer, 0U);          // offset
                WriteBigEndian(writer, 4U);          // size

                // Entry 1: string data (game title)
                WriteBigEndian(writer, (ushort)5);
                WriteBigEndian(writer, 0x8000UL);
                WriteBigEndian(writer, 4U);          // offset
                WriteBigEndian(writer, (uint)titleBytes.Length);

                writer.Write(new byte[4]);           // icon payload
                writer.Write(titleBytes);
            }
        }

        /// <summary>
        /// Creates &lt;root&gt;\&lt;build&gt;\content\&lt;XUID&gt;\FFFE07D1\00010000\&lt;XUID&gt; with an Account file.
        /// </summary>
        internal static string CreateAccountDirectory(string root, string build)
        {
            const string xuid = "E0300000AAAAAAAA";
            var accountDir = Path.Combine(root, build, "content", xuid, "FFFE07D1", "00010000", xuid);
            Directory.CreateDirectory(accountDir);
            File.WriteAllBytes(Path.Combine(accountDir, "Account"), new byte[4]);
            return accountDir;
        }

        /// <summary>
        /// Writes a minimal XDBF/GPD holding section-1 achievement records. Entry and free
        /// capacities are equal so both the full loader and the progress reader find the data.
        /// </summary>
        internal static void WriteFakeGpdWithAchievements(string path, params (uint Id, bool Earned, ulong UnlockTime)[] achievements)
        {
            var payloads = achievements.Select(achievement =>
            {
                using (var buffer = new MemoryStream())
                using (var payload = new BinaryWriter(buffer))
                {
                    WriteBigEndian(payload, 0x10U);                 // magic
                    WriteBigEndian(payload, achievement.Id);
                    WriteBigEndian(payload, achievement.Id);        // icon id
                    WriteBigEndian(payload, 10U);                   // gamerscore
                    WriteBigEndian(payload, achievement.Earned ? 0x20001U : 0x1U);
                    WriteBigEndian(payload, achievement.UnlockTime);
                    payload.Write(Encoding.BigEndianUnicode.GetBytes($"Title {achievement.Id}\0"));
                    payload.Write(Encoding.BigEndianUnicode.GetBytes($"Unlocked {achievement.Id}\0"));
                    payload.Write(Encoding.BigEndianUnicode.GetBytes($"Locked {achievement.Id}\0"));
                    payload.Flush();
                    return buffer.ToArray();
                }
            }).ToList();

            var capacity = (uint)payloads.Count;
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                WriteBigEndian(writer, 0x58444246U); // XDBF magic
                WriteBigEndian(writer, 1U);          // version
                WriteBigEndian(writer, capacity);    // entry capacity
                WriteBigEndian(writer, capacity);    // entries used
                WriteBigEndian(writer, capacity);    // free capacity
                WriteBigEndian(writer, 0U);          // free used

                var offset = 0U;
                foreach (var payload in payloads)
                {
                    WriteBigEndian(writer, (ushort)1);
                    WriteBigEndian(writer, (ulong)offset);
                    WriteBigEndian(writer, offset);
                    WriteBigEndian(writer, (uint)payload.Length);
                    offset += (uint)payload.Length;
                }

                writer.Write(new byte[8 * capacity]); // free table
                foreach (var payload in payloads)
                {
                    writer.Write(payload);
                }
            }
        }

        private static void WriteBigEndian(BinaryWriter writer, ushort value)
        {
            writer.Write((byte)(value >> 8));
            writer.Write((byte)value);
        }

        private static void WriteBigEndian(BinaryWriter writer, uint value)
        {
            writer.Write((byte)(value >> 24));
            writer.Write((byte)(value >> 16));
            writer.Write((byte)(value >> 8));
            writer.Write((byte)value);
        }

        private static void WriteBigEndian(BinaryWriter writer, ulong value)
        {
            WriteBigEndian(writer, (uint)(value >> 32));
            WriteBigEndian(writer, (uint)value);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(XeniaScannerTests),
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

            public FakeNotificationsApi TestNotifications { get; } = new FakeNotificationsApi();

            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database => null;
            public IDialogsFactory Dialogs => null;
            public IPlaynitePathsAPI Paths { get; }
            public INotificationsAPI Notifications => TestNotifications;
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

        private sealed class FakeNotificationsApi : INotificationsAPI
        {
            public ObservableCollection<NotificationMessage> Messages { get; } = new ObservableCollection<NotificationMessage>();

            public int Count => Messages.Count;

            public void Add(NotificationMessage message)
            {
                if (message != null)
                {
                    Messages.Add(message);
                }
            }

            public void Add(string id, string text, NotificationType type)
            {
                Messages.Add(new NotificationMessage(id, text, type));
            }

            public void Remove(string id)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return;
                }

                var message = Messages.FirstOrDefault(item => string.Equals(item?.Id, id, StringComparison.OrdinalIgnoreCase));
                if (message != null)
                {
                    Messages.Remove(message);
                }
            }

            public void RemoveAll()
            {
                Messages.Clear();
            }
        }
    }
}
