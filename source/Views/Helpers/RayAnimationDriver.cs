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
    }

    /// <summary>
    /// The single composition tick behind every ray burst on screen.
    ///
    /// One hook for the whole application, attached when the first burst subscribes and detached the
    /// moment the last one leaves, so an application with the effect switched off — or simply scrolled
    /// away from any qualifying row — is not paying for a per-frame callback at all.
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
        private const double TargetFramesPerSecond = 30.0;

        private const double DueToleranceMs = 1.0;

        // Seconds for one lap of the silhouette, at the slow and fast ends of the shared glow speed
        // setting. The visual beat is a lap divided by the arrow count, since the standing wave makes
        // the picture repeat that often — a couple of seconds at the default speed.
        private const double SlowLapSeconds = 72.0;
        private const double FastLapSeconds = 5.0;

        private static readonly List<IRayAnimationTarget> Subscribers = new List<IRayAnimationTarget>();
        private static readonly List<IRayAnimationTarget> Dispatch = new List<IRayAnimationTarget>();

        private static Dispatcher _owner;
        private static TimeSpan _lastRenderingTime = TimeSpan.MinValue;
        private static double _nextDueMs;

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
            if (Subscribers.Count != 1)
            {
                return;
            }

            _owner = dispatcher;
            _lastRenderingTime = TimeSpan.MinValue;
            _nextDueMs = GlowAnimationClock.ElapsedMilliseconds;
            CompositionTarget.Rendering += OnRendering;
        }

        public static void Unsubscribe(IRayAnimationTarget target)
        {
            if (target == null || !Subscribers.Remove(target))
            {
                return;
            }

            if (Subscribers.Count > 0)
            {
                return;
            }

            CompositionTarget.Rendering -= OnRendering;
            _owner = null;
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

            var nowMs = GlowAnimationClock.ElapsedMilliseconds;
            if (nowMs < _nextDueMs - DueToleranceMs)
            {
                return;
            }

            // Step the due time forward in whole intervals rather than from now, so a stall resumes on
            // cadence instead of bunching the frames it missed.
            var intervalMs = 1000.0 / TargetFramesPerSecond;
            do
            {
                _nextDueMs += intervalMs;
            }
            while (_nextDueMs <= nowMs);

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
                    if (target.WantsRayFrames)
                    {
                        target.OnRayFrame();
                    }
                    else
                    {
                        Unsubscribe(target);
                    }
                }
            }

            Dispatch.Clear();
        }
    }
}
