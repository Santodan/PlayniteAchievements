using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal sealed class CueTrackLayout
    {
        public CueTrackEntry Track { get; set; }
        public string ResolvedPath { get; set; }
        public int PhysicalSectorSize { get; set; }
        public int DataOffset { get; set; }
        public int LogicalSectorSize { get; set; }
        public long StartByte { get; set; }
        public long LogicalLength { get; set; }
    }

    internal static class CueTrackReader
    {
        public const int LogicalSectorSize = 2048;

        public static bool IsCuePath(string filePath)
        {
            return !string.IsNullOrWhiteSpace(filePath) &&
                   string.Equals(Path.GetExtension(filePath), ".cue", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasReadableDataTrack(string cuePath)
        {
            return TryResolveFirstDataTrack(cuePath, out _, out _);
        }

        public static bool TryGetDataTrackDependencies(
            string cuePath,
            out IReadOnlyList<string> dependencyPaths,
            out string error)
        {
            dependencyPaths = Array.Empty<string>();
            if (!TryResolveFirstDataTrack(cuePath, out var layout, out error))
            {
                return false;
            }

            var paths = new[]
                {
                    Path.GetFullPath(cuePath),
                    Path.GetFullPath(layout.ResolvedPath)
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            dependencyPaths = paths;
            return paths.Count > 0;
        }

        public static bool TryOpenFirstDataTrackStream(string cuePath, out Stream stream, out string error)
        {
            stream = null;
            if (!TryResolveFirstDataTrack(cuePath, out var layout, out error))
            {
                return false;
            }

            try
            {
                stream = new CueTrackPayloadStream(layout);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                stream?.Dispose();
                stream = null;
                return false;
            }
        }

        internal static bool TryResolveFirstDataTrack(string cuePath, out CueTrackLayout layout, out string error)
        {
            layout = null;
            error = null;

            if (!IsCuePath(cuePath))
            {
                error = "Path is not a cue sheet.";
                return false;
            }

            if (!CueSheetParser.TryParseFile(cuePath, out var sheet, out error))
            {
                return false;
            }

            foreach (var track in sheet.Tracks.Where(IsDataTrack))
            {
                if (!TryGetSectorLayout(track.Mode, out var physicalSectorSize, out var dataOffset))
                {
                    error = $"Unsupported cue track mode '{track.Mode}'.";
                    continue;
                }

                var resolvedPath = ResolveTrackPath(cuePath, track.File?.FileName);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    error = $"Cue track file does not exist: {track.File?.FileName}";
                    continue;
                }

                if (!TryBuildLayout(sheet, track, resolvedPath, physicalSectorSize, dataOffset, out layout, out error))
                {
                    continue;
                }

                return true;
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                error = "Cue sheet does not contain a readable data track.";
            }

            return false;
        }

        private static bool TryBuildLayout(
            CueSheet sheet,
            CueTrackEntry track,
            string resolvedPath,
            int physicalSectorSize,
            int dataOffset,
            out CueTrackLayout layout,
            out string error)
        {
            layout = null;
            error = null;

            if (physicalSectorSize <= 0 || dataOffset < 0 || dataOffset + LogicalSectorSize > physicalSectorSize)
            {
                error = $"Invalid sector layout for cue track mode '{track.Mode}'.";
                return false;
            }

            var fileInfo = new FileInfo(resolvedPath);
            if (!fileInfo.Exists || fileInfo.Length <= 0)
            {
                error = $"Cue track file is empty or missing: {resolvedPath}";
                return false;
            }

            var startFrame = Math.Max(0, track.Index01Frames ?? 0);
            var startByte = (long)startFrame * physicalSectorSize;
            var endByte = fileInfo.Length;

            var trackIndex = sheet.Tracks.IndexOf(track);
            for (var i = trackIndex + 1; i < sheet.Tracks.Count; i++)
            {
                var next = sheet.Tracks[i];
                if (!ReferenceEquals(next.File, track.File))
                {
                    continue;
                }

                var nextFrame = next.Index00Frames ?? next.Index01Frames;
                if (nextFrame.HasValue && nextFrame.Value > startFrame)
                {
                    endByte = Math.Min(endByte, (long)nextFrame.Value * physicalSectorSize);
                }
                break;
            }

            if (startByte < 0 || startByte >= fileInfo.Length || endByte <= startByte)
            {
                error = $"Cue track has no readable sector data: {resolvedPath}";
                return false;
            }

            var physicalBytes = endByte - startByte;
            var sectorCount = physicalBytes / physicalSectorSize;
            if (sectorCount <= 0)
            {
                error = $"Cue track has no complete sectors: {resolvedPath}";
                return false;
            }

            layout = new CueTrackLayout
            {
                Track = track,
                ResolvedPath = resolvedPath,
                PhysicalSectorSize = physicalSectorSize,
                DataOffset = dataOffset,
                LogicalSectorSize = LogicalSectorSize,
                StartByte = startByte,
                LogicalLength = sectorCount * LogicalSectorSize
            };
            return true;
        }

        private static bool IsDataTrack(CueTrackEntry track)
        {
            var mode = track?.Mode ?? string.Empty;
            return mode.StartsWith("MODE", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("CDI", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetSectorLayout(string mode, out int physicalSectorSize, out int dataOffset)
        {
            physicalSectorSize = 0;
            dataOffset = 0;

            mode = (mode ?? string.Empty).Trim().ToUpperInvariant();
            switch (mode)
            {
                case "MODE1/2048":
                case "MODE2/2048":
                    physicalSectorSize = 2048;
                    dataOffset = 0;
                    return true;

                case "MODE1/2352":
                    physicalSectorSize = 2352;
                    dataOffset = 16;
                    return true;

                case "MODE2/2352":
                    physicalSectorSize = 2352;
                    dataOffset = 24;
                    return true;

                case "MODE2/2336":
                    physicalSectorSize = 2336;
                    dataOffset = 8;
                    return true;

                default:
                    return false;
            }
        }

        private static string ResolveTrackPath(string cuePath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var normalizedName = fileName.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedName))
            {
                return Path.GetFullPath(normalizedName);
            }

            var cueDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? string.Empty;
            return Path.GetFullPath(Path.Combine(cueDir, normalizedName));
        }
    }

    internal sealed class CueTrackPayloadStream : Stream
    {
        private readonly CueTrackLayout _layout;
        private readonly FileStream _fileStream;
        private long _position;

        public CueTrackPayloadStream(CueTrackLayout layout)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _fileStream = new FileStream(layout.ResolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _layout.LogicalLength;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            if (count == 0 || _position >= Length) return 0;

            var totalRead = 0;
            var remaining = (int)Math.Min(count, Length - _position);

            while (remaining > 0)
            {
                var sectorIndex = _position / _layout.LogicalSectorSize;
                var offsetInSector = (int)(_position % _layout.LogicalSectorSize);
                var toRead = Math.Min(remaining, _layout.LogicalSectorSize - offsetInSector);
                var physicalOffset =
                    _layout.StartByte +
                    (sectorIndex * _layout.PhysicalSectorSize) +
                    _layout.DataOffset +
                    offsetInSector;

                _fileStream.Seek(physicalOffset, SeekOrigin.Begin);
                var read = _fileStream.Read(buffer, offset + totalRead, toRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
                remaining -= read;
                _position += read;

                if (read < toRead)
                {
                    break;
                }
            }

            return totalRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    target = offset;
                    break;
                case SeekOrigin.Current:
                    target = _position + offset;
                    break;
                case SeekOrigin.End:
                    target = Length + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (target < 0)
            {
                throw new IOException("Cannot seek before the beginning of the cue track.");
            }

            _position = target;
            return _position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fileStream?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
