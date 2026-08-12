using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Tests.Images
{
    /// <summary>
    /// Covers what the ray track cache does differently from the bitmap cache it sits over: which uri
    /// prefixes it treats as the same silhouette, and that it follows the same invalidation signals.
    /// </summary>
    [TestClass]
    public class RayTrackServiceTests
    {
        [TestMethod]
        public async Task SameUri_SharesOneTrack()
        {
            await WithService(async (root, service) =>
            {
                var icon = WritePng(root, "game-a", "boss.png");

                var first = await service.GetAsync(icon, CancellationToken.None);
                var second = await service.GetAsync(icon, CancellationToken.None);

                Assert.IsNotNull(first);
                Assert.AreSame(first, second, "a track is immutable, so it should be handed out, not rebuilt");
            });
        }

        [TestMethod]
        public async Task CachedTrack_IsAvailableSynchronously()
        {
            // The offscreen capture path has no seam to await on, so this is the only way it ever sees
            // a real silhouette.
            await WithService(async (root, service) =>
            {
                var icon = WritePng(root, "game-a", "boss.png");

                Assert.IsFalse(service.TryGet(icon, out _), "nothing should be cached yet");

                var built = await service.GetAsync(icon, CancellationToken.None);

                Assert.IsTrue(service.TryGet(icon, out var cached));
                Assert.AreSame(built, cached);
            });
        }

        [TestMethod]
        public async Task GrayscaleVariant_SharesTheTrackWithItsOriginal()
        {
            // Grayscale rewrites the color bytes and leaves alpha alone, so a locked icon has exactly
            // the silhouette its unlocked original does.
            await WithService(async (root, service) =>
            {
                var icon = WritePng(root, "game-a", "boss.png");

                var unlocked = await service.GetAsync(icon, CancellationToken.None);
                var locked = await service.GetAsync("gray:" + icon, CancellationToken.None);

                Assert.AreSame(unlocked, locked);
            });
        }

        [TestMethod]
        public async Task EvictByUriSegment_DropsOnlyTheMatchingTracks()
        {
            await WithService(async (root, service, images) =>
            {
                var iconA = WritePng(root, "game-a", "boss.png");
                var iconB = WritePng(root, "game-b", "boss.png");

                await service.GetAsync(iconA, CancellationToken.None);
                await service.GetAsync(iconB, CancellationToken.None);
                Assert.IsTrue(service.TryGet(iconA, out _));
                Assert.IsTrue(service.TryGet(iconB, out _));

                images.EvictByUriSegment("game-a");

                Assert.IsFalse(service.TryGet(iconA, out _), "the evicted game's track should be gone");
                Assert.IsTrue(service.TryGet(iconB, out _), "an unrelated game's track should survive");
            });
        }

        [TestMethod]
        public async Task ClearingTheBitmapCache_ClearsTheTracks()
        {
            await WithService(async (root, service, images) =>
            {
                var icon = WritePng(root, "game-a", "boss.png");
                await service.GetAsync(icon, CancellationToken.None);
                Assert.IsTrue(service.TryGet(icon, out _));

                images.Clear();

                Assert.IsFalse(service.TryGet(icon, out _));
            });
        }

        [TestMethod]
        public async Task AnalysisDoesNotDisplaceDisplayBitmaps()
        {
            // Analysis reads a bitmap once and keeps only the track, so it must not take an LRU slot
            // from an icon that is actually on screen.
            await WithService(async (root, service, images) =>
            {
                var icon = WritePng(root, "game-a", "boss.png");
                await service.GetAsync(icon, CancellationToken.None);

                // Nothing was cached by the analysis read, so deleting the file leaves no bitmap behind.
                File.Delete(icon);

                Assert.IsNull(await images.GetAsync(icon, 64, CancellationToken.None));
                Assert.IsTrue(service.TryGet(icon, out _), "the track itself should outlive its source");
            });
        }

        [TestMethod]
        public async Task MissingFile_FallsBackInsteadOfThrowing()
        {
            await WithService(async (root, service) =>
            {
                var track = await service.GetAsync(Path.Combine(root, "nope.png"), CancellationToken.None);

                Assert.IsNotNull(track);
                Assert.IsTrue(track.IsAnalytic);
            });
        }

        [TestMethod]
        public async Task BlankUri_YieldsTheEmptyTrack()
        {
            await WithService(async (root, service) =>
            {
                Assert.AreSame(RayTrack.Empty, await service.GetAsync(null, CancellationToken.None));
                Assert.AreSame(RayTrack.Empty, await service.GetAsync("   ", CancellationToken.None));
                Assert.IsFalse(service.TryGet(null, out _));
            });
        }

        private static Task WithService(Func<string, RayTrackService, Task> body)
        {
            return WithService((root, service, _) => body(root, service));
        }

        private static async Task WithService(Func<string, RayTrackService, MemoryImageService, Task> body)
        {
            var root = Path.Combine(Path.GetTempPath(), "PlayAchRayTrack", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                using (var disk = new DiskImageService(logger: null, cacheRoot: root))
                using (var images = new MemoryImageService(logger: null, disk))
                using (var service = new RayTrackService(logger: null, images))
                {
                    await body(root, service, images);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static string WritePng(string root, string game, string name)
        {
            var path = Path.Combine(root, "icon_cache", game, "128", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // A circle, so the builder traces a silhouette instead of taking the opaque-rect shortcut.
            const int size = 32;
            var stride = size * 4;
            var pixels = new byte[stride * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - 15.5;
                    var dy = y - 15.5;
                    var index = (y * stride) + (x * 4);
                    pixels[index + 0] = 255;
                    pixels[index + 1] = 255;
                    pixels[index + 2] = 255;
                    pixels[index + 3] = Math.Sqrt((dx * dx) + (dy * dy)) <= 13 ? (byte)255 : (byte)0;
                }
            }

            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            return path;
        }
    }
}
