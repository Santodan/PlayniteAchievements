using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The screenshot frame's chrome over the opening of a framed unlock clip: full opacity from
    /// <see cref="StartTicks"/>, fading out by <see cref="FramedClipTiming"/> at the end of its
    /// hold, or held to <see cref="EndTicks"/> when there is no hold. The chrome is converted for
    /// the decoded frame size once per export and reused by every re-encoded run.
    /// </summary>
    internal sealed class FrameChromeOverlaySource : IFrameOverlaySource
    {
        private const double OneSecond100ns = 10_000_000.0;

        private readonly FrameChromeImage _chrome;
        private readonly double? _holdSeconds;
        private StaticNv12Overlay _converted;
        private int _convertedW;
        private int _convertedH;

        /// <param name="startTicks">Base-clip tick the frame first shows on.</param>
        /// <param name="endTicks">Base-clip tick the frame last shows on (inclusive).</param>
        /// <param name="holdSeconds">How long the frame shows before it has faded; null for no fade.</param>
        public FrameChromeOverlaySource(FrameChromeImage chrome, long startTicks, long endTicks, double? holdSeconds)
        {
            _chrome = chrome;
            _holdSeconds = holdSeconds;
            StartTicks = startTicks;
            EndTicks = endTicks;
        }

        public long StartTicks { get; }

        public long EndTicks { get; }

        public IFrameOverlay CreateRenderer(int frameW, int frameH, int stride)
        {
            if (_converted == null || _convertedW != frameW || _convertedH != frameH)
            {
                _converted = StaticNv12Overlay.Create(_chrome?.Pixels, _chrome?.Width ?? 0, _chrome?.Height ?? 0, frameW, frameH);
                _convertedW = frameW;
                _convertedH = frameH;
            }

            return new Renderer(this, _converted, new OverlayCompositor(frameW, frameH, stride));
        }

        private sealed class Renderer : IFrameOverlay
        {
            private readonly FrameChromeOverlaySource _source;
            private readonly StaticNv12Overlay _overlay;
            private readonly OverlayCompositor _compositor;

            public Renderer(FrameChromeOverlaySource source, StaticNv12Overlay overlay, OverlayCompositor compositor)
            {
                _source = source;
                _overlay = overlay;
                _compositor = compositor;
            }

            public bool TryCompose(Sample frame, long baseTime)
            {
                if (_overlay == null || _overlay.Region.IsEmpty ||
                    baseTime < _source.StartTicks || baseTime > _source.EndTicks)
                {
                    return false;
                }

                var opacity = FramedClipTiming.Opacity(
                    (baseTime - _source.StartTicks) / OneSecond100ns, _source._holdSeconds);
                if (opacity <= 0)
                {
                    return false;
                }

                return _compositor.ComposeRegion(frame, _overlay.Region, (y, uv) => _overlay.Blend(y, uv, opacity));
            }
        }
    }
}
