using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    /// <summary>
    /// Pins the contract the rotating ray burst layer depends on: Common has no art (matching the
    /// soft glow), every other tier does, and the art is frozen so it can be shared across the
    /// per-row instances a virtualized grid creates.
    /// </summary>
    [TestClass]
    public class RarityRayBurstArtTests
    {
        [TestMethod]
        public void GetRayBurstImage_ReturnsNoArtForCommon()
        {
            Assert.IsNull(
                RarityAppearanceHelper.GetRayBurstImage(RarityTier.Common),
                "Common has no rarity glow, so it must have no sunburst either.");
        }

        [TestMethod]
        public void GetRayBurstImage_ReturnsFrozenArtForGlowingTiers()
        {
            foreach (var tier in new[] { RarityTier.Uncommon, RarityTier.Rare, RarityTier.UltraRare })
            {
                var image = RarityAppearanceHelper.GetRayBurstImage(tier);

                Assert.IsNotNull(image, $"{tier} should have sunburst art.");
                Assert.IsTrue(image.IsFrozen, $"{tier} sunburst art should be frozen for sharing.");
            }
        }

        [TestMethod]
        public void GetCompletedRayBurstImage_ReturnsFrozenArt()
        {
            var image = RarityAppearanceHelper.GetCompletedRayBurstImage();

            Assert.IsNotNull(image);
            Assert.IsTrue(image.IsFrozen);
        }

        [TestMethod]
        public void GetRayBurstImage_TracksConfiguredTierColor()
        {
            var settings = new PersistedSettings();
            settings.RarityColors.UltraRare = "#FF00FF00";

            var image = RarityAppearanceHelper.GetRayBurstImage(RarityTier.UltraRare, settings);

            Assert.IsNotNull(image);
            Assert.IsTrue(
                ContainsColorChannel(image, green: true),
                "The burst should be drawn in the configured tier color.");
        }

        /// <summary>
        /// Walks the generated drawing for a brush whose color carries the expected channel, which is
        /// enough to prove the tier color reached the art without pinning exact gradient stops.
        /// </summary>
        private static bool ContainsColorChannel(DrawingImage image, bool green)
        {
            var group = image.Drawing as DrawingGroup;
            Assert.IsNotNull(group, "The sunburst should be a DrawingGroup.");

            foreach (var drawing in group.Children)
            {
                if (!(drawing is GeometryDrawing geometry))
                {
                    continue;
                }

                if (geometry.Brush is GradientBrush gradient)
                {
                    foreach (var stop in gradient.GradientStops)
                    {
                        if (green && stop.Color.G > stop.Color.R && stop.Color.G > stop.Color.B)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
