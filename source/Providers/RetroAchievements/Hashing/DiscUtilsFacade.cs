using PlayniteAchievements.Common.Disc;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// ISO9660 access over the 2048-byte payload view of one disc track. Filesystem parsing goes to
    /// the shared DiscFileSystemReader (ISO9660 only); when it rejects the volume, lookups fall back
    /// to the port of rcheevos's own directory walk, which accepts layouts DiscUtils does not
    /// (for example a volume descriptor set without a terminator).
    /// </summary>
    internal sealed class DiscUtilsFacade : IDisposable
    {
        private readonly DiscTrack _ownedTrack;
        private readonly Stream _view;
        private readonly DiscFileSystemReader _fs;

        public DiscUtilsFacade(string isoPath)
            : this(RaHashSource.FromFile(isoPath))
        {
        }

        /// <summary>Opens the first data track of the image and owns it.</summary>
        public DiscUtilsFacade(RaHashSource source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Path)) throw new ArgumentException("Disc image is required.", nameof(source));

            var track = DiscImage.Open(source).OpenTrack(DiscTrackSelector.FirstData)
                        ?? throw new InvalidDataException("Disc image has no readable data track.");
            try
            {
                _view = track.OpenPayloadStream();
                _fs = TryCreateReader(_view);
            }
            catch
            {
                _view?.Dispose();
                track.Dispose();
                throw;
            }

            _ownedTrack = track;
            Track = track;
        }

        /// <summary>Reads the filesystem of an already-open track, which the caller keeps owning.</summary>
        public DiscUtilsFacade(DiscTrack track)
        {
            Track = track ?? throw new ArgumentNullException(nameof(track));
            _view = track.OpenPayloadStream();
            try
            {
                _fs = TryCreateReader(_view);
            }
            catch
            {
                _view.Dispose();
                throw;
            }
        }

        public DiscTrack Track { get; }

        /// <summary>The payload view the filesystem is read from; file extents are offsets into it.</summary>
        public Stream ImageStream => _view;

        public void Dispose()
        {
            _fs?.Dispose();
            _view?.Dispose();
            _ownedTrack?.Dispose();
        }

        public Stream OpenFileOrNull(string pathInsideIso)
        {
            if (_fs != null)
            {
                return _fs.OpenFileOrNull(pathInsideIso);
            }

            return TryGetFileExtent(pathInsideIso, out var start, out var length)
                ? new WindowStream(_view, start, length)
                : null;
        }

        public bool FileExists(string pathInsideIso)
        {
            return _fs != null
                ? _fs.FileExists(pathInsideIso)
                : TryGetFileExtent(pathInsideIso, out _, out _);
        }

        public bool TryGetFileExtent(string pathInsideIso, out long startByte, out long length)
        {
            if (_fs != null)
            {
                return _fs.TryGetFileExtent(pathInsideIso, out startByte, out length);
            }

            startByte = 0;
            length = 0;
            if (string.IsNullOrWhiteSpace(pathInsideIso))
            {
                return false;
            }

            var sector = Iso9660SectorLocator.FindFileSector(Track, pathInsideIso.Trim().Replace('/', '\\'), out var size);
            if (sector == 0)
            {
                return false;
            }

            startByte = Track.ToPayloadOffset(sector);
            length = size;
            return startByte >= 0;
        }

        /// <summary>Reads up to <paramref name="count"/> bytes of the payload view at <paramref name="startByte"/>.</summary>
        public int ReadAt(long startByte, byte[] buffer, int count)
        {
            if (startByte < 0 || startByte >= _view.Length)
            {
                return 0;
            }

            _view.Position = startByte;
            return HashUtils.ReadFull(_view, buffer, 0, count);
        }

        /// <summary>
        /// rc_hash_cd_file (hash_disc.c:174-208) over the payload view: appends <paramref name="size"/>
        /// bytes (capped at 64 MB) from <paramref name="startByte"/>, reading on past the file's own
        /// extent when the size says so and stopping at the end of the track. False when the first
        /// sector is not whole.
        /// </summary>
        public async Task<bool> AppendFileAsync(HashAlgorithm md5, long startByte, uint size, CancellationToken cancel)
        {
            var first = new byte[DiscTrack.CookedSectorSize];
            if (ReadAt(startByte, first, first.Length) < first.Length)
            {
                return false;
            }

            long remaining = Math.Min(size, (uint)HashUtils.MaxHashBytes);
            var head = (int)Math.Min(remaining, first.Length);
            md5.TransformBlock(first, 0, head, null, 0);
            remaining -= head;

            if (remaining > 0)
            {
                _view.Position = startByte + first.Length;
                await HashUtils.AppendStreamAsync(md5, _view, remaining, cancel).ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>The DiscUtils reader, or null when DiscUtils does not accept the volume.</summary>
        private static DiscFileSystemReader TryCreateReader(Stream view)
        {
            try
            {
                return DiscFileSystemReader.OpenIso9660(view, leaveOpen: true);
            }
            catch (Exception ex) when (!HashUtils.IsTransientReadFailure(ex))
            {
                return null;
            }
        }

        /// <summary>A read-only window onto a file's bytes within the payload view.</summary>
        private sealed class WindowStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _start;
            private readonly long _length;
            private long _position;

            public WindowStream(Stream inner, long start, long length)
            {
                _inner = inner;
                _start = start;
                _length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _position;
                set
                {
                    if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                    _position = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _length || count <= 0)
                {
                    return 0;
                }

                _inner.Position = _start + _position;
                var read = _inner.Read(buffer, offset, (int)Math.Min(count, _length - _position));
                _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin: Position = offset; break;
                    case SeekOrigin.Current: Position = _position + offset; break;
                    case SeekOrigin.End: Position = _length + offset; break;
                    default: throw new ArgumentOutOfRangeException(nameof(origin));
                }

                return _position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
