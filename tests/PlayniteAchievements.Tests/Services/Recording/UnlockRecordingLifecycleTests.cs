using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class UnlockRecordingLifecycleTests
    {
        [TestMethod]
        public void SessionShutdown_DrainsClipWithoutCancellingItsPendingToastTrack()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("private async Task ShutdownSessionAsync", StringComparison.Ordinal);
            var end = source.IndexOf("// === Unlock handling ===", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start);
            var shutdown = source.Substring(start, end - start);
            StringAssert.Contains(shutdown, "Task.WhenAll(inFlight)");
            Assert.IsFalse(shutdown.Contains("TrackTcs?.TrySetResult(null)"),
                "Stopping a game must not discard an overlay track that is still rendering.");
        }

        [TestMethod]
        public void GameStart_GatesCaptureBeforeBuildingASession()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("public void OnGameStarted", StringComparison.Ordinal);
            var session = source.IndexOf("var session = new CaptureSession", start, StringComparison.Ordinal);
            var gate = source.IndexOf("ShouldCaptureGame(game, persisted", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && session > start);
            Assert.IsTrue(gate > start && gate < session,
                "Capture must be gated before a session, its buffer and its recorders are created.");
        }

        [TestMethod]
        public void CaptureGate_ChecksBothExclusionAndProviderCapability()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("private bool ShouldCaptureGame", StringComparison.Ordinal);
            var end = source.IndexOf("public void OnGameStopped", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start);
            var gate = source.Substring(start, end - start);

            // A game the user excluded from refreshes must not be captured either: the exclusion is
            // the user saying the plugin should leave that game alone.
            StringAssert.Contains(gate, "GetExcludedRefreshGameIds");
            // And a game no enabled provider can service can never report an unlock, so a clip for
            // it can never be requested.
            StringAssert.Contains(gate, "_isAnyProviderCapable");
            // The capability delegate is optional, so missing wiring must not silently kill capture.
            StringAssert.Contains(gate, "_isAnyProviderCapable != null");
        }

        [TestMethod]
        public void HapticRemoval_NeverDropsRecordedAudioOnUncertainty()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("private SegmentTimeline.ClipPlan TryRemoveHapticAudio", StringComparison.Ordinal);
            var end = source.IndexOf("private void TryDeleteCleanedAudio", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start);
            var removal = source.Substring(start, end - start);
            StringAssert.Contains(removal, "HasHapticHole");
            StringAssert.Contains(removal, "IsReferenceSafelyAbsentOrRemoved");
            StringAssert.Contains(removal, "maximumResidualCorrelation");
            StringAssert.Contains(removal, "commitVerifiedBlocksOnWeakPass: true");
            StringAssert.Contains(removal, "minimumCorrelation: HapticCancellationMinimumCorrelation");
            StringAssert.Contains(removal, "attemptVerifiedBlocksWhenGloballyClean: true");
            StringAssert.Contains(removal, "independentChannelGains: true");
            StringAssert.Contains(removal, "gainCrossfadeFrames: 0");
            StringAssert.Contains(removal, "fractionalLagSteps: 32");
            StringAssert.Contains(removal, "referenceCovered && reference == null");
            StringAssert.Contains(removal, "keeping the recorded audio");
            Assert.IsFalse(
                removal.Contains("return null;"),
                "A non-null recorded plan must never become the exporter's no-audio sentinel.");
            Assert.IsFalse(
                removal.Contains("without audio"),
                "Haptic-removal uncertainty must retain the recorded audio, buzz included.");
        }

        [TestMethod]
        public void ChimeAndHaptics_UseTheSameTimestampAlignedSubtractionPath()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var marker = "PcmAudio.CancelCorrelated(";
            var first = source.IndexOf(marker, StringComparison.Ordinal);
            var second = source.IndexOf(marker, first + marker.Length, StringComparison.Ordinal);

            Assert.IsTrue(first >= 0 && second > first,
                "Both chime cleanup and haptic cleanup must use the shared subtraction path.");

            var pcm = File.ReadAllText(FindRepoFile(
                "source", "Services", "Capture", "PcmAudio.cs"));
            StringAssert.Contains(pcm, "FitKnownBlock(");
            Assert.IsFalse(pcm.Contains("private static BlockFit FitBlock("));
            Assert.IsFalse(pcm.Contains("SearchLags("));
        }

        [TestMethod]
        public void EveryAudioCapturePath_UsesOneTickPreciseFrameTimeline()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            StringAssert.Contains(recorder, "AttachTimestampedCancellationTracks");
            StringAssert.Contains(recorder, "WriteStampedAuxiliaryPacket");
            Assert.IsFalse(recorder.Contains("ReferenceTeeSampleProvider"));
            StringAssert.Contains(recorder, "RecordingPaths.AudioFrameAt(");
            var utcMarker = "RecordingPaths.AudioFrameUtc(";
            var firstUtc = recorder.IndexOf(utcMarker, StringComparison.Ordinal);
            var secondUtc = recorder.IndexOf(utcMarker, firstUtc + utcMarker.Length, StringComparison.Ordinal);
            Assert.IsTrue(firstUtc >= 0 && secondUtc > firstUtc,
                "Both sparse haptic chunks and pump-paced chunks must use the shared frame grid.");
            Assert.IsFalse(recorder.Contains("AddSeconds(startFrame /"));
            Assert.IsFalse(recorder.Contains("_chunkStartWallClockSamples / (double)"));

            var capture = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "ProcessLoopbackCapture.cs"));
            var qpcMarker = "CaptureTimelineClock.FromQpc100ns(";
            var firstQpc = capture.IndexOf(qpcMarker, StringComparison.Ordinal);
            var secondQpc = capture.IndexOf(qpcMarker, firstQpc + qpcMarker.Length, StringComparison.Ordinal);
            Assert.IsTrue(firstQpc >= 0 && secondQpc > firstQpc,
                "Initial anchors and packet placement must use the same one-sample QPC projection.");
            Assert.IsFalse(capture.Contains("CaptureTimelineClock.UtcNow.AddTicks(-"));

            var paths = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "RecordingPaths.cs"));
            StringAssert.Contains(paths, "yyyyMMdd-HHmmssfffffff'Z'");
        }

        [TestMethod]
        public void HapticRemoval_KeepsVerifiedWorkWhenAnotherReferenceFails()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("private SegmentTimeline.ClipPlan TryRemoveHapticAudio", StringComparison.Ordinal);
            var end = source.IndexOf("private void TryDeleteCleanedAudio", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start);
            var removal = source.Substring(start, end - start);
            var uncertainty = removal.IndexOf(
                "!PcmAudio.IsReferenceSafelyAbsentOrRemoved", StringComparison.Ordinal);
            var nextOutcome = removal.IndexOf("removedAny |=", uncertainty, StringComparison.Ordinal);

            Assert.IsTrue(uncertainty >= 0 && nextOutcome > uncertainty);
            var uncertainBranch = removal.Substring(uncertainty, nextOutcome - uncertainty);
            StringAssert.Contains(uncertainBranch, "continue;");
            Assert.IsFalse(
                uncertainBranch.Contains("return audioPlan;"),
                "One bad controller reference must not discard another reference's verified cleanup.");
            StringAssert.Contains(removal, "unremovedActiveReferences");
            StringAssert.Contains(removal, "verified partial haptic cleanup");
        }

        [TestMethod]
        public void ClipExport_RetriesOriginalAudioIfCleanedTrackCannotBeMuxed()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("// Audio rides the same window", StringComparison.Ordinal);
            var end = source.IndexOf("if (!ok)", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start);
            var export = source.Substring(start, end - start);
            StringAssert.Contains(export, "var recordedAudioPlan = audioPlan;");
            StringAssert.Contains(export, "selectedAudioPlan ?? recordedAudioPlan");
            StringAssert.Contains(export, "cleanedAudioDirectory != null && recordedAudioPlan != null");
            StringAssert.Contains(export, "exporter.Export(");
            StringAssert.Contains(export, "plan, recordedAudioPlan, tempPath");
            StringAssert.Contains(export, "retrying with the");
            StringAssert.Contains(export, "original recorded audio");
        }

        [TestMethod]
        public void ClipExporter_DoesNotTurnAPlannedTrackIntoVideoOnlyOnReadFailure()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Capture", "MediaFoundationClipExporter.cs"));

            StringAssert.Contains(source, "audioStream = AddAudioStream");
            StringAssert.Contains(source, "Planned clip audio produced no samples.");
            StringAssert.Contains(source, "hasAudio = audio.MoveNext();");
            Assert.IsFalse(
                source.Contains("private bool TryMoveNext"),
                "Audio iterator failures must reach Export so the original-audio retry can run.");
            Assert.IsFalse(
                source.Contains("Clip audio read failed; clip will be video-only."),
                "A supplied audio plan must not silently degrade to an empty track.");
        }

        [TestMethod]
        public void OverlayFailure_KeepsTheBaseClipInsteadOfDroppingItsAudio()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Capture", "MediaFoundationOverlayReencoder.cs"));

            StringAssert.Contains(source, "aborting the overlay");
            StringAssert.Contains(source, "caller keeps the toastless clip with its audio");
            StringAssert.Contains(source, "base clip declared audio but produced no samples");
            Assert.IsFalse(
                source.Contains("Base clip has no usable audio stream; re-encoding video only."),
                "An unexpected overlay audio failure must fall back to the intact base clip.");
        }

        [TestMethod]
        public void HapticRecorder_PropagatesIncompleteAndUncapturableEndpointScans()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            var scan = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "RenderEndpointScan.cs"));

            StringAssert.Contains(recorder, "out var scanComplete, out var hasDefaultHapticEndpoint");
            StringAssert.Contains(recorder, "!scanComplete || hasDefaultHapticEndpoint");
            StringAssert.Contains(scan, "out bool scanComplete");
            StringAssert.Contains(scan, "out bool hasUncapturableDefaultHapticEndpoint");
            StringAssert.Contains(scan, "scanComplete = false");
            StringAssert.Contains(scan, "hasUncapturableDefaultHapticEndpoint |= keptAsOutput");
        }

        [TestMethod]
        public void MicrophoneCapture_NeverFallsBackToAnUnverifiedDefaultInput()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            var selector = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "MicrophoneSelector.cs"));

            StringAssert.Contains(recorder, "omitted-no-safe-input");
            Assert.IsFalse(
                recorder.Contains("micDevice == null\r\n                                ? new WasapiCapture()") ||
                recorder.Contains("micDevice == null\n                                ? new WasapiCapture()"),
                "A null safe-device selection must omit the microphone, not use Windows default.");
            StringAssert.Contains(selector, "A controller microphone is never selected");
            StringAssert.Contains(selector, "microphone capture is omitted");
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = directory.FullName;
                foreach (var part in parts)
                {
                    path = Path.Combine(path, part);
                }

                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Repository file not found: " + Path.Combine(parts));
            return null;
        }
    }
}
