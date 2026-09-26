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
    /// <summary>rc_hash_jaguar_cd (hash_disc.c:544-655).</summary>
    internal sealed class AtariJaguarCdCustomHasher : DiscBasedHasher
    {
        private const int RawSectorSize = 2352;
        private static readonly byte[] NormalHeader = Encoding.ASCII.GetBytes("ATARI APPROVED DATA HEADER ATRI ");
        private static readonly byte[] ByteswappedHeader = Encoding.ASCII.GetBytes("TARA IPARPVODED TA AEHDAREA RT I");
        private static readonly byte[] HomebrewMarker = Encoding.ASCII.GetBytes("RT!IRTKA");
        private const string HomebrewHash = "254487b59ab21bc005338e85cbf9fd2f";

        private readonly string _homebrewHashOverride;

        public AtariJaguarCdCustomHasher(ILogger logger) : base(logger) { }

        /// <summary>
        /// Treats <paramref name="homebrewHashOverride"/> as the shared homebrew bootloader hash too,
        /// as rcheevos's _rc_hash_jaguar_cd_homebrew_hash test hook does.
        /// </summary>
        internal AtariJaguarCdCustomHasher(ILogger logger, string homebrewHashOverride) : base(logger)
        {
            _homebrewHashOverride = homebrewHashOverride;
        }

        public override string Name => "Atari Jaguar CD (boot code MD5)";

        protected override Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            return Task.Run(() => Compute(source, cancel), cancel);
        }

        private IReadOnlyList<string> Compute(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            var image = DiscImage.Open(source);

            // The boot header is in the first sector of the first track of the second session. rcheevos
            // opens only cue sheets; a plain image is read as one track of raw sectors.
            var track = image.IsMultiTrack
                ? image.OpenTrack(DiscTrackSelector.FirstOfSecondSession)
                : image.OpenRawTrack(GuessSectorSize(source.Length));
            if (track == null)
            {
                WarnOnce($"[RA] {Name}: No track in the second session: {filePath}");
                return Array.Empty<string>();
            }

            // rcheevos reads 2352 bytes per sector; a plain image keeps its own sector size.
            var buffer = new byte[image.IsMultiTrack ? RawSectorSize : track.RawDataSize];
            long sector;
            var byteswapped = false;
            var offset = 0;
            uint size = 0;

            try
            {
                sector = track.FirstTrackSector;
                track.ReadSector(sector, buffer, 0, buffer.Length);

                for (var i = 64; i < buffer.Length - 32 - (4 * 3); i++)
                {
                    if (HashUtils.MatchesAt(buffer, i, ByteswappedHeader))
                    {
                        byteswapped = true;
                        offset = i + 32 + 4;
                        size = (uint)((buffer[offset] << 16) | (buffer[offset + 1] << 24) | buffer[offset + 2] | (buffer[offset + 3] << 8));
                        break;
                    }

                    if (HashUtils.MatchesAt(buffer, i, NormalHeader))
                    {
                        byteswapped = false;
                        offset = i + 32 + 4;
                        size = (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
                        break;
                    }
                }

                if (size == 0)
                {
                    WarnOnce($"[RA] {Name}: Not a Jaguar CD image: {filePath}");
                    return Array.Empty<string>();
                }
            }
            catch
            {
                track.Dispose();
                throw;
            }

            for (var pass = 0; ; pass++)
            {
                string hash;
                using (track)
                {
                    hash = HashBootCode(track, buffer, sector, offset + 4, size, byteswapped, cancel);
                }

                if (hash == null)
                {
                    WarnOnce($"[RA] {Name}: Not enough data for the boot code: {filePath}");
                    return Array.Empty<string>();
                }

                // Homebrew discs share one bootloader and keep the game code in track 2.
                var isHomebrew = (string.Equals(hash, HomebrewHash, StringComparison.Ordinal) && byteswapped) ||
                                 (_homebrewHashOverride != null && string.Equals(hash, _homebrewHashOverride, StringComparison.Ordinal));
                if (!isHomebrew || pass == 1)
                {
                    return new[] { hash };
                }

                track = image.IsMultiTrack ? image.OpenTrack(DiscTrackSelector.Track(2)) : null;
                if (track == null)
                {
                    WarnOnce($"[RA] {Name}: Homebrew bootloader found but track 2 is missing: {filePath}");
                    return Array.Empty<string>();
                }

                Array.Clear(buffer, 0, buffer.Length);
                sector = track.FirstTrackSector;
                track.ReadSector(sector, buffer, 0, buffer.Length);
                if (!HashUtils.MatchesAt(buffer, 0x5E, HomebrewMarker))
                {
                    track.Dispose();
                    WarnOnce($"[RA] {Name}: Homebrew executable not found in track 2: {filePath}");
                    return Array.Empty<string>();
                }

                offset = 0xA6;
                size = (uint)((buffer[offset] << 16) | (buffer[offset + 1] << 24) | buffer[offset + 2] | (buffer[offset + 3] << 8));
            }
        }

        /// <summary>
        /// Hashes <paramref name="size"/> bytes (capped at 64 MB) starting <paramref name="offset"/>
        /// bytes into the already-read <paramref name="sector"/>, continuing through whole following
        /// sectors. Null when the track ends first.
        /// </summary>
        private static string HashBootCode(DiscTrack track, byte[] first, long sector, int offset, uint size, bool byteswapped, CancellationToken cancel)
        {
            if (size > HashUtils.MaxHashBytes)
            {
                size = HashUtils.MaxHashBytes;
            }

            var sectorBytes = first.Length;
            using (var md5 = MD5.Create())
            {
                if (byteswapped)
                {
                    HashUtils.ByteSwap16(first, first.Length);
                }

                var remaining = sectorBytes - offset;
                if (remaining >= size)
                {
                    md5.TransformFinalBlock(first, offset, (int)size);
                    return HashUtils.ToHexLower(md5.Hash);
                }

                md5.TransformBlock(first, offset, remaining, null, 0);
                size -= (uint)remaining;

                // Each following sector must be read whole. When a sector's payload is exactly one
                // read (audio tracks), runs of sectors are read at once; otherwise each read keeps
                // cdreader's per-request semantics.
                var batched = sectorBytes == track.RawDataSize;
                var runLength = batched ? DiscTrack.RunSectors : 1;
                var run = new byte[runLength * sectorBytes];
                sector++;
                while (size > 0)
                {
                    cancel.ThrowIfCancellationRequested();

                    var sectors = (int)Math.Min(runLength, (size + sectorBytes - 1) / sectorBytes);
                    var read = batched
                        ? track.ReadSectorRun(sector, sectors, sectorBytes, run, 0)
                        : track.ReadSector(sector, run, 0, sectorBytes);
                    if (read < sectors * sectorBytes)
                    {
                        return null;
                    }

                    for (var i = 0; i < sectors; i++)
                    {
                        var chunk = i * sectorBytes;
                        if (byteswapped)
                        {
                            SwapRange(run, chunk, sectorBytes);
                        }

                        var take = (int)Math.Min(sectorBytes, size);
                        md5.TransformBlock(run, chunk, take, null, 0);
                        size -= (uint)take;
                    }

                    sector += sectors;
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return HashUtils.ToHexLower(md5.Hash);
            }
        }

        private static void SwapRange(byte[] buffer, int offset, int count)
        {
            for (var i = offset; i + 1 < offset + count; i += 2)
            {
                var tmp = buffer[i];
                buffer[i] = buffer[i + 1];
                buffer[i + 1] = tmp;
            }
        }

        private static int GuessSectorSize(long length)
        {
            if (length > 0 && length % 2352 == 0) return 2352;
            return 2048;
        }
    }
}
