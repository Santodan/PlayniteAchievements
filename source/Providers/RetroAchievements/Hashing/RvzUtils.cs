using System;
using System.IO;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// Entry points for Dolphin RVZ images. Decoding lives in <see cref="RvzStream"/>.
    /// </summary>
    internal static class RvzUtils
    {
        public static bool IsRvzPath(string filePath)
        {
            return RvzStream.IsRvzPath(filePath);
        }

        /// <summary>
        /// Opens an RVZ image for random access. Only the groups a read touches are decoded.
        /// </summary>
        public static RvzStream OpenStream(string rvzPath)
        {
            return RvzStream.Open(rvzPath);
        }

        /// <summary>
        /// Decodes the whole image to a temporary ISO. Only needed by consumers that require
        /// a file path; hashers read through <see cref="OpenStream"/>.
        /// </summary>
        public static ArchiveUtils.TempFile DecompressToTempFile(string rvzPath, Action<ulong, ulong> progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(rvzPath))
                throw new ArgumentNullException(nameof(rvzPath));

            var outPath = Path.Combine(Path.GetTempPath(), $"PlayniteAchievements_rvz_{Guid.NewGuid():N}.iso");
            var outFileCreated = false;

            try
            {
                using (var inStream = RvzStream.Open(rvzPath))
                using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    outFileCreated = true;

                    var total = (ulong)inStream.Length;
                    var buffer = new byte[1024 * 1024];
                    ulong written = 0;
                    while (written < total)
                    {
                        var toRead = (int)Math.Min((ulong)buffer.Length, total - written);
                        var read = inStream.Read(buffer, 0, toRead);
                        if (read <= 0)
                        {
                            throw new EndOfStreamException($"RVZ decoded {written} of {total} bytes.");
                        }

                        outStream.Write(buffer, 0, read);
                        written += (ulong)read;
                        progressCallback?.Invoke(written, total);
                    }
                }
            }
            catch
            {
                if (outFileCreated)
                {
                    try
                    {
                        File.Delete(outPath);
                    }
                    catch
                    {
                        // ignore cleanup failures
                    }
                }

                throw;
            }

            return new ArchiveUtils.TempFile(outPath);
        }
    }
}
