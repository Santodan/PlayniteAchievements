using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.ShadPS4;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ShadPS4MatchIdHelperTests
    {
        [DataTestMethod]
        [DataRow("  cusa00432  ", "CUSA00432", "TitleId")]
        [DataRow("  npwr12345_00  ", "NPWR12345_00", "NpCommId")]
        public void Normalize_ValidValues_ReturnsNormalizedMatchId(string input, string expected, string expectedKind)
        {
            var normalized = ShadPS4MatchIdHelper.Normalize(input);

            Assert.AreEqual(expected, normalized);
            Assert.AreEqual(expectedKind, ShadPS4MatchIdHelper.GetKind(normalized).ToString());
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("CUSA0432")]
        [DataRow("CUSA004321")]
        [DataRow("NPWR1234_00")]
        [DataRow("NPWR12345_0")]
        [DataRow("NPXS12345_00")]
        [DataRow("4D5307E6")]
        [DataRow("invalid")]
        public void Normalize_InvalidValues_ReturnsNull(string input)
        {
            Assert.IsNull(ShadPS4MatchIdHelper.Normalize(input));
            Assert.AreEqual(ShadPS4MatchIdKind.None, ShadPS4MatchIdHelper.GetKind(input));
        }
    }
}
