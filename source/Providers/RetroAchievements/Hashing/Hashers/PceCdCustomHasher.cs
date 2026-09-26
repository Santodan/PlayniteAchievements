using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    /// <summary>rc_hash_pce_cd and rc_hash_pce_track (hash_disc.c:714-789).</summary>
    internal sealed class PceCdCustomHasher : DiscBasedHasher
    {
        private static readonly byte[] Signature = Encoding.ASCII.GetBytes("PC Engine CD-ROM SYSTEM");

        public PceCdCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PC Engine CD (title + boot code MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            using (var track = DiscImage.Open(source).OpenTrack(DiscTrackSelector.FirstData))
            {
                if (track == null)
                {
                    WarnOnce($"[RA] {Name}: No data track: {source.Path}");
                    return Array.Empty<string>();
                }

                return await HashTrackAsync(track, source.Path, cancel).ConfigureAwait(false);
            }
        }

        /// <summary>True when the second sector of the track carries the PC Engine CD header.</summary>
        internal static bool HasSignature(byte[] header)
        {
            return HashUtils.MatchesAt(header, 32, Signature);
        }

        /// <summary>Hashes an already-open track (rc_hash_pce_track).</summary>
        internal async Task<IReadOnlyList<string>> HashTrackAsync(DiscTrack track, string filePath, CancellationToken cancel)
        {
            var header = new byte[128];
            if (track.ReadSector((long)track.FirstTrackSector + 1, header, 0, header.Length) < header.Length)
            {
                return Array.Empty<string>();
            }

            if (HasSignature(header))
            {
                using (var md5 = MD5.Create())
                {
                    // The title is the last 22 bytes of the header.
                    md5.TransformBlock(header, 106, 22, null, 0);

                    // Program sector (3 bytes big-endian, relative to the track) and sector count.
                    var programSector = (header[0] << 16) | (header[1] << 8) | header[2];
                    var numSectors = header[3];
                    if (!await HashSectorsAsync(md5, track, (long)track.FirstTrackSector + programSector, numSectors, cancel).ConfigureAwait(false))
                    {
                        return Array.Empty<string>();
                    }

                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }

            // GameExpress discs use a standard ISO9660 filesystem: hash BOOT.BIN.
            using (var iso = new DiscUtilsFacade(track))
            using (var boot = iso.OpenFileOrNull("BOOT.BIN"))
            {
                if (boot == null)
                {
                    WarnOnce($"[RA] {Name}: Not a PC Engine CD image (no header signature, no BOOT.BIN): {filePath}");
                    return Array.Empty<string>();
                }

                var length = boot.Length;
                if (length >= HashUtils.MaxHashBytes)
                {
                    WarnOnce($"[RA] {Name}: BOOT.BIN too large to hash: {filePath}");
                    return Array.Empty<string>();
                }

                using (var md5 = MD5.Create())
                {
                    await HashUtils.AppendStreamAsync(md5, boot, length, cancel).ConfigureAwait(false);
                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
        }

        /// <summary>
        /// Appends <paramref name="numSectors"/> whole 2048-byte sectors from <paramref name="lba"/>,
        /// read in runs. False when the track ends first.
        /// </summary>
        internal static async Task<bool> HashSectorsAsync(HashAlgorithm md5, DiscTrack track, long lba, long numSectors, CancellationToken cancel)
        {
            var buffer = new byte[DiscTrack.RunSectors * DiscTrack.CookedSectorSize];
            while (numSectors > 0)
            {
                cancel.ThrowIfCancellationRequested();

                var sectors = (int)Math.Min(DiscTrack.RunSectors, numSectors);
                var wanted = sectors * DiscTrack.CookedSectorSize;
                var read = await track.ReadSectorRunAsync(lba, sectors, DiscTrack.CookedSectorSize, buffer, cancel).ConfigureAwait(false);
                if (read < wanted)
                {
                    return false;
                }

                md5.TransformBlock(buffer, 0, wanted, null, 0);
                lba += sectors;
                numSectors -= sectors;
            }

            return true;
        }
    }
}
