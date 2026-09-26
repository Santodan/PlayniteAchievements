using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class HeaderSizeModSkipHasher : IRaHasher
    {
        private readonly int _skipBytes;
        private readonly int? _sizeModuloBytes;
        private readonly int? _sizeRemainderBytes;
        private readonly int? _sizeBitFlagBytes;

        public HeaderSizeModSkipHasher(int skipBytes, int? sizeModuloBytes, int? sizeRemainderBytes, int? sizeBitFlagBytes)
        {
            _skipBytes = Math.Max(0, skipBytes);
            _sizeModuloBytes = sizeModuloBytes;
            _sizeRemainderBytes = sizeRemainderBytes;
            _sizeBitFlagBytes = sizeBitFlagBytes;
        }

        public string Name => "MD5 (size-based header skip)";

        public bool SupportsForwardOnlyInput => true;

        public async Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            var fileSize = source.Length;

            long offset = 0;

            if (_sizeBitFlagBytes.HasValue && _sizeBitFlagBytes.Value > 0)
            {
                if ((fileSize & _sizeBitFlagBytes.Value) != 0)
                {
                    offset = _skipBytes;
                }
            }
            else if (_sizeModuloBytes.HasValue && _sizeModuloBytes.Value > 0 && _sizeRemainderBytes.HasValue)
            {
                if (fileSize % _sizeModuloBytes.Value == _sizeRemainderBytes.Value)
                {
                    offset = _skipBytes;
                }
            }

            var maxBytes = Math.Max(0, (long)HashUtils.MaxHashBytes - offset);
            using (var stream = source.Open())
            {
                HashUtils.Skip(stream, offset);
                var hash = await HashUtils
                    .ComputeMd5HexFromStreamAsync(stream, maxBytes, cancel)
                    .ConfigureAwait(false);

                return new[] { hash };
            }
        }
    }
}
