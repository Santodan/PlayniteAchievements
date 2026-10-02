using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class Md5FullFileHasher : IRaHasher
    {
        public string Name => "MD5 (whole file)";

        public bool SupportsForwardOnlyInput => true;

        public async Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            using (var stream = source.Open())
            {
                var hash = await HashUtils
                    .ComputeMd5HexFromStreamAsync(stream, HashUtils.MaxHashBytes, cancel)
                    .ConfigureAwait(false);

                return new[] { hash };
            }
        }
    }
}
