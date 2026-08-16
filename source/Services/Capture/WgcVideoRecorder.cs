using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    /// A single pacing thread drives capture→tonemap→encode: each tick pulls the latest WGC frame
    /// (re-using the last one for a static scene, and repeating it as many times as a stall calls for, so
    /// frame count always matches elapsed time), tone-maps it if HDR, and writes it to the current
    /// segment. The one other thread that touches the capture device builds the next segment's writer
    /// ahead of a rotation, which is why the device is multithread-protected.
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
        private int _segmentCount;
        private DateTime _segmentStartUtc;

        // Synchronous MF writer timing is accumulated with Stopwatch timestamps (no per-frame
        // allocation) and emitted only with the recorder's already-sampled segment diagnostics.
        private long _encodeSamples;
        private long _encodeTotalTicks;
        private long _encodeMaxTicks;
        private long _encodeOverBudget;
        private bool _encoderDescriptionLogged;
        private bool _gpuPriorityLowered;
        private DateTime _lastDebtLogUtc = DateTime.MinValue;
        private int _suppressedDebtLogs;

        private Thread _pumpThread;
        private volatile bool _running;
        private bool _disposed;
        private IDisposable _mediaFoundationLease;
        private int _cleanupStarted;
        private int _resourcesReleased;

        // Segments are written out off the pump thread; see FinalizeSegment.
        private readonly object _finalizeGate = new object();
        private Task _finalizeChain = Task.CompletedTask;

        // How large a gap between one segment's grid ending and the next opening we will carry as
        // repeated frames. Rotation costs on the order of 100 ms; past this it is a stall, and covering
        // it would mean writing seconds of duplicates.
        private static readonly TimeSpan MaxRotationCarry = TimeSpan.FromSeconds(1);

        // How far before a boundary the next segment's writer starts being built. Building measured
        // ~105 ms; this leaves room for a slow build without holding two writers open for long.
        private static readonly TimeSpan PrepareLead = TimeSpan.FromMilliseconds(750);

        // The next segment's writer, built ahead of the boundary; see MaybePrepareNextSegment.
        private readonly object _prepareGate = new object();
        private PreparedSegment _prepared;
        private Task _prepareTask;


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
                // One process-wide lease for the whole session, around every segment encoder this
                // recorder builds. Exporters share the same lease count and therefore cannot shut
                // Media Foundation down while this recorder is still active.
                _mediaFoundationLease = MediaFoundationRuntime.Acquire();

                _device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware,
                    D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);

                // The next segment's writer is built on a background thread while the pump is encoding
                // into the current one, so two threads touch this device.
                using (var multithread = _device.QueryInterface<D3D11.Multithread>())
                {
                    multithread.SetMultithreadProtected(true);
                }
                using (var dxgiDevice = _device.QueryInterface<DXGI.Device>())
                {
                    try
                    {
                        // Capture is opportunistic background work. A mild relative reduction leaves
                        // DWM and the game ahead of our copy/scale/tonemap commands under contention;
                        // unlike idle priority, -1 still has a forward-progress guarantee.
                        dxgiDevice.GPUThreadPriority = -1;
                        _gpuPriorityLowered = true;
                    }
                    catch (Exception ex)
                    {
                        // Unsupported drivers retain the normal priority and keep recording.
                        _logger?.Debug(ex, "[Recording] Could not lower the capture GPU priority; using normal priority.");
                    }

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

            // A retarget must not seed the new game's segment with the previous game's held frame.
            // WGC may need a few ticks to deliver its first frame; until then the new session stays
            // empty instead of recording unrelated footage under the new timeline.
            _latest?.Dispose();
            _latest = null;
            _scaled?.Dispose();
            _scaled = null;

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
            var updateRateLimited = WgcCaptureBorder.LimitUpdateRate(_session, _fps);
            WgcCaptureBorder.Suppress(_session);
            _session.StartCapture();
            _activeHwnd = hwnd;
            // The captured size, the crop derived from it and the monitor's scale together explain any
            // later "the clip is cropped/zoomed" report, which is otherwise indistinguishable from the
            // game simply rendering that way.
            _logger?.Info(
                $"[Recording] WGC-MF capturing game window 0x{hwnd.ToInt64():X} (hdr={_hdr}, " +
                $"{item.Size.Width}x{item.Size.Height}@{_fps}, crop={_cropW}x{_cropH}+{_cropX}+{_cropY}, " +
                $"wgcRateLimited={updateRateLimited}, gpuPriority={(_gpuPriorityLowered ? "-1" : "normal")}, " +
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
            // Ticks, not TimeSpan.FromSeconds: that overload rounds to the nearest millisecond, so
            // 1.0/60 became 17 ms and pinned the pump to 58.8 fps — 2% under the rate the segments
            // then declared, which is most of the wall-clock-versus-media gap this loop used to open.
            var frameInterval = CaptureWorkloadPolicy.FrameInterval(_fps);
            var next = CaptureTimelineClock.UtcNow;
            var lastResolveUtc = DateTime.MinValue;
            var lastRebuildUtc = DateTime.MinValue;
            var pacer = new FramePacer();
            if (!pacer.IsHighResolution)
            {
                _logger?.Debug(
                    "[Recording] WGC-MF pump has no high-resolution timer; pacing falls back to Thread.Sleep.");
            }

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
                    if (activeDead || (CaptureTimelineClock.UtcNow - lastResolveUtc).TotalSeconds >= 1)
                    {
                        lastResolveUtc = CaptureTimelineClock.UtcNow;
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
                        if (_geometryStale && (CaptureTimelineClock.UtcNow - lastRebuildUtc).TotalSeconds >= 1)
                        {
                            lastRebuildUtc = CaptureTimelineClock.UtcNow;
                            _geometryStale = false;
                            _latest?.Dispose();
                            _latest = null;
                            SetupCapture(_activeHwnd);
                        }
                    }

                    if (_latest != null && _framePool != null)
                    {
                        var encodeNow = CaptureTimelineClock.UtcNow;
                        var dueBeforeRotation = _encoder == null
                            ? 0
                            : DueFrameCount(encodeNow);
                        // A writer can block when its queue is full. Do not answer a substantial
                        // block by immediately flooding it with every missed duplicate: close this
                        // partial segment and let the exporter's wall-clock gap handling hold its
                        // last frame until a fresh segment begins now. Never resync a writer that has
                        // not accepted its first frame; an unusually slow constructor would otherwise
                        // be replaced by another equally expensive constructor in a loop.
                        var resynchronize = _encoder != null && _segmentFrameIndex > 0 &&
                            CaptureWorkloadPolicy.ShouldResynchronize(
                                dueBeforeRotation, _segmentFrameIndex, _fps);

                        // Create the first segment once we have a frame (its size), and roll over on
                        // schedule. The encoder can't be built before the first frame, so this — not
                        // a one-time call before the loop — is what starts encoding.
                        if (_encoder == null ||
                            (encodeNow - _segmentStartUtc).TotalSeconds >= _segmentSeconds ||
                            resynchronize)
                        {
                            if (resynchronize)
                            {
                                LogDebtResynchronization(dueBeforeRotation - _segmentFrameIndex);
                            }

                            RotateSegment(resynchronize);
                        }

                        if (_encoder != null)
                        {
                            try
                            {
                                // Constant frame rate, by frame count rather than by timestamp. The H.264
                                // encoder rewrites per-sample durations onto the grid its declared frame
                                // rate implies — measured: uneven durations in, one stts entry out — so a
                                // real-time timestamp cannot survive it and the only way to keep the
                                // timeline honest is to make that grid true. Emit exactly as many frames
                                // as the elapsed time calls for, repeating the held frame to cover a
                                // stall, so slot i really is the picture that was on screen at i/fps.
                                // Segments record the clean game frame only; the unlock toast is
                                // composited into each achievement's clip at export from its
                                // recorded overlay track.
                                var due = DueFrameCount(CaptureTimelineClock.UtcNow);
                                var missing = due - _segmentFrameIndex;

                                // A segment can never hold more than its own length; anything beyond that
                                // belongs to the next one, which the rotation above will open.
                                var ceiling = (long)_segmentSeconds * _fps;
                                if (_segmentFrameIndex + missing > ceiling)
                                {
                                    missing = Math.Max(0, ceiling - _segmentFrameIndex);
                                }

                                if (missing > 0)
                                {
                                    // Scale once, not once per repeat: duplicates are the same picture.
                                    var encodeFrame = ScaleForEncode(_latest);
                                    for (var repeat = 0L; repeat < missing; repeat++)
                                    {
                                        var pts = PtsForFrame(_segmentFrameIndex);
                                        RecordEncodeLatency(_encoder.WriteFrame(
                                            encodeFrame, pts, PtsForFrame(_segmentFrameIndex + 1) - pts));
                                        _segmentFrameIndex++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug(ex, "[Recording] WGC-MF frame encode failed.");
                            }

                            // Build the next segment's writer before we need it, so the rotation above
                            // never stops capture.
                            MaybePrepareNextSegment();
                        }
                    }

                    next += frameInterval;
                    var now = CaptureTimelineClock.UtcNow;
                    var sleep = next - now;
                    if (sleep > TimeSpan.Zero)
                    {
                        pacer.Wait(sleep);
                    }
                    else
                    {
                        // Behind schedule: give up the deficit rather than running ticks back to back to
                        // make it up. Catching up would emit a burst of frames the capture has no new
                        // content for — duplicates of the held frame, each with a near-zero duration —
                        // and the moment the pump is most likely to fall behind is exactly the unlock,
                        // where the toast, the screenshot and the overlay track all compete with it. A
                        // frame held for its true length reads far better than a flurry of stills.
                        // The high-resolution timer is what keeps the rate honest; it measured 60.00 fps
                        // with no catch-up at all.
                        next = now;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Recording] WGC-MF pump loop stopped on error.");
            }
            finally
            {
                _running = false;
                pacer.Dispose();
                // A writer prepared for a segment that will never open still owns a file.
                DiscardPrepared(TakePreparedSegment(waitForCompletion: true));
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

        /// <summary>
        /// Where frame <paramref name="index"/> sits on a segment's nominal grid, in 100-ns units.
        /// Derived from the index rather than accumulated, so a rate that does not divide a second evenly
        /// (60 fps is 166666.67 ticks) cannot drift the segment's length as the frames add up.
        /// </summary>
        private long PtsForFrame(long index)
        {
            return index * TimeSpan.TicksPerSecond / _fps;
        }

        private long DueFrameCount(DateTime nowUtc)
        {
            return (long)((nowUtc - _segmentStartUtc).TotalSeconds * _fps) + 1;
        }

        private void RotateSegment(bool resynchronize = false)
        {
            var rotate = Stopwatch.StartNew();
            var prepared = TakePreparedSegment();
            FinalizeSegment();
            if (_latest == null)
            {
                // No frame yet; defer segment creation until we know the size.
                DiscardPrepared(prepared);
                return;
            }

            // Encode at the resolution-capped size; frames are downscaled from the captured client
            // size in ComposeFrame when a cap applies.
            ComputeEncodeSize(_latest.Description.Width, _latest.Description.Height, out _encW, out _encH);

            // One instant for both the name and the PTS origin. Clip planning maps the name onto a
            // position on the timeline and the frames inside are stamped relative to the origin, so
            // taking them from separate DateTime reads would label the segment a few milliseconds
            // off from the frames it holds — by however long the encoder took to build.
            //
            // That origin is where the previous segment's grid ended rather than "now": writing the old
            // segment out and building this one costs real time (measured ~100 ms), and an origin stamped
            // after the gap simply loses it. The export can bridge it between files, but the final
            // re-encode flattens the timeline again and the gap returns as drift. Continuing the grid
            // makes the pump's catch-up fill the gap with the held frame instead. A gap far larger than
            // rotation cost is a genuine stall, where resyncing beats emitting seconds of duplicates.
            var now = CaptureTimelineClock.UtcNow;
            if (_segmentCount > 0 && !resynchronize)
            {
                var previousGridEnd = _segmentStartUtc.AddTicks(PtsForFrame(_segmentFrameIndex));
                _segmentStartUtc = now - previousGridEnd < MaxRotationCarry && previousGridEnd <= now
                    ? previousGridEnd
                    : now;
            }
            else
            {
                _segmentStartUtc = now;
            }

            // Use the writer prepared during the previous segment when it is for exactly this segment;
            // building one costs ~105ms, which on this thread is frames of frozen picture.
            string path;
            var reused = false;
            if (prepared != null &&
                prepared.Width == _encW && prepared.Height == _encH && prepared.StartUtc == _segmentStartUtc)
            {
                _encoder = prepared.Encoder;
                path = prepared.Path;
                reused = true;
            }
            else
            {
                // Wrong size (a capture rebuild) or a start the prediction missed: throw it away and pay
                // the build here, as before.
                DiscardPrepared(prepared);

                // The dimensions ride in the name so the clip planner can group segments by size
                // without opening them — a capture rebuilt at a new size starts a run the planner
                // will not concatenate with the old one.
                var name = RecordingPaths.BuildSegmentFileName(_segmentStartUtc, _encW, _encH);
                path = EnsureUniqueSegment(Path.Combine(_bufferDirectory, name));
                _encoder = new MediaFoundationH264Encoder(
                    _device, path, _encW, _encH, _fps, ComputeBitrate(_encW, _encH));
            }

            LogEncoderDescriptionOnce(_encoder);

            _segmentFrameIndex = 0;
            _segmentCount++;
            if (_segmentCount <= 2 || _segmentCount % 12 == 0)
            {
                // The rotation cost is pump time no frame could be captured in, so it is worth seeing:
                // whatever is left of it lands as the previous frame held that long, once per segment.
                _logger?.Debug(
                    $"[Recording] WGC-MF segment #{_segmentCount} started ({Path.GetFileName(path)}, {_encW}x{_encH}, " +
                    $"rotate={rotate.ElapsedMilliseconds}ms, prepared={reused}{TakeEncodeLatencySummary()}).");
            }
        }

        private void RecordEncodeLatency(long elapsedTicks)
        {
            if (elapsedTicks < 0)
            {
                return;
            }

            _encodeSamples++;
            _encodeTotalTicks += elapsedTicks;
            if (elapsedTicks > _encodeMaxTicks)
            {
                _encodeMaxTicks = elapsedTicks;
            }

            var frameBudgetTicks = Stopwatch.Frequency / Math.Max(1, _fps);
            if (elapsedTicks > frameBudgetTicks)
            {
                _encodeOverBudget++;
            }
        }

        private string TakeEncodeLatencySummary()
        {
            if (_encodeSamples <= 0)
            {
                return string.Empty;
            }

            var averageMs = _encodeTotalTicks * 1000d / Stopwatch.Frequency / _encodeSamples;
            var maximumMs = _encodeMaxTicks * 1000d / Stopwatch.Frequency;
            var summary =
                $", encodeAvg={averageMs:0.00}ms, encodeMax={maximumMs:0.00}ms, " +
                $"overBudget={_encodeOverBudget}/{_encodeSamples}";
            _encodeSamples = 0;
            _encodeTotalTicks = 0;
            _encodeMaxTicks = 0;
            _encodeOverBudget = 0;
            return summary;
        }

        private void LogEncoderDescriptionOnce(MediaFoundationH264Encoder encoder)
        {
            if (_encoderDescriptionLogged || encoder == null)
            {
                return;
            }

            _encoderDescriptionLogged = true;
            _logger?.Debug($"[Recording] Media Foundation transform chain: {encoder.TransformDescription}.");
        }

        private void LogDebtResynchronization(long overdueFrames)
        {
            var now = CaptureTimelineClock.UtcNow;
            if ((now - _lastDebtLogUtc).TotalSeconds < 30)
            {
                _suppressedDebtLogs++;
                return;
            }

            var suppressed = _suppressedDebtLogs > 0
                ? $", suppressedSinceLast={_suppressedDebtLogs}"
                : string.Empty;
            _logger?.Debug(
                $"[Recording] Encoder fell {overdueFrames} frames behind; starting a fresh segment " +
                $"instead of burst-filling the debt{suppressed}.");
            _lastDebtLogUtc = now;
            _suppressedDebtLogs = 0;
        }

        /// <summary>
        /// Starts building the next segment's writer on a background thread, shortly before the boundary
        /// it is for, so the rotation itself costs nothing. The pump keeps capturing into the current
        /// segment while this runs. Only meaningful because the constant-rate pacing makes a full
        /// segment exactly <c>_segmentSeconds * _fps</c> frames, which is what lets the next segment's
        /// start instant — and therefore its file name — be known before it begins.
        /// </summary>
        private void MaybePrepareNextSegment()
        {
            if (_encoder == null || _prepareTask != null || _latest == null)
            {
                return;
            }

            lock (_prepareGate)
            {
                if (_prepared != null)
                {
                    return;
                }
            }

            var boundary = _segmentStartUtc.AddTicks(PtsForFrame((long)_segmentSeconds * _fps));
            if (CaptureTimelineClock.UtcNow < boundary - PrepareLead)
            {
                return;
            }

            var width = _encW;
            var height = _encH;
            var name = RecordingPaths.BuildSegmentFileName(boundary, width, height);
            var path = EnsureUniqueSegment(Path.Combine(_bufferDirectory, name));
            _prepareTask = Task.Run(() =>
            {
                try
                {
                    var encoder = new MediaFoundationH264Encoder(
                        _device, path, width, height, _fps, ComputeBitrate(width, height));
                    lock (_prepareGate)
                    {
                        _prepared = new PreparedSegment
                        {
                            Encoder = encoder,
                            Path = path,
                            StartUtc = boundary,
                            Width = width,
                            Height = height,
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[Recording] Preparing the next capture segment failed; it will be built inline.");
                    TryDeleteSegmentFile(path);
                }
            });
        }

        /// <summary>
        /// Hands over the prepared segment. A normal rotation waits briefly; shutdown waits for the
        /// task outright because its encoder and output file must not outlive the capture device.
        /// </summary>
        private PreparedSegment TakePreparedSegment(bool waitForCompletion = false)
        {
            var task = _prepareTask;
            if (task != null && !task.IsCompleted)
            {
                try
                {
                    if (waitForCompletion)
                    {
                        task.Wait();
                    }
                    else if (!task.Wait(TimeSpan.FromSeconds(2)))
                    {
                        // Keep the task reachable. Dropping it here allowed Dispose to release the
                        // D3D device while the background writer was still being constructed.
                        return null;
                    }
                }
                catch
                {
                    // A failed task has completed and owns no prepared writer.
                }
            }

            _prepareTask = null;
            lock (_prepareGate)
            {
                var prepared = _prepared;
                _prepared = null;
                return prepared;
            }
        }

        /// <summary>Throws away a prepared segment that turned out not to fit, file included.</summary>
        private void DiscardPrepared(PreparedSegment prepared)
        {
            if (prepared == null)
            {
                return;
            }

            try { prepared.Encoder.Dispose(); } catch { }
            TryDeleteSegmentFile(prepared.Path);
        }

        private void TryDeleteSegmentFile(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Could not remove an unused prepared segment file.");
            }
        }

        private sealed class PreparedSegment
        {
            public MediaFoundationH264Encoder Encoder;
            public string Path;
            public DateTime StartUtc;
            public int Width;
            public int Height;
        }

        /// <summary>
        /// Hands the current segment to the background finalizer and clears it. Idempotent.
        /// <para>
        /// Writing out a segment costs far more than a frame interval — the sink writes the moov atom
        /// and drains the hardware encoder — so doing it on the pump thread stopped capture for that
        /// long once every <c>_segmentSeconds</c>, losing frames the clip then holds still for.
        /// Finalizes are chained so only one runs at a time, and teardown waits for the chain. A clip
        /// export cannot race this: it waits a segment length plus a margin past its window end
        /// before it reads any segment.
        /// </para>
        /// </summary>
        private void FinalizeSegment()
        {
            var outgoing = _encoder;
            _encoder = null;
            if (outgoing == null)
            {
                return;
            }

            lock (_finalizeGate)
            {
                _finalizeChain = _finalizeChain.ContinueWith(
                    _ =>
                    {
                        try
                        {
                            outgoing.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "[Recording] Writing out a capture segment failed.");
                        }
                    },
                    TaskContinuationOptions.None);
            }
        }

        private Task GetFinalizerChain()
        {
            lock (_finalizeGate)
            {
                return _finalizeChain;
            }
        }

        private void CompleteCleanup()
        {
            if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
            {
                return;
            }

            // PumpLoop normally handed this off before exiting. Keep the call idempotent for a Start
            // failure that never created a pump thread.
            FinalizeSegment();
            var chain = GetFinalizerChain();
            var completed = chain.IsCompleted;
            try
            {
                completed = completed || chain.Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Waiting for capture segments to finish writing failed.");
                completed = chain.IsCompleted;
            }

            if (completed)
            {
                ReleaseNativeResources();
                return;
            }

            // A slow/hung native finalizer must keep the D3D device and MF runtime alive. Releasing
            // them after an arbitrary timeout was a native use-after-release path.
            _logger?.Warn("[Recording] Capture finalization exceeded 10s; native resources remain owned until it completes.");
            chain.ContinueWith(
                _ => ReleaseNativeResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void ReleaseNativeResources()
        {
            if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
            {
                return;
            }

            _latest?.Dispose();
            _latest = null;
            _scaled?.Dispose();
            _scaled = null;
            _frameScaler?.Dispose();
            _frameScaler = null;
            _toneMapper?.Dispose();
            _toneMapper = null;
            TearDownCapture();
            _device?.ImmediateContext?.Dispose();
            _device?.Dispose();
            _device = null;
            _winrtDevice = null;
            _mediaFoundationLease?.Dispose();
            _mediaFoundationLease = null;
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
            var pump = _pumpThread;
            try
            {
                pump?.Join(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // ignore
            }

            if (pump != null && pump.IsAlive)
            {
                _logger?.Warn("[Recording] WGC-MF pump did not stop within 3s; its native resources remain owned.");
            }
            else if (ReferenceEquals(_pumpThread, pump))
            {
                _pumpThread = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            var pump = _pumpThread;
            if (pump != null && pump.IsAlive)
            {
                // Dispose remains bounded even if a driver call is wedged, but cleanup is ordered
                // strictly after the pump actually exits. All retained workers are background work,
                // so a permanently wedged driver still cannot keep Playnite from terminating.
                Task.Run(() =>
                {
                    try { pump.Join(); } catch { }
                    CompleteCleanup();
                });
                return;
            }

            CompleteCleanup();
        }
    }
}
