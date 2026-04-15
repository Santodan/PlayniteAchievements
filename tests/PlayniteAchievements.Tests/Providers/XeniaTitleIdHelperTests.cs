using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Xenia;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class XeniaTitleIdHelperTests
    {
        [TestMethod]
        public void Normalize_AcceptsLowercaseAndPrefix()
        {
            var normalized = XeniaTitleIdHelper.Normalize("  0x4d5307e6  ");

            Assert.AreEqual("4D5307E6", normalized);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("4D5307E")]
        [DataRow("4D5307E60")]
        [DataRow("4D53ZZE6")]
        [DataRow("xyz")]
        public void Normalize_InvalidValues_ReturnsNull(string value)
        {
            Assert.IsNull(XeniaTitleIdHelper.Normalize(value));
        }
    }
}
