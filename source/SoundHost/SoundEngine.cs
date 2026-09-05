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

namespace PlayniteAchievements.SoundHost
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
                EnsureStreaming();
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
                    _notifications = new NotificationClient(() => Post(Reopen));
                    _enumerator.RegisterEndpointNotificationCallback(_notifications);
                }

                var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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

        private void EnsureStreaming()
        {
            if (_output == null || _streaming)
            {
                return;
            }

            _output.Play();
            _streaming = true;
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
                Post(Reopen);
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
                var clip = new Clip(key, samples, _voiceFormat.Channels);
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
            public Clip(string key, float[] samples, int channels)
            {
                Key = key;
                Samples = samples;
                Channels = channels;
            }

            public string Key { get; }
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
            private readonly Action _defaultChanged;

            public NotificationClient(Action defaultChanged)
            {
                _defaultChanged = defaultChanged;
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string deviceId) { }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

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
