using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Something the driver invalidates each frame.
    /// </summary>
    internal interface IRayAnimationTarget
    {
        /// <summary>
        /// Re-checked every tick, so a target that has stopped qualifying is dropped even if its own
        /// unsubscribe never ran. Grid rows are recycled without always raising Unloaded, and a burst
        /// left on the tick while drawing nothing is the exact failure the removed attempts hit.
        /// </summary>
        bool WantsRayFrames { get; }

        void OnRayFrame();

        /// <summary>
        /// Keeps receiving frames while <see cref="GlowAnimationGate"/> is suspended (content shown
        /// over a running game, such as the unlock notification card).
        /// </summary>
        bool IgnoresGlowGate { get; }

        /// <summary>
        /// Served at every composed frame while a held rate is set, where the other targets are
        /// served at that rate. For a target whose frame costs little.
        /// </summary>
        bool TakesEveryHeldFrame { get; }
    }

    /// <summary>
    /// A target whose frame has a part that can be worked out on any thread. The driver begins
    /// every such target's frame on the UI thread, fills them all at once across threads, and
    /// ends each on the UI thread again: a wave of several notification cards then waits for the
    /// longest of its bursts' fills, where one after another it waited for their sum
    /// (tools/capture-harness README.md, "Waves of several cards").
    /// </summary>
    internal interface IRayBatchTarget
    {
        /// <summary>UI thread. False when this frame has nothing to fill; OnRayFrame is then called.</summary>
        bool BeginRayFrame();

        /// <summary>Any thread, at the same time as other targets'. Touches only the target's own buffers.</summary>
        void FillRayFrame();

        /// <summary>UI thread, after every begun target has been filled.</summary>
        void EndRayFrame();
    }

    /// <summary>
    /// The single composition tick behind every animated rarity glow on screen: the ray bursts and
    /// the phase-locked glow pulse (<see cref="RarityGlowPulse"/>).
    ///
    /// One hook for the whole application, attached when the first target subscribes and detached the
    /// moment the last one leaves, so an application with the effect switched off — or simply scrolled
    /// away from any qualifying row — is not paying for a per-frame callback at all. It also detaches
    /// while <see cref="GlowAnimationGate"/> is suspended and no exempt target remains, since a
    /// subscribed Rendering handler alone keeps WPF composing at the display's refresh rate.
    ///
    /// The tick only invalidates. Phase comes from <see cref="GlowAnimationClock"/>, never accumulated
    /// across frames, which is what keeps separate bursts in step, lets a recycled row resume mid-cycle,
    /// and makes a surface that renders once offscreen produce the same frame as one that has been
    /// ticking for an hour.
    /// </summary>
    internal static class RayAnimationDriver
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(RayAnimationDriver));

        /// <summary>
        /// Invalidations per second. Below the composition rate on purpose — the effect is a slow drift
        /// and does not need every frame. Deliberately not Timeline.DesiredFrameRate, which does not
        /// throttle this and can cost the whole composition tick.
        /// </summary>
        private const double DefaultFramesPerSecond = 30.0;

        /// <summary>
        /// The overlay-track sampling rate while a toast wave is being recorded, 0 otherwise. Sampling
        /// above the invalidation rate would store duplicate ray frames with beat-dependent phase —
        /// judder in the exported clip — so the driver ticks at whichever rate is higher for the span
        /// of the recording. UI thread only, like the subscriber list.
        /// </summary>
        private static double _samplingFps;

        private const double DueToleranceMs = 1.0;

        // Seconds for one lap of the silhouette, at the slow and fast ends of the shared glow speed
        // setting. The visual beat is a lap divided by the arrow count, since the standing wave makes
        // the picture repeat that often — a couple of seconds at the default speed.
        private const double SlowLapSeconds = 72.0;
        private const double FastLapSeconds = 5.0;

        private static readonly List<IRayAnimationTarget> Subscribers = new List<IRayAnimationTarget>();
        private static readonly List<IRayAnimationTarget> Dispatch = new List<IRayAnimationTarget>();

        // The targets whose frames were begun this tick and are filled together.
        private static readonly List<IRayBatchTarget> Batch = new List<IRayBatchTarget>();
        private static readonly Action<int> FillBatched = index => Batch[index].FillRayFrame();

        private static Dispatcher _owner;
        private static TimeSpan _lastRenderingTime = TimeSpan.MinValue;
        private static double _nextDueMs;
        private static bool _hooked;

        /// <summary>
        /// Advances once per served tick. Targets that cache per-frame work key it on this value.
        /// </summary>
        internal static int FrameNumber { get; private set; }

        static RayAnimationDriver()
        {
            GlowAnimationGate.Changed += (s, e) => UpdateHook();
        }

        /// <summary>
        /// Targets currently on the render tick. This list holds strong references, and a WPF
        /// child references its parent, so a target stranded here roots its whole ancestor
        /// chain (grid row, grid, hosting control). Diagnostics only.
        /// </summary>
        internal static int SubscriberCount => Subscribers.Count;

        public static void Subscribe(IRayAnimationTarget target)
        {
            if (target == null || Subscribers.Contains(target))
            {
                return;
            }

            // Rendering is per-dispatcher. Everything here lives on Playnite's UI thread, so one static
            // hook covers every surface; refuse a target from elsewhere rather than adding it to a list
            // that would never tick.
            var dispatcher = Dispatcher.CurrentDispatcher;
            if (_owner != null && !ReferenceEquals(_owner, dispatcher))
            {
                return;
            }

            Subscribers.Add(target);
            _owner = dispatcher;
            UpdateHook();
        }

        /// <summary>Raises the tick rate to the given track sampling rate for a recording's span.</summary>
        public static void SetSamplingFps(double fps)
        {
            _samplingFps = fps > 0 && !double.IsNaN(fps) && !double.IsInfinity(fps) ? fps : 0;
        }

        public static void ClearSamplingFps()
        {
            _samplingFps = 0;
        }

        /// <summary>
        /// Raises the tick rate while a notification rests with its rays in a layer of their own,
        /// and serves the targets that take every held frame at every composed frame.
        /// Measured there (tools/capture-harness README.md, "The plugin's presenter and hold
        /// layers" and "The rays' cost"): 60 against 30 took the same processor time and the same
        /// game frame rate. A recording's sampling rate takes its place while one is set.
        /// </summary>
        public static void SetHeldFps(double fps)
        {
            _heldFps = fps > 0 && !double.IsNaN(fps) && !double.IsInfinity(fps) ? fps : 0;
        }

        public static void ClearHeldFps()
        {
            _heldFps = 0;
        }

        private static double _heldFps;

        public static void Unsubscribe(IRayAnimationTarget target)
        {
            if (target == null || !Subscribers.Remove(target))
            {
                return;
            }

            if (Subscribers.Count == 0)
            {
                _owner = null;
            }

            UpdateHook();
        }

        private static bool WantsHook()
        {
            if (Subscribers.Count == 0)
            {
                return false;
            }

            if (!GlowAnimationGate.IsSuspended)
            {
                return true;
            }

            for (var i = 0; i < Subscribers.Count; i++)
            {
                if (Subscribers[i].IgnoresGlowGate)
                {
                    return true;
                }
            }

            return false;
        }

        private static void UpdateHook()
        {
            var wants = WantsHook();
            if (wants == _hooked)
            {
                return;
            }

            _hooked = wants;
            if (wants)
            {
                _lastRenderingTime = TimeSpan.MinValue;
                _nextDueMs = GlowAnimationClock.ElapsedMilliseconds;
                CompositionTarget.Rendering += OnRendering;
            }
            else
            {
                CompositionTarget.Rendering -= OnRendering;
            }
        }

        /// <summary>Seconds for one full lap of the loop, from the shared glow speed setting.</summary>
        public static double LapPeriodMs(PersistedSettings persisted)
        {
            var speed = persisted?.RarityGlowPulseSpeed ?? 0.5;
            if (double.IsNaN(speed) || double.IsInfinity(speed))
            {
                speed = 0.5;
            }

            speed = Math.Max(0.0, Math.Min(1.0, speed));
            return (SlowLapSeconds - (speed * (SlowLapSeconds - FastLapSeconds))) * 1000.0;
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            // WPF raises Rendering more than once for a single composed frame, with the same
            // RenderingTime. Used only to spot the repeat; it is never the animation's clock.
            var renderingTime = (e as RenderingEventArgs)?.RenderingTime;
            if (renderingTime.HasValue)
            {
                if (renderingTime.Value <= _lastRenderingTime)
                {
                    return;
                }

                _lastRenderingTime = renderingTime.Value;
            }

            // Stand down for a notification slide's span: every invalidation here costs a burst
            // re-render on the frame the slide needs. Skipping before the due-time math means the
            // catch-up loop below treats the quiet span exactly like a stall and resumes on
            // cadence; phase comes from GlowAnimationClock per tick, so no drift accumulates.
            if (RenderQuietGate.IsEngaged)
            {
                return;
            }

            var nowMs = GlowAnimationClock.ElapsedMilliseconds;
            var due = nowMs >= _nextDueMs - DueToleranceMs;
            var everyFrame = _samplingFps <= 0 && _heldFps > 0;
            if (!due && !everyFrame)
            {
                return;
            }

            if (due)
            {
                // Step the due time forward in whole intervals rather than from now, so a stall
                // resumes on cadence instead of bunching the frames it missed.
                var intervalMs = 1000.0 / Math.Max(DefaultFramesPerSecond, _samplingFps > 0 ? _samplingFps : _heldFps);
                do
                {
                    _nextDueMs += intervalMs;
                }
                while (_nextDueMs <= nowMs);

                // Bursts that share a shape build their arrow geometry once for the whole frame,
                // keyed on this number.
                FrameNumber = unchecked(FrameNumber + 1);
            }

            var suspended = GlowAnimationGate.IsSuspended;

            // A target may unsubscribe itself while being served, so walk a copy.
            Dispatch.Clear();
            Dispatch.AddRange(Subscribers);

            using (PerfScope.PerfTracingEnabled
                ? PerfScope.Start(Logger, "rayglow.tick", 4, "bursts=" + Dispatch.Count)
                : null)
            {
                for (var i = 0; i < Dispatch.Count; i++)
                {
                    var target = Dispatch[i];
                    if (suspended && !target.IgnoresGlowGate)
                    {
                        // Held still, not dropped: it resumes when the gate opens.
                        continue;
                    }

                    if (!due && !target.TakesEveryHeldFrame)
                    {
                        continue;
                    }

                    if (target.WantsRayFrames)
                    {
                        if (target is IRayBatchTarget batched && batched.BeginRayFrame())
                        {
                            Batch.Add(batched);
                        }
                        else
                        {
                            target.OnRayFrame();
                        }
                    }
                    else
                    {
                        Unsubscribe(target);
                    }
                }

                if (Batch.Count == 1)
                {
                    Batch[0].FillRayFrame();
                }
                else if (Batch.Count > 1)
                {
                    System.Threading.Tasks.Parallel.For(0, Batch.Count, FillBatched);
                }

                for (var i = 0; i < Batch.Count; i++)
                {
                    Batch[i].EndRayFrame();
                }

                Batch.Clear();
            }

            Dispatch.Clear();
        }
    }
}
