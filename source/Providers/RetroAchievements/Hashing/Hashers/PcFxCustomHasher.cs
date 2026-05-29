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
    internal sealed class PcFxCustomHasher : DiscBasedHasher
    {
        public PcFxCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PC-FX (boot header + program sectors MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(string filePath, CancellationToken cancel)
        {
            using (var image = DiscImageReader.Open(filePath))
            using (var md5 = MD5.Create())
            {
                // PC-FX marker in sector 0.
                var markerBuf = new byte[32];
                if (!await image.TryReadSectorAsync(sectorIndex: 0, markerBuf, markerBuf.Length, cancel).ConfigureAwait(false))
                {
                    return Array.Empty<string>();
                }

                var marker = Encoding.ASCII.GetBytes("PC-FX:Hu_CD-ROM");
                if (!HashUtils.MatchesAt(markerBuf, 0, marker))
                {
                    // Some PC-FX images still identify as PCE CDs.
                    return await new PceCdCustomHasher(Logger).ComputeHashesAsync(filePath, cancel).ConfigureAwait(false);
                }

                // First 128 bytes of sector 1 are hashed.
                var header = new byte[128];
                if (!await image.TryReadSectorAsync(sectorIndex: 1, header, header.Length, cancel).ConfigureAwait(false))
                {
                    return Array.Empty<string>();
                }

                md5.TransformBlock(header, 0, header.Length, null, 0);

                // Program sector (3 bytes little-endian in bytes 32..34), num sectors (bytes 36..38).
                var programSector = (header[34] << 16) | (header[33] << 8) | header[32];
                var numSectors = (header[38] << 16) | (header[37] << 8) | header[36];

                var sectorBuf = new byte[2048];
                for (var i = 0; i < numSectors; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!await image.TryReadSectorAsync(sectorIndex: programSector + i, sectorBuf, sectorBuf.Length, cancel).ConfigureAwait(false))
                    {
                        return Array.Empty<string>();
                    }

                    md5.TransformBlock(sectorBuf, 0, sectorBuf.Length, null, 0);
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return new[] { HashUtils.ToHexLower(md5.Hash) };
            }
        }
    }
}
