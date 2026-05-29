using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class AtariJaguarCdCustomHasher : DiscBasedHasher
    {
        private static readonly byte[] NormalHeader = Encoding.ASCII.GetBytes("ATARI APPROVED DATA HEADER ATRI ");
        private static readonly byte[] ByteswappedHeader = Encoding.ASCII.GetBytes("TARA IPARPVODED TA AEHDAREA RT I");
        private const string HomebrewHash = "254487b59ab21bc005338e85cbf9fd2f";

        public AtariJaguarCdCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "Atari Jaguar CD (boot code MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(string filePath, CancellationToken cancel)
        {
            var sectorSize = CueTrackReader.IsCuePath(filePath) ? 2048 : GuessSectorSize(filePath);
            if (sectorSize <= 0)
            {
                return Array.Empty<string>();
            }

            using (var image = DiscImageReader.Open(filePath, sectorSize))
            using (var md5 = MD5.Create())
            {
                var buffer = new byte[image.SectorSize];
                if (!await image.TryReadSectorAsync(sectorIndex: 0, buffer, cancel).ConfigureAwait(false))
                {
                    return Array.Empty<string>();
                }

                var byteswapped = false;
                var offset = 0;
                uint size = 0;

                for (var i = 64; i < buffer.Length - 32 - 12; i++)
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
                    Logger?.Warn($"[RA] {Name}: Not a Jaguar CD image: {filePath}");
                    return Array.Empty<string>();
                }

                offset += 4; // skip size field; start of boot code
                if (size > HashUtils.MaxHashBytes)
                {
                    size = HashUtils.MaxHashBytes;
                }

                var sector = 0;
                var remaining = size;

                while (remaining > 0)
                {
                    cancel.ThrowIfCancellationRequested();

                    if (byteswapped)
                    {
                        HashUtils.ByteSwap16(buffer, buffer.Length);
                    }

                    var available = buffer.Length - offset;
                    if (available <= 0)
                    {
                        return Array.Empty<string>();
                    }

                    var toHash = (int)Math.Min((uint)available, remaining);
                    md5.TransformBlock(buffer, offset, toHash, null, 0);
                    remaining -= (uint)toHash;

                    if (remaining == 0)
                    {
                        break;
                    }

                    offset = 0;
                    sector++;
                    if (!await image.TryReadSectorAsync(sector, buffer, cancel).ConfigureAwait(false))
                    {
                        return Array.Empty<string>();
                    }
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = HashUtils.ToHexLower(md5.Hash);

                if (string.Equals(hash, HomebrewHash, StringComparison.OrdinalIgnoreCase))
                {
                    Logger?.Warn($"[RA] {Name}: Potential homebrew Jaguar CD detected; multi-track handling not implemented (returning base hash).");
                }

                return new[] { hash };
            }
        }

        private static int GuessSectorSize(string filePath)
        {
            var len = new FileInfo(filePath).Length;
            if (len > 0 && len % 2352 == 0) return 2352;
            if (len > 0 && len % 2048 == 0) return 2048;
            return 2048;
        }
    }
}
