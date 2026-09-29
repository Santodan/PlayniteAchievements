using System;
using System.Drawing;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// A still premultiplied-BGRA overlay converted once into the terms an NV12 blend needs, so
    /// every frame it covers costs integer arithmetic only. The conversion is the one
    /// <see cref="OverlayBlitMath.BlendOntoNv12"/> performs per frame (BT.709 limited range, luma
    /// per pixel, chroma per 2x2 block from the block's mean coverage), done at construction for a
    /// fixed frame size, with the overlay nearest-neighbour scaled to that size. Each row keeps the
    /// span of pixels with any coverage, so transparent margins cost nothing.
    /// <para>
    /// Built for a full-frame overlay such as the screenshot frame's chrome, which the per-frame
    /// path would convert pixel by pixel in floating point on every frame of a framed clip.
    /// </para>
    /// </summary>
    internal sealed class StaticNv12Overlay
    {
        // Fixed point: alpha in 1/256 units (0..256); terms in 1/256 of a code value.
        private readonly ushort[] _lumaAlpha;
        private readonly ushort[] _lumaTerm;
        private readonly ushort[] _chromaAlpha;
        private readonly ushort[] _cbTerm;
        private readonly ushort[] _crTerm;
        private readonly int[] _lumaSpanStart;
        private readonly int[] _lumaSpanEnd;
        private readonly int[] _chromaSpanStart;
        private readonly int[] _chromaSpanEnd;

        private StaticNv12Overlay(Rectangle region)
        {
            Region = region;
            var lumaLength = region.Width * region.Height;
            var blocks = (region.Width / 2) * (region.Height / 2);
            _lumaAlpha = new ushort[lumaLength];
            _lumaTerm = new ushort[lumaLength];
            _chromaAlpha = new ushort[blocks];
            _cbTerm = new ushort[blocks];
            _crTerm = new ushort[blocks];
            _lumaSpanStart = new int[region.Height];
            _lumaSpanEnd = new int[region.Height];
            _chromaSpanStart = new int[region.Height / 2];
            _chromaSpanEnd = new int[region.Height / 2];
        }

        /// <summary>
        /// The frame area the overlay covers: the bounds of its non-transparent pixels, widened to
        /// whole chroma blocks. Empty when the overlay is fully transparent.
        /// </summary>
        public Rectangle Region { get; }

        /// <summary>
        /// Converts <paramref name="pixels"/> (premultiplied BGRA, <paramref name="width"/> x
        /// <paramref name="height"/>) for frames of <paramref name="frameW"/> x
        /// <paramref name="frameH"/>, stretching it over the whole frame. Null for unusable input.
        /// </summary>
        public static StaticNv12Overlay Create(byte[] pixels, int width, int height, int frameW, int frameH)
        {
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < width * height * 4 ||
                frameW < 2 || frameH < 2)
            {
                return null;
            }

            var evenW = frameW & ~1;
            var evenH = frameH & ~1;
            var sourceXs = new int[evenW];
            for (var x = 0; x < evenW; x++)
            {
                sourceXs[x] = Math.Min(width - 1, (int)((long)x * width / frameW));
            }

            var sourceRows = new int[evenH];
            for (var y = 0; y < evenH; y++)
            {
                sourceRows[y] = Math.Min(height - 1, (int)((long)y * height / frameH)) * width * 4;
            }

            // Bounds of the covered pixels, in frame coordinates.
            int x0 = evenW, y0 = evenH, x1 = 0, y1 = 0;
            for (var y = 0; y < evenH; y++)
            {
                var row = sourceRows[y];
                for (var x = 0; x < evenW; x++)
                {
                    if (pixels[row + (sourceXs[x] << 2) + 3] == 0)
                    {
                        continue;
                    }

                    if (x < x0) x0 = x;
                    if (x >= x1) x1 = x + 1;
                    if (y < y0) y0 = y;
                    if (y >= y1) y1 = y + 1;
                }
            }

            var region = x0 >= x1
                ? Rectangle.Empty
                : OverlayBlitMath.AlignToChromaBlocks(new Rectangle(x0, y0, x1 - x0, y1 - y0), evenW, evenH);
            var overlay = new StaticNv12Overlay(region);
            if (!region.IsEmpty)
            {
                overlay.Fill(pixels, sourceXs, sourceRows);
            }

            return overlay;
        }

        private void Fill(byte[] pixels, int[] sourceXs, int[] sourceRows)
        {
            var regionW = Region.Width;
            var blocksPerRow = regionW / 2;
            for (var y = 0; y < Region.Height; y++)
            {
                _lumaSpanStart[y] = regionW;
                _lumaSpanEnd[y] = 0;
            }

            for (var by = 0; by < Region.Height / 2; by++)
            {
                _chromaSpanStart[by] = blocksPerRow;
                _chromaSpanEnd[by] = 0;
                for (var bx = 0; bx < blocksPerRow; bx++)
                {
                    double alphaSum = 0, cbSum = 0, crSum = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var y = (by * 2) + dy;
                        var row = sourceRows[Region.Y + y];
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var x = (bx * 2) + dx;
                            var src = row + (sourceXs[Region.X + x] << 2);
                            var alpha = pixels[src + 3] / 255.0;
                            if (alpha <= 0)
                            {
                                continue;
                            }

                            var b = pixels[src] / 255.0;
                            var g = pixels[src + 1] / 255.0;
                            var r = pixels[src + 2] / 255.0;
                            var luma = (OverlayBlitMath.Kr * r) + (OverlayBlitMath.Kg * g) + (OverlayBlitMath.Kb * b);
                            var index = (y * regionW) + x;
                            _lumaAlpha[index] = ToFixed(alpha * 256.0);
                            _lumaTerm[index] = ToFixed(
                                ((OverlayBlitMath.LumaOffset * alpha) + (OverlayBlitMath.LumaScale * luma)) * 256.0);
                            if (x < _lumaSpanStart[y]) _lumaSpanStart[y] = x;
                            if (x >= _lumaSpanEnd[y]) _lumaSpanEnd[y] = x + 1;

                            alphaSum += alpha;
                            cbSum += (OverlayBlitMath.ChromaOffset * alpha) +
                                (OverlayBlitMath.ChromaScale * (b - luma) / (2.0 * (1.0 - OverlayBlitMath.Kb)));
                            crSum += (OverlayBlitMath.ChromaOffset * alpha) +
                                (OverlayBlitMath.ChromaScale * (r - luma) / (2.0 * (1.0 - OverlayBlitMath.Kr)));
                        }
                    }

                    if (alphaSum <= 0)
                    {
                        continue;
                    }

                    var block = (by * blocksPerRow) + bx;
                    _chromaAlpha[block] = ToFixed(alphaSum / 4.0 * 256.0);
                    _cbTerm[block] = ToFixed(cbSum / 4.0 * 256.0);
                    _crTerm[block] = ToFixed(crSum / 4.0 * 256.0);
                    if (bx < _chromaSpanStart[by]) _chromaSpanStart[by] = bx;
                    if (bx >= _chromaSpanEnd[by]) _chromaSpanEnd[by] = bx + 1;
                }
            }
        }

        /// <summary>
        /// Blends the overlay at <paramref name="opacity"/> into <see cref="Region"/>'s luma rows
        /// (<paramref name="yRegion"/>) and the interleaved chroma rows beneath them
        /// (<paramref name="uvRegion"/>), both packed at the region's width, as
        /// <see cref="OverlayCompositor.ComposeRegion"/> hands them over. Premultiplied source-over
        /// with colour and coverage scaled together: dst = o x term + (1 - o x alpha) x dst.
        /// </summary>
        public void Blend(byte[] yRegion, byte[] uvRegion, double opacity)
        {
            var o = (int)Math.Round(Math.Max(0, Math.Min(1, opacity)) * 256.0);
            if (o == 0 || Region.IsEmpty || yRegion == null || uvRegion == null)
            {
                return;
            }

            // Bands of chroma rows with the luma rows above them are independent, so a large
            // overlay (the frame chrome covers the whole picture) splits across cores; a small one
            // stays on the calling thread. Measured at 1080p on the Release build with full-frame
            // coverage: 4 ms per frame on one thread and 0.9 ms split into bands, against 34 ms
            // for the per-frame conversion this replaces.
            var chromaRows = Region.Height / 2;
            var bands = (chromaRows + BandChromaRows - 1) / BandChromaRows;
            if (bands > 1 && Region.Width * Region.Height >= ParallelMinimumPixels)
            {
                Parallel.For(0, bands, band => BlendBand(yRegion, uvRegion, o, band));
                return;
            }

            for (var band = 0; band < bands; band++)
            {
                BlendBand(yRegion, uvRegion, o, band);
            }
        }

        private const int BandChromaRows = 32;
        private const int ParallelMinimumPixels = 256 * 1024;

        private void BlendBand(byte[] yRegion, byte[] uvRegion, int o, int band)
        {
            var regionW = Region.Width;
            var firstBlockRow = band * BandChromaRows;
            var lastBlockRow = Math.Min(Region.Height / 2, firstBlockRow + BandChromaRows);
            for (var y = firstBlockRow * 2; y < lastBlockRow * 2; y++)
            {
                var rowBase = y * regionW;
                for (var x = _lumaSpanStart[y]; x < _lumaSpanEnd[y]; x++)
                {
                    var i = rowBase + x;
                    var a = _lumaAlpha[i];
                    if (a == 0)
                    {
                        continue;
                    }

                    yRegion[i] = Mix(o, _lumaTerm[i], a, yRegion[i]);
                }
            }

            var blocksPerRow = regionW / 2;
            for (var by = firstBlockRow; by < lastBlockRow; by++)
            {
                var blockBase = by * blocksPerRow;
                var rowBase = by * regionW;
                for (var bx = _chromaSpanStart[by]; bx < _chromaSpanEnd[by]; bx++)
                {
                    var k = blockBase + bx;
                    var a = _chromaAlpha[k];
                    if (a == 0)
                    {
                        continue;
                    }

                    var i = rowBase + (bx * 2);
                    uvRegion[i] = Mix(o, _cbTerm[k], a, uvRegion[i]);
                    uvRegion[i + 1] = Mix(o, _crTerm[k], a, uvRegion[i + 1]);
                }
            }
        }

        private static byte Mix(int opacity, int term, int alpha, int dst)
        {
            var value = ((opacity * term) + ((65536 - (opacity * alpha)) * dst) + 32768) >> 16;
            return value > 255 ? (byte)255 : (byte)value;
        }

        private static ushort ToFixed(double value)
        {
            var rounded = (int)Math.Round(value);
            return rounded < 0 ? (ushort)0 : rounded > ushort.MaxValue ? ushort.MaxValue : (ushort)rounded;
        }
    }
}
