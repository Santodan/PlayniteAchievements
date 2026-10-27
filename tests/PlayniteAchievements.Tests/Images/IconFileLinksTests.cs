using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Images.Tests
{
    [TestClass]
    public class IconFileLinksTests
    {
        [TestMethod]
        public async Task LinkIdenticalGameIconFiles_LinksSameBytesAcrossFolders_AndLeavesCustomAlone()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var gameDir = Path.Combine(tempDir, "icon_cache", gameId);
                var own = Write(Path.Combine(gameDir, "original", "a.png"), "same");
                var borrowed = Write(Path.Combine(gameDir, "metadata", "PlayStation", "b.png"), "same");
                var category = Write(Path.Combine(gameDir, "category_defaults", "c.jpg"), "same");
                var different = Write(Path.Combine(gameDir, "original", "d.png"), "diff");
                var custom = Write(Path.Combine(gameDir, "custom", "e.png"), "same");

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var linked = await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false);
                    var again = await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false);

                    Assert.AreEqual(2, linked);
                    Assert.AreEqual(0, again);
                }

                Assert.AreEqual(3, LinkCount(own));
                Assert.AreEqual(3, LinkCount(borrowed));
                Assert.AreEqual(3, LinkCount(category));
                Assert.AreEqual(1, LinkCount(different));
                Assert.AreEqual(1, LinkCount(custom));
                Assert.AreEqual("same", File.ReadAllText(borrowed));
                Assert.AreEqual(0, Directory.GetFiles(gameDir, "*" + IconFileLinks.TempSuffix, SearchOption.AllDirectories).Length);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task OverwritingALinkedIcon_LeavesTheNamesItWasLinkedTo()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var gameDir = Path.Combine(tempDir, "icon_cache", gameId);
                var first = Write(Path.Combine(gameDir, "original", "a.png"), "same");
                var second = Write(Path.Combine(gameDir, "original", "b.png"), "same");
                var replacement = Write(Path.Combine(tempDir, "new.png"), "new image");

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false);
                    Assert.AreEqual(2, LinkCount(first));

                    await diskImageService.CopyCachedIconAsync(replacement, second, CancellationToken.None, overwriteExistingTarget: true);
                }

                Assert.AreEqual("new image", File.ReadAllText(second));
                Assert.AreEqual("same", File.ReadAllText(first));
                Assert.AreEqual(1, LinkCount(first));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task CompressingALinkedIcon_LeavesTheNamesItWasLinkedTo()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var gameDir = Path.Combine(tempDir, "icon_cache", gameId);
                var first = Write(Path.Combine(gameDir, "original", "a.png"), "same");
                var second = Write(Path.Combine(gameDir, "metadata", "PlayStation", "b.png"), "same");

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false);
                    await diskImageService.ReplaceCachedImageBytesAsync(second, Encoding.UTF8.GetBytes("small"), CancellationToken.None);
                }

                Assert.AreEqual("small", File.ReadAllText(second));
                Assert.AreEqual("same", File.ReadAllText(first));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task LinkIdenticalGameIconFiles_SkipsAnIconOpenWithoutDeleteSharing()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var gameDir = Path.Combine(tempDir, "icon_cache", gameId);
                var first = Write(Path.Combine(gameDir, "original", "a.png"), "same");
                var second = Write(Path.Combine(gameDir, "original", "b.png"), "same");

                int linked;
                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    // Both names held open, so whichever one the pass would replace is in use.
                    using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        linked = await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false);
                    }

                    Assert.AreEqual(0, linked);
                    Assert.AreEqual(1, await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false),
                        "the next pass links them once they are closed");
                }

                Assert.AreEqual("same", File.ReadAllText(first));
                Assert.AreEqual("same", File.ReadAllText(second));
                Assert.AreEqual(0, Directory.GetFiles(gameDir, "*" + IconFileLinks.TempSuffix, SearchOption.AllDirectories).Length);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task AnOverwriteDuringTheLinkPass_KeepsTheNewImage()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var gameDir = Path.Combine(tempDir, "icon_cache", gameId);
                var replacement = Write(Path.Combine(tempDir, "new.png"), "new image");

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    for (var i = 0; i < 40; i++)
                    {
                        var originals = Enumerable.Range(0, 20)
                            .Select(n => Write(Path.Combine(gameDir, "original", $"a{n}.png"), "same"))
                            .ToList();
                        var target = originals[i % originals.Count];

                        var link = Task.Run(() => diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false));
                        var overwrite = Task.Run(() => diskImageService.CopyCachedIconAsync(replacement, target, CancellationToken.None, overwriteExistingTarget: true));
                        await Task.WhenAll(link, overwrite);

                        Assert.AreEqual("new image", File.ReadAllText(target), $"round {i}");
                        Assert.IsTrue(originals.Where(p => p != target).All(p => File.ReadAllText(p) == "same"), $"round {i}");
                        Directory.Delete(gameDir, recursive: true);
                    }
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task DeletingAFolderOfLinkedIcons_KeepsTheirOtherNames()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var gameDir = Path.Combine(tempDir, "icon_cache", gameId);
                var own = Write(Path.Combine(gameDir, "original", "a.png"), "same");
                Write(Path.Combine(gameDir, "metadata", "PlayStation", "b.png"), "same");

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None, onlyWhenWritten: false);
                    diskImageService.DeleteCacheRelativeDirectory(Path.Combine("icon_cache", gameId, "metadata", "PlayStation"));
                }

                Assert.IsFalse(Directory.Exists(Path.Combine(gameDir, "metadata", "PlayStation")));
                Assert.AreEqual("same", File.ReadAllText(own));
                Assert.AreEqual(1, LinkCount(own));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task LinkIdenticalGameIconFiles_ScansAFolderOnce_ThenOnlyAfterWrites()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var gameDir = Path.Combine(tempDir, "icon_cache", gameId);
                var first = Write(Path.Combine(gameDir, "original", "a.png"), "same");
                Write(Path.Combine(gameDir, "original", "b.png"), "same");
                var source = Write(Path.Combine(tempDir, "source.png"), "same");

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    Assert.AreEqual(1, await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None),
                        "the first pass links what the folder already holds");

                    Write(Path.Combine(gameDir, "original", "d.png"), "same");
                    Assert.AreEqual(0, await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None),
                        "a scanned folder the service did not write into is skipped");

                    await diskImageService.CopyCachedIconAsync(source, Path.Combine(gameDir, "original", "c.png"), CancellationToken.None);
                    Assert.AreEqual(2, await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None),
                        "a write brings the folder back into the next pass");
                }

                Write(Path.Combine(gameDir, "original", "e.png"), "same");
                using (var restarted = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    Assert.AreEqual(0, await restarted.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None),
                        "the once-scanned record outlives the session");
                }

                Assert.AreEqual(4, LinkCount(first));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Detach_KeepsAnUnlinkedFile()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var path = Write(Path.Combine(tempDir, "a.png"), "only");

                IconFileLinks.Detach(path);

                Assert.IsTrue(File.Exists(path));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static int LinkCount(string path)
        {
            Assert.IsTrue(IconFileLinks.TryGetIdentity(path, out _, out var linkCount));
            return linkCount;
        }

        private static string Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            return path;
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayAchLinks_" + Guid.NewGuid().ToString("N"));
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
    }
}
