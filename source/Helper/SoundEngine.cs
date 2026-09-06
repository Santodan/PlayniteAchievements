using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using PlayniteAchievements.Services.Sound;

namespace PlayniteAchievements.Helper
{
    /// <summary>
    /// Renders preloaded clips through one persistent shared-mode WASAPI stream. A play is a
    /// pointer swap observed at the next engine period, so the launch-to-audible latency is the
    /// event-driven buffer (30 ms) plus the endpoint's own, not a device open or a decode. One
    /// voice: a new sound replaces the previous one, which is what the recorder assumes when it
    /// bounds a clip's composited chime.
    ///
    /// NAudio's CoreAudioApi is safe here. The MMDeviceEnumerator CLSID collision the plugin must
    /// avoid arises only when two extensions load NAudio into Playnite's process; this exe is
    /// alone in its own process.
    /// </summary>
    internal sealed class SoundEngine : IDisposable
    {
        private const int LatencyMs = 30;
        private const int IdleStopSeconds = 60;
        private const int MaxCachedSeconds = 60;

        private readonly Action<string> _emit;
        private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private readonly Voice _voice = new Voice();
        private readonly Dictionary<string, Clip> _cache = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _preloadSet = new List<string>();
        private readonly Timer _idleTimer;
        private MMDeviceEnumerator _enumerator;
        private NotificationClient _notifications;
        private WasapiOut _output;
        private string _deviceId;
        private WaveFormat _voiceFormat;
        private bool _streaming;
        private long _lastActivityTicks = Stopwatch.GetTimestamp();
        private bool _disposed;

        public SoundEngine(Action<string> emit)
        {
            _emit = emit;
            _voice.Started = OnVoiceStarted;
            _thread = new Thread(Run) { IsBackground = true, Name = "SoundEngine" };
            _thread.Start();
            _idleTimer = new Timer(_ => Post(StopIfIdle), null, 5000, 5000);
        }

        public void Post(Action action)
        {
            if (!_disposed)
            {
                try
                {
                    _work.Add(action);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        public void Preload(IReadOnlyList<string> paths)
        {
            Post(() =>
            {
                _preloadSet.Clear();
                _preloadSet.AddRange(paths);
                EnsureOpen();
                PreloadCurrentSet();
            });
        }

        public void Play(int id, string path, double gain)
        {
            Post(() =>
            {
                if (!EnsureOpen())
                {
                    _emit(SoundHostProtocol.EncodeError(id, "No render device is available."));
                    return;
                }

                var clip = GetOrDecode(path, id);
                if (clip == null)
                {
                    return;
                }

                _voice.Assign(clip, (float)Math.Max(0.0, Math.Min(1.0, gain)), id);
                _lastActivityTicks = Stopwatch.GetTimestamp();
                EnsureStreaming(retryOnFailure: true);
            });
        }

        public void Stop()
        {
            Post(() => _voice.Assign(null, 0f, -1));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idleTimer.Dispose();
            _work.CompleteAdding();
            _thread.Join(2000);
            Close();
            try
            {
                if (_notifications != null)
                {
                    _enumerator?.UnregisterEndpointNotificationCallback(_notifications);
                }

                _enumerator?.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void Run()
        {
            foreach (var action in _work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _emit(SoundHostProtocol.EncodeError(-1, ex.GetType().Name + ": " + ex.Message));
                }
            }
        }

        private bool EnsureOpen()
        {
            if (_output != null)
            {
                return true;
            }

            try
            {
                if (_enumerator == null)
                {
                    _enumerator = new MMDeviceEnumerator();
                    _notifications = new NotificationClient(
                        () => Post(Reopen),
                        deviceId => Post(() => OnDeviceFormatChanged(deviceId)));
                    _enumerator.RegisterEndpointNotificationCallback(_notifications);
                }

                var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceId = device.ID;
                var mix = device.AudioClient.MixFormat;
                var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                if (_voiceFormat == null || !SameFormat(_voiceFormat, format))
                {
                    _voiceFormat = format;
                    _cache.Clear();
                    _voice.Assign(null, 0f, -1);
                }

                _voice.WaveFormat = format;
                var output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(_voice);
                _output = output;
                _streaming = false;
                return true;
            }
            catch (Exception ex)
            {
                _emit(SoundHostProtocol.EncodeError(-1, "Open failed: " + ex.Message));
                Close();
                return false;
            }
        }

        /// <summary>
        /// Starts the stream if it is idle. A stream initialized against a device whose format has
        /// since changed fails here with AUDCLNT_E_DEVICE_INVALIDATED; the pending sound is then
        /// replayed once through a reopened stream rather than lost.
        /// </summary>
        private void EnsureStreaming(bool retryOnFailure)
        {
            if (_output == null || _streaming)
            {
                return;
            }

            try
            {
                _output.Play();
                _streaming = true;
            }
            catch (Exception ex)
            {
                _emit(SoundHostProtocol.EncodeError(-1, "Start failed: " + ex.Message));
                RecoverStream(_output, retryOnFailure);
            }
        }

        /// <summary>
        /// Reopens after the stream owned by <paramref name="failed"/> broke. A sound assigned but
        /// not yet rendered is re-decoded for the reopened format and replayed once. A report from
        /// a stream that has already been replaced is ignored, so a late failure cannot tear down
        /// the replacement while it plays.
        /// </summary>
        private void RecoverStream(object failed, bool replayPending)
        {
            if (_output != null && !ReferenceEquals(failed, _output))
            {
                return;
            }

            var pending = replayPending ? _voice.TakeIfUnannounced() : null;
            Reopen();
            if (pending == null)
            {
                return;
            }

            var clip = GetOrDecode(pending.Clip.Path, pending.Id);
            if (clip != null)
            {
                _voice.Assign(clip, pending.Gain, pending.Id);
                _lastActivityTicks = Stopwatch.GetTimestamp();
                EnsureStreaming(retryOnFailure: false);
            }
        }

        /// <summary>
        /// The default device's shared-mode format changed (a speaker layout switch, for instance).
        /// The open stream is bound to the old format and would fail on its next start, so reopen
        /// now, while no sound is in flight, rather than lose the next one.
        /// </summary>
        private void OnDeviceFormatChanged(string deviceId)
        {
            if (_output == null || !string.Equals(deviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_voice.IsActive)
            {
                // Let the current sound finish; a start against the stale stream is recovered by
                // EnsureStreaming.
                return;
            }

            Reopen();
        }

        private void StopIfIdle()
        {
            if (_output == null || !_streaming || _voice.IsActive)
            {
                return;
            }

            var idleSeconds = (Stopwatch.GetTimestamp() - _lastActivityTicks) / (double)Stopwatch.Frequency;
            if (idleSeconds >= IdleStopSeconds)
            {
                _output.Stop();
                _streaming = false;
            }
        }

        private void Reopen()
        {
            Close();
            if (EnsureOpen())
            {
                PreloadCurrentSet();
            }
        }

        private void Close()
        {
            var output = _output;
            _output = null;
            _streaming = false;
            if (output == null)
            {
                return;
            }

            try
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                output.Stop();
                output.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                _emit(SoundHostProtocol.EncodeError(-1, "Playback stopped: " + e.Exception.Message));
                Post(() => RecoverStream(sender, replayPending: true));
            }
        }

        private void OnVoiceStarted(int id, long qpc)
        {
            _emit(SoundHostProtocol.EncodeStarted(id, qpc));
        }

        private void PreloadCurrentSet()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _preloadSet)
            {
                var clip = GetOrDecode(path, -1);
                if (clip != null)
                {
                    keep.Add(clip.Key);
                }
            }

            var stale = new List<string>();
            foreach (var key in _cache.Keys)
            {
                if (!keep.Contains(key))
                {
                    stale.Add(key);
                }
            }

            foreach (var key in stale)
            {
                _cache.Remove(key);
            }
        }

        private Clip GetOrDecode(string path, int id)
        {
            if (_voiceFormat == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string key;
            try
            {
                key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception ex)
            {
                _emit(SoundHostProtocol.EncodeError(id, "Unreadable path '" + path + "': " + ex.Message));
                return null;
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            try
            {
                var samples = Decode(path, _voiceFormat);
                var clip = new Clip(key, path, samples, _voiceFormat.Channels);
                if (CachedSeconds() + clip.Seconds(_voiceFormat.SampleRate) <= MaxCachedSeconds)
                {
                    _cache[key] = clip;
                }

                return clip;
            }
            catch (Exception ex)
            {
                _emit(SoundHostProtocol.EncodeError(id, "Decode failed for '" + path + "': " + ex.Message));
                return null;
            }
        }

        private double CachedSeconds()
        {
            var total = 0.0;
            foreach (var clip in _cache.Values)
            {
                total += clip.Seconds(_voiceFormat.SampleRate);
            }

            return total;
        }

        private static float[] Decode(string path, WaveFormat target)
        {
            using (var reader = new MediaFoundationReader(path))
            using (var resampler = new MediaFoundationResampler(reader, target) { ResamplerQuality = 60 })
            {
                var provider = resampler.ToSampleProvider();
                var chunks = new List<float[]>();
                var total = 0;
                var buffer = new float[target.SampleRate * target.Channels];
                while (true)
                {
                    var read = provider.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    var chunk = new float[read];
                    Array.Copy(buffer, chunk, read);
                    chunks.Add(chunk);
                    total += read;
                }

                var samples = new float[total];
                var offset = 0;
                foreach (var chunk in chunks)
                {
                    Array.Copy(chunk, 0, samples, offset, chunk.Length);
                    offset += chunk.Length;
                }

                return samples;
            }
        }

        private static bool SameFormat(WaveFormat a, WaveFormat b)
        {
            return a.SampleRate == b.SampleRate && a.Channels == b.Channels;
        }

        private sealed class Clip
        {
            public Clip(string key, string path, float[] samples, int channels)
            {
                Key = key;
                Path = path;
                Samples = samples;
                Channels = channels;
            }

            public string Key { get; }
            public string Path { get; }
            public float[] Samples { get; }
            public int Channels { get; }

            public double Seconds(int sampleRate)
            {
                return Samples.Length / (double)(sampleRate * Channels);
            }
        }

        /// <summary>A clip in flight; immutable apart from the render thread's position.</summary>
        private sealed class Playback
        {
            public Playback(Clip clip, float gain, int id)
            {
                Clip = clip;
                Gain = gain;
                Id = id;
            }

            public Clip Clip { get; }
            public float Gain { get; }
            public int Id { get; }
            public int Position;
            public bool Announced;
        }

        /// <summary>The single always-attached source: silence until a clip is assigned.</summary>
        private sealed class Voice : ISampleProvider
        {
            private volatile Playback _current;

            public WaveFormat WaveFormat { get; set; }
            public Action<int, long> Started { get; set; }
            public bool IsActive => _current != null;

            public void Assign(Clip clip, float gain, int id)
            {
                _current = clip == null ? null : new Playback(clip, gain, id);
            }

            /// <summary>
            /// Detaches the assigned sound if the render thread never reached it, so a stream
            /// failure between assignment and first read can replay it; null otherwise.
            /// </summary>
            public Playback TakeIfUnannounced()
            {
                var playback = _current;
                if (playback == null || playback.Announced)
                {
                    return null;
                }

                _current = null;
                return playback;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                var playback = _current;
                var written = 0;
                if (playback != null)
                {
                    if (!playback.Announced)
                    {
                        playback.Announced = true;
                        Started?.Invoke(playback.Id, Stopwatch.GetTimestamp());
                    }

                    var samples = playback.Clip.Samples;
                    var remaining = samples.Length - playback.Position;
                    var take = Math.Min(remaining, count);
                    var gain = playback.Gain;
                    for (var i = 0; i < take; i++)
                    {
                        buffer[offset + i] = samples[playback.Position + i] * gain;
                    }

                    playback.Position += take;
                    written = take;
                    if (playback.Position >= samples.Length && ReferenceEquals(_current, playback))
                    {
                        _current = null;
                    }
                }

                if (written < count)
                {
                    Array.Clear(buffer, offset + written, count - written);
                }

                return count;
            }
        }

        private sealed class NotificationClient : IMMNotificationClient
        {
            // PKEY_AudioEngine_DeviceFormat and PKEY_AudioEngine_OEMFormat: an endpoint's
            // shared-mode mix format, which changes with its speaker layout or sample rate.
            private static readonly Guid DeviceFormatKey = new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
            private static readonly Guid OemFormatKey = new Guid("e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04");

            private readonly Action _defaultChanged;
            private readonly Action<string> _formatChanged;

            public NotificationClient(Action defaultChanged, Action<string> formatChanged)
            {
                _defaultChanged = defaultChanged;
                _formatChanged = formatChanged;
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string deviceId) { }

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
            {
                if (key.formatId == DeviceFormatKey || key.formatId == OemFormatKey)
                {
                    _formatChanged(pwstrDeviceId);
                }
            }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render && role == Role.Multimedia)
                {
                    _defaultChanged();
                }
            }
        }
    }
}
