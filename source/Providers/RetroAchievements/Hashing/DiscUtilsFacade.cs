using PlayniteAchievements.Common.Disc;
using System;
using System.IO;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// Filesystem access over RA-hashable disc images. Opens the image through
    /// DiscImageReader so cue sheets resolve to their data track, then delegates
    /// filesystem parsing to the shared DiscFileSystemReader.
    /// </summary>
    internal sealed class DiscUtilsFacade : IDisposable
    {
        private readonly DiscImageReader _image;
        private readonly DiscFileSystemReader _fs;

        public DiscUtilsFacade(string isoPath)
        {
            if (string.IsNullOrWhiteSpace(isoPath)) throw new ArgumentException("ISO path is required.", nameof(isoPath));

            DiscImageReader image = null;
            DiscFileSystemReader fs = null;

            try
            {
                image = DiscImageReader.Open(isoPath);
                fs = new DiscFileSystemReader(image.Stream, leaveOpen: true);
            }
            catch
            {
                fs?.Dispose();
                image?.Dispose();
                throw;
            }

            _image = image;
            _fs = fs;
        }

        public void Dispose()
        {
            _fs?.Dispose();
            _image?.Dispose();
        }

        public Stream OpenFileOrNull(string pathInsideIso)
        {
            return _fs.OpenFileOrNull(pathInsideIso);
        }

        public bool FileExists(string pathInsideIso)
        {
            return _fs.FileExists(pathInsideIso);
        }
    }
}
