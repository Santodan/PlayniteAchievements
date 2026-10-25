using System;
using System.Drawing;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Blurs a decoded NV12 clip frame under a blurred toast card, in place, before the card is
    /// blended over it: the clip's counterpart of the DWM blur under the live toast. Works on the
    /// card's region only, through <see cref="OverlayCompositor.ComposeRegion"/>, with a three-pass
    /// box blur (close to a gaussian) on the luma and chroma planes, and writes back only the pixels
    /// inside the card's rounded rect. One instance per renderer keeps its scratch buffers.
    /// </summary>
    internal sealed class Nv12CardBlur
    {
        private const int Passes = 3;

        private byte[] _yBlur;
        private byte[] _uvBlur;
        private byte[] _line;

        /// <param name="card">The card's rect in frame pixels.</param>
        /// <param name="cornerRadius">The card's corner radius in frame pixels.</param>
        /// <param name="pxPerPhys">Frame pixels per physical screen pixel, which scales the reach.</param>
        /// <param name="strength">0 to 1: how much of the blur replaces the frame (the host opacity).</param>
        public void Apply(
            OverlayCompositor compositor, SharpDX.MediaFoundation.Sample frame,
            int frameW, int frameH, RectangleF card, float cornerRadius, double pxPerPhys, double strength)
        {
            if (compositor == null || frame == null || strength <= 0 || card.Width <= 0 || card.Height <= 0)
            {
                return;
            }

            // A box of radius r applied three times spreads about as far as the screenshot's
            // shrink-and-stretch by ToastBlurPatch.ReachPhys.
            var radius = Math.Max(1, (int)Math.Round(ToastBlurPatch.ReachPhys * pxPerPhys / 2));
            var pad = radius * Passes;
            var region = OverlayBlitMath.AlignToChromaBlocks(
                OverlayBlitMath.ClipToFrame(Rectangle.Round(RectangleF.Inflate(card, pad, pad)), frameW, frameH),
                frameW, frameH);
            if (region.IsEmpty)
            {
                return;
            }

            var local = new RectangleF(card.X - region.X, card.Y - region.Y, card.Width, card.Height);
            compositor.ComposeRegion(frame, region, (y, uv) =>
                BlurRegion(y, uv, region.Width, region.Height, local, cornerRadius, radius, strength));
        }

        private void BlurRegion(
            byte[] y, byte[] uv, int width, int height, RectangleF card, float cornerRadius, int radius, double strength)
        {
            var lumaLength = width * height;
            var chromaLength = lumaLength / 2;
            if (_yBlur == null || _yBlur.Length < lumaLength)
            {
                _yBlur = new byte[lumaLength];
                _uvBlur = new byte[chromaLength];
            }

            if (_line == null || _line.Length < Math.Max(width, height) * 2)
            {
                _line = new byte[Math.Max(width, height) * 2];
            }

            Buffer.BlockCopy(y, 0, _yBlur, 0, lumaLength);
            Buffer.BlockCopy(uv, 0, _uvBlur, 0, chromaLength);
            for (var pass = 0; pass < Passes; pass++)
            {
                BoxBlur(_yBlur, width, height, 1, radius);
                BoxBlur(_uvBlur, width / 2, height / 2, 2, Math.Max(1, radius / 2));
            }

            var weight = (int)Math.Round(Math.Max(0, Math.Min(1, strength)) * 256);
            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    if (InsideRoundedRect(col + 0.5f, row + 0.5f, card, cornerRadius))
                    {
                        var i = row * width + col;
                        y[i] = Mix(y[i], _yBlur[i], weight);
                    }
                }
            }

            // Each interleaved UV pair covers a 2x2 luma block; it follows the block's center.
            for (var row = 0; row < height / 2; row++)
            {
                for (var pair = 0; pair < width / 2; pair++)
                {
                    if (InsideRoundedRect(pair * 2 + 1f, row * 2 + 1f, card, cornerRadius))
                    {
                        var i = row * width + pair * 2;
                        uv[i] = Mix(uv[i], _uvBlur[i], weight);
                        uv[i + 1] = Mix(uv[i + 1], _uvBlur[i + 1], weight);
                    }
                }
            }
        }

        private static byte Mix(byte original, byte blurred, int weight)
        {
            return (byte)(original + (((blurred - original) * weight) >> 8));
        }

        /// <summary>
        /// One horizontal and one vertical running-sum box pass over a plane of
        /// <paramref name="channels"/>-byte pixels, edges clamped.
        /// </summary>
        private void BoxBlur(byte[] plane, int width, int height, int channels, int radius)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var rowBytes = width * channels;
            var span = radius * 2 + 1;
            for (var row = 0; row < height; row++)
            {
                var start = row * rowBytes;
                Buffer.BlockCopy(plane, start, _line, 0, rowBytes);
                for (var c = 0; c < channels; c++)
                {
                    var sum = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        sum += _line[Clamp(k, width) * channels + c];
                    }

                    for (var x = 0; x < width; x++)
                    {
                        plane[start + x * channels + c] = (byte)(sum / span);
                        sum += _line[Clamp(x + radius + 1, width) * channels + c] -
                               _line[Clamp(x - radius, width) * channels + c];
                    }
                }
            }

            for (var col = 0; col < width; col++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var offset = col * channels + c;
                    for (var row = 0; row < height; row++)
                    {
                        _line[row] = plane[row * rowBytes + offset];
                    }

                    var sum = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        sum += _line[Clamp(k, height)];
                    }

                    for (var row = 0; row < height; row++)
                    {
                        plane[row * rowBytes + offset] = (byte)(sum / span);
                        sum += _line[Clamp(row + radius + 1, height)] - _line[Clamp(row - radius, height)];
                    }
                }
            }
        }

        private static int Clamp(int index, int length)
        {
            return index < 0 ? 0 : index >= length ? length - 1 : index;
        }

        private static bool InsideRoundedRect(float x, float y, RectangleF rect, float radius)
        {
            if (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom)
            {
                return false;
            }

            var r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
            if (r <= 0)
            {
                return true;
            }

            var cx = x < rect.Left + r ? rect.Left + r : x > rect.Right - r ? rect.Right - r : x;
            var cy = y < rect.Top + r ? rect.Top + r : y > rect.Bottom - r ? rect.Bottom - r : y;
            var dx = x - cx;
            var dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }
    }
}
