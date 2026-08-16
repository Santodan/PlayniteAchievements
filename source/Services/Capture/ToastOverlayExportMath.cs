using System;
using System.Drawing;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure position synthesis for compositing a recorded toast card into decoded video frames:
    /// where a genuine lone toast of the current frame's size would sit (the same corner math the
    /// live placer uses), plus the slide transform's recorded offset interpolated to the output
    /// frame's instant. Measured screen geometry never enters — live window moves, placement
    /// corrections, and stacking cannot reach the clip. Kept free of Media Foundation so it
    /// unit-tests directly.
    /// </summary>
    internal static class ToastOverlayExportMath
    {
        /// <summary>
        /// The slide offset (physical pixels, sub-pixel) at <paramref name="secondsIntoTrack"/>:
        /// linearly interpolated between <paramref name="sampleIndex"/> and the next sample, so a
        /// clip frame that lands between two samples gets the motion the card actually had there
        /// rather than a stair-step. Clamped to the samples at the track's ends.
        /// </summary>
        public static void GetSlideOffset(
            ToastOverlayTrack track, int sampleIndex, double secondsIntoTrack,
            out double x, out double y)
        {
            var s0 = track.Samples[sampleIndex];
            x = s0.SlideXPhys;
            y = s0.SlideYPhys;
            if (sampleIndex + 1 >= track.Samples.Count)
            {
                return;
            }

            var s1 = track.Samples[sampleIndex + 1];
            var span = s1.ElapsedMs - s0.ElapsedMs;
            if (span <= 0)
            {
                return;
            }

            var t = (secondsIntoTrack * 1000.0 - s0.ElapsedMs) / span;
            t = Math.Max(0.0, Math.Min(1.0, t));
            x = s0.SlideXPhys + ((s1.SlideXPhys - s0.SlideXPhys) * t);
            y = s0.SlideYPhys + ((s1.SlideYPhys - s0.SlideYPhys) * t);
        }

        /// <summary>
        /// The shadow-layer multiplier at <paramref name="secondsIntoTrack"/>: linearly
        /// interpolated between the sample at <paramref name="sampleIndex"/> and the next, clamped
        /// at the track's ends — the same treatment the slide offset gets, so the glow pulse plays
        /// at the clip's full frame rate even where pixel frames repeat.
        /// </summary>
        public static double GetGlowScale(ToastOverlayTrack track, int sampleIndex, double secondsIntoTrack)
        {
            var s0 = track.Samples[sampleIndex];
            if (sampleIndex + 1 >= track.Samples.Count)
            {
                return s0.GlowScale;
            }

            var s1 = track.Samples[sampleIndex + 1];
            var span = s1.ElapsedMs - s0.ElapsedMs;
            if (span <= 0)
            {
                return s0.GlowScale;
            }

            var t = (secondsIntoTrack * 1000.0 - s0.ElapsedMs) / span;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return s0.GlowScale + ((s1.GlowScale - s0.GlowScale) * t);
        }

        /// <summary>
        /// The frame-space rect to blit the overlay into: the lone-toast corner for a card of the
        /// sample's recorded physical size plus the interpolated slide offset, scaled from client
        /// space into the video frame with one final rounding. The referenced pixel frame may be
        /// smaller than the card (captured at the clip's scale, or lower under load); the blit
        /// stretches it into this rect.
        /// </summary>
        public static Rectangle ComputeDestRect(
            ToastOverlayTrack track, int sampleIndex, double secondsIntoTrack,
            int frameW, int frameH)
        {
            var sample = track.Samples[sampleIndex];
            if (sample.ClientW <= 0 || sample.ClientH <= 0 ||
                sample.CardWPhys <= 0 || sample.CardHPhys <= 0)
            {
                return Rectangle.Empty;
            }

            ToastWindowPlacer.ComputeCorner(
                new Rectangle(0, 0, sample.ClientW, sample.ClientH), sample.CardWPhys, sample.CardHPhys,
                track.MonitorScale, track.AlignRight, track.AlignBottom, track.GapDip,
                out var cornerX, out var cornerY);
            GetSlideOffset(track, sampleIndex, secondsIntoTrack, out var slideX, out var slideY);
            return OverlayBlitMath.ScaleRect(
                cornerX + slideX, cornerY + slideY, sample.CardWPhys, sample.CardHPhys,
                sample.ClientW, sample.ClientH, frameW, frameH);
        }
    }
}
