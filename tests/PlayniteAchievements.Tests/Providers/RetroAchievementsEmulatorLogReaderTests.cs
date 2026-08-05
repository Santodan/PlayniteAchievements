using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements.EmulatorLog;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class RetroAchievementsEmulatorLogReaderTests
    {
        private static RaEmulatorLogSession NewSession(string logPath, params string[] schemaIds)
        {
            return new RaEmulatorLogSession(logPath, RaEmulatorLogParseProfile.Rcheevos, schemaIds);
        }

        [TestMethod]
        public void TryRead_FirstReadOfCumulativeLog_SkipsHistoryAndEmitsNothing()
        {
            var path = Path.GetTempFileName();
            try
            {
                // Emulators such as PPSSPP append across launches, so the log already holds an earlier
                // run's award when this session starts. The first read must baseline past it.
                File.WriteAllText(
                    path,
                    "Game 3537 loaded, Hardcore disabled\n" +
                    "Awarding achievement 245100: Give Them Nothing\n");

                var session = NewSession(path, "245100");

                Assert.IsTrue(RaEmulatorLogReader.TryRead(session, out var observations));
                Assert.AreEqual(0, observations.Count, "Pre-existing awards must not be emitted.");
                Assert.IsTrue(session.Primed);
                Assert.AreEqual(new FileInfo(path).Length, session.ConsumedOffset);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void TryRead_AwardAppendedAfterPriming_IsEmittedOnce()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Awarding achievement 245100: Give Them Nothing\n");
                var session = NewSession(path, "245100");

                // First read primes past the stale award.
                Assert.IsTrue(RaEmulatorLogReader.TryRead(session, out _));

                // The genuine in-session unlock is appended after priming.
                File.AppendAllText(path, "Awarding achievement 245100: Give Them Nothing\n");

                Assert.IsTrue(RaEmulatorLogReader.TryRead(session, out var observations));
                Assert.AreEqual(1, observations.Count);
                Assert.AreEqual("245100", observations[0].ApiName);
                Assert.IsTrue(observations[0].Unlocked);

                // A subsequent read with no new bytes emits nothing.
                Assert.IsTrue(RaEmulatorLogReader.TryRead(session, out var none));
                Assert.AreEqual(0, none.Count);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void TryRead_AwardForIdOutsideSchema_IsIgnored()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "start\n");
                var session = NewSession(path, "245100");
                Assert.IsTrue(RaEmulatorLogReader.TryRead(session, out _));

                // An award for a different game's achievement (globally unique ids) must be filtered out.
                File.AppendAllText(path, "Awarding achievement 999999: Some Other Game\n");

                Assert.IsTrue(RaEmulatorLogReader.TryRead(session, out var observations));
                Assert.AreEqual(0, observations.Count);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
