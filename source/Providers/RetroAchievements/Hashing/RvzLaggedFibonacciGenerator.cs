using System;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// Port of Dolphin's DiscIO LaggedFibonacciGenerator (CC0-1.0), the generator RVZ uses to
    /// reproduce the pseudo-random padding ("junk data") that Nintendo discs carry.
    /// Only the reader side is ported: seeding, forwarding and byte output.
    /// </summary>
    internal sealed class RvzLaggedFibonacciGenerator
    {
        public const int SeedWords = 17;
        public const int SeedBytes = SeedWords * sizeof(uint);

        private const int LfgK = 521;
        private const int LfgJ = 32;
        private const int BufferBytes = LfgK * sizeof(uint);

        // Words are kept in the byte order Dolphin stores them after Initialize (already swapped),
        // so output is the little-endian bytes of each word, as Dolphin's memcpy of its u32 array.
        private readonly uint[] _buffer = new uint[LfgK];
        private int _positionBytes;

        public void SetSeed(byte[] seed, int offset)
        {
            _positionBytes = 0;

            for (var i = 0; i < SeedWords; i++)
            {
                var p = offset + i * sizeof(uint);
                _buffer[i] = ((uint)seed[p] << 24) | ((uint)seed[p + 1] << 16) | ((uint)seed[p + 2] << 8) | seed[p + 3];
            }

            Initialize();
        }

        public void GetBytes(int count, byte[] output, int offset)
        {
            while (count > 0)
            {
                var length = Math.Min(count, BufferBytes - _positionBytes);
                CopyBytes(_positionBytes, output, offset, length);

                _positionBytes += length;
                count -= length;
                offset += length;

                if (_positionBytes == BufferBytes)
                {
                    Forward();
                    _positionBytes = 0;
                }
            }
        }

        public void Forward(long count)
        {
            var position = _positionBytes + count;
            while (position >= BufferBytes)
            {
                Forward();
                position -= BufferBytes;
            }

            _positionBytes = (int)position;
        }

        private void CopyBytes(int sourceByte, byte[] output, int offset, int length)
        {
            for (var i = 0; i < length; i++)
            {
                var byteIndex = sourceByte + i;
                var word = _buffer[byteIndex >> 2];
                output[offset + i] = (byte)(word >> ((byteIndex & 3) * 8));
            }
        }

        private void Forward()
        {
            for (var i = 0; i < LfgJ; i++)
            {
                _buffer[i] ^= _buffer[i + LfgK - LfgJ];
            }

            for (var i = LfgJ; i < LfgK; i++)
            {
                _buffer[i] ^= _buffer[i - LfgJ];
            }
        }

        private void Initialize()
        {
            for (var i = SeedWords; i < LfgK; i++)
            {
                _buffer[i] = (_buffer[i - 17] << 23) ^ (_buffer[i - 16] >> 9) ^ _buffer[i - 1];
            }

            // Dolphin applies its "shift by 18 instead of 16" output quirk and the byte swap here,
            // so output is a plain byte copy of the buffer.
            for (var i = 0; i < LfgK; i++)
            {
                var x = _buffer[i];
                _buffer[i] = Swap32((x & 0xFF00FFFF) | ((x >> 2) & 0x00FF0000));
            }

            for (var i = 0; i < 4; i++)
            {
                Forward();
            }
        }

        private static uint Swap32(uint v)
        {
            return (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
        }
    }
}
