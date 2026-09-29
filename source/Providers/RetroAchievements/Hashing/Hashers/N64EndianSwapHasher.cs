using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class N64EndianSwapHasher : IRaHasher
    {
        public string Name => "Nintendo 64 (endian normalized MD5)";

        public bool SupportsForwardOnlyInput => true;

        public async Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            using (var stream = source.Open())
            {
                // Peek the first byte without seeking so forward-only inputs work: it is pushed
                // back in front of the stream for hashing.
                var first = new byte[1];
                if (HashUtils.ReadFull(stream, first, 0, 1) != 1)
                {
                    return Array.Empty<string>();
                }

                Action<byte[], int> transform;
                switch (first[0])
                {
                    case 0x80: // z64 - big endian
                    case 0xE8: // ndd - don't byteswap
                    case 0x22:
                        transform = null;
                        break;
                    case 0x37: // v64 - byteswapped (16-bit)
                        transform = HashUtils.ByteSwap16;
                        break;
                    case 0x40: // n64 - little endian (32-bit words)
                        transform = HashUtils.ByteSwap32;
                        break;
                    default:
                        return Array.Empty<string>();
                }

                using (var joined = new PrefixedStream(first, stream))
                {
                    var hash = await HashUtils
                        .ComputeMd5HexFromStreamAsync(joined, HashUtils.MaxHashBytes, cancel, transform)
                        .ConfigureAwait(false);
                    return new[] { hash };
                }
            }
        }

        /// <summary>Forward-only stream that yields a few already-read bytes before the rest.</summary>
        private sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Stream _rest;
            private int _prefixPos;

            public PrefixedStream(byte[] prefix, Stream rest)
            {
                _prefix = prefix;
                _rest = rest;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_prefixPos < _prefix.Length)
                {
                    var n = Math.Min(count, _prefix.Length - _prefixPos);
                    Buffer.BlockCopy(_prefix, _prefixPos, buffer, offset, n);
                    _prefixPos += n;
                    return n;
                }

                return _rest.Read(buffer, offset, count);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
