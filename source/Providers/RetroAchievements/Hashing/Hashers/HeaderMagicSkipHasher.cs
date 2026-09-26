using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class HeaderMagicSkipHasher : IRaHasher
    {
        private readonly IReadOnlyList<byte[]> _magicPrefixes;
        private readonly int _skipBytes;

        public HeaderMagicSkipHasher(IReadOnlyList<byte[]> magicPrefixes, int skipBytes)
        {
            _magicPrefixes = magicPrefixes ?? throw new ArgumentNullException(nameof(magicPrefixes));
            _skipBytes = Math.Max(0, skipBytes);
        }

        public string Name => $"MD5 (magic header skip {_skipBytes} bytes)";

        public bool SupportsForwardOnlyInput => true;

        public async Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            if (_magicPrefixes.Count == 0)
            {
                throw new InvalidOperationException("No magic prefixes configured.");
            }

            var maxMagicLen = _magicPrefixes.Max(m => m?.Length ?? 0);
            if (maxMagicLen <= 0)
            {
                throw new InvalidOperationException("Invalid magic prefix configuration.");
            }

            // One pass: read the header, then hash [offset, MaxHashBytes) of the same stream.
            var header = new byte[maxMagicLen];
            using (var stream = source.Open())
            {
                var read = HashUtils.ReadFull(stream, header, 0, header.Length);
                var offset = HasMagic(header, read) ? _skipBytes : 0;

                using (var md5 = MD5.Create())
                {
                    var limit = (long)HashUtils.MaxHashBytes;
                    var fromHeader = (int)Math.Max(0, Math.Min(read, limit) - offset);
                    if (fromHeader > 0)
                    {
                        md5.TransformBlock(header, offset, fromHeader, null, 0);
                    }

                    if (read == header.Length)
                    {
                        HashUtils.Skip(stream, offset - read);
                        var consumed = Math.Max(read, offset);
                        await HashUtils.AppendStreamAsync(md5, stream, limit - consumed, cancel).ConfigureAwait(false);
                    }

                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
        }

        private bool HasMagic(byte[] header, int read)
        {
            foreach (var magic in _magicPrefixes)
            {
                if (magic == null || magic.Length == 0 || read < magic.Length) continue;

                var matches = true;
                for (var i = 0; i < magic.Length; i++)
                {
                    if (header[i] != magic[i])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
