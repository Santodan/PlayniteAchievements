using System;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// One GPU pass from a captured WGC frame to the encoder's input texture: crops to the client
    /// sub-rect, tone-maps when the source is scRGB HDR, and scales to the encoder size, all in a
    /// single draw.
    /// <para>
    /// The path this replaces ran three full-resolution passes — <see cref="GpuHdrToneMapper"/> over
    /// the whole frame, a <c>CopySubresourceRegion</c> crop, then a <see cref="FrameScaler"/>
    /// downscale — plus the two intermediate textures between them. At 2560x1440 HDR into 1080p that
    /// is 96.8 MB of memory traffic per frame against 37.8 MB here, on a GPU shared with the game and
    /// the compositor.
    /// </para>
    /// <para>
    /// The crop rides in the constant buffer as normalized source coordinates rather than being
    /// applied by a copy, so the sub-rect costs nothing. No half-texel inset is needed: destination
    /// pixel centers map to <c>(cropX + (j + 0.5) * cropW / N) / srcW</c>, and the resolution cap
    /// never upscales, so every bilinear tap stays inside the crop.
    /// </para>
    /// </summary>
    internal sealed class FrameComposer : IDisposable
    {
        // The tone-map math is character-for-character the operator GpuHdrToneMapper applies, so the
        // only thing this class changes about an HDR frame is that the scale now happens in linear
        // light before the transfer curve instead of after it.
        private const string ShaderSource = @"
cbuffer Params : register(b0) { float2 UvOrigin; float2 UvSize; float InvRefWhite; float3 _pad; };
Texture2D<float4> Src : register(t0);
SamplerState Samp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID) {
    VSOut o;
    float2 t = float2((id << 1) & 2, id & 2);
    // The oversized triangle extrapolates past the crop for the off-screen half; the rasterizer
    // clips it away, so only t in [0,1] — the crop itself — is ever sampled.
    o.uv = UvOrigin + t * UvSize;
    o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
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

float4 PSToneMap(VSOut i) : SV_Target {
    float3 lin = Src.Sample(Samp, i.uv).rgb * InvRefWhite;
    return float4(toSrgb(shoulder(lin.r)), toSrgb(shoulder(lin.g)), toSrgb(shoulder(lin.b)), 1);
}

float4 PSCopy(VSOut i) : SV_Target {
    return float4(Src.Sample(Samp, i.uv).rgb, 1);
}";

        private readonly D3D11.Device _device;
        private readonly D3D11.DeviceContext _context;
        private readonly D3D11.VertexShader _vs;
        private readonly D3D11.PixelShader _psToneMap;
        private readonly D3D11.PixelShader _psCopy;
        private readonly D3D11.SamplerState _sampler;
        private readonly D3D11.Buffer _params;

        private D3D11.Texture2D _rtvTarget;
        private D3D11.RenderTargetView _rtv;
        private bool _disposed;

        public FrameComposer(D3D11.Device device)
        {
            _device = device;
            _context = device.ImmediateContext;

            using (var vsb = ShaderBytecode.Compile(ShaderSource, "VS", "vs_4_0"))
            {
                _vs = new D3D11.VertexShader(device, vsb);
            }

            using (var psb = ShaderBytecode.Compile(ShaderSource, "PSToneMap", "ps_4_0"))
            {
                _psToneMap = new D3D11.PixelShader(device, psb);
            }

            using (var psb = ShaderBytecode.Compile(ShaderSource, "PSCopy", "ps_4_0"))
            {
                _psCopy = new D3D11.PixelShader(device, psb);
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

            // float2 UvOrigin + float2 UvSize + float InvRefWhite + float3 padding = 32 bytes.
            _params = new D3D11.Buffer(device, new D3D11.BufferDescription
            {
                SizeInBytes = 32,
                Usage = D3D11.ResourceUsage.Dynamic,
                BindFlags = D3D11.BindFlags.ConstantBuffer,
                CpuAccessFlags = D3D11.CpuAccessFlags.Write,
            });
        }

        /// <summary>
        /// Draws the <paramref name="cropW"/>x<paramref name="cropH"/> region at
        /// (<paramref name="cropX"/>, <paramref name="cropY"/>) of <paramref name="source"/> into the
        /// whole of <paramref name="target"/>, tone-mapping when <paramref name="hdr"/> is set.
        /// A non-positive crop size means the whole source.
        /// </summary>
        public void Compose(
            D3D11.Texture2D source,
            D3D11.Texture2D target,
            int cropX,
            int cropY,
            int cropW,
            int cropH,
            bool hdr,
            float refWhite)
        {
            if (_disposed || source == null || target == null)
            {
                return;
            }

            var sourceDesc = source.Description;
            if (cropW <= 0 || cropH <= 0)
            {
                cropX = 0;
                cropY = 0;
                cropW = sourceDesc.Width;
                cropH = sourceDesc.Height;
            }

            EnsureRtv(target);

            using (var srv = new D3D11.ShaderResourceView(_device, source))
            {
                var box = _context.MapSubresource(_params, 0, D3D11.MapMode.WriteDiscard, D3D11.MapFlags.None);
                Utilities.Write(
                    box.DataPointer,
                    new[]
                    {
                        cropX / (float)sourceDesc.Width,
                        cropY / (float)sourceDesc.Height,
                        cropW / (float)sourceDesc.Width,
                        cropH / (float)sourceDesc.Height,
                        1f / Math.Max(0.001f, refWhite),
                        0f, 0f, 0f,
                    },
                    0,
                    8);
                _context.UnmapSubresource(_params, 0);

                var targetDesc = target.Description;
                _context.OutputMerger.SetBlendState(null);
                _context.OutputMerger.SetRenderTargets(_rtv);
                _context.Rasterizer.SetViewport(new Viewport(0, 0, targetDesc.Width, targetDesc.Height));
                _context.InputAssembler.PrimitiveTopology = D3D.PrimitiveTopology.TriangleList;
                _context.InputAssembler.InputLayout = null;
                _context.VertexShader.Set(_vs);
                _context.VertexShader.SetConstantBuffer(0, _params);
                _context.PixelShader.Set(hdr ? _psToneMap : _psCopy);
                _context.PixelShader.SetShaderResource(0, srv);
                _context.PixelShader.SetSampler(0, _sampler);
                _context.PixelShader.SetConstantBuffer(0, _params);
                _context.Draw(3, 0);
                _context.PixelShader.SetShaderResource(0, null);
                _context.OutputMerger.SetRenderTargets((D3D11.RenderTargetView)null);
            }
        }

        /// <summary>A BGRA render target of the given size, for the composed encoder frame.</summary>
        public static D3D11.Texture2D CreateTarget(D3D11.Device device, int width, int height)
        {
            return new D3D11.Texture2D(device, new D3D11.Texture2DDescription
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
            _params?.Dispose();
            _sampler?.Dispose();
            _psCopy?.Dispose();
            _psToneMap?.Dispose();
            _vs?.Dispose();
        }
    }
}
