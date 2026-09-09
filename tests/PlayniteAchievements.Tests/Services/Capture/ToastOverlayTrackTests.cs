using System;
using System.Collections.Generic;
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

        // === Resample cadence ===
        //
        // Tracks are sampled once per recording frame, so a clip at that same frame rate must advance
        // one sample per frame. Getting a distinct sample only every other frame is the judder these
        // guard against, and it is invisible in the output unless asserted.

        /// <summary>Sample times as the recorder stores them: rounded to whole ms.</summary>
        private static ToastOverlayTrack TrackSampledAt(double fps, int frames)
        {
            var track = new ToastOverlayTrack();
            for (var i = 0; i < frames; i++)
            {
                track.Samples.Add(new ToastOverlayTrack.Sample
                {
                    ElapsedMs = (int)Math.Round(i * (1000.0 / fps)),
                    FrameIndex = 0,
                });
            }

            return track;
        }

        private static int DistinctSamplesOverPlayback(ToastOverlayTrack track, double fps, int frames)
        {
            var indices = new int[frames];
            for (var i = 0; i < frames; i++)
            {
                indices[i] = track.FindSampleIndexAtOrBefore(i / fps);
            }

            return indices.Distinct().Count();
        }

        [TestMethod]
        public void FindSample_SixtyFpsTrackAtSixtyFps_AdvancesEveryFrame()
        {
            var track = TrackSampledAt(60, 61);

            for (var frame = 0; frame < 61; frame++)
            {
                Assert.AreEqual(
                    frame, track.FindSampleIndexAtOrBefore(frame / 60.0),
                    $"frame {frame} did not resolve to its own sample");
            }
        }

        [TestMethod]
        public void FindSample_ThirtyFpsTrackAtThirtyFps_AdvancesEveryFrame()
        {
            var track = TrackSampledAt(30, 31);

            for (var frame = 0; frame < 31; frame++)
            {
                Assert.AreEqual(frame, track.FindSampleIndexAtOrBefore(frame / 30.0));
            }
        }

        [TestMethod]
        public void FindSample_SixtyFpsTrackAtSixtyFps_DoesNotHoldPositionsInPairs()
        {
            // The pre-change behaviour: a ~30 Hz track played at 60 fps yielded ~31 distinct
            // positions across 61 frames. Sampling at the recording rate must not land near that.
            var atRate = DistinctSamplesOverPlayback(TrackSampledAt(60, 61), 60.0, 61);
            var atHalfRate = DistinctSamplesOverPlayback(TrackSampledAt(30, 31), 60.0, 61);

            Assert.AreEqual(61, atRate);
            Assert.IsTrue(atHalfRate <= 31, $"expected pair-held playback, got {atHalfRate} distinct");
        }

        [TestMethod]
        public void FindSample_TrackSampledFasterThanPlayback_SkipsEvenly()
        {
            // Recording at 30 fps while the track was sampled at 60: every other sample is unused,
            // never the same one twice.
            var track = TrackSampledAt(60, 61);

            Assert.AreEqual(0, track.FindSampleIndexAtOrBefore(0.0));
            Assert.AreEqual(2, track.FindSampleIndexAtOrBefore(1 / 30.0));
            Assert.AreEqual(4, track.FindSampleIndexAtOrBefore(2 / 30.0));
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

        // === Delta frame chain ===

        private const int DeltaW = 8;
        private const int DeltaH = 4;

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var result = new byte[a.Length];
            for (var i = 0; i < a.Length; i++)
            {
                result[i] = (byte)(a[i] ^ b[i]);
            }

            return result;
        }

        /// <summary>
        /// Builds a track of <paramref name="count"/> frames, a keyframe every
        /// <paramref name="keyframeInterval"/> and XOR deltas between, exactly as the recorder does.
        /// Returns the raw pixels each frame should reconstruct to.
        /// </summary>
        private static ToastOverlayTrack DeltaTrack(int count, int keyframeInterval, out List<byte[]> expected)
        {
            var track = new ToastOverlayTrack();
            expected = new List<byte[]>();
            var random = new Random(7);
            byte[] previous = null;

            for (var i = 0; i < count; i++)
            {
                var raw = new byte[DeltaW * DeltaH * 4];
                random.NextBytes(raw);
                expected.Add(raw);

                var isDelta = previous != null && i % keyframeInterval != 0;
                var payload = isDelta ? Xor(raw, previous) : raw;
                track.Frames.Add(ToastOverlayTrack.Frame.Compress(payload, DeltaW, DeltaH, isDelta));
                previous = raw;
            }

            return track;
        }

        [TestMethod]
        public void Reconstruct_WalkingForward_RebuildsEveryFrameExactly()
        {
            var track = DeltaTrack(10, keyframeInterval: 4, out var expected);
            byte[] buffer = null;
            var bufferIndex = -1;

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.IsTrue(track.TryReconstructFrame(i, ref buffer, ref bufferIndex), $"frame {i}");
                Assert.AreEqual(i, bufferIndex);
                CollectionAssert.AreEqual(expected[i], buffer, $"frame {i} pixels");
            }
        }

        [TestMethod]
        public void Reconstruct_SkippingFrames_StillRebuildsExactly()
        {
            // A 30 fps clip over a 60 fps track asks for every other sample.
            var track = DeltaTrack(10, keyframeInterval: 4, out var expected);
            byte[] buffer = null;
            var bufferIndex = -1;

            foreach (var i in new[] { 1, 3, 5, 9 })
            {
                Assert.IsTrue(track.TryReconstructFrame(i, ref buffer, ref bufferIndex), $"frame {i}");
                CollectionAssert.AreEqual(expected[i], buffer, $"frame {i} pixels");
            }
        }

        [TestMethod]
        public void Reconstruct_ColdStartMidTrack_ReplaysFromTheKeyframe()
        {
            var track = DeltaTrack(10, keyframeInterval: 4, out var expected);
            byte[] buffer = null;
            var bufferIndex = -1;

            // Frame 7 is a delta whose chain runs back to the keyframe at 4.
            Assert.IsTrue(track.TryReconstructFrame(7, ref buffer, ref bufferIndex));
            CollectionAssert.AreEqual(expected[7], buffer);
        }

        [TestMethod]
        public void Reconstruct_RepeatedRequest_ReturnsTheHeldFrame()
        {
            var track = DeltaTrack(5, keyframeInterval: 4, out var expected);
            byte[] buffer = null;
            var bufferIndex = -1;

            Assert.IsTrue(track.TryReconstructFrame(2, ref buffer, ref bufferIndex));
            var first = buffer;
            Assert.IsTrue(track.TryReconstructFrame(2, ref buffer, ref bufferIndex));

            Assert.AreSame(first, buffer, "a repeated request should not re-inflate");
            CollectionAssert.AreEqual(expected[2], buffer);
        }

        [TestMethod]
        public void Reconstruct_BrokenChain_FailsWithoutCorruptingTheHeldFrame()
        {
            var track = DeltaTrack(10, keyframeInterval: 4, out var expected);
            byte[] buffer = null;
            var bufferIndex = -1;
            Assert.IsTrue(track.TryReconstructFrame(5, ref buffer, ref bufferIndex));
            var held = (byte[])buffer.Clone();

            // Frame 6's compression failed, so 6 and 7 are unreachable.
            track.Frames[6].Deflated = null;

            Assert.IsFalse(track.TryReconstructFrame(6, ref buffer, ref bufferIndex));
            CollectionAssert.AreEqual(held, buffer, "the previously reconstructed frame was modified");

            // The next keyframe recovers.
            buffer = null;
            bufferIndex = -1;
            Assert.IsTrue(track.TryReconstructFrame(8, ref buffer, ref bufferIndex));
            CollectionAssert.AreEqual(expected[8], buffer);
        }

        [TestMethod]
        public void Reconstruct_OutOfRange_Fails()
        {
            var track = DeltaTrack(3, keyframeInterval: 4, out _);
            byte[] buffer = null;
            var bufferIndex = -1;

            Assert.IsFalse(track.TryReconstructFrame(-1, ref buffer, ref bufferIndex));
            Assert.IsFalse(track.TryReconstructFrame(3, ref buffer, ref bufferIndex));
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

        [TestMethod]
        public void ClipToFrame_KeepsTheOverlapAndRejectsTheRest()
        {
            Assert.AreEqual(new Rectangle(2, 3, 4, 5), OverlayBlitMath.ClipToFrame(new Rectangle(2, 3, 4, 5), 10, 10));
            Assert.AreEqual(new Rectangle(0, 0, 2, 2), OverlayBlitMath.ClipToFrame(new Rectangle(-3, -3, 5, 5), 10, 10));
            Assert.AreEqual(new Rectangle(8, 9, 2, 1), OverlayBlitMath.ClipToFrame(new Rectangle(8, 9, 5, 5), 10, 10));
            Assert.IsTrue(OverlayBlitMath.ClipToFrame(new Rectangle(10, 0, 2, 2), 10, 10).IsEmpty);
            Assert.IsTrue(OverlayBlitMath.ClipToFrame(new Rectangle(-5, -5, 2, 2), 10, 10).IsEmpty);
            Assert.IsTrue(OverlayBlitMath.ClipToFrame(new Rectangle(1, 1, 0, 3), 10, 10).IsEmpty);
            Assert.IsTrue(OverlayBlitMath.ClipToFrame(new Rectangle(1, 1, 3, 3), 0, 10).IsEmpty);
        }

        /// <summary>
        /// The in-place compositor blends only the clipped rectangle, cut out of the frame and blitted
        /// with the destination rectangle translated into it. That must land every pixel exactly
        /// where a blit over the whole frame would, including when the card hangs off an edge.
        /// </summary>
        [TestMethod]
        public void BlendOnto_ClippedRegionWithTranslatedRect_MatchesTheWholeFrameBlit()
        {
            const int FrameW = 8, FrameH = 6, Stride = FrameW * 4;
            var overlay = new byte[3 * 2 * 4];
            for (var i = 0; i < overlay.Length; i += 4)
            {
                overlay[i] = (byte)(20 * i); overlay[i + 1] = (byte)(7 * i); overlay[i + 2] = (byte)(3 * i); overlay[i + 3] = 160;
            }

            foreach (var destRect in new[] { new Rectangle(2, 1, 4, 3), new Rectangle(-2, -1, 5, 4), new Rectangle(5, 3, 6, 6) })
            {
                var whole = Gradient(FrameW, FrameH);
                OverlayBlitMath.BlendOnto(whole, FrameW, FrameH, Stride, overlay, 3, 2, destRect);

                var regional = Gradient(FrameW, FrameH);
                var clipped = OverlayBlitMath.ClipToFrame(destRect, FrameW, FrameH);
                var regionStride = clipped.Width * 4;
                var region = new byte[regionStride * clipped.Height];
                for (var row = 0; row < clipped.Height; row++)
                {
                    Array.Copy(regional, ((clipped.Y + row) * Stride) + (clipped.X * 4), region, row * regionStride, regionStride);
                }

                OverlayBlitMath.BlendOnto(
                    region, clipped.Width, clipped.Height, regionStride, overlay, 3, 2,
                    new Rectangle(destRect.X - clipped.X, destRect.Y - clipped.Y, destRect.Width, destRect.Height));
                for (var row = 0; row < clipped.Height; row++)
                {
                    Array.Copy(region, row * regionStride, regional, ((clipped.Y + row) * Stride) + (clipped.X * 4), regionStride);
                }

                CollectionAssert.AreEqual(whole, regional, "rect " + destRect);
            }
        }

        [TestMethod]
        public void AlignToChromaBlocks_WidensToEvenBoundsInsideTheFrame()
        {
            Assert.AreEqual(new Rectangle(2, 2, 4, 4), OverlayBlitMath.AlignToChromaBlocks(new Rectangle(3, 3, 2, 2), 10, 10));
            Assert.AreEqual(new Rectangle(2, 4, 4, 2), OverlayBlitMath.AlignToChromaBlocks(new Rectangle(2, 4, 4, 2), 10, 10));
            Assert.AreEqual(new Rectangle(8, 8, 2, 2), OverlayBlitMath.AlignToChromaBlocks(new Rectangle(9, 9, 1, 1), 10, 10));
            Assert.AreEqual(new Rectangle(0, 0, 10, 10), OverlayBlitMath.AlignToChromaBlocks(new Rectangle(0, 0, 10, 10), 11, 11), "odd frame edges are left alone");
            Assert.IsTrue(OverlayBlitMath.AlignToChromaBlocks(Rectangle.Empty, 10, 10).IsEmpty);
        }

        private static void Nv12Planes(int w, int h, byte y, byte cb, byte cr, out byte[] luma, out byte[] chroma)
        {
            luma = new byte[w * h];
            chroma = new byte[w * h / 2];
            for (var i = 0; i < luma.Length; i++) { luma[i] = y; }
            for (var i = 0; i < chroma.Length; i += 2) { chroma[i] = cb; chroma[i + 1] = cr; }
        }

        [TestMethod]
        public void BlendOntoNv12_OpaqueColours_LandOnTheBt709LimitedRangeValues()
        {
            // (premultiplied B, G, R, A) -> expected (Y, Cb, Cr), the textbook 8-bit limited values.
            var cases = new[]
            {
                new { Overlay = SolidOverlay(2, 2, 255, 255, 255, 255), Y = 235, Cb = 128, Cr = 128 },
                new { Overlay = SolidOverlay(2, 2, 0, 0, 0, 255), Y = 16, Cb = 128, Cr = 128 },
                new { Overlay = SolidOverlay(2, 2, 0, 0, 255, 255), Y = 63, Cb = 102, Cr = 240 },
                new { Overlay = SolidOverlay(2, 2, 0, 255, 0, 255), Y = 173, Cb = 42, Cr = 26 },
                new { Overlay = SolidOverlay(2, 2, 255, 0, 0, 255), Y = 32, Cb = 240, Cr = 118 },
            };
            foreach (var c in cases)
            {
                Nv12Planes(2, 2, 100, 100, 100, out var luma, out var chroma);
                OverlayBlitMath.BlendOntoNv12(luma, chroma, 2, 2, c.Overlay, 2, 2, new Rectangle(0, 0, 2, 2));
                Assert.IsTrue(luma.All(v => Math.Abs(v - c.Y) <= 1), "Y " + string.Join(",", luma) + " vs " + c.Y);
                Assert.IsTrue(Math.Abs(chroma[0] - c.Cb) <= 1, "Cb " + chroma[0] + " vs " + c.Cb);
                Assert.IsTrue(Math.Abs(chroma[1] - c.Cr) <= 1, "Cr " + chroma[1] + " vs " + c.Cr);
            }
        }

        [TestMethod]
        public void BlendOntoNv12_ZeroAlpha_LeavesPlanesUntouched()
        {
            Nv12Planes(4, 4, 77, 90, 160, out var luma, out var chroma);
            OverlayBlitMath.BlendOntoNv12(luma, chroma, 4, 4, SolidOverlay(2, 2, 255, 255, 255, 0), 2, 2, new Rectangle(0, 0, 4, 4));

            Assert.IsTrue(luma.All(v => v == 77));
            for (var i = 0; i < chroma.Length; i += 2) { Assert.AreEqual(90, chroma[i]); Assert.AreEqual(160, chroma[i + 1]); }
        }

        [TestMethod]
        public void BlendOntoNv12_HalfAlphaWhiteOverBlack_MeetsInTheMiddle()
        {
            Nv12Planes(2, 2, 16, 128, 128, out var luma, out var chroma);
            // Premultiplied half-alpha white: 128,128,128,128.
            OverlayBlitMath.BlendOntoNv12(luma, chroma, 2, 2, SolidOverlay(1, 1, 128, 128, 128, 128), 1, 1, new Rectangle(0, 0, 2, 2));

            // 16 * (1 - a) + (16 a + 219 a) with a = 128/255.
            var expected = (int)Math.Round((16 * (1 - 128 / 255.0)) + (235 * (128 / 255.0)));
            Assert.IsTrue(luma.All(v => Math.Abs(v - expected) <= 1), string.Join(",", luma));
            Assert.AreEqual(128, chroma[0]);
            Assert.AreEqual(128, chroma[1]);
        }

        [TestMethod]
        public void BlendOntoNv12_PartialBlock_BlendsChromaByMeanCoverage()
        {
            // An opaque red card covering only the left column of one 2x2 block: luma changes on
            // that column alone, and the block's chroma moves half way toward red's Cb/Cr.
            Nv12Planes(2, 2, 128, 128, 128, out var luma, out var chroma);
            OverlayBlitMath.BlendOntoNv12(luma, chroma, 2, 2, SolidOverlay(1, 2, 0, 0, 255, 255), 1, 2, new Rectangle(0, 0, 1, 2));

            Assert.AreEqual(63, luma[0], 1);
            Assert.AreEqual(63, luma[2], 1);
            Assert.AreEqual(128, luma[1]);
            Assert.AreEqual(128, luma[3]);
            Assert.AreEqual((128 + 102) / 2, chroma[0], 1);
            Assert.AreEqual((128 + 240) / 2, chroma[1], 1);
        }

        [TestMethod]
        public void BlendOntoNv12_ClipsToTheRegionAndRejectsOddRegions()
        {
            Nv12Planes(4, 4, 50, 128, 128, out var luma, out var chroma);
            OverlayBlitMath.BlendOntoNv12(luma, chroma, 4, 4, SolidOverlay(2, 2, 255, 255, 255, 255), 2, 2, new Rectangle(3, 3, 4, 4));
            Assert.AreEqual(235, luma[15]);
            Assert.AreEqual(50, luma[10], "pixels outside the rectangle are untouched");
            Assert.AreEqual(50, luma[0]);

            Nv12Planes(3, 4, 50, 128, 128, out var oddLuma, out var oddChroma);
            OverlayBlitMath.BlendOntoNv12(oddLuma, oddChroma, 3, 4, SolidOverlay(2, 2, 255, 255, 255, 255), 2, 2, new Rectangle(0, 0, 3, 4));
            Assert.IsTrue(oddLuma.All(v => v == 50), "an odd-sized region is refused rather than misaddressed");
        }

        private static byte[] Gradient(int width, int height)
        {
            var frame = new byte[width * height * 4];
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (byte)(i * 37);
            }

            return frame;
        }
    }
}
