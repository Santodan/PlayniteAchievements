using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;
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

        /// <summary>A buffer file of a given byte size, for budget-cutoff tests.</summary>
        private static SegmentTimeline.SegmentInfo Bytes(DateTime startUtc, long sizeBytes)
        {
            return new SegmentTimeline.SegmentInfo
            {
                Path = $@"C:\buf\seg_{startUtc:yyyyMMdd-HHmmss}.mp4",
                StartUtc = startUtc,
                SizeBytes = sizeBytes
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
            // The observation guard wins by two seconds, ensuring the locally observed event is
            // present even when the provider anchor's notification slot ended first.
            Assert.AreEqual(detection.AddSeconds(1), window.EndUtc);
            Assert.AreEqual(26, (window.EndUtc - window.StartUtc).TotalSeconds, 0.001);
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
        public void ComputeClipWindow_UnreachableUnlockTimestamp_AnchorsOnDetectionWithFullPreRoll()
        {
            // The Spider-Man case: Steam recorded SetAchievement two minutes before StoreStats
            // popped the overlay, so the timestamp points at footage that has left the buffer.
            // The clip must follow the player -- a full pre-roll ending on the pop -- rather than
            // collapse onto the floored start and show an unrelated moment.
            var captureStart = T0;
            var detection = T0.AddSeconds(300);
            var unlock = T0.AddSeconds(60);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(detection, window.ToastAnchorUtc);
            Assert.AreEqual(detection.AddSeconds(-15), window.StartUtc);
            Assert.AreEqual(detection.AddSeconds(9), window.EndUtc);
            Assert.AreEqual(24, (window.EndUtc - window.StartUtc).TotalSeconds, 0.001);
        }

        [TestMethod]
        public void ComputeClipWindow_ReachableUnlockTimestamp_StillAnchorsOnIt()
        {
            // The ordinary case -- SetAchievement and StoreStats back to back -- must be untouched.
            var captureStart = T0;
            var detection = T0.AddSeconds(300);
            var unlock = detection.AddSeconds(-4);

            var window = SegmentTimeline.ComputeClipWindow(
                unlock, detection, captureStart, null,
                pollIntervalSeconds: 15, preRollSeconds: 15,
                toastSlotSeconds: 8, tailSeconds: 1);

            Assert.AreEqual(unlock, window.ToastAnchorUtc);
            Assert.AreEqual(unlock.AddSeconds(-15), window.StartUtc);
        }

        [TestMethod]
        public void ParseSegments_ExplicitUtcStamp_IgnoresLocalTimeZone()
        {
            var segments = SegmentTimeline.ParseSegments(
                new[] { (@"C:\buf\seg_20260101-120000437Z_1884x976.mp4", 123L) },
                PlusTwo);

            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual(
                new DateTime(2026, 1, 1, 12, 0, 0, 437, DateTimeKind.Utc),
                segments[0].StartUtc);
            Assert.AreEqual(DateTimeKind.Utc, segments[0].StartUtc.Kind);
        }

        [TestMethod]
        public void ComputeClipWindow_EarlyAnchor_CannotCutOffObservedEvent()
        {
            // Bills Must Be Paid supplied a Steam epoch 8.1s before the Windows-clock file event.
            // The old 7s toast+tail window ended before the purchase that triggered the unlock.
            var captureStart = T0;
            var reported = T0.AddSeconds(60);
            var observed = reported.AddSeconds(8.1);

            var window = SegmentTimeline.ComputeClipWindow(
                reported, observed, captureStart, null,
                pollIntervalSeconds: 10, preRollSeconds: 15,
                toastSlotSeconds: 5, tailSeconds: 2);

            Assert.AreEqual(reported, window.ToastAnchorUtc);
            Assert.AreEqual(observed.AddSeconds(2), window.EndUtc);
            Assert.IsTrue(window.StartUtc <= reported);
            Assert.IsTrue(window.EndUtc > observed);
        }

        // === Buffer budget ===

        [TestMethod]
        public void ResolveBudgetCutoffUtc_KeepsTheNewestFilesThatFitTheBudget()
        {
            // Four 100-byte files a second apart; a 250-byte budget holds the newest two.
            var files = new List<SegmentTimeline.SegmentInfo>
            {
                Bytes(T0, 100),
                Bytes(T0.AddSeconds(1), 100),
                Bytes(T0.AddSeconds(2), 100),
                Bytes(T0.AddSeconds(3), 100)
            };

            var cutoff = SegmentTimeline.ResolveBudgetCutoffUtc(files, 250, DateTime.MaxValue);

            Assert.AreEqual(T0.AddSeconds(2), cutoff);
            CollectionAssert.AreEqual(
                new[] { files[0], files[1] },
                SegmentTimeline.SelectPrunable(files, cutoff).ToArray());
        }

        [TestMethod]
        public void ResolveBudgetCutoffUtc_MixesVideoAndAudioIntoOneSpan()
        {
            // Video and audio share the budget and the retained span: a clip needs both.
            var files = new List<SegmentTimeline.SegmentInfo>
            {
                Bytes(T0, 100),
                Bytes(T0, 20),
                Bytes(T0.AddSeconds(5), 100),
                Bytes(T0.AddSeconds(5), 20)
            };

            var cutoff = SegmentTimeline.ResolveBudgetCutoffUtc(files, 130, DateTime.MaxValue);

            // The newest pair (120 bytes) fits; adding either older file would exceed 130.
            Assert.AreEqual(T0.AddSeconds(5), cutoff);
        }

        [TestMethod]
        public void ResolveBudgetCutoffUtc_FloorWinsWhenTheBudgetCannotHoldAClipWindow()
        {
            var files = new List<SegmentTimeline.SegmentInfo>
            {
                Bytes(T0, 100),
                Bytes(T0.AddSeconds(5), 100),
                Bytes(T0.AddSeconds(10), 100)
            };

            // A budget that holds only the newest file, but the floor demands the last 10 seconds:
            // the buffer overruns the budget rather than leaving clips that cannot be built.
            var cutoff = SegmentTimeline.ResolveBudgetCutoffUtc(files, 100, T0);

            Assert.AreEqual(T0, cutoff);
            Assert.AreEqual(0, SegmentTimeline.SelectPrunable(files, cutoff).Count);
        }

        [TestMethod]
        public void ResolveBudgetCutoffUtc_NonPositiveBudgetKeepsEverything()
        {
            var files = new List<SegmentTimeline.SegmentInfo> { Bytes(T0, 100) };

            Assert.AreEqual(DateTime.MinValue, SegmentTimeline.ResolveBudgetCutoffUtc(files, 0, DateTime.MaxValue));
            Assert.AreEqual(DateTime.MinValue, SegmentTimeline.ResolveBudgetCutoffUtc(files, -1, DateTime.MaxValue));
            Assert.AreEqual(DateTime.MinValue, SegmentTimeline.ResolveBudgetCutoffUtc(null, 100, DateTime.MaxValue));
        }

        [TestMethod]
        public void ResolveBudgetCutoffUtc_BudgetSmallerThanOneFileStillKeepsTheNewest()
        {
            var files = new List<SegmentTimeline.SegmentInfo>
            {
                Bytes(T0, 100),
                Bytes(T0.AddSeconds(5), 100)
            };

            var cutoff = SegmentTimeline.ResolveBudgetCutoffUtc(files, 10, DateTime.MaxValue);

            Assert.AreEqual(T0.AddSeconds(5), cutoff);
            Assert.AreEqual(1, SegmentTimeline.SelectPrunable(files, cutoff).Count);
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
            Assert.AreEqual(T0.AddSeconds(3), plan.StartUtc);
            Assert.AreEqual(3, plan.StartOffsetSeconds, 0.001);
            Assert.AreEqual(9, plan.DurationSeconds, 0.001);
        }

        [TestMethod]
        public void PlanClip_PreservesSubMillisecondStartAndEndExactly()
        {
            var segment = Segment(T0.AddTicks(17));
            var requestedStart = T0.AddSeconds(1).AddTicks(2345);
            var requestedEnd = T0.AddSeconds(4).AddTicks(6789);

            var plan = SegmentTimeline.PlanClip(
                new[] { segment }, requestedStart, requestedEnd, 5);

            Assert.IsNotNull(plan);
            Assert.AreEqual(requestedStart, plan.StartUtc);
            Assert.AreEqual(requestedEnd, plan.EndUtc);
            Assert.AreEqual(
                requestedStart - segment.StartUtc,
                plan.StartUtc - plan.Segments[0].StartUtc);
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
        public void ParseSegments_ReadsMillisecondsFromTheStamp()
        {
            // Sub-second precision is what keeps audio aligned to picture: the exporter trims each
            // stream by the offset from its file's stamp to the window start, so a stamp rounded to
            // the second shifts that stream by up to a second.
            var segments = SegmentTimeline.ParseSegments(
                new[]
                {
                    (@"C:\buf\seg_20260101-140000437_1884x976.mp4", 1L),
                    (@"C:\buf\seg_20260101-140005150_1884x976.mp4", 1L)
                },
                PlusTwo);

            Assert.AreEqual(2, segments.Count);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 0, 437), segments[0].StartUtc);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 5, 150), segments[1].StartUtc);
            Assert.AreEqual(1884, segments[0].Width);
        }

        [TestMethod]
        public void ParseSegments_ReadsMillisecondsFromAudioChunkNames()
        {
            var chunks = SegmentTimeline.ParseSegments(
                new[] { (@"C:\buf\aud_20260101-140002875.wav", 1L) },
                PlusTwo,
                "aud_",
                ".wav");

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 2, 875), chunks[0].StartUtc);
        }

        [TestMethod]
        public void ParseSegments_StillReadsLegacyUtcMillisecondNames()
        {
            var expected = new DateTime(2026, 1, 1, 14, 0, 0, 437, DateTimeKind.Utc);
            var segments = SegmentTimeline.ParseSegments(
                new[] { (@"C:\buf\seg_20260101-140000437Z_1884x976.mp4", 1L) },
                PlusTwo);

            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual(expected, segments[0].StartUtc);
            Assert.AreEqual(DateTimeKind.Utc, segments[0].StartUtc.Kind);
        }

        [TestMethod]
        public void ParseSegments_StillReadsSecondResolutionStamps()
        {
            // Buffers written before milliseconds were included must keep parsing.
            var segments = SegmentTimeline.ParseSegments(
                new[]
                {
                    (@"C:\buf\seg_20260101-140000.mp4", 1L),
                    (@"C:\buf\seg_20260101-140005_1884x976.mp4", 1L)
                },
                PlusTwo);

            Assert.AreEqual(2, segments.Count);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 0), segments[0].StartUtc);
            Assert.AreEqual(new DateTime(2026, 1, 1, 12, 0, 5), segments[1].StartUtc);
            Assert.AreEqual(1884, segments[1].Width);
        }

        [TestMethod]
        public void BuildSegmentFileName_RoundTripsThroughTheParser()
        {
            var utcStart = new DateTime(2026, 1, 1, 12, 0, 7, 42, DateTimeKind.Utc)
                .AddTicks(6789);
            var name = RecordingPaths.BuildSegmentFileName(utcStart, 1884, 976);

            var segments = SegmentTimeline.ParseSegments(
                new[] { ($@"C:\buf\{name}", 1L) }, PlusTwo);

            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual(utcStart, segments[0].StartUtc);
            StringAssert.Contains(name, "1200070426789Z_");
            Assert.AreEqual(1884, segments[0].Width);
            Assert.AreEqual(976, segments[0].Height);
        }

        [TestMethod]
        public void BuildAudioChunkFileName_RoundTripsThroughTheParser()
        {
            var utcStart = new DateTime(2026, 1, 1, 12, 0, 3, 601, DateTimeKind.Utc)
                .AddTicks(4321);
            var prefixes = new[]
            {
                RecordingPaths.AudioChunkFilePrefix,
                RecordingPaths.ChimeChunkFilePrefix,
                RecordingPaths.GameReferenceChunkFilePrefix,
                RecordingPaths.HapticReferenceChunkFilePrefix(0),
                RecordingPaths.HapticReferenceChunkFilePrefix(3),
            };

            foreach (var prefix in prefixes)
            {
                var name = RecordingPaths.BuildAudioChunkFileName(prefix, utcStart);
                var chunks = SegmentTimeline.ParseSegments(
                    new[] { ($@"C:\buf\{name}", 1L) },
                    PlusTwo,
                    prefix,
                    RecordingPaths.AudioChunkFileExtension);

                Assert.AreEqual(1, chunks.Count, prefix);
                Assert.AreEqual(utcStart, chunks[0].StartUtc, prefix);
                StringAssert.Contains(name, "1200036014321Z.wav", prefix);
            }
        }

        [TestMethod]
        public void AudioFrameTimeline_RoundTripsWithoutCrossTrackDrift()
        {
            var origin = T0.AddTicks(73);
            foreach (var frame in new long[] { -48001, -1, 0, 1, 2, 47, 48, 47999, 48000, 48001, 17_280_000 })
            {
                var utc = RecordingPaths.AudioFrameUtc(origin, frame, PcmAudio.SampleRate);
                Assert.AreEqual(
                    frame,
                    RecordingPaths.AudioFrameAt(origin, utc, PcmAudio.SampleRate),
                    $"frame {frame} did not survive UTC placement");
                Assert.AreEqual(
                    Math.Max(0, frame) * PcmAudio.BlockAlign,
                    PcmAudio.TicksToAlignedBytes(Math.Max(0, (utc - origin).Ticks)),
                    $"frame {frame} did not survive PCM placement");
            }
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
        public void SelectPrunable_TakesEverythingStartingBeforeTheCutoff()
        {
            var segments = Enumerable.Range(0, 6)
                .Select(i => Segment(T0.AddSeconds(i * 5)))
                .ToList();

            var prunable = SegmentTimeline.SelectPrunable(segments, T0.AddSeconds(15));

            Assert.AreEqual(3, prunable.Count);
            CollectionAssert.AreEqual(segments.Take(3).ToArray(), prunable.ToArray());
        }

        [TestMethod]
        public void SelectPrunable_AudioChunksSharTheVideoCutoff()
        {
            // One cutoff governs every stream, so a clip always has picture and sound over the
            // same span.
            var chunks = SegmentTimeline.ParseSegments(
                Enumerable.Range(0, 6)
                    .Select(i => ($@"C:\buf\aud_{T0.AddHours(2).AddSeconds(i * 5):yyyyMMdd-HHmmss}.wav", 1L))
                    .ToList(),
                PlusTwo,
                "aud_",
                ".wav");

            var prunable = SegmentTimeline.SelectPrunable(chunks, T0.AddSeconds(15));

            Assert.AreEqual(3, prunable.Count);
            Assert.IsTrue(prunable.All(c => c.Path.Contains("aud_")));
        }

        [TestMethod]
        public void SelectPrunable_EmptyInputOrMinValueCutoff_ReturnsEmpty()
        {
            Assert.AreEqual(0, SegmentTimeline.SelectPrunable(null, T0).Count);
            Assert.AreEqual(
                0,
                SegmentTimeline.SelectPrunable(
                    new List<SegmentTimeline.SegmentInfo> { Segment(T0) }, DateTime.MinValue).Count);
        }
    }
}
