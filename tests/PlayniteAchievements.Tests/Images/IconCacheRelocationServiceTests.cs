using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images.Relocation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Images.Tests
{
    [TestClass]
    public class IconCacheRelocationServiceTests
    {
        private string _temp;
        private string _pluginData;
        private IconCacheLocation _location;
        private FakeHost _host;
        private IconCacheWriteGate _gate;
        private IconCacheRelocationService _service;

        [TestInitialize]
        public void SetUp()
        {
            _temp = Path.Combine(Path.GetTempPath(), "pa-iconmove-" + Guid.NewGuid().ToString("N"));
            _pluginData = Path.Combine(_temp, "data");
            Directory.CreateDirectory(_pluginData);
            _location = new IconCacheLocation(_pluginData, null);
            _host = new FakeHost();
            _gate = new IconCacheWriteGate();
            _service = new IconCacheRelocationService(_location, _gate, _host, null);
            WriteFile(Path.Combine(_location.Root, "game", "original", "a.png"), 3);
            WriteFile(Path.Combine(_location.Root, "game", "custom", "b.png"), 5);
        }

        [TestCleanup]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        [TestMethod]
        public async Task Move_OnOneVolumeRenamesAndPointsThePluginAtTheTarget()
        {
            var oldRoot = _location.Root;
            var target = Path.Combine(_temp, "icons");
            var changes = new List<IconCacheRootChangedEventArgs>();
            _location.RootChanged += (_, e) => changes.Add(e);

            var plan = _service.Plan(target);
            Assert.AreEqual(IconCacheMoveStrategy.Rename, plan.Strategy);
            var result = await _service.MoveAsync(plan, null, CancellationToken.None);

            Assert.AreEqual(IconCacheMoveOutcome.Moved, result.Outcome);
            Assert.AreEqual(target, _location.Root);
            Assert.IsTrue(File.Exists(Path.Combine(target, "game", "custom", "b.png")));
            Assert.IsFalse(Directory.Exists(oldRoot));
            Assert.AreEqual(1, changes.Count);
            CollectionAssert.Contains(_host.OldRoots.ToList(), oldRoot);
            Assert.AreEqual(target, _host.NewRoot);
            Assert.AreEqual(target, _host.SettingValue);
            Assert.IsNull(IconCacheRelocationJournal.TryRead(_pluginData));
            Assert.IsTrue(_gate.IsOpen);
            Assert.AreEqual(1, _host.Released);
        }

        [TestMethod]
        public async Task Move_FileByFileThenBackToTheDefaultFolder()
        {
            var oldRoot = _location.Root;
            var target = Path.Combine(_temp, "icons");
            var plan = _service.Plan(target);
            plan.Strategy = IconCacheMoveStrategy.PerFile;

            Assert.AreEqual(IconCacheMoveOutcome.Moved, (await _service.MoveAsync(plan, null, CancellationToken.None)).Outcome);
            Assert.IsTrue(File.Exists(Path.Combine(target, "game", "original", "a.png")));
            Assert.IsFalse(Directory.Exists(oldRoot));

            var back = _service.Plan(_location.DefaultRootPath);
            back.Strategy = IconCacheMoveStrategy.PerFile;
            Assert.AreEqual(IconCacheMoveOutcome.Moved, (await _service.MoveAsync(back, null, CancellationToken.None)).Outcome);
            Assert.AreEqual(oldRoot, _location.Root);
            Assert.IsFalse(_location.IsCustom);
            Assert.AreEqual(string.Empty, _host.SettingValue);
            Assert.IsTrue(File.Exists(Path.Combine(oldRoot, "game", "original", "a.png")));
            Assert.IsFalse(Directory.Exists(target));
        }

        [TestMethod]
        public async Task Cancel_PutsEveryFileBack()
        {
            var oldRoot = _location.Root;
            var target = Path.Combine(_temp, "icons");
            var plan = _service.Plan(target);
            plan.Strategy = IconCacheMoveStrategy.PerFile;
            using (var cancel = new CancellationTokenSource())
            {
                var result = await _service.MoveAsync(
                    plan,
                    (text, done, total) =>
                    {
                        if (done >= 1)
                        {
                            cancel.Cancel();
                        }
                    },
                    cancel.Token);

                Assert.AreEqual(IconCacheMoveOutcome.Cancelled, result.Outcome);
            }

            Assert.AreEqual(oldRoot, _location.Root);
            Assert.IsTrue(File.Exists(Path.Combine(oldRoot, "game", "original", "a.png")));
            Assert.IsTrue(File.Exists(Path.Combine(oldRoot, "game", "custom", "b.png")));
            Assert.IsFalse(Directory.Exists(target));
            Assert.IsNull(_host.NewRoot);
            Assert.IsNull(IconCacheRelocationJournal.TryRead(_pluginData));
            Assert.IsTrue(_gate.IsOpen);
        }

        [TestMethod]
        public async Task Move_WaitsForNoRefresh()
        {
            _host.RefreshRunning = true;

            var result = await _service.MoveAsync(_service.Plan(Path.Combine(_temp, "icons")), null, CancellationToken.None);

            Assert.AreEqual(IconCacheMoveOutcome.RefreshRunning, result.Outcome);
            Assert.IsTrue(Directory.Exists(_location.Root));
            Assert.IsTrue(_gate.IsOpen);
        }

        [TestMethod]
        public async Task Move_CopiesAFileHeldOpenAndLeavesTheSourceBehind()
        {
            var oldRoot = _location.Root;
            var held = Path.Combine(oldRoot, "game", "original", "a.png");
            var target = Path.Combine(_temp, "icons");
            var plan = _service.Plan(target);
            plan.Strategy = IconCacheMoveStrategy.PerFile;

            IconCacheMoveResult result;
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = await _service.MoveAsync(plan, null, CancellationToken.None);
            }

            Assert.AreEqual(IconCacheMoveOutcome.Moved, result.Outcome);
            Assert.AreEqual(target, _location.Root);
            Assert.IsTrue(File.Exists(Path.Combine(target, "game", "original", "a.png")));
            Assert.IsTrue(File.Exists(Path.Combine(target, "game", "custom", "b.png")));
            Assert.IsTrue(File.Exists(held), "a file held open stays in the old folder");
        }

        [TestMethod]
        public void Resume_FinishesAMoveTheLastSessionStarted()
        {
            var oldRoot = _location.Root;
            var target = Path.Combine(_temp, "icons");
            WriteFile(Path.Combine(target, "game", "original", "a.png"), 3);
            File.Delete(Path.Combine(oldRoot, "game", "original", "a.png"));
            new IconCacheRelocationJournal
            {
                Phase = IconCacheRelocationPhase.Moving,
                CurrentRoot = oldRoot,
                SourceFolder = oldRoot,
                TargetFolder = target,
                Strategy = IconCacheMoveStrategy.PerFile,
                SettingValue = target
            }.Write(_pluginData);

            var location = IconCacheRelocationService.CreateStartupLocation(_pluginData, null, out var journal);
            Assert.IsFalse(location.IsAvailable);
            var gate = new IconCacheWriteGate();
            gate.Close();
            var service = new IconCacheRelocationService(location, gate, _host, null);

            var result = service.Resume(journal, null);

            Assert.AreEqual(IconCacheMoveOutcome.Moved, result.Outcome);
            Assert.AreEqual(target, location.Root);
            Assert.IsTrue(location.IsAvailable);
            Assert.IsTrue(gate.IsOpen);
            Assert.IsTrue(File.Exists(Path.Combine(target, "game", "custom", "b.png")));
            Assert.IsFalse(Directory.Exists(oldRoot));
            Assert.IsNull(IconCacheRelocationJournal.TryRead(_pluginData));
        }

        [TestMethod]
        public void Commit_KeepsTheJournalUntilTheSettingsAreSaved()
        {
            _host.SavesSettings = false;
            var target = Path.Combine(_temp, "icons");

            var result = _service.MoveAsync(_service.Plan(target), null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(IconCacheMoveOutcome.Moved, result.Outcome);
            Assert.AreEqual(IconCacheRelocationPhase.Committed, IconCacheRelocationJournal.TryRead(_pluginData).Phase);
            _service.CompleteCommittedJournal("something else");
            Assert.IsNotNull(IconCacheRelocationJournal.TryRead(_pluginData));
            _service.CompleteCommittedJournal(target);
            Assert.IsNull(IconCacheRelocationJournal.TryRead(_pluginData));
        }

        [TestMethod]
        public void CustomDataStore_RewritesStoredIconPathsAndCapstoneArt()
        {
            var oldRoot = Path.Combine(_temp, "old");
            var newRoot = Path.Combine(_temp, "new");
            var gameId = Guid.NewGuid();
            var store = new GameCustomDataStore(_pluginData);
            store.Save(gameId, new GameCustomDataFile
            {
                PlayniteGameId = gameId,
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["moved"] = new AchievementOverride { UnlockedIconPath = Path.Combine(oldRoot, gameId.ToString(), "custom", "a.png") },
                    ["kept"] = new AchievementOverride { UnlockedIconPath = @"E:\elsewhere\b.png" }
                }
            });
            store.SetCapstoneArtRecord(gameId, "capstone", new CapstoneArtRecordRow
            {
                ApiName = "capstone",
                Path = Path.Combine(oldRoot, gameId.ToString(), "custom", "capstone.png")
            });

            var rewritten = store.RewriteIconCacheRoot(oldRoot, newRoot);

            Assert.AreEqual(2, rewritten);
            Assert.IsTrue(store.TryLoad(gameId, out var loaded));
            Assert.AreEqual(Path.Combine(newRoot, gameId.ToString(), "custom", "a.png"), loaded.AchievementOverrides["moved"].UnlockedIconPath);
            Assert.AreEqual(@"E:\elsewhere\b.png", loaded.AchievementOverrides["kept"].UnlockedIconPath);
            Assert.AreEqual(
                Path.Combine(newRoot, gameId.ToString(), "custom", "capstone.png"),
                store.LoadAllCapstoneArtRecords().Single().Path);
            Assert.AreEqual(0, store.RewriteIconCacheRoot(oldRoot, newRoot));
        }

        private static void WriteFile(string path, int length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[length]);
        }

        private sealed class FakeHost : IIconCacheRelocationHost
        {
            public bool RefreshRunning;
            public bool SavesSettings = true;
            public int Released;
            public IReadOnlyList<string> OldRoots = Array.Empty<string>();
            public string NewRoot;
            public string SettingValue;

            public bool TryReserveRefreshes() => !RefreshRunning;

            public void ReleaseRefreshes() => Released++;

            public bool CommitStoredPaths(IReadOnlyList<string> oldRoots, string newRoot, string settingValue)
            {
                OldRoots = oldRoots;
                NewRoot = newRoot;
                SettingValue = settingValue;
                return SavesSettings;
            }
        }
    }
}
