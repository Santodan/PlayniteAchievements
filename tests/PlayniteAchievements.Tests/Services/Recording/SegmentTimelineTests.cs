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
                Path = $@"C:\buf\seg_{startUtc:yyyyMMdd-HHmmss}.ts",
                StartUtc = startUtc,
                SizeBytes = size
            };
        }

        // === Filename parsing ===

        [TestMethod]
        public void ParseSegments_ConvertsLocalStampsToUtcWithInjectedZone()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[] { (@"C:\buf\seg_20260101-140000.ts", 123L) },
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
                    (@"C:\buf\seg_20260101-140010.ts", 1L),
                    (@"C:\buf\clip_abc.mp4", 1L),
                    (@"C:\buf\seg_20260101-140000.ts", 1L),
                    (@"C:\buf\seg_garbage.ts", 1L),
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
                    (@"C:\buf\seg_20260101-140000.ts", 1L),
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
                    (@"C:\buf\seg_20260101-140000.ts", 1L),
                    (@"C:\buf\aud_20260101-140000.wav", 1L)
                },
                PlusTwo);

            Assert.AreEqual(1, segments.Count);
            StringAssert.EndsWith(segments[0].Path, "seg_20260101-140000.ts");
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

        // === Clip window ===

        [TestMethod]
        public void ComputeClipWindow_PreciseUnlock_PreRollThroughToastDismissal()
        {
            var captureStart = T0;
            var unlock = T0.AddSeconds(60);
            var detection = unlock.AddSeconds(10);
            var toast = detection.AddSeconds(1);

            var (start, end) = SegmentTimeline.ComputeClipWindow(
                unlock, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            Assert.AreEqual(unlock.AddSeconds(-15), start);
            // End is guaranteed past the toast's dismissal: shown + 6s display + 1s tail.
            Assert.AreEqual(toast.AddSeconds(7), end);
            // Length emerges from the anchors: 15 pre-roll + 11 gap + 7 toast = 33s.
            Assert.AreEqual(33, (end - start).TotalSeconds, 0.001);
        }

        [TestMethod]
        public void ComputeClipWindow_CoarseUnlock_PreRollBehindWorstCasePollInterval()
        {
            var captureStart = T0;
            var detection = T0.AddSeconds(120);
            var toast = detection.AddSeconds(1);

            var (start, end) = SegmentTimeline.ComputeClipWindow(
                null, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            // Unlock is somewhere in [detection - N, detection]; the pre-roll applies behind
            // the worst case, so the clip still has 15s before the true unlock moment.
            Assert.AreEqual(detection.AddSeconds(-30), start);
            Assert.AreEqual(toast.AddSeconds(7), end);
        }

        [TestMethod]
        public void ComputeClipWindow_LongerToastDurationExtendsTheEnd()
        {
            var captureStart = T0;
            var unlock = T0.AddSeconds(60);
            var detection = unlock.AddSeconds(1);
            var toast = detection.AddSeconds(1);

            var (start, end) = SegmentTimeline.ComputeClipWindow(
                unlock, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 10);

            Assert.AreEqual(unlock.AddSeconds(-15), start);
            Assert.AreEqual(toast.AddSeconds(11), end);
        }

        [TestMethod]
        public void ComputeClipWindow_NoToast_FallsBackToDetectionAnchoredEnd()
        {
            var detection = T0.AddSeconds(60);

            var (_, end) = SegmentTimeline.ComputeClipWindow(
                null, detection, null, T0, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            Assert.AreEqual(detection.AddSeconds(5), end);
        }

        [TestMethod]
        public void ComputeClipWindow_MidnightUnlockIsTreatedAsCoarse()
        {
            var captureStart = new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc);
            var unlock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var detection = unlock.AddMinutes(5);
            var toast = detection.AddSeconds(1);

            var (start, _) = SegmentTimeline.ComputeClipWindow(
                unlock, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            // Coarse anchor: detection - N - preRoll, not unlock - preRoll.
            Assert.AreEqual(detection.AddSeconds(-30), start);
        }

        [TestMethod]
        public void ComputeClipWindow_PreSessionUnlockIsTreatedAsCoarse()
        {
            var captureStart = T0;
            var unlock = T0.AddMinutes(-30);
            var detection = T0.AddSeconds(120);
            var toast = detection.AddSeconds(1);

            var (start, _) = SegmentTimeline.ComputeClipWindow(
                unlock, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            Assert.AreEqual(detection.AddSeconds(-30), start);
        }

        [TestMethod]
        public void ComputeClipWindow_FutureUnlockTimestampIsTreatedAsCoarse()
        {
            var captureStart = T0;
            var detection = T0.AddSeconds(120);
            var unlock = detection.AddMinutes(10);
            var toast = detection.AddSeconds(1);

            var (start, _) = SegmentTimeline.ComputeClipWindow(
                unlock, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            Assert.AreEqual(detection.AddSeconds(-30), start);
        }

        [TestMethod]
        public void ComputeClipWindow_ClampsToCaptureStart()
        {
            var captureStart = T0;
            var unlock = T0.AddSeconds(2);
            var detection = T0.AddSeconds(3);
            var toast = detection.AddSeconds(1);

            var (start, _) = SegmentTimeline.ComputeClipWindow(
                unlock, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            Assert.AreEqual(captureStart, start);
        }

        [TestMethod]
        public void ComputeClipWindow_ClampsToOldestSegmentWhenLater()
        {
            var captureStart = T0;
            var oldestSegment = T0.AddSeconds(50);
            var unlock = T0.AddSeconds(52);
            var detection = T0.AddSeconds(53);
            var toast = detection.AddSeconds(1);

            var (start, _) = SegmentTimeline.ComputeClipWindow(
                unlock, detection, toast, captureStart, oldestSegment,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            Assert.AreEqual(oldestSegment, start);
        }

        [TestMethod]
        public void ComputeClipWindow_HardCapsAtBufferDepth_EndAnchorWins()
        {
            var captureStart = T0;
            var detection = T0.AddSeconds(300);
            // Pathologically late toast: raw window would be 30 + 90 + 7 = 127s > depth (60s).
            var toast = detection.AddSeconds(90);

            var (start, end) = SegmentTimeline.ComputeClipWindow(
                null, detection, toast, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15, toastVisibleSeconds: 6);

            Assert.AreEqual(60, (end - start).TotalSeconds, 0.001);
            // The toast end anchor is preserved; the start slides forward.
            Assert.AreEqual(toast.AddSeconds(7), end);
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
