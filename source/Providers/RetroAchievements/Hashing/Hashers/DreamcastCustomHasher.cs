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
    /// <summary>rc_hash_dreamcast (hash_disc.c:326-412).</summary>
    internal sealed class DreamcastCustomHasher : DiscBasedHasher
    {
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("SEGA SEGAKATANA ");

        public DreamcastCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "Dreamcast (IP.BIN + boot executable MD5)";

        protected override Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            return Task.Run(() => Compute(source, cancel), cancel);
        }

        private IReadOnlyList<string> Compute(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            var image = DiscImage.Open(source);
            var meta = new byte[256];

            // Track 3 is the GD-ROM data track holding IP.BIN; MIL-CDs keep it in the first data track.
            var track = image.OpenTrack(DiscTrackSelector.Track(3));
            try
            {
                if (track != null)
                {
                    track.ReadSector(track.FirstTrackSector, meta, 0, meta.Length);
                }

                if (!HashUtils.MatchesAt(meta, 0, Marker))
                {
                    track?.Dispose();
                    track = image.OpenTrack(DiscTrackSelector.FirstData);
                    if (track == null)
                    {
                        return Array.Empty<string>();
                    }

                    Array.Clear(meta, 0, meta.Length);
                    track.ReadSector(track.FirstTrackSector, meta, 0, meta.Length);
                    if (!HashUtils.MatchesAt(meta, 0, Marker))
                    {
                        WarnOnce($"[RA] {Name}: Missing SEGA SEGAKATANA marker: {filePath}");
                        return Array.Empty<string>();
                    }
                }

                // The boot file name is 96 bytes into IP.BIN, padded with spaces.
                var nameLength = 0;
                while (nameLength < 16 && !IsSpace(meta[96 + nameLength]))
                {
                    nameLength++;
                }

                if (nameLength == 0)
                {
                    WarnOnce($"[RA] {Name}: Boot executable not specified on IP.BIN: {filePath}");
                    return Array.Empty<string>();
                }

                var exeName = Encoding.ASCII.GetString(meta, 96, nameLength);
                var sector = Iso9660SectorLocator.FindFileSector(track, exeName, out var size);
                if (sector == 0)
                {
                    WarnOnce($"[RA] {Name}: Could not locate boot executable '{exeName}': {filePath}");
                    return Array.Empty<string>();
                }

                cancel.ThrowIfCancellationRequested();

                // The boot executable is normally in the last track when it is not in the IP.BIN track.
                if (track.ReadSector(sector, new byte[1], 0, 1) == 0)
                {
                    track.Dispose();
                    track = image.OpenTrack(DiscTrackSelector.Last);
                }

                using (var md5 = MD5.Create())
                {
                    md5.TransformBlock(meta, 0, meta.Length, null, 0);
                    if (!Iso9660SectorLocator.HashFile(md5, track, sector, size))
                    {
                        WarnOnce($"[RA] {Name}: Could not read boot executable '{exeName}': {filePath}");
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

        private static bool IsSpace(byte b)
        {
            return b == ' ' || (b >= '\t' && b <= '\r');
        }
    }
}
