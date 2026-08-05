using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// HDR scRGB (R16G16B16A16_Float, linear, BT.709 primaries, 1.0 = 80 nits) to sRGB 8-bit.
    /// Normalizes by the monitor's SDR white level so SDR content maps to 1.0 (correct exposure,
    /// not over-bright), applies a gentle shoulder so real HDR highlights roll off instead of
    /// clipping, then the sRGB OETF. scRGB primaries are BT.709 = sRGB, so no gamut conversion is
    /// needed. CPU implementation for a single screenshot; a video path would port this to a shader.
    /// </summary>
    internal static class HdrToneMap
    {
        public static Bitmap BuildBitmap(IntPtr src, int rowPitch, int width, int height, float refWhite)
        {
            var inv = 1f / Math.Max(0.001f, refWhite);

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var outRow = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    var rowPtr = src + y * rowPitch;
                    for (var x = 0; x < width; x++)
                    {
                        var px = rowPtr + x * 8;
                        var r = HalfToFloat((ushort)Marshal.ReadInt16(px)) * inv;
                        var g = HalfToFloat((ushort)Marshal.ReadInt16(px + 2)) * inv;
                        var b = HalfToFloat((ushort)Marshal.ReadInt16(px + 4)) * inv;

                        var o = x * 4;
                        outRow[o + 0] = ToSrgbByte(Shoulder(b)); // B
                        outRow[o + 1] = ToSrgbByte(Shoulder(g)); // G
                        outRow[o + 2] = ToSrgbByte(Shoulder(r)); // R
                        outRow[o + 3] = 255;                      // A
                    }

                    Marshal.Copy(outRow, 0, data.Scan0 + y * data.Stride, outRow.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        /// <summary>
        /// Identity up to the knee (SDR content untouched), then an exponential roll of everything
        /// above the knee into [knee, 1] so HDR highlights compress smoothly instead of clipping.
        /// </summary>
        private static float Shoulder(float n)
        {
            if (n <= 0f)
            {
                return 0f; // scRGB can carry small negatives (out-of-gamut)
            }

            const float knee = 0.9f;
            if (n <= knee)
            {
                return n;
            }

            return knee + (1f - knee) * (1f - (float)Math.Exp(-(n - knee) / (1f - knee)));
        }

        private static byte ToSrgbByte(float c)
        {
            if (c <= 0f)
            {
                return 0;
            }

            if (c >= 1f)
            {
                return 255;
            }

            var s = c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
            var v = (int)(s * 255f + 0.5f);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        /// <summary>IEEE half (float16) to float32.</summary>
        private static float HalfToFloat(ushort h)
        {
            var sign = (h >> 15) & 0x1;
            var exp = (h >> 10) & 0x1F;
            var mant = h & 0x3FF;

            int f;
            if (exp == 0)
            {
                if (mant == 0)
                {
                    f = sign << 31; // +/- 0
                }
                else
                {
                    exp = 127 - 15 + 1;
                    while ((mant & 0x400) == 0)
                    {
                        mant <<= 1;
                        exp--;
                    }

                    mant &= 0x3FF;
                    f = (sign << 31) | (exp << 23) | (mant << 13);
                }
            }
            else if (exp == 0x1F)
            {
                f = (sign << 31) | (0xFF << 23) | (mant << 13); // Inf / NaN
            }
            else
            {
                f = (sign << 31) | ((exp - 15 + 127) << 23) | (mant << 13);
            }

            var bytes = BitConverter.GetBytes(f);
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}
