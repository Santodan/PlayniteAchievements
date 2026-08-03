using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Blends a premultiplied-alpha BGRA overlay (the rendered notification toast) onto a target BGRA
    /// texture at a destination rectangle, on the GPU. Used by the video recorder to composite the
    /// toast into each captured frame — WGC's per-window capture can't see the separate toast window.
    /// The overlay is uploaded to a dynamic texture and drawn as a viewport-filling quad with
    /// premultiplied-over blending (SrcBlend=One, DestBlend=InvSrcAlpha).
    /// </summary>
    internal sealed class OverlayBlitter : IDisposable
    {
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
        private int _overlayW;
        private int _overlayH;

        private D3D11.Texture2D _rtvTarget;
        private D3D11.RenderTargetView _rtv;
        private bool _disposed;

        public OverlayBlitter(D3D11.Device device)
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

            var blendDesc = new D3D11.BlendStateDescription();
            blendDesc.RenderTarget[0] = new D3D11.RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = D3D11.BlendOption.One, // overlay is premultiplied
                DestinationBlend = D3D11.BlendOption.InverseSourceAlpha,
                BlendOperation = D3D11.BlendOperation.Add,
                SourceAlphaBlend = D3D11.BlendOption.One,
                DestinationAlphaBlend = D3D11.BlendOption.InverseSourceAlpha,
                AlphaBlendOperation = D3D11.BlendOperation.Add,
                RenderTargetWriteMask = D3D11.ColorWriteMaskFlags.All,
            };
            _blend = new D3D11.BlendState(device, blendDesc);
        }

        /// <summary>
        /// Composites the premultiplied-BGRA <paramref name="overlayBgra"/> (tightly packed) onto
        /// <paramref name="target"/> at [destX, destY, destW, destH] (target pixels). Clips to the
        /// target implicitly via the viewport.
        /// </summary>
        public void Blit(
            D3D11.Texture2D target, byte[] overlayBgra, int overlayW, int overlayH,
            int destX, int destY, int destW, int destH)
        {
            if (_disposed || target == null || overlayBgra == null || overlayW <= 0 || overlayH <= 0 || destW <= 0 || destH <= 0)
            {
                return;
            }

            UploadOverlay(overlayBgra, overlayW, overlayH);
            EnsureRtv(target);

            _context.OutputMerger.SetBlendState(_blend, null, unchecked((int)0xFFFFFFFF));
            _context.OutputMerger.SetRenderTargets(_rtv);
            _context.Rasterizer.SetViewport(new Viewport(destX, destY, destW, destH));
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

        private void UploadOverlay(byte[] bgra, int width, int height)
        {
            if (_overlay == null || _overlayW != width || _overlayH != height)
            {
                _overlaySrv?.Dispose();
                _overlay?.Dispose();
                _overlay = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = D3D11.ResourceUsage.Dynamic,
                    BindFlags = D3D11.BindFlags.ShaderResource,
                    CpuAccessFlags = D3D11.CpuAccessFlags.Write,
                    OptionFlags = D3D11.ResourceOptionFlags.None,
                });
                _overlaySrv = new D3D11.ShaderResourceView(_device, _overlay);
                _overlayW = width;
                _overlayH = height;
            }

            var box = _context.MapSubresource(_overlay, 0, D3D11.MapMode.WriteDiscard, D3D11.MapFlags.None);
            try
            {
                var srcStride = width * 4;
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(bgra, y * srcStride, box.DataPointer + y * box.RowPitch, srcStride);
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
