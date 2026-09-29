using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.LZMA;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// Read-only, seekable view of the GameCube disc inside a Dolphin RVZ image.
    /// Follows Dolphin's WIARVZFileReader (Source/Core/DiscIO/WIABlob.cpp): header parsing and
    /// validation from Initialize, offset mapping from Read and ReadFromGroups, and group decoding
    /// from Chunk and RVZPackDecompressor. Groups are decoded on demand and the last one is kept,
    /// so hashing a few MB of a disc decodes a few groups instead of the whole image.
    /// Wii partitions are stored decrypted without their hash blocks and are not supported.
    /// </summary>
    internal sealed class RvzStream : Stream
    {
        private const uint RvzMagic = 0x015A5652; // "RVZ\x1" read as little-endian
        private const uint RvzVersion = 0x01000000;
        private const uint RvzVersionReadCompatible = 0x00030000;

        private const int Header1Size = 0x48;
        private const int Header2Size = 0xDC;
        private const int CompressorDataCapacity = 7;
        private const int DiscHeaderSize = 0x80;
        private const int Sha1Size = 20;

        private const int RawDataEntrySize = 0x18;
        private const int RvzGroupEntrySize = 0x0C;

        // VolumeWii::BLOCK_TOTAL_SIZE: raw data offsets are rounded down to this.
        private const long BlockTotalSize = 0x8000;
        private const long GroupTotalSize = 0x200000;

        private readonly FileStream _file;
        private readonly RvzCompression _compression;
        private readonly byte[] _compressorData;
        private readonly ZstdSharp.Decompressor _zstd;
        private readonly long _chunkSize;
        private readonly byte[] _discHeader;
        private readonly long _length;
        private readonly RawDataEntry[] _rawDataEntries;
        private readonly GroupEntry[] _groupEntries;

        // Raw data entries ordered by end offset, for Dolphin's upper_bound lookup.
        private readonly long[] _dataEntryEnds;
        private readonly int[] _dataEntryIndices;

        private long _cachedGroupFileOffset = -1;
        private byte[] _cachedGroup;
        private int _cachedGroupLength;
        private long _position;

        private enum RvzCompression : uint
        {
            None = 0,
            Purge = 1,
            Bzip2 = 2,
            Lzma = 3,
            Lzma2 = 4,
            Zstd = 5
        }

        private struct RawDataEntry
        {
            public long DataOffset;
            public long DataSize;
            public uint GroupIndex;
            public uint NumberOfGroups;
        }

        private struct GroupEntry
        {
            public uint DataOffset; // >> 2
            public uint DataSize;   // high bit: compressed
            public uint RvzPackedSize;
        }

        private RvzStream(
            FileStream file,
            RvzCompression compression,
            byte[] compressorData,
            long chunkSize,
            byte[] discHeader,
            long length,
            RawDataEntry[] rawDataEntries,
            GroupEntry[] groupEntries)
        {
            _file = file;
            _compression = compression;
            _compressorData = compressorData;
            _zstd = compression == RvzCompression.Zstd ? new ZstdSharp.Decompressor() : null;
            _chunkSize = chunkSize;
            _discHeader = discHeader;
            _length = length;
            _rawDataEntries = rawDataEntries;
            _groupEntries = groupEntries;

            var ordered = rawDataEntries
                .Select((entry, index) => new { End = entry.DataOffset + entry.DataSize, Size = entry.DataSize, Index = index })
                .Where(e => e.Size != 0)
                .OrderBy(e => e.End)
                .ToList();
            _dataEntryEnds = ordered.Select(e => e.End).ToArray();
            _dataEntryIndices = ordered.Select(e => e.Index).ToArray();
        }

        public static bool IsRvzPath(string filePath)
        {
            return !string.IsNullOrWhiteSpace(filePath) &&
                   string.Equals(Path.GetExtension(filePath), ".rvz", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Opens and validates an RVZ image. Mirrors WIARVZFileReader::Initialize.</summary>
        public static RvzStream Open(string rvzPath)
        {
            if (string.IsNullOrWhiteSpace(rvzPath))
                throw new ArgumentNullException(nameof(rvzPath));

            var file = new FileStream(rvzPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            try
            {
                return OpenCore(file);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private static RvzStream OpenCore(FileStream file)
        {
            var header1 = new byte[Header1Size];
            ReadExactlyAt(file, 0, header1, 0, header1.Length);

            if (BitConverter.ToUInt32(header1, 0) != RvzMagic)
                throw new InvalidDataException("Not an RVZ file.");

            var version = ReadU32(header1, 4);
            var versionCompatible = ReadU32(header1, 8);
            if (RvzVersion < versionCompatible || RvzVersionReadCompatible > version)
                throw new InvalidDataException($"Unsupported RVZ version 0x{version:X8}.");

            if (!Sha1(header1, 0, Header1Size - Sha1Size).SequenceEqual(Slice(header1, 52, Sha1Size)))
                throw new InvalidDataException("RVZ header 1 hash mismatch.");

            var isoFileSize = (long)ReadU64(header1, 36);
            var wiaFileSize = (long)ReadU64(header1, 44);
            if (wiaFileSize != file.Length)
                throw new InvalidDataException("RVZ file size does not match its header.");

            var header2Size = ReadU32(header1, 12);
            const int header2MinSize = Header2Size - CompressorDataCapacity;
            if (header2Size < header2MinSize || header2Size > 1024 * 1024)
                throw new InvalidDataException("Invalid RVZ header 2 size.");

            var header2Raw = new byte[header2Size];
            ReadExactlyAt(file, Header1Size, header2Raw, 0, header2Raw.Length);
            if (!Sha1(header2Raw, 0, header2Raw.Length).SequenceEqual(Slice(header1, 16, Sha1Size)))
                throw new InvalidDataException("RVZ header 2 hash mismatch.");

            // Short header 2 copies are zero-extended, as Dolphin's memcpy into a zeroed struct.
            var header2 = new byte[Header2Size];
            Buffer.BlockCopy(header2Raw, 0, header2, 0, (int)Math.Min(header2Raw.Length, Header2Size));

            var compressorDataSize = header2[0xD4];
            if (compressorDataSize > CompressorDataCapacity || header2Size < header2MinSize + compressorDataSize)
                throw new InvalidDataException("Invalid RVZ compressor data size.");

            var chunkSize = ReadU32(header2, 12);
            var isPowerOfTwo = (chunkSize & (chunkSize - 1)) == 0;
            if ((chunkSize < BlockTotalSize || !isPowerOfTwo) && chunkSize % GroupTotalSize != 0)
                throw new InvalidDataException($"Invalid RVZ chunk size {chunkSize}.");

            var compressionType = ReadU32(header2, 4);
            if (compressionType > (uint)RvzCompression.Zstd || compressionType == (uint)RvzCompression.Purge)
                throw new InvalidDataException($"Unsupported RVZ compression type {compressionType}.");
            var compression = (RvzCompression)compressionType;
            var compressorData = Slice(header2, 0xD5, compressorDataSize);

            var discHeader = Slice(header2, 16, DiscHeaderSize);

            var partitionEntryCount = ReadU32(header2, 0x90);
            var partitionEntrySize = ReadU32(header2, 0x94);
            var partitionEntriesOffset = (long)ReadU64(header2, 0x98);
            var partitionBytes = checked((int)(partitionEntryCount * partitionEntrySize));
            var partitionEntries = new byte[partitionBytes];
            ReadExactlyAt(file, partitionEntriesOffset, partitionEntries, 0, partitionBytes);
            if (!Sha1(partitionEntries, 0, partitionBytes).SequenceEqual(Slice(header2, 0xA0, Sha1Size)))
                throw new InvalidDataException("RVZ partition entries hash mismatch.");
            if (partitionEntryCount != 0)
                throw new NotSupportedException("Wii RVZ images (partition data) are not supported.");

            var rawDataEntryCount = ReadU32(header2, 0xB4);
            var rawDataEntriesOffset = (long)ReadU64(header2, 0xB8);
            var rawDataEntriesSize = ReadU32(header2, 0xC0);
            var rawBytes = DecompressTable(file, compression, compressorData, rawDataEntriesOffset,
                rawDataEntriesSize, checked((int)(rawDataEntryCount * RawDataEntrySize)));

            var rawDataEntries = new RawDataEntry[rawDataEntryCount];
            for (var i = 0; i < rawDataEntries.Length; i++)
            {
                var p = i * RawDataEntrySize;
                rawDataEntries[i] = new RawDataEntry
                {
                    DataOffset = (long)ReadU64(rawBytes, p),
                    DataSize = (long)ReadU64(rawBytes, p + 8),
                    GroupIndex = ReadU32(rawBytes, p + 16),
                    NumberOfGroups = ReadU32(rawBytes, p + 20)
                };
            }

            var groupEntryCount = ReadU32(header2, 0xC4);
            var groupEntriesOffset = (long)ReadU64(header2, 0xC8);
            var groupEntriesSize = ReadU32(header2, 0xD0);
            var groupBytes = DecompressTable(file, compression, compressorData, groupEntriesOffset,
                groupEntriesSize, checked((int)(groupEntryCount * RvzGroupEntrySize)));

            var groupEntries = new GroupEntry[groupEntryCount];
            for (var i = 0; i < groupEntries.Length; i++)
            {
                var p = i * RvzGroupEntrySize;
                groupEntries[i] = new GroupEntry
                {
                    DataOffset = ReadU32(groupBytes, p),
                    DataSize = ReadU32(groupBytes, p + 4),
                    RvzPackedSize = ReadU32(groupBytes, p + 8)
                };
            }

            var stream = new RvzStream(file, compression, compressorData, chunkSize, discHeader,
                isoFileSize, rawDataEntries, groupEntries);
            stream.ValidateNoOverlap();
            return stream;
        }

        // Mirrors HasDataOverlap for raw data entries.
        private void ValidateNoOverlap()
        {
            for (var i = 0; i < _rawDataEntries.Length; i++)
            {
                if (_rawDataEntries[i].DataSize == 0)
                {
                    continue;
                }

                var slot = UpperBound(_rawDataEntries[i].DataOffset);
                if (slot < 0 || _dataEntryIndices[slot] != i)
                    throw new InvalidDataException("RVZ raw data entries overlap.");
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

            var remaining = (int)Math.Min(count, Math.Max(0, _length - _position));
            var total = 0;

            if (remaining > 0 && _position < DiscHeaderSize)
            {
                var n = (int)Math.Min(DiscHeaderSize - _position, remaining);
                Buffer.BlockCopy(_discHeader, (int)_position, buffer, offset, n);
                Advance(ref offset, ref remaining, ref total, n);
            }

            while (remaining > 0)
            {
                var n = ReadFromDataEntry(buffer, offset, remaining);
                if (n <= 0)
                {
                    throw new InvalidDataException($"RVZ has no data for offset 0x{_position:X}.");
                }

                Advance(ref offset, ref remaining, ref total, n);
            }

            return total;
        }

        private void Advance(ref int offset, ref int remaining, ref int total, int n)
        {
            offset += n;
            remaining -= n;
            total += n;
            _position += n;
        }

        // One step of ReadFromGroups: reads from the single group containing _position.
        private int ReadFromDataEntry(byte[] buffer, int offset, int count)
        {
            var slot = UpperBound(_position);
            if (slot < 0)
            {
                return 0;
            }

            var entry = _rawDataEntries[_dataEntryIndices[slot]];
            if (_position < entry.DataOffset)
            {
                return 0;
            }

            var skippedData = entry.DataOffset % BlockTotalSize;
            var dataOffset = entry.DataOffset - skippedData;
            var dataSize = entry.DataSize + skippedData;

            var groupInEntry = (_position - dataOffset) / _chunkSize;
            if (groupInEntry >= entry.NumberOfGroups)
            {
                return 0;
            }

            var totalGroupIndex = entry.GroupIndex + groupInEntry;
            if (totalGroupIndex >= _groupEntries.Length)
            {
                throw new InvalidDataException("RVZ group index out of range.");
            }

            var groupOffsetInData = groupInEntry * _chunkSize;
            var offsetInGroup = _position - groupOffsetInData - dataOffset;
            var groupLength = Math.Min(_chunkSize, dataSize - groupOffsetInData);
            var n = (int)Math.Min(groupLength - offsetInGroup, count);

            var group = _groupEntries[totalGroupIndex];
            var groupDataSize = group.DataSize & 0x7FFFFFFF;

            if (groupDataSize == 0)
            {
                Array.Clear(buffer, offset, n);
                return n;
            }

            var groupFileOffset = (long)group.DataOffset << 2;
            if (groupFileOffset != _cachedGroupFileOffset)
            {
                _cachedGroupFileOffset = -1;
                var compressed = (group.DataSize & 0x80000000) != 0;
                _cachedGroup = DecodeGroup(groupFileOffset, (int)groupDataSize, compressed,
                    group.RvzPackedSize, (int)groupLength, groupOffsetInData);
                _cachedGroupLength = (int)groupLength;
                _cachedGroupFileOffset = groupFileOffset;
            }

            if (offsetInGroup + n > _cachedGroupLength)
            {
                throw new InvalidDataException("RVZ group is shorter than expected.");
            }

            Buffer.BlockCopy(_cachedGroup, (int)offsetInGroup, buffer, offset, n);
            return n;
        }

        // Index of the first data entry whose end is greater than offset, or -1.
        private int UpperBound(long offset)
        {
            int lo = 0, hi = _dataEntryEnds.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (_dataEntryEnds[mid] <= offset) lo = mid + 1;
                else hi = mid;
            }

            return lo < _dataEntryEnds.Length ? lo : -1;
        }

        private byte[] DecodeGroup(long fileOffset, int storedSize, bool compressed, uint rvzPackedSize, int groupLength, long groupOffsetInData)
        {
            var stored = new byte[storedSize];
            ReadExactlyAt(_file, fileOffset, stored, 0, storedSize);

            // Uncompressed groups are stored as-is; packed ones still carry the RVZ pack stream.
            var packedLength = rvzPackedSize != 0 ? (int)rvzPackedSize : groupLength;
            var decoded = compressed
                ? Decompress(_compression, _compressorData, _zstd, stored, storedSize, packedLength)
                : stored;

            if (rvzPackedSize == 0)
            {
                if (decoded.Length < groupLength)
                    throw new InvalidDataException("RVZ group decoded short.");
                return decoded;
            }

            return Unpack(decoded, (int)rvzPackedSize, groupLength, groupOffsetInData);
        }

        /// <summary>
        /// Expands an RVZ pack stream (RVZPackDecompressor): big-endian u32 records whose high bit
        /// marks junk data regenerated from a 68-byte seed, otherwise that many literal bytes.
        /// </summary>
        private static byte[] Unpack(byte[] packed, int packedLength, int outputLength, long dataOffset)
        {
            var output = new byte[outputLength];
            var lfg = new RvzLaggedFibonacciGenerator();
            var inPos = 0;
            var outPos = 0;

            while (outPos < outputLength)
            {
                if (inPos + 4 > packedLength)
                    throw new InvalidDataException("RVZ pack stream truncated.");

                var header = ReadU32(packed, inPos);
                inPos += 4;

                var junk = (header & 0x80000000) != 0;
                var size = (int)(header & 0x7FFFFFFF);
                if (size > outputLength - outPos)
                    throw new InvalidDataException("RVZ pack record exceeds group size.");

                if (junk)
                {
                    if (inPos + RvzLaggedFibonacciGenerator.SeedBytes > packedLength)
                        throw new InvalidDataException("RVZ pack seed truncated.");

                    lfg.SetSeed(packed, inPos);
                    lfg.Forward(dataOffset % BlockTotalSize);
                    inPos += RvzLaggedFibonacciGenerator.SeedBytes;

                    lfg.GetBytes(size, output, outPos);
                }
                else
                {
                    if (inPos + size > packedLength)
                        throw new InvalidDataException("RVZ pack literal truncated.");

                    Buffer.BlockCopy(packed, inPos, output, outPos, size);
                    inPos += size;
                }

                outPos += size;
                dataOffset += size;
            }

            if (inPos != packedLength)
                throw new InvalidDataException("RVZ pack stream has trailing data.");

            return output;
        }

        private static byte[] DecompressTable(FileStream file, RvzCompression compression, byte[] compressorData, long offset, uint storedSize, int expectedSize)
        {
            var stored = new byte[storedSize];
            ReadExactlyAt(file, offset, stored, 0, stored.Length);

            if (compression == RvzCompression.None)
            {
                if (stored.Length < expectedSize)
                    throw new InvalidDataException("RVZ table decoded short.");
                return stored;
            }

            if (compression == RvzCompression.Zstd)
            {
                using (var zstd = new ZstdSharp.Decompressor())
                {
                    return Decompress(compression, compressorData, zstd, stored, stored.Length, expectedSize);
                }
            }

            return Decompress(compression, compressorData, null, stored, stored.Length, expectedSize);
        }

        private static byte[] Decompress(RvzCompression compression, byte[] compressorData, ZstdSharp.Decompressor zstd, byte[] input, int inputLength, int outputLength)
        {
            var output = new byte[outputLength];

            switch (compression)
            {
                case RvzCompression.None:
                    if (inputLength < outputLength)
                        throw new InvalidDataException("RVZ stored data is truncated.");
                    Buffer.BlockCopy(input, 0, output, 0, outputLength);
                    return output;

                case RvzCompression.Zstd:
                    var written = zstd.Unwrap(input, 0, inputLength, output, 0, outputLength);
                    if (written != outputLength)
                        throw new InvalidDataException($"RVZ zstd group decoded {written} of {outputLength} bytes.");
                    return output;

                case RvzCompression.Bzip2:
                    using (var source = new MemoryStream(input, 0, inputLength, writable: false))
                    using (var bzip = new BZip2Stream(source, CompressionMode.Decompress, false))
                    {
                        FillFromStream(bzip, output);
                    }
                    return output;

                case RvzCompression.Lzma:
                case RvzCompression.Lzma2:
                    using (var source = new MemoryStream(input, 0, inputLength, writable: false))
                    using (var lzma = new LzmaStream(compressorData, source, inputLength, outputLength, null, compression == RvzCompression.Lzma2))
                    {
                        FillFromStream(lzma, output);
                    }
                    return output;

                default:
                    throw new NotSupportedException($"Unsupported RVZ compression {compression}.");
            }
        }

        private static void FillFromStream(Stream source, byte[] output)
        {
            var total = 0;
            while (total < output.Length)
            {
                var read = source.Read(output, total, output.Length - total);
                if (read <= 0)
                    throw new InvalidDataException($"RVZ group decoded {total} of {output.Length} bytes.");
                total += read;
            }
        }

        private static void ReadExactlyAt(Stream stream, long position, byte[] buffer, int offset, int count)
        {
            stream.Position = position;
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                    throw new EndOfStreamException($"RVZ read past end of file at 0x{position + total:X}.");
                total += read;
            }
        }

        private static uint ReadU32(byte[] b, int p)
        {
            return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
        }

        private static ulong ReadU64(byte[] b, int p)
        {
            return ((ulong)ReadU32(b, p) << 32) | ReadU32(b, p + 4);
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(source, offset, result, 0, length);
            return result;
        }

        private static byte[] Sha1(byte[] data, int offset, int count)
        {
            using (var sha = SHA1.Create())
            {
                return sha.ComputeHash(data, offset, count);
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
                _zstd?.Dispose();
                _file.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
