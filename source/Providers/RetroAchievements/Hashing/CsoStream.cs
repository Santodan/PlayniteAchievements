using System;
using System.IO;
using System.IO.Compression;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// Read-only, seekable view of the uncompressed ISO inside a CSO/CISO image.
    /// Blocks are inflated on demand, and the most recent one is kept, so reading
    /// a few files from a disc costs a few blocks instead of the whole image.
    /// </summary>
    internal sealed class CsoStream : Stream
    {
        private readonly FileStream _file;
        private readonly CsoUtils.CsoHeader _header;
        private readonly uint[] _blockIndex;
        private readonly long _offsetBase;
        private readonly int _blockCount;
        private readonly long _length;

        private readonly byte[] _block;
        private byte[] _compressed;
        private int _cachedBlock = -1;
        private int _cachedBlockLength;
        private long _position;

        private CsoStream(FileStream file, CsoUtils.CsoHeader header, uint[] blockIndex, long offsetBase)
        {
            _file = file;
            _header = header;
            _blockIndex = blockIndex;
            _offsetBase = offsetBase;
            _blockCount = blockIndex.Length - 1;
            _length = (long)header.UncompressedSize;
            _block = new byte[header.BlockSize];
            _compressed = new byte[header.BlockSize];
        }

        public static CsoStream Open(string csoPath)
        {
            if (string.IsNullOrWhiteSpace(csoPath))
                throw new ArgumentNullException(nameof(csoPath));

            // Block reads are small and mostly sequential; a 64 KB buffer turns runs of them into one read.
            var file = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            try
            {
                if (!CsoUtils.TryReadHeader(file, out var header))
                    throw new InvalidOperationException("Invalid CSO file header.");

                CsoUtils.ValidateHeader(header);

                var blockCount = CsoUtils.GetBlockCount(header);
                var indexEntryCount = checked(blockCount + 1);
                var indexSize = checked((long)indexEntryCount * sizeof(uint));

                Exception lastIndexException = null;
                foreach (var candidateIndexOffset in CsoUtils.GetIndexOffsetCandidates(header, file.Length, indexSize))
                {
                    try
                    {
                        file.Position = candidateIndexOffset;
                        var candidateIndex = CsoUtils.ReadBlockIndex(file, indexEntryCount);
                        var candidateOffsetBase = CsoUtils.ResolveOffsetBase(candidateIndex, header, candidateIndexOffset, indexSize, file.Length);
                        CsoUtils.ValidateBlockIndex(candidateIndex, header, candidateOffsetBase, file.Length);

                        return new CsoStream(file, header, candidateIndex, candidateOffsetBase);
                    }
                    catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException)
                    {
                        lastIndexException = ex;
                    }
                }

                throw new InvalidDataException("Failed to parse CSO block index.", lastIndexException);
            }
            catch
            {
                file.Dispose();
                throw;
            }
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
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

            var total = 0;
            while (count > 0 && _position < _length)
            {
                var blockNumber = (int)(_position / _header.BlockSize);
                var inBlock = (int)(_position % _header.BlockSize);

                LoadBlock(blockNumber);

                var available = _cachedBlockLength - inBlock;
                if (available <= 0)
                {
                    break;
                }

                var n = Math.Min(available, count);
                Buffer.BlockCopy(_block, inBlock, buffer, offset, n);

                offset += n;
                count -= n;
                total += n;
                _position += n;
            }

            return total;
        }

        private void LoadBlock(int blockNumber)
        {
            if (blockNumber == _cachedBlock)
            {
                return;
            }

            if (blockNumber < 0 || blockNumber >= _blockCount)
            {
                throw new ArgumentOutOfRangeException(nameof(blockNumber));
            }

            var rawEntry = _blockIndex[blockNumber];
            var blockOffset = CsoUtils.DecodeEntryOffset(rawEntry, _header.IndexShift, _offsetBase);
            var nextBlockOffset = CsoUtils.DecodeEntryOffset(_blockIndex[blockNumber + 1], _header.IndexShift, _offsetBase);
            var storedSize = nextBlockOffset - blockOffset;

            var blockStart = (long)blockNumber * _header.BlockSize;
            var blockLength = (int)Math.Min(_header.BlockSize, _length - blockStart);

            // Invalidate first so a failed decode never leaves a half-written block cached.
            _cachedBlock = -1;
            _file.Position = blockOffset;

            if (CsoUtils.IsCompressedBlock(_header.Version, rawEntry, storedSize, _header.BlockSize))
            {
                if (_header.Version == CsoUtils.CsoV2 && (rawEntry & CsoUtils.CisoNotCompressedMask) != 0)
                {
                    throw new InvalidDataException($"Unsupported CSO v2 LZ4-compressed block at index {blockNumber}.");
                }

                if (storedSize > int.MaxValue)
                {
                    throw new InvalidDataException($"Compressed CSO block {blockNumber} is too large to process: {storedSize} bytes.");
                }

                // Stored size can exceed the block size when index alignment pads the data.
                if (_compressed.Length < storedSize)
                {
                    _compressed = new byte[storedSize];
                }

                CsoUtils.ReadExactly(_file, _compressed, 0, (int)storedSize);
                Inflate(_compressed, (int)storedSize, blockLength, blockNumber);
            }
            else
            {
                if (storedSize < blockLength)
                {
                    throw new InvalidDataException($"Uncompressed CSO block {blockNumber} is truncated ({storedSize} < {blockLength}).");
                }

                CsoUtils.ReadExactly(_file, _block, 0, blockLength);
            }

            _cachedBlock = blockNumber;
            _cachedBlockLength = blockLength;
        }

        private void Inflate(byte[] compressed, int compressedLength, int outputLength, int blockNumber)
        {
            // CSO blocks are raw deflate with no zlib header. The framework DeflateStream is zlib-backed.
            using (var input = new MemoryStream(compressed, 0, compressedLength, writable: false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                var total = 0;
                while (total < outputLength)
                {
                    var read = deflate.Read(_block, total, outputLength - total);
                    if (read <= 0)
                    {
                        throw new InvalidDataException($"Deflate block {blockNumber} ended early. Expected {outputLength} bytes, got {total}.");
                    }

                    total += read;
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;
                case SeekOrigin.Current:
                    Position = _position + offset;
                    break;
                case SeekOrigin.End:
                    Position = _length + offset;
                    break;
            }

            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
