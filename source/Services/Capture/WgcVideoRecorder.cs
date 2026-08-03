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

        private readonly IntPtr _hwnd;
        private readonly string _bufferDirectory;
        private readonly int _fps;
        private readonly int _segmentSeconds;
        private readonly int _bitrate;
        private readonly ILogger _logger;

        private D3D11.Device _device;
        private WinRtIDirect3DDevice _winrtDevice;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;
        private GpuHdrToneMapper _toneMapper;
        private bool _hdr;
        private float _refWhite = 1.0f;

        private D3D11.Texture2D _latest; // owned, BGRA, the most recent (tone-mapped) frame
        private MediaFoundationH264Encoder _encoder;
        private long _segmentFrameIndex;
        private int _segmentCount;
        private DateTime _segmentStartUtc;
        private readonly long _frameDuration100ns;

        private Thread _pumpThread;
        private volatile bool _running;
        private bool _disposed;

        public WgcVideoRecorder(IntPtr hwnd, string bufferDirectory, int fps, int bitrate, int segmentSeconds, ILogger logger)
        {
            _hwnd = hwnd;
            _bufferDirectory = bufferDirectory;
            _fps = Math.Max(1, fps);
            _bitrate = Math.Max(1_000_000, bitrate);
            _segmentSeconds = Math.Max(1, segmentSeconds);
            _logger = logger;
            _frameDuration100ns = 10_000_000L / _fps;
        }

        public static bool IsSupported => GraphicsCaptureSession.IsSupported();

        /// <summary>Starts capture. Returns false (and leaves nothing running) if it can't initialize.</summary>
        public bool Start()
        {
            try
            {
                _hdr = HdrDisplayDetector.IsHdrActive(_hwnd);
                _refWhite = _hdr ? HdrDisplayDetector.GetSdrWhiteScRgb(_hwnd) : 1.0f;

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

                _item = CreateItemForWindow(_hwnd);
                if (_item == null || _item.Size.Width <= 0 || _item.Size.Height <= 0)
                {
                    return false;
                }

                if (_hdr)
                {
                    _toneMapper = new GpuHdrToneMapper(_device);
                }

                var pixelFormat = _hdr
                    ? DirectXPixelFormat.R16G16B16A16Float
                    : DirectXPixelFormat.B8G8R8A8UIntNormalized;
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, pixelFormat, 2, _item.Size);
                _session = _framePool.CreateCaptureSession(_item);
                TrySuppressBorder(_session);
                _session.StartCapture();

                Directory.CreateDirectory(_bufferDirectory);
                _running = true;
                _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "PlayAch-WgcVideo" };
                _pumpThread.Start();
                _logger?.Info($"[Recording] WGC-MF capture started (hdr={_hdr}, {_item.Size.Width}x{_item.Size.Height}@{_fps}).");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Recording] WGC-MF capture failed to start.");
                Stop();
                return false;
            }
        }

        private void PumpLoop()
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
            var next = DateTime.UtcNow;
            try
            {
                while (_running)
                {
                    PullLatestFrame();

                    if (_latest != null)
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
                                _encoder.WriteFrame(_latest, _segmentFrameIndex * _frameDuration100ns, _frameDuration100ns);
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
                    EnsureLatest(bgra.Description.Width, bgra.Description.Height);
                    _device.ImmediateContext.CopyResource(bgra, _latest);
                }
            }
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
            _encoder = new MediaFoundationH264Encoder(_device, path, _latest.Description.Width, _latest.Description.Height, _fps, _bitrate);
            _segmentFrameIndex = 0;
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
            _toneMapper?.Dispose();
            _session?.Dispose();
            _framePool?.Dispose();
            _device?.ImmediateContext?.Dispose();
            _device?.Dispose();
            _winrtDevice = null;
        }
    }
}
