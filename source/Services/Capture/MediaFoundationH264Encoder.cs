using System;
using System.IO;
using SharpDX.MediaFoundation;
using D3D11 = SharpDX.Direct3D11;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// GPU-resident H.264 encoder via Media Foundation's SinkWriter. Frames are fed as D3D11 textures
    /// (no CPU readback): the writer is bound to the capture D3D11 device through a DXGI device
    /// manager and hardware transforms are enabled, so a hardware H.264 MFT (NVENC / QuickSync / AMF)
    /// consumes the textures directly and writes an MP4.
    ///
    /// Not available on Windows N/KN without the Media Feature Pack (the H.264 encoder MFT is absent)
    /// Construction throws when the required Windows media components are unavailable.
    /// This first cut takes BGRA (ARGB32) input; a later pass moves the tonemap shader to NV12 output
    /// to keep the color-conversion on the GPU as well.
    /// </summary>
    internal sealed class MediaFoundationH264Encoder : IDisposable
    {
        // IID of ID3D11Texture2D — for wrapping a texture as an MF DXGI surface buffer.
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        private readonly SinkWriter _writer;
        private readonly DXGIDeviceManager _deviceManager;
        private readonly int _streamIndex;
        private bool _disposed;

        private static bool? _available;

        /// <summary>
        /// Whether MF H.264 encoding is usable on this machine — false on Windows N/KN without the
        /// Media Feature Pack (the H.264 encoder MFT is absent), so recording is unavailable.
        /// Probed once by building and tearing down a tiny encoder; cached for the process.
        /// </summary>
        public static bool IsAvailable()
        {
            if (_available.HasValue)
            {
                return _available.Value;
            }

            string temp = null;
            D3D11.Device device = null;
            try
            {
                temp = Path.Combine(Path.GetTempPath(), $"pa_mfprobe_{Guid.NewGuid():N}.mp4");
                device = new D3D11.Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
                using (new MediaFoundationH264Encoder(device, temp, 64, 64, 30, 1_000_000))
                {
                }

                _available = true;
            }
            catch
            {
                _available = false;
            }
            finally
            {
                device?.Dispose();
                try
                {
                    if (temp != null && File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                    // ignore probe cleanup failure
                }
            }

            return _available.Value;
        }

        /// <summary>
        /// Target H.264 bitrate from resolution and fps (~0.12 bits/pixel/frame): ~15 Mbps at
        /// 1080p60, ~27 at 1440p60, ~60 at 4K60. Clamped to a sane range. Shared by the live
        /// segment encoder and the export-time overlay re-encoder so clip quality matches.
        /// </summary>
        internal static int ComputeBitrate(int width, int height, int fps)
        {
            var bits = (long)(width * (double)height * fps * 0.12);
            return (int)Math.Max(8_000_000L, Math.Min(120_000_000L, bits));
        }

        public MediaFoundationH264Encoder(
            D3D11.Device device, string outputPath, int width, int height, int fps, int bitrate)
        {
            MediaManager.Startup();

            // Bind MF to the same D3D11 device the frames come from, so the hardware encoder reads
            // the textures in place.
            _deviceManager = new DXGIDeviceManager();
            _deviceManager.ResetDevice(device);

            using (var attributes = new MediaAttributes(2))
            {
                attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
                attributes.Set(SinkWriterAttributeKeys.D3DManager, _deviceManager);
                _writer = MediaFactory.CreateSinkWriterFromURL(outputPath, null, attributes);
            }

            using (var outputType = new MediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
                // One keyframe per second so the concat/stream-copy clip trim snaps at most ~1s early
                // (matching the old ffmpeg -force_key_frames), not to a distant GOP boundary.
                outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, fps);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                _writer.AddStream(outputType, out _streamIndex);
            }

            using (var inputType = new MediaType())
            {
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                _writer.SetInputMediaType(_streamIndex, inputType, null);
            }

            _writer.BeginWriting();
        }

        /// <summary>
        /// Encodes one frame from a BGRA D3D11 texture. Times are in 100-ns units (MF's unit).
        /// </summary>
        public void WriteFrame(D3D11.Texture2D texture, long timestamp100ns, long duration100ns)
        {
            if (_disposed)
            {
                return;
            }

            var iid = IID_ID3D11Texture2D;
            MediaFactory.CreateDXGISurfaceBuffer(iid, texture, 0, false, out var buffer);
            using (buffer)
            {
                using (var buffer2D = buffer.QueryInterface<Buffer2D>())
                {
                    buffer.CurrentLength = buffer2D.ContiguousLength;
                }

                using (var sample = MediaFactory.CreateSample())
                {
                    sample.AddBuffer(buffer);
                    sample.SampleTime = timestamp100ns;
                    sample.SampleDuration = duration100ns;
                    _writer.WriteSample(_streamIndex, sample);
                }
            }
        }

        // MF packs a size/ratio into a UINT64: high 32 bits = width/numerator, low 32 = height/denominator.
        private static long Pack(int high, int low)
        {
            return ((long)high << 32) | (uint)low;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Finalize();
            }
            catch
            {
                // A clip torn down before any frame was written finalizes to an empty/failed file.
            }

            _writer?.Dispose();
            _deviceManager?.Dispose();
            try
            {
                MediaManager.Shutdown();
            }
            catch
            {
                // Startup/Shutdown are refcounted per process; ignore an unbalanced shutdown at teardown.
            }
        }
    }
}
