using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class GridAlignmentMappingTests
    {
        [TestMethod]
        public void ToHorizontalAlignment_MapsAllGridAlignmentValues()
        {
            Assert.AreEqual(HorizontalAlignment.Left, GridAlignmentMapping.ToHorizontalAlignment(GridAlignment.Left));
            Assert.AreEqual(HorizontalAlignment.Center, GridAlignmentMapping.ToHorizontalAlignment(GridAlignment.Center));
            Assert.AreEqual(HorizontalAlignment.Right, GridAlignmentMapping.ToHorizontalAlignment(GridAlignment.Right));
        }

        [TestMethod]
        public void ToTextAlignment_MapsAllGridAlignmentValues()
        {
            Assert.AreEqual(TextAlignment.Left, GridAlignmentMapping.ToTextAlignment(GridAlignment.Left));
            Assert.AreEqual(TextAlignment.Center, GridAlignmentMapping.ToTextAlignment(GridAlignment.Center));
            Assert.AreEqual(TextAlignment.Right, GridAlignmentMapping.ToTextAlignment(GridAlignment.Right));
        }

        [TestMethod]
        public void ToVerticalAlignment_MapsAllGridVerticalAlignmentValues()
        {
            Assert.AreEqual(VerticalAlignment.Top, GridAlignmentMapping.ToVerticalAlignment(GridVerticalAlignment.Top));
            Assert.AreEqual(VerticalAlignment.Center, GridAlignmentMapping.ToVerticalAlignment(GridVerticalAlignment.Center));
            Assert.AreEqual(VerticalAlignment.Bottom, GridAlignmentMapping.ToVerticalAlignment(GridVerticalAlignment.Bottom));
        }
    }
}
