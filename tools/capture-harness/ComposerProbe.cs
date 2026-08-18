using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using PlayniteAchievements.Services.Capture;
using SharpDX;
using SharpDX.DXGI;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;

namespace CaptureHarnessTools
{
    /// <summary>
    /// A/B pixel comparison of the recorder's frame path: the shipped three-pass route
    /// (GpuHdrToneMapper -> CopySubresourceRegion crop -> FrameScaler downscale) against the single
    /// FrameComposer pass meant to replace it, over identical synthetic input.
    ///
    /// This is the check that has to pass before the recorder is switched over, because the two
    /// classes of defect the fold can introduce are both invisible in a plausible-looking clip:
    /// a crop that lands on the wrong pixels, and a sampler that pulls window chrome into the edge
    /// of the frame. Both show up here as a numeric delta against the path that ships today.
    ///
    /// One difference is expected rather than a defect: on the HDR path the shipped route tone-maps
    /// at full resolution and then averages the sRGB-encoded result, while the fold averages linear
    /// scRGB and then tone-maps. Averaging in linear light before the transfer curve is the correct
    /// order, so the fold should differ from the reference exactly where the reference is wrong.
    /// The probe reports that difference instead of asserting it away.
    /// </summary>
    internal static class ComposerProbe
    {
        private const float RefWhite = 1.0f;

        private sealed class Case
        {
            public string Name;
            public int SrcW, SrcH;
            public int CropX, CropY, CropW, CropH;
            public int EncW, EncH;
            public bool Hdr;
            // A hard checker of blown highlights maximises the tone-map order difference; a
            // smooth ramp is what a real frame looks like.
            public bool Checker;
            // The luminance centroid only locates the picture while the two paths agree on colour.
            // Where the order difference is deliberately extreme it moves for photometric reasons,
            // so those cases characterise the difference and geometry is proven by the 1:1 cases.
            public bool GeometryCheck = true;
            // Geometry cases must match exactly; resampling cases carry an expected tolerance.
            public double MaxAllowed;
            public string Expectation;
        }

        private static readonly Case[] Cases =
        {
            new Case
            {
                Name = "SDR identity 1:1",
                SrcW = 640, SrcH = 360, CropX = 0, CropY = 0, CropW = 640, CropH = 360,
                EncW = 640, EncH = 360, Hdr = false, MaxAllowed = 0,
                Expectation = "exact: same texels, no resample",
            },
            new Case
            {
                Name = "SDR identity downscale",
                SrcW = 1280, SrcH = 720, CropX = 0, CropY = 0, CropW = 1280, CropH = 720,
                EncW = 640, EncH = 360, Hdr = false, MaxAllowed = 1,
                Expectation = "exact: one bilinear pass either way",
            },
            new Case
            {
                Name = "SDR sub-rect crop 1:1",
                SrcW = 640, SrcH = 360, CropX = 8, CropY = 31, CropW = 600, CropH = 300,
                EncW = 600, EncH = 300, Hdr = false, MaxAllowed = 0,
                Expectation = "exact: crop must land on the same pixels",
            },
            new Case
            {
                Name = "SDR sub-rect crop downscale",
                SrcW = 1280, SrcH = 720, CropX = 8, CropY = 31, CropW = 1264, CropH = 681,
                EncW = 632, EncH = 340, Hdr = false, MaxAllowed = 2,
                Expectation = "edge bleed check: sampler must stay inside the crop",
            },
            new Case
            {
                Name = "SDR odd-size crop 1:1",
                SrcW = 641, SrcH = 361, CropX = 0, CropY = 0, CropW = 640, CropH = 360,
                EncW = 640, EncH = 360, Hdr = false, MaxAllowed = 0,
                Expectation = "exact: evenDimensions rounds an odd window down by a pixel",
            },
            new Case
            {
                Name = "HDR identity 1:1",
                SrcW = 640, SrcH = 360, CropX = 0, CropY = 0, CropW = 640, CropH = 360,
                EncW = 640, EncH = 360, Hdr = true, MaxAllowed = 1,
                Expectation = "exact: tone-map order cannot differ without a resample",
            },
            new Case
            {
                Name = "HDR sub-rect crop 1:1",
                SrcW = 2560, SrcH = 1440, CropX = 8, CropY = 31, CropW = 2544, CropH = 1401,
                EncW = 2544, EncH = 1401, Hdr = true, Checker = true, MaxAllowed = 0,
                Expectation = "exact: HDR geometry, no resample so order cannot matter",
            },
            new Case
            {
                Name = "HDR identity downscale",
                SrcW = 1280, SrcH = 720, CropX = 0, CropY = 0, CropW = 1280, CropH = 720,
                EncW = 640, EncH = 360, Hdr = true, Checker = true, MaxAllowed = 255,
                GeometryCheck = false,
                Expectation = "worst-case order difference on blown-highlight checker",
            },
            new Case
            {
                Name = "HDR checker 1440p->1080p",
                SrcW = 2560, SrcH = 1440, CropX = 8, CropY = 31, CropW = 2544, CropH = 1401,
                EncW = 1920, EncH = 1080, Hdr = true, Checker = true, MaxAllowed = 255,
                GeometryCheck = false,
                Expectation = "worst case: blown-highlight checker at a non-integer ratio",
            },
            new Case
            {
                Name = "HDR production 1440p->1080p",
                SrcW = 2560, SrcH = 1440, CropX = 8, CropY = 31, CropW = 2544, CropH = 1401,
                EncW = 1920, EncH = 1080, Hdr = true, MaxAllowed = 8,
                Expectation = "realistic ramp: order difference must stay small",
            },
        };

        private static int Main()
        {
            try
            {
                using (var device = new D3D11.Device(
                    D3D.DriverType.Hardware,
                    D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport))
                using (var toneMapper = new GpuHdrToneMapper(device))
                using (var scaler = new FrameScaler(device))
                using (var composer = new FrameComposer(device))
                {
                    Console.WriteLine(
                        "{0,-30} {1,10} {2,10} {3,9} {4,9}  {5}",
                        "case", "maxDelta", "meanDelta", "refShift", "newShift", "expectation");
                    Console.WriteLine(new string('-', 110));

                    var failures = 0;
                    foreach (var c in Cases)
                    {
                        failures += RunCase(device, toneMapper, scaler, composer, c) ? 0 : 1;
                    }

                    Console.WriteLine();
                    Console.WriteLine(failures == 0
                        ? "PASS: the fold reproduces the shipped path within tolerance on every case."
                        : $"FAIL: {failures} case(s) outside tolerance.");
                    return failures == 0 ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("probe failed: " + ex);
                return 2;
            }
        }

        private static bool RunCase(
            D3D11.Device device,
            GpuHdrToneMapper toneMapper,
            FrameScaler scaler,
            FrameComposer composer,
            Case c)
        {
            using (var source = c.Hdr
                ? CreateHdrSource(device, c.SrcW, c.SrcH, c.Checker)
                : CreateSdrSource(device, c.SrcW, c.SrcH))
            using (var referenceOut = RunReference(device, toneMapper, scaler, source, c))
            using (var foldedOut = FrameComposer.CreateTarget(device, c.EncW, c.EncH))
            {
                composer.Compose(source, foldedOut, c.CropX, c.CropY, c.CropW, c.CropH, c.Hdr, RefWhite);

                var reference = ReadBack(device, referenceOut, c.EncW, c.EncH);
                var folded = ReadBack(device, foldedOut, c.EncW, c.EncH);

                double max = 0, total = 0;
                long samples = 0;
                for (var i = 0; i < reference.Length; i++)
                {
                    // Alpha is forced opaque by both paths; comparing it would only measure that.
                    if (i % 4 == 3)
                    {
                        continue;
                    }

                    var delta = Math.Abs(reference[i] - folded[i]);
                    if (delta > max)
                    {
                        max = delta;
                    }

                    total += delta;
                    samples++;
                }

                var mean = samples == 0 ? 0 : total / samples;

                // A geometry error moves the image; a filtering difference does not. The luminance
                // centroid separates the two, so an expected colour difference cannot mask a crop
                // that landed on the wrong pixels.
                var referenceShift = Centroid(reference, c.EncW, c.EncH);
                var foldedShift = Centroid(folded, c.EncW, c.EncH);
                var shift = Math.Sqrt(
                    Math.Pow(referenceShift.X - foldedShift.X, 2) +
                    Math.Pow(referenceShift.Y - foldedShift.Y, 2));

                // Half a destination pixel: below this the two agree on where the picture is.
                const double MaxShift = 0.5;
                var ok = max <= c.MaxAllowed && (!c.GeometryCheck || shift <= MaxShift);

                Console.WriteLine(
                    "{0,-30} {1,10} {2,10} {3,9} {4,9}  {5}{6}",
                    c.Name,
                    max.ToString("0.0", CultureInfo.InvariantCulture),
                    mean.ToString("0.000", CultureInfo.InvariantCulture),
                    referenceShift.X.ToString("0.00", CultureInfo.InvariantCulture),
                    foldedShift.X.ToString("0.00", CultureInfo.InvariantCulture),
                    c.Expectation,
                    ok ? string.Empty : $"   <== FAIL (shift={shift:0.000}px)");

                return ok;
            }
        }

        /// <summary>The shipped route, wired exactly as WgcVideoRecorder.PullLatestFrame wires it.</summary>
        private static D3D11.Texture2D RunReference(
            D3D11.Device device,
            GpuHdrToneMapper toneMapper,
            FrameScaler scaler,
            D3D11.Texture2D source,
            Case c)
        {
            var bgra = c.Hdr ? toneMapper.ToneMap(source, RefWhite) : source;

            var latest = CreateBgra(device, c.CropW, c.CropH);
            var region = new D3D11.ResourceRegion(
                c.CropX, c.CropY, 0, c.CropX + c.CropW, c.CropY + c.CropH, 1);
            device.ImmediateContext.CopySubresourceRegion(bgra, 0, region, latest, 0, 0, 0, 0);

            if (c.CropW == c.EncW && c.CropH == c.EncH)
            {
                return latest;
            }

            using (latest)
            {
                var scaled = CreateBgra(device, c.EncW, c.EncH);
                scaler.Scale(latest, scaled);
                return scaled;
            }
        }

        /// <summary>
        /// Every pixel states its own coordinates, so a crop that lands one pixel off produces a
        /// numerically different image rather than a similar-looking one.
        /// </summary>
        private static D3D11.Texture2D CreateSdrSource(D3D11.Device device, int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    pixels[i + 0] = (byte)(x & 0xFF);                          // B
                    pixels[i + 1] = (byte)(y & 0xFF);                          // G
                    pixels[i + 2] = (byte)(((x >> 8) << 4) | (y >> 8));        // R
                    pixels[i + 3] = 255;
                }
            }

            return CreateTexture(device, width, height, Format.B8G8R8A8_UNorm, pixels, width * 4);
        }

        /// <summary>
        /// scRGB float16, with a coordinate ramp plus deliberate highlights above the SDR white
        /// level so the tone-map shoulder is exercised rather than the linear part alone.
        /// </summary>
        private static D3D11.Texture2D CreateHdrSource(
            D3D11.Device device, int width, int height, bool checker)
        {
            var pixels = new byte[width * height * 8];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 8;
                    var r = x / (float)Math.Max(1, width - 1);
                    var g = y / (float)Math.Max(1, height - 1);
                    // A hard checker of blown highlights: the shoulder is the non-linear part, and a
                    // checker guarantees neighbouring texels straddle it so averaging order matters.
                    var b = checker
                        ? (((x / 4) + (y / 4)) % 2 == 0 ? 4.0f : 0.02f)
                        : 0.02f + 3.98f * ((x + y) / (float)Math.Max(1, width + height - 2));
                    WriteHalf(pixels, i + 0, r);
                    WriteHalf(pixels, i + 2, g);
                    WriteHalf(pixels, i + 4, b);
                    WriteHalf(pixels, i + 6, 1f);
                }
            }

            return CreateTexture(device, width, height, Format.R16G16B16A16_Float, pixels, width * 8);
        }

        private static D3D11.Texture2D CreateTexture(
            D3D11.Device device, int width, int height, Format format, byte[] data, int stride)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return new D3D11.Texture2D(
                    device,
                    new D3D11.Texture2DDescription
                    {
                        Width = width,
                        Height = height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = D3D11.ResourceUsage.Default,
                        BindFlags = D3D11.BindFlags.ShaderResource,
                        CpuAccessFlags = D3D11.CpuAccessFlags.None,
                        OptionFlags = D3D11.ResourceOptionFlags.None,
                    },
                    new[] { new DataBox(handle.AddrOfPinnedObject(), stride, 0) });
            }
            finally
            {
                handle.Free();
            }
        }

        private static D3D11.Texture2D CreateBgra(D3D11.Device device, int width, int height)
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

        private static byte[] ReadBack(D3D11.Device device, D3D11.Texture2D texture, int width, int height)
        {
            using (var staging = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Staging,
                BindFlags = D3D11.BindFlags.None,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read,
                OptionFlags = D3D11.ResourceOptionFlags.None,
            }))
            {
                device.ImmediateContext.CopyResource(texture, staging);
                var box = device.ImmediateContext.MapSubresource(
                    staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                try
                {
                    var result = new byte[width * height * 4];
                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(
                            IntPtr.Add(box.DataPointer, y * box.RowPitch),
                            result,
                            y * width * 4,
                            width * 4);
                    }

                    return result;
                }
                finally
                {
                    device.ImmediateContext.UnmapSubresource(staging, 0);
                }
            }
        }

        /// <summary>Luminance centroid in pixels: moves when the image moves, not when it recolours.</summary>
        private static (double X, double Y) Centroid(byte[] bgra, int width, int height)
        {
            double sum = 0, sx = 0, sy = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var luma = 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];
                    sum += luma;
                    sx += luma * x;
                    sy += luma * y;
                }
            }

            return sum <= 0 ? (0, 0) : (sx / sum, sy / sum);
        }

        private static void WriteHalf(byte[] buffer, int offset, float value)
        {
            var half = FloatToHalf(value);
            buffer[offset] = (byte)(half & 0xFF);
            buffer[offset + 1] = (byte)(half >> 8);
        }

        /// <summary>IEEE754 binary32 to binary16, round-to-zero. Enough for a deterministic fixture.</summary>
        private static ushort FloatToHalf(float value)
        {
            var bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            var sign = (ushort)((bits >> 16) & 0x8000);
            var exponent = (int)((bits >> 23) & 0xFF) - 127 + 15;
            var mantissa = bits & 0x7FFFFF;

            if (exponent <= 0)
            {
                return sign;
            }

            if (exponent >= 31)
            {
                return (ushort)(sign | 0x7BFF);
            }

            return (ushort)(sign | (ushort)(exponent << 10) | (ushort)(mantissa >> 13));
        }
    }
}
