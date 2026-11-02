using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CompletionNotificationPolicyTests
    {
        [TestMethod]
        public void CapstoneWithoutGroups_SkipsStandalone()
        {
            Assert.IsFalse(CompletionNotificationPolicy.SendsStandalone(true, true, null));
        }

        [TestMethod]
        public void BaseGroupCapstone_SkipsStandalone()
        {
            Assert.IsFalse(CompletionNotificationPolicy.SendsStandalone(true, true, "Base"));
        }

        [TestMethod]
        public void BaseUpdateCapstone_SkipsStandalone()
        {
            Assert.IsFalse(CompletionNotificationPolicy.SendsStandalone(true, true, "Base, Update"));
        }

        [TestMethod]
        public void DlcCapstone_SendsStandalone()
        {
            Assert.IsTrue(CompletionNotificationPolicy.SendsStandalone(true, true, "DLC"));
        }

        [TestMethod]
        public void SubsetCapstone_SendsStandalone()
        {
            Assert.IsTrue(CompletionNotificationPolicy.SendsStandalone(true, true, "Subset"));
        }

        [TestMethod]
        public void RegularUnlock_SendsStandalone()
        {
            Assert.IsTrue(CompletionNotificationPolicy.SendsStandalone(true, false, null));
        }

        [TestMethod]
        public void SettingOff_SendsStandalone()
        {
            Assert.IsTrue(CompletionNotificationPolicy.SendsStandalone(false, true, null));
        }
    }
}
