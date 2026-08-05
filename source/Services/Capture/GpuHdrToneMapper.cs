using System;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// GPU HDR→SDR tone-mapper. Renders an scRGB (R16G16B16A16Float) WGC frame to a BGRA (SDR)
    /// texture on the GPU with the same operator as <see cref="HdrToneMap"/> (normalize by the SDR
    /// white level, shoulder the highlights, sRGB-encode) — per frame, GPU-resident, so it can run at
    /// video frame rates where the CPU path can't. The output texture is bindable as an MF DXGI
    /// surface for the hardware encoder. Reused across frames; recreated when the size changes.
    /// </summary>
    internal sealed class GpuHdrToneMapper : IDisposable
    {
        private const string ShaderSource = @"
cbuffer Params : register(b0) { float InvRefWhite; float3 _pad; };
Texture2D<float4> Src : register(t0);
SamplerState Samp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID) {
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float shoulder(float n) {
    if (n <= 0) return 0;
    const float knee = 0.9;
    if (n <= knee) return n;
    return knee + (1 - knee) * (1 - exp(-(n - knee) / (1 - knee)));
}

float toSrgb(float c) {
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

float4 PS(VSOut i) : SV_Target {
    float3 lin = Src.Sample(Samp, i.uv).rgb * InvRefWhite;
    return float4(toSrgb(shoulder(lin.r)), toSrgb(shoulder(lin.g)), toSrgb(shoulder(lin.b)), 1);
}";

        private readonly D3D11.Device _device;
        private readonly D3D11.DeviceContext _context;
        private readonly D3D11.VertexShader _vs;
        private readonly D3D11.PixelShader _ps;
        private readonly D3D11.SamplerState _sampler;
        private readonly D3D11.Buffer _params;

        private D3D11.Texture2D _output;
        private D3D11.RenderTargetView _rtv;
        private int _width;
        private int _height;
        private bool _disposed;

        public GpuHdrToneMapper(D3D11.Device device)
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

            // 16-byte constant buffer (float InvRefWhite + padding).
            _params = new D3D11.Buffer(device, new D3D11.BufferDescription
            {
                SizeInBytes = 16,
                Usage = D3D11.ResourceUsage.Dynamic,
                BindFlags = D3D11.BindFlags.ConstantBuffer,
                CpuAccessFlags = D3D11.CpuAccessFlags.Write,
            });
        }

        /// <summary>
        /// Tone-maps <paramref name="source"/> (scRGB float) into an owned BGRA SDR texture and returns
        /// it. <paramref name="refWhite"/> is the monitor's SDR white level in scRGB (1.0 = 80 nits).
        /// The returned texture is owned by this mapper and reused on the next call — copy or encode it
        /// before calling again.
        /// </summary>
        public D3D11.Texture2D ToneMap(D3D11.Texture2D source, float refWhite)
        {
            var desc = source.Description;
            EnsureOutput(desc.Width, desc.Height);

            using (var srv = new D3D11.ShaderResourceView(_device, source))
            {
                var box = _context.MapSubresource(_params, 0, D3D11.MapMode.WriteDiscard, D3D11.MapFlags.None);
                Utilities.Write(box.DataPointer, new float[] { 1f / Math.Max(0.001f, refWhite), 0, 0, 0 }, 0, 4);
                _context.UnmapSubresource(_params, 0);

                _context.OutputMerger.SetRenderTargets(_rtv);
                _context.Rasterizer.SetViewport(new Viewport(0, 0, _width, _height));
                _context.InputAssembler.PrimitiveTopology = D3D.PrimitiveTopology.TriangleList;
                _context.InputAssembler.InputLayout = null;
                _context.VertexShader.Set(_vs);
                _context.PixelShader.Set(_ps);
                _context.PixelShader.SetShaderResource(0, srv);
                _context.PixelShader.SetSampler(0, _sampler);
                _context.PixelShader.SetConstantBuffer(0, _params);
                _context.Draw(3, 0);
                _context.PixelShader.SetShaderResource(0, null);
                _context.OutputMerger.SetRenderTargets((D3D11.RenderTargetView)null);
            }

            return _output;
        }

        private void EnsureOutput(int width, int height)
        {
            if (_output != null && _width == width && _height == height)
            {
                return;
            }

            _rtv?.Dispose();
            _output?.Dispose();

            _output = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Default,
                BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
                CpuAccessFlags = D3D11.CpuAccessFlags.None,
                OptionFlags = D3D11.ResourceOptionFlags.None,
            });
            _rtv = new D3D11.RenderTargetView(_device, _output);
            _width = width;
            _height = height;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _rtv?.Dispose();
            _output?.Dispose();
            _params?.Dispose();
            _sampler?.Dispose();
            _ps?.Dispose();
            _vs?.Dispose();
        }
    }
}
