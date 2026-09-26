using System;
using System.IO;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// The bytes a hasher reads: a file on disk, a seekable stream such as the ISO inside a
    /// CSO/RVZ image, or a forward-only stream such as an archive entry.
    /// <see cref="Path"/> is always set; for streams it names the container so hashers that key
    /// on the extension or file name keep working.
    /// </summary>
    internal sealed class RaHashSource : IDisposable
    {
        private readonly Stream _sharedStream;
        private Func<Stream> _openOnce;

        private RaHashSource(string path, bool isFile, long length, Stream sharedStream, Func<Stream> openOnce)
        {
            Path = path;
            IsFile = isFile;
            Length = length;
            _sharedStream = sharedStream;
            _openOnce = openOnce;
        }

        public string Path { get; }

        /// <summary>True when <see cref="Path"/> is the file whose bytes are hashed.</summary>
        public bool IsFile { get; }

        public long Length { get; }

        /// <summary>False only for forward-only sources, whose stream can be opened once.</summary>
        public bool IsSeekable => _openOnce == null;

        public static RaHashSource FromFile(string path)
        {
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            return new RaHashSource(path, isFile: true, length, null, null);
        }

        /// <summary>
        /// Wraps a seekable stream the source owns. Each <see cref="Open"/> returns an independent
        /// view with its own position, so a hasher may open the image more than once.
        /// </summary>
        public static RaHashSource FromSeekableStream(string path, Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
            return new RaHashSource(path, isFile: false, stream.Length, stream, null);
        }

        /// <summary>A stream that can be read once from the start, with a known length.</summary>
        public static RaHashSource FromForwardOnlyStream(string path, long length, Func<Stream> openOnce)
        {
            if (openOnce == null) throw new ArgumentNullException(nameof(openOnce));
            return new RaHashSource(path, isFile: false, length, null, openOnce);
        }

        /// <summary>Opens the bytes from the start. The caller disposes the returned stream.</summary>
        public Stream Open()
        {
            if (IsFile)
            {
                return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashUtils.FileBufferSize, FileOptions.SequentialScan);
            }

            if (_sharedStream != null)
            {
                return new SharedStreamView(_sharedStream);
            }

            var open = _openOnce ?? throw new InvalidOperationException("Forward-only hash source was already opened.");
            _openOnce = () => throw new InvalidOperationException("Forward-only hash source was already opened.");
            return open();
        }

        public void Dispose()
        {
            _sharedStream?.Dispose();
        }

        /// <summary>Read-only view over a shared seekable stream that keeps its own position.</summary>
        private sealed class SharedStreamView : Stream
        {
            private readonly Stream _inner;
            private long _position;

            public SharedStreamView(Stream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;

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
                _inner.Position = _position;
                var read = _inner.Read(buffer, offset, count);
                _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin: Position = offset; break;
                    case SeekOrigin.Current: Position = _position + offset; break;
                    case SeekOrigin.End: Position = Length + offset; break;
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
