using System;
using System.Drawing;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class ToastOverlayTrackTests
    {
        private static ToastOverlayTrack TrackWithSamples(params int[] elapsedMs)
        {
            var track = new ToastOverlayTrack();
            foreach (var ms in elapsedMs)
            {
                track.Samples.Add(new ToastOverlayTrack.Sample { ElapsedMs = ms, FrameIndex = 0 });
            }

            return track;
        }

        // === Sample lookup ===

        [TestMethod]
        public void FindSample_EmptyTrack_ReturnsMinusOne()
        {
            Assert.AreEqual(-1, new ToastOverlayTrack().FindSampleIndexAtOrBefore(0.5));
        }

        [TestMethod]
        public void FindSample_BeforeFirstSample_ReturnsMinusOne()
        {
            var track = TrackWithSamples(100, 200, 300);

            Assert.AreEqual(-1, track.FindSampleIndexAtOrBefore(0.05));
        }

        [TestMethod]
        public void FindSample_ExactHit_ReturnsThatSample()
        {
            var track = TrackWithSamples(0, 33, 66, 99);

            Assert.AreEqual(0, track.FindSampleIndexAtOrBefore(0.0));
            Assert.AreEqual(2, track.FindSampleIndexAtOrBefore(0.066));
        }

        [TestMethod]
        public void FindSample_BetweenSamples_ReturnsPreceding()
        {
            var track = TrackWithSamples(0, 33, 66, 99);

            Assert.AreEqual(1, track.FindSampleIndexAtOrBefore(0.050));
        }

        [TestMethod]
        public void FindSample_AfterLastSample_ReturnsLast()
        {
            var track = TrackWithSamples(0, 33, 66);

            Assert.AreEqual(2, track.FindSampleIndexAtOrBefore(10.0));
        }

        // === Frame round-trip ===

        [TestMethod]
        public void Frame_DeflateRoundTrip_IsByteExact()
        {
            var raw = new byte[8 * 4 * 4];
            new Random(42).NextBytes(raw);

            var frame = ToastOverlayTrack.Frame.FromRaw(raw, 8, 4);
            var restored = frame.ToRaw();

            CollectionAssert.AreEqual(raw, restored);
        }

        // === Rect scaling ===

        [TestMethod]
        public void ScaleRect_UnityClientToFrame_IsIdentity()
        {
            var rect = OverlayBlitMath.ScaleRect(100, 50, 400, 150, 1920, 1080, 1920, 1080);

            Assert.AreEqual(new Rectangle(100, 50, 400, 150), rect);
        }

        [TestMethod]
        public void ScaleRect_DownscaledFrame_ScalesPositionAndSize()
        {
            // Encode cap halved the frame: 1920x1080 client -> 960x540 video.
            var rect = OverlayBlitMath.ScaleRect(100, 50, 400, 150, 1920, 1080, 960, 540);

            Assert.AreEqual(new Rectangle(50, 25, 200, 75), rect);
        }

        [TestMethod]
        public void ScaleRect_InvalidDimensions_ReturnsEmpty()
        {
            Assert.AreEqual(Rectangle.Empty, OverlayBlitMath.ScaleRect(0, 0, 10, 10, 0, 1080, 960, 540));
            Assert.AreEqual(Rectangle.Empty, OverlayBlitMath.ScaleRect(0, 0, 10, 10, 1920, 1080, 0, 540));
        }

        // === Blend ===

        private static byte[] SolidFrame(int w, int h, byte b, byte g, byte r)
        {
            var frame = new byte[w * h * 4];
            for (var i = 0; i < frame.Length; i += 4)
            {
                frame[i] = b;
                frame[i + 1] = g;
                frame[i + 2] = r;
                frame[i + 3] = 255;
            }

            return frame;
        }

        private static byte[] SolidOverlay(int w, int h, byte b, byte g, byte r, byte a)
        {
            // Premultiplied: channel values are already scaled by alpha.
            var overlay = new byte[w * h * 4];
            for (var i = 0; i < overlay.Length; i += 4)
            {
                overlay[i] = b;
                overlay[i + 1] = g;
                overlay[i + 2] = r;
                overlay[i + 3] = a;
            }

            return overlay;
        }

        [TestMethod]
        public void BlendOnto_OpaqueOverlay_ReplacesPixels()
        {
            var frame = SolidFrame(4, 4, 10, 20, 30);
            var overlay = SolidOverlay(2, 2, 200, 100, 50, 255);

            OverlayBlitMath.BlendOnto(frame, 4, 4, 16, overlay, 2, 2, new Rectangle(1, 1, 2, 2));

            // Inside the dest rect: replaced.
            var inside = (2 * 16) + (2 * 4);
            Assert.AreEqual(200, frame[inside]);
            Assert.AreEqual(100, frame[inside + 1]);
            Assert.AreEqual(50, frame[inside + 2]);
            // Outside: untouched.
            Assert.AreEqual(10, frame[0]);
        }

        [TestMethod]
        public void BlendOnto_ZeroAlpha_LeavesFrameUntouched()
        {
            var frame = SolidFrame(2, 2, 10, 20, 30);
            var overlay = SolidOverlay(2, 2, 0, 0, 0, 0);

            OverlayBlitMath.BlendOnto(frame, 2, 2, 8, overlay, 2, 2, new Rectangle(0, 0, 2, 2));

            Assert.AreEqual(10, frame[0]);
            Assert.AreEqual(20, frame[1]);
            Assert.AreEqual(30, frame[2]);
        }

        [TestMethod]
        public void BlendOnto_HalfAlpha_BlendsPremultipliedOver()
        {
            var frame = SolidFrame(1, 1, 100, 100, 100);
            // Premultiplied half-alpha white: 128,128,128 @ a=128.
            var overlay = SolidOverlay(1, 1, 128, 128, 128, 128);

            OverlayBlitMath.BlendOnto(frame, 1, 1, 4, overlay, 1, 1, new Rectangle(0, 0, 1, 1));

            // dst = 128 + round(100 * 127 / 255) = 128 + 50 = 178.
            Assert.AreEqual(178, frame[0]);
            Assert.AreEqual(178, frame[1]);
            Assert.AreEqual(178, frame[2]);
        }

        [TestMethod]
        public void BlendOnto_DestRectPartiallyOffFrame_ClipsWithoutThrowing()
        {
            var frame = SolidFrame(4, 4, 0, 0, 0);
            var overlay = SolidOverlay(2, 2, 255, 255, 255, 255);

            // Slide-out: card half off the right/bottom edge.
            OverlayBlitMath.BlendOnto(frame, 4, 4, 16, overlay, 2, 2, new Rectangle(3, 3, 2, 2));

            var corner = (3 * 16) + (3 * 4);
            Assert.AreEqual(255, frame[corner]);
            // Off-frame pixels never written: everything else is still black.
            Assert.AreEqual(0, frame[(3 * 16) + (2 * 4)]);
        }

        [TestMethod]
        public void BlendOnto_FullyOffFrame_NoOp()
        {
            var frame = SolidFrame(2, 2, 5, 5, 5);
            var overlay = SolidOverlay(2, 2, 255, 255, 255, 255);

            OverlayBlitMath.BlendOnto(frame, 2, 2, 8, overlay, 2, 2, new Rectangle(-5, -5, 2, 2));
            OverlayBlitMath.BlendOnto(frame, 2, 2, 8, overlay, 2, 2, new Rectangle(10, 10, 2, 2));

            Assert.IsTrue(frame.Where((_, i) => i % 4 != 3).All(v => v == 5));
        }

        [TestMethod]
        public void BlendOnto_ScaledDest_NearestNeighborCoversWholeRect()
        {
            var frame = SolidFrame(4, 2, 0, 0, 0);
            // 2x1 overlay: left pixel white, right pixel gray — scaled to 4x2.
            var overlay = new byte[]
            {
                255, 255, 255, 255,
                100, 100, 100, 255,
            };

            OverlayBlitMath.BlendOnto(frame, 4, 2, 16, overlay, 2, 1, new Rectangle(0, 0, 4, 2));

            Assert.AreEqual(255, frame[0]);        // (0,0) <- src x0
            Assert.AreEqual(255, frame[4]);        // (1,0) <- src x0
            Assert.AreEqual(100, frame[8]);        // (2,0) <- src x1
            Assert.AreEqual(100, frame[12]);       // (3,0) <- src x1
            Assert.AreEqual(255, frame[16]);       // (0,1) <- src y0
        }
    }
}
