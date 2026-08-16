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
    /// frame per item; the UI thread only rasterizes into a pooled buffer and enqueues, and a
    /// single background worker owns everything else — the dedup memcmp, the XOR delta, the
    /// Deflate, and every append to the track — so the render tick pays rasterization cost and
    /// nothing more. Consecutive identical frames dedup by memcmp (static cards collapse to a
    /// handful of frames, GIF and countdown cards keep their real cadence). Memory is capped per
    /// track and per recorder: past a cap new frames stop (samples continue, so the card freezes
    /// at its last frame in the clip) with a single log line.
    ///
    /// A unique frame is normally stored as the XOR against the frame before it, with a whole keyframe
    /// every <see cref="KeyframeIntervalFrames"/>. An animating countdown bar makes nearly every sample
    /// unique while leaving almost all of the card untouched, so whole frames would burn the per-track
    /// budget partway through a clip on a full-bleed photographic background.
    ///
    /// Per-item ordering holds because jobs are FIFO through one queue served by a single worker; a
    /// second worker would need per-item partitioning before it could be added safely.
    /// </summary>
    internal sealed class ToastOverlayTrackRecorder
    {
        private const long PerTrackCompressedCapBytes = 48L * 1024 * 1024;
        private const long TotalCompressedCapBytes = 128L * 1024 * 1024;

        /// <summary>
        /// A whole keyframe is stored every this many frames. Bounds two things: how far export replays
        /// forward when it enters a track partway, and how long the card holds a stale image if one
        /// frame's compression fails and breaks the delta chain.
        /// </summary>
        private const int KeyframeIntervalFrames = 60;

        /// <summary>
        /// Rented buffers a card keeps around between rentals. Steady state circulates two or three
        /// (one being rendered into, one held as the dedup reference, the rest in flight); anything
        /// past this is a transient the pool need not retain.
        /// </summary>
        private const int MaxPooledBuffersPerItem = 8;

        private sealed class ItemState
        {
            public ToastOverlayTrack Track;

            /// <summary>UI thread only: whether any pixel-carrying job was ever enqueued.</summary>
            public bool HasEnqueuedPixels;

            /// <summary>Buffer pool, guarded by the recorder's queue lock.</summary>
            public Stack<byte[]> BufferPool = new Stack<byte[]>();

            /// <summary>Set under the queue lock by the worker, read under it by the UI thread.</summary>
            public bool FramesCapped;

            // Worker-only state below.
            public int DedupTicks;
            public int RefusedTicks;
            public byte[] LastRaw;
            public int LastFrameIndex = -1;
            public double FirstSampleMs;
            public bool HasFirstTick;
            public long CompressedBytes;
            public int FramesSinceKeyframe;
            public byte[] XorScratch;
        }

        private sealed class SampleJob
        {
            public ItemState State;

            /// <summary>Rendered pixels, or null for a repeat tick (backlog or cap skip).</summary>
            public byte[] Pixels;

            public int Width;
            public int Height;
            public double SlideXPhys;
            public double SlideYPhys;
            public double GlowScale;
            public int ClientW;
            public int ClientH;
            public double ElapsedMs;
        }

        private readonly ILogger _logger;
        private readonly double _sampleIntervalMs;
        private readonly bool _alignRight;
        private readonly bool _alignBottom;
        private readonly double _gapDip;
        private readonly double _monitorScale;

        /// <summary>
        /// Pixel-carrying jobs allowed in flight before <see cref="CanAcceptFrame"/> asks the caller
        /// to skip rasterization for a tick (the sample then repeats the previous frame). Bounds
        /// raw-buffer memory if the worker falls behind the render loop: half a second of frames.
        /// </summary>
        private readonly int _maxQueuedPixelJobs;

        private readonly Dictionary<AchievementToastViewModel, ItemState> _items =
            new Dictionary<AchievementToastViewModel, ItemState>();
        private readonly object _queueLock = new object();
        private readonly Queue<SampleJob> _pending = new Queue<SampleJob>();
        private Task _worker;
        private int _pendingPixelJobs;
        private long _totalCompressedBytes;
        private bool _capLogged;

        /// <param name="sampleIntervalMs">
        /// The interval the caller samples at (one recording frame). Sizes the pixel-job backlog cap
        /// and pads the last sample, so a track's duration covers the frame its final sample
        /// represents.
        /// </param>
        /// <param name="alignRight">Wave placement geometry, stamped on every track: the corner
        /// alignment and the DIP gap/monitor scale the export uses to compute where a lone toast of
        /// each frame's size would sit. Resolved once per wave, like the live placement.</param>
        public ToastOverlayTrackRecorder(
            ILogger logger, double sampleIntervalMs,
            bool alignRight, bool alignBottom, double gapDip, double monitorScale)
        {
            _logger = logger;
            _sampleIntervalMs = sampleIntervalMs > 0 ? sampleIntervalMs : 1;
            _maxQueuedPixelJobs = Math.Max(16, (int)Math.Round(1000.0 / _sampleIntervalMs / 2.0));
            _alignRight = alignRight;
            _alignBottom = alignBottom;
            _gapDip = gapDip;
            _monitorScale = monitorScale > 0 ? monitorScale : 1.0;
        }

        /// <summary>
        /// Whether the caller should rasterize this item's card for the current tick. False when the
        /// worker's pixel backlog is full or the item's frame budget is spent — the caller then
        /// records a pixel-less repeat sample instead, so the timeline never gaps, only pixel
        /// freshness degrades. A first frame is never refused for backlog: a track with samples but
        /// no frame at all would be useless. UI thread only.
        /// </summary>
        public bool CanAcceptFrame(AchievementToastViewModel vm)
        {
            if (vm == null)
            {
                return false;
            }

            var firstFrame = !_items.TryGetValue(vm, out var state) || !state.HasEnqueuedPixels;
            lock (_queueLock)
            {
                if (state != null && state.FramesCapped)
                {
                    return false;
                }

                return firstFrame || _pendingPixelJobs < _maxQueuedPixelJobs;
            }
        }

        /// <summary>
        /// A pixel buffer for the caller to render into and hand to <see cref="Sample"/>: a pooled
        /// one when a matching size is free, else fresh. Ownership passes back with the Sample call;
        /// the worker recycles it. UI thread only.
        /// </summary>
        public byte[] RentBuffer(AchievementToastViewModel vm, int length)
        {
            var state = vm != null ? GetOrCreateState(vm) : null;
            if (state != null)
            {
                lock (_queueLock)
                {
                    // Wrong-size leftovers (the card resized) are dropped rather than kept forever.
                    while (state.BufferPool.Count > 0)
                    {
                        var buffer = state.BufferPool.Pop();
                        if (buffer.Length == length)
                        {
                            return buffer;
                        }
                    }
                }
            }

            return new byte[length];
        }

        /// <summary>
        /// Records one tick of one card's animation: its rendered pixels, the slide transform's
        /// current value (physical pixels, sub-pixel), and the game client size. UI thread only.
        /// Null pixels record a repeat of the item's previous frame at this tick's position.
        /// </summary>
        /// <param name="elapsedMs">
        /// The composing frame's timestamp (the render tick's <c>RenderingTime</c>), in ms on any
        /// epoch the caller keeps stable for the wave; each item's samples are stored relative to its
        /// own first one. Supplied rather than read here because <c>Environment.TickCount</c> resolves
        /// to ~15.6 ms — coarser than a single frame at 60 fps, which would quantize the timeline.
        /// </param>
        public void Sample(
            AchievementToastViewModel vm, byte[] premulBgra, int width, int height,
            double slideXPhys, double slideYPhys, double glowScale,
            int clientW, int clientH, double elapsedMs)
        {
            if (vm == null || (premulBgra != null && (width <= 0 || height <= 0)))
            {
                return;
            }

            var state = GetOrCreateState(vm);
            if (premulBgra != null)
            {
                state.HasEnqueuedPixels = true;
            }

            lock (_queueLock)
            {
                if (premulBgra != null)
                {
                    _pendingPixelJobs++;
                }

                _pending.Enqueue(new SampleJob
                {
                    State = state,
                    Pixels = premulBgra,
                    Width = width,
                    Height = height,
                    SlideXPhys = slideXPhys,
                    SlideYPhys = slideYPhys,
                    GlowScale = glowScale,
                    ClientW = clientW,
                    ClientH = clientH,
                    ElapsedMs = elapsedMs,
                });
                if (_worker == null || _worker.IsCompleted)
                {
                    _worker = Task.Run(() => DrainQueue());
                }
            }
        }

        /// <summary>
        /// Stores the card's shadow/glow difference layer (with-effects render minus
        /// effects-stripped render, premultiplied BGRA at the card's pixel size). Compressed here,
        /// once per capture — a few milliseconds, paid before the wave's slide begins. UI thread
        /// only, and safe against the worker: the layer lives on the track object, which the
        /// worker never reads, and export runs only after the drain.
        /// </summary>
        public void SetShadowLayer(AchievementToastViewModel vm, byte[] premulBgraDelta, int width, int height)
        {
            if (vm == null || premulBgraDelta == null || width <= 0 || height <= 0)
            {
                return;
            }

            var state = GetOrCreateState(vm);
            try
            {
                state.Track.ShadowLayer =
                    ToastOverlayTrack.Frame.Compress(premulBgraDelta, width, height, isDelta: false);
            }
            catch (Exception ex)
            {
                // The clip then shows the card without its halo rather than failing the track.
                _logger?.Debug(ex, "Toast shadow layer compression failed.");
            }
        }

        /// <summary>
        /// Drains the sample queue and finalizes track durations. Call after sampling has
        /// stopped (the handler is detached); no further <see cref="Sample"/> calls may follow.
        /// Loops rather than awaiting one worker snapshot: an enqueue that raced a worker's exit
        /// can leave jobs queued with no live worker, and any sample left unprocessed would be
        /// missing from the clip.
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

                // One line per track per wave: how the animation's freshness survived capture. A
                // unique-frame rate far below the sample rate is what "slow" animation in a clip
                // is made of, and this line says which stage lost it — unchanged pixels mean the
                // live card itself wasn't animating any faster, refused ticks mean the worker's
                // backlog throttled rasterization.
                var duration = state.Track.DurationSeconds;
                _logger?.Info(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[Recording] Toast track '{0}': {1:0.00}s, {2} samples ({3:0.0}/s), {4} unique frames " +
                    "({5:0.0}/s), {6} unchanged, {7} refused, capped={8}",
                    state.Track.AchievementName,
                    duration,
                    samples.Count,
                    duration > 0 ? samples.Count / duration : 0,
                    state.Track.Frames.Count,
                    duration > 0 ? state.Track.Frames.Count / duration : 0,
                    state.DedupTicks,
                    state.RefusedTicks,
                    state.FramesCapped));
            }

            return tracks;
        }

        private ItemState GetOrCreateState(AchievementToastViewModel vm)
        {
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
                        AlignRight = _alignRight,
                        AlignBottom = _alignBottom,
                        GapDip = _gapDip,
                        MonitorScale = _monitorScale,
                    },
                };
                _items[vm] = state;
            }

            return state;
        }

        private void DrainQueue()
        {
            while (true)
            {
                SampleJob job;
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
                    ProcessJob(job);
                }
                catch (Exception ex)
                {
                    // A failed job degrades to a dropped tick; the track ships without it.
                    _logger?.Debug(ex, "Toast overlay sample job failed.");
                }
            }
        }

        private void ProcessJob(SampleJob job)
        {
            var state = job.State;
            if (!state.HasFirstTick)
            {
                state.FirstSampleMs = job.ElapsedMs;
                state.HasFirstTick = true;
            }

            var frameIndex = state.LastFrameIndex;
            if (job.Pixels != null)
            {
                bool capped;
                lock (_queueLock)
                {
                    _pendingPixelJobs--;
                    capped = state.FramesCapped;
                }

                if (capped || RawEquals(state.LastRaw, job.Pixels))
                {
                    state.DedupTicks++;
                    ReturnBuffer(state, job.Pixels);
                }
                else
                {
                    frameIndex = StoreFrame(state, job);
                }
            }
            else
            {
                state.RefusedTicks++;
            }

            if (frameIndex < 0)
            {
                // No frame stored yet (a first frame that failed to compress): a sample pointing at
                // nothing would be useless, so the tick is dropped.
                return;
            }

            state.Track.Samples.Add(new ToastOverlayTrack.Sample
            {
                ElapsedMs = (int)Math.Round(job.ElapsedMs - state.FirstSampleMs),
                FrameIndex = frameIndex,
                SlideXPhys = job.SlideXPhys,
                SlideYPhys = job.SlideYPhys,
                GlowScale = job.GlowScale,
                ClientW = job.ClientW,
                ClientH = job.ClientH,
            });
        }

        /// <summary>
        /// Compresses and appends one unique frame, fully formed — a Deflate failure appends nothing
        /// (the sample repeats the previous frame) rather than leaving a payload-less link that would
        /// break the delta chain for everything after it. Returns the index the sample should
        /// reference. Worker thread only.
        /// </summary>
        private int StoreFrame(ItemState state, SampleJob job)
        {
            // Store the XOR against the previous frame, except on the periodic keyframe or when
            // there is nothing valid to diff against (first frame, or the card changed size).
            var canDelta = state.LastRaw != null &&
                state.LastRaw.Length == job.Pixels.Length &&
                state.LastFrameIndex >= 0 &&
                state.FramesSinceKeyframe < KeyframeIntervalFrames;
            byte[] payload;
            if (canDelta)
            {
                if (state.XorScratch == null || state.XorScratch.Length != job.Pixels.Length)
                {
                    state.XorScratch = new byte[job.Pixels.Length];
                }

                XorInto(state.XorScratch, job.Pixels, state.LastRaw);
                payload = state.XorScratch;
            }
            else
            {
                payload = job.Pixels;
            }

            ToastOverlayTrack.Frame frame;
            try
            {
                frame = ToastOverlayTrack.Frame.Compress(payload, job.Width, job.Height, canDelta);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast overlay frame compression failed.");
                ReturnBuffer(state, job.Pixels);
                return state.LastFrameIndex;
            }

            state.Track.Frames.Add(frame);
            var frameIndex = state.Track.Frames.Count - 1;
            state.FramesSinceKeyframe = canDelta ? state.FramesSinceKeyframe + 1 : 0;

            var previous = state.LastRaw;
            state.LastRaw = job.Pixels;
            state.LastFrameIndex = frameIndex;
            if (previous != null)
            {
                ReturnBuffer(state, previous);
            }

            state.CompressedBytes += frame.Deflated.Length;
            lock (_queueLock)
            {
                _totalCompressedBytes += frame.Deflated.Length;
                if (!state.FramesCapped &&
                    (state.CompressedBytes >= PerTrackCompressedCapBytes ||
                     _totalCompressedBytes >= TotalCompressedCapBytes))
                {
                    state.FramesCapped = true;
                    if (!_capLogged)
                    {
                        _capLogged = true;
                        _logger?.Warn(
                            "Toast overlay track memory cap reached; the card freezes at its last frame in the clip.");
                    }
                }
            }

            return frameIndex;
        }

        private void ReturnBuffer(ItemState state, byte[] buffer)
        {
            lock (_queueLock)
            {
                if (state.BufferPool.Count < MaxPooledBuffersPerItem)
                {
                    state.BufferPool.Push(buffer);
                }
            }
        }

        /// <summary>
        /// The XOR of two equal-length card renders into a reusable scratch: zero everywhere the
        /// card did not change, which is most of it.
        /// </summary>
        private static void XorInto(byte[] destination, byte[] current, byte[] previous)
        {
            for (var i = 0; i < current.Length; i++)
            {
                destination[i] = (byte)(current[i] ^ previous[i]);
            }
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
    }
}
