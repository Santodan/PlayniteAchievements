using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class NativeSourceReaderTests
    {
        private string _root;
        [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "PlayAchNative-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
        [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }
        private string Write(string name, string text) { var file = Path.GetFullPath(Path.Combine(_root, name)); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, text); return file; }
        private NativeSourceResult State(string source, string file, string text, params string[] ids)
        {
            var r = new NativeSourceResult { Source = source, Save = Write(file, text), Achievements = ids.Select(id => new NativeAchievement { Id = id, Name = id }).ToList() };
            NativeSourceReader.ReadState(r); return r;
        }
        [TestMethod] public void MarkerPatchCombinesSignedLowAndHighWords()
        {
            var r = State("markerpatch", "settings.txt", "Controls.AcL.X = -2147483648\nControls.AcL.Y = 1\n", "0", "31", "32");
            Assert.IsFalse(r.Achievements[0].Unlocked); Assert.IsTrue(r.Achievements[1].Unlocked); Assert.IsTrue(r.Achievements[2].Unlocked);
            Assert.IsNull(r.Achievements[1].Time);
        }
        [TestMethod] public void MadnessPatchReads64BitFlagWithoutInventingDates()
        {
            var r = State("madnesspatch", "Achievements.txt", "UnlockFlag = 17592186044417\n", "0", "1", "44");
            Assert.IsTrue(r.Achievements[0].Unlocked); Assert.IsFalse(r.Achievements[1].Unlocked); Assert.IsTrue(r.Achievements[2].Unlocked); Assert.IsNull(r.Achievements[2].Time);
        }
        [TestMethod] public void MissingPatchFlagFailsInsteadOfRelocking() => Assert.ThrowsException<InvalidDataException>(() => State("madnesspatch", "a.txt", "UnlockFlag = broken", "0"));
        [TestMethod] public void LadxReadsBooleanLines() { var r = State("ladxhd", "achievements", "achievement0 true\nachievement1 false\n", "0", "1"); Assert.IsTrue(r.Achievements[0].Unlocked); Assert.IsFalse(r.Achievements[1].Unlocked); }
        [TestMethod] public void SmbBitStringUsesDefinitionOrder() { var r = State("smb1r", "achievements.sav", "010\n", "first", "second", "third"); Assert.IsTrue(r.Achievements[1].Unlocked); Assert.IsFalse(r.Achievements[0].Unlocked); }
        [TestMethod] public void SmsUsesRecordedUnixDate() { var r = State("sms", "achievements.txt", "2 1792512020\n", "2"); Assert.IsTrue(r.Achievements[0].Unlocked); Assert.AreEqual(DateTimeKind.Utc, r.Achievements[0].Time.Value.Kind); }
        [TestMethod] public void SmsTornLineFails() => Assert.ThrowsException<InvalidDataException>(() => State("sms", "achievements.txt", "2 1792512020\n3 x", "2", "3"));
        [TestMethod] public void Ps3RecompUsesJsonRatherThanRpcs3Binary() { var r = State("ps3recomp", "NPWR00001.json", "{\"trophies\":[{\"id\":2,\"timestamp\":1792512020}]}", "1", "2"); Assert.IsFalse(r.Achievements[0].Unlocked); Assert.IsTrue(r.Achievements[1].Unlocked); }
        [TestMethod] public void KytyConvertsMicrosecondRtcTick()
        {
            var time = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            var r = State("kytyps5", "trophies_1_0.json", "{\"unlockedTrophies\":[3],\"unlockedAt\":{\"3\":" + time.Ticks / 10 + "}}", "3");
            Assert.AreEqual(time, r.Achievements[0].Time);
        }
        [TestMethod] public void DusklightProgressDoesNotUnlockBeforeFlag()
        {
            var r = new NativeSourceResult { Source = "dusklight", Save = Write("a.json", "{\"goal\":{\"unlocked\":false,\"progress\":7}}"), Achievements = new List<NativeAchievement> { new NativeAchievement { Id = "goal", Goal = 10 } } };
            NativeSourceReader.ReadState(r); Assert.IsFalse(r.Achievements[0].Unlocked); Assert.AreEqual(7, r.Achievements[0].Progress);
        }
        [TestMethod] public void DkrUsesCatalogueVersionAndWhen() { var r = State("dkrr", "progress-v1.json", "{\"schema\":1,\"entries\":{\"1\":{\"unlocked\":true,\"when\":1792512020}}}", "1"); Assert.IsTrue(r.Achievements[0].Unlocked); Assert.IsNotNull(r.Achievements[0].Time); }
        [TestMethod] public void GhostshipUsesGoalInsteadOfAnyPositiveProgress()
        {
            var r = new NativeSourceResult { Source = "ghostship", Save = Write("save_0.json", "{\"shipSaveData\":{\"achievementSaveData\":{\"entries\":[{\"id\":2,\"progress\":4}]}}}"), Achievements = new List<NativeAchievement> { new NativeAchievement { Id = "2", Goal = 5 } } };
            NativeSourceReader.ReadState(r); Assert.IsFalse(r.Achievements[0].Unlocked); Assert.AreEqual(4, r.Achievements[0].Progress);
        }
        [TestMethod] public void XboxTomlConvertsFiletime()
        {
            var date = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            var r = State("x360recomp", "4D5307E6.toml", "[unlocked.2]\nfiletime = " + date.ToFileTimeUtc() + "\n", "2"); Assert.AreEqual(date, r.Achievements[0].Time);
        }
        [TestMethod] public void XboxLegacyTomlSupportsUndatedIds() { var r = State("x360recomp", "4D5307E6.toml", "unlocked = [1, 3]\n", "1", "2", "3"); Assert.IsTrue(r.Achievements[2].Unlocked); Assert.IsNull(r.Achievements[2].Time); }
        [TestMethod] public void XboxProfileTsvRequiresHeader() => Assert.ThrowsException<InvalidDataException>(() => State("x360recomp", "a.tsv", "1\t1792512020", "1"));
        [TestMethod] public void Ps4RuntimeLogsIgnoreUnrelatedNumbers() { var r = State("ps4recomp", "last_run.log", "Frame 123\nRuntime: trophy 2 unlocked\n", "123", "2"); Assert.IsFalse(r.Achievements[0].Unlocked); Assert.IsTrue(r.Achievements[1].Unlocked); }
        [TestMethod] public void Ps4JsonSnapshotUsesItsOwnDates() { var r = State("ps4recomp", "trophies.json", "{\"version\":1,\"unlocked\":[{\"id\":3,\"unix\":1792512020}]}", "3"); Assert.IsTrue(r.Achievements[0].Unlocked); Assert.IsNotNull(r.Achievements[0].Time); }
        [TestMethod] public void EmptyOrTornJsonFails() { Assert.ThrowsException<InvalidDataException>(() => State("ps3recomp", "a.json", "", "1")); Assert.ThrowsException<JsonReaderException>(() => State("ps3recomp", "a.json", "{\"trophies\":[", "1")); }
        [TestMethod] public void JsonDefinitionsAcceptArraysAndObjects()
        {
            var array = NativeSourceReader.JsonSchema(JArray.Parse("[{\"id\":2,\"displayName\":\"Hello\"}]"), _root);
            var obj = NativeSourceReader.JsonSchema(JObject.Parse("{\"achievements\":{\"2\":{\"displayName\":\"Hello\"}}}"), _root);
            Assert.AreEqual("2", array.Single().Id); Assert.AreEqual("Hello", obj.Single().Name);
        }
        [TestMethod] public void TrophyXmlPreservesPlatinumAndHidden()
        {
            var list = NativeTrophyReader.Xml("<trophyconf><trophy id=\"0\" ttype=\"P\" hidden=\"yes\"><name>Done &amp; dusted</name><detail>All</detail></trophy></trophyconf>", _ => null);
            Assert.AreEqual("platinum", list.Single().TrophyType); Assert.IsTrue(list.Single().Hidden); Assert.AreEqual("Done & dusted", list.Single().Name);
        }
        [TestMethod] public void TrophyXmlRejectsExternalEntities() => Assert.ThrowsException<System.Xml.XmlException>(() => NativeTrophyReader.Xml("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/private'>]><trophyconf/>", _ => null));
        [TestMethod] public void AmbiguousProfilesRequireAnExactOverride()
        {
            Write("saves/save_0.json", "{}"); Write("saves/save_1.json", "{}");
            var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "ghostship" }, null);
            Assert.IsFalse(result.Success); StringAssert.Contains(result.Message, "Several unlock/profile");
        }
        [TestMethod] public void ExplicitMissingSaveDoesNotFallBackToOtherSlot()
        {
            Write("saves/save_0.json", "{}");
            var missing = Path.Combine(_root, "saves", "save_1.json");
            var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "ghostship", SavePath = missing }, null);
            Assert.IsFalse(result.Success); Assert.AreEqual(missing, result.Save);
        }
        [TestMethod] public void BundledDefinitionsAreAvailableWithoutNetwork()
        {
            foreach (var source in new[] { "ghostship", "dusklight", "dkrr", "smb1r", "sms", "smseclipse" })
            {
                var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = source }, null, false);
                Assert.IsTrue(result.Success, source + ": " + result.Message); Assert.IsTrue(result.Achievements.Count > 20);
            }
        }
        [TestMethod] public void NativeSettingsRoundTripPreservesPerGameConfiguration()
        {
            var id = Guid.NewGuid();
            var settings = new LocalSettings { NativeSourcesEnabled = false, DisabledNativeSources = new List<string> { "sms" }, NativeSourceOverrides = new Dictionary<Guid, NativeSourceConfiguration> { [id] = new NativeSourceConfiguration { Source = "kytyps5", SavePath = "chosen.json" } } };
            var copy = JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            Assert.IsFalse(copy.NativeSourcesEnabled); Assert.AreEqual("chosen.json", copy.NativeSourceOverrides[id].SavePath); CollectionAssert.Contains(copy.DisabledNativeSources, "sms");
        }
        [TestMethod] public void RejectsOversizedStateFile()
        {
            var path = Write("huge.txt", ""); using (var stream = File.OpenWrite(path)) stream.SetLength(NativeSourceReader.MaxTextBytes + 1);
            Assert.ThrowsException<IOException>(() => NativeSourceReader.Text(path));
        }
        [TestMethod] public void QuiverLibraryHonorsCustomAppsPathAndRejectsTraversal()
        {
            Write("apps.json", "{\"apps\":[{\"name\":\"Ghostship\",\"folderName\":\"ghost\"},{\"name\":\"Bad\",\"folderName\":\"../outside\"}]}");
            Write("settings.json", new JObject { ["AppsPath"] = Path.Combine(_root, "moved") }.ToString());
            var entries = NativeQuiverDiscovery.ReadLibrary(_root);
            Assert.AreEqual(1, entries.Count); Assert.AreEqual(Path.Combine(_root, "moved", "ghost"), entries[0].Value);
        }
        private string Package(string filename, bool ucp, params KeyValuePair<string, byte[]>[] entries)
        {
            var start = ucp ? 96 : filename == "TROPHY.TRP" ? 64 : 96;
            var header = new byte[start + entries.Length * 64 + entries.Sum(e => e.Value.Length)];
            Put32(header, 0, ucp ? 0xb228c60aU : 0xdca24d00U); Put32(header, 4, 1);
            Put32(header, 16, (uint)entries.Length); Put32(header, 20, 64);
            Put32(header, 12, (uint)header.Length);
            var offset = start + entries.Length * 64;
            for (int i = 0; i < entries.Length; i++)
            {
                var at = start + i * 64;
                Encoding.ASCII.GetBytes(entries[i].Key).CopyTo(header, at);
                Put32(header, at + 36, (uint)offset); Put32(header, at + 44, (uint)entries[i].Value.Length);
                entries[i].Value.CopyTo(header, offset); offset += entries[i].Value.Length;
            }
            var file = Path.Combine(_root, filename); File.WriteAllBytes(file, header); return file;
        }
        private static void Put32(byte[] bytes, int at, uint value) { bytes[at] = (byte)(value >> 24); bytes[at + 1] = (byte)(value >> 16); bytes[at + 2] = (byte)(value >> 8); bytes[at + 3] = (byte)value; }
        private static KeyValuePair<string, byte[]> Entry(string name, string text) => new KeyValuePair<string, byte[]>(name, Encoding.UTF8.GetBytes(text));
        [TestMethod] public void Ps3PackageExtractsOriginalTrophyDefinitions()
        {
            var file = Package("TROPHY.TRP", false, Entry("TROP.XML", "<trophyconf><trophy id=\"1\" ttype=\"B\"><name>First</name><detail>Win</detail></trophy></trophyconf>"));
            Assert.AreEqual("First", NativeTrophyReader.Read(file, null).Single().Name);
        }
        [TestMethod] public void UcpPackageCombinesConfigurationWithLocalizedTexts()
        {
            var file = Package("trophy00.ucp", true,
                Entry("tropconf.json", "{\"defaultLanguage\":\"en-US\",\"trophies\":[{\"id\":1,\"grade\":\"G\",\"hidden\":true}]}"),
                Entry("tropmeta_en-US.json", "{\"metadata\":{\"trophyMetadata\":[{\"id\":1,\"name\":\"Gold\",\"detail\":\"Win\"}]}}"));
            var trophy = NativeTrophyReader.Read(file, null).Single(); Assert.AreEqual("Gold", trophy.Name); Assert.AreEqual("gold", trophy.TrophyType); Assert.IsTrue(trophy.Hidden);
        }
        [TestMethod] public void TrophyPackageRejectsEntryOutsideFile()
        {
            var file = Package("trophy00.trp", false, Entry("TROP.XML", "invalid"));
            var bytes = File.ReadAllBytes(file); Put32(bytes, 96 + 36, uint.MaxValue); File.WriteAllBytes(file, bytes);
            Assert.ThrowsException<InvalidDataException>(() => NativeTrophyReader.Container(file, false));
        }
        [TestMethod] public void FullPatchReadUsesResourcesAndSelectedUnlockFile()
        {
            Write("MarkerPatch.ini", "AchievementSupport = 1"); Write("achievements/txt/en.txt", "First|Win\nSecond|Win again\n");
            var save = Write("settings.txt", "Controls.AcL.X = 1\nControls.AcL.Y = 0\n");
            var r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "auto", SavePath = save }, null);
            Assert.IsTrue(r.Success, r.Message); Assert.AreEqual("markerpatch", r.Source); Assert.IsTrue(r.Achievements[0].Unlocked); Assert.IsFalse(r.Achievements[1].Unlocked);
        }
        [TestMethod] public void PatchHexFlagsAndCommentsAreSupported()
        {
            var r = State("markerpatch", "settings.txt", "Controls.AcL.X = 0x80000000 ; low\nControls.AcL.Y = 0 # high\n", "31"); Assert.IsTrue(r.Achievements[0].Unlocked);
        }
        [TestMethod] public void PatchWordOverflowIsRejected() => Assert.ThrowsException<InvalidDataException>(() => State("markerpatch", "settings.txt", "Controls.AcL.X = 4294967296\nControls.AcL.Y = 0\n", "0"));
        [TestMethod] public void DisabledPatchDoesNotReadStaleUnlocks()
        {
            Write("MarkerPatch.ini", "AchievementSupport = false\n");
            var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "markerpatch" }, null);
            Assert.IsFalse(result.Success); StringAssert.Contains(result.Message, "disabled");
        }
        [TestMethod] public void CollectionLogKeepsUnchartedGamesSeparate()
        {
            var r = new NativeSourceResult { Source = "ps4recomp", CollectionGame = "u2", Save = Write("last_run.log", "Preparing eboot.bin\nRuntime: trophy 1 unlocked\nSwitching to big2-ps4_Shipping.elf\nRuntime: trophy 2 unlocked\n"), Achievements = new List<NativeAchievement> { new NativeAchievement { Id = "1" }, new NativeAchievement { Id = "2" } } };
            NativeSourceReader.ReadState(r); Assert.IsFalse(r.Achievements[0].Unlocked); Assert.IsTrue(r.Achievements[1].Unlocked);
        }
        [TestMethod] public void AmbiguousCollectionLogFailsRatherThanMixingGames() => Assert.ThrowsException<InvalidDataException>(() => State("ps4recomp", "last_run.log", "Switching to big2-ps4_Shipping.elf\nRuntime: trophy 2 unlocked\n", "2"));
        [TestMethod] public void SelectingAnotherNativeProfileDoesNotPreserveTheOldProfilesUnlocks()
        {
            var a = NativeSourceIdentity.Key("ghostship", "C:\\game\\save_0.json", "bundled", "");
            var b = NativeSourceIdentity.Key("ghostship", "C:\\game\\save_1.json", "bundled", "");
            Assert.IsTrue(NativeSourceIdentity.IsNewSelection("Local", b, a));
            Assert.IsFalse(NativeSourceIdentity.IsNewSelection("Local", a, a));
            Assert.IsFalse(NativeSourceIdentity.IsNewSelection("Steam", b, a));
            Assert.IsFalse(NativeSourceIdentity.IsNewSelection("Local", "123", a));
            Assert.AreEqual(a, NativeSourceIdentity.Key("ghostship", "c:\\GAME\\save_0.json", "bundled", ""));
        }
        [TestMethod] public void NativeSettingsEditCloneDoesNotModifySavedOverrides()
        {
            var id = Guid.NewGuid(); var saved = new LocalSettings(); saved.NativeSourceOverrides[id] = new NativeSourceConfiguration { Source = "ghostship", SavePath = "save_0.json" };
            var edit = (LocalSettings)saved.Clone(); edit.NativeSourceOverrides[id].SavePath = "save_1.json"; edit.DisabledNativeSources.Add("ghostship");
            Assert.AreEqual("save_0.json", saved.NativeSourceOverrides[id].SavePath); Assert.AreEqual(0, saved.DisabledNativeSources.Count);
        }
        [TestMethod] public void Ps4BinaryFlagsAreLittleEndian()
        {
            var file = Path.Combine(_root, "trophies.dat"); var bytes = new byte[16]; bytes[4] = 1; File.WriteAllBytes(file, bytes);
            var r = new NativeSourceResult { Source = "ps4recomp", Save = file, Achievements = new List<NativeAchievement> { new NativeAchievement { Id = "32" }, new NativeAchievement { Id = "0" } } };
            NativeSourceReader.ReadState(r); Assert.IsTrue(r.Achievements[0].Unlocked); Assert.IsFalse(r.Achievements[1].Unlocked);
        }
        private NativeSourceReadContext Context(NativeSourceLocations locations, string name = "Test Game", string gameId = "") =>
            new NativeSourceReadContext { Locations = locations, GameName = name, GameId = gameId };
        [TestMethod] public void SharedGhostshipSaveFolderAndDefaultSlotAreUsed()
        {
            Write("relocated/save_0.json", "{\"shipSaveData\":{\"achievementSaveData\":{\"entries\":[{\"id\":\"Get1Star\",\"progress\":1}]}}}");
            Write("relocated/save_1.json", "{}");
            var r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "ghostship" }, null, context: Context(new NativeSourceLocations { SaveFolders = Path.Combine(_root, "relocated"), Profile = "save_0" }));
            Assert.IsTrue(r.Success, r.Message); StringAssert.EndsWith(r.Save, "save_0.json"); Assert.IsTrue(r.Achievements.Single(a => a.Id == "Get1Star").Unlocked);
        }
        [TestMethod] public void PerGameFileOverridesSharedDefaultSlot()
        {
            var save = Write("selected.json", "{\"shipSaveData\":{\"achievementSaveData\":{\"entries\":[]}}}");
            var r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "ghostship", SavePath = save }, null, context: Context(new NativeSourceLocations { SaveFolders = Path.Combine(_root, "missing"), Profile = "save_3" }));
            Assert.IsTrue(r.Success, r.Message); Assert.AreEqual(save, r.Save);
        }
        [TestMethod] public void ConfiguredMissingSaveRootDoesNotAdoptTheOldPortableSave()
        {
            Write("saves/save_0.json", "{\"shipSaveData\":{\"achievementSaveData\":{\"entries\":[]}}}");
            var r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "ghostship" }, null, context: Context(new NativeSourceLocations { SaveFolders = Path.Combine(_root, "unplugged-drive") }));
            Assert.IsFalse(r.Success); Assert.IsNull(r.Save);
        }
        [TestMethod] public void SharedPs3SaveLibraryMatchesTheSchemasNpwrIdentity()
        {
            var original = Package("TROPHY.TRP", false, Entry("TROP.XML", "<trophyconf><trophy id=\"1\" ttype=\"B\"><name>First</name></trophy></trophyconf>"));
            var schemaDir = Path.Combine(_root, "game", "TROPDIR", "NPWR00001_00"); Directory.CreateDirectory(schemaDir);
            var schema = Path.Combine(schemaDir, "TROPHY.TRP"); File.Copy(original, schema);
            var wanted = Write("shared-trophies/NPWR00001.json", "{\"trophies\":[{\"id\":1,\"timestamp\":1792512020}]}");
            Write("shared-trophies/NPWR00002.json", "{\"trophies\":[]}");
            var r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "ps3recomp", SchemaPath = schema }, null, context: Context(new NativeSourceLocations { SaveFolders = Path.Combine(_root, "shared-trophies") }));
            Assert.IsTrue(r.Success, r.Message); Assert.AreEqual(wanted, r.Save);
        }
        [TestMethod] public void SharedSaveLibraryNeverBorrowsAnotherGamesOnlyFile()
        {
            Write("shared/Other Game/achievements/12345678.toml", "unlocked = [1]\n");
            var r = new NativeSourceResult { Source = "x360recomp", Root = _root };
            var files = NativeLocationFiles.Saves(r, new NativeSourceConfiguration(), Context(new NativeSourceLocations { SaveFolders = Path.Combine(_root, "shared") }, "Wanted Game", "87654321")).ToList();
            Assert.AreEqual(0, files.Count);
        }
        [TestMethod] public void SharedXboxSaveLibraryMatchesTitleId()
        {
            var wanted = Write("shared/87654321/player.tsv", "port\t87654321\t123\n");
            Write("shared/12345678/player.tsv", "port\t12345678\t123\n");
            var r = new NativeSourceResult { Source = "x360recomp", Root = _root };
            var files = NativeLocationFiles.Saves(r, new NativeSourceConfiguration(), Context(new NativeSourceLocations { SaveFolders = Path.Combine(_root, "shared") }, "Wanted Game", "87654321")).ToList();
            CollectionAssert.AreEqual(new[] { wanted }, files);
        }
        [TestMethod] public void SharedDefinitionLibrarySelectsOnlyMatchingGameFolder()
        {
            var wanted = Write("dumps/Wanted Game/trophies.tsv", "1\tB\t0\tFirst\tWin\n"); Write("dumps/Other Game/trophies.tsv", "1\tB\t0\tOther\tOther\n");
            var files = NativeLocationFiles.Definitions(new NativeSourceResult { Source = "ps4recomp", Root = _root }, Context(new NativeSourceLocations { DefinitionFolders = Path.Combine(_root, "dumps") }, "Wanted Game")).ToList();
            CollectionAssert.AreEqual(new[] { wanted }, files);
        }
        [TestMethod] public void GameLibraryDiscoveryFindsTheExactGameAndRejectsDuplicateInstalls()
        {
            var a = Write("library-a/Wanted Game/ps3recomp.toml", "");
            Assert.AreEqual(Path.GetDirectoryName(a), NativeLocationDiscovery.FindInstall(null, "Wanted Game", "", "auto", new[] { Path.Combine(_root, "library-a") }));
            Write("library-b/Wanted Game/ps3recomp.toml", "");
            Assert.IsNull(NativeLocationDiscovery.FindInstall(null, "Wanted Game", "", "auto", new[] { Path.Combine(_root, "library-a"), Path.Combine(_root, "library-b") }));
        }
        [TestMethod] public void KytyIniFollowsPerGameUserBeforeGlobalUser()
        {
            var game = Path.Combine(_root, "Game");
            var text = "[Launcher]\ngame_dirs=\"" + _root.Replace("\\", "\\\\") + "\"\n[GlobalConfiguration]\nuser_id=1\n[GameConfigurations]\n1\\game_path=\"" + game.Replace("\\", "\\\\") + "\"\n1\\user_id=2\n";
            var settings = NativeKytySettings.Parse(_root, text);
            Assert.AreEqual("2", settings.ActiveUser(game)); Assert.AreEqual("1", settings.ActiveUser(Path.Combine(_root, "Other"))); CollectionAssert.Contains(settings.GameFolders, game);
        }
        [TestMethod] public void KytyIniRefusesMalformedUserId() => Assert.ThrowsException<InvalidDataException>(() => NativeKytySettings.Parse(_root, "[GlobalConfiguration]\nuser_id=bad\n"));
        [TestMethod] public void KytyFolderNormalizerAcceptsTrophyAndTitleFolders()
        {
            var emulator = Path.Combine(_root, "emu");
            Assert.AreEqual(emulator, NativeKytySettings.EmulatorRoot(Path.Combine(emulator, "_Trophies")));
            Assert.AreEqual(emulator, NativeKytySettings.EmulatorRoot(Path.Combine(emulator, "_Trophies", "PPSA00001")));
        }
        private NativeSourceResult KytyWithSharedFolders(string perGameUser = "", string globalOverride = "")
        {
            var package = Package("trophy00.ucp", true, Entry("tropconf.json", "{\"defaultLanguage\":\"en-US\",\"trophies\":[{\"id\":1,\"grade\":\"B\"}]}"), Entry("tropmeta_en-US.json", "{\"metadata\":{\"trophyMetadata\":[{\"id\":1,\"name\":\"First\"}]}}"));
            Write("sce_sys/param.json", "{\"titleId\":\"PPSA00001\"}");
            Write("emu/Kyty.ini", "[GlobalConfiguration]\nuser_id=1\n");
            Write("emu/_Trophies/PPSA00001/trophies_1_0.json", "{\"unlockedTrophies\":[],\"unlockedAt\":{}}");
            Write("emu/_Trophies/PPSA00001/trophies_2_0.json", "{\"unlockedTrophies\":[1],\"unlockedAt\":{}}");
            Write("emu/_Trophies/PPSA00002/trophies_1_0.json", "{\"unlockedTrophies\":[1]}");
            return NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "kytyps5", SchemaPath = package, UserId = perGameUser }, null,
                context: Context(new NativeSourceLocations { EmulatorFolders = Path.Combine(_root, "emu"), UserId = globalOverride }));
        }
        [TestMethod] public void KytyEmulatorFolderResolvesOnlyTheActiveUsersOwnGame()
        {
            var r = KytyWithSharedFolders(); Assert.IsTrue(r.Success, r.Message); StringAssert.EndsWith(r.Save, "trophies_1_0.json"); Assert.IsFalse(r.Achievements[0].Unlocked);
        }
        [TestMethod] public void KytySharedUserOverrideTakesPriorityOverIni()
        {
            var r = KytyWithSharedFolders(globalOverride: "2"); Assert.IsTrue(r.Success, r.Message); StringAssert.EndsWith(r.Save, "trophies_2_0.json"); Assert.IsTrue(r.Achievements[0].Unlocked);
        }
        [TestMethod] public void KytyPerGameUserOverrideTakesPriorityOverSharedUser()
        {
            var r = KytyWithSharedFolders(perGameUser: "1", globalOverride: "2"); Assert.IsTrue(r.Success, r.Message); StringAssert.EndsWith(r.Save, "trophies_1_0.json"); Assert.IsFalse(r.Achievements[0].Unlocked);
        }
        [TestMethod] public void SharedLocationEditCloneDoesNotModifySavedLocations()
        {
            var saved = new LocalSettings { NativeGameLibraryFolders = _root }; saved.NativeSourceLocations["kytyps5"] = new NativeSourceLocations { EmulatorFolders = "old", UserId = "1" };
            var edit = (LocalSettings)saved.Clone(); edit.NativeSourceLocations["kytyps5"].EmulatorFolders = "new"; edit.NativeGameLibraryFolders = "changed";
            Assert.AreEqual("old", saved.NativeSourceLocations["kytyps5"].EmulatorFolders); Assert.AreEqual(_root, saved.NativeGameLibraryFolders);
        }
        [TestMethod] public void KytyRelocatedTrophyRootFollowsIniUserAndReplacesTheOldRoot()
        {
            var r = KytyWithSharedFolders();
            Write("moved/PPSA00001/trophies_1_0.json", "{\"unlockedTrophies\":[1],\"unlockedAt\":{}}");
            Write("moved/PPSA00001/trophies_2_0.json", "{\"unlockedTrophies\":[],\"unlockedAt\":{}}");
            var context = Context(new NativeSourceLocations { EmulatorFolders = Path.Combine(_root, "emu"), SaveFolders = Path.Combine(_root, "moved") });
            r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "kytyps5", SchemaPath = Path.Combine(_root, "trophy00.ucp") }, null, context: context);
            Assert.IsTrue(r.Success, r.Message); StringAssert.Contains(r.Save, "moved"); StringAssert.EndsWith(r.Save, "trophies_1_0.json"); Assert.IsTrue(r.Achievements[0].Unlocked);
        }
        [TestMethod] public void PerGameProfileOverridesSharedProfile()
        {
            Write("moved/save_0.json", "{}"); var selected = Write("moved/save_1.json", "{\"shipSaveData\":{\"achievementSaveData\":{\"entries\":[]}}}");
            var r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "ghostship", Profile = "save_1" }, null, context: Context(new NativeSourceLocations { SaveFolders = Path.Combine(_root, "moved"), Profile = "save_0" }));
            Assert.IsTrue(r.Success, r.Message); Assert.AreEqual(selected, r.Save);
        }
        [TestMethod] public void KytyCustomIniOverridesThePortableIni()
        {
            var r = KytyWithSharedFolders(); var ini = Write("settings/Kyty.ini", "[GlobalConfiguration]\nuser_id=2\n");
            r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "kytyps5", SchemaPath = Path.Combine(_root, "trophy00.ucp") }, null,
                context: Context(new NativeSourceLocations { EmulatorFolders = Path.Combine(_root, "emu"), SettingsFile = ini }));
            Assert.IsTrue(r.Success, r.Message); StringAssert.EndsWith(r.Save, "trophies_2_0.json");
        }
        [TestMethod] public void MissingKytyIniOverrideFailsRatherThanChangingUsers()
        {
            var r = KytyWithSharedFolders();
            r = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "kytyps5", SchemaPath = Path.Combine(_root, "trophy00.ucp") }, null,
                context: Context(new NativeSourceLocations { EmulatorFolders = Path.Combine(_root, "emu"), SettingsFile = Path.Combine(_root, "unplugged", "Kyty.ini") }));
            Assert.IsFalse(r.Success); StringAssert.Contains(r.Message, "Configured Kyty.ini not found");
        }
        [TestMethod] public void KytyQtSettingsListPreservesQuotedCommasAndEscapedCharacters()
        {
            var paths = NativeKytySettings.List("\"C:\\\\Games, Ports\", \"D:\\\\Pok\\xe9mon\"").ToArray();
            Assert.AreEqual("C:\\Games, Ports", paths[0]); Assert.AreEqual("D:\\Pokémon", paths[1]);
        }
    }
}
