using System;
using System.Drawing;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Blurs a decoded NV12 clip frame under a blurred toast card, in place, before the card is
    /// blended over it: the clip's counterpart of the live background the card shows on screen.
    /// Works on the card's region only, through <see cref="OverlayCompositor.ComposeRegion"/>, with
    /// <see cref="BoxBlur"/> on the luma and chroma planes, and writes back only the pixels inside
    /// the card's rounded rect. One instance per renderer keeps its scratch buffers.
    /// </summary>
    internal sealed class Nv12CardBlur
    {
        private byte[] _yBlur;
        private byte[] _uvBlur;
        private byte[] _line;

        /// <param name="card">The card's rect in frame pixels.</param>
        /// <param name="cornerRadius">The card's corner radius in frame pixels.</param>
        /// <param name="reachPx">How far the blur spreads, in frame pixels.</param>
        /// <param name="strength">0 to 1: how much of the blur replaces the frame (the host opacity).</param>
        public void Apply(
            OverlayCompositor compositor, SharpDX.MediaFoundation.Sample frame,
            int frameW, int frameH, RectangleF card, float cornerRadius, double reachPx, double strength)
        {
            if (compositor == null || frame == null || strength <= 0 || reachPx < 1 ||
                card.Width <= 0 || card.Height <= 0)
            {
                return;
            }

            var radius = BoxBlur.RadiusFor(reachPx);
            var pad = radius * BoxBlur.Passes;
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

            Buffer.BlockCopy(y, 0, _yBlur, 0, lumaLength);
            Buffer.BlockCopy(uv, 0, _uvBlur, 0, chromaLength);
            _line = BoxBlur.Apply(_yBlur, 0, width, height, width, 1, radius, _line);

            // Chroma is half resolution both ways, interleaved U/V: rows are still width bytes long.
            _line = BoxBlur.Apply(_uvBlur, 0, width / 2, height / 2, width, 2, Math.Max(1, radius / 2), _line);

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

        internal static bool InsideRoundedRect(float x, float y, RectangleF rect, float radius)
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
