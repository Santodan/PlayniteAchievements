using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class ToastOverlayExportMathTests
    {
        private const int ClientW = 1920;
        private const int ClientH = 1080;
        private const int CardW = 420;
        private const int CardH = 130;

        private static ToastOverlayTrack BottomRightTrack(double gapDip = 24.0, double monitorScale = 1.0)
        {
            return new ToastOverlayTrack
            {
                AlignRight = true,
                AlignBottom = true,
                GapDip = gapDip,
                MonitorScale = monitorScale,
            };
        }

        private static void AddSample(
            ToastOverlayTrack track, int elapsedMs, double slideX = 0, double slideY = 0)
        {
            track.Samples.Add(new ToastOverlayTrack.Sample
            {
                ElapsedMs = elapsedMs,
                FrameIndex = 0,
                SlideXPhys = slideX,
                SlideYPhys = slideY,
                ClientW = ClientW,
                ClientH = ClientH,
            });
        }

        // === Slide offset interpolation ===

        [TestMethod]
        public void GetSlideOffset_ExactSampleInstant_ReturnsThatSample()
        {
            var track = BottomRightTrack();
            AddSample(track, 0, slideY: 150);
            AddSample(track, 33, slideY: 90);

            ToastOverlayExportMath.GetSlideOffset(track, 0, 0.0, out var x, out var y);

            Assert.AreEqual(0.0, x);
            Assert.AreEqual(150.0, y);
        }

        [TestMethod]
        public void GetSlideOffset_BetweenSamples_InterpolatesLinearly()
        {
            var track = BottomRightTrack();
            AddSample(track, 0, slideY: 100);
            AddSample(track, 40, slideY: 60);

            // Halfway through the 40 ms span.
            ToastOverlayExportMath.GetSlideOffset(track, 0, 0.020, out _, out var y);

            Assert.AreEqual(80.0, y, 1e-9);
        }

        [TestMethod]
        public void GetSlideOffset_QueryPastNextSample_ClampsToNextSample()
        {
            var track = BottomRightTrack();
            AddSample(track, 0, slideY: 100);
            AddSample(track, 40, slideY: 60);

            // A query beyond the pair's span must not extrapolate past the next sample.
            ToastOverlayExportMath.GetSlideOffset(track, 0, 0.100, out _, out var y);

            Assert.AreEqual(60.0, y, 1e-9);
        }

        [TestMethod]
        public void GetSlideOffset_LastSample_HoldsWithoutExtrapolating()
        {
            var track = BottomRightTrack();
            AddSample(track, 0, slideY: 100);
            AddSample(track, 40, slideY: 60);

            ToastOverlayExportMath.GetSlideOffset(track, 1, 5.0, out _, out var y);

            Assert.AreEqual(60.0, y, 1e-9);
        }

        [TestMethod]
        public void GetSlideOffset_SingleSample_ReturnsIt()
        {
            var track = BottomRightTrack();
            AddSample(track, 0, slideX: 12.5, slideY: 7.25);

            ToastOverlayExportMath.GetSlideOffset(track, 0, 1.0, out var x, out var y);

            Assert.AreEqual(12.5, x);
            Assert.AreEqual(7.25, y);
        }

        // === Destination rect synthesis ===

        [TestMethod]
        public void ComputeDestRect_BottomRightAtRest_MatchesHandCornerMath()
        {
            var track = BottomRightTrack();
            AddSample(track, 0);

            var rect = ToastOverlayExportMath.ComputeDestRect(
                track, 0, 0.0, CardW, CardH, ClientW, ClientH);

            // Corner math: right/bottom edge less the gap less the card size.
            Assert.AreEqual(ClientW - CardW - 24, rect.X);
            Assert.AreEqual(ClientH - CardH - 24, rect.Y);
            Assert.AreEqual(CardW, rect.Width);
            Assert.AreEqual(CardH, rect.Height);
        }

        [TestMethod]
        public void ComputeDestRect_HoldPhase_IsIdenticalForEveryInstant()
        {
            // The defect this pins down: the card must not move during the hold. With constant
            // dims and zero slide offsets, every output instant must synthesize the same rect.
            var track = BottomRightTrack();
            for (var ms = 0; ms <= 4000; ms += 33)
            {
                AddSample(track, ms);
            }

            var expected = ToastOverlayExportMath.ComputeDestRect(
                track, 0, 0.0, CardW, CardH, ClientW, ClientH);
            for (var frame = 0; frame < 240; frame++)
            {
                var t = frame / 60.0;
                var index = track.FindSampleIndexAtOrBefore(t);
                var rect = ToastOverlayExportMath.ComputeDestRect(
                    track, index, t, CardW, CardH, ClientW, ClientH);
                Assert.AreEqual(expected, rect, $"rect moved at t={t:0.000}s");
            }
        }

        [TestMethod]
        public void ComputeDestRect_SlideOffset_TranslatesTheCorner()
        {
            var track = BottomRightTrack();
            AddSample(track, 0, slideY: 150);

            var atRest = BottomRightTrack();
            AddSample(atRest, 0);

            var slid = ToastOverlayExportMath.ComputeDestRect(
                track, 0, 0.0, CardW, CardH, ClientW, ClientH);
            var rest = ToastOverlayExportMath.ComputeDestRect(
                atRest, 0, 0.0, CardW, CardH, ClientW, ClientH);

            Assert.AreEqual(rest.X, slid.X);
            Assert.AreEqual(rest.Y + 150, slid.Y);
        }

        [TestMethod]
        public void ComputeDestRect_MonitorScale_ScalesTheGap()
        {
            var track = BottomRightTrack(gapDip: 24.0, monitorScale: 1.5);
            AddSample(track, 0);

            var rect = ToastOverlayExportMath.ComputeDestRect(
                track, 0, 0.0, CardW, CardH, ClientW, ClientH);

            Assert.AreEqual(ClientW - CardW - 36, rect.X);
            Assert.AreEqual(ClientH - CardH - 36, rect.Y);
        }

        [TestMethod]
        public void ComputeDestRect_DownscaledFrame_ScalesPositionAndSize()
        {
            var track = BottomRightTrack();
            AddSample(track, 0);

            var rect = ToastOverlayExportMath.ComputeDestRect(
                track, 0, 0.0, CardW, CardH, ClientW / 2, ClientH / 2);

            Assert.AreEqual((ClientW - CardW - 24) / 2, rect.X);
            Assert.AreEqual((ClientH - CardH - 24) / 2, rect.Y);
            Assert.AreEqual(CardW / 2, rect.Width);
            Assert.AreEqual(CardH / 2, rect.Height);
        }

        [TestMethod]
        public void ComputeDestRect_InvalidClientDims_ReturnsEmpty()
        {
            var track = BottomRightTrack();
            track.Samples.Add(new ToastOverlayTrack.Sample { ElapsedMs = 0, FrameIndex = 0 });

            Assert.AreEqual(
                Rectangle.Empty,
                ToastOverlayExportMath.ComputeDestRect(track, 0, 0.0, CardW, CardH, 1920, 1080));
        }

        // === ScaleRect double overload ===

        [TestMethod]
        public void ScaleRect_Double_SubPixelPositionRoundsAfterScaling()
        {
            // 10.6 physical px halved is 5.3, which must round from the scaled value (5), not
            // from a pre-rounded 11/2 (6 after the early snap's 11 * 0.5 = 5.5 rounding up).
            var rect = OverlayBlitMath.ScaleRect(10.6, 10.6, 100, 100, 1000, 1000, 500, 500);

            Assert.AreEqual(5, rect.X);
            Assert.AreEqual(5, rect.Y);
        }

        [TestMethod]
        public void ScaleRect_Double_InvalidDimensions_ReturnsEmpty()
        {
            Assert.AreEqual(
                Rectangle.Empty, OverlayBlitMath.ScaleRect(0.0, 0.0, 10, 10, 0, 1080, 960, 540));
        }
    }
}
