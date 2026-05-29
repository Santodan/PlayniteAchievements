using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal sealed class DiscImageReader : IDisposable
    {
        private readonly Stream _stream;

        private DiscImageReader(Stream stream, int sectorSize, bool isCue)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            SectorSize = sectorSize;
            IsCue = isCue;
        }

        public Stream Stream => _stream;
        public int SectorSize { get; }
        public bool IsCue { get; }
        public long Length => _stream.Length;

        public static DiscImageReader Open(string filePath, int sectorSizeForPlainFile = 2048)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Disc image path is required.", nameof(filePath));
            }

            if (CueTrackReader.IsCuePath(filePath))
            {
                if (!CueTrackReader.TryOpenFirstDataTrackStream(filePath, out var cueStream, out var error))
                {
                    throw new InvalidDataException(error ?? "Unable to open cue data track.");
                }

                return new DiscImageReader(cueStream, CueTrackReader.LogicalSectorSize, isCue: true);
            }

            return new DiscImageReader(
                new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                sectorSizeForPlainFile,
                isCue: false);
        }

        public Task<bool> TryReadSectorAsync(int sectorIndex, byte[] buffer, int count, CancellationToken cancel)
        {
            return HashUtils.TryReadSectorAsync(_stream, sectorIndex, SectorSize, buffer, count, cancel);
        }

        public Task<bool> TryReadSectorAsync(int sectorIndex, byte[] buffer, CancellationToken cancel)
        {
            return HashUtils.TryReadSectorAsync(_stream, SectorSize, sectorIndex, buffer, cancel);
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}
