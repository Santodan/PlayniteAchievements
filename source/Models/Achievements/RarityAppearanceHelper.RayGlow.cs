using System.Collections.Generic;
using System.Windows.Media;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Brushes for the rays glow.
    ///
    /// A ray is built from several translucent copies of itself, each narrower, shorter and stronger
    /// than the last. Where they overlap they accumulate, so the ray comes out bright along its spine
    /// and falling away to nothing at its edges and tip — a soft ray drawn entirely with flat fills.
    ///
    /// It is done this way because the two things that would normally soften an edge are both barred
    /// here. A blur is a bitmap effect, and this layer moves every frame: WPF would re-render it to an
    /// intermediate surface per row per frame, which is the same cost that made a populated grid lag.
    /// And a gradient anchored at the middle of the subject only works when the rays are a circular fan
    /// around that point, which an earlier sunburst was: on a silhouette loop a corner arrow's base sits
    /// half again as far out as an edge arrow's, so any ramp that fades the edge arrows correctly has
    /// already run to nothing before the corner arrows even begin. Stacking copies measures the falloff
    /// along each arrow, so it stays right whatever shape the loop is.
    /// </summary>
    public static partial class RarityAppearanceHelper
    {
        /// <summary>One translucent copy of a ray: how wide, how far along, and in what.</summary>
        public sealed class RayGlowLayer
        {
            public RayGlowLayer(SolidColorBrush brush, double widthMultiplier, double heightFraction)
            {
                Brush = brush;
                WidthMultiplier = widthMultiplier;
                HeightFraction = heightFraction;
            }

            public SolidColorBrush Brush { get; }

            public double WidthMultiplier { get; }

            public double HeightFraction { get; }
        }

        /// <summary>The stack of copies that makes up a ray, widest and faintest first.</summary>
        public sealed class RayGlowPalette
        {
            public RayGlowPalette(IReadOnlyList<RayGlowLayer> layers)
            {
                Layers = layers;
            }

            public IReadOnlyList<RayGlowLayer> Layers { get; }
        }

        // Width multiplier, length fraction and alpha per copy, widest and faintest first.
        //
        // How soft a ray looks comes down to two things: how many copies there are, and how far the
        // widest sits from the narrowest. Every copy has a hard polygon edge, so the outermost one has
        // to be faint enough that its edge cannot be picked out — low single-digit percent — and there
        // have to be enough steps in between that no single one shows as a band.
        //
        // The outer few deliberately run past the gap to the next arrow and overlap it. Blurring
        // anything spreads it into its neighbours, so tails that stop dead at the gap would be the one
        // thing a real blur never does; where they cross they sum to a few percent and read as haze
        // between the rays. What has to stay inside the gap is everything bright enough to read as part
        // of a particular ray — see RayReadableAlpha.
        private static readonly double[] RayLayerWidths =
            { 9.00, 7.28, 5.56, 3.84, 2.13, 0.41 };

        private static readonly double[] RayLayerHeights =
            { 1.00, 0.82, 0.64, 0.46, 0.28, 0.10 };

        private static readonly byte[] RayLayerAlphas =
            { 0x05, 0x09, 0x18, 0x35, 0x61, 0x9F };

        /// <summary>
        /// Opacity at or above which a copy is taken to define the ray rather than to haze around it.
        /// Copies this strong have to stay within the gap to the next arrow, or the rays stop reading
        /// as separate; fainter ones are free to overlap.
        /// </summary>
        public const byte RayReadableAlpha = 0x1A;


        /// <summary>How far the innermost copy is lifted toward white, so the spine reads as heat
        /// rather than as more of the same color.</summary>
        private const double RayCoreWhiteBlend = 0.28;

        // Resolved from OnRender, so UI thread only and no lock is needed.
        private static readonly Dictionary<RarityTier, RayGlowPalette> RayPalettes =
            new Dictionary<RarityTier, RayGlowPalette>();

        private static RayGlowPalette _completedRayPalette;

        /// <summary>
        /// Bumped whenever the palettes are dropped. Lets a burst notice a recolor by comparing a number
        /// it already holds, so correctness does not depend on it having caught an event — a control
        /// that subscribed to a static event and missed its unsubscribe would live forever.
        /// </summary>
        internal static int RayGlowPaletteGeneration { get; private set; }

        public static RayGlowPalette GetRayGlowPalette(RarityTier tier, PersistedSettings settings = null)
        {
            if (RayPalettes.TryGetValue(tier, out var cached))
            {
                return cached;
            }

            var color = GetBaseColor(tier, settings);
            var palette = CreateRayGlowPalette(color, color);
            RayPalettes[tier] = palette;
            return palette;
        }

        /// <summary>
        /// Completion counterpart, taking the same two colors the completed-art bloom uses so the rays
        /// read as part of that bloom rather than as a separate effect sitting behind it.
        /// </summary>
        public static RayGlowPalette GetCompletedRayGlowPalette(PersistedSettings settings = null)
        {
            if (_completedRayPalette != null)
            {
                return _completedRayPalette;
            }

            _completedRayPalette = CreateRayGlowPalette(
                GetCompletedStartColor(settings),
                GetCompletedEndColor(settings));

            return _completedRayPalette;
        }

        internal static void ClearRayGlowPalettes()
        {
            RayPalettes.Clear();
            _completedRayPalette = null;
            RayGlowPaletteGeneration++;
        }

        private static RayGlowPalette CreateRayGlowPalette(Color outerColor, Color innerColor)
        {
            var layers = new RayGlowLayer[RayLayerWidths.Length];
            var last = layers.Length - 1;

            for (var i = 0; i < layers.Length; i++)
            {
                // Copies run outermost to innermost, so they cross from the outer color to the inner one
                // and the innermost also picks up the lift toward white.
                var blend = last > 0 ? i / (double)last : 1.0;
                var color = Lerp(outerColor, innerColor, blend);
                if (i == last)
                {
                    color = Color.FromRgb(
                        TowardWhite(color.R), TowardWhite(color.G), TowardWhite(color.B));
                }

                layers[i] = new RayGlowLayer(
                    CreateSolidBrush(Color.FromArgb(RayLayerAlphas[i], color.R, color.G, color.B)),
                    RayLayerWidths[i],
                    RayLayerHeights[i]);
            }

            return new RayGlowPalette(layers);
        }

        private static Color Lerp(Color from, Color to, double amount)
        {
            return Color.FromRgb(
                (byte)(from.R + ((to.R - from.R) * amount)),
                (byte)(from.G + ((to.G - from.G) * amount)),
                (byte)(from.B + ((to.B - from.B) * amount)));
        }

        private static byte TowardWhite(byte channel)
        {
            return (byte)(channel + ((255 - channel) * RayCoreWhiteBlend));
        }
    }
}
