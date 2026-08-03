using System;
using SharpDX;
using SharpDX.D3DCompiler;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// GPU downscale of one BGRA texture into another (a viewport-filling textured quad with linear
    /// filtering). Used by the video recorder to honor the resolution cap: the captured client frame
    /// is scaled to the encoder's (smaller) dimensions before encoding, so segments are written at the
    /// chosen resolution and the clip exporter can stream-copy them untouched.
    /// </summary>
    internal sealed class FrameScaler : IDisposable
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

        private D3D11.Texture2D _srvSource;
        private D3D11.ShaderResourceView _srv;
        private D3D11.Texture2D _rtvTarget;
        private D3D11.RenderTargetView _rtv;
        private bool _disposed;

        public FrameScaler(D3D11.Device device)
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
        }

        /// <summary>Draws <paramref name="source"/> scaled to fill <paramref name="target"/>.</summary>
        public void Scale(D3D11.Texture2D source, D3D11.Texture2D target)
        {
            if (_disposed || source == null || target == null)
            {
                return;
            }

            EnsureSrv(source);
            EnsureRtv(target);

            _context.OutputMerger.SetBlendState(null);
            _context.OutputMerger.SetRenderTargets(_rtv);
            _context.Rasterizer.SetViewport(new Viewport(0, 0, target.Description.Width, target.Description.Height));
            _context.InputAssembler.PrimitiveTopology = D3D.PrimitiveTopology.TriangleList;
            _context.InputAssembler.InputLayout = null;
            _context.VertexShader.Set(_vs);
            _context.PixelShader.Set(_ps);
            _context.PixelShader.SetShaderResource(0, _srv);
            _context.PixelShader.SetSampler(0, _sampler);
            _context.Draw(3, 0);

            _context.PixelShader.SetShaderResource(0, null);
            _context.OutputMerger.SetRenderTargets((D3D11.RenderTargetView)null);
        }

        private void EnsureSrv(D3D11.Texture2D source)
        {
            if (ReferenceEquals(_srvSource, source) && _srv != null)
            {
                return;
            }

            _srv?.Dispose();
            _srv = new D3D11.ShaderResourceView(_device, source);
            _srvSource = source;
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
            _srv?.Dispose();
            _sampler?.Dispose();
            _ps?.Dispose();
            _vs?.Dispose();
        }
    }
}
