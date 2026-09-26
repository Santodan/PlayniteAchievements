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
    /// <summary>rc_hash_pcfx_cd (hash_disc.c:791-864).</summary>
    internal sealed class PcFxCustomHasher : DiscBasedHasher
    {
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("PC-FX:Hu_CD-ROM");

        public PcFxCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PC-FX (boot header + program sectors MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            var image = DiscImage.Open(source);
            var buffer = new byte[128];

            // The executable can be in any track: check the largest data track, then track 2.
            var track = image.OpenTrack(DiscTrackSelector.Largest);
            if (track == null)
            {
                WarnOnce($"[RA] {Name}: No data track: {filePath}");
                return Array.Empty<string>();
            }

            try
            {
                long sector = track.FirstTrackSector;
                track.ReadSector(sector, buffer, 0, 32);
                if (!HashUtils.MatchesAt(buffer, 0, Marker))
                {
                    track.Dispose();
                    track = image.OpenTrack(DiscTrackSelector.Track(2));
                    if (track == null)
                    {
                        WarnOnce($"[RA] {Name}: Not a PC-FX image: {filePath}");
                        return Array.Empty<string>();
                    }

                    Array.Clear(buffer, 0, buffer.Length);
                    sector = track.FirstTrackSector;
                    track.ReadSector(sector, buffer, 0, 32);
                }

                if (!HashUtils.MatchesAt(buffer, 0, Marker))
                {
                    // Some PC-FX discs still identify as PC Engine CDs.
                    Array.Clear(buffer, 0, buffer.Length);
                    track.ReadSector(sector + 1, buffer, 0, 128);
                    if (PceCdCustomHasher.HasSignature(buffer))
                    {
                        return await new PceCdCustomHasher(Logger).HashTrackAsync(track, filePath, cancel).ConfigureAwait(false);
                    }

                    WarnOnce($"[RA] {Name}: Not a PC-FX image: {filePath}");
                    return Array.Empty<string>();
                }

                // The boot header fills the first two sectors; the first 128 bytes of the second are hashed.
                Array.Clear(buffer, 0, buffer.Length);
                track.ReadSector(sector + 1, buffer, 0, 128);

                using (var md5 = MD5.Create())
                {
                    md5.TransformBlock(buffer, 0, buffer.Length, null, 0);

                    // Program sector (bytes 32..34) and sector count (bytes 36..38), little-endian, relative to the track.
                    var programSector = (buffer[34] << 16) | (buffer[33] << 8) | buffer[32];
                    var numSectors = (buffer[38] << 16) | (buffer[37] << 8) | buffer[36];
                    if (!await PceCdCustomHasher.HashSectorsAsync(md5, track, track.FirstTrackSector + (long)programSector, numSectors, cancel).ConfigureAwait(false))
                    {
                        return Array.Empty<string>();
                    }

                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
            finally
            {
                track?.Dispose();
            }
        }
    }
}
