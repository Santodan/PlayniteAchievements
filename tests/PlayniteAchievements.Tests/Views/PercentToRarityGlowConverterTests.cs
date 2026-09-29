using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class PercentToRarityGlowConverterTests
    {
        [TestMethod]
        public void Convert_ReturnsGlowForRarityTier()
        {
            var converter = new PercentToRarityGlowConverter();

            var result = converter.Convert(RarityTier.Rare, null, null, null);

            Assert.IsInstanceOfType(result, typeof(DropShadowEffect));
        }

        [TestMethod]
        public void Convert_ReturnsNullGlowForCommonRarityTier()
        {
            var converter = new PercentToRarityGlowConverter();

            var result = converter.Convert(RarityTier.Common, null, null, null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void Convert_ReturnsNullForPercentInput()
        {
            var converter = new PercentToRarityGlowConverter();

            var result = converter.Convert(5.0d, null, null, null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void TiersConvert_ReturnsGlowForSelectedCommonWithRarityData()
        {
            var converter = new RarityGlowForTiersConverter();

            var result = converter.Convert(
                new object[] { RarityTier.Common, RaritySelection.Common, true }, null, "20", null);

            Assert.IsInstanceOfType(result, typeof(DropShadowEffect));
        }

        [TestMethod]
        public void TiersConvert_ReturnsNullForSelectedCommonWithoutRarityData()
        {
            var converter = new RarityGlowForTiersConverter();

            var result = converter.Convert(
                new object[] { RarityTier.Common, RaritySelection.Common, false }, null, "20", null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void TiersConvert_ReturnsNullForUnselectedCommon()
        {
            var converter = new RarityGlowForTiersConverter();

            var result = converter.Convert(
                new object[] { RarityTier.Common, RaritySelectionExtensions.DefaultSoftGlowTiers, true }, null, "20", null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ShineConvert_ReturnsBorderBrushForCommonRarityTier()
        {
            var converter = new RarityToShineBrushConverter();

            var result = converter.Convert(RarityTier.Common, null, null, null);

            Assert.IsInstanceOfType(result, typeof(LinearGradientBrush));
        }
    }
}
