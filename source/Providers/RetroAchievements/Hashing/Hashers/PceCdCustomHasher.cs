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
    internal sealed class PceCdCustomHasher : DiscBasedHasher
    {
        public PceCdCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PC Engine CD (title + boot code MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(string filePath, CancellationToken cancel)
        {
            using (var image = DiscImageReader.Open(filePath))
            {
                // Read 128 bytes from sector 1.
                var header = new byte[128];
                if (!await image.TryReadSectorAsync(sectorIndex: 1, header, header.Length, cancel).ConfigureAwait(false))
                {
                    return Array.Empty<string>();
                }

                var signature = Encoding.ASCII.GetBytes("PC Engine CD-ROM SYSTEM");

                if (HashUtils.MatchesAt(header, 32, signature))
                {
                    // Title is the last 22 bytes of the header (bytes 106..127).
                    using (var md5 = MD5.Create())
                    {
                        md5.TransformBlock(header, 106, 22, null, 0);

                        // Sector (3 bytes big-endian), count (1 byte).
                        var programSector = (header[0] << 16) | (header[1] << 8) | header[2];
                        var numSectors = header[3];

                        var buffer = new byte[2048];
                        for (var i = 0; i < numSectors; i++)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (!await image.TryReadSectorAsync(sectorIndex: programSector + i, buffer, buffer.Length, cancel).ConfigureAwait(false))
                            {
                                return Array.Empty<string>();
                            }
                            md5.TransformBlock(buffer, 0, buffer.Length, null, 0);
                        }

                        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        return new[] { HashUtils.ToHexLower(md5.Hash) };
                    }
                }

                // GameExpress: hash BOOT.BIN from Joliet filesystem if present and not huge.
                using (var iso = new DiscUtilsFacade(filePath))
                using (var boot = iso.OpenFileOrNull("BOOT.BIN"))
                {
                    if (boot == null)
                    {
                        Logger?.Warn($"[RA] {Name}: Not a PC Engine CD image (no header signature, no BOOT.BIN): {filePath}");
                        return Array.Empty<string>();
                    }

                    if (boot.Length >= HashUtils.MaxHashBytes)
                    {
                        Logger?.Warn($"[RA] {Name}: BOOT.BIN too large to hash: {filePath}");
                        return Array.Empty<string>();
                    }

                    using (var md5 = MD5.Create())
                    {
                        await HashUtils.AppendStreamAsync(md5, boot, boot.Length, cancel).ConfigureAwait(false);
                        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        return new[] { HashUtils.ToHexLower(md5.Hash) };
                    }
                }
            }
        }
    }
}
