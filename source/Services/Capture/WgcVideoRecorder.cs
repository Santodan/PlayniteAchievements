using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Playnite.SDK;
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
    /// H.264 MP4 segments (Media Foundation, GPU-resident) in the buffer directory — the same rolling
    /// segments the ffmpeg export/prune already consume, so clip extraction and audio muxing are
    /// unchanged. Replaces the ffmpeg screen-capture process for the WGC-MF recording path.
    ///
    /// A single pacing thread drives capture→tonemap→encode so there is no cross-thread GPU sharing:
    /// each tick pulls the latest WGC frame (re-using the last one for a static scene, matching a
    /// constant-fps screen capture), tone-maps it if HDR, and writes it to the current segment.
    /// </summary>
    internal sealed class WgcVideoRecorder : IDisposable
    {
        /// <summary>Segment filename pattern (local wall-clock), mirroring the ffmpeg capture's.</summary>
        public const string SegmentFilePrefix = "seg_";

        public const string SegmentFileExtension = ".mp4";

        private const string SegmentStrftime = "yyyyMMdd-HHmmss";

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
        private readonly ILogger _logger;

        private D3D11.Device _device;
        private WinRtIDirect3DDevice _winrtDevice;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;
        private GpuHdrToneMapper _toneMapper;
        private OverlayBlitter _overlayBlitter;
        private bool _hdr;
        private float _refWhite = 1.0f;

        private D3D11.Texture2D _latest; // owned, BGRA, the most recent (tone-mapped) clean game frame
        private D3D11.Texture2D _composited; // owned, BGRA, scratch for game+toast when a toast is showing
        private MediaFoundationH264Encoder _encoder;
        private long _segmentFrameIndex;
        private long _lastSegmentPts100ns; // last PTS written in the current segment (strictly increasing)
        private int _segmentCount;
        private DateTime _segmentStartUtc;
        private readonly long _frameDuration100ns;

        private Thread _pumpThread;
        private volatile bool _running;
        private bool _disposed;

        public WgcVideoRecorder(Func<IntPtr> resolveHwnd, string bufferDirectory, int fps, int segmentSeconds, ILogger logger)
        {
            _resolveHwnd = resolveHwnd;
            _bufferDirectory = bufferDirectory;
            _fps = Math.Max(1, fps);
            _segmentSeconds = Math.Max(1, segmentSeconds);
            _logger = logger;
            _frameDuration100ns = 10_000_000L / _fps;
        }

        // Target H.264 bitrate from the actual capture resolution and fps (~0.12 bits/pixel/frame):
        // ~15 Mbps at 1080p60, ~27 at 1440p60, ~60 at 4K60. Clamped to a sane range.
        private int ComputeBitrate(int width, int height)
        {
            var bits = (long)(width * (double)height * _fps * 0.12);
            return (int)Math.Max(8_000_000L, Math.Min(120_000_000L, bits));
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
                _logger?.Debug(ex, "[Recording] WGC-MF could not create a capture item for the game window.");
                return;
            }

            if (item == null || item.Size.Width <= 0 || item.Size.Height <= 0)
            {
                return;
            }

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
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, pixelFormat, 2, item.Size);
            _session = _framePool.CreateCaptureSession(_item);
            TrySuppressBorder(_session);
            _session.StartCapture();
            _activeHwnd = hwnd;
            _logger?.Info($"[Recording] WGC-MF capturing game window 0x{hwnd.ToInt64():X} (hdr={_hdr}, {item.Size.Width}x{item.Size.Height}@{_fps}).");
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
        /// captured pixels — measured against the DWM extended frame bounds (what WGC captures) and
        /// scaled into the texture, with even dimensions for H.264. Falls back to the full frame.
        /// </summary>
        private static void ComputeClientCrop(IntPtr hwnd, int capturedW, int capturedH, out int x, out int y, out int w, out int h)
        {
            x = 0;
            y = 0;
            w = capturedW;
            h = capturedH;
            try
            {
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf(typeof(RECT))) != 0)
                {
                    return;
                }

                var fw = frame.Right - frame.Left;
                var fh = frame.Bottom - frame.Top;
                if (fw <= 0 || fh <= 0 || !GetClientRect(hwnd, out var client))
                {
                    return;
                }

                var cw = client.Right - client.Left;
                var ch = client.Bottom - client.Top;
                var origin = new POINT { X = 0, Y = 0 };
                if (cw <= 0 || ch <= 0 || !ClientToScreen(hwnd, ref origin))
                {
                    return;
                }

                var sx = (double)capturedW / fw;
                var sy = (double)capturedH / fh;
                var cx = Math.Max(0, Math.Min((int)Math.Round((origin.X - frame.Left) * sx), capturedW - 2));
                var cy = Math.Max(0, Math.Min((int)Math.Round((origin.Y - frame.Top) * sy), capturedH - 2));
                var cwp = Math.Max(2, Math.Min((int)Math.Round(cw * sx), capturedW - cx)) & ~1;
                var chp = Math.Max(2, Math.Min((int)Math.Round(ch * sy), capturedH - cy)) & ~1;

                x = cx;
                y = cy;
                w = cwp;
                h = chp;
            }
            catch
            {
                x = 0;
                y = 0;
                w = capturedW & ~1;
                h = capturedH & ~1;
            }
        }

        private void PumpLoop()
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
            var next = DateTime.UtcNow;
            var lastResolveUtc = DateTime.MinValue;
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
                                // Composite the toast fresh each encoded frame (including held dups
                                // during a capture stall) so it keeps animating even while the game
                                // frame is frozen, and off a clean base so successive toasts don't
                                // smear over each other.
                                _encoder.WriteFrame(ComposeFrame(), pts, _frameDuration100ns);
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
        /// The texture to encode for the current frame: the clean captured game frame when no toast is
        /// on screen, otherwise a scratch copy with the current toast composited on top. Compositing
        /// off a fresh copy of <see cref="_latest"/> (rather than into it) keeps the clean frame reusable
        /// for held dups and prevents successive toast states from smearing over each other.
        /// </summary>
        private D3D11.Texture2D ComposeFrame()
        {
            if (_latest == null)
            {
                return null;
            }

            if (!VideoOverlaySink.TryGet(
                    out var overlayBgra, out var ow, out var oh,
                    out var clientX, out var clientY, out var clientW, out var clientH, out _) ||
                overlayBgra == null || ow <= 0 || oh <= 0 || clientW <= 0 || clientH <= 0)
            {
                return _latest;
            }

            EnsureComposited(_latest.Description.Width, _latest.Description.Height);
            _device.ImmediateContext.CopyResource(_latest, _composited);

            // The overlay position is expressed in the game's client-pixel space; the target is the
            // (possibly differently-sized) captured client area. Scale into target pixels.
            var scaleX = _composited.Description.Width / clientW;
            var scaleY = _composited.Description.Height / clientH;
            var destX = (int)Math.Round(clientX * scaleX);
            var destY = (int)Math.Round(clientY * scaleY);
            var destW = (int)Math.Round(ow * scaleX);
            var destH = (int)Math.Round(oh * scaleY);

            if (_overlayBlitter == null)
            {
                _overlayBlitter = new OverlayBlitter(_device);
            }

            _overlayBlitter.Blit(_composited, overlayBgra, ow, oh, destX, destY, destW, destH);
            return _composited;
        }

        private void EnsureComposited(int width, int height)
        {
            if (_composited != null && _composited.Description.Width == width && _composited.Description.Height == height)
            {
                return;
            }

            _composited?.Dispose();
            _composited = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
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

            var name = SegmentFilePrefix + DateTime.Now.ToString(SegmentStrftime, CultureInfo.InvariantCulture) + SegmentFileExtension;
            var path = EnsureUniqueSegment(Path.Combine(_bufferDirectory, name));
            _encoder = new MediaFoundationH264Encoder(
                _device, path, _latest.Description.Width, _latest.Description.Height, _fps,
                ComputeBitrate(_latest.Description.Width, _latest.Description.Height));
            _segmentFrameIndex = 0;
            _lastSegmentPts100ns = 0;
            _segmentStartUtc = DateTime.UtcNow;
            _segmentCount++;
            if (_segmentCount <= 2 || _segmentCount % 12 == 0)
            {
                _logger?.Debug($"[Recording] WGC-MF segment #{_segmentCount} started ({Path.GetFileName(path)}, {_latest.Description.Width}x{_latest.Description.Height}).");
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

        private static void TrySuppressBorder(GraphicsCaptureSession session)
        {
            try
            {
                var prop = session.GetType().GetProperty("IsBorderRequired");
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(session, false);
                }
            }
            catch
            {
                // Older build; the border isn't in the captured pixels anyway.
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
            _composited?.Dispose();
            _overlayBlitter?.Dispose();
            _toneMapper?.Dispose();
            _session?.Dispose();
            _framePool?.Dispose();
            _device?.ImmediateContext?.Dispose();
            _device?.Dispose();
            _winrtDevice = null;
        }
    }
}
