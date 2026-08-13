using System;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Tests.TestInfrastructure
{
    /// <summary>
    /// Builds real GIF bytes for tests. Every encoded frame is a single pixel while the logical
    /// canvas is whatever size is asked for, so a fixture can carry hundreds of frames at a large
    /// declared resolution without allocating anything close to that many full-canvas bitmaps.
    /// </summary>
    internal static class GifFixture
    {
        internal static byte[] BuildSparseGif(int width, int height, int frameCount)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
                writer.Write((ushort)width);
                writer.Write((ushort)height);
                writer.Write((byte)0x80); // global two-color table
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write(new byte[] { 0, 0, 0, 255, 255, 255 });

                // Loop forever.
                writer.Write(new byte[] { 0x21, 0xFF, 0x0B });
                writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                writer.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

                for (var i = 0; i < frameCount; i++)
                {
                    // Graphic control extension: 40ms delay.
                    writer.Write(new byte[] { 0x21, 0xF9, 0x04, 0x00, 0x04, 0x00, 0x00, 0x00 });
                    // One-pixel image at (0,0) on the large logical canvas.
                    writer.Write((byte)0x2C);
                    writer.Write((ushort)0);
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)1);
                    writer.Write((byte)0);
                    // LZW: clear, color index 1, end.
                    writer.Write(new byte[] { 0x02, 0x02, 0x4C, 0x01, 0x00 });
                }

                writer.Write((byte)0x3B);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Writes bytes to a fresh temp directory as animation.gif and returns the full path.
        /// Pair with <see cref="DeleteTempPayload"/>.
        /// </summary>
        internal static string WriteTempGif(byte[] bytes)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "animation.gif");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal static void DeleteTempPayload(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }
}
