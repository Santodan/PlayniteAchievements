using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class SegaCdSaturnCustomHasher : DiscBasedHasher
    {
        public SegaCdSaturnCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "Sega CD / Saturn (sector 0 header MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(string filePath, CancellationToken cancel)
        {
            var buffer = new byte[512];
            using (var image = DiscImageReader.Open(filePath))
            {
                var read = await HashUtils.ReadExactlyAsync(image.Stream, buffer, 0, buffer.Length, cancel).ConfigureAwait(false);
                if (read < buffer.Length)
                {
                    return Array.Empty<string>();
                }
            }

            var segaCd = System.Text.Encoding.ASCII.GetBytes("SEGADISCSYSTEM  ");
            var saturn = System.Text.Encoding.ASCII.GetBytes("SEGA SEGASATURN ");

            var ok = true;
            for (var i = 0; i < 16; i++)
            {
                if (buffer[i] != segaCd[i])
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
            {
                ok = true;
                for (var i = 0; i < 16; i++)
                {
                    if (buffer[i] != saturn[i])
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok)
            {
                Logger?.Warn($"[RA] {Name}: Not a Sega CD / Saturn image: {filePath}");
                return Array.Empty<string>();
            }

            var hash = HashUtils.ComputeMd5Hex(buffer);
            return new[] { hash };
        }
    }
}
