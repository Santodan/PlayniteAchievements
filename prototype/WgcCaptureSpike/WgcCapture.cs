using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static WgcCaptureSpike.NativeInterop;
using WinRtIDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace WgcCaptureSpike
{
    /// <summary>
    /// One-shot per-window capture via Windows.Graphics.Capture. Captures the composited surface of
    /// a specific HWND (occlusion-independent), reads the first frame back off the GPU, tone-maps it
    /// if HDR, and returns a <see cref="Bitmap"/>. This is the feasibility spike — the production
    /// version would keep the device/pool alive and expose a frozen WPF bitmap instead.
    /// </summary>
    internal sealed class WgcCapture
    {
        public sealed class Result
        {
            public Bitmap Bitmap;
            public DirectXPixelFormat PixelFormat;
            public int Width;
            public int Height;
            public bool Hdr;
            public bool BorderDisabled;
            public float MaxLinearChannel; // >1.0 confirms real HDR content on the float surface
            public long ElapsedMs;
        }

        /// <summary>
        /// Captures <paramref name="hwnd"/> once. <paramref name="hdr"/> selects the float frame
        /// pool + tonemap; pass the result of <see cref="HdrDisplayDetector.IsHdrActive(IntPtr)"/>.
        /// <paramref name="log"/> receives step-by-step diagnostics for the go/no-go gates.
        /// </summary>
        public Result Capture(IntPtr hwnd, bool hdr, float manualWhite, Action<string> log)
        {
            if (hwnd == IntPtr.Zero)
            {
                throw new ArgumentException("hwnd is null");
            }

            var sw = Stopwatch.StartNew();
            var result = new Result { Hdr = hdr };

            // 1. D3D11 device (BgraSupport is required for WGC/D2D interop).
            log("Creating D3D11 device (Vortice)...");
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null,
                out ID3D11Device device,
                out ID3D11DeviceContext context).CheckError();

            // 2. Wrap the DXGI device as a WinRT IDirect3DDevice for the frame pool.
            log("Wrapping DXGI device as WinRT IDirect3DDevice...");
            WinRtIDirect3DDevice winrtDevice;
            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            {
                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable).CheckWin32("CreateDirect3D11DeviceFromDXGIDevice");
                try
                {
                    winrtDevice = (WinRtIDirect3DDevice)Marshal.GetObjectForIUnknown(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }

            // 3. HWND -> GraphicsCaptureItem via the activation-factory interop (GATE 1 + 2 + 3).
            log("Resolving GraphicsCaptureItem from HWND via IGraphicsCaptureItemInterop...");
            var item = CreateItemForWindow(hwnd);
            log($"  item.DisplayName='{item.DisplayName}', size={item.Size.Width}x{item.Size.Height}");
            result.Width = item.Size.Width;
            result.Height = item.Size.Height;

            var pixelFormat = hdr
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            result.PixelFormat = pixelFormat;
            log($"Frame pool format: {pixelFormat} (hdr={hdr})");

            // 4. Free-threaded frame pool + session. Free-threaded avoids a DispatcherQueue.
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, pixelFormat, 2, item.Size);
            var session = framePool.CreateCaptureSession(item);

            // 5. Try to suppress the on-screen capture border (GATE 6). Best-effort: older builds
            //    lack the property / the borderless consent, in which case the border just shows
            //    (it is never in the captured pixels).
            result.BorderDisabled = TryDisableBorder(session, log);

            using (var frameArrived = new ManualResetEventSlim(false))
            {
                Direct3D11CaptureFrame captured = null;
                TypedEventHandler<Direct3D11CaptureFramePool, object> handler = (pool, _) =>
                {
                    var f = pool.TryGetNextFrame();
                    if (f != null && captured == null)
                    {
                        captured = f;
                        frameArrived.Set();
                    }
                    else
                    {
                        f?.Dispose();
                    }
                };
                framePool.FrameArrived += handler;

                log("StartCapture; waiting for first frame...");
                session.StartCapture();

                // A static scene can be slow to deliver; also poll as a backstop.
                if (!frameArrived.Wait(TimeSpan.FromSeconds(2)))
                {
                    captured = captured ?? framePool.TryGetNextFrame();
                }

                framePool.FrameArrived -= handler;

                if (captured == null)
                {
                    throw new TimeoutException("No frame delivered within 2s (window may be minimized or not rendering).");
                }

                using (captured)
                {
                    log("Reading frame back off the GPU...");
                    result.Bitmap = ReadBack(device, context, captured, hdr, manualWhite, out var maxChannel);
                    result.MaxLinearChannel = maxChannel;
                }
            }

            session.Dispose();
            framePool.Dispose();
            context.Dispose();
            device.Dispose();

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        {
            var factory = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal
                .GetActivationFactory(typeof(GraphicsCaptureItem));
            var interop = (IGraphicsCaptureItemInterop)factory;
            var iid = IID_IGraphicsCaptureItem;
            var itemPtr = interop.CreateForWindow(hwnd, ref iid);
            try
            {
                return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }

        private static bool TryDisableBorder(GraphicsCaptureSession session, Action<string> log)
        {
            // IsBorderRequired (build 20348+) and GraphicsCaptureAccess (Win11) are newer than the
            // 10.0.19041 WinRT contracts this spike pins, so touch them by reflection: the spike
            // still compiles on the common contracts, and disables the border when the runtime has
            // it. The border is never in the captured pixels regardless, so failure is cosmetic.
            try
            {
                var prop = session.GetType().GetProperty("IsBorderRequired");
                if (prop == null || !prop.CanWrite)
                {
                    log("  IsBorderRequired not present on this build; border will show (not in captured pixels).");
                    return false;
                }

                prop.SetValue(session, false);
                log("  IsBorderRequired=false set.");
                return true;
            }
            catch (Exception ex)
            {
                log($"  Could not disable border (likely needs borderless consent / newer build): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Copies the frame texture to a CPU-readable staging texture, maps it, and builds a 32-bpp
        /// SDR Bitmap. For the HDR float surface, tone-maps scRGB (linear, BT.709) to sRGB.
        /// </summary>
        private static Bitmap ReadBack(
            ID3D11Device device,
            ID3D11DeviceContext context,
            Direct3D11CaptureFrame frame,
            bool hdr,
            float manualWhite,
            out float maxChannel)
        {
            maxChannel = 0f;

            var access = (IDirect3DDxgiInterfaceAccess)(object)frame.Surface;
            var texIid = IID_ID3D11Texture2D;
            var texPtr = access.GetInterface(ref texIid);
            using (var frameTexture = new ID3D11Texture2D(texPtr))
            {
                var desc = frameTexture.Description;
                var width = desc.Width;
                var height = desc.Height;

                var stagingDesc = desc;
                stagingDesc.Usage = ResourceUsage.Staging;
                stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
                stagingDesc.BindFlags = BindFlags.None;
                stagingDesc.MiscFlags = ResourceOptionFlags.None;

                using (var staging = device.CreateTexture2D(stagingDesc))
                {
                    context.CopyResource(staging, frameTexture);
                    var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        return hdr
                            ? BuildBitmapFromHdr(map.DataPointer, (int)map.RowPitch, width, height, manualWhite, out maxChannel)
                            : BuildBitmapFromBgra(map.DataPointer, (int)map.RowPitch, width, height);
                    }
                    finally
                    {
                        context.Unmap(staging, 0);
                    }
                }
            }
        }

        private static Bitmap BuildBitmapFromBgra(IntPtr src, int rowPitch, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(src + y * rowPitch, row, 0, row.Length);
                    for (var x = 3; x < row.Length; x += 4)
                    {
                        row[x] = 255; // force opaque; WGC alpha is not meaningful for a screenshot
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        /// <summary>
        /// scRGB (R16G16B16A16_Float, linear, BT.709 primaries, 1.0 = 80 nits) to sRGB 8-bit.
        /// Per-channel extended Reinhard with an auto (or manual) white point, then the sRGB OETF.
        /// This is a starting operator; tone quality needs tuning on real HDR hardware.
        /// </summary>
        private static Bitmap BuildBitmapFromHdr(
            IntPtr src, int rowPitch, int width, int height, float manualWhite, out float maxChannel)
        {
            // First pass: find the peak channel so we can auto-set the Reinhard white point and
            // report the HDR range (GATE 5: values > 1.0 confirm HDR content).
            var peak = 0f;
            for (var y = 0; y < height; y++)
            {
                var rowPtr = src + y * rowPitch;
                for (var x = 0; x < width; x++)
                {
                    var px = rowPtr + x * 8;
                    for (var c = 0; c < 3; c++)
                    {
                        var v = HalfToFloat((ushort)Marshal.ReadInt16(px + c * 2));
                        if (v > peak)
                        {
                            peak = v;
                        }
                    }
                }
            }

            maxChannel = peak;
            var white = manualWhite > 0f ? manualWhite : Math.Max(1f, peak);

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var outRow = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    var rowPtr = src + y * rowPitch;
                    for (var x = 0; x < width; x++)
                    {
                        var px = rowPtr + x * 8;
                        var r = HalfToFloat((ushort)Marshal.ReadInt16(px));
                        var g = HalfToFloat((ushort)Marshal.ReadInt16(px + 2));
                        var b = HalfToFloat((ushort)Marshal.ReadInt16(px + 4));

                        var o = x * 4;
                        outRow[o + 0] = ToSrgbByte(Tonemap(b, white)); // B
                        outRow[o + 1] = ToSrgbByte(Tonemap(g, white)); // G
                        outRow[o + 2] = ToSrgbByte(Tonemap(r, white)); // R
                        outRow[o + 3] = 255;                            // A
                    }

                    Marshal.Copy(outRow, 0, data.Scan0 + y * data.Stride, outRow.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        private static float Tonemap(float x, float white)
        {
            if (x <= 0f)
            {
                return 0f; // scRGB can carry small negatives (out-of-gamut); clamp
            }

            // Extended Reinhard: preserves highlights up to 'white' instead of hard-clipping at 1.0.
            return x * (1f + x / (white * white)) / (1f + x);
        }

        private static byte ToSrgbByte(float c)
        {
            if (c <= 0f)
            {
                return 0;
            }

            if (c >= 1f)
            {
                return 255;
            }

            var s = c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
            var v = (int)(s * 255f + 0.5f);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        /// <summary>IEEE half (float16) to float32.</summary>
        private static float HalfToFloat(ushort h)
        {
            var sign = (h >> 15) & 0x1;
            var exp = (h >> 10) & 0x1F;
            var mant = h & 0x3FF;

            int f;
            if (exp == 0)
            {
                if (mant == 0)
                {
                    f = sign << 31; // +/- 0
                }
                else
                {
                    // Subnormal: normalize.
                    exp = 127 - 15 + 1;
                    while ((mant & 0x400) == 0)
                    {
                        mant <<= 1;
                        exp--;
                    }
                    mant &= 0x3FF;
                    f = (sign << 31) | (exp << 23) | (mant << 13);
                }
            }
            else if (exp == 0x1F)
            {
                f = (sign << 31) | (0xFF << 23) | (mant << 13); // Inf / NaN
            }
            else
            {
                f = (sign << 31) | ((exp - 15 + 127) << 23) | (mant << 13);
            }

            var bytes = BitConverter.GetBytes(f);
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}
