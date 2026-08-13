using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Accumulates one <see cref="ToastOverlayTrack"/> per toast item over a wave's on-screen
    /// lifetime. The toast pipeline calls <see cref="Sample"/> on the UI thread once per recording
    /// frame per item with the card's rendered pixels and client-relative rect; consecutive
    /// identical frames dedup by memcmp (static cards collapse to a handful of frames, GIF and
    /// countdown cards keep their real cadence), and unique frames Deflate-compress on a single
    /// background worker so the render tick never pays compression cost. Memory is capped per
    /// track and per recorder: past a cap new frames stop (samples continue, so the card freezes
    /// at its last frame in the clip) with a single log line.
    ///
    /// A unique frame is normally stored as the XOR against the frame before it, with a whole keyframe
    /// every <see cref="KeyframeIntervalFrames"/>. An animating countdown bar makes nearly every sample
    /// unique while leaving almost all of the card untouched, so whole frames would burn the per-track
    /// budget partway through a clip on a full-bleed photographic background. The XOR is computed here
    /// on the UI thread — this is where the previous frame's pixels already live for the dedup
    /// comparison — and only the Deflate runs on the worker.
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

        /// <summary>
        /// A whole keyframe is stored every this many frames. Bounds two things: how far export replays
        /// forward when it enters a track partway, and how long the card holds a stale image if one
        /// frame's compression fails and breaks the delta chain.
        /// </summary>
        private const int KeyframeIntervalFrames = 60;

        private sealed class ItemState
        {
            public ToastOverlayTrack Track;
            public byte[] LastRaw;
            public int LastFrameIndex = -1;
            public double FirstSampleMs;
            public bool HasFirstTick;
            public long CompressedBytes;
            public bool FramesCapped;
            public int FramesSinceKeyframe;
        }

        private sealed class CompressionJob
        {
            public ItemState State;
            public ToastOverlayTrack.Frame Frame;

            /// <summary>Whole pixels for a keyframe, or the XOR against the previous frame.</summary>
            public byte[] Payload;

            public bool IsDelta;
        }

        private readonly ILogger _logger;
        private readonly double _sampleIntervalMs;
        private readonly Dictionary<AchievementToastViewModel, ItemState> _items =
            new Dictionary<AchievementToastViewModel, ItemState>();
        private readonly object _queueLock = new object();
        private readonly Queue<CompressionJob> _pending = new Queue<CompressionJob>();
        private Task _worker;
        private long _totalCompressedBytes;
        private bool _capLogged;

        /// <param name="sampleIntervalMs">
        /// The interval the caller samples at (one recording frame). Used only as the trailing pad on
        /// the last sample, so a track's duration covers the frame its final sample represents.
        /// </param>
        public ToastOverlayTrackRecorder(ILogger logger, double sampleIntervalMs)
        {
            _logger = logger;
            _sampleIntervalMs = sampleIntervalMs > 0 ? sampleIntervalMs : 1;
        }

        /// <summary>
        /// Records one tick of one card's animation. UI thread only. The rect is the card's
        /// top-left relative to the game client rect plus the client size, all physical pixels.
        /// </summary>
        /// <param name="elapsedMs">
        /// The composing frame's timestamp (the render tick's <c>RenderingTime</c>), in ms on any
        /// epoch the caller keeps stable for the wave; each item's samples are stored relative to its
        /// own first one. Supplied rather than read here because <c>Environment.TickCount</c> resolves
        /// to ~15.6 ms — coarser than a single frame at 60 fps, which would quantize the timeline.
        /// </param>
        public void Sample(
            AchievementToastViewModel vm, byte[] premulBgra, int width, int height,
            int relX, int relY, int clientW, int clientH, double elapsedMs)
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
                        CaptureCorrelationId = vm.CaptureCorrelationId,
                        ProviderKey = vm.ProviderKey,
                        AchievementName = vm.AchievementName,
                        StartUtc = CaptureTimelineClock.UtcNow,
                    },
                };
                _items[vm] = state;
            }

            if (!state.HasFirstTick)
            {
                state.FirstSampleMs = elapsedMs;
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
                ElapsedMs = (int)Math.Round(elapsedMs - state.FirstSampleMs),
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
        /// Drains the compression queue and finalizes track durations. Call after sampling has
        /// stopped (the handler is detached); no further <see cref="Sample"/> calls may follow.
        /// Loops rather than awaiting one worker snapshot: an enqueue that raced a worker's exit
        /// can leave jobs queued with no live worker, and any frame left uncompressed would play
        /// back as a freeze in the clip.
        /// </summary>
        public async Task<IReadOnlyList<ToastOverlayTrack>> CompleteAsync()
        {
            while (true)
            {
                Task worker;
                lock (_queueLock)
                {
                    var workerDone = _worker == null || _worker.IsCompleted;
                    if (workerDone && _pending.Count == 0)
                    {
                        break;
                    }

                    if (workerDone)
                    {
                        _worker = Task.Run(() => DrainQueue());
                    }

                    worker = _worker;
                }

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

                state.Track.DurationSeconds = (samples[samples.Count - 1].ElapsedMs + _sampleIntervalMs) / 1000.0;
                tracks.Add(state.Track);
            }

            return tracks;
        }

        /// <summary>
        /// The XOR of two equal-length card renders: zero everywhere the card did not change, which is
        /// most of it. Cheap enough for the render tick (a few tens of microseconds on a card-sized
        /// buffer) and it keeps the raw pixels off the compression worker.
        /// </summary>
        private static byte[] Xor(byte[] current, byte[] previous)
        {
            var delta = new byte[current.Length];
            for (var i = 0; i < current.Length; i++)
            {
                delta[i] = (byte)(current[i] ^ previous[i]);
            }

            return delta;
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

                // Store the XOR against the previous frame, except on the periodic keyframe or when
                // there is nothing valid to diff against (first frame, or the card changed size).
                var canDelta = state.LastRaw != null &&
                    state.LastRaw.Length == raw.Length &&
                    state.LastFrameIndex >= 0 &&
                    state.FramesSinceKeyframe < KeyframeIntervalFrames;
                var payload = canDelta ? Xor(raw, state.LastRaw) : raw;

                var frame = new ToastOverlayTrack.Frame
                {
                    Width = width,
                    Height = height,
                    IsDelta = canDelta,
                };
                state.Track.Frames.Add(frame);
                frameIndex = state.Track.Frames.Count - 1;
                state.FramesSinceKeyframe = canDelta ? state.FramesSinceKeyframe + 1 : 0;
                _pending.Enqueue(new CompressionJob
                {
                    State = state,
                    Frame = frame,
                    Payload = payload,
                    IsDelta = canDelta,
                });
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
                    var compressed = ToastOverlayTrack.Frame.Compress(
                        job.Payload, job.Frame.Width, job.Frame.Height, job.IsDelta);
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
