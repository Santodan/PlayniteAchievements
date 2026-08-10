using System;
using SharpDX.MediaFoundation;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;
using Rectangle = System.Drawing.Rectangle;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Composites the toast card without the frame ever leaving the GPU: the decoded sample's D3D11
    /// texture is copied into a render target, the card is blended in by <see cref="GpuOverlayBlitter"/>,
    /// and that texture is handed back as a sample the hardware encoder reads directly. Where the CPU
    /// compositor moves about 44 MB per frame through system memory, nothing here is copied across the
    /// bus at all.
    /// <para>
    /// Returns null when the sample carries no DXGI buffer — a reader that decoded into system memory,
    /// which the caller answers by composing that frame on the CPU instead.
    /// </para>
    /// </summary>
    internal sealed class GpuOverlayCompositor : IOverlayCompositor
    {
        // IID of ID3D11Texture2D, for unwrapping and re-wrapping MF's DXGI surface buffers.
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        private readonly D3D11.Device _device;
        private readonly D3D11.Multithread _multithread;
        private readonly GpuOverlayBlitter _blitter;
        private readonly int _frameW;
        private readonly int _frameH;
        private bool _disposed;

        public GpuOverlayCompositor(D3D11.Device device, int frameW, int frameH)
        {
            _device = device;
            _frameW = frameW;
            _frameH = frameH;
            _blitter = new GpuOverlayBlitter(device);

            // The decoder shares this device and its immediate context, on its own thread. Multithread
            // protection makes single calls safe but not a sequence of them, and a blit is a sequence:
            // bind the target, set the viewport and shaders, draw. Without holding the lock across the
            // whole sequence the decoder's own context work interleaves into the middle of it, and the
            // card lands on the wrong target or not at all.
            _multithread = device.QueryInterfaceOrNull<D3D11.Multithread>();
        }

        public Sample Compose(Sample source, byte[] overlay, int overlayW, int overlayH, Rectangle destRect)
        {
            if (_disposed || source == null || overlay == null || source.BufferCount < 1)
            {
                return null;
            }

            using (var buffer = source.GetBufferByIndex(0))
            {
                var dxgi = QueryDxgi(buffer);
                if (dxgi == null)
                {
                    return null;
                }

                using (dxgi)
                {
                    var subresource = dxgi.SubresourceIndex;
                    dxgi.GetResource(IID_ID3D11Texture2D, out var resource);
                    if (resource == IntPtr.Zero)
                    {
                        return null;
                    }

                    // GetResource added a reference; the wrapper owns it from here.
                    using (var decoded = new D3D11.Texture2D(resource))
                    {
                        var description = decoded.Description;
                        if (description.Width != _frameW || description.Height != _frameH)
                        {
                            return null;
                        }

                        return ComposeTexture(decoded, subresource, overlay, overlayW, overlayH, destRect);
                    }
                }
            }
        }

        private Sample ComposeTexture(
            D3D11.Texture2D decoded, int subresource,
            byte[] overlay, int overlayW, int overlayH, Rectangle destRect)
        {
            // A fresh target per frame rather than a reused one: the sink writer holds queued samples
            // for as long as the encoder needs them, and reusing a texture would let the next frame
            // overwrite one still waiting to be read. Media Foundation keeps the texture alive by its
            // own reference once the buffer wraps it, so the using below only drops ours.
            using (var target = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
            {
                Width = _frameW,
                Height = _frameH,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Default,
                BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
                CpuAccessFlags = D3D11.CpuAccessFlags.None,
                OptionFlags = D3D11.ResourceOptionFlags.None,
            }))
            {
                // One critical section for the copy and the blit together: see the constructor.
                _multithread?.Enter();
                try
                {
                    _device.ImmediateContext.CopySubresourceRegion(decoded, subresource, null, target, 0);
                    _blitter.Blend(target, overlay, overlayW, overlayH, destRect);
                }
                finally
                {
                    _multithread?.Leave();
                }

                MediaFactory.CreateDXGISurfaceBuffer(IID_ID3D11Texture2D, target, 0, false, out var outBuffer);
                using (outBuffer)
                {
                    using (var buffer2D = outBuffer.QueryInterface<Buffer2D>())
                    {
                        outBuffer.CurrentLength = buffer2D.ContiguousLength;
                    }

                    // Time and duration are stamped by the caller.
                    var outSample = MediaFactory.CreateSample();
                    outSample.AddBuffer(outBuffer);
                    return outSample;
                }
            }
        }

        private static DXGIBuffer QueryDxgi(MediaBuffer buffer)
        {
            try
            {
                return buffer.QueryInterfaceOrNull<DXGIBuffer>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _blitter?.Dispose();
            _multithread?.Dispose();
        }
    }
}
