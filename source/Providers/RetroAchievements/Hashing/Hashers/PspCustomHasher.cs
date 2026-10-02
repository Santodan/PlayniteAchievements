using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class PspCustomHasher : DiscBasedHasher
    {
        public PspCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PSP (PARAM.SFO + EBOOT.BIN MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            // Hash PBP as whole file.
            if (filePath.EndsWith(".pbp", StringComparison.OrdinalIgnoreCase))
            {
                using (var pbp = source.Open())
                {
                    var hash = await HashUtils
                        .ComputeMd5HexFromStreamAsync(pbp, HashUtils.MaxHashBytes, cancel)
                        .ConfigureAwait(false);
                    return new[] { hash };
                }
            }

            using (var iso = new DiscUtilsFacade(source))
            using (var md5 = MD5.Create())
            {
                using (var paramStream = iso.OpenFileOrNull("PSP_GAME\\PARAM.SFO"))
                {
                    if (paramStream == null)
                    {
                        WarnOnce($"[RA] {Name}: Not a PSP game disc or missing PARAM.SFO: {filePath}");
                        return Array.Empty<string>();
                    }
                    await HashUtils.AppendStreamAsync(md5, paramStream, paramStream.Length, cancel).ConfigureAwait(false);
                }

                using (var ebootStream = iso.OpenFileOrNull("PSP_GAME\\SYSDIR\\EBOOT.BIN"))
                {
                    if (ebootStream == null)
                    {
                        WarnOnce($"[RA] {Name}: Could not find primary executable EBOOT.BIN: {filePath}");
                        return Array.Empty<string>();
                    }
                    await HashUtils.AppendStreamAsync(md5, ebootStream, ebootStream.Length, cancel).ConfigureAwait(false);
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return new[] { HashUtils.ToHexLower(md5.Hash) };
            }
        }
    }
}

