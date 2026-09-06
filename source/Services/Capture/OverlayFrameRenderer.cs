using System;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Composites one achievement's toast track onto decoded base-clip frames, one frame at a
    /// time, walking forward in output time. Holds the caches the walk relies on — the last
    /// reconstructed card frame (the track stores frames as XOR deltas), the inflated shadow
    /// layer, the two ray layers being crossfaded and the scratch they blend into — so the
    /// per-frame cost is one inflate-and-XOR at most. Created per re-encoded run by
    /// <see cref="ToastOverlaySource"/>, for the whole-clip pass and the spliced runs alike.
    /// </summary>
    internal sealed class OverlayFrameRenderer : IFrameOverlay
    {
        private const long OneSecond100ns = 10_000_000L;

        private readonly ToastOverlayTrack _track;
        private readonly OverlayCompositor _compositor;
        private readonly long _toastStart;
        private readonly long _toastEnd;
        private readonly int _frameW;
        private readonly int _frameH;

        private byte[] _inflated;
        private int _inflatedIndex = -1;

        // The card's shadow/glow halo, captured effect-free at record time and re-applied here per
        // output frame as frame + layer × interpolated scale (the recorded pixels carry no
        // effects). Inflated once for the whole export; composited into a scratch so the XOR
        // reconstruction buffer stays pristine. The ray-burst layers work the same way but are
        // timed: the cursor advances with output time and the current layer is inflated on change.
        private readonly ToastOverlayTrack.Frame _shadowLayer;
        private readonly byte[] _shadowPixels;
        private byte[] _glowScratch;

        // Adjacent ray layers are crossfaded by output time — the rays drift slowly, so the blend
        // reads as smooth motion at a fraction of the capture rate. Two layers stay inflated: the
        // current one and the next, promoted forward as the cursor advances.
        private int _rayCursor = -1;
        private int _rayIndex = -1;
        private byte[] _rayPixels;
        private int _rayNextIndex = -1;
        private byte[] _rayNextPixels;

        /// <param name="toastStart">Base-clip tick the card first shows on.</param>
        /// <param name="toastEnd">Base-clip tick the card last shows on (inclusive).</param>
        public OverlayFrameRenderer(
            ToastOverlayTrack track, long toastStart, long toastEnd, int frameW, int frameH, int stride)
        {
            _track = track;
            _toastStart = toastStart;
            _toastEnd = toastEnd;
            _frameW = frameW;
            _frameH = frameH;
            _compositor = new OverlayCompositor(frameW, frameH, stride);
            _shadowLayer = track.ShadowLayer;
            _shadowPixels = _shadowLayer?.Inflate();
        }

        /// <summary>Whether a frame at <paramref name="baseTime"/> falls inside the card interval.</summary>
        public bool Covers(long baseTime)
        {
            return baseTime >= _toastStart && baseTime <= _toastEnd;
        }

        /// <summary>
        /// Blends the card into <paramref name="frame"/> in place when <paramref name="baseTime"/>
        /// falls inside the card interval and the track has a frame for it; returns whether it did.
        /// </summary>
        public bool TryCompose(Sample frame, long baseTime)
        {
            if (!Covers(baseTime))
            {
                return false;
            }

            var secondsIntoTrack = (baseTime - _toastStart) / (double)OneSecond100ns;
            var sampleIndex = _track.FindSampleIndexAtOrBefore(secondsIntoTrack);
            if (sampleIndex < 0 || !TryGetOverlay(sampleIndex, out var overlayFrame))
            {
                return false;
            }

            // Pixels hold at the nearest-previous sample; the position is synthesized (lone-toast
            // corner + slide offset) and interpolated to this frame's instant, so motion stays
            // smooth even where pixel frames repeat.
            var destRect = ToastOverlayExportMath.ComputeDestRect(
                _track, sampleIndex, secondsIntoTrack, _frameW, _frameH);

            _rayCursor = ToastOverlayExportMath.FindRayLayerAtOrBefore(_track, secondsIntoTrack, _rayCursor);
            var displayIndex = _track.RayLayers.Count > 0 ? Math.Max(0, _rayCursor) : -1;
            if (displayIndex >= 0 && displayIndex != _rayIndex)
            {
                _rayPixels = displayIndex == _rayNextIndex
                    ? _rayNextPixels
                    : _track.RayLayers[displayIndex].Layer?.Inflate();
                _rayIndex = displayIndex;
            }

            var followIndex = displayIndex >= 0 && displayIndex + 1 < _track.RayLayers.Count
                ? displayIndex + 1
                : -1;
            if (followIndex >= 0 && followIndex != _rayNextIndex)
            {
                _rayNextPixels = _track.RayLayers[followIndex].Layer?.Inflate();
                _rayNextIndex = followIndex;
            }

            var composeRays = _rayIndex >= 0 && _rayPixels != null &&
                LayerMatchesFrame(_track.RayLayers[_rayIndex].Layer, overlayFrame) &&
                _rayPixels.Length == _inflated.Length;
            var composeShadow = _shadowPixels != null &&
                _shadowLayer.Width == overlayFrame.Width &&
                _shadowLayer.Height == overlayFrame.Height &&
                _shadowPixels.Length == _inflated.Length;

            var overlayPixels = _inflated;
            if (composeRays || composeShadow)
            {
                if (_glowScratch == null || _glowScratch.Length != _inflated.Length)
                {
                    _glowScratch = new byte[_inflated.Length];
                }

                Buffer.BlockCopy(_inflated, 0, _glowScratch, 0, _inflated.Length);
                if (composeRays)
                {
                    var hostOpacity = ToastOverlayExportMath.GetHostOpacity(_track, sampleIndex, secondsIntoTrack);
                    var blend = ToastOverlayExportMath.GetRayLayerBlend(_track, _rayCursor, secondsIntoTrack);
                    var blendNext = blend > 0 && followIndex >= 0 && _rayNextPixels != null &&
                        LayerMatchesFrame(_track.RayLayers[followIndex].Layer, overlayFrame) &&
                        _rayNextPixels.Length == _inflated.Length;
                    OverlayBlitMath.AddScaled(
                        _glowScratch, _rayPixels, hostOpacity * (blendNext ? 1.0 - blend : 1.0));
                    if (blendNext)
                    {
                        OverlayBlitMath.AddScaled(_glowScratch, _rayNextPixels, hostOpacity * blend);
                    }
                }

                if (composeShadow)
                {
                    OverlayBlitMath.AddScaled(
                        _glowScratch, _shadowPixels,
                        ToastOverlayExportMath.GetGlowScale(_track, sampleIndex, secondsIntoTrack));
                }

                overlayPixels = _glowScratch;
            }

            return _compositor.Compose(frame, overlayPixels, overlayFrame.Width, overlayFrame.Height, destRect);
        }

        /// <summary>
        /// The track frame for a sample, reconstructed lazily and cached (tracks are sampled per
        /// recording frame, but consecutive samples share a frame whenever the card's pixels did not
        /// change, and frames are stored as XOR deltas against their predecessor). Samples are walked
        /// forward, so the usual cost is one inflate-and-XOR onto the frame already in hand.
        ///
        /// A broken chain — a frame whose compression failed — falls back to the previously
        /// reconstructed frame so the card holds instead of flickering out, and recovers at the track's
        /// next keyframe.
        /// </summary>
        private bool TryGetOverlay(int sampleIndex, out ToastOverlayTrack.Frame frame)
        {
            frame = null;
            var frameIndex = _track.Samples[sampleIndex].FrameIndex;
            if (frameIndex < 0 || frameIndex >= _track.Frames.Count)
            {
                frameIndex = _inflatedIndex;
            }

            if (frameIndex < 0)
            {
                return false;
            }

            // Keep the last good reconstruction so a failure can fall back to it: TryReconstructFrame
            // clears the index it is handed when it gives up partway.
            var held = _inflated;
            var heldIndex = _inflatedIndex;
            if (_track.TryReconstructFrame(frameIndex, ref _inflated, ref _inflatedIndex))
            {
                frame = _track.Frames[frameIndex];
                return _inflated != null;
            }

            _inflated = held;
            _inflatedIndex = heldIndex;
            if (_inflatedIndex < 0 || _inflated == null)
            {
                return false;
            }

            frame = _track.Frames[_inflatedIndex];
            return frame != null;
        }

        private static bool LayerMatchesFrame(ToastOverlayTrack.Frame layer, ToastOverlayTrack.Frame frame)
        {
            return layer != null && frame != null &&
                layer.Width == frame.Width && layer.Height == frame.Height;
        }
    }
}
