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
        /// The part of <paramref name="destRect"/> that lies inside a frame of the given size —
        /// the rows and columns a blit will actually touch — or an empty rectangle when none does.
        /// </summary>
        public static Rectangle ClipToFrame(Rectangle destRect, int frameW, int frameH)
        {
            if (frameW <= 0 || frameH <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
            {
                return Rectangle.Empty;
            }

            var x0 = Math.Max(0, destRect.X);
            var y0 = Math.Max(0, destRect.Y);
            var x1 = Math.Min(frameW, destRect.X + destRect.Width);
            var y1 = Math.Min(frameH, destRect.Y + destRect.Height);
            return x0 >= x1 || y0 >= y1 ? Rectangle.Empty : new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// Adds a premultiplied-BGRA difference layer into <paramref name="target"/>, scaled:
        /// target = clamp(target + layer × scale), all four channels (the layer's alpha is the
        /// halo's alpha). Reconstructs an effect's contribution on top of an effect-stripped card
        /// render — the layer is non-negative by construction (with-effect minus without), so this
        /// only ever brightens. A non-positive scale is a no-op.
        /// </summary>
        public static void AddScaled(byte[] target, byte[] layer, double scale)
        {
            if (target == null || layer == null || target.Length != layer.Length || scale <= 0)
            {
                return;
            }

            // Fixed-point 8.8 multiplier; clamped well past any sane pulse overshoot.
            var factor = (int)Math.Round(Math.Min(scale, 4.0) * 256.0);
            for (var i = 0; i < target.Length; i++)
            {
                var value = target[i] + ((layer[i] * factor) >> 8);
                target[i] = value > 255 ? (byte)255 : (byte)value;
            }
        }

        /// <summary>
        /// Widens a clipped rectangle to even bounds inside an even-sized frame, so it covers whole
        /// 2x2 chroma blocks of a 4:2:0 image. Empty stays empty.
        /// </summary>
        public static Rectangle AlignToChromaBlocks(Rectangle clipped, int frameW, int frameH)
        {
            if (clipped.IsEmpty)
            {
                return Rectangle.Empty;
            }

            var x0 = clipped.X & ~1;
            var y0 = clipped.Y & ~1;
            var x1 = Math.Min(frameW & ~1, (clipped.Right + 1) & ~1);
            var y1 = Math.Min(frameH & ~1, (clipped.Bottom + 1) & ~1);
            return x0 >= x1 || y0 >= y1 ? Rectangle.Empty : new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        // BT.709 luma weights and the limited-range 8-bit scale the clips are encoded with.
        private const double Kr = 0.2126;
        private const double Kb = 0.0722;
        private const double Kg = 1.0 - Kr - Kb;
        private const double LumaScale = 219.0;
        private const double ChromaScale = 224.0;
        private const double LumaOffset = 16.0;
        private const double ChromaOffset = 128.0;

        /// <summary>
        /// Blends a premultiplied-BGRA overlay onto an NV12 region: <paramref name="yRegion"/> holds
        /// <paramref name="regionH"/> luma rows of <paramref name="regionW"/> bytes, <paramref name="uvRegion"/>
        /// the interleaved Cb/Cr rows beneath them (half as many rows, the same bytes per row). Both
        /// dimensions must be even, and <paramref name="destRect"/> is relative to the region. The
        /// overlay converts to BT.709 limited-range Y'CbCr as it goes: luma blends per pixel, chroma
        /// per 2x2 block from the block's mean coverage, so a card edge lands the same way the
        /// encoder's own subsampling would land it. Pixels outside the frame or the rectangle
        /// contribute nothing, and an overlay pixel of zero alpha leaves its pixel untouched.
        /// </summary>
        public static void BlendOntoNv12(
            byte[] yRegion, byte[] uvRegion, int regionW, int regionH,
            byte[] overlay, int overlayW, int overlayH, Rectangle destRect)
        {
            if (yRegion == null || uvRegion == null || overlay == null ||
                regionW <= 0 || regionH <= 0 || (regionW & 1) != 0 || (regionH & 1) != 0 ||
                overlayW <= 0 || overlayH <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
            {
                return;
            }

            var x0 = Math.Max(0, destRect.X);
            var y0 = Math.Max(0, destRect.Y);
            var x1 = Math.Min(regionW, destRect.X + destRect.Width);
            var y1 = Math.Min(regionH, destRect.Y + destRect.Height);
            if (x0 >= x1 || y0 >= y1)
            {
                return;
            }

            var overlayStride = overlayW * 4;
            // Whole chroma blocks around the touched pixels; pixels outside the rectangle inside a
            // block count as transparent, which is what keeps a card edge from tinting its neighbours.
            var bx0 = x0 & ~1;
            var by0 = y0 & ~1;
            var bx1 = Math.Min(regionW, (x1 + 1) & ~1);
            var by1 = Math.Min(regionH, (y1 + 1) & ~1);
            for (var by = by0; by < by1; by += 2)
            {
                for (var bx = bx0; bx < bx1; bx += 2)
                {
                    var alphaSum = 0.0;
                    var cbSum = 0.0;
                    var crSum = 0.0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var y = by + dy;
                        var sy = (int)((long)(y - destRect.Y) * overlayH / destRect.Height);
                        sy = Math.Min(overlayH - 1, Math.Max(0, sy));
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var x = bx + dx;
                            if (x < x0 || x >= x1 || y < y0 || y >= y1)
                            {
                                continue;
                            }

                            var sx = (int)((long)(x - destRect.X) * overlayW / destRect.Width);
                            sx = Math.Min(overlayW - 1, Math.Max(0, sx));
                            var src = (sy * overlayStride) + (sx << 2);
                            var alpha = overlay[src + 3] / 255.0;
                            if (alpha <= 0)
                            {
                                continue;
                            }

                            // Premultiplied components, so each term below is already alpha × the
                            // colour's own value: the blend is dst = term + (1 - alpha) × dst.
                            var b = overlay[src] / 255.0;
                            var g = overlay[src + 1] / 255.0;
                            var r = overlay[src + 2] / 255.0;
                            var luma = (Kr * r) + (Kg * g) + (Kb * b);
                            var lumaTerm = (LumaOffset * alpha) + (LumaScale * luma);
                            var yIndex = (y * regionW) + x;
                            yRegion[yIndex] = Clamp(lumaTerm + ((1.0 - alpha) * yRegion[yIndex]));

                            alphaSum += alpha;
                            cbSum += (ChromaOffset * alpha) + (ChromaScale * (b - luma) / (2.0 * (1.0 - Kb)));
                            crSum += (ChromaOffset * alpha) + (ChromaScale * (r - luma) / (2.0 * (1.0 - Kr)));
                        }
                    }

                    if (alphaSum <= 0)
                    {
                        continue;
                    }

                    var blockAlpha = alphaSum / 4.0;
                    var uvIndex = ((by / 2) * regionW) + bx;
                    uvRegion[uvIndex] = Clamp((cbSum / 4.0) + ((1.0 - blockAlpha) * uvRegion[uvIndex]));
                    uvRegion[uvIndex + 1] = Clamp((crSum / 4.0) + ((1.0 - blockAlpha) * uvRegion[uvIndex + 1]));
                }
            }
        }

        private static byte Clamp(double value)
        {
            var rounded = (int)Math.Round(value);
            return rounded < 0 ? (byte)0 : rounded > 255 ? (byte)255 : (byte)rounded;
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
