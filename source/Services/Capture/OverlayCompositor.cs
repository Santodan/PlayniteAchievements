using System;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Blends a premultiplied-BGRA overlay into a decoded NV12 frame in place. Only the chroma
    /// blocks the card's rectangle covers are touched: their luma and chroma rows are copied out
    /// of the sample's own buffer, blended by <see cref="OverlayBlitMath.BlendOntoNv12"/>, and
    /// copied back, so a card costs its own area rather than the frame's. The earlier version
    /// worked on RGB32: the reader converted every decoded frame to RGB, the compositor copied the
    /// whole frame into managed memory, blended, allocated a fresh Media Foundation buffer and
    /// copied the frame back, and the encoding sink converted RGB back to NV12 — two colour
    /// conversions and three full-frame copies per frame that made a carded frame cost about 2.6
    /// times a plain one at 1080p and dominated the re-encode.
    /// <para>
    /// The buffer is addressed through <see cref="IMF2DBuffer"/> when the sample offers it, which
    /// hands out the real scanline pointer and pitch; otherwise the buffer is locked flat and rows
    /// are mapped by the stride it was created with. A sample holding several buffers is first
    /// collapsed to one, which the sample then keeps, so the caller's sample is always the one
    /// carrying the card. The chroma plane follows the luma plane at the same pitch, NV12's layout;
    /// the re-encoder hands over frames already repacked to the exact frame height, so that plane
    /// sits exactly <c>frameH</c> rows down.
    /// </para>
    /// </summary>
    internal sealed class OverlayCompositor
    {
        private readonly int _stride;
        private readonly int _frameW;
        private readonly int _frameH;
        private byte[] _yRegion;
        private byte[] _uvRegion;

        /// <param name="stride">
        /// The decoded type's default stride (bytes per luma row), used only by the contiguous
        /// fallback; the 2D path reads the pitch from the buffer itself.
        /// </param>
        public OverlayCompositor(int frameW, int frameH, int stride)
        {
            _frameW = frameW;
            _frameH = frameH;
            _stride = Math.Abs(stride);
        }

        /// <summary>
        /// Draws the overlay into <paramref name="frame"/> at <paramref name="destRect"/>
        /// (nearest-neighbour scaled, clipped to the frame). Returns false when nothing was drawn:
        /// no overlay, or a rectangle entirely off the frame.
        /// </summary>
        public bool Compose(Sample frame, byte[] overlay, int overlayW, int overlayH, Rectangle destRect)
        {
            if (frame == null || overlay == null || overlayW <= 0 || overlayH <= 0)
            {
                return false;
            }

            var region = OverlayBlitMath.AlignToChromaBlocks(
                OverlayBlitMath.ClipToFrame(destRect, _frameW, _frameH), _frameW, _frameH);
            if (region.IsEmpty)
            {
                return false;
            }

            var lumaLength = region.Width * region.Height;
            if (_yRegion == null || _yRegion.Length < lumaLength)
            {
                _yRegion = new byte[lumaLength];
                _uvRegion = new byte[lumaLength / 2];
            }

            // The rectangle relative to the region, so the blit's own clipping lands the same pixels.
            var regionRect = new Rectangle(
                destRect.X - region.X, destRect.Y - region.Y, destRect.Width, destRect.Height);

            if (frame.BufferCount == 1)
            {
                using (var buffer = frame.GetBufferByIndex(0))
                {
                    if (!TryBlend2D(buffer, overlay, overlayW, overlayH, region, regionRect))
                    {
                        BlendContiguous(buffer, overlay, overlayW, overlayH, region, regionRect);
                    }
                }

                return true;
            }

            // Several buffers: collapse to one contiguous buffer, blend into it, and make it the
            // sample's only buffer so the composited pixels are what the sink receives.
            using (var contiguous = frame.ConvertToContiguousBuffer())
            {
                BlendContiguous(contiguous, overlay, overlayW, overlayH, region, regionRect);
                frame.RemoveAllBuffers();
                frame.AddBuffer(contiguous);
            }

            return true;
        }

        /// <summary>
        /// The 2D path: locks the buffer's scanlines directly. Returns false when the buffer does
        /// not implement <c>IMF2DBuffer</c> or reports a bottom-up pitch (never the case for NV12),
        /// leaving the contiguous fallback to do the work.
        /// </summary>
        private bool TryBlend2D(
            MediaBuffer buffer, byte[] overlay, int overlayW, int overlayH, Rectangle region, Rectangle regionRect)
        {
            using (var view = Buffer2DHandle.From(buffer))
            {
                if (!view.IsValid)
                {
                    return false;
                }

                view.Buffer.Lock2D(out var scanline0, out var pitch);
                try
                {
                    if (pitch <= 0)
                    {
                        return false;
                    }

                    BlendPlanes(scanline0, pitch, overlay, overlayW, overlayH, region, regionRect);
                }
                finally
                {
                    view.Buffer.Unlock2D();
                }

                return true;
            }
        }

        /// <summary>The contiguous path: a 1D lock with rows laid out by the decoded type's stride.</summary>
        private void BlendContiguous(
            MediaBuffer buffer, byte[] overlay, int overlayW, int overlayH, Rectangle region, Rectangle regionRect)
        {
            var ptr = buffer.Lock(out _, out var currentLength);
            try
            {
                if (currentLength < _stride * _frameH * 3 / 2)
                {
                    return;
                }

                BlendPlanes(ptr, _stride, overlay, overlayW, overlayH, region, regionRect);
            }
            finally
            {
                buffer.Unlock();
            }
        }

        /// <summary>
        /// Copies the region's luma rows and the chroma rows beneath them out of the frame, blends
        /// the overlay into them, and copies both back. The chroma plane starts <c>frameH</c> rows
        /// below the luma plane and shares its pitch; its rows cover two luma rows each.
        /// </summary>
        private void BlendPlanes(
            IntPtr scanline0, int pitch, byte[] overlay, int overlayW, int overlayH, Rectangle region, Rectangle regionRect)
        {
            var chromaPlane = IntPtr.Add(scanline0, pitch * _frameH);
            var chromaRows = region.Height / 2;
            for (var row = 0; row < region.Height; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(scanline0, ((region.Y + row) * pitch) + region.X),
                    _yRegion, row * region.Width, region.Width);
            }

            for (var row = 0; row < chromaRows; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(chromaPlane, (((region.Y / 2) + row) * pitch) + region.X),
                    _uvRegion, row * region.Width, region.Width);
            }

            OverlayBlitMath.BlendOntoNv12(
                _yRegion, _uvRegion, region.Width, region.Height, overlay, overlayW, overlayH, regionRect);

            for (var row = 0; row < region.Height; row++)
            {
                Marshal.Copy(
                    _yRegion, row * region.Width,
                    IntPtr.Add(scanline0, ((region.Y + row) * pitch) + region.X), region.Width);
            }

            for (var row = 0; row < chromaRows; row++)
            {
                Marshal.Copy(
                    _uvRegion, row * region.Width,
                    IntPtr.Add(chromaPlane, (((region.Y / 2) + row) * pitch) + region.X), region.Width);
            }
        }

    }
}
