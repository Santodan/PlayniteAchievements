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
    /// <summary>rc_hash_ps2 (hash_disc.c:981-1019).</summary>
    internal sealed class Ps2CustomHasher : DiscBasedHasher
    {
        public Ps2CustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PlayStation 2 (SYSTEM.CNF BOOT2 + executable MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            using (var iso = new DiscUtilsFacade(source))
            {
                var exeName = PsxCustomHasher.FindBootExecutableName(iso, "BOOT2", "cdrom0:");
                if (exeName == null)
                {
                    WarnOnce($"[RA] {Name}: Could not locate primary executable via SYSTEM.CNF: {filePath}");
                    return Array.Empty<string>();
                }

                if (!iso.TryGetFileExtent(exeName, out var start, out var length))
                {
                    WarnOnce($"[RA] {Name}: Could not locate primary executable '{exeName}': {filePath}");
                    return Array.Empty<string>();
                }

                using (var md5 = MD5.Create())
                {
                    // The boot file name as SYSTEM.CNF spells it is part of the hash.
                    var titleBytes = Encoding.ASCII.GetBytes(exeName);
                    md5.TransformBlock(titleBytes, 0, titleBytes.Length, null, 0);

                    if (!await iso.AppendFileAsync(md5, start, (uint)length, cancel).ConfigureAwait(false))
                    {
                        WarnOnce($"[RA] {Name}: Could not read primary executable '{exeName}': {filePath}");
                        return Array.Empty<string>();
                    }

                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
        }
    }
}
