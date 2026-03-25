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
        public void Convert_ReturnsNullForPercentInput()
        {
            var converter = new PercentToRarityGlowConverter();

            var result = converter.Convert(5.0d, null, null, null);

            Assert.IsNull(result);
        }
    }
}
