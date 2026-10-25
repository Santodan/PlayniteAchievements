using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Where a blurred toast card sits inside its rendered container, for the outputs that draw the
    /// card themselves: the with-notification screenshot and the unlock clip. The live toast's blur
    /// is drawn by DWM and never reaches the card's own render, so without this those outputs would
    /// show the tint over a sharp game frame.
    ///
    /// The geometry is stored as fractions of the container, so it maps onto the container's
    /// overlay at whatever size an output draws it. Measured on the UI thread (the toast service
    /// builds it from the realized container); applied on the pool.
    /// </summary>
    internal sealed class ToastBlurPatch
    {
        // How far the screenshot is shrunk before being stretched back: the blur's reach in
        // physical pixels. The clip's box blur is sized to match (Nv12CardBlur).
        public const int ReachPhys = 16;

        private readonly RectangleF _cardFraction;
        private readonly float _cornerRadiusFraction;

        /// <param name="cardFraction">The card's rect as fractions of the container's size.</param>
        /// <param name="cornerRadiusFraction">The card's corner radius as a fraction of the container's width.</param>
        public ToastBlurPatch(RectangleF cardFraction, float cornerRadiusFraction)
        {
            _cardFraction = cardFraction;
            _cornerRadiusFraction = cornerRadiusFraction;
        }

        /// <summary>The card's rect for a container overlay drawn at <paramref name="overlayDest"/>.</summary>
        public RectangleF MapCard(RectangleF overlayDest)
        {
            return new RectangleF(
                overlayDest.X + _cardFraction.X * overlayDest.Width,
                overlayDest.Y + _cardFraction.Y * overlayDest.Height,
                _cardFraction.Width * overlayDest.Width,
                _cardFraction.Height * overlayDest.Height);
        }

        /// <summary>The card's corner radius for a container overlay drawn at <paramref name="overlayDest"/>.</summary>
        public float MapCornerRadius(RectangleF overlayDest)
        {
            return _cornerRadiusFraction * overlayDest.Width;
        }

        /// <summary>
        /// Blurs <paramref name="shot"/> under the card, for a container overlay drawn at
        /// <paramref name="overlayDest"/>; <paramref name="pxPerPhys"/> is the shot's pixels per
        /// physical screen pixel, which scales the blur's reach.
        /// </summary>
        public void Apply(Bitmap shot, Rectangle overlayDest, double pxPerPhys)
        {
            var card = MapCard(overlayDest);
            var factor = Math.Max(2, (int)Math.Round(ReachPhys * pxPerPhys));

            // The sampled area reaches past the card so its edges blur against what surrounds them.
            var pad = factor * 2;
            var source = Rectangle.Intersect(
                Rectangle.Round(RectangleF.Inflate(card, pad, pad)),
                new Rectangle(0, 0, shot.Width, shot.Height));
            if (source.Width <= 0 || source.Height <= 0)
            {
                return;
            }

            var smallWidth = Math.Max(1, source.Width / factor);
            var smallHeight = Math.Max(1, source.Height / factor);
            using (var small = new Bitmap(smallWidth, smallHeight, PixelFormat.Format32bppPArgb))
            using (var attributes = new ImageAttributes())
            {
                // Mirrored edges keep the stretch from fading toward transparent at the borders.
                attributes.SetWrapMode(WrapMode.TileFlipXY);

                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(
                        shot, new Rectangle(0, 0, smallWidth, smallHeight),
                        source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                }

                using (var g = Graphics.FromImage(shot))
                using (var clip = CreateRoundedRect(card, MapCornerRadius(overlayDest)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.SetClip(clip);
                    g.DrawImage(
                        small, source,
                        0, 0, smallWidth, smallHeight, GraphicsUnit.Pixel, attributes);
                }
            }
        }

        private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (diameter <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
