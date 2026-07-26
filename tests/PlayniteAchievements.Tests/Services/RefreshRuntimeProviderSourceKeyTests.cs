using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class RefreshRuntimeProviderSourceKeyTests
    {
        [TestMethod]
        public void HasProviderSourceKeyChanged_MissingPrevious_ReportsChanged()
        {
            Assert.IsTrue(RefreshRuntime.HasProviderSourceKeyChanged(null, "NPWR00033_00"));
            Assert.IsTrue(RefreshRuntime.HasProviderSourceKeyChanged(string.Empty, "NPWR00033_00"));
            Assert.IsTrue(RefreshRuntime.HasProviderSourceKeyChanged("   ", "NPWR00033_00"));
        }

        [TestMethod]
        public void HasProviderSourceKeyChanged_EqualIgnoringCaseAndWhitespace_ReportsUnchanged()
        {
            Assert.IsFalse(RefreshRuntime.HasProviderSourceKeyChanged("NPWR00033_00", "npwr00033_00"));
            Assert.IsFalse(RefreshRuntime.HasProviderSourceKeyChanged(" NPWR00033_00 ", "NPWR00033_00"));
        }

        [TestMethod]
        public void HasProviderSourceKeyChanged_DifferentKeys_ReportsChanged()
        {
            Assert.IsTrue(RefreshRuntime.HasProviderSourceKeyChanged("NPWR00033_00", "NPWR00011_00"));
            Assert.IsTrue(RefreshRuntime.HasProviderSourceKeyChanged(
                "NPWR01341_00+NPWR01433_00",
                "NPWR01341_00+NPWR01433_00+NPWR01435_00"));
        }

        [TestMethod]
        public void HasProviderSourceKeyChanged_MissingCurrent_ReportsUnchanged()
        {
            Assert.IsFalse(RefreshRuntime.HasProviderSourceKeyChanged("NPWR00033_00", null));
            Assert.IsFalse(RefreshRuntime.HasProviderSourceKeyChanged(null, string.Empty));
        }
    }
}
