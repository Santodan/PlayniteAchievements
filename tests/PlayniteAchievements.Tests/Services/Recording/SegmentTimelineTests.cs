using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class SegmentTimelineTests
    {
        // Fixed-offset zone so parsing tests don't depend on the machine's local time zone.
        private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone(
            "Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static SegmentTimeline.SegmentInfo Segment(DateTime startUtc, long size = 1)
        {
            return new SegmentTimeline.SegmentInfo
            {
                Path = $@"C:\buf\seg_{startUtc:yyyyMMdd-HHmmss}.mp4",
                StartUtc = startUtc,
                SizeBytes = size
            };
        }

        private static SegmentTimeline.SegmentInfo Sized(DateTime startUtc, int width, int height)
        {
            return new SegmentTimeline.SegmentInfo
            {
                Path = $@"C:\buf\seg_{startUtc:yyyyMMdd-HHmmss}_{width}x{height}.mp4",
                StartUtc = startUtc,
                SizeBytes = 1,
                Width = width,
                Height = height
            };
        }

        // === Filename parsing ===

        [TestMethod]
        public void ParseSegments_ConvertsLocalStampsToUtcWithInjectedZone()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[] { (@"C:\buf\seg_20260101-140000.mp4", 123L) },
                PlusTwo);

            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 0), segments[0].StartUtc);
            Assert.AreEqual(123L, segments[0].SizeBytes);
        }

        [TestMethod]
        public void ParseSegments_OrdersOldestFirstAndSkipsForeignFiles()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[]
                {
                    (@"C:\buf\seg_20260101-140010.mp4", 1L),
                    (@"C:\buf\clip_abc.mp4", 1L),
                    (@"C:\buf\seg_20260101-140000.mp4", 1L),
                    (@"C:\buf\seg_garbage.mp4", 1L),
                    (@"C:\buf\notes.txt", 1L)
                },
                PlusTwo);

            Assert.AreEqual(2, segments.Count);
            Assert.IsTrue(segments[0].StartUtc < segments[1].StartUtc);
        }

        [TestMethod]
        public void ParseSegments_AudioPrefixAndExtension_ParsesOnlyAudioChunks()
        {
            var chunks = SegmentTimeline.ParseSegments(
                new[]
                {
                    (@"C:\buf\aud_20260101-140005.wav", 2L),
                    (@"C:\buf\seg_20260101-140000.mp4", 1L),
                    (@"C:\buf\aud_20260101-140000.wav", 1L),
                    (@"C:\buf\aud_garbage.wav", 1L),
                    (@"C:\buf\clipaud_abc.txt", 1L)
                },
                PlusTwo,
                "aud_",
                ".wav");

            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 0), chunks[0].StartUtc);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 5), chunks[1].StartUtc);
        }

        [TestMethod]
        public void ParseSegments_DefaultFilterStillParsesVideoSegmentsOnly()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[]
                {
                    (@"C:\buf\seg_20260101-140000.mp4", 1L),
                    (@"C:\buf\aud_20260101-140000.wav", 1L)
                },
                PlusTwo);

            Assert.AreEqual(1, segments.Count);
            StringAssert.EndsWith(segments[0].Path, "seg_20260101-140000.mp4");
        }

        // === Precise-unlock detection ===

        [TestMethod]
        public void IsPreciseUnlockTime_NullIsCoarse()
        {
            Assert.IsFalse(SegmentTimeline.IsPreciseUnlockTime(null, T0, T0.AddSeconds(30)));
        }

        [TestMethod]
        public void IsPreciseUnlockTime_MidnightTimeOfDayIsCoarse()
        {
            var dateOnly = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(SegmentTimeline.IsPreciseUnlockTime(
                dateOnly, dateOnly.AddHours(-1), dateOnly.AddHours(1)));
        }

        [TestMethod]
        public void IsPreciseUnlockTime_BeforeCaptureStartIsCoarse()
        {
            Assert.IsFalse(SegmentTimeline.IsPreciseUnlockTime(
                T0.AddSeconds(-1), T0, T0.AddSeconds(30)));
        }

        [TestMethod]
        public void IsPreciseUnlockTime_FarInFutureIsCoarse()
        {
            var detection = T0.AddSeconds(30);

            Assert.IsFalse(SegmentTimeline.IsPreciseUnlockTime(
                detection.AddSeconds(6), T0, detection));
            Assert.IsTrue(SegmentTimeline.IsPreciseUnlockTime(
                detection.AddSeconds(5), T0, detection));
        }

        [TestMethod]
        public void IsPreciseUnlockTime_WithinCaptureWindowIsPrecise()
        {
            Assert.IsTrue(SegmentTimeline.IsPreciseUnlockTime(
                T0.AddSeconds(10), T0, T0.AddSeconds(30)));
        }

        // === Clip window (unlock-anchored; the toast is composited at export) ===

        [TestMethod]
        public void ComputeClipWindow_PreciseUnlock_PreRollThroughToastSlot()
        {
            var captureStart = T0;
            var unlock = T0.AddSeconds(60);
            var detection = unlock.AddSeconds(10);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(unlock.AddSeconds(-15), window.StartUtc);
            Assert.AreEqual(unlock, window.ToastAnchorUtc);
            // End is anchor + slot + tail; the on-screen toast never moves the window.
            Assert.AreEqual(unlock.AddSeconds(9), window.EndUtc);
            Assert.AreEqual(24, (window.EndUtc - window.StartUtc).TotalSeconds, 0.001);
        }

        [TestMethod]
        public void ComputeClipWindow_CoarseUnlock_AnchorsOnDetection()
        {
            var captureStart = T0;
            var detection = T0.AddSeconds(120);

            var window = SegmentTimeline.ComputeClipWindow(
                null, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            // Coarse: pre-roll before detection (the unlock happened within the last poll
            // interval; the user's pre-roll setting governs the lead).
            Assert.AreEqual(detection.AddSeconds(-15), window.StartUtc);
            Assert.AreEqual(detection, window.ToastAnchorUtc);
            Assert.AreEqual(detection.AddSeconds(9), window.EndUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_LongerToastSlotExtendsTheEnd()
        {
            var captureStart = T0;
            var unlock = T0.AddSeconds(60);
            var detection = unlock.AddSeconds(1);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 12, tailSeconds: 1);

            Assert.AreEqual(unlock.AddSeconds(-15), window.StartUtc);
            Assert.AreEqual(unlock.AddSeconds(13), window.EndUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_MidnightUnlockIsTreatedAsCoarse()
        {
            var captureStart = new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc);
            var unlock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var detection = unlock.AddMinutes(5);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            // Coarse anchor: detection - preRoll, not unlock - preRoll.
            Assert.AreEqual(detection.AddSeconds(-15), window.StartUtc);
            Assert.AreEqual(detection, window.ToastAnchorUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_PreSessionUnlockIsTreatedAsCoarse()
        {
            var captureStart = T0;
            var unlock = T0.AddMinutes(-30);
            var detection = T0.AddSeconds(120);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(detection.AddSeconds(-15), window.StartUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_FutureUnlockTimestampIsTreatedAsCoarse()
        {
            var captureStart = T0;
            var detection = T0.AddSeconds(120);
            var unlock = detection.AddMinutes(10);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(detection.AddSeconds(-15), window.StartUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_ClampsToCaptureStart_AnchorNeverBeforeStart()
        {
            var captureStart = T0;
            var unlock = T0.AddSeconds(2);
            var detection = T0.AddSeconds(3);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(captureStart, window.StartUtc);
            // The pre-roll got clamped, but the anchor (unlock) is after the start, so it holds.
            Assert.AreEqual(unlock, window.ToastAnchorUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_AnchorRaisedToStartWhenClampPassesIt()
        {
            var captureStart = T0;
            var oldestSegment = T0.AddSeconds(50);
            // Clamping to the oldest segment moves the start past the coarse anchor's own time
            // minus pre-roll AND past the anchor: toast begins at the clip start.
            var detection = T0.AddSeconds(45);

            var window = SegmentTimeline.ComputeClipWindow(
                null, detection, captureStart, oldestSegment,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(oldestSegment, window.StartUtc);
            Assert.AreEqual(oldestSegment, window.ToastAnchorUtc);
            Assert.AreEqual(oldestSegment.AddSeconds(9), window.EndUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_ClampsToOldestSegmentWhenLater()
        {
            var captureStart = T0;
            var oldestSegment = T0.AddSeconds(50);
            var unlock = T0.AddSeconds(52);
            var detection = T0.AddSeconds(53);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, oldestSegment,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(oldestSegment, window.StartUtc);
        }

        [TestMethod]
        public void ComputeClipWindow_FarBackPreciseUnlock_StartFlooredToPollIntervalPlusPreRoll()
        {
            var captureStart = T0;
            var detection = T0.AddSeconds(300);
            // A trusted timestamp far earlier in the session would open a huge clip; the floor
            // pulls the start to one poll interval + pre-roll before detection.
            var unlock = T0.AddSeconds(60);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(detection.AddSeconds(-30), window.StartUtc);
            // The floored start passed the far-back unlock anchor: toast begins at the start.
            Assert.AreEqual(window.StartUtc, window.ToastAnchorUtc);
        }

        // === Depth math ===

        [TestMethod]
        public void BufferDepthSeconds_TakesMaxOfTripleIntervalAndClipSpan()
        {
            Assert.AreEqual(60, SegmentTimeline.BufferDepthSeconds(15, 15));
            Assert.AreEqual(70, SegmentTimeline.BufferDepthSeconds(10, 30));
            Assert.AreEqual(180, SegmentTimeline.BufferDepthSeconds(60, 15));
        }

        [TestMethod]
        public void RetainedSegmentCount_CeilsDepthPlusTwo()
        {
            Assert.AreEqual(11, SegmentTimeline.RetainedSegmentCount(45, 5));
            Assert.AreEqual(12, SegmentTimeline.RetainedSegmentCount(46, 5));
        }

        // === Clip planning ===

        [TestMethod]
        public void PlanClip_SelectsOverlappingSegmentsWithOffsetAndDuration()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Segment(T0),
                Segment(T0.AddSeconds(5)),
                Segment(T0.AddSeconds(10)),
                Segment(T0.AddSeconds(15))
            };

            var plan = SegmentTimeline.PlanClip(segments, T0.AddSeconds(3), T0.AddSeconds(12), 5);

            Assert.IsNotNull(plan);
            CollectionAssert.AreEqual(
                new[] { segments[0], segments[1], segments[2] },
                plan.Segments.ToArray());
            Assert.AreEqual(3, plan.StartOffsetSeconds, 0.001);
            Assert.AreEqual(9, plan.DurationSeconds, 0.001);
        }

        [TestMethod]
        public void PlanClip_WindowBeforeFirstSegment_SnapsToRecordedData()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Segment(T0.AddSeconds(10)),
                Segment(T0.AddSeconds(15))
            };

            var plan = SegmentTimeline.PlanClip(segments, T0, T0.AddSeconds(18), 5);

            Assert.IsNotNull(plan);
            Assert.AreEqual(0, plan.StartOffsetSeconds, 0.001);
            Assert.AreEqual(8, plan.DurationSeconds, 0.001);
        }

        [TestMethod]
        public void PlanClip_BoundarySegmentIsExcludedWhenWindowStartsAtItsEnd()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Segment(T0),
                Segment(T0.AddSeconds(5))
            };

            // Window starts exactly where segment 0 ends: only segment 1 participates.
            var plan = SegmentTimeline.PlanClip(segments, T0.AddSeconds(5), T0.AddSeconds(9), 5);

            Assert.IsNotNull(plan);
            Assert.AreEqual(1, plan.Segments.Count);
            Assert.AreSame(segments[1], plan.Segments[0]);
        }

        [TestMethod]
        public void PlanClip_NoOverlap_ReturnsNull()
        {
            var segments = new List<SegmentTimeline.SegmentInfo> { Segment(T0) };

            Assert.IsNull(SegmentTimeline.PlanClip(
                segments, T0.AddSeconds(30), T0.AddSeconds(40), 5));
            Assert.IsNull(SegmentTimeline.PlanClip(
                new List<SegmentTimeline.SegmentInfo>(), T0, T0.AddSeconds(10), 5));
        }

        // === Clip planning across a capture resize ===

        [TestMethod]
        public void PlanClip_UniformDimensions_KeepsEveryOverlappingSegment()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Sized(T0, 1884, 976),
                Sized(T0.AddSeconds(5), 1884, 976),
                Sized(T0.AddSeconds(10), 1884, 976)
            };

            var plan = SegmentTimeline.PlanClip(
                segments, T0, T0.AddSeconds(15), 5, T0.AddSeconds(11));

            Assert.IsNotNull(plan);
            Assert.AreEqual(3, plan.Segments.Count);
            Assert.IsFalse(plan.TruncatedByResize);
            Assert.AreEqual(15, plan.DurationSeconds, 0.001);
            Assert.AreEqual(T0.AddSeconds(15), plan.EndUtc);
        }

        [TestMethod]
        public void PlanClip_DimensionChange_KeepsOnlyTheRunHoldingTheUnlock()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Sized(T0, 1884, 976),
                Sized(T0.AddSeconds(5), 1884, 976),
                Sized(T0.AddSeconds(10), 3840, 2160),
                Sized(T0.AddSeconds(15), 3840, 2160)
            };

            // The unlock sits in the first (smaller) run even though the later run is just as long.
            var plan = SegmentTimeline.PlanClip(
                segments, T0, T0.AddSeconds(20), 5, T0.AddSeconds(6));

            Assert.IsNotNull(plan);
            Assert.IsTrue(plan.TruncatedByResize);
            CollectionAssert.AreEqual(new[] { segments[0], segments[1] }, plan.Segments.ToArray());
            Assert.AreEqual(1884, plan.Segments[0].Width);
            // Cut at the resize rather than running to the requested window end.
            Assert.AreEqual(T0.AddSeconds(10), plan.EndUtc);
            Assert.AreEqual(10, plan.DurationSeconds, 0.001);
        }

        [TestMethod]
        public void PlanClip_DimensionChange_AnchorInLaterRunKeepsThatRun()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Sized(T0, 1884, 976),
                Sized(T0.AddSeconds(5), 1884, 976),
                Sized(T0.AddSeconds(10), 3840, 2160)
            };

            var plan = SegmentTimeline.PlanClip(
                segments, T0, T0.AddSeconds(15), 5, T0.AddSeconds(12));

            Assert.IsNotNull(plan);
            Assert.IsTrue(plan.TruncatedByResize);
            Assert.AreEqual(1, plan.Segments.Count);
            Assert.AreEqual(3840, plan.Segments[0].Width);
            // The last kept run reaches the window end, so nothing is cut off it.
            Assert.AreEqual(T0.AddSeconds(15), plan.EndUtc);
        }

        [TestMethod]
        public void PlanClip_DimensionChange_NoAnchor_KeepsTheLongestRun()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Sized(T0, 1884, 976),
                Sized(T0.AddSeconds(5), 3840, 2160),
                Sized(T0.AddSeconds(10), 3840, 2160)
            };

            var plan = SegmentTimeline.PlanClip(segments, T0, T0.AddSeconds(15), 5);

            Assert.IsNotNull(plan);
            Assert.IsTrue(plan.TruncatedByResize);
            CollectionAssert.AreEqual(new[] { segments[1], segments[2] }, plan.Segments.ToArray());
        }

        [TestMethod]
        public void ParseSegments_ReadsDimensionsFromSegmentNames()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[]
                {
                    (@"C:\buf\seg_20260101-140000_1884x976.mp4", 1L),
                    (@"C:\buf\seg_20260101-140005_3840x2160.mp4", 1L)
                },
                PlusTwo);

            Assert.AreEqual(2, segments.Count);
            Assert.AreEqual(1884, segments[0].Width);
            Assert.AreEqual(976, segments[0].Height);
            Assert.AreEqual(3840, segments[1].Width);
            Assert.AreEqual(2160, segments[1].Height);
        }

        [TestMethod]
        public void ParseSegments_ToleratesTheSameSecondUniquifierAndMissingDimensions()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[]
                {
                    // Written when a capture rebuild rolled a second segment in the same second.
                    (@"C:\buf\seg_20260101-140000_1884x976-1.mp4", 1L),
                    // A name from before dimensions were part of the format.
                    (@"C:\buf\seg_20260101-140005.mp4", 1L)
                },
                PlusTwo);

            Assert.AreEqual(2, segments.Count);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 0), segments[0].StartUtc);
            Assert.AreEqual(1884, segments[0].Width);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 5), segments[1].StartUtc);
            Assert.AreEqual(0, segments[1].Width);
        }

        [TestMethod]
        public void ParseSegments_StillRejectsNamesWithAForeignStampSuffix()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[] { (@"C:\buf\seg_20260101-140000x.mp4", 1L) },
                PlusTwo);

            Assert.AreEqual(0, segments.Count);
        }

        // === Pruning ===

        [TestMethod]
        public void SelectPrunable_KeepsBufferDepthNewestSegments()
        {
            // N=15, preRoll=15 -> depth 60s -> retain ceil(60/5)+2 = 14.
            var segments = Enumerable.Range(0, 18)
                .Select(i => Segment(T0.AddSeconds(i * 5)))
                .ToList();

            var prunable = SegmentTimeline.SelectPrunable(segments, 15, 15, 5, maxTotalBytes: 0);

            Assert.AreEqual(4, prunable.Count);
            CollectionAssert.AreEqual(segments.Take(4).ToArray(), prunable.ToArray());
        }

        [TestMethod]
        public void SelectPrunable_ByteCapPrunesOldestBeyondBudget()
        {
            const long gigabyte = 1024L * 1024 * 1024;
            var segments = Enumerable.Range(0, 5)
                .Select(i => Segment(T0.AddSeconds(i * 5), gigabyte))
                .ToList();

            // Depth would keep all 5, but only the newest two fit under 2 GB.
            var prunable = SegmentTimeline.SelectPrunable(segments, 15, 15, 5, maxTotalBytes: 2 * gigabyte);

            Assert.AreEqual(3, prunable.Count);
            CollectionAssert.AreEqual(segments.Take(3).ToArray(), prunable.ToArray());
        }

        [TestMethod]
        public void SelectPrunable_AlwaysKeepsTheNewestSegmentEvenOverBudget()
        {
            var segments = new List<SegmentTimeline.SegmentInfo>
            {
                Segment(T0, 10),
                Segment(T0.AddSeconds(5), long.MaxValue / 2)
            };

            var prunable = SegmentTimeline.SelectPrunable(segments, 15, 15, 5, maxTotalBytes: 100);

            Assert.AreEqual(1, prunable.Count);
            Assert.AreSame(segments[0], prunable[0]);
        }

        [TestMethod]
        public void SelectPrunable_AudioChunks_UseSameRetentionAsVideo()
        {
            // Same 5s cadence as the video test: N=15, preRoll=15 -> retain 14 of 18.
            var chunks = SegmentTimeline.ParseSegments(
                Enumerable.Range(0, 18)
                    .Select(i => ($@"C:\buf\aud_{T0.AddHours(2).AddSeconds(i * 5):yyyyMMdd-HHmmss}.wav", 1L))
                    .ToList(),
                PlusTwo,
                "aud_",
                ".wav");

            var prunable = SegmentTimeline.SelectPrunable(chunks, 15, 15, 5, maxTotalBytes: 0);

            Assert.AreEqual(4, prunable.Count);
            CollectionAssert.AreEqual(chunks.Take(4).ToList(), prunable);
            Assert.IsTrue(prunable.All(c => c.Path.Contains("aud_")));
        }

        [TestMethod]
        public void SelectPrunable_EmptyInput_ReturnsEmpty()
        {
            Assert.AreEqual(0, SegmentTimeline.SelectPrunable(null, 15, 15, 5, 0).Count);
        }
    }
}
