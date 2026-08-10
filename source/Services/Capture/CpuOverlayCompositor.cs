using System;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Composites the toast card in system memory: the decoded frame is copied into a managed buffer,
    /// blended by <see cref="OverlayBlitMath"/>, and copied into a fresh Media Foundation buffer. Costs
    /// three full-frame copies plus (for bottom-up rows) two row flips per composited frame — about
    /// 44 MB of memcpy at 1440p — so it is the fallback for machines where the decoder and encoder
    /// cannot share a D3D11 device.
    /// </summary>
    internal sealed class CpuOverlayCompositor : IOverlayCompositor
    {
        private readonly byte[] _frameBuffer;
        private readonly int _absStride;
        private readonly bool _bottomUp;
        private readonly int _frameW;
        private readonly int _frameH;

        /// <param name="stride">
        /// The decoded type's default stride. Negative means bottom-up rows, which the blend has to be
        /// normalized around: <see cref="OverlayBlitMath.BlendOnto"/> works top-down.
        /// </param>
        public CpuOverlayCompositor(int frameW, int frameH, int stride)
        {
            _frameW = frameW;
            _frameH = frameH;
            _absStride = Math.Abs(stride);
            _bottomUp = stride < 0;
            _frameBuffer = new byte[_absStride * frameH];
        }

        public Sample Compose(Sample source, byte[] overlay, int overlayW, int overlayH, Rectangle destRect)
        {
            if (source == null || overlay == null)
            {
                return null;
            }

            using (var buffer = source.ConvertToContiguousBuffer())
            {
                var ptr = buffer.Lock(out _, out var currentLength);
                try
                {
                    var length = Math.Min(currentLength, _frameBuffer.Length);
                    Marshal.Copy(ptr, _frameBuffer, 0, length);
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            // A negative stride means bottom-up rows: normalize to top-down, blit, restore.
            if (_bottomUp)
            {
                FlipRows(_frameBuffer, _absStride, _frameH);
            }

            OverlayBlitMath.BlendOnto(
                _frameBuffer, _frameW, _frameH, _absStride, overlay, overlayW, overlayH, destRect);

            if (_bottomUp)
            {
                FlipRows(_frameBuffer, _absStride, _frameH);
            }

            var outBuffer = MediaFactory.CreateMemoryBuffer(_frameBuffer.Length);
            try
            {
                var outPtr = outBuffer.Lock(out _, out _);
                try
                {
                    Marshal.Copy(_frameBuffer, 0, outPtr, _frameBuffer.Length);
                }
                finally
                {
                    outBuffer.Unlock();
                }

                outBuffer.CurrentLength = _frameBuffer.Length;

                var outSample = MediaFactory.CreateSample();
                outSample.AddBuffer(outBuffer);
                return outSample;
            }
            finally
            {
                outBuffer.Dispose();
            }
        }

        private static void FlipRows(byte[] buffer, int stride, int height)
        {
            var temp = new byte[stride];
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(buffer, top * stride, temp, 0, stride);
                Buffer.BlockCopy(buffer, bottom * stride, buffer, top * stride, stride);
                Buffer.BlockCopy(temp, 0, buffer, bottom * stride, stride);
            }
        }

        public void Dispose()
        {
            // The frame buffer is managed; nothing native is held.
        }
    }
}
