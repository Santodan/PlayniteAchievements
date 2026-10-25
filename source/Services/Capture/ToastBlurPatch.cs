using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Where a blurred toast card sits inside its rendered container and how far its blur reaches,
    /// for the outputs that draw the card themselves: the with-notification screenshot and the
    /// unlock clip. Their card renders leave out the live background, so each blurs its own frame
    /// under the card instead (<see cref="Apply"/> for the screenshot, Nv12CardBlur for the clip).
    ///
    /// The geometry is stored as fractions of the container, so it maps onto the container's
    /// overlay at whatever size an output draws it. Measured on the UI thread (the toast service
    /// builds it from the realized container); applied on the pool.
    /// </summary>
    internal sealed class ToastBlurPatch
    {
        /// <summary>The blur reach at full strength, in physical screen pixels.</summary>
        public const double MaxReachPhys = 48;

        /// <summary>The strength (0-100) a style without one blurs at.</summary>
        public const double DefaultStrength = 50;

        private readonly RectangleF _cardFraction;
        private readonly float _cornerRadiusFraction;

        /// <param name="cardFraction">The card's rect as fractions of the container's size.</param>
        /// <param name="cornerRadiusFraction">The card's corner radius as a fraction of the container's width.</param>
        /// <param name="reachPhys">How far the blur spreads, in physical screen pixels.</param>
        public ToastBlurPatch(RectangleF cardFraction, float cornerRadiusFraction, double reachPhys)
        {
            _cardFraction = cardFraction;
            _cornerRadiusFraction = cornerRadiusFraction;
            ReachPhys = reachPhys;
        }

        /// <summary>How far the blur spreads, in physical screen pixels.</summary>
        public double ReachPhys { get; }

        /// <summary>
        /// The blur reach for a style's strength (0-100, null for the default), in physical screen
        /// pixels. The single mapping the live background, the screenshot and the clip share.
        /// </summary>
        public static double ReachForStrength(double? strengthPercent)
        {
            var percent = Math.Max(0, Math.Min(100, strengthPercent ?? DefaultStrength));
            return percent / 100 * MaxReachPhys;
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
        /// physical screen pixel, which scales the reach.
        /// </summary>
        public void Apply(Bitmap shot, Rectangle overlayDest, double pxPerPhys)
        {
            var reach = ReachPhys * pxPerPhys;
            if (reach < 1)
            {
                return;
            }

            var card = MapCard(overlayDest);
            var radius = BoxBlur.RadiusFor(reach);

            // The sampled area reaches past the card so its edges blur against what surrounds them.
            var pad = radius * BoxBlur.Passes;
            var source = Rectangle.Intersect(
                Rectangle.Round(RectangleF.Inflate(card, pad, pad)),
                new Rectangle(0, 0, shot.Width, shot.Height));
            if (source.Width <= 0 || source.Height <= 0)
            {
                return;
            }

            using (var blurred = shot.Clone(source, PixelFormat.Format32bppArgb))
            {
                var data = blurred.LockBits(
                    new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    var bytes = new byte[data.Stride * source.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    BoxBlur.Apply(bytes, 0, source.Width, source.Height, data.Stride, 4, radius);
                    Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
                }
                finally
                {
                    blurred.UnlockBits(data);
                }

                // A texture fill of the rounded card antialiases its edge; a clip region would not.
                using (var g = Graphics.FromImage(shot))
                using (var brush = new TextureBrush(blurred, WrapMode.Clamp))
                using (var path = CreateRoundedRect(card, MapCornerRadius(overlayDest)))
                {
                    brush.TranslateTransform(source.X, source.Y);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.FillPath(brush, path);
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
