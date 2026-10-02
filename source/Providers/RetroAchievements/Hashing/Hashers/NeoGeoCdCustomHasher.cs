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
    internal sealed class NeoGeoCdCustomHasher : DiscBasedHasher
    {
        public NeoGeoCdCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "Neo Geo CD (IPL.TXT PRG chain MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            using (var iso = new DiscUtilsFacade(source))
            using (var iplStream = iso.OpenFileOrNull("IPL.TXT"))
            {
                if (iplStream == null)
                {
                    WarnOnce($"[RA] {Name}: Not a Neo Geo CD image (missing IPL.TXT): {filePath}");
                    return Array.Empty<string>();
                }

                var buffer = new byte[1024];
                var read = await HashUtils.ReadExactlyAsync(iplStream, buffer, 0, buffer.Length, cancel).ConfigureAwait(false);
                if (read <= 0)
                {
                    return Array.Empty<string>();
                }

                var text = Encoding.ASCII.GetString(buffer, 0, read);

                using (var md5 = MD5.Create())
                {
                    foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        cancel.ThrowIfCancellationRequested();

                        var line = rawLine.Trim();
                        if (line.Length == 0) continue;

                        var dot = line.IndexOf('.');
                        if (dot < 0) continue;

                        var ext = line.Substring(dot);
                        if (!ext.StartsWith(".PRG", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var prgName = line.Substring(0, dot + 4); // include .PRG
                        using (var prgStream = iso.OpenFileOrNull(prgName))
                        {
                            if (prgStream == null)
                            {
                                WarnOnce($"[RA] {Name}: Missing PRG '{prgName}': {filePath}");
                                return Array.Empty<string>();
                            }

                            await HashUtils.AppendStreamAsync(md5, prgStream, prgStream.Length, cancel).ConfigureAwait(false);
                        }
                    }

                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
        }
    }
}

