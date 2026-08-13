using System;
using System.Drawing;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>Which window rect a capture texture was matched to.</summary>
    internal enum CaptureAnchor
    {
        None = 0,
        FrameBounds,
        WindowRect
    }

    /// <summary>
    /// The relation between screen coordinates and the pixels of a window's capture texture: the
    /// screen point the texture's top-left corner sits at, and the factor screen lengths are
    /// multiplied by to reach texture lengths. Projecting a screen rect through it lands on the
    /// same content in the texture.
    /// </summary>
    internal readonly struct CaptureMapping
    {
        public CaptureMapping(Point origin, double scale, CaptureAnchor anchor)
        {
            Origin = origin;
            Scale = scale;
            Anchor = anchor;
        }

        public Point Origin { get; }

        public double Scale { get; }

        public CaptureAnchor Anchor { get; }

        public bool IsValid => Scale > 0 && Anchor != CaptureAnchor.None;

        /// <summary>
        /// Projects a screen rect into texture pixels, clamped inside a texture of the given size.
        /// <paramref name="evenDimensions"/> rounds the result down to even width and height, which
        /// H.264 requires of an encoded frame and a still image does not.
        /// </summary>
        public Rectangle Project(Rectangle screenRect, int capturedW, int capturedH, bool evenDimensions)
        {
            var minimum = evenDimensions ? 2 : 1;
            var x = Clamp((int)Math.Round((screenRect.X - Origin.X) * Scale), 0, capturedW - minimum);
            var y = Clamp((int)Math.Round((screenRect.Y - Origin.Y) * Scale), 0, capturedH - minimum);
            var width = Clamp((int)Math.Round(screenRect.Width * Scale), minimum, capturedW - x);
            var height = Clamp((int)Math.Round(screenRect.Height * Scale), minimum, capturedH - y);
            return evenDimensions
                ? new Rectangle(x, y, Even(width), Even(height))
                : new Rectangle(x, y, width, height);
        }

        public override string ToString()
        {
            return IsValid
                ? $"anchor={Anchor} scale={Scale:0.###}"
                : "anchor=none";
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private static int Even(int value)
        {
            return value & ~1;
        }
    }

    /// <summary>
    /// Pure geometry for locating a window's client area inside the texture WGC captures of it.
    /// Kept free of interop so it unit-tests directly — every case it handles was a field report.
    ///
    /// The texture is a uniformly scaled copy of some window rect, but neither term is known up
    /// front:
    ///
    /// Which rect. It may be the DWM extended frame bounds, or the outer window rect — which also
    /// spans the invisible resize border (nothing at the top, several pixels at the sides and
    /// bottom, itself DPI-scaled). Measuring against the wrong one shifts the crop down and runs it
    /// past the client's bottom edge: the top of the picture is cut off and a strip of bottom
    /// chrome stays in frame.
    ///
    /// What scale. Normally 1. But a DPI-unaware application on a scaled display renders into a
    /// surface at its own unscaled size which DWM stretches for display, so the texture can be a
    /// uniformly smaller copy of the window — a 1280x720 surface behind a 1920x1080 window.
    /// Ignoring that keeps a magnified corner of the picture.
    ///
    /// Both are recovered the same way: a candidate rect describes the texture only if it maps onto
    /// it by one scale on <em>both</em> axes. The candidate whose axes agree most closely wins, and
    /// its scale and origin place the client area. When no candidate agrees, nothing reliably maps
    /// the client area into the texture — the window resized out from under the measurement, say —
    /// and the whole frame is kept rather than a crop derived from a relation never established.
    ///
    /// All rects must arrive in one coordinate space (physical pixels, read per-monitor-aware).
    /// Mixing a virtualized client rect with physical DWM bounds is wrong before any of the above.
    /// </summary>
    internal static class CaptureCropMath
    {
        /// <summary>
        /// How far apart the two axes' scale factors may be, relative to the larger, before a
        /// candidate is judged not to describe the texture. Absorbs rounding on odd sizes without
        /// admitting a rect whose aspect genuinely differs.
        /// </summary>
        public const double UniformScaleTolerance = 0.02;

        /// <summary>
        /// The client-area sub-region of a captured texture, in texture pixels. Falls back to the
        /// whole frame whenever the geometry does not establish where the client area lands.
        /// </summary>
        /// <param name="capturedW">Texture width.</param>
        /// <param name="capturedH">Texture height.</param>
        /// <param name="rects">The window's rectangles, measured in one coordinate space.</param>
        /// <param name="evenDimensions">
        /// Round the crop down to even width and height, as an H.264 encode requires and a still
        /// image does not.
        /// </param>
        public static Rectangle ClientCrop(int capturedW, int capturedH, WindowRects rects, bool evenDimensions)
        {
            var fullFrame = FullFrame(capturedW, capturedH, evenDimensions);
            if (fullFrame.IsEmpty)
            {
                return Rectangle.Empty;
            }

            var client = rects.ClientArea;
            var mapping = ResolveMapping(capturedW, capturedH, rects.FrameBounds, rects.OuterRect);
            return mapping.IsValid && client.Width > 0 && client.Height > 0
                ? mapping.Project(client, capturedW, capturedH, evenDimensions)
                : fullFrame;
        }

        /// <summary>The whole texture. Empty when there is none.</summary>
        public static Rectangle FullFrame(int capturedW, int capturedH, bool evenDimensions)
        {
            var minimum = evenDimensions ? 2 : 1;
            if (capturedW < minimum || capturedH < minimum)
            {
                return Rectangle.Empty;
            }

            return evenDimensions
                ? new Rectangle(0, 0, capturedW & ~1, capturedH & ~1)
                : new Rectangle(0, 0, capturedW, capturedH);
        }

        /// <summary>
        /// The screen-to-texture mapping, from whichever candidate rect describes the texture most
        /// consistently. Invalid when neither does.
        /// </summary>
        public static CaptureMapping ResolveMapping(
            int capturedW, int capturedH, Rectangle frameBounds, Rectangle windowRect)
        {
            var hasFrame = TryMapCandidate(capturedW, capturedH, frameBounds, out var frameScale, out var frameSkew);
            var hasWindow = TryMapCandidate(capturedW, capturedH, windowRect, out var windowScale, out var windowSkew);
            if (hasFrame && (!hasWindow || frameSkew <= windowSkew))
            {
                return new CaptureMapping(frameBounds.Location, frameScale, CaptureAnchor.FrameBounds);
            }

            return hasWindow
                ? new CaptureMapping(windowRect.Location, windowScale, CaptureAnchor.WindowRect)
                : default;
        }

        /// <summary>
        /// Whether a candidate maps onto the texture by one scale on both axes, yielding that scale
        /// and how far the axes disagreed — the smaller the skew, the better the candidate
        /// describes the texture.
        /// </summary>
        private static bool TryMapCandidate(
            int capturedW, int capturedH, Rectangle candidate, out double scale, out double skew)
        {
            scale = 0;
            skew = double.MaxValue;
            if (candidate.Width <= 0 || candidate.Height <= 0 || capturedW <= 0 || capturedH <= 0)
            {
                return false;
            }

            var sx = (double)capturedW / candidate.Width;
            var sy = (double)capturedH / candidate.Height;
            var relative = Math.Abs(sx - sy) / Math.Max(sx, sy);
            if (relative > UniformScaleTolerance)
            {
                return false;
            }

            scale = (sx + sy) / 2;
            skew = relative;
            return true;
        }
    }
}
