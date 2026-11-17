using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Images.Relocation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Images.Tests
{
    [TestClass]
    public class IconCacheRelocationTests
    {
        private const string PluginData = @"C:\Data\Plugin";
        private const string CustomRoot = @"D:\Icons";

        [TestMethod]
        public void Location_MapsPathsUnderACustomRootToTheLogicalForm()
        {
            var location = new IconCacheLocation(PluginData, CustomRoot + @"\");

            Assert.AreEqual(CustomRoot, location.Root);
            Assert.IsTrue(location.IsCustom);
            Assert.IsTrue(location.TryMakeLogical(@"d:\ICONS\game\original\a.png", out var logical));
            Assert.AreEqual(@"icon_cache\game\original\a.png", logical);
            Assert.AreEqual(@"D:\Icons\game\original\a.png", location.ResolveLogical(@"icon_cache\game\original\a.png"));
            Assert.AreEqual(@"D:\Icons\game\a.png", location.ResolveLogical("icon_cache/game/a.png"));
            Assert.AreEqual(@"C:\Data\Plugin\other\x.png", location.ResolveLogical(@"other\x.png"));
            Assert.IsFalse(location.TryMakeLogical(@"D:\IconsOther\a.png", out _));
            Assert.IsFalse(location.TryMakeLogical(@"C:\Data\Plugin\icon_cache\a.png", out _));
        }

        [TestMethod]
        public void Location_DefaultsToIconCacheInPluginData()
        {
            var location = new IconCacheLocation(PluginData + @"\", null);

            Assert.AreEqual(@"C:\Data\Plugin\icon_cache", location.Root);
            Assert.IsFalse(location.IsCustom);
            Assert.IsTrue(location.IsAvailable);
        }

        [TestMethod]
        public void ChangeRoot_RaisesOnceWithBothRoots()
        {
            var location = new IconCacheLocation(PluginData, null);
            location.MarkUnavailable();
            var raised = new List<IconCacheRootChangedEventArgs>();
            location.RootChanged += (_, e) => raised.Add(e);

            location.ChangeRoot(CustomRoot);
            location.ChangeRoot(CustomRoot);

            Assert.AreEqual(1, raised.Count);
            Assert.AreEqual(@"C:\Data\Plugin\icon_cache", raised[0].OldRoot);
            Assert.AreEqual(CustomRoot, raised[0].NewRoot);
            Assert.IsTrue(location.IsAvailable);
        }

        [TestMethod]
        public void Classification_FollowsACustomRootWithoutTheFolderName()
        {
            var previous = IconCacheLocation.Current;
            IconCacheLocation.SetCurrent(new IconCacheLocation(PluginData, CustomRoot));
            try
            {
                Assert.IsTrue(AchievementIconCachePathBuilder.IsCachedIconPath(@"D:\Icons\game\original\a.png"));
                Assert.IsTrue(AchievementIconCachePathBuilder.IsCustomIconPath(@"D:\Icons\game\custom\a.png"));
                Assert.IsFalse(AchievementIconCachePathBuilder.IsCachedIconPath(@"E:\Elsewhere\a.png"));
                Assert.IsTrue(AchievementIconCachePathBuilder.IsMetadataSourceIconPath(@"D:\Icons\metadata\exophase\list_1\a.png", "game"));
                Assert.IsTrue(AchievementIconCachePathBuilder.IsMetadataSourceIconPath(@"D:\Icons\game\metadata\exophase\a.png", "game"));
                Assert.IsFalse(AchievementIconCachePathBuilder.IsMetadataSourceIconPath(@"D:\Icons\game\original\a.png", "game"));
            }
            finally
            {
                IconCacheLocation.SetCurrent(previous);
            }
        }

        [TestMethod]
        public void Classification_WithoutALocationUsesTheFolderName()
        {
            var previous = IconCacheLocation.Current;
            IconCacheLocation.SetCurrent(null);
            try
            {
                Assert.IsTrue(AchievementIconCachePathBuilder.IsCachedIconPath(@"C:\Data\icon_cache\game\original\a.png"));
                Assert.IsTrue(AchievementIconCachePathBuilder.IsMetadataSourceIconPath(@"C:\Data\icon_cache\metadata\src\list\a.png", null));
                Assert.IsFalse(AchievementIconCachePathBuilder.IsCachedIconPath(@"C:\Data\my_icon_cache_x\a.png"));
            }
            finally
            {
                IconCacheLocation.SetCurrent(previous);
            }
        }

        [TestMethod]
        public void StoredPathMapping_StoresIconPathsLogicallyAndOthersRelativeToPluginData()
        {
            Assert.AreEqual(@"icon_cache\g\a.png", StoredPathMapping.ToStored(@"D:\Icons\g\a.png", PluginData, CustomRoot));
            Assert.AreEqual(@"showcase\a.png", StoredPathMapping.ToStored(@"C:\Data\Plugin\showcase\a.png", PluginData, CustomRoot));
            Assert.AreEqual(@"E:\x.png", StoredPathMapping.ToStored(@"E:\x.png", PluginData, CustomRoot));
            Assert.AreEqual("https://x/a.png", StoredPathMapping.ToStored("https://x/a.png", PluginData, CustomRoot));

            Assert.AreEqual(@"D:\Icons\g\a.png", StoredPathMapping.FromStored(@"icon_cache\g\a.png", PluginData, CustomRoot));
            Assert.AreEqual(@"C:\Data\Plugin\showcase\a.png", StoredPathMapping.FromStored(@"showcase\a.png", PluginData, CustomRoot));
            Assert.AreEqual(@"C:\Data\Plugin\icon_cache\g\a.png", StoredPathMapping.FromStored(@"icon_cache\g\a.png", PluginData, null));
        }

        [TestMethod]
        public void RebaseJson_RewritesOnlyPathsUnderTheOldRoot()
        {
            var json = "{\"Icon\":\"C:\\\\Data\\\\Plugin\\\\icon_cache\\\\g\\\\custom\\\\a.png\"," +
                       "\"Other\":\"E:\\\\keep.png\",\"When\":\"2024-01-02T03:04:05Z\",\"Rate\":1.50," +
                       "\"List\":[{\"Path\":\"c:\\\\data\\\\plugin\\\\icon_cache\\\\b.png\"}]}";

            Assert.IsTrue(IconCachePathRebaser.TryRebaseJson(json, @"C:\Data\Plugin\icon_cache", CustomRoot, out var rebased));

            StringAssert.Contains(rebased, "\"Icon\":\"D:\\\\Icons\\\\g\\\\custom\\\\a.png\"");
            StringAssert.Contains(rebased, "\"Path\":\"D:\\\\Icons\\\\b.png\"");
            StringAssert.Contains(rebased, "\"Other\":\"E:\\\\keep.png\"");
            StringAssert.Contains(rebased, "\"When\":\"2024-01-02T03:04:05Z\"");
            StringAssert.Contains(rebased, "\"Rate\":1.50");
            Assert.IsFalse(IconCachePathRebaser.TryRebaseJson(rebased, @"C:\Data\Plugin\icon_cache", CustomRoot, out _));
        }

        [TestMethod]
        public void Rebaser_SkipsTextThatCannotBeAPath()
        {
            // A note may hold characters no path can, which Path.IsPathRooted throws on.
            var json = "{\"Note\":\"say \\\"hi\\\" <b>|</b>\",\"Ctrl\":\"a\\u0001b\"," +
                       "\"Icon\":\"C:\\\\Data\\\\Plugin\\\\icon_cache\\\\a.png\"}";

            Assert.IsTrue(IconCachePathRebaser.TryRebaseJson(json, @"C:\Data\Plugin\icon_cache", CustomRoot, out var rebased));
            StringAssert.Contains(rebased, "\"Icon\":\"D:\\\\Icons\\\\a.png\"");
            StringAssert.Contains(rebased, "<b>|</b>");
            Assert.AreEqual("say \"hi\"", IconCachePathRebaser.RebasePath("say \"hi\"", @"C:\Data\Plugin\icon_cache", CustomRoot));
            Assert.IsFalse(IconCacheLocation.IsRooted(@"C:\a<b"));
        }

        [TestMethod]
        public void Planner_RejectsUnsafeTargets()
        {
            var env = new FakeEnvironment();
            env.Folders.Add(@"C:\Data\Plugin\icon_cache");
            env.Folders.Add(@"E:\Full");
            env.NonEmpty.Add(@"E:\Full");
            const string root = @"C:\Data\Plugin\icon_cache";

            Assert.AreEqual(IconCacheMoveProblem.NoTarget, IconCacheMovePlanner.Plan(root, " ", env).Problem);
            Assert.AreEqual(IconCacheMoveProblem.SameFolder, IconCacheMovePlanner.Plan(root, root + @"\", env).Problem);
            Assert.AreEqual(IconCacheMoveProblem.TargetIsDriveRoot, IconCacheMovePlanner.Plan(root, @"E:\", env).Problem);
            Assert.AreEqual(IconCacheMoveProblem.TargetInsideCache, IconCacheMovePlanner.Plan(root, root + @"\sub", env).Problem);
            Assert.AreEqual(IconCacheMoveProblem.CacheInsideTarget, IconCacheMovePlanner.Plan(root, @"C:\Data", env).Problem);
            Assert.AreEqual(IconCacheMoveProblem.TargetNotEmpty, IconCacheMovePlanner.Plan(root, @"E:\Full", env).Problem);
            Assert.AreEqual(IconCacheMoveProblem.TargetUnreachable, IconCacheMovePlanner.Plan(root, @"Q:\Nowhere\Icons", env).Problem);
        }

        [TestMethod]
        public void Planner_RenamesOnOneVolumeAndMovesFilesAcrossVolumes()
        {
            var env = new FakeEnvironment();
            env.Folders.Add(@"C:\Data\Plugin\icon_cache");
            env.Folders.Add(@"C:\Other");
            env.Folders.Add(@"D:\Games");
            env.Volumes["C"] = 1;
            env.Volumes["D"] = 2;
            const string root = @"C:\Data\Plugin\icon_cache";

            var same = IconCacheMovePlanner.Plan(root, @"C:\Other\Icons", env);
            var across = IconCacheMovePlanner.Plan(root, @"D:\Games\Icons", env);

            Assert.IsTrue(same.IsValid);
            Assert.AreEqual(IconCacheMoveStrategy.Rename, same.Strategy);
            Assert.IsTrue(across.IsValid);
            Assert.AreEqual(IconCacheMoveStrategy.PerFile, across.Strategy);
        }

        [TestMethod]
        public void Planner_MovesAJunctionsTargetAndRemovesTheLink()
        {
            var env = new FakeEnvironment();
            env.Folders.Add(@"C:\Data\Plugin\icon_cache");
            env.Folders.Add(@"D:\Real");
            env.Folders.Add(@"D:\New");
            env.Links[@"C:\Data\Plugin\icon_cache"] = @"D:\Real";
            env.Volumes["C"] = 1;
            env.Volumes["D"] = 2;
            const string root = @"C:\Data\Plugin\icon_cache";

            var move = IconCacheMovePlanner.Plan(root, @"D:\New\Icons", env);
            var relink = IconCacheMovePlanner.Plan(root, @"D:\Real", env);

            Assert.AreEqual(@"D:\Real", move.SourceFolder);
            Assert.AreEqual(root, move.LinkToRemove);
            Assert.AreEqual(IconCacheMoveStrategy.Rename, move.Strategy);
            Assert.IsTrue(relink.IsValid);
            Assert.AreEqual(IconCacheMoveStrategy.RelinkOnly, relink.Strategy);
        }

        [TestMethod]
        public void TreeMover_MovesEveryFileAndKeepsHardLinks()
        {
            var temp = CreateTempFolder();
            try
            {
                var source = Path.Combine(temp, "source");
                var target = Path.Combine(temp, "target");
                Directory.CreateDirectory(Path.Combine(source, "game", "original"));
                var first = Path.Combine(source, "game", "original", "a.png");
                var second = Path.Combine(source, "game", "original", "b.png");
                File.WriteAllBytes(first, new byte[] { 1, 2, 3 });
                Assert.IsTrue(IconFileLinks.TryCreateLink(second, first));
                File.WriteAllBytes(Path.Combine(source, "loose.png"), new byte[] { 4 });
                File.WriteAllBytes(Path.Combine(source, "x" + IconFileLinks.TempSuffix), new byte[] { 5 });

                var moved = IconCacheTreeMover.MoveTree(source, target, null, CancellationToken.None);

                Assert.AreEqual(3, moved.Moved);
                Assert.AreEqual(0, moved.Leftovers.Count);
                Assert.IsFalse(Directory.Exists(source));
                Assert.IsTrue(File.Exists(Path.Combine(target, "loose.png")));
                Assert.IsTrue(IconFileLinks.TryGetIdentity(Path.Combine(target, "game", "original", "a.png"), out var a, out var links));
                Assert.IsTrue(IconFileLinks.TryGetIdentity(Path.Combine(target, "game", "original", "b.png"), out var b, out _));
                Assert.AreEqual(a, b);
                Assert.AreEqual(2, links);
                Assert.IsFalse(File.Exists(Path.Combine(target, "x" + IconFileLinks.TempSuffix)));
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        [TestMethod]
        public void TreeMover_FinishesARunThatStoppedPartWay()
        {
            var temp = CreateTempFolder();
            try
            {
                var source = Path.Combine(temp, "source");
                var target = Path.Combine(temp, "target");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                File.WriteAllBytes(Path.Combine(source, "copied.png"), new byte[] { 1, 2 });
                File.WriteAllBytes(Path.Combine(target, "copied.png"), new byte[] { 1, 2 });
                File.WriteAllBytes(Path.Combine(source, "partial.png"), new byte[] { 1, 2, 3, 4 });
                File.WriteAllBytes(Path.Combine(target, "partial.png"), new byte[] { 1 });

                IconCacheTreeMover.MoveTree(source, target, null, CancellationToken.None);

                Assert.IsFalse(Directory.Exists(source));
                Assert.AreEqual(2, new FileInfo(Path.Combine(target, "copied.png")).Length);
                Assert.AreEqual(4, new FileInfo(Path.Combine(target, "partial.png")).Length);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        [TestMethod]
        public void StartupLocation_FollowsTheJournalAndClosesAMissingCustomFolder()
        {
            var temp = CreateTempFolder();
            try
            {
                var missing = Path.Combine(temp, "missing");
                var location = IconCacheRelocationService.CreateStartupLocation(temp, missing, out var none);
                Assert.IsNull(none);
                Assert.IsFalse(location.IsAvailable);

                new IconCacheRelocationJournal
                {
                    Phase = IconCacheRelocationPhase.Moving,
                    CurrentRoot = Path.Combine(temp, "icon_cache"),
                    SourceFolder = Path.Combine(temp, "icon_cache"),
                    TargetFolder = Path.Combine(temp, "new"),
                    Strategy = IconCacheMoveStrategy.PerFile
                }.Write(temp);
                var moving = IconCacheRelocationService.CreateStartupLocation(temp, null, out var movingJournal);
                Assert.AreEqual(IconCacheRelocationPhase.Moving, movingJournal.Phase);
                Assert.AreEqual(Path.Combine(temp, "icon_cache"), moving.Root);
                Assert.IsFalse(moving.IsAvailable);

                movingJournal.Phase = IconCacheRelocationPhase.Committed;
                movingJournal.Write(temp);
                var committed = IconCacheRelocationService.CreateStartupLocation(temp, null, out _);
                Assert.AreEqual(Path.Combine(temp, "new"), committed.Root);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        [TestMethod]
        public async Task WriteGate_DrainWaitsForRunningWritesAndRefusesNewOnes()
        {
            var gate = new IconCacheWriteGate();
            var lease = gate.TryEnter();
            var drain = gate.CloseAndDrainAsync(CancellationToken.None);

            Assert.IsFalse(drain.IsCompleted);
            Assert.IsNull(gate.TryEnter());
            lease.Dispose();
            await drain;
            gate.Open();
            Assert.IsNotNull(gate.TryEnter());
        }

        [TestMethod]
        public void Settings_CloneAndCopyFromKeepTheIconCacheFolder()
        {
            var source = new PersistedSettings { IconCacheDirectory = CustomRoot };
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(CustomRoot, source.Clone().IconCacheDirectory);
            Assert.AreEqual(CustomRoot, target.IconCacheDirectory);
        }

        private static string CreateTempFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "pa-iconcache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private sealed class FakeEnvironment : IconCacheMoveEnvironment
        {
            public readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> NonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, uint> Volumes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            public override bool FolderExists(string folder) =>
                Folders.Contains(folder.TrimEnd('\\')) || folder.Length <= 3 && Volumes.ContainsKey(folder.Substring(0, 1));

            public override bool FolderIsEmpty(string folder) => !NonEmpty.Contains(folder);

            public override bool IsLink(string folder) => Links.ContainsKey(folder);

            public override string ResolveLink(string folder) => Links.TryGetValue(folder, out var target) ? target : null;

            public override uint? VolumeOf(string folder) =>
                !string.IsNullOrEmpty(folder) && FolderExists(folder) && Volumes.TryGetValue(folder.Substring(0, 1), out var volume)
                    ? volume
                    : (uint?)null;
        }
    }
}
