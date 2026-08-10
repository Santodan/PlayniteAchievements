using System.Collections.Generic;
using System.Windows.Media;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Brushes for the rays glow.
    ///
    /// The rays fade toward their tips through two fill passes rather than a gradient. A gradient
    /// anchored at the middle of the subject only works when the rays are a circular fan around that
    /// point, which is what an earlier sunburst was: on a silhouette loop, a corner arrow's base sits
    /// half again as far out as an edge arrow's, so any ramp that fades the edge arrows correctly has
    /// already run to nothing before the corner arrows even begin. Measuring the fade along each arrow
    /// instead keeps it right whatever shape the loop is.
    /// </summary>
    public static partial class RarityAppearanceHelper
    {
        /// <summary>The two fills that make up a ray: a wide soft one at full length, and a narrow
        /// bright one that stops short.</summary>
        public sealed class RayGlowPalette
        {
            public RayGlowPalette(SolidColorBrush halo, SolidColorBrush core)
            {
                Halo = halo;
                Core = core;
            }

            public SolidColorBrush Halo { get; }

            public SolidColorBrush Core { get; }
        }

        private const byte RayHaloAlpha = 0x46;
        private const byte RayCoreAlpha = 0xB0;

        /// <summary>How far the bright core is lifted toward white, so it reads as heat rather than
        /// just more of the same color.</summary>
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

        private static RayGlowPalette CreateRayGlowPalette(Color haloColor, Color coreColor)
        {
            return new RayGlowPalette(
                CreateSolidBrush(Color.FromArgb(RayHaloAlpha, haloColor.R, haloColor.G, haloColor.B)),
                CreateSolidBrush(Color.FromArgb(
                    RayCoreAlpha,
                    TowardWhite(coreColor.R),
                    TowardWhite(coreColor.G),
                    TowardWhite(coreColor.B))));
        }

        private static byte TowardWhite(byte channel)
        {
            return (byte)(channel + ((255 - channel) * RayCoreWhiteBlend));
        }
    }
}
