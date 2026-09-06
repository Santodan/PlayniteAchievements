using System.Collections.Generic;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Something drawn onto decoded base-clip frames over one interval of the base clip's
    /// timeline. The interval is what the splice plans around (frames outside every overlay's
    /// interval are stream-copied); the renderer is created per re-encoded run once the decoded
    /// frame geometry is known, so its per-frame caches live exactly as long as one forward walk.
    /// </summary>
    internal interface IFrameOverlaySource
    {
        /// <summary>First base-clip tick this overlay changes.</summary>
        long StartTicks { get; }

        /// <summary>Last base-clip tick this overlay changes, inclusive.</summary>
        long EndTicks { get; }

        IFrameOverlay CreateRenderer(int frameW, int frameH, int stride);
    }

    /// <summary>Draws one overlay onto frames of a fixed geometry, walked forward in time.</summary>
    internal interface IFrameOverlay
    {
        /// <summary>
        /// A new sample with the overlay drawn onto <paramref name="source"/>, or null to leave
        /// the frame as it is. Never modifies or disposes the source.
        /// </summary>
        Sample TryCompose(Sample source, long baseTime);
    }

    /// <summary>
    /// The overlays of one export applied in order to each frame, each drawing onto the previous
    /// one's result, so any number of cards or other marks compose without knowing about each
    /// other. Frames no overlay touches pass through untouched.
    /// </summary>
    internal sealed class FrameOverlayStack
    {
        private readonly List<IFrameOverlay> _overlays;

        private FrameOverlayStack(List<IFrameOverlay> overlays)
        {
            _overlays = overlays;
        }

        public static FrameOverlayStack Create(
            IReadOnlyList<IFrameOverlaySource> sources, int frameW, int frameH, int stride)
        {
            var overlays = new List<IFrameOverlay>(sources.Count);
            foreach (var source in sources)
            {
                overlays.Add(source.CreateRenderer(frameW, frameH, stride));
            }

            return new FrameOverlayStack(overlays);
        }

        /// <summary>The changed intervals the splice must re-encode, one per overlay.</summary>
        public static List<OverlaySplicePlan.Interval> ChangedIntervals(IReadOnlyList<IFrameOverlaySource> sources)
        {
            var intervals = new List<OverlaySplicePlan.Interval>(sources.Count);
            foreach (var source in sources)
            {
                intervals.Add(new OverlaySplicePlan.Interval(source.StartTicks, source.EndTicks));
            }

            return intervals;
        }

        /// <summary>
        /// The composited frame, or null when no overlay drew on it. The source is never modified
        /// or disposed; intermediate results are.
        /// </summary>
        public Sample TryCompose(Sample source, long baseTime)
        {
            Sample current = null;
            foreach (var overlay in _overlays)
            {
                var next = overlay.TryCompose(current ?? source, baseTime);
                if (next == null)
                {
                    continue;
                }

                current?.Dispose();
                current = next;
            }

            return current;
        }
    }

    /// <summary>One achievement's toast card, shown from its recorded track over a base-clip interval.</summary>
    internal sealed class ToastOverlaySource : IFrameOverlaySource
    {
        private readonly ToastOverlayTrack _track;

        public ToastOverlaySource(ToastOverlayTrack track, long startTicks, long endTicks)
        {
            _track = track;
            StartTicks = startTicks;
            EndTicks = endTicks;
        }

        public long StartTicks { get; }

        public long EndTicks { get; }

        public IFrameOverlay CreateRenderer(int frameW, int frameH, int stride)
        {
            return new OverlayFrameRenderer(_track, StartTicks, EndTicks, frameW, frameH, stride);
        }
    }
}
