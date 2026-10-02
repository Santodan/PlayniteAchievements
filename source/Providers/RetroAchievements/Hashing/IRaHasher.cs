using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal interface IRaHasher
    {
        string Name { get; }

        /// <summary>
        /// True when the hasher reads its input front to back once, so it can hash a
        /// forward-only stream such as an archive entry without a temporary file.
        /// </summary>
        bool SupportsForwardOnlyInput { get; }

        Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel);
    }

    internal static class RaHasherExtensions
    {
        public static Task<IReadOnlyList<string>> ComputeHashesAsync(this IRaHasher hasher, string filePath, CancellationToken cancel)
        {
            return hasher.ComputeHashesAsync(RaHashSource.FromFile(filePath), cancel);
        }
    }
}
