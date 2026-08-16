using System;
using System.Drawing;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure pixel math for compositing a recorded toast card into decoded video frames at clip
    /// export: client-to-frame rect scaling and a premultiplied source-over CPU blend with
    /// clipping and nearest-neighbor scaling. Kept free of Media Foundation so it unit-tests
    /// directly.
    /// </summary>
    internal static class OverlayBlitMath
    {
        /// <summary>
        /// Maps a card rect recorded relative to the game client rect (physical pixels) into
        /// video-frame coordinates. Encoded frames are the client area, possibly downscaled, so
        /// the mapping is a pure ratio — the same math the live compositor used.
        /// </summary>
        public static Rectangle ScaleRect(
            int relX, int relY, int cardW, int cardH,
            int clientW, int clientH, int frameW, int frameH)
        {
            return ScaleRect((double)relX, relY, cardW, cardH, clientW, clientH, frameW, frameH);
        }

        /// <summary>
        /// Sub-pixel position variant: the synthesized corner-plus-slide position carries fractional
        /// physical pixels, and rounding once here — after the frame scaling — is what keeps a slide
        /// smooth instead of stair-stepped by an early integer snap.
        /// </summary>
        public static Rectangle ScaleRect(
            double relX, double relY, int cardW, int cardH,
            int clientW, int clientH, int frameW, int frameH)
        {
            if (clientW <= 0 || clientH <= 0 || frameW <= 0 || frameH <= 0)
            {
                return Rectangle.Empty;
            }

            var sx = (double)frameW / clientW;
            var sy = (double)frameH / clientH;
            var x = (int)Math.Round(relX * sx);
            var y = (int)Math.Round(relY * sy);
            var w = Math.Max(1, (int)Math.Round(cardW * sx));
            var h = Math.Max(1, (int)Math.Round(cardH * sy));
            return new Rectangle(x, y, w, h);
        }

        /// <summary>
        /// Blends a premultiplied-BGRA overlay onto a top-down BGRA/RGB32 frame buffer at
        /// <paramref name="destRect"/> (nearest-neighbor scaled), clipping to the frame bounds.
        /// Premultiplied source-over per channel: dst = src + dst * (255 - srcA) / 255. The
        /// frame's fourth byte is left untouched (X channel in RGB32 video).
        /// </summary>
        public static void BlendOnto(
            byte[] frame, int frameW, int frameH, int frameStride,
            byte[] overlay, int overlayW, int overlayH,
            Rectangle destRect)
        {
            if (frame == null || overlay == null ||
                frameW <= 0 || frameH <= 0 || overlayW <= 0 || overlayH <= 0 ||
                destRect.Width <= 0 || destRect.Height <= 0)
            {
                return;
            }

            var x0 = Math.Max(0, destRect.X);
            var y0 = Math.Max(0, destRect.Y);
            var x1 = Math.Min(frameW, destRect.X + destRect.Width);
            var y1 = Math.Min(frameH, destRect.Y + destRect.Height);
            if (x0 >= x1 || y0 >= y1)
            {
                return;
            }

            // Nearest-neighbor source index maps, precomputed once per blit.
            var srcXs = new int[x1 - x0];
            for (var dx = x0; dx < x1; dx++)
            {
                var sx = (int)((long)(dx - destRect.X) * overlayW / destRect.Width);
                srcXs[dx - x0] = Math.Min(overlayW - 1, Math.Max(0, sx));
            }

            var overlayStride = overlayW * 4;
            for (var dy = y0; dy < y1; dy++)
            {
                var sy = (int)((long)(dy - destRect.Y) * overlayH / destRect.Height);
                sy = Math.Min(overlayH - 1, Math.Max(0, sy));
                var srcRow = sy * overlayStride;
                var dstRow = dy * frameStride;
                for (var dx = x0; dx < x1; dx++)
                {
                    var src = srcRow + (srcXs[dx - x0] << 2);
                    var alpha = overlay[src + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    var dst = dstRow + (dx << 2);
                    if (alpha == 255)
                    {
                        frame[dst] = overlay[src];
                        frame[dst + 1] = overlay[src + 1];
                        frame[dst + 2] = overlay[src + 2];
                        continue;
                    }

                    var inv = 255 - alpha;
                    frame[dst] = (byte)(overlay[src] + ((frame[dst] * inv + 127) / 255));
                    frame[dst + 1] = (byte)(overlay[src + 1] + ((frame[dst + 1] * inv + 127) / 255));
                    frame[dst + 2] = (byte)(overlay[src + 2] + ((frame[dst + 2] * inv + 127) / 255));
                }
            }
        }
    }
}
