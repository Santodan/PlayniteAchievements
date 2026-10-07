using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class InGameSessionUnlocksTests
    {
        [TestMethod]
        public void BaselineUnlocks_CannotReplayAfterRefreshRelocksAllAchievements()
        {
            var session = new InGameSessionUnlocks();
            var baseline = Enumerable.Range(1, 24).Select(i => "TFD_ACHIEVEMENT_" + i).ToArray();
            session.Remember(baseline);
            session.Remember(new string[0]); // Remote refresh reports everything locked.
            Assert.AreEqual(0, baseline.Count(key => session.Add(key)));
            Assert.IsTrue(session.Add("NEW_ACHIEVEMENT"));
            Assert.IsFalse(session.Add("new_achievement"));
        }

        [TestMethod]
        public void SilentFirstRead_IsRememberedAndSessionRestartGetsFreshState()
        {
            var session = new InGameSessionUnlocks();
            session.Remember(null);
            session.Remember(new[] { null, "", "OLD_UNLOCK" });
            Assert.IsFalse(session.Add("old_unlock"));
            Assert.IsTrue(session.Contains("OLD_UNLOCK"));
            Assert.IsTrue(new InGameSessionUnlocks().Add("OLD_UNLOCK"));
        }
    }
}
