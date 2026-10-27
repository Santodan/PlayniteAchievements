using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using System;
using System.IO;
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
                    var linked = await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None);
                    var again = await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None);

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
                    await diskImageService.LinkIdenticalGameIconFilesAsync(gameId, CancellationToken.None);
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
