using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using PlayniteAchievements.Common;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using static PlayniteAchievements.Services.Capture.NativeInterop;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;
using WinRtIDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Per-window screen capture via Windows.Graphics.Capture (WGC). Captures the composited surface
    /// of a specific window — independent of focus and occlusion, as long as the window is rendering
    /// and not minimized — and tone-maps HDR (scRGB) desktops down to correct SDR. Lives in a
    /// dedicated net462 assembly so the WinRT + D3D11 references stay out of the plugin's build.
    ///
    /// The D3D11 device is created once and reused across captures. A capture session is created and
    /// torn down per grab; a future video path would keep the session alive and pump
    /// <see cref="Windows.Graphics.Capture.Direct3D11CaptureFramePool.FrameArrived"/> continuously —
    /// the device/HDR/tone-map plumbing here is shared with that path.
    /// </summary>
    public sealed class WgcWindowCapture : IDisposable
    {
        private readonly object _deviceGate = new object();
        private D3D11.Device _device;
        private D3D11.DeviceContext _context;
        private WinRtIDirect3DDevice _winrtDevice;
        private bool _disposed;

        /// <summary>True on machines new enough to have WGC window capture (Windows 10 1903+).</summary>
        public static bool IsSupported => GraphicsCaptureSession.IsSupported();

        /// <summary>
        /// Captures <paramref name="hwnd"/> once, auto-selecting the HDR float path when the window's
        /// monitor has HDR enabled. Returns null (never throws) when the window can't be captured —
        /// minimized, not rendering, or a transient device failure — so callers degrade gracefully.
        /// The returned <see cref="Bitmap"/> is a detached 32-bpp copy the caller owns.
        /// </summary>
        public CaptureResult CaptureWindow(IntPtr hwnd, int warmupMs = 150)
        {
            if (_disposed || hwnd == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                EnsureDevice();
                var hdr = HdrDisplayDetector.IsHdrActive(hwnd);
                var refWhite = hdr ? HdrDisplayDetector.GetSdrWhiteScRgb(hwnd) : 1.0f;
                var result = CaptureFromItem(CreateItemForWindow(hwnd), hdr, refWhite, warmupMs);
                if (result?.Bitmap == null)
                {
                    return result;
                }

                // WGC captures the whole window; crop to the client area so window chrome (title bar,
                // borders) is excluded — matching the old GDI client-rect capture. Borderless/
                // fullscreen games (client == window) are returned unchanged.
                var cropped = CropToClient(result.Bitmap, hwnd);
                return ReferenceEquals(cropped, result.Bitmap)
                    ? result
                    : new CaptureResult(cropped, result.Hdr, cropped.Width, cropped.Height);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Crops a full-window capture down to the client area, using the same measurement and
        /// geometry the video recorder crops its frames with — see <see cref="CaptureCropMath"/>
        /// for how the captured texture is related back to the window. Returns the input unchanged
        /// when the client already fills the frame or nothing can be resolved.
        /// </summary>
        private static Bitmap CropToClient(Bitmap full, IntPtr hwnd)
        {
            try
            {
                var crop = CaptureCropMath.ClientCrop(
                    full.Width, full.Height, WindowRectangles.Measure(hwnd), evenDimensions: false);

                // Borderless / fullscreen: the client already fills the frame — nothing to crop.
                if (crop.IsEmpty || (crop.X == 0 && crop.Y == 0 && crop.Width == full.Width && crop.Height == full.Height))
                {
                    return full;
                }

                var cropped = full.Clone(crop, full.PixelFormat);
                full.Dispose();
                return cropped;
            }
            catch
            {
                return full;
            }
        }

        /// <summary>
        /// Captures the whole monitor that <paramref name="hwndOnMonitor"/> sits on (via WGC
        /// CreateForMonitor) — used only for the out-of-game test fire, where there is no game
        /// window and the toast is genuinely on the Playnite monitor. Same HDR tone-map path.
        /// Returns null (never throws) on failure.
        /// </summary>
        public CaptureResult CaptureMonitorForWindow(IntPtr hwndOnMonitor, int warmupMs = 150)
        {
            if (_disposed)
            {
                return null;
            }

            try
            {
                var hMonitor = MonitorFromWindow(hwndOnMonitor, MONITOR_DEFAULTTONEAREST);
                if (hMonitor == IntPtr.Zero)
                {
                    return null;
                }

                EnsureDevice();
                var hdr = HdrDisplayDetector.IsHdrActive(hwndOnMonitor);
                var refWhite = hdr ? HdrDisplayDetector.GetSdrWhiteScRgb(hwndOnMonitor) : 1.0f;
                return CaptureFromItem(CreateItemForMonitor(hMonitor), hdr, refWhite, warmupMs);
            }
            catch
            {
                return null;
            }
        }

        private CaptureResult CaptureFromItem(GraphicsCaptureItem item, bool hdr, float refWhite, int warmupMs)
        {
            if (item == null || item.Size.Width <= 0 || item.Size.Height <= 0)
            {
                return null;
            }

            var pixelFormat = hdr
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;

            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, pixelFormat, 2, item.Size);
            var session = framePool.CreateCaptureSession(item);
            WgcCaptureBorder.Suppress(session);

            var gate = new object();
            Direct3D11CaptureFrame latest = null;
            TypedEventHandler<Direct3D11CaptureFramePool, object> handler = (pool, _) =>
            {
                var f = pool.TryGetNextFrame();
                if (f == null)
                {
                    return;
                }

                lock (gate)
                {
                    latest?.Dispose();
                    latest = f;
                }
            };

            try
            {
                framePool.FrameArrived += handler;
                session.StartCapture();

                // Keep the latest frame over a short warmup rather than the first: a static/occluded
                // window's first FrameArrived can be an uninitialized (black) frame before DWM
                // supplies the real composited surface.
                Thread.Sleep(Math.Max(0, warmupMs));
                framePool.FrameArrived -= handler;

                Direct3D11CaptureFrame captured;
                lock (gate)
                {
                    captured = latest;
                    latest = null;
                }

                captured = captured ?? framePool.TryGetNextFrame();
                if (captured == null)
                {
                    return null;
                }

                using (captured)
                {
                    var bitmap = ReadBack(captured, hdr, refWhite);
                    return bitmap == null
                        ? null
                        : new CaptureResult(bitmap, hdr, bitmap.Width, bitmap.Height);
                }
            }
            finally
            {
                framePool.FrameArrived -= handler;
                lock (gate)
                {
                    latest?.Dispose();
                }

                session.Dispose();
                framePool.Dispose();
            }
        }

        private void EnsureDevice()
        {
            lock (_deviceGate)
            {
                if (_device != null)
                {
                    return;
                }

                // BgraSupport is required for WGC/D2D interop.
                _device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport);
                _context = _device.ImmediateContext;
                using (var dxgiDevice = _device.QueryInterface<DXGI.Device>())
                {
                    CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable)
                        .CheckWin32("CreateDirect3D11DeviceFromDXGIDevice");
                    try
                    {
                        _winrtDevice = (WinRtIDirect3DDevice)Marshal.GetObjectForIUnknown(inspectable);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
            }
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

        private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
        {
            var factory = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal
                .GetActivationFactory(typeof(GraphicsCaptureItem));
            var interop = (IGraphicsCaptureItemInterop)factory;
            var iid = IID_IGraphicsCaptureItem;
            var itemPtr = interop.CreateForMonitor(hMonitor, ref iid);
            try
            {
                return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }

        /// <summary>
        /// Copies the frame texture to a CPU-readable staging texture, maps it, and builds a detached
        /// 32-bpp Bitmap. SDR frames copy straight through; HDR (scRGB float) frames are tone-mapped
        /// on the CPU here — fine for a single screenshot; a video path would move this to a GPU
        /// shader.
        /// </summary>
        private Bitmap ReadBack(Direct3D11CaptureFrame frame, bool hdr, float refWhite)
        {
            var access = (IDirect3DDxgiInterfaceAccess)(object)frame.Surface;
            var texIid = IID_ID3D11Texture2D;
            var texPtr = access.GetInterface(ref texIid);
            using (var frameTexture = new D3D11.Texture2D(texPtr))
            {
                var desc = frameTexture.Description;
                var stagingDesc = desc;
                stagingDesc.Usage = D3D11.ResourceUsage.Staging;
                stagingDesc.CpuAccessFlags = D3D11.CpuAccessFlags.Read;
                stagingDesc.BindFlags = D3D11.BindFlags.None;
                stagingDesc.OptionFlags = D3D11.ResourceOptionFlags.None;

                using (var staging = new D3D11.Texture2D(_device, stagingDesc))
                {
                    _context.CopyResource(frameTexture, staging); // SharpDX: (source, destination)
                    var box = _context.MapSubresource(staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                    try
                    {
                        return hdr
                            ? HdrToneMap.BuildBitmap(box.DataPointer, box.RowPitch, desc.Width, desc.Height, refWhite)
                            : BuildBitmapFromBgra(box.DataPointer, box.RowPitch, desc.Width, desc.Height);
                    }
                    finally
                    {
                        _context.UnmapSubresource(staging, 0);
                    }
                }
            }
        }

        private static Bitmap BuildBitmapFromBgra(IntPtr src, int rowPitch, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
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

        public void Dispose()
        {
            lock (_deviceGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _context?.Dispose();
                _device?.Dispose();
                _context = null;
                _device = null;
                _winrtDevice = null;
            }
        }
    }

    /// <summary>A captured frame: a detached 32-bpp bitmap plus whether it came from an HDR path.</summary>
    public sealed class CaptureResult
    {
        public CaptureResult(Bitmap bitmap, bool hdr, int width, int height)
        {
            Bitmap = bitmap;
            Hdr = hdr;
            Width = width;
            Height = height;
        }

        public Bitmap Bitmap { get; }

        public bool Hdr { get; }

        public int Width { get; }

        public int Height { get; }
    }
}
