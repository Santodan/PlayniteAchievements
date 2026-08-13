using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Captures the audio of a single process tree (the game) via the Windows Application Loopback
    /// API — <c>ActivateAudioInterfaceAsync</c> against the virtual process-loopback device with
    /// <c>PROCESS_LOOPBACK</c> activation params — exposed as an <see cref="IWaveIn"/> so it drops
    /// into <see cref="AudioLoopbackRecorder"/> in place of the full-system loopback. Requires
    /// Windows 10 build 19041+; callers gate on <see cref="IsSupported"/> and fall back to full
    /// system audio when it's unavailable or activation fails. The mixed stream is delivered as
    /// 48 kHz stereo 32-bit float, polled off a background thread.
    /// </summary>
    internal sealed class ProcessLoopbackCapture : IWaveIn
    {
        private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const uint WAVE_FORMAT_IEEE_FLOAT = 3;
        // ActivationType: 0 = default, 1 = process loopback. Mode: 0 = include target tree.
        private const int ProcessLoopbackActivation = 1;
        private const int IncludeTargetProcessTree = 0;
        private const int ExcludeTargetProcessTree = 1;

        // AUDCLNT_BUFFERFLAGS_SILENT: the packet is digital silence, so its zeroed buffer stands.
        private const int BufferFlagsSilent = 0x2;

        // The most dropped audio one gap will stand silence in for.
        private const int MaxGapSeconds = 5;

        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        private readonly int _processId;
        private readonly int _mode;
        private IAudioClient _audioClient;
        private IAudioCaptureClient _captureClient;
        private Thread _pollThread;
        private volatile bool _capturing;
        private bool _disposed;
        private int _clientsReleased;

        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;

        /// <summary>
        /// When the first delivered packet's audio was actually rendered, from the QPC stamp
        /// <c>IAudioCaptureClient.GetBuffer</c> reports alongside it, or null while no packet has
        /// arrived (or when the driver reports no usable stamp).
        /// <para>
        /// Packets reach us later than the audio they carry, by the engine's buffering plus this
        /// poll loop's own interval. A consumer pacing writes to wall clock therefore places every
        /// sample late by that delay unless it anchors to this instead of to the moment capture
        /// started.
        /// </para>
        /// </summary>
        public DateTime? FirstPacketCaptureUtc => _firstPacketCaptureUtc;

        private DateTime? _firstPacketCaptureUtc;

        /// <summary>
        /// Frames of silence delivered in place of audio the engine dropped, over this capture's life.
        /// Non-zero means the track carries real glitches — worth reporting before a listener blames
        /// the gaps on a sync bug.
        /// </summary>
        public long PaddedGapFrames => _paddedGapFrames;

        private long _paddedGapFrames;

        // The device frame position the next packet should begin at; -1 until the first one fixes it.
        private long _nextDevicePosition = -1;

        /// <summary>
        /// Frames missing between where the previous packet ended and where this one begins, per the
        /// device's own frame counter, and advances that counter past this packet.
        /// <para>
        /// A jump is audio the engine dropped before it reached us — the glitch
        /// AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY reports, whose size only the position reveals. The
        /// result is capped so a driver reporting a nonsense position cannot make us insert an
        /// unbounded run of silence, and drivers that never move the position simply report no gaps.
        /// </para>
        /// </summary>
        private long TakeGapBefore(long devicePosition, uint framesAvailable)
        {
            var expected = _nextDevicePosition;
            _nextDevicePosition = devicePosition + framesAvailable;
            if (expected < 0 || devicePosition <= expected)
            {
                return 0;
            }

            var gap = devicePosition - expected;
            var cap = (long)WaveFormat.SampleRate * MaxGapSeconds;
            return gap > cap ? cap : gap;
        }

        /// <summary>
        /// Converts a GetBuffer QPC stamp (100-ns units on the performance counter's timebase) to
        /// UTC, by measuring how old it is against the counter's current value. Returns null when
        /// the driver reports no stamp, or when the result is not plausibly recent -- some drivers
        /// report zero or a value on an unrelated timebase, and a bad anchor is worse than none.
        /// </summary>
        private static DateTime? QpcToUtc(long qpcPosition100ns)
        {
            if (qpcPosition100ns <= 0)
            {
                return null;
            }

            var now100ns = (long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));
            var age100ns = now100ns - qpcPosition100ns;

            // A packet is at most a second or so old; anything outside that is a timebase we do
            // not understand. Negative means the stamp is in the future, which is equally wrong.
            if (age100ns < 0 || age100ns > 10_000_000L)
            {
                return null;
            }

            return CaptureTimelineClock.UtcNow.AddTicks(-age100ns);
        }

        /// <summary>48 kHz stereo 32-bit IEEE float — the format the loopback client mixes the process to.</summary>
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        /// <summary>Process-loopback activation exists on Windows 10 build 19041+ (20H1).</summary>
        public static bool IsSupported
        {
            get
            {
                try
                {
                    return Environment.OSVersion.Platform == PlatformID.Win32NT &&
                           Environment.OSVersion.Version >= new Version(10, 0, 19041);
                }
                catch
                {
                    return false;
                }
            }
        }

        public ProcessLoopbackCapture(int processId, bool includeProcessTree = true)
        {
            _processId = processId;
            _mode = includeProcessTree ? IncludeTargetProcessTree : ExcludeTargetProcessTree;
            try
            {
                _audioClient = ActivateProcessLoopbackClient(processId, _mode);
                InitializeClient();
            }
            catch
            {
                ReleaseClients();
                throw;
            }
        }

        private IAudioClient ActivateProcessLoopbackClient(int processId, int mode)
        {
            var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = ProcessLoopbackActivation,
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = processId,
                    ProcessLoopbackMode = mode,
                },
            };

            var paramSize = Marshal.SizeOf(typeof(AUDIOCLIENT_ACTIVATION_PARAMS));
            var paramPtr = Marshal.AllocHGlobal(paramSize);
            try
            {
                Marshal.StructureToPtr(activationParams, paramPtr, false);

                var prop = new PROPVARIANT
                {
                    vt = 65, // VT_BLOB
                    blobSize = paramSize,
                    blobData = paramPtr,
                };

                var handler = new ActivationHandler();
                IActivateAudioInterfaceAsyncOperation op = null;
                try
                {
                    var iid = IID_IAudioClient;
                    var hr = ActivateAudioInterfaceAsync(
                        VirtualAudioDeviceProcessLoopback, ref iid, ref prop, handler, out op);
                    if (hr != 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }

                    if (!handler.Completed.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("ActivateAudioInterfaceAsync did not complete.");
                    }

                    if (handler.ActivateHr != 0 || handler.Interface == null)
                    {
                        Marshal.ThrowExceptionForHR(
                            handler.ActivateHr != 0 ? handler.ActivateHr : unchecked((int)0x80004005));
                    }

                    return (IAudioClient)handler.Interface;
                }
                finally
                {
                    if (op != null)
                    {
                        Marshal.ReleaseComObject(op);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(paramPtr);
            }
        }

        private void InitializeClient()
        {
            var format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)WAVE_FORMAT_IEEE_FLOAT,
                nChannels = (ushort)WaveFormat.Channels,
                nSamplesPerSec = (uint)WaveFormat.SampleRate,
                wBitsPerSample = (ushort)WaveFormat.BitsPerSample,
                nBlockAlign = (ushort)(WaveFormat.Channels * WaveFormat.BitsPerSample / 8),
                cbSize = 0,
            };
            format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

            var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEX)));
            try
            {
                Marshal.StructureToPtr(format, formatPtr, false);
                // 200 ms buffer (100-ns units). Process loopback requires shared mode + the loopback flag.
                var hr = _audioClient.Initialize(
                    AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 2_000_000, 0, formatPtr, IntPtr.Zero);
                if (hr != 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            var captureIid = IID_IAudioCaptureClient;
            var svcHr = _audioClient.GetService(ref captureIid, out var captureObj);
            if (svcHr != 0 || captureObj == null)
            {
                Marshal.ThrowExceptionForHR(svcHr != 0 ? svcHr : unchecked((int)0x80004005));
            }

            _captureClient = (IAudioCaptureClient)captureObj;
        }

        public void StartRecording()
        {
            if (_capturing || _disposed)
            {
                return;
            }

            // The device counter is only meaningful within one run: carrying it across a restart would
            // read as an enormous gap and pad the track with silence that never happened.
            _nextDevicePosition = -1;
            _audioClient.Start();
            _capturing = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "PA-ProcLoopback" };
            _pollThread.Start();
        }

        private void PollLoop()
        {
            Exception error = null;
            try
            {
                var blockAlign = WaveFormat.BlockAlign;
                while (_capturing)
                {
                    var packet = _captureClient.GetNextPacketSize(out var frames) == 0 ? frames : 0;
                    if (packet == 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    while (frames > 0)
                    {
                        if (_captureClient.GetBuffer(
                                out var dataPtr, out var framesAvailable, out var flags,
                                out var devicePosition, out var qpcPosition) != 0)
                        {
                            break;
                        }

                        var bytes = (int)framesAvailable * blockAlign;
                        if (_firstPacketCaptureUtc == null && bytes > 0)
                        {
                            // Read before ReleaseBuffer so the stamp still belongs to this packet.
                            _firstPacketCaptureUtc = QpcToUtc(qpcPosition);
                        }

                        var gapFrames = TakeGapBefore(devicePosition, framesAvailable);

                        var buffer = new byte[bytes];
                        if ((flags & BufferFlagsSilent) == 0 && dataPtr != IntPtr.Zero && bytes > 0)
                        {
                            Marshal.Copy(dataPtr, buffer, 0, bytes);
                        }

                        _captureClient.ReleaseBuffer(framesAvailable);

                        // Stand silence in for what the engine dropped, before the packet that follows
                        // it. Delivering the packets back to back instead would pull all later audio
                        // permanently early against picture — A/V drift that never recovers.
                        if (gapFrames > 0)
                        {
                            _paddedGapFrames += gapFrames;
                            var gapBytes = (int)gapFrames * blockAlign;
                            DataAvailable?.Invoke(this, new WaveInEventArgs(new byte[gapBytes], gapBytes));
                        }

                        if (bytes > 0)
                        {
                            DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                        }

                        if (_captureClient.GetNextPacketSize(out frames) != 0)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
            }
        }

        public void StopRecording()
        {
            if (!_capturing)
            {
                return;
            }

            _capturing = false;
            // Stop the native engine first so a poll blocked in an audio-client call can return.
            try { _audioClient?.Stop(); } catch { }
            try { _pollThread?.Join(500); } catch { }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopRecording();
            var poll = _pollThread;
            if (poll != null && poll.IsAlive)
            {
                // Never release COM interfaces while PollLoop can still call through them. A slow
                // driver may outlive the bounded Dispose call, so retain ownership until it exits.
                Task.Run(() =>
                {
                    try { poll.Join(); } catch { }
                    ReleaseClients();
                });
                return;
            }

            ReleaseClients();
        }

        private void ReleaseClients()
        {
            if (Interlocked.Exchange(ref _clientsReleased, 1) != 0)
            {
                return;
            }

            if (_captureClient != null)
            {
                Marshal.ReleaseComObject(_captureClient);
                _captureClient = null;
            }

            if (_audioClient != null)
            {
                Marshal.ReleaseComObject(_audioClient);
                _audioClient = null;
            }
        }

        // === Native interop ===

        [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            ref PROPVARIANT activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            public int TargetProcessId;
            public int ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public int ActivationType;
            public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort r1;
            public ushort r2;
            public ushort r3;
            public int blobSize;
            public IntPtr blobData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(
                [MarshalAs(UnmanagedType.Error)] out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(
                int shareMode, int streamFlags, long bufferDuration, long periodicity,
                IntPtr format, IntPtr audioSessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint bufferFrames);

            [PreserveSig]
            int GetStreamLatency(out long latency);

            [PreserveSig]
            int GetCurrentPadding(out uint padding);

            [PreserveSig]
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr format);

            [PreserveSig]
            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr dataBuffer, out uint framesToRead, out int bufferFlags, out long devicePosition, out long qpcPosition);

            [PreserveSig]
            int ReleaseBuffer(uint framesWritten);

            [PreserveSig]
            int GetNextPacketSize(out uint framesInNextPacket);
        }

        /// <summary>
        /// Marker interface making the completion handler's CCW apartment-agile.
        /// ActivateAudioInterfaceAsync rejects non-agile handlers with E_ILLEGAL_METHOD_CALL
        /// (0x8000000E); .NET Core CCWs are agile by default, but .NET Framework's are not, so
        /// the handler must implement this explicitly.
        /// </summary>
        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject
        {
        }

        /// <summary>Blocks the caller until the async activation completes, capturing its result.</summary>
        private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Completed = new ManualResetEvent(false);
            public int ActivateHr;
            public object Interface;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                try
                {
                    activateOperation.GetActivateResult(out ActivateHr, out Interface);
                }
                catch (Exception ex)
                {
                    ActivateHr = Marshal.GetHRForException(ex);
                }
                finally
                {
                    Completed.Set();
                }
            }
        }
    }
}
