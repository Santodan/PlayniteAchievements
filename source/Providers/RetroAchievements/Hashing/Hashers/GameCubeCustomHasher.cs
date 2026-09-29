using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class GameCubeCustomHasher : DiscBasedHasher
    {
        private const int BaseHeaderSize = 0x2440;
        private const int MaxHeaderSize = 1024 * 1024;
        private const int ApploaderHeaderSize = 0x20;
        private const int DolHeaderSize = 0xD8;
        private const int SegmentCount = 18;
        private const int MaxChunkSize = 1024 * 1024;

        public GameCubeCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "GameCube (apploader + DOL segments MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            using (var stream = source.Open())
            {
                var magic = new byte[4];
                stream.Seek(0x1c, SeekOrigin.Begin);
                if (await HashUtils.ReadExactlyAsync(stream, magic, 0, 4, cancel).ConfigureAwait(false) != 4)
                {
                    return Array.Empty<string>();
                }

                if (magic[0] != 0xC2 || magic[1] != 0x33 || magic[2] != 0x9F || magic[3] != 0x3D)
                {
                    WarnOnce($"[RA] {Name}: Not a GameCube image: {filePath}");
                    return Array.Empty<string>();
                }

                using (var md5 = MD5.Create())
                {
                    // Read apploader sizes to determine how much header to hash.
                    stream.Seek(BaseHeaderSize + 0x14, SeekOrigin.Begin);
                    var sizes = new byte[8];
                    if (await HashUtils.ReadExactlyAsync(stream, sizes, 0, sizes.Length, cancel).ConfigureAwait(false) != sizes.Length)
                    {
                        return Array.Empty<string>();
                    }

                    var apploaderBodySize = HashUtils.ReadUInt32BE(sizes, 0);
                    var apploaderTrailerSize = HashUtils.ReadUInt32BE(sizes, 4);

                    var headerSize = (long)BaseHeaderSize + ApploaderHeaderSize + apploaderBodySize + apploaderTrailerSize;
                    if (headerSize > MaxHeaderSize) headerSize = MaxHeaderSize;
                    if (headerSize < 0) headerSize = 0;

                    // Hash partition header.
                    stream.Seek(0, SeekOrigin.Begin);
                    var headerBuffer = new byte[(int)headerSize];
                    if (await HashUtils.ReadExactlyAsync(stream, headerBuffer, 0, headerBuffer.Length, cancel).ConfigureAwait(false) != headerBuffer.Length)
                    {
                        return Array.Empty<string>();
                    }
                    md5.TransformBlock(headerBuffer, 0, headerBuffer.Length, null, 0);

                    // DOL offset is stored at 0x420 in the header.
                    if (headerBuffer.Length < 0x424)
                    {
                        return Array.Empty<string>();
                    }

                    var dolOffset = HashUtils.ReadUInt32BE(headerBuffer, 0x420);

                    // Read DOL header.
                    stream.Seek(dolOffset, SeekOrigin.Begin);
                    var dolHeader = new byte[DolHeaderSize];
                    if (await HashUtils.ReadExactlyAsync(stream, dolHeader, 0, dolHeader.Length, cancel).ConfigureAwait(false) != dolHeader.Length)
                    {
                        return Array.Empty<string>();
                    }

                    var segmentOffsets = new uint[SegmentCount];
                    var segmentSizes = new uint[SegmentCount];

                    for (var i = 0; i < SegmentCount; i++)
                    {
                        segmentOffsets[i] = HashUtils.ReadUInt32BE(dolHeader, 0x00 + i * 4);
                        segmentSizes[i] = HashUtils.ReadUInt32BE(dolHeader, 0x90 + i * 4);
                    }

                    var chunk = new byte[MaxChunkSize];

                    for (var i = 0; i < SegmentCount; i++)
                    {
                        cancel.ThrowIfCancellationRequested();

                        var size = segmentSizes[i];
                        if (size == 0) continue;

                        stream.Seek(segmentOffsets[i], SeekOrigin.Begin);

                        var remaining = size;
                        while (remaining > 0)
                        {
                            cancel.ThrowIfCancellationRequested();

                            var toRead = (int)Math.Min((uint)chunk.Length, remaining);
                            var read = await HashUtils.ReadExactlyAsync(stream, chunk, 0, toRead, cancel).ConfigureAwait(false);
                            if (read != toRead)
                            {
                                return Array.Empty<string>();
                            }

                            md5.TransformBlock(chunk, 0, toRead, null, 0);
                            remaining -= (uint)toRead;
                        }
                    }

                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
        }
    }
}

