using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class ArduboyHexNormalizeHasher : IRaHasher
    {
        public string Name => "Arduboy (.hex normalized MD5)";

        public bool SupportsForwardOnlyInput => true;

        public async Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            // RetroAchievements uses normalized line endings and always appends a final \n.
            // This matches rcheevos' rc_hash_text behavior.
            using (var stream = source.Open())
            {
                var hash = await HashUtils.ComputeMd5HexForNormalizedTextAsync(stream, cancel).ConfigureAwait(false);
                return new[] { hash };
            }
        }
    }
}
