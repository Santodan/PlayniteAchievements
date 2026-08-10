using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;
// SharpDX has a Rectangle of its own; the overlay geometry is all System.Drawing.
using Rectangle = System.Drawing.Rectangle;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Blends a premultiplied-BGRA toast card into a BGRA frame texture on the GPU: the card is
    /// uploaded to a dynamic texture and drawn as a viewport-filling triangle over the destination
    /// rect. Deliberately matches <see cref="OverlayBlitMath.BlendOnto"/> pixel for pixel — the same
    /// premultiplied source-over blend, the same nearest-neighbor scaling, and the frame's fourth
    /// channel left alone (it is the unused X channel of RGB32 video) — so moving a clip onto this
    /// path changes only where the work happens.
    /// <para>
    /// A destination rect reaching outside the frame is fine: the viewport may sit partly off the
    /// render target and the rasterizer clips it, which is the clipping the CPU blend does by hand.
    /// </para>
    /// </summary>
    internal sealed class GpuOverlayBlitter : IDisposable
    {
        // The same viewport-filling triangle FrameScaler draws: the visible region maps to UV 0..1.
        private const string ShaderSource = @"
Texture2D<float4> Src : register(t0);
SamplerState Samp : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VS(uint id : SV_VertexID) {
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 PS(VSOut i) : SV_Target { return Src.Sample(Samp, i.uv); }";

        private readonly D3D11.Device _device;
        private readonly D3D11.DeviceContext _context;
        private readonly D3D11.VertexShader _vs;
        private readonly D3D11.PixelShader _ps;
        private readonly D3D11.SamplerState _sampler;
        private readonly D3D11.BlendState _blend;

        private D3D11.Texture2D _overlay;
        private D3D11.ShaderResourceView _overlaySrv;
        private D3D11.Texture2D _rtvTarget;
        private D3D11.RenderTargetView _rtv;
        private bool _disposed;

        public GpuOverlayBlitter(D3D11.Device device)
        {
            _device = device;
            _context = device.ImmediateContext;

            using (var vsb = ShaderBytecode.Compile(ShaderSource, "VS", "vs_4_0"))
            {
                _vs = new D3D11.VertexShader(device, vsb);
            }

            using (var psb = ShaderBytecode.Compile(ShaderSource, "PS", "ps_4_0"))
            {
                _ps = new D3D11.PixelShader(device, psb);
            }

            // Linear, where the CPU blend scaled nearest-neighbor. A card is only ever drawn at 1:1 or
            // smaller (frames are the client area, possibly resolution-capped), and at 1:1 linear
            // sampling lands on texel centers and is bit-identical to point. It differs only when the
            // cap downscales the card — where nearest-neighbor drops pixels and aliases the text, so
            // this is the better of the two. FrameScaler already downscales frames the same way.
            _sampler = new D3D11.SamplerState(device, new D3D11.SamplerStateDescription
            {
                Filter = D3D11.Filter.MinMagMipLinear,
                AddressU = D3D11.TextureAddressMode.Clamp,
                AddressV = D3D11.TextureAddressMode.Clamp,
                AddressW = D3D11.TextureAddressMode.Clamp,
                ComparisonFunction = D3D11.Comparison.Never,
                MinimumLod = 0,
                MaximumLod = float.MaxValue,
            });

            // dst = src + dst * (1 - srcA): premultiplied source-over, matching BlendOnto. The write
            // mask keeps the frame's fourth channel as it was.
            var blendDescription = new D3D11.BlendStateDescription();
            blendDescription.RenderTarget[0] = new D3D11.RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = D3D11.BlendOption.One,
                DestinationBlend = D3D11.BlendOption.InverseSourceAlpha,
                BlendOperation = D3D11.BlendOperation.Add,
                SourceAlphaBlend = D3D11.BlendOption.One,
                DestinationAlphaBlend = D3D11.BlendOption.InverseSourceAlpha,
                AlphaBlendOperation = D3D11.BlendOperation.Add,
                RenderTargetWriteMask =
                    D3D11.ColorWriteMaskFlags.Red | D3D11.ColorWriteMaskFlags.Green | D3D11.ColorWriteMaskFlags.Blue,
            };
            _blend = new D3D11.BlendState(device, blendDescription);
        }

        /// <summary>
        /// Blends <paramref name="overlay"/> (premultiplied BGRA, tightly packed at
        /// <paramref name="overlayW"/> * 4 bytes per row) into <paramref name="target"/> at
        /// <paramref name="destRect"/>. The target must have been created as a render target.
        /// </summary>
        public void Blend(
            D3D11.Texture2D target, byte[] overlay, int overlayW, int overlayH, Rectangle destRect)
        {
            if (_disposed || target == null || overlay == null ||
                overlayW <= 0 || overlayH <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
            {
                return;
            }

            if (overlay.Length < overlayW * overlayH * 4)
            {
                return;
            }

            EnsureOverlayTexture(overlayW, overlayH);
            Upload(overlay, overlayW, overlayH);
            EnsureRtv(target);

            _context.OutputMerger.SetBlendState(_blend);
            _context.OutputMerger.SetRenderTargets(_rtv);
            _context.Rasterizer.SetViewport(
                new Viewport(destRect.X, destRect.Y, destRect.Width, destRect.Height));
            _context.InputAssembler.PrimitiveTopology = D3D.PrimitiveTopology.TriangleList;
            _context.InputAssembler.InputLayout = null;
            _context.VertexShader.Set(_vs);
            _context.PixelShader.Set(_ps);
            _context.PixelShader.SetShaderResource(0, _overlaySrv);
            _context.PixelShader.SetSampler(0, _sampler);
            _context.Draw(3, 0);

            _context.PixelShader.SetShaderResource(0, null);
            _context.OutputMerger.SetRenderTargets((D3D11.RenderTargetView)null);
            _context.OutputMerger.SetBlendState(null);
        }

        private void EnsureOverlayTexture(int width, int height)
        {
            if (_overlay != null &&
                _overlay.Description.Width == width && _overlay.Description.Height == height)
            {
                return;
            }

            _overlaySrv?.Dispose();
            _overlay?.Dispose();
            _overlay = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Dynamic,
                BindFlags = D3D11.BindFlags.ShaderResource,
                CpuAccessFlags = D3D11.CpuAccessFlags.Write,
                OptionFlags = D3D11.ResourceOptionFlags.None,
            });
            _overlaySrv = new D3D11.ShaderResourceView(_device, _overlay);
        }

        private void Upload(byte[] overlay, int overlayW, int overlayH)
        {
            var box = _context.MapSubresource(_overlay, 0, D3D11.MapMode.WriteDiscard, D3D11.MapFlags.None);
            try
            {
                // Row by row: a mapped texture's pitch is whatever the driver chose, not width * 4.
                var sourceStride = overlayW * 4;
                for (var y = 0; y < overlayH; y++)
                {
                    Marshal.Copy(
                        overlay, y * sourceStride, IntPtr.Add(box.DataPointer, y * box.RowPitch), sourceStride);
                }
            }
            finally
            {
                _context.UnmapSubresource(_overlay, 0);
            }
        }

        private void EnsureRtv(D3D11.Texture2D target)
        {
            if (ReferenceEquals(_rtvTarget, target) && _rtv != null)
            {
                return;
            }

            _rtv?.Dispose();
            _rtv = new D3D11.RenderTargetView(_device, target);
            _rtvTarget = target;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _rtv?.Dispose();
            _overlaySrv?.Dispose();
            _overlay?.Dispose();
            _blend?.Dispose();
            _sampler?.Dispose();
            _ps?.Dispose();
            _vs?.Dispose();
        }
    }
}
