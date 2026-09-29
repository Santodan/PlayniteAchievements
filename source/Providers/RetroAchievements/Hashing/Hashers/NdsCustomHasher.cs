using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class NdsCustomHasher : IRaHasher
    {
        private const int ChunkSize = 64 * 1024;

        public string Name => "Nintendo DS (header + arm9 + arm7 + icon/title MD5)";

        // Reads the header, then seeks to the arm9, arm7 and icon regions.
        public bool SupportsForwardOnlyInput => false;

        public Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            return Task.Run(() => Compute(source, cancel), cancel);
        }

        private static IReadOnlyList<string> Compute(RaHashSource source, CancellationToken cancel)
        {
            using (var stream = source.Open())
            using (var md5 = MD5.Create())
            {
                var header = new byte[512];
                long baseOffset = 0;

                if (HashUtils.ReadFull(stream, header, 0, header.Length) != 512)
                {
                    return Array.Empty<string>();
                }

                // SuperCard header detection: ignore first 512 bytes and re-read header.
                if (header[0] == 0x2E && header[1] == 0x00 && header[2] == 0x00 && header[3] == 0xEA &&
                    header[0xB0] == 0x44 && header[0xB1] == 0x46 && header[0xB2] == 0x96 && header[0xB3] == 0x00)
                {
                    baseOffset = 512;
                    stream.Seek(baseOffset, SeekOrigin.Begin);
                    if (HashUtils.ReadFull(stream, header, 0, header.Length) != 512)
                    {
                        return Array.Empty<string>();
                    }
                }

                var arm9Offset = HashUtils.ReadUInt32LE(header, 0x20);
                var arm9Size = HashUtils.ReadUInt32LE(header, 0x2C);
                var arm7Offset = HashUtils.ReadUInt32LE(header, 0x30);
                var arm7Size = HashUtils.ReadUInt32LE(header, 0x3C);
                var iconOffset = HashUtils.ReadUInt32LE(header, 0x68);

                if (arm9Size + arm7Size > 16u * 1024u * 1024u)
                {
                    return Array.Empty<string>();
                }

                // Hash header (first 0x160 bytes), then arm9, arm7, then 0xA00 bytes icon/title (0-padded if short).
                md5.TransformBlock(header, 0, 0x160, null, 0);

                var buffer = new byte[Math.Max(ChunkSize, 0xA00)];

                if (!HashRange(stream, md5, buffer, baseOffset + arm9Offset, arm9Size, cancel) ||
                    !HashRange(stream, md5, buffer, baseOffset + arm7Offset, arm7Size, cancel))
                {
                    return Array.Empty<string>();
                }

                stream.Seek(baseOffset + iconOffset, SeekOrigin.Begin);
                var iconRead = HashUtils.ReadFull(stream, buffer, 0, 0xA00);
                if (iconRead < 0xA00)
                {
                    Array.Clear(buffer, iconRead, 0xA00 - iconRead);
                }
                md5.TransformBlock(buffer, 0, 0xA00, null, 0);

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return new[] { HashUtils.ToHexLower(md5.Hash) };
            }
        }

        private static bool HashRange(Stream stream, HashAlgorithm md5, byte[] buffer, long offset, uint size, CancellationToken cancel)
        {
            if (size == 0)
            {
                return true;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            var remaining = (long)size;
            while (remaining > 0)
            {
                cancel.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(ChunkSize, remaining);
                if (HashUtils.ReadFull(stream, buffer, 0, toRead) != toRead)
                {
                    return false;
                }

                md5.TransformBlock(buffer, 0, toRead, null, 0);
                remaining -= toRead;
            }

            return true;
        }
    }
}
