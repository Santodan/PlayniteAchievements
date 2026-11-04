using System;
using System.Drawing;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure geometry for the live blurred toast background: which part of the capture texture to
    /// read for the cards' screen rect, how far to shrink it on the GPU, and where each card sits in
    /// the frame that comes back. Free of interop so it unit-tests directly.
    /// </summary>
    internal static class LiveBackdropMath
    {
        /// <summary>The deepest mip level read back: 1/16 of the region on each axis.</summary>
        public const int MaxMipLevel = 4;

        /// <summary>The smallest edge, in pixels, a read-back mip level may have.</summary>
        public const int MinMipEdge = 8;

        /// <summary>
        /// The screen rect to capture for cards covering <paramref name="cards"/>: grown by the blur's
        /// sampling reach, so the card edges blur against what surrounds them, and by
        /// <paramref name="motionPad"/>, so a card that moves before the next frame arrives is still
        /// inside the frame it is shown from.
        /// </summary>
        public static Rectangle RequestFor(Rectangle cards, double reachPhys, int motionPad)
        {
            if (cards.Width <= 0 || cards.Height <= 0)
            {
                return Rectangle.Empty;
            }

            var pad = (int)Math.Ceiling(Math.Max(0, reachPhys)) + Math.Max(0, motionPad);
            return Rectangle.Inflate(cards, pad, pad);
        }

        /// <summary>
        /// Projects <paramref name="screen"/> into a <paramref name="textureW"/> x
        /// <paramref name="textureH"/> capture texture whose top-left sits at screen point
        /// <paramref name="origin"/>, <paramref name="scale"/> texture pixels per screen pixel.
        /// A rect running off the texture is shifted back inside rather than shrunk, so the region's
        /// size (and the GPU textures sized to it) stays put while a card slides in from an edge.
        /// <paramref name="covered"/> is the screen rect the result actually spans. Empty when nothing
        /// can be read.
        /// </summary>
        public static Rectangle Project(
            Point origin, double scale, Rectangle screen, int textureW, int textureH, out Rectangle covered)
        {
            covered = Rectangle.Empty;
            if (scale <= 0 || textureW <= 0 || textureH <= 0 || screen.Width <= 0 || screen.Height <= 0)
            {
                return Rectangle.Empty;
            }

            var width = Math.Min(textureW, Math.Max(1, (int)Math.Round(screen.Width * scale)));
            var height = Math.Min(textureH, Math.Max(1, (int)Math.Round(screen.Height * scale)));
            var x = Clamp((int)Math.Round((screen.X - origin.X) * scale), 0, textureW - width);
            var y = Clamp((int)Math.Round((screen.Y - origin.Y) * scale), 0, textureH - height);

            covered = new Rectangle(
                origin.X + (int)Math.Round(x / scale),
                origin.Y + (int)Math.Round(y / scale),
                (int)Math.Round(width / scale),
                (int)Math.Round(height / scale));
            return new Rectangle(x, y, width, height);
        }

        /// <summary>
        /// How many times to halve a <paramref name="width"/> x <paramref name="height"/> region on the
        /// GPU before reading it back: as deep as a blur spreading <paramref name="reachPx"/> pixels
        /// hides, which is about half its reach per axis, without dropping an edge below
        /// <see cref="MinMipEdge"/>.
        /// </summary>
        public static int MipLevelFor(double reachPx, int width, int height)
        {
            var level = 0;
            while (level < MaxMipLevel &&
                   (1 << (level + 1)) <= reachPx / 2 &&
                   (width >> (level + 1)) >= MinMipEdge &&
                   (height >> (level + 1)) >= MinMipEdge)
            {
                level++;
            }

            return level;
        }

        /// <summary>
        /// Where <paramref name="card"/> sits in a frame spanning <paramref name="covered"/>, as
        /// fractions of the frame (an ImageBrush viewbox). A card reaching past the frame is shifted
        /// back inside, showing nearby blurred content rather than an empty edge.
        /// </summary>
        public static RectangleF Viewbox(Rectangle card, Rectangle covered)
        {
            if (covered.Width <= 0 || covered.Height <= 0 || card.Width <= 0 || card.Height <= 0)
            {
                return RectangleF.Empty;
            }

            var width = Math.Min(1f, (float)card.Width / covered.Width);
            var height = Math.Min(1f, (float)card.Height / covered.Height);
            var x = Math.Max(0f, Math.Min(1f - width, (float)(card.X - covered.X) / covered.Width));
            var y = Math.Max(0f, Math.Min(1f - height, (float)(card.Y - covered.Y) / covered.Height));
            return new RectangleF(x, y, width, height);
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
