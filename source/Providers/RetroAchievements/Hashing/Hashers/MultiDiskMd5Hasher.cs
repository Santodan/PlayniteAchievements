using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class MultiDiskMd5Hasher : IRaHasher
    {
        public string Name => "Multi-disk (MD5 per disk)";

        public bool SupportsForwardOnlyInput => true;

        public async Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(source?.Path))
            {
                return Array.Empty<string>();
            }

            if (source.IsFile && source.Path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
            {
                var paths = ExpandM3u(source.Path);
                var hashes = new List<string>();
                foreach (var p in paths)
                {
                    cancel.ThrowIfCancellationRequested();

                    if (File.Exists(p))
                    {
                        var h = await HashUtils
                            .ComputeMd5HexFromFileAsync(p, startOffset: 0, maxBytes: HashUtils.MaxHashBytes, cancel)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(h))
                        {
                            hashes.Add(h);
                        }
                    }
                }

                return hashes;
            }

            using (var stream = source.Open())
            {
                var single = await HashUtils
                    .ComputeMd5HexFromStreamAsync(stream, HashUtils.MaxHashBytes, cancel)
                    .ConfigureAwait(false);
                return new[] { single };
            }
        }

        private static IEnumerable<string> ExpandM3u(string m3uPath)
        {
            var dir = Path.GetDirectoryName(m3uPath) ?? string.Empty;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(m3uPath);
            }
            catch
            {
                return Array.Empty<string>();
            }

            var results = new List<string>();
            foreach (var line in lines)
            {
                var t = (line ?? string.Empty).Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("#", StringComparison.Ordinal)) continue;

                var resolved = Path.IsPathRooted(t) ? t : Path.Combine(dir, t);
                results.Add(resolved);
            }

            return results;
        }
    }
}
