using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.Xenia
{
    internal static class XeniaTitleIDExtractor
    {
        private const uint Xex2Magic = 0x58455832; // "XEX2"
        private const uint ExecutionIdHeaderId = 0x00040006;
        private const string XdvdfsMagic = "MICROSOFT*XBOX*MEDIA";
        private const int SectorSize = 2048;

        internal class ExecutionInfo
        {
            public uint MediaId;
            public uint Version;
            public uint BaseVersion;
            public uint TitleId;
            public byte Platform;
            public byte ExecutableType;
            public byte DiscNumber;
            public byte DiscCount;
            public uint SaveGameId;

            public string TitleIdHex => TitleId.ToString("X8");
        }

        // Reads the Title ID from a .xex file
        internal static ExecutionInfo GetFromXexFile(string xexPath)
        {
            using (var stream = File.OpenRead(xexPath))
                return FindTitleID(stream);
        }

        // Locates default.xex inside an Xbox 360 .iso (XDVDFS) image and reads its Title ID
        internal static ExecutionInfo GetFromIsoFile(string isoPath, string xexFileName = "default.xex")
        {
            using (var fs = File.OpenRead(isoPath))
            {
                DirEntry entry = null;
                long volumeOffset = 0;
                bool foundAnyVolumeDescriptor = false;

                // Fast path: the standard "trimmed" ISO layout has the real volume
                // descriptor at 0x10000, so try that first without scanning the whole file.
                const long trimmedOffset = 0x10000L;
                if (trimmedOffset + SectorSize <= fs.Length && HasXDVDFSMagicAt(fs, trimmedOffset))
                {
                    foundAnyVolumeDescriptor = true;
                    entry = TryFindFileEntry(fs, trimmedOffset, xexFileName);
                    if (entry != null)
                        volumeOffset = trimmedOffset;
                }

                // Fallback: Xbox 360 discs frequently contain decoy "MICROSOFT*XBOX*MEDIA" as an anti-ripping measure, so the real descriptor isn't at 0x10000 at all.
                // Scan for every sector-aligned occurrence and try each in turn until one actually contains the xex file.
                if (entry == null)
                {
                    var candidateOffsets = new List<long>();
                    foreach (long off in FindXdvdfsVolumeOffsets(fs))
                    {
                        if (off != trimmedOffset) // already tried above
                            candidateOffsets.Add(off);
                    }

                    foundAnyVolumeDescriptor |= candidateOffsets.Count > 0;

                    foreach (long candidateOffset in candidateOffsets)
                    {
                        var candidateEntry = TryFindFileEntry(fs, candidateOffset, xexFileName);
                        if (candidateEntry != null)
                        {
                            entry = candidateEntry;
                            volumeOffset = candidateOffset;
                            break;
                        }
                    }
                }

                if (!foundAnyVolumeDescriptor)
                    throw new InvalidDataException(
                        "Could not locate an XDVDFS volume descriptor in this ISO. " +
                        "It may not be an Xbox 360 disc image, or uses an unusual layout.");

                if (entry == null)
                    throw new FileNotFoundException($"'{xexFileName}' was not found in the ISO's root directory.");

                long xexAbsoluteOffset = (volumeOffset - 0x10000) + (long)entry.StartSector * SectorSize;

                // We only need the header portion of the XEX, not the whole executable.
                int headerBytesToRead = (int)Math.Min(entry.FileSize, 0x8000); // 32KB is comfortably enough
                fs.Seek(xexAbsoluteOffset, SeekOrigin.Begin);
                byte[] headerBytes = new byte[headerBytesToRead];
                ReadFully(fs, headerBytes);

                using (var ms = new MemoryStream(headerBytes))
                    return FindTitleID(ms);
            }
        }

        private static ExecutionInfo FindTitleID(Stream xexFile)
        {
            // Check steam starts with XEX2
            xexFile.Seek(0, SeekOrigin.Begin);
            uint magic = ReadUInt32BE(xexFile);
            if (magic != Xex2Magic)
                throw new InvalidDataException("Not a valid XEX2 file (bad magic).");

            // Get number of header table entries
            xexFile.Seek(0x14, SeekOrigin.Begin);
            uint headerCount = ReadUInt32BE(xexFile);

            // Read though header entries looking for execution info entry
            xexFile.Seek(0x18, SeekOrigin.Begin);
            uint executionInfoOffset = 0;
            bool found = false;

            for (int i = 0; i < headerCount; i++)
            {
                uint id = ReadUInt32BE(xexFile);
                uint value = ReadUInt32BE(xexFile);
                if (id == ExecutionIdHeaderId)
                {
                    executionInfoOffset = value;
                    found = true;
                    break;
                }
            }

            if (!found)
                throw new InvalidDataException("Failed to find execution info in file.");

            // Jump to the offset from the header entry for the execution info
            xexFile.Seek(executionInfoOffset, SeekOrigin.Begin);
            var info = new ExecutionInfo
            {
                MediaId = ReadUInt32BE(xexFile),
                Version = ReadUInt32BE(xexFile),
                BaseVersion = ReadUInt32BE(xexFile),
                TitleId = ReadUInt32BE(xexFile),
                Platform = ReadByte(xexFile),
                ExecutableType = ReadByte(xexFile),
                DiscNumber = ReadByte(xexFile),
                DiscCount = ReadByte(xexFile),
                SaveGameId = ReadUInt32BE(xexFile)
            };

            return info;
        }

        #region ISO Functions
        private class DirEntry
        {
            public uint StartSector;
            public uint FileSize;
            public byte Attributes;
            public string Name;
        }

        // Attempts to find targetName in the root directory of the XDVDFS volume at candidateOffset. 
        private static DirEntry TryFindFileEntry(Stream iso, long candidateOffset, string targetName)
        {
            try
            {
                return FindFileEntry(iso, candidateOffset, targetName);
            }
            catch (InvalidDataException)
            {
                // Garbage rootDirSize/sector - not a real volume descriptor, just a decoy.
                return null;
            }
            catch (EndOfStreamException)
            {
                // Computed root directory offset/size ran past the end of the file.
                return null;
            }
        }

        // Scans the entire image for every sector-aligned offset where the XDVDFS magic
        // string appears.
        private static IEnumerable<long> FindXdvdfsVolumeOffsets(Stream iso)
        {
            // Scan in large chunks (rather than one seek+read per 2048-byte sector, which
            // would be painfully slow on a multi-GB ISO). Covers untrimmed/full dumps where
            // the game partition starts further into the file, as well as decoy signatures.
            const int chunkSize = 4 * 1024 * 1024; // 4MB
            byte[] magicBytes = Encoding.ASCII.GetBytes(XdvdfsMagic);
            byte[] buffer = new byte[chunkSize + magicBytes.Length]; // overlap so we don't miss a match spanning a chunk boundary

            iso.Seek(0, SeekOrigin.Begin);
            long baseOffset = 0;
            int carryOver = 0;

            while (baseOffset < iso.Length)
            {
                int bytesRead = iso.Read(buffer, carryOver, buffer.Length - carryOver);
                if (bytesRead <= 0) break;
                int validLength = carryOver + bytesRead;

                int searchFrom = 0;
                while (true)
                {
                    int found = IndexOf(buffer, validLength, magicBytes, searchFrom);
                    if (found < 0) break;

                    long candidate = baseOffset - carryOver + found;
                    // Only sector-aligned matches (offset % 2048 == 0) can be real volume descriptors.
                    if (candidate % SectorSize == 0)
                        yield return candidate;

                    searchFrom = found + 1;
                }

                // Keep the tail in case the magic string straddles this chunk boundary.
                carryOver = Math.Min(validLength, magicBytes.Length - 1);
                Array.Copy(buffer, validLength - carryOver, buffer, 0, carryOver);
                baseOffset += bytesRead;
            }
        }

        private static int IndexOf(byte[] haystack, int haystackLength, byte[] needle, int startFrom = 0)
        {
            for (int i = startFrom; i <= haystackLength - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        // Finds a file by name (case-insensitive) in the root directory table.
        // The directory table is technically a binary search tree (left/right offsets per
        // entry), but a lot of real-world XISO creation tools don't build a proper BST
        private static DirEntry FindFileEntry(Stream iso, long volumeDescriptorOffset, string targetName)
        {
            // Volume descriptor always sits 32 sectors (0x10000 bytes) into the partition
            long partitionOffset = volumeDescriptorOffset - 0x10000;
            if (partitionOffset < 0)
                throw new InvalidDataException("Volume descriptor offset is too close to the start of the file to be real.");

            // Volume descriptor layout: magic(20) + rootDirSector(4,LE) + rootDirSize(4,LE) + ...
            iso.Seek(volumeDescriptorOffset + 20, SeekOrigin.Begin);
            uint rootDirSector = ReadUInt32LE(iso);
            uint rootDirSize = ReadUInt32LE(iso);

            if (rootDirSize == 0 || rootDirSize > 64 * 1024 * 1024)
                throw new InvalidDataException(
                    $"Root directory table size looks invalid ({rootDirSize} bytes). " +
                    "The detected volume offset is probably wrong for this ISO.");

            long rootDirOffset = partitionOffset + (long)rootDirSector * SectorSize;

            byte[] table = new byte[rootDirSize];
            iso.Seek(rootDirOffset, SeekOrigin.Begin);
            ReadFully(iso, table);

            return ScanDirectoryTable(table, targetName);
        }

        private static DirEntry ScanDirectoryTable(byte[] table, string targetName)
        {
            int offset = 0;

            while (offset + 14 <= table.Length)
            {
                // left/right pointers exist here too, but we deliberately ignore them -
                // see the remarks on FindFileEntry above.
                uint startSector = ReadUInt32LE(table, offset + 4);
                uint fileSize = ReadUInt32LE(table, offset + 8);
                byte attributes = table[offset + 12];
                byte nameLength = table[offset + 13];

                // 0x00 or 0xFF name length means we've hit unused padding at the end of
                // the used portion of the table - nothing more to find.
                if (nameLength == 0x00 || nameLength == 0xFF)
                    break;

                if (offset + 14 + nameLength > table.Length)
                    break; // malformed/truncated entry, stop rather than read out of bounds

                string name = Encoding.ASCII.GetString(table, offset + 14, nameLength);

                if (string.Equals(targetName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new DirEntry
                    {
                        StartSector = startSector,
                        FileSize = fileSize,
                        Attributes = attributes,
                        Name = name
                    };
                }

                // Advance to the next entry: header (14 bytes) + name, rounded up to a
                // 4-byte boundary.
                int entryLength = 14 + nameLength;
                int padded = (entryLength + 3) & ~3;
                offset += padded;
            }

            return null;
        }
        #endregion

        #region Helpers

        private static bool HasXDVDFSMagicAt(Stream iso, long offset)
        {
            iso.Seek(offset, SeekOrigin.Begin);
            byte[] buffer = new byte[XdvdfsMagic.Length];
            if (iso.Read(buffer, 0, buffer.Length) != buffer.Length) return false;
            return Encoding.ASCII.GetString(buffer) == XdvdfsMagic;
        }

        private static void ReadFully(Stream s, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = s.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        private static byte ReadByte(Stream s)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return (byte)b;
        }

        // XEX header fields are big-endian (PowerPC).
        private static uint ReadUInt32BE(Stream s)
        {
            byte[] b = new byte[4];
            ReadFully(s, b);
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        // XDVDFS filesystem fields are little-endian.
        private static uint ReadUInt32LE(Stream s)
        {
            byte[] b = new byte[4];
            ReadFully(s, b);
            return BitConverter.ToUInt32(b, 0);
        }

        private static uint ReadUInt32LE(byte[] buffer, int offset)
        {
            return BitConverter.ToUInt32(buffer, offset);
        }

    }

    #endregion
}