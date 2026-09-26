using System;
using System.Security.Cryptography;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// Direct ports of rcheevos's ISO9660 file lookup and file hashing (hash_disc.c), which read
    /// sectors by absolute LBA. Used where a file must be located on one track and read from
    /// another, which a filesystem library over a single track cannot express.
    /// </summary>
    internal static class Iso9660SectorLocator
    {
        /// <summary>
        /// rc_cd_find_file_sector (hash_disc.c:82-170): the absolute sector of a file or directory,
        /// or 0 when not found. Only the root directory is scanned past its first sector.
        /// </summary>
        public static uint FindFileSector(DiscTrack track, string path, out uint size)
        {
            size = 0;
            if (track == null || string.IsNullOrEmpty(path))
            {
                return 0;
            }

            var buffer = new byte[2048];
            uint sector;
            uint numSectors = 0;

            if (path[0] == '\\')
            {
                path = path.Substring(1);
            }

            var slash = path.LastIndexOf('\\');
            if (slash >= 0)
            {
                sector = FindFileSector(track, path.Substring(0, slash), out _);
                if (sector == 0)
                {
                    return 0;
                }

                path = path.Substring(slash + 1);
            }
            else
            {
                if (track.ReadSector((long)track.FirstTrackSector + 16, buffer, 0, 256) == 0)
                {
                    return 0;
                }

                // The root directory record starts at 156; its extent is 2 bytes in.
                sector = (uint)(buffer[156 + 2] | (buffer[156 + 3] << 8) | (buffer[156 + 4] << 16));

                var logicalBlockSize = (uint)(buffer[128] | (buffer[128 + 1] << 8));
                if (logicalBlockSize == 0)
                {
                    numSectors = 1;
                }
                else
                {
                    numSectors = ReadUInt32(buffer, 156 + 10) / logicalBlockSize;
                }
            }

            var name = Encoding.ASCII.GetBytes(path);
            var nameLength = name.Length;

            if (track.ReadSector(sector, buffer, 0, buffer.Length) == 0)
            {
                return 0;
            }

            var offset = 0;
            while (true)
            {
                if (offset >= buffer.Length || buffer[offset] == 0)
                {
                    // End of this directory block; keep scanning when the directory spans sectors.
                    if (numSectors > 1)
                    {
                        --numSectors;
                        if (track.ReadSector(++sector, buffer, 0, buffer.Length) != 0)
                        {
                            offset = 0;
                            continue;
                        }
                    }

                    break;
                }

                // The name is 33 bytes into the record, as "FILENAME;version" or "DIRECTORY".
                if ((ByteAt(buffer, offset + 32) == nameLength || ByteAt(buffer, offset + 33 + nameLength) == ';') &&
                    NameMatches(buffer, offset + 33, name))
                {
                    size = ReadUInt32(buffer, offset + 10);
                    return (uint)(buffer[offset + 2] | (buffer[offset + 3] << 8) | (buffer[offset + 4] << 16));
                }

                offset += buffer[offset];
            }

            return 0;
        }

        /// <summary>
        /// rc_hash_cd_file (hash_disc.c:174-208): appends <paramref name="size"/> bytes (capped at
        /// 64 MB) read sector by sector from <paramref name="sector"/>. The first sector must be
        /// whole; the file ends early at the end of the track.
        /// </summary>
        public static bool HashFile(HashAlgorithm md5, DiscTrack track, uint sector, uint size)
        {
            if (track == null)
            {
                return false;
            }

            var buffer = new byte[DiscTrack.RunSectors * DiscTrack.CookedSectorSize];
            if (track.ReadSector(sector, buffer, 0, DiscTrack.CookedSectorSize) < DiscTrack.CookedSectorSize)
            {
                return false;
            }

            long remaining = Math.Min(size, (uint)HashUtils.MaxHashBytes);
            var first = (int)Math.Min(remaining, DiscTrack.CookedSectorSize);
            md5.TransformBlock(buffer, 0, first, null, 0);
            remaining -= first;

            long next = (long)sector + 1;
            while (remaining > 0)
            {
                var sectors = (int)Math.Min(DiscTrack.RunSectors, (remaining + DiscTrack.CookedSectorSize - 1) / DiscTrack.CookedSectorSize);
                var wanted = (int)Math.Min(remaining, (long)sectors * DiscTrack.CookedSectorSize);
                var read = track.ReadSector(next, buffer, 0, wanted);
                if (read <= 0)
                {
                    break;
                }

                md5.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
                next += sectors;
                if (read < wanted)
                {
                    break;
                }
            }

            return true;
        }

        private static bool NameMatches(byte[] buffer, int offset, byte[] name)
        {
            if (offset + name.Length > buffer.Length)
            {
                return false;
            }

            for (var i = 0; i < name.Length; i++)
            {
                if (ToLower(buffer[offset + i]) != ToLower(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte ByteAt(byte[] buffer, int index)
        {
            return index >= 0 && index < buffer.Length ? buffer[index] : (byte)0;
        }

        private static byte ToLower(byte b)
        {
            return b >= 'A' && b <= 'Z' ? (byte)(b + 32) : b;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }
    }
}
