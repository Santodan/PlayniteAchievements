using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.PlayStationTrophies;
using PlayniteAchievements.Providers.Vita3K;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class Vita3KProviderTests
    {
        private const string TitleId = "PCSE00120";
        private const string NpCommId = "NPWR01234_00";

        // 2024-03-01 12:00:00 UTC
        private const ulong UnlockSeconds = 1709294400;

        [TestMethod]
        public void TropusrReader_ReadsUnlockBitsAndTimesAcrossWordBoundaries()
        {
            var bytes = BuildTropusr(128, new Dictionary<int, ulong>
            {
                [0] = UnlockSeconds,
                [31] = UnlockSeconds + 1,
                [32] = UnlockSeconds + 2,
                [127] = 0
            });

            Assert.IsTrue(Vita3KTropusrReader.TryRead(bytes, out var unlocked, out var error), error);
            CollectionAssert.AreEquivalent(new[] { 0, 31, 32, 127 }, unlocked.Keys.ToArray());
            Assert.AreEqual(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), unlocked[0]);
            Assert.AreEqual(new DateTime(2024, 3, 1, 12, 0, 2, DateTimeKind.Utc), unlocked[32]);
            Assert.IsNull(unlocked[127], "An unlocked trophy with time 0 has an unknown unlock time.");
        }

        [TestMethod]
        public void TropusrReader_IgnoresBitsBeyondTrophyCount()
        {
            var bytes = BuildTropusr(10, new Dictionary<int, ulong> { [3] = UnlockSeconds, [40] = UnlockSeconds });

            Assert.IsTrue(Vita3KTropusrReader.TryRead(bytes, out var unlocked, out _));
            CollectionAssert.AreEqual(new[] { 3 }, unlocked.Keys.ToArray());
        }

        [TestMethod]
        public void TropusrReader_RejectsWrongMagicOrSize()
        {
            var bytes = BuildTropusr(4, new Dictionary<int, ulong> { [0] = UnlockSeconds });
            bytes[0] ^= 0xFF;
            Assert.IsFalse(Vita3KTropusrReader.TryRead(bytes, out _, out _));

            var truncated = BuildTropusr(4, new Dictionary<int, ulong>()).Take(1000).ToArray();
            Assert.IsFalse(Vita3KTropusrReader.TryRead(truncated, out _, out _));

            Assert.IsFalse(Vita3KTropusrReader.TryRead((byte[])null, out _, out _));
        }

        [TestMethod]
        public void InstallationContext_PortableFolderWinsOverPrefPath()
        {
            var root = CreateTempDirectory();
            try
            {
                var emulatorRoot = Path.Combine(root, "emu");
                Directory.CreateDirectory(Path.Combine(emulatorRoot, "portable", "fs", "ux0", "user", "00"));
                File.WriteAllText(Path.Combine(emulatorRoot, "portable", "fs", "ux0", "user", "00", "user.xml"), "<user id=\"00\" name=\"A\"/>");
                File.WriteAllText(Path.Combine(emulatorRoot, "config.yml"), $"pref-path: {Path.Combine(root, "elsewhere")}\n");

                var context = Vita3KInstallationContext.Create(emulatorRoot, logger: null, defaultDataRoot: Path.Combine(root, "appdata"));

                Assert.IsNotNull(context);
                Assert.AreEqual(Path.Combine(emulatorRoot, "portable", "fs"), context.DataRoot);
                Assert.AreEqual("00", context.UserId);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void InstallationContext_UsesQuotedPrefPathAndConfiguredUser()
        {
            var root = CreateTempDirectory();
            try
            {
                var emulatorRoot = Path.Combine(root, "emu");
                var dataRoot = Path.Combine(root, "vita fs");
                Directory.CreateDirectory(emulatorRoot);
                CreateUser(dataRoot, "00");
                CreateUser(dataRoot, "01");
                File.WriteAllText(
                    Path.Combine(emulatorRoot, "config.yml"),
                    "---\nuser-id: '01'\npref-path: \"" + dataRoot.Replace("\\", "\\\\") + "\"\nsys-lang: 1\n");

                var context = Vita3KInstallationContext.Create(emulatorRoot, logger: null, defaultDataRoot: Path.Combine(root, "appdata"));

                Assert.IsNotNull(context);
                Assert.AreEqual(dataRoot, context.DataRoot);
                Assert.AreEqual("01", context.UserId);
                Assert.AreEqual("config.yml user-id", context.UserIdSource);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void InstallationContext_FallsBackToDefaultRootAndFirstUser()
        {
            var root = CreateTempDirectory();
            try
            {
                var emulatorRoot = Path.Combine(root, "emu");
                var defaultRoot = Path.Combine(root, "appdata");
                Directory.CreateDirectory(emulatorRoot);
                CreateUser(defaultRoot, "02");
                CreateUser(defaultRoot, "01");
                File.WriteAllText(Path.Combine(emulatorRoot, "config.yml"), "pref-path: ''\nuser-id: 09\n");

                var context = Vita3KInstallationContext.Create(emulatorRoot, logger: null, defaultDataRoot: defaultRoot);

                Assert.IsNotNull(context);
                Assert.AreEqual(defaultRoot, context.DataRoot);
                Assert.AreEqual("01", context.UserId, "A missing configured user falls back to the alphabetically first user.");
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void TitleIdResolver_ReadsArgumentsAndPaths()
        {
            Assert.AreEqual(TitleId, Vita3KTitleIdResolver.FromArguments("--frontend sdl -r pcse00120"));
            Assert.AreEqual("PCSB00001", Vita3KTitleIdResolver.FromArguments("--installed-path=\"PCSB00001\""));
            Assert.IsNull(Vita3KTitleIdResolver.FromArguments("\"{ImagePath}\""));

            Assert.AreEqual(TitleId, Vita3KTitleIdResolver.FromPath(@"D:\Vita3K\fs\ux0\app\PCSE00120\eboot.bin"));
            Assert.AreEqual("PCSG00001", Vita3KTitleIdResolver.FromPath(@"D:\PCSE99999\Game (PCSG00001).vpk"));
            Assert.IsNull(Vita3KTitleIdResolver.FromPath(@"D:\Roms\Persona 4 Golden.vpk"));
        }

        [TestMethod]
        public void TitleIdResolver_ReadsParamSfoInsideVpk()
        {
            var root = CreateTempDirectory();
            try
            {
                var vpkPath = Path.Combine(root, "Persona 4 Golden.vpk");
                using (var archive = ZipFile.Open(vpkPath, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("sce_sys/param.sfo");
                    using (var stream = entry.Open())
                    {
                        var bytes = BuildParamSfo(new Dictionary<string, string> { ["TITLE"] = "Persona 4 Golden", ["TITLE_ID"] = TitleId });
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }

                Assert.AreEqual(TitleId, Vita3KTitleIdResolver.FromVpk(vpkPath, logger: null));
                Assert.IsNull(Vita3KTitleIdResolver.FromVpk(Path.Combine(root, "missing.vpk"), logger: null));
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void MatchIdHelper_ClassifiesTitleAndNpCommIds()
        {
            Assert.AreEqual(Vita3KMatchIdKind.TitleId, Vita3KMatchIdHelper.GetKind("pcse00120"));
            Assert.AreEqual(Vita3KMatchIdKind.NpCommId, Vita3KMatchIdHelper.GetKind("npwr01234_00"));
            Assert.AreEqual(NpCommId, Vita3KMatchIdHelper.Normalize(" npwr01234_00 "));
            Assert.IsNull(Vita3KMatchIdHelper.Normalize("PCSE0012"));
        }

        [TestMethod]
        public void RegionHint_ReadsVitaTitleIdFourthLetter()
        {
            Assert.AreEqual("na", ExophaseGameNameMatcher.MapVitaTitleIdToRegionHint("PCSE00120"));
            Assert.AreEqual("eu", ExophaseGameNameMatcher.MapVitaTitleIdToRegionHint("PCSB00245"));
            Assert.AreEqual("jp", ExophaseGameNameMatcher.MapVitaTitleIdToRegionHint("PCSG00001"));
            Assert.AreEqual("as", ExophaseGameNameMatcher.MapVitaTitleIdToRegionHint("PCSH00021"));
            Assert.IsNull(ExophaseGameNameMatcher.MapVitaTitleIdToRegionHint("CUSA00432"));
        }

        [TestMethod]
        public void TrophyConfParser_FallsBackToTropSfmWhenNoLocalizedFileExists()
        {
            var root = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "TROPCONF.SFM"), BuildTropconf());
                File.WriteAllText(Path.Combine(root, "TROP.SFM"), BuildTropSfm("Default"));

                var trophies = TrophyConfParser.ParseTrophyDefinitions(Path.Combine(root, "TROPCONF.SFM"), "fr", logger: null);

                Assert.AreEqual(3, trophies.Count);
                Assert.AreEqual("Default Platinum", trophies[0].Name);
                Assert.AreEqual("Default DLC Pack", trophies[2].GroupName);
                Assert.AreEqual("Test Vita Game", TrophyConfParser.ReadSetTitle(root, logger: null));
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_InstalledGame_ReadsConfFolderAndProgress()
        {
            var root = CreateTempDirectory();
            try
            {
                var emulatorRoot = CreatePortableInstall(root, out var dataRoot);
                CreateAppTrophyFolder(dataRoot, TitleId, NpCommId, trp: null);
                var confFolder = Path.Combine(dataRoot, "ux0", "user", "00", "trophy", "conf", NpCommId);
                Directory.CreateDirectory(confFolder);
                File.WriteAllText(Path.Combine(confFolder, "TROPCONF.SFM"), BuildTropconf());
                File.WriteAllText(Path.Combine(confFolder, "TROP.SFM"), BuildTropSfm("Default"));
                File.WriteAllText(Path.Combine(confFolder, "TROP_02.SFM"), BuildTropSfm("Francais"));
                File.WriteAllBytes(Path.Combine(confFolder, "TROP001.PNG"), new byte[] { 1 });
                var dataFolder = Path.Combine(dataRoot, "ux0", "user", "00", "trophy", "data", NpCommId);
                Directory.CreateDirectory(dataFolder);
                File.WriteAllBytes(
                    Path.Combine(dataFolder, "TROPUSR.DAT"),
                    BuildTropusr(3, new Dictionary<int, ulong> { [1] = UnlockSeconds }));

                var provider = CreateProvider(emulatorRoot, globalLanguage: "french");
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Some Other Name",
                    InstallDirectory = Path.Combine(dataRoot, "ux0", "app", TitleId)
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("Vita3K", data.ProviderKey);
                Assert.AreEqual(NpCommId, data.ProviderGameKey);
                Assert.AreEqual(3, data.Achievements.Count);

                var platinum = data.Achievements.Single(a => a.ApiName == "0");
                Assert.AreEqual("Francais Platinum", platinum.DisplayName);
                Assert.AreEqual("platinum", platinum.TrophyType);
                Assert.IsTrue(platinum.IsCapstone);
                Assert.IsFalse(platinum.Unlocked);

                var gold = data.Achievements.Single(a => a.ApiName == "1");
                Assert.IsTrue(gold.Unlocked);
                Assert.AreEqual(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), gold.UnlockTimeUtc);
                Assert.AreEqual(Path.Combine(confFolder, "TROP001.PNG"), gold.UnlockedIconPath);
                Assert.IsTrue(gold.Hidden);

                var dlc = data.Achievements.Single(a => a.ApiName == "2");
                Assert.AreEqual("DLC", dlc.CategoryKind);
                Assert.AreEqual("Francais DLC Pack", dlc.Category);
                Assert.AreEqual("Base", gold.CategoryKind);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NeverBootedGame_ReadsDefinitionsFromAppTrp()
        {
            var root = CreateTempDirectory();
            try
            {
                var emulatorRoot = CreatePortableInstall(root, out var dataRoot);
                CreateAppTrophyFolder(dataRoot, TitleId, NpCommId, BuildTrp(new Dictionary<string, byte[]>
                {
                    ["TROPCONF.SFM"] = Encoding.UTF8.GetBytes(BuildTropconf()),
                    ["TROP.SFM"] = Encoding.UTF8.GetBytes(BuildTropSfm("Default")),
                    ["TROP000.PNG"] = new byte[] { 7 }
                }));

                var provider = CreateProvider(emulatorRoot, pluginUserDataPath: Path.Combine(root, "plugin"));
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Test Vita Game",
                    GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>(),
                    Roms = new System.Collections.ObjectModel.ObservableCollection<GameRom>
                    {
                        new GameRom("Game", Path.Combine(root, "roms", $"Test Vita Game [{TitleId}].vpk"))
                    }
                };

                var data = await RefreshSingleGameAsync(provider, game).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual(3, data.Achievements.Count);
                Assert.IsTrue(data.Achievements.All(a => !a.Unlocked));
                Assert.AreEqual("Default Platinum", data.Achievements[0].DisplayName);
                Assert.IsNotNull(data.Achievements[0].UnlockedIconPath, "TRP icons are extracted into the plugin icon cache.");
                StringAssert.StartsWith(data.Achievements[0].UnlockedIconPath, Path.Combine(root, "plugin", "icon_cache", "vita3k"));
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_InvalidProgressFile_PreservesCachedAchievements()
        {
            var root = CreateTempDirectory();
            try
            {
                var emulatorRoot = CreatePortableInstall(root, out var dataRoot);
                CreateAppTrophyFolder(dataRoot, TitleId, NpCommId, trp: null);
                var confFolder = Path.Combine(dataRoot, "ux0", "user", "00", "trophy", "conf", NpCommId);
                Directory.CreateDirectory(confFolder);
                File.WriteAllText(Path.Combine(confFolder, "TROPCONF.SFM"), BuildTropconf());
                var dataFolder = Path.Combine(dataRoot, "ux0", "user", "00", "trophy", "data", NpCommId);
                Directory.CreateDirectory(dataFolder);
                File.WriteAllBytes(Path.Combine(dataFolder, "TROPUSR.DAT"), new byte[100]);

                var provider = CreateProvider(emulatorRoot);
                var data = await RefreshSingleGameAsync(provider, new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Test Vita Game",
                    InstallDirectory = Path.Combine(dataRoot, "ux0", "app", TitleId)
                }).ConfigureAwait(false);

                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_NoTitleId_MatchesConfFolderBySetTitle()
        {
            var root = CreateTempDirectory();
            try
            {
                var emulatorRoot = CreatePortableInstall(root, out var dataRoot);
                var confFolder = Path.Combine(dataRoot, "ux0", "user", "00", "trophy", "conf", NpCommId);
                Directory.CreateDirectory(confFolder);
                File.WriteAllText(Path.Combine(confFolder, "TROPCONF.SFM"), BuildTropconf());
                File.WriteAllText(Path.Combine(confFolder, "TROP.SFM"), BuildTropSfm("Default"));

                var provider = CreateProvider(emulatorRoot);
                var data = await RefreshSingleGameAsync(provider, new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Test Vita Game™"
                }).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual(NpCommId, data.ProviderGameKey);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        private static Vita3KDataProvider CreateProvider(string emulatorRoot, string globalLanguage = null, string pluginUserDataPath = null)
        {
            var settings = new PlayniteAchievementsSettings();
            if (globalLanguage != null)
            {
                settings.Persisted.GlobalLanguage = globalLanguage;
            }

            var registry = new ProviderRegistry(settings, new[] { "Vita3K" });
            var providerSettings = registry.GetSettings<Vita3KSettings>();
            providerSettings.ExecutablePaths = new List<string> { Path.Combine(emulatorRoot, "Vita3K.exe") };
            registry.Save(providerSettings);

            return new Vita3KDataProvider(new FakeLogger(), settings, new FakePlayniteApi(), pluginUserDataPath ?? string.Empty);
        }

        private static async Task<GameAchievementData> RefreshSingleGameAsync(Vita3KDataProvider provider, Game game)
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

        private static string CreatePortableInstall(string root, out string dataRoot)
        {
            var emulatorRoot = Path.Combine(root, "Vita3K");
            dataRoot = Path.Combine(emulatorRoot, "portable", "fs");
            Directory.CreateDirectory(emulatorRoot);
            File.WriteAllBytes(Path.Combine(emulatorRoot, "Vita3K.exe"), new byte[0]);
            CreateUser(dataRoot, "00");
            Directory.CreateDirectory(Path.Combine(dataRoot, "ux0", "user", "00", "trophy"));
            return emulatorRoot;
        }

        private static void CreateUser(string dataRoot, string userId)
        {
            var userFolder = Path.Combine(dataRoot, "ux0", "user", userId);
            Directory.CreateDirectory(userFolder);
            File.WriteAllText(Path.Combine(userFolder, "user.xml"), $"<user id=\"{userId}\" name=\"Vita3K\"/>");
        }

        private static void CreateAppTrophyFolder(string dataRoot, string titleId, string npCommId, byte[] trp)
        {
            var folder = Path.Combine(dataRoot, "ux0", "app", titleId, "sce_sys", "trophy", npCommId);
            Directory.CreateDirectory(folder);
            if (trp != null)
            {
                File.WriteAllBytes(Path.Combine(folder, "TROPHY.TRP"), trp);
            }
        }

        private static string BuildTropconf()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                   "<trophyconf version=\"1.1\" platform=\"psp2\">\n" +
                   $"  <npcommid>{NpCommId}</npcommid>\n" +
                   "  <group id=\"001\"/>\n" +
                   "  <trophy id=\"000\" hidden=\"no\" ttype=\"P\" pid=\"-1\"/>\n" +
                   "  <trophy id=\"001\" hidden=\"yes\" ttype=\"G\" pid=\"000\"/>\n" +
                   "  <trophy id=\"002\" hidden=\"no\" ttype=\"B\" pid=\"000\" gid=\"001\"/>\n" +
                   "</trophyconf>\n";
        }

        private static string BuildTropSfm(string prefix)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                   "<trophyconf version=\"1.1\" platform=\"psp2\">\n" +
                   $"  <npcommid>{NpCommId}</npcommid>\n" +
                   "  <title-name>Test Vita Game</title-name>\n" +
                   $"  <group id=\"001\"><name>{prefix} DLC Pack</name></group>\n" +
                   $"  <trophy id=\"000\"><name>{prefix} Platinum</name><detail>All</detail></trophy>\n" +
                   $"  <trophy id=\"001\"><name>{prefix} Gold</name><detail>Gold detail</detail></trophy>\n" +
                   $"  <trophy id=\"002\"><name>{prefix} Bronze</name><detail>Bronze detail</detail></trophy>\n" +
                   "</trophyconf>\n";
        }

        private static byte[] BuildTropusr(int trophyCount, IDictionary<int, ulong> unlockTimes)
        {
            var bytes = new byte[Vita3KTropusrReader.FileSize];
            BitConverter.GetBytes(Vita3KTropusrReader.Magic).CopyTo(bytes, 0);
            BitConverter.GetBytes((uint)trophyCount).CopyTo(bytes, Vita3KTropusrReader.TrophyCountOffset);
            foreach (var pair in unlockTimes)
            {
                var wordOffset = Vita3KTropusrReader.UnlockedBitsetOffset + ((pair.Key >> 5) * 4);
                var word = BitConverter.ToUInt32(bytes, wordOffset) | (1u << (pair.Key & 31));
                BitConverter.GetBytes(word).CopyTo(bytes, wordOffset);
                BitConverter.GetBytes(pair.Value).CopyTo(bytes, Vita3KTropusrReader.UnlockTimesOffset + (pair.Key * 8));
            }

            return bytes;
        }

        private static byte[] BuildTrp(IDictionary<string, byte[]> files)
        {
            const int headerSize = 0x30;
            const int entrySize = 0x40;
            var dataStart = headerSize + (files.Count * entrySize);
            var output = new List<byte>();
            output.AddRange(BigEndian(0xDCA24D00u));
            output.AddRange(BigEndian(1u));
            output.AddRange(new byte[8]);
            output.AddRange(BigEndian((uint)files.Count));
            output.AddRange(BigEndian((uint)entrySize));
            output.AddRange(new byte[headerSize - output.Count]);

            var offset = (ulong)dataStart;
            foreach (var file in files)
            {
                var name = new byte[32];
                Encoding.ASCII.GetBytes(file.Key).CopyTo(name, 0);
                output.AddRange(name);
                output.AddRange(BigEndian((uint)(offset >> 32)));
                output.AddRange(BigEndian((uint)offset));
                output.AddRange(BigEndian(0u));
                output.AddRange(BigEndian((uint)file.Value.Length));
                output.AddRange(new byte[16]);
                offset += (ulong)file.Value.Length;
            }

            foreach (var file in files)
            {
                output.AddRange(file.Value);
            }

            return output.ToArray();
        }

        private static byte[] BigEndian(uint value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static byte[] BuildParamSfo(IDictionary<string, string> entries)
        {
            var keyTable = new List<byte>();
            var dataTable = new List<byte>();
            var entryData = new List<Tuple<ushort, uint, uint>>();
            foreach (var entry in entries)
            {
                var keyOffset = (ushort)keyTable.Count;
                keyTable.AddRange(Encoding.ASCII.GetBytes(entry.Key));
                keyTable.Add(0);

                var valueOffset = (uint)dataTable.Count;
                var valueBytes = Encoding.UTF8.GetBytes(entry.Value);
                dataTable.AddRange(valueBytes);
                dataTable.Add(0);
                entryData.Add(Tuple.Create(keyOffset, (uint)(valueBytes.Length + 1), valueOffset));
            }

            var keyTableOffset = 20 + (entryData.Count * 16);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46535000u);
                writer.Write(0x00000101u);
                writer.Write((uint)keyTableOffset);
                writer.Write((uint)(keyTableOffset + keyTable.Count));
                writer.Write((uint)entryData.Count);
                foreach (var entry in entryData)
                {
                    writer.Write(entry.Item1);
                    writer.Write((ushort)0x0204);
                    writer.Write(entry.Item2);
                    writer.Write(entry.Item2);
                    writer.Write(entry.Item3);
                }

                writer.Write(keyTable.ToArray());
                writer.Write(dataTable.ToArray());
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", nameof(Vita3KProviderTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
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
