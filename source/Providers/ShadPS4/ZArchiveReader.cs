using System;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.ShadPS4
{
    /// <summary>
    /// Reads single files out of a ZArchive (.zar) game image, the packed format shadPS4 boots
    /// directly. Layout follows Exzap/ZArchive: 64 KiB data blocks (zstd, or raw when a block
    /// would not shrink), then offset records, a name table and a file tree, located through a
    /// big-endian footer at the end of the file.
    /// </summary>
    internal static class ZArchiveReader
    {
        private const int BlockSize = 64 * 1024;
        private const int EntriesPerOffsetRecord = 16;
        private const int OffsetRecordSize = 8 + 2 * EntriesPerOffsetRecord;
        private const int FileTreeEntrySize = 16;
        private const int FooterSize = 16 * 6 + 32 + 8 + 4 + 4;
        private const uint Magic = 0x169f52d6;
        private const uint Version1 = 0x61bf3a01;

        // Bounds for the metadata sections; a PS4 game's tree and names stay far below these.
        private const long MaxNameTableBytes = 64L * 1024 * 1024;
        private const long MaxFileTreeBytes = 64L * 1024 * 1024;
        private const long MaxOffsetRecordBytes = 64L * 1024 * 1024;

        /// <summary>
        /// The contents of <paramref name="innerPath"/> inside the archive, or null when the file
        /// is not a ZArchive, the path is absent or names a directory, the file exceeds
        /// <paramref name="maxBytes"/>, or any part of the archive is malformed.
        /// </summary>
        public static byte[] TryReadFile(string archivePath, string innerPath, int maxBytes)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(innerPath) || maxBytes <= 0)
            {
                return null;
            }

            try
            {
                using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return TryReadFile(stream, innerPath, maxBytes);
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static byte[] TryReadFile(Stream stream, string innerPath, int maxBytes)
        {
            try
            {
                return ReadFile(stream, innerPath, maxBytes);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                                       ex is ArgumentException || ex is OverflowException ||
                                       ex is IndexOutOfRangeException || ex is ZstdSharp.ZstdException)
            {
                return null;
            }
        }

        private static byte[] ReadFile(Stream stream, string innerPath, int maxBytes)
        {
            var archiveSize = stream.Length;
            if (archiveSize < FooterSize)
            {
                return null;
            }

            var footer = ReadBytes(stream, archiveSize - FooterSize, FooterSize);
            if (ReadUInt32(footer, FooterSize - 4) != Magic ||
                ReadUInt32(footer, FooterSize - 8) != Version1 ||
                (long)ReadUInt64(footer, FooterSize - 16) != archiveSize)
            {
                return null;
            }

            var compressedData = ReadSection(footer, 0, archiveSize);
            var offsetRecords = ReadSection(footer, 1, archiveSize);
            var names = ReadSection(footer, 2, archiveSize);
            var fileTree = ReadSection(footer, 3, archiveSize);
            if (compressedData == null || offsetRecords == null || names == null || fileTree == null ||
                offsetRecords.Size > MaxOffsetRecordBytes || offsetRecords.Size % OffsetRecordSize != 0 ||
                names.Size > MaxNameTableBytes ||
                fileTree.Size > MaxFileTreeBytes || fileTree.Size % FileTreeEntrySize != 0 || fileTree.Size == 0)
            {
                return null;
            }

            var nameTable = ReadBytes(stream, names.Offset, (int)names.Size);
            var tree = ReadBytes(stream, fileTree.Offset, (int)fileTree.Size);

            var node = LookUp(tree, nameTable, innerPath);
            if (node < 0 || !IsFile(tree, node))
            {
                return null;
            }

            var fileOffset = GetFileOffset(tree, node);
            var fileSize = GetFileSize(tree, node);
            if (fileSize > (ulong)maxBytes)
            {
                return null;
            }

            var records = ReadBytes(stream, offsetRecords.Offset, (int)offsetRecords.Size);
            var result = new byte[(int)fileSize];
            var block = new byte[BlockSize];
            using (var zstd = new ZstdSharp.Decompressor())
            {
                var written = 0;
                var rawOffset = fileOffset;
                while (written < result.Length)
                {
                    var blockIndex = rawOffset / BlockSize;
                    var blockOffset = (int)(rawOffset % BlockSize);
                    var step = Math.Min(result.Length - written, BlockSize - blockOffset);
                    if (!LoadBlock(stream, records, compressedData, blockIndex, block, zstd))
                    {
                        return null;
                    }

                    Buffer.BlockCopy(block, blockOffset, result, written, step);
                    written += step;
                    rawOffset += (ulong)step;
                }
            }

            return result;
        }

        private static bool LoadBlock(Stream stream, byte[] records, Section compressedData, ulong blockIndex, byte[] block, ZstdSharp.Decompressor zstd)
        {
            var recordIndex = blockIndex / EntriesPerOffsetRecord;
            var subIndex = (int)(blockIndex % EntriesPerOffsetRecord);
            if (recordIndex >= (ulong)(records.Length / OffsetRecordSize))
            {
                return false;
            }

            var recordStart = (int)recordIndex * OffsetRecordSize;
            var offset = ReadUInt64(records, recordStart);
            for (var i = 0; i < subIndex; i++)
            {
                offset += (ulong)ReadUInt16(records, recordStart + 8 + i * 2) + 1;
            }

            var compressedSize = ReadUInt16(records, recordStart + 8 + subIndex * 2) + 1;
            if (offset + (ulong)compressedSize > (ulong)compressedData.Size)
            {
                return false;
            }

            var absoluteOffset = compressedData.Offset + (long)offset;
            if (compressedSize == BlockSize)
            {
                var raw = ReadBytes(stream, absoluteOffset, BlockSize);
                Buffer.BlockCopy(raw, 0, block, 0, BlockSize);
                return true;
            }

            var compressed = ReadBytes(stream, absoluteOffset, compressedSize);
            return zstd.Unwrap(compressed, 0, compressedSize, block, 0, BlockSize) == BlockSize;
        }

        private static int LookUp(byte[] tree, byte[] nameTable, string path)
        {
            var current = 0;
            foreach (var part in path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsFile(tree, current))
                {
                    return -1;
                }

                var start = ReadUInt32(tree, current * FileTreeEntrySize + 4);
                var count = ReadUInt32(tree, current * FileTreeEntrySize + 8);
                var entryCount = (uint)(tree.Length / FileTreeEntrySize);
                if (start > entryCount || count > entryCount - start)
                {
                    return -1;
                }

                var match = -1;
                for (var index = start; index < start + count; index++)
                {
                    var name = GetName(nameTable, ReadUInt32(tree, (int)index * FileTreeEntrySize) & 0x7FFFFFFF);
                    if (string.Equals(name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        match = (int)index;
                        break;
                    }
                }

                if (match < 0)
                {
                    return -1;
                }

                current = match;
            }

            return current;
        }

        private static string GetName(byte[] nameTable, uint nameOffset)
        {
            if (nameOffset == 0x7FFFFFFF || nameOffset >= (uint)nameTable.Length)
            {
                return null;
            }

            var offset = (int)nameOffset;
            int length = nameTable[offset] & 0x7F;
            if ((nameTable[offset] & 0x80) != 0)
            {
                if (offset + 1 >= nameTable.Length)
                {
                    return null;
                }

                length |= nameTable[offset + 1] << 7;
                offset += 2;
            }
            else
            {
                offset += 1;
            }

            return offset + length > nameTable.Length
                ? null
                : Encoding.UTF8.GetString(nameTable, offset, length);
        }

        private static bool IsFile(byte[] tree, int node)
        {
            return (ReadUInt32(tree, node * FileTreeEntrySize) & 0x80000000) != 0;
        }

        private static ulong GetFileOffset(byte[] tree, int node)
        {
            var start = node * FileTreeEntrySize;
            return ReadUInt32(tree, start + 4) | ((ulong)(ReadUInt32(tree, start + 12) & 0xFFFF) << 32);
        }

        private static ulong GetFileSize(byte[] tree, int node)
        {
            var start = node * FileTreeEntrySize;
            return ReadUInt32(tree, start + 8) | ((ulong)(ReadUInt32(tree, start + 12) & 0xFFFF0000) << 16);
        }

        private sealed class Section
        {
            public long Offset;
            public long Size;
        }

        private static Section ReadSection(byte[] footer, int index, long archiveSize)
        {
            var offset = ReadUInt64(footer, index * 16);
            var size = ReadUInt64(footer, index * 16 + 8);
            if (offset > (ulong)archiveSize || size > (ulong)archiveSize - offset)
            {
                return null;
            }

            return new Section { Offset = (long)offset, Size = (long)size };
        }

        private static byte[] ReadBytes(Stream stream, long offset, int count)
        {
            var buffer = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    throw new InvalidDataException("Unexpected end of ZArchive.");
                }

                read += n;
            }

            return buffer;
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static ulong ReadUInt64(byte[] data, int offset)
        {
            return ((ulong)ReadUInt32(data, offset) << 32) | ReadUInt32(data, offset + 4);
        }
    }
}
