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
    internal sealed class DreamcastCustomHasher : DiscBasedHasher
    {
        public DreamcastCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "Dreamcast (IP.BIN + boot executable MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(string filePath, CancellationToken cancel)
        {
            var meta = new byte[256];
            using (var image = DiscImageReader.Open(filePath))
            {
                if (await HashUtils.ReadExactlyAsync(image.Stream, meta, 0, meta.Length, cancel).ConfigureAwait(false) != meta.Length)
                {
                    return Array.Empty<string>();
                }
            }

            var marker = Encoding.ASCII.GetBytes("SEGA SEGAKATANA ");
            for (var i = 0; i < marker.Length; i++)
            {
                if (meta[i] != marker[i])
                {
                    Logger?.Warn($"[RA] {Name}: Missing SEGA SEGAKATANA marker: {filePath}");
                    return Array.Empty<string>();
                }
            }

            var exeName = ExtractBootFileName(meta);
            if (string.IsNullOrWhiteSpace(exeName))
            {
                Logger?.Warn($"[RA] {Name}: Boot executable not specified on IP.BIN: {filePath}");
                return Array.Empty<string>();
            }

            using (var md5 = MD5.Create())
            {
                md5.TransformBlock(meta, 0, meta.Length, null, 0);

                try
                {
                    using (var iso = new DiscUtilsFacade(filePath))
                    using (var exeStream = iso.OpenFileOrNull(exeName))
                    {
                        if (exeStream == null)
                        {
                            Logger?.Warn($"[RA] {Name}: Could not locate boot executable '{exeName}': {filePath}");
                            return Array.Empty<string>();
                        }

                        var maxBytes = Math.Min(HashUtils.MaxHashBytes, exeStream.Length);
                        await HashUtils.AppendStreamAsync(md5, exeStream, maxBytes, cancel).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Warn(ex, $"[RA] {Name}: Failed to read ISO filesystem: {filePath}");
                    return Array.Empty<string>();
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return new[] { HashUtils.ToHexLower(md5.Hash) };
            }
        }

        private static string ExtractBootFileName(byte[] ipBin)
        {
            var start = 96;
            var maxLen = 16;
            var len = 0;
            while (len < maxLen && start + len < ipBin.Length && !char.IsWhiteSpace((char)ipBin[start + len]))
            {
                len++;
            }

            if (len == 0) return null;

            return Encoding.ASCII.GetString(ipBin, start, len);
        }
    }
}
