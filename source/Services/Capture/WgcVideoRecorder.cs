using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Recording;
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
    /// Occlusion-independent, HDR-correct unlock-clip capture: continuously captures a game window via
    /// WGC, tone-maps HDR frames on the GPU, paces to a constant frame rate, and encodes to rotating
    /// H.264 MP4 segments (Media Foundation, GPU-resident) in the buffer directory that the clip
    /// export and prune consume. Segments hold clean game footage only — each achievement's toast is
    /// composited into its clip at export from the recorded overlay track.
    ///
    /// A single pacing thread drives capture→tonemap→encode so there is no cross-thread GPU sharing:
    /// each tick pulls the latest WGC frame (re-using the last one for a static scene, matching a
    /// constant-fps screen capture), tone-maps it if HDR, and writes it to the current segment.
    /// </summary>
    internal sealed class WgcVideoRecorder : IDisposable
    {
        // Resolves the game window to capture, re-checked each second so the recorder follows the
        // learned game window (idle until it's known, re-target if it changes) instead of grabbing
        // whatever is foreground at start.
        private readonly Func<IntPtr> _resolveHwnd;
        private IntPtr _activeHwnd;
        // Client-area crop box within the captured window texture (excludes chrome); set per window.
        private int _cropX, _cropY, _cropW, _cropH;
        private readonly string _bufferDirectory;
        private readonly int _fps;
        private readonly int _segmentSeconds;
        private readonly RecordingResolution _resolution;
        private readonly RecordingQuality _quality;
        private readonly ILogger _logger;

        private D3D11.Device _device;
        private WinRtIDirect3DDevice _winrtDevice;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;
        private GpuHdrToneMapper _toneMapper;
        private bool _hdr;
        private float _refWhite = 1.0f;
        // The size the frame pool was built at, and the pixel format it was built with. WGC does not
        // resize the pool when the window does: it keeps handing back textures of this size holding
        // the top-left corner of the larger content, so a window that grows after capture starts is
        // recorded cropped (a "zoomed in" clip) until the pool is rebuilt. Compared against each
        // frame's ContentSize to detect that.
        private Windows.Graphics.SizeInt32 _poolSize;
        private bool _geometryStale;
        // Last window handle whose capture-item creation failed, so a window that is not capturable
        // yet is logged once instead of once per second for as long as it stays that way.
        private IntPtr _lastItemFailureHwnd;

        private D3D11.Texture2D _latest; // owned, BGRA, the most recent (tone-mapped) clean game frame
        private D3D11.Texture2D _scaled; // owned, BGRA, downscaled encode frame when a resolution cap applies
        private FrameScaler _frameScaler;
        private int _encW, _encH; // encoder (output) dimensions, after any resolution cap
        private MediaFoundationH264Encoder _encoder;
        private long _segmentFrameIndex;
        private long _lastSegmentPts100ns; // last PTS written in the current segment (strictly increasing)
        private int _segmentCount;
        private DateTime _segmentStartUtc;
        private readonly long _frameDuration100ns;

        private Thread _pumpThread;
        private volatile bool _running;
        private bool _disposed;

        public WgcVideoRecorder(
            Func<IntPtr> resolveHwnd, string bufferDirectory, int fps, int segmentSeconds,
            RecordingResolution resolution, RecordingQuality quality, ILogger logger)
        {
            _resolveHwnd = resolveHwnd;
            _bufferDirectory = bufferDirectory;
            _fps = Math.Max(1, fps);
            _segmentSeconds = Math.Max(1, segmentSeconds);
            _resolution = resolution;
            _quality = quality;
            _logger = logger;
            _frameDuration100ns = 10_000_000L / _fps;
        }

        // The encoded-frame size after the resolution cap: caps the height to 1080/720 (aspect
        // preserving, even dimensions as H.264 requires), never upscales; Native keeps the captured
        // client size. Shared with the screenshot pipeline, which reads the same options.
        private void ComputeEncodeSize(int clientW, int clientH, out int width, out int height)
        {
            var size = ResolutionCapMath.Apply(
                clientW, clientH, ResolutionCapMath.CapHeightFor(_resolution), evenDimensions: true);
            width = size.Width;
            height = size.Height;
        }

        private int ComputeBitrate(int width, int height)
        {
            return MediaFoundationH264Encoder.ComputeBitrate(width, height, _fps, _quality);
        }

        public static bool IsSupported => GraphicsCaptureSession.IsSupported();

        /// <summary>
        /// Starts the recorder: creates the D3D11/MF device and the pump thread. The pump idles until
        /// the game window is resolvable (see the resolver), then captures it — so it never grabs a
        /// foreground window that isn't the game. Returns false if the device can't be created.
        /// </summary>
        public bool Start()
        {
            try
            {
                _device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware,
                    D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
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

                Directory.CreateDirectory(_bufferDirectory);
                _running = true;
                _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "PlayAch-WgcVideo" };
                _pumpThread.Start();
                _logger?.Info("[Recording] WGC-MF capture started (following the game window).");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Recording] WGC-MF capture failed to start.");
                Stop();
                return false;
            }
        }

        /// <summary>
        /// Points the capture at <paramref name="hwnd"/>: tears down the old WGC session/pool/item,
        /// re-detects HDR for the new window's monitor, and starts a fresh capture. Ends the current
        /// segment so the resolution change starts a new one (segments in a clip must match size).
        /// </summary>
        private void SetupCapture(IntPtr hwnd)
        {
            GraphicsCaptureItem item;
            try
            {
                item = CreateItemForWindow(hwnd);
            }
            catch (Exception ex)
            {
                // A game window commonly exists before it is capturable (still initializing, cloaked,
                // no composition surface yet); the pump retries every tick, so log the first attempt
                // per handle with the geometry that identifies the window and stay quiet after that.
                if (hwnd != _lastItemFailureHwnd)
                {
                    _lastItemFailureHwnd = hwnd;
                    _logger?.Debug(
                        ex,
                        $"[Recording] WGC-MF could not create a capture item for game window 0x{hwnd.ToInt64():X} " +
                        $"({DescribeWindow(hwnd, 0, 0)}); retrying every second until it becomes capturable.");
                }

                return;
            }

            if (item == null || item.Size.Width <= 0 || item.Size.Height <= 0)
            {
                return;
            }

            _lastItemFailureHwnd = IntPtr.Zero;

            TearDownCapture();
            FinalizeSegment();

            _hdr = HdrDisplayDetector.IsHdrActive(hwnd);
            _refWhite = _hdr ? HdrDisplayDetector.GetSdrWhiteScRgb(hwnd) : 1.0f;
            if (_hdr && _toneMapper == null)
            {
                _toneMapper = new GpuHdrToneMapper(_device);
            }

            ComputeClientCrop(hwnd, item.Size.Width, item.Size.Height, out _cropX, out _cropY, out _cropW, out _cropH);

            var pixelFormat = _hdr
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            _item = item;
            _poolSize = item.Size;
            _geometryStale = false;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, pixelFormat, 2, item.Size);
            _session = _framePool.CreateCaptureSession(_item);
            WgcCaptureBorder.Suppress(_session);
            _session.StartCapture();
            _activeHwnd = hwnd;
            // The captured size, the crop derived from it and the monitor's scale together explain any
            // later "the clip is cropped/zoomed" report, which is otherwise indistinguishable from the
            // game simply rendering that way.
            _logger?.Info(
                $"[Recording] WGC-MF capturing game window 0x{hwnd.ToInt64():X} (hdr={_hdr}, " +
                $"{item.Size.Width}x{item.Size.Height}@{_fps}, crop={_cropW}x{_cropH}+{_cropX}+{_cropY}, " +
                $"{DescribeWindow(hwnd, item.Size.Width, item.Size.Height)}).");
        }

        /// <summary>
        /// A compact description of a window for capture diagnostics: the rects the crop is derived
        /// from, the monitor's true scale, and — the part that explains a mis-cropped clip — which
        /// rect the texture was matched to and at what scale. A texture-to-screen scale other than
        /// 1 means the window is DPI-unaware on a scaled display; "none" means no rect described
        /// the texture and the whole frame was kept. Never throws.
        /// </summary>
        private static string DescribeWindow(IntPtr hwnd, int capturedW, int capturedH)
        {
            try
            {
                var rects = WindowRectangles.Measure(hwnd);
                // Only meaningful once there is a texture to relate the window to; a window that
                // could not be captured at all has none, and reporting "no rect matched" there
                // would read as a mapping failure rather than an absent capture.
                var mapping = capturedW > 0 && capturedH > 0
                    ? CaptureCropMath.ResolveMapping(capturedW, capturedH, rects.FrameBounds, rects.OuterRect).ToString()
                    : "anchor=n/a";
                return $"{rects} monitorScale={UI.ToastWindowPlacer.ResolveMonitorScale(hwnd):0.##} {mapping} " +
                    $"visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)}";
            }
            catch
            {
                return "geometry=unavailable";
            }
        }

        private void TearDownCapture()
        {
            try { _session?.Dispose(); } catch { }
            try { _framePool?.Dispose(); } catch { }
            _session = null;
            _framePool = null;
            _item = null;
        }

        /// <summary>
        /// The client-area sub-region within the captured window texture (excludes chrome), in
        /// captured pixels. The window is measured by <see cref="WindowRectangles"/> and the region
        /// derived by <see cref="CaptureCropMath"/>, which owns the reasoning about how a texture
        /// relates to the window it was captured from — the same pair the still-capture path uses.
        /// </summary>
        private static void ComputeClientCrop(IntPtr hwnd, int capturedW, int capturedH, out int x, out int y, out int w, out int h)
        {
            var crop = CaptureCropMath.ClientCrop(
                capturedW, capturedH, WindowRectangles.Measure(hwnd), evenDimensions: true);
            x = crop.X;
            y = crop.Y;
            w = crop.Width;
            h = crop.Height;
        }

        private void PumpLoop()
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
            var next = DateTime.UtcNow;
            var lastResolveUtc = DateTime.MinValue;
            var lastRebuildUtc = DateTime.MinValue;
            try
            {
                while (_running)
                {
                    // Follow the game window: (re)target when the resolved handle changes. Normally
                    // throttled to once a second, but if the window we're capturing has been destroyed
                    // (common when a game recreates its window during a loading screen) re-resolve every
                    // tick so we latch onto the replacement the instant it exists — otherwise the last
                    // frame is held for up to a full second, lengthening the loading freeze. The held
                    // frame keeps the timeline real-time (better a brief freeze than a concat skip), so
                    // we never tear down while waiting; we just retarget as soon as the new window
                    // appears. Idle while the game window isn't known yet rather than capturing a
                    // foreground window that isn't the game.
                    var activeDead = _activeHwnd != IntPtr.Zero && !IsWindow(_activeHwnd);
                    if (activeDead || (DateTime.UtcNow - lastResolveUtc).TotalSeconds >= 1)
                    {
                        lastResolveUtc = DateTime.UtcNow;
                        var hwnd = _resolveHwnd?.Invoke() ?? IntPtr.Zero;
                        if (hwnd != IntPtr.Zero && hwnd != _activeHwnd)
                        {
                            SetupCapture(hwnd);
                        }
                    }

                    if (_activeHwnd != IntPtr.Zero && _framePool != null)
                    {
                        PullLatestFrame();

                        // The window changed size under a pool built for the old one. Rebuild the
                        // capture at the new size (which also re-measures the crop and re-detects
                        // HDR for its monitor) and drop the held frame, so the next tick starts a
                        // new segment at the new dimensions instead of encoding a stale crop. Held
                        // to once a second: a window being dragged-resized reports a new size every
                        // frame, and each rebuild costs a segment boundary.
                        if (_geometryStale && (DateTime.UtcNow - lastRebuildUtc).TotalSeconds >= 1)
                        {
                            lastRebuildUtc = DateTime.UtcNow;
                            _geometryStale = false;
                            _latest?.Dispose();
                            _latest = null;
                            SetupCapture(_activeHwnd);
                        }
                    }

                    if (_latest != null && _framePool != null)
                    {
                        // Create the first segment once we have a frame (its size), and roll over on
                        // schedule. The encoder can't be built before the first frame, so this — not
                        // a one-time call before the loop — is what starts encoding.
                        if (_encoder == null || (DateTime.UtcNow - _segmentStartUtc).TotalSeconds >= _segmentSeconds)
                        {
                            RotateSegment();
                        }

                        if (_encoder != null)
                        {
                            try
                            {
                                // Stamp frames by real elapsed time since the segment started, not by
                                // frame index. A pump stall (e.g. the game recreating its window during
                                // a loading screen tears down and rebuilds capture) then shows as the
                                // last frame held for its real duration, instead of the timeline
                                // collapsing that gap into a skip — which would also drift audio sync.
                                var pts = (DateTime.UtcNow - _segmentStartUtc).Ticks;
                                if (pts <= _lastSegmentPts100ns)
                                {
                                    pts = _lastSegmentPts100ns + 1;
                                }
                                _lastSegmentPts100ns = pts;
                                // Segments record the clean game frame only; the unlock toast is
                                // composited into each achievement's clip at export from its
                                // recorded overlay track.
                                _encoder.WriteFrame(ScaleForEncode(_latest), pts, _frameDuration100ns);
                                _segmentFrameIndex++;
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug(ex, "[Recording] WGC-MF frame encode failed.");
                            }
                        }
                    }

                    next += frameInterval;
                    var sleep = next - DateTime.UtcNow;
                    if (sleep > TimeSpan.Zero)
                    {
                        Thread.Sleep(sleep);
                    }
                    else
                    {
                        next = DateTime.UtcNow; // fell behind; resync rather than spin
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Recording] WGC-MF pump loop stopped on error.");
            }
            finally
            {
                FinalizeSegment();
                TearDownCapture();
            }
        }

        private void PullLatestFrame()
        {
            var frame = _framePool?.TryGetNextFrame();
            if (frame == null)
            {
                return; // static scene: keep the previous frame (constant-fps dup)
            }

            using (frame)
            {
                // WGC never resizes the frame pool on its own: once the window is bigger than the
                // pool it keeps handing back pool-sized textures holding only the content's top-left
                // corner, which records as a clip zoomed into that corner. Flag it and let the pump
                // rebuild the capture rather than encoding this frame against a stale crop.
                var content = frame.ContentSize;
                if (content.Width > 0 && content.Height > 0 &&
                    (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
                {
                    _logger?.Info(
                        $"[Recording] WGC-MF game window resized ({_poolSize.Width}x{_poolSize.Height} -> " +
                        $"{content.Width}x{content.Height}); rebuilding the capture.");
                    _geometryStale = true;
                    return;
                }

                var access = (IDirect3DDxgiInterfaceAccess)(object)frame.Surface;
                var texIid = IID_ID3D11Texture2D;
                var texPtr = access.GetInterface(ref texIid);
                using (var frameTexture = new D3D11.Texture2D(texPtr))
                {
                    var bgra = _hdr ? _toneMapper.ToneMap(frameTexture, _refWhite) : frameTexture;

                    // Crop to the client area (exclude window chrome) with a GPU sub-region copy.
                    var w = _cropW > 0 ? _cropW : bgra.Description.Width;
                    var h = _cropH > 0 ? _cropH : bgra.Description.Height;
                    EnsureLatest(w, h);
                    var region = new D3D11.ResourceRegion(_cropX, _cropY, 0, _cropX + w, _cropY + h, 1);
                    _device.ImmediateContext.CopySubresourceRegion(bgra, 0, region, _latest, 0, 0, 0, 0);
                }
            }
        }

        /// <summary>
        /// Returns <paramref name="src"/> unchanged when it already matches the encoder size, else a
        /// GPU downscale of it to the resolution-capped encoder dimensions.
        /// </summary>
        private D3D11.Texture2D ScaleForEncode(D3D11.Texture2D src)
        {
            if (src == null || (src.Description.Width == _encW && src.Description.Height == _encH))
            {
                return src;
            }

            EnsureScaled(_encW, _encH);
            if (_frameScaler == null)
            {
                _frameScaler = new FrameScaler(_device);
            }

            _frameScaler.Scale(src, _scaled);
            return _scaled;
        }

        private void EnsureScaled(int width, int height)
        {
            if (_scaled != null && _scaled.Description.Width == width && _scaled.Description.Height == height)
            {
                return;
            }

            _scaled?.Dispose();
            _scaled = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Default,
                BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
                CpuAccessFlags = D3D11.CpuAccessFlags.None,
                OptionFlags = D3D11.ResourceOptionFlags.None,
            });
        }


        private void EnsureLatest(int width, int height)
        {
            if (_latest != null && _latest.Description.Width == width && _latest.Description.Height == height)
            {
                return;
            }

            _latest?.Dispose();
            _latest = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Default,
                BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
                CpuAccessFlags = D3D11.CpuAccessFlags.None,
                OptionFlags = D3D11.ResourceOptionFlags.None,
            });
        }

        private void RotateSegment()
        {
            FinalizeSegment();
            if (_latest == null)
            {
                // No frame yet; defer segment creation until we know the size.
                return;
            }

            // Encode at the resolution-capped size; frames are downscaled from the captured client
            // size in ComposeFrame when a cap applies.
            ComputeEncodeSize(_latest.Description.Width, _latest.Description.Height, out _encW, out _encH);

            // The dimensions ride in the name so the clip planner can group segments by size
            // without opening them — a capture rebuilt at a new size starts a run the planner
            // will not concatenate with the old one.
            var name = RecordingPaths.BuildSegmentFileName(DateTime.Now, _encW, _encH);
            var path = EnsureUniqueSegment(Path.Combine(_bufferDirectory, name));
            _encoder = new MediaFoundationH264Encoder(
                _device, path, _encW, _encH, _fps, ComputeBitrate(_encW, _encH));
            _segmentFrameIndex = 0;
            _lastSegmentPts100ns = 0;
            _segmentStartUtc = DateTime.UtcNow;
            _segmentCount++;
            if (_segmentCount <= 2 || _segmentCount % 12 == 0)
            {
                _logger?.Debug($"[Recording] WGC-MF segment #{_segmentCount} started ({Path.GetFileName(path)}, {_encW}x{_encH}).");
            }
        }

        private void FinalizeSegment()
        {
            var encoder = _encoder;
            _encoder = null;
            encoder?.Dispose();
        }

        private static string EnsureUniqueSegment(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(dir, $"{stem}-{i}{ext}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return path;
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

        public void Stop()
        {
            _running = false;
            try
            {
                _pumpThread?.Join(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // ignore
            }

            _pumpThread = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            FinalizeSegment();
            _latest?.Dispose();
            _scaled?.Dispose();
            _frameScaler?.Dispose();
            _toneMapper?.Dispose();
            _session?.Dispose();
            _framePool?.Dispose();
            _device?.ImmediateContext?.Dispose();
            _device?.Dispose();
            _winrtDevice = null;
        }
    }
}
