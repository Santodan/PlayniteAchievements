using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Accumulates one <see cref="ToastOverlayTrack"/> per toast item over a wave's on-screen
    /// lifetime. The toast pipeline calls <see cref="Sample"/> on the UI thread once per publish
    /// tick per item with the card's rendered pixels and client-relative rect; consecutive
    /// identical frames dedup by memcmp (static cards collapse to a handful of frames, GIF and
    /// countdown cards keep their real cadence), and unique frames Deflate-compress on a single
    /// background worker so the render tick never pays compression cost. Memory is capped per
    /// track and per recorder: past a cap new frames stop (samples continue, so the card freezes
    /// at its last frame in the clip) with a single log line.
    /// </summary>
    internal sealed class ToastOverlayTrackRecorder
    {
        private const long PerTrackCompressedCapBytes = 48L * 1024 * 1024;
        private const long TotalCompressedCapBytes = 128L * 1024 * 1024;

        /// <summary>
        /// Compression jobs allowed in flight before new frames are skipped for a tick (the
        /// sample then reuses the previous frame). Bounds raw-buffer memory if the worker falls
        /// behind the render loop.
        /// </summary>
        private const int MaxQueuedCompressions = 16;

        /// <summary>Trailing pad added to the last sample time, one nominal sample interval.</summary>
        private const int SampleIntervalMs = 33;

        private sealed class ItemState
        {
            public ToastOverlayTrack Track;
            public byte[] LastRaw;
            public int LastFrameIndex = -1;
            public int FirstSampleTick;
            public bool HasFirstTick;
            public long CompressedBytes;
            public bool FramesCapped;
        }

        private sealed class CompressionJob
        {
            public ItemState State;
            public ToastOverlayTrack.Frame Frame;
            public byte[] Raw;
        }

        private readonly ILogger _logger;
        private readonly Dictionary<AchievementToastViewModel, ItemState> _items =
            new Dictionary<AchievementToastViewModel, ItemState>();
        private readonly object _queueLock = new object();
        private readonly Queue<CompressionJob> _pending = new Queue<CompressionJob>();
        private Task _worker;
        private long _totalCompressedBytes;
        private bool _capLogged;

        public ToastOverlayTrackRecorder(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Records one tick of one card's animation. UI thread only. The rect is the card's
        /// top-left relative to the game client rect plus the client size, all physical pixels.
        /// </summary>
        public void Sample(
            AchievementToastViewModel vm, byte[] premulBgra, int width, int height,
            int relX, int relY, int clientW, int clientH)
        {
            if (vm == null || premulBgra == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (!_items.TryGetValue(vm, out var state))
            {
                state = new ItemState
                {
                    Track = new ToastOverlayTrack
                    {
                        ProviderKey = vm.ProviderKey,
                        AchievementName = vm.AchievementName,
                        StartUtc = DateTime.UtcNow,
                    },
                };
                _items[vm] = state;
            }

            var now = Environment.TickCount;
            if (!state.HasFirstTick)
            {
                state.FirstSampleTick = now;
                state.HasFirstTick = true;
            }

            var frameIndex = state.LastFrameIndex;
            if (!RawEquals(state.LastRaw, premulBgra))
            {
                if (!state.FramesCapped && TryEnqueueCompression(state, premulBgra, width, height, out var newIndex))
                {
                    frameIndex = newIndex;
                    state.LastRaw = premulBgra;
                    state.LastFrameIndex = newIndex;
                }
                // else: worker backlog or cap — reuse the previous frame for this tick.
            }

            state.Track.Samples.Add(new ToastOverlayTrack.Sample
            {
                ElapsedMs = unchecked(now - state.FirstSampleTick),
                FrameIndex = frameIndex,
                RelX = relX,
                RelY = relY,
                ClientW = clientW,
                ClientH = clientH,
            });
        }

        /// <summary>
        /// Stores the constant translation that moves this card's settled on-screen position to
        /// its synthetic single-toast corner (client-relative physical pixels).
        /// </summary>
        public void SetCornerOffset(AchievementToastViewModel vm, int offsetX, int offsetY)
        {
            if (vm != null && _items.TryGetValue(vm, out var state))
            {
                state.Track.OffsetX = offsetX;
                state.Track.OffsetY = offsetY;
            }
        }

        /// <summary>
        /// Drains the compression worker and finalizes track durations. Call after sampling has
        /// stopped (the handler is detached); no further <see cref="Sample"/> calls may follow.
        /// </summary>
        public async Task<IReadOnlyList<ToastOverlayTrack>> CompleteAsync()
        {
            Task worker;
            lock (_queueLock)
            {
                worker = _worker;
            }

            if (worker != null)
            {
                try
                {
                    await worker.ConfigureAwait(false);
                }
                catch
                {
                    // Worker failures already degraded individual frames; the track ships without them.
                }
            }

            var tracks = new List<ToastOverlayTrack>();
            foreach (var state in _items.Values)
            {
                var samples = state.Track.Samples;
                if (samples.Count == 0)
                {
                    continue;
                }

                state.Track.DurationSeconds = (samples[samples.Count - 1].ElapsedMs + SampleIntervalMs) / 1000.0;
                tracks.Add(state.Track);
            }

            return tracks;
        }

        private static bool RawEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryEnqueueCompression(ItemState state, byte[] raw, int width, int height, out int frameIndex)
        {
            frameIndex = -1;
            lock (_queueLock)
            {
                // A first frame is never skipped: a track with samples but no frame at all would
                // be useless, and the queue can't be full before the first frame anyway in
                // practice.
                if (_pending.Count >= MaxQueuedCompressions && state.LastFrameIndex >= 0)
                {
                    return false;
                }

                var frame = new ToastOverlayTrack.Frame { Width = width, Height = height };
                state.Track.Frames.Add(frame);
                frameIndex = state.Track.Frames.Count - 1;
                _pending.Enqueue(new CompressionJob { State = state, Frame = frame, Raw = raw });
                if (_worker == null || _worker.IsCompleted)
                {
                    _worker = Task.Run(() => DrainQueue());
                }
            }

            return true;
        }

        private void DrainQueue()
        {
            while (true)
            {
                CompressionJob job;
                lock (_queueLock)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    job = _pending.Dequeue();
                }

                try
                {
                    var compressed = ToastOverlayTrack.Frame.FromRaw(job.Raw, job.Frame.Width, job.Frame.Height);
                    job.Frame.Deflated = compressed.Deflated;

                    job.State.CompressedBytes += compressed.Deflated.Length;
                    _totalCompressedBytes += compressed.Deflated.Length;
                    if (!job.State.FramesCapped &&
                        (job.State.CompressedBytes >= PerTrackCompressedCapBytes ||
                         _totalCompressedBytes >= TotalCompressedCapBytes))
                    {
                        job.State.FramesCapped = true;
                        if (!_capLogged)
                        {
                            _capLogged = true;
                            _logger?.Warn(
                                "Toast overlay track memory cap reached; the card freezes at its last frame in the clip.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Frame stays null; the export blit skips it (the previous frame holds).
                    _logger?.Debug(ex, "Toast overlay frame compression failed.");
                }
            }
        }
    }
}
