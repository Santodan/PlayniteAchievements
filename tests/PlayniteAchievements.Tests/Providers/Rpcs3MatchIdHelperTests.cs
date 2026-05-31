using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RPCS3;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class Rpcs3MatchIdHelperTests
    {
        [DataTestMethod]
        [DataRow("NPWR12345_00", "NPWR12345_00")]
        [DataRow(" npwr12345_00 ", "NPWR12345_00")]
        public void Normalize_ValidNpCommId_ReturnsCanonicalValue(string input, string expected)
        {
            Assert.AreEqual(expected, Rpcs3MatchIdHelper.Normalize(input));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("NPEB01947")]
        [DataRow("CUSA00432")]
        [DataRow("NPWR1234_00")]
        [DataRow("NPWR123456_00")]
        [DataRow("NPWR12345")]
        public void Normalize_InvalidValue_ReturnsNull(string input)
        {
            Assert.IsNull(Rpcs3MatchIdHelper.Normalize(input));
        }
    }
}
