using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.PSN;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class PsnNpCommIdHelperTests
    {
        [TestMethod]
        public void TryNormalize_AcceptsNpwrPattern_AndUppercases()
        {
            Assert.IsTrue(PsnNpCommIdHelper.TryNormalize("npwr12345_00", out var normalized));
            Assert.AreEqual("NPWR12345_00", normalized);
        }

        [TestMethod]
        public void TryNormalize_TrimsSurroundingWhitespace()
        {
            Assert.IsTrue(PsnNpCommIdHelper.TryNormalize("  NPWR00001_00  ", out var normalized));
            Assert.AreEqual("NPWR00001_00", normalized);
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("CUSA12345")]
        [DataRow("NPWR1234_00")]
        [DataRow("NPWR12345_0")]
        [DataRow("NPWR12345")]
        public void TryNormalize_RejectsInvalidValues(string value)
        {
            Assert.IsFalse(PsnNpCommIdHelper.TryNormalize(value, out var normalized));
            Assert.IsNull(normalized);
        }
    }
}
