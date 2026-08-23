using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Tests.Services.Capture
{
    [TestClass]
    public class CaptureWorkloadPolicyTests
    {
        [DataTestMethod]
        [DataRow(10, 3)]
        [DataRow(30, 8)]
        [DataRow(60, 15)]
        public void MaximumCatchUpFrames_CoversAtLeastAQuarterSecond(int fps, int expected)
        {
            Assert.AreEqual(expected, CaptureWorkloadPolicy.MaximumCatchUpFrames(fps));
        }

        [DataTestMethod]
        [DataRow(30)]
        [DataRow(60)]
        public void ShouldResynchronize_AllowsBoundaryButRejectsTheNextFrame(int fps)
        {
            var maximum = CaptureWorkloadPolicy.MaximumCatchUpFrames(fps);

            Assert.IsFalse(CaptureWorkloadPolicy.ShouldResynchronize(100 + maximum, 100, fps));
            Assert.IsTrue(CaptureWorkloadPolicy.ShouldResynchronize(101 + maximum, 100, fps));
        }

        [TestMethod]
        public void ShouldResynchronize_NeverTreatsAheadOrCurrentWriterAsDebt()
        {
            Assert.IsFalse(CaptureWorkloadPolicy.ShouldResynchronize(50, 50, 60));
            Assert.IsFalse(CaptureWorkloadPolicy.ShouldResynchronize(49, 50, 60));
        }

        [TestMethod]
        public void FrameInterval_UsesTicksInsteadOfMillisecondRounding()
        {
            Assert.AreEqual(TimeSpan.TicksPerSecond / 60, CaptureWorkloadPolicy.FrameInterval(60).Ticks);
            Assert.AreEqual(TimeSpan.TicksPerSecond, CaptureWorkloadPolicy.FrameInterval(0).Ticks);
        }

        [TestMethod]
        public void CaptureSourceInterval_StaysSlightlyAheadOfTheConsumer()
        {
            var source = CaptureWorkloadPolicy.CaptureSourceInterval(60);
            var consumer = CaptureWorkloadPolicy.FrameInterval(60);

            Assert.IsTrue(source < consumer);
            Assert.IsTrue(source > TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 67));
        }
    }
}
