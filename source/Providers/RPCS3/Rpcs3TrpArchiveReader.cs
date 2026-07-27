using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Text;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// A single named entry inside a TROPHY.TRP archive.
    /// </summary>
    internal sealed class Rpcs3TrpEntry
    {
        public string Name { get; set; }
        public long Offset { get; set; }
        public long Size { get; set; }
    }

    /// <summary>
    /// Reader for the PS3 TROPHY.TRP container format.
    /// Layout (all integers big-endian, per rpcs3 Loader/TRP.h):
    /// header = magic u32 (0xDCA24D00), version u32, file size u64,
    /// entry count u32, element size u32 (0x40), dev flag u32, sha1[20],
    /// padding[16] (version 2+). Entry table follows the header; each entry
    /// is 64 bytes: name char[32] (NUL-padded ASCII), offset u64, size u64,
    /// 16 bytes reserved. SFM entries contain plain XML trophy documents.
    /// </summary>
    internal static class Rpcs3TrpArchiveReader
    {
        private const uint TrpMagic = 0xDCA24D00;
        private const int EntrySize = 64;
        private const int EntryNameSize = 32;
        private const int MaxEntries = 4096;
        private const int HeaderSizeV1 = 0x30;
        private const int HeaderSizeV2 = 0x40;

        public static bool HasTrpMagic(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 4 && ReadUInt32BE(bytes, 0) == TrpMagic;
        }

        /// <summary>
        /// Reads the entry table from a TRP file's bytes.
        /// Returns null when the bytes are not a structurally valid TRP archive;
        /// never throws.
        /// </summary>
        public static IReadOnlyList<Rpcs3TrpEntry> ReadEntries(byte[] trpBytes, ILogger logger = null)
        {
            if (!HasTrpMagic(trpBytes) || trpBytes.Length < HeaderSizeV1)
            {
                return null;
            }

            var version = ReadUInt32BE(trpBytes, 0x04);
            var entryCount = (int)ReadUInt32BE(trpBytes, 0x10);
            if (entryCount <= 0 || entryCount > MaxEntries)
            {
                logger?.Debug($"[RPCS3] TRP entry count {entryCount} out of range");
                return null;
            }

            var tableStart = ResolveEntryTableStart(trpBytes, version, entryCount);
            if (tableStart < 0)
            {
                logger?.Debug("[RPCS3] Could not locate a valid TRP entry table");
                return null;
            }

            var entries = new List<Rpcs3TrpEntry>(entryCount);
            for (var i = 0; i < entryCount; i++)
            {
                var entryOffset = tableStart + (i * EntrySize);
                var name = ReadEntryName(trpBytes, entryOffset);
                var dataOffset = (long)ReadUInt64BE(trpBytes, entryOffset + EntryNameSize);
                var dataSize = (long)ReadUInt64BE(trpBytes, entryOffset + EntryNameSize + 8);

                if (string.IsNullOrWhiteSpace(name) ||
                    dataOffset < 0 ||
                    dataSize < 0 ||
                    dataOffset + dataSize > trpBytes.Length)
                {
                    continue;
                }

                entries.Add(new Rpcs3TrpEntry { Name = name, Offset = dataOffset, Size = dataSize });
            }

            return entries;
        }

        /// <summary>
        /// Extracts a named entry's bytes, or null when absent.
        /// </summary>
        public static byte[] ExtractEntry(byte[] trpBytes, IReadOnlyList<Rpcs3TrpEntry> entries, string entryName)
        {
            if (trpBytes == null || entries == null || string.IsNullOrWhiteSpace(entryName))
            {
                return null;
            }

            foreach (var entry in entries)
            {
                if (!string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = new byte[entry.Size];
                Array.Copy(trpBytes, entry.Offset, data, 0, (int)entry.Size);
                return data;
            }

            return null;
        }

        /// <summary>
        /// Extracts a named entry as UTF-8 text (BOM stripped), or null when absent.
        /// </summary>
        public static string ExtractEntryText(byte[] trpBytes, IReadOnlyList<Rpcs3TrpEntry> entries, string entryName)
        {
            var data = ExtractEntry(trpBytes, entries, entryName);
            if (data == null)
            {
                return null;
            }

            var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(data, start, data.Length - start);
        }

        /// <summary>
        /// Resolves where the entry table starts. The header is 0x30 bytes in
        /// version 1 and 0x40 bytes in version 2+; each candidate is validated
        /// by checking the first entry's name looks like a NUL-padded ASCII
        /// filename, so a mis-declared version cannot misparse the table.
        /// </summary>
        private static int ResolveEntryTableStart(byte[] trpBytes, uint version, int entryCount)
        {
            var candidates = version >= 2
                ? new[] { HeaderSizeV2, HeaderSizeV1 }
                : new[] { HeaderSizeV1, HeaderSizeV2 };

            foreach (var candidate in candidates)
            {
                if ((long)candidate + ((long)entryCount * EntrySize) > trpBytes.Length)
                {
                    continue;
                }

                if (LooksLikeValidEntryName(trpBytes, candidate))
                {
                    return candidate;
                }
            }

            return -1;
        }

        private static string ReadEntryName(byte[] bytes, int offset)
        {
            var length = 0;
            while (length < EntryNameSize && bytes[offset + length] != 0)
            {
                length++;
            }

            return length == 0 ? null : Encoding.ASCII.GetString(bytes, offset, length);
        }

        private static bool LooksLikeValidEntryName(byte[] bytes, int offset)
        {
            var sawCharacter = false;
            var terminated = false;

            for (var i = 0; i < EntryNameSize; i++)
            {
                var value = bytes[offset + i];
                if (value == 0)
                {
                    terminated = true;
                }
                else if (terminated || value < 0x20 || value > 0x7E)
                {
                    // Non-printable byte, or data after the NUL padding began.
                    return false;
                }
                else
                {
                    sawCharacter = true;
                }
            }

            return sawCharacter && terminated;
        }

        private static uint ReadUInt32BE(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static ulong ReadUInt64BE(byte[] bytes, int offset)
        {
            return ((ulong)ReadUInt32BE(bytes, offset) << 32) | ReadUInt32BE(bytes, offset + 4);
        }
    }
}
