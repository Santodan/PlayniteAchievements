using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal static class CsoUtils
    {
        private const uint CisoMagic = 0x4F534943; // "CISO" in little-endian
        internal const int CisoHeaderSize = 24;
        internal const uint CisoNotCompressedMask = 0x80000000;
        private const uint CisoOffsetMask = 0x7FFFFFFF;
        internal const byte CsoV2 = 2;

        public static bool IsCsoPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var ext = Path.GetExtension(filePath);
            return ext != null &&
                   (ext.Equals(".cso", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".ciso", StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryReadHeader(string filePath, out CsoHeader header)
        {
            header = default;

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return TryReadHeader(fs, out header);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryReadHeader(Stream stream, out CsoHeader header)
        {
            header = default;

            try
            {
                var buffer = new byte[CisoHeaderSize];
                var bytesRead = stream.Read(buffer, 0, CisoHeaderSize);
                if (bytesRead < CisoHeaderSize) return false;

                var magic = BitConverter.ToUInt32(buffer, 0);
                if (magic != CisoMagic) return false;

                header = new CsoHeader
                {
                    Magic = magic,
                    HeaderSize = BitConverter.ToUInt32(buffer, 4),
                    UncompressedSize = BitConverter.ToUInt64(buffer, 8),
                    BlockSize = BitConverter.ToUInt32(buffer, 16),
                    Version = buffer[20],
                    IndexShift = buffer[21]
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Opens a CSO image for random access. Only the blocks a read touches are inflated,
        /// so hashing a few files from the image does not decompress the whole disc.
        /// </summary>
        public static CsoStream OpenStream(string csoPath)
        {
            return CsoStream.Open(csoPath);
        }

        /// <summary>
        /// Decompresses the whole image to a temporary ISO. Only needed by consumers that
        /// require a file path; hashers read through <see cref="OpenStream"/>.
        /// </summary>
        public static ArchiveUtils.TempFile DecompressToTempFile(string csoPath, Action<ulong, ulong> progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(csoPath))
                throw new ArgumentNullException(nameof(csoPath));

            var outPath = Path.Combine(Path.GetTempPath(), $"PlayniteAchievements_cso_{Guid.NewGuid():N}.iso");
            var outFileCreated = false;

            try
            {
                using (var inStream = CsoStream.Open(csoPath))
                using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    outFileCreated = true;

                    var total = (ulong)inStream.Length;
                    var buffer = new byte[1024 * 1024];
                    ulong written = 0;
                    int read;
                    while ((read = inStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outStream.Write(buffer, 0, read);
                        written += (ulong)read;
                        progressCallback?.Invoke(written, total);
                    }

                    if (written != total)
                    {
                        throw new EndOfStreamException($"CSO decoded {written} of {total} bytes.");
                    }
                }
            }
            catch
            {
                if (outFileCreated)
                {
                    TryDeleteFile(outPath);
                }

                throw;
            }

            return new ArchiveUtils.TempFile(outPath);
        }

        internal static void ValidateHeader(CsoHeader header)
        {
            if (header.BlockSize == 0)
            {
                throw new InvalidDataException("Invalid CSO header: block size is 0.");
            }

            if (header.BlockSize > int.MaxValue)
            {
                throw new InvalidDataException($"Unsupported CSO block size: {header.BlockSize}.");
            }

            if (header.IndexShift > 31)
            {
                throw new InvalidDataException($"Invalid CSO index shift: {header.IndexShift}.");
            }

            if (header.UncompressedSize > long.MaxValue)
            {
                throw new InvalidDataException($"CSO file is too large to process: {header.UncompressedSize} bytes.");
            }
        }

        internal static int GetBlockCount(CsoHeader header)
        {
            if (header.UncompressedSize == 0)
            {
                return 0;
            }

            var blockCount = (header.UncompressedSize + header.BlockSize - 1) / header.BlockSize;
            if (blockCount > int.MaxValue - 1)
            {
                throw new InvalidDataException($"CSO has too many blocks to process: {blockCount}.");
            }

            return (int)blockCount;
        }

        internal static IEnumerable<long> GetIndexOffsetCandidates(CsoHeader header, long fileLength, long indexSize)
        {
            var defaultOffset = (long)CisoHeaderSize;
            var declaredOffset = header.HeaderSize >= CisoHeaderSize ? (long)header.HeaderSize : defaultOffset;

            // CSO v1 header_size is often unreliable in the wild; prefer canonical 24-byte header.
            if (defaultOffset + indexSize <= fileLength)
            {
                yield return defaultOffset;
            }

            if (declaredOffset != defaultOffset && declaredOffset + indexSize <= fileLength)
            {
                yield return declaredOffset;
            }
        }

        internal static uint[] ReadBlockIndex(Stream stream, int indexEntryCount)
        {
            var bytes = new byte[checked(indexEntryCount * sizeof(uint))];
            ReadExactly(stream, bytes, 0, bytes.Length);

            var result = new uint[indexEntryCount];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                for (var i = 0; i < result.Length; i++)
                {
                    var v = result[i];
                    result[i] = (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
                }
            }

            return result;
        }

        internal static long ResolveOffsetBase(uint[] blockIndex, CsoHeader header, long indexOffset, long indexSize, long fileLength)
        {
            if (blockIndex == null || blockIndex.Length == 0)
            {
                return 0;
            }

            var firstOffset = DecodeEntryOffset(blockIndex[0], header.IndexShift, 0);
            var minDataStart = indexOffset + indexSize;

            // Most CSO files store absolute file offsets.
            if (firstOffset >= minDataStart && firstOffset < fileLength)
            {
                return 0;
            }

            // Fallback for non-standard files that store offsets relative to index start.
            var relativeFirstOffset = firstOffset + indexOffset;
            if (relativeFirstOffset >= minDataStart && relativeFirstOffset < fileLength)
            {
                return indexOffset;
            }

            throw new InvalidDataException("Unable to determine CSO data offset base from block index.");
        }

        /// <summary>
        /// Checks that every block's stored range is ordered, non-empty and inside the file,
        /// so random-access reads can trust the index without re-validating per block.
        /// </summary>
        internal static void ValidateBlockIndex(uint[] blockIndex, CsoHeader header, long offsetBase, long fileLength)
        {
            for (var i = 0; i + 1 < blockIndex.Length; i++)
            {
                var blockOffset = DecodeEntryOffset(blockIndex[i], header.IndexShift, offsetBase);
                var nextBlockOffset = DecodeEntryOffset(blockIndex[i + 1], header.IndexShift, offsetBase);

                if (nextBlockOffset < blockOffset)
                {
                    throw new InvalidDataException($"Invalid CSO block index ordering at block {i}.");
                }

                if (nextBlockOffset - blockOffset <= 0)
                {
                    throw new InvalidDataException($"Invalid CSO block size at block {i}: {nextBlockOffset - blockOffset}.");
                }

                if (blockOffset < 0 || nextBlockOffset > fileLength)
                {
                    throw new InvalidDataException($"CSO block {i} points outside file bounds.");
                }
            }
        }

        internal static long DecodeEntryOffset(uint indexEntry, byte indexShift, long offsetBase)
        {
            var rawOffset = (long)(indexEntry & CisoOffsetMask);
            var shiftedOffset = rawOffset << indexShift;
            return shiftedOffset + offsetBase;
        }

        internal static bool IsCompressedBlock(byte version, uint indexEntry, long storedSize, uint blockSize)
        {
            if (version == CsoV2)
            {
                // In CSO v2, blocks with stored size >= block size must be treated as uncompressed.
                return storedSize < blockSize;
            }

            return (indexEntry & CisoNotCompressedMask) == 0;
        }

        internal static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of stream (wanted {count} bytes, read {totalRead} bytes).");
                }

                totalRead += read;
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        public struct CsoHeader
        {
            public uint Magic;
            public uint HeaderSize;
            public ulong UncompressedSize;
            public uint BlockSize;
            public byte Version;
            public byte IndexShift;
        }
    }
}
