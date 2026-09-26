using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class ThreeDoCustomHasher : DiscBasedHasher
    {
        public ThreeDoCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "3DO (OperaFS header + LaunchMe MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            var operafsIdentifier = new byte[] { 0x01, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x01 };

            using (var track = DiscImage.Open(source).OpenTrack(DiscTrackSelector.FirstData))
            using (var md5 = MD5.Create())
            {
                if (track == null)
                {
                    return Array.Empty<string>();
                }

                // Sectors are addressed from the start of the track (rc_hash_3do reads track 1 from sector 0).
                bool ReadWhole(long sectorIndex, byte[] target) =>
                    track.ReadSector(track.FirstTrackSector + sectorIndex, target, 0, target.Length) == target.Length;

                var sector0 = new byte[2048];
                if (!ReadWhole(0, sector0))
                {
                    return Array.Empty<string>();
                }

                // Volume header is first 132 bytes of sector 0.
                for (var i = 0; i < operafsIdentifier.Length; i++)
                {
                    if (sector0[i] != operafsIdentifier[i])
                    {
                        WarnOnce($"[RA] {Name}: Not an OperaFS disc: {filePath}");
                        return Array.Empty<string>();
                    }
                }

                md5.TransformBlock(sector0, 0, 132, null, 0);

                // Block size at 0x4D..0x4F (big-endian 3 bytes).
                var blockSize = sector0[0x4D] * 65536 + sector0[0x4E] * 256 + sector0[0x4F];
                if (blockSize <= 0)
                {
                    return Array.Empty<string>();
                }

                // Root directory block location at 0x65..0x67 (big-endian 3 bytes), multiplied by blockSize.
                var rootBlockLocation = sector0[0x65] * 65536 + sector0[0x66] * 256 + sector0[0x67];
                var rootDirAddress = (long)rootBlockLocation * blockSize;
                var rootDirSector = (int)(rootDirAddress / 2048);

                // Scan directory for LaunchMe.
                var dirSector = rootDirSector;
                var dirBaseAddress = rootDirAddress;

                long launchMeAddress = 0;
                var launchMeSize = 0;

                var sector = new byte[2048];
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();

                    if (!ReadWhole(dirSector, sector))
                    {
                        return Array.Empty<string>();
                    }

                    var entryOffset = sector[0x12] * 256 + sector[0x13];
                    var entryStop = sector[0x0D] * 65536 + sector[0x0E] * 256 + sector[0x0F];

                    while (entryOffset < entryStop)
                    {
                        if (sector[entryOffset + 0x03] == 0x02)
                        {
                            var name = ReadNullTerminatedAscii(sector, entryOffset + 0x20, 32);
                            if (string.Equals(name, "LaunchMe", StringComparison.OrdinalIgnoreCase))
                            {
                                var fileBlockSize = sector[entryOffset + 0x0D] * 65536 + sector[entryOffset + 0x0E] * 256 + sector[entryOffset + 0x0F];
                                var fileBlockLocation = sector[entryOffset + 0x45] * 65536 + sector[entryOffset + 0x46] * 256 + sector[entryOffset + 0x47];
                                launchMeAddress = (long)fileBlockLocation * fileBlockSize;
                                launchMeSize = sector[entryOffset + 0x11] * 65536 + sector[entryOffset + 0x12] * 256 + sector[entryOffset + 0x13];
                                break;
                            }
                        }

                        var extraCopies = sector[entryOffset + 0x43];
                        entryOffset += 0x48 + extraCopies * 4;
                    }

                    if (launchMeSize != 0)
                    {
                        break;
                    }

                    // Directory continuation pointer at 0x02..0x03.
                    var continuation = sector[0x02] * 256 + sector[0x03];
                    if (continuation == 0xFFFF)
                    {
                        break;
                    }

                    var nextOffsetBytes = continuation * blockSize;
                    dirSector = (int)((dirBaseAddress + nextOffsetBytes) / 2048);
                }

                if (launchMeSize == 0)
                {
                    WarnOnce($"[RA] {Name}: Could not find LaunchMe: {filePath}");
                    return Array.Empty<string>();
                }

                // LaunchMe is read in runs of whole sectors; the last sector is only partly hashed.
                long launchSector = launchMeAddress / 2048;
                var remaining = Math.Min(launchMeSize, HashUtils.MaxHashBytes);
                var run = new byte[DiscTrack.RunSectors * 2048];
                while (remaining > 0)
                {
                    cancel.ThrowIfCancellationRequested();

                    var sectors = Math.Min(DiscTrack.RunSectors, (remaining + 2047) / 2048);
                    var read = await track.ReadSectorRunAsync(track.FirstTrackSector + launchSector, sectors, 2048, run, cancel).ConfigureAwait(false);
                    if (read < sectors * 2048)
                    {
                        return Array.Empty<string>();
                    }

                    var toHash = Math.Min(sectors * 2048, remaining);
                    md5.TransformBlock(run, 0, toHash, null, 0);
                    remaining -= toHash;
                    launchSector += sectors;
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return new[] { HashUtils.ToHexLower(md5.Hash) };
            }
        }

        private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int maxLen)
        {
            var end = offset;
            var max = Math.Min(buffer.Length, offset + maxLen);
            while (end < max && buffer[end] != 0)
            {
                end++;
            }
            return Encoding.ASCII.GetString(buffer, offset, end - offset);
        }
    }
}
