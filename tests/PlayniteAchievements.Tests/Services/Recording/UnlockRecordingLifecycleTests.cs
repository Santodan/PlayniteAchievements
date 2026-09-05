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
        public void RecordingModes_UseOneEndpointAndCleanupFailsOpenToAudibleAudio()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            StringAssert.Contains(recorder, "_source == RecordingAudioSource.GameOnly");
            StringAssert.Contains(recorder, "includeProcessTree: false");
            StringAssert.Contains(recorder, "ProcessLoopbackCapture.ForEndpoint(speaker.Id)");
            StringAssert.Contains(recorder, "return ProcessLoopbackCapture.ForEndpoint(fallbackId);");
            StringAssert.Contains(recorder, "haptic-free full-system speaker audio");

            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = service.IndexOf("PrepareClipAudio(", StringComparison.Ordinal);
            var end = service.IndexOf("private static PcmCancellationOutcome SubtractReference", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start);
            var cleanup = service.Substring(start, end - start);
            StringAssert.Contains(cleanup, "return (audioPlan, null);");
            StringAssert.Contains(cleanup, "TryReadReference(session, startUtc, endUtc)");
            StringAssert.Contains(cleanup, "keeping the haptic-free");
        }

        [TestMethod]
        public void FullSystem_AttachesAnIncludeSoundHostReference()
        {
            // Full System keeps the speaker endpoint as the main track and captures the sound
            // host's own render (include-tree on its pid) as the reference export subtracts, so
            // the live unlock sound leaves the clip without any file-based removal. No pid means
            // no reference: the clip keeps the live sound.
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            StringAssert.Contains(recorder, "ReferenceTrackKind.IncludeSoundHostTree");
            StringAssert.Contains(recorder, "_soundHostProcessId");
            StringAssert.Contains(recorder, "new ProcessLoopbackCapture(hostPid.Value, includeProcessTree: true)");
            StringAssert.Contains(recorder, "clips keep the live unlock sound");

            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "RequiresReferenceCleanup");
            StringAssert.Contains(service, "_getSoundHostProcessId");
            StringAssert.Contains(service, "[Recording] Reference subtraction ({kind})");
        }

        [TestMethod]
        public void NoChimeRemovalPathExists()
        {
            // The removal engine, its occurrence registry, the Playnite-tree sidecar and the
            // composite authorization gates all went with UniPlaySong; nothing may grow them back.
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            foreach (var banned in new[]
            {
                "ChimeRemovalEngine", "_soundOccurrences", "ChimeCompositeAuthorized",
                "ChimeCompositeClaimed", "ChimeRecorder", "ChimeCleanupTasks", "chm_", "UniPlaySong",
            })
            {
                Assert.IsFalse(service.Contains(banned), banned);
            }

            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            foreach (var banned in new[] { "capturePlayniteChimes", "PlayniteChimeCaptureMode", "ChimeChunkFilePrefix" })
            {
                Assert.IsFalse(recorder.Contains(banned), banned);
            }

            var recordingDirectory = Path.GetDirectoryName(
                FindRepoFile("source", "Services", "Recording", "UnlockRecordingService.cs"));
            Assert.IsFalse(File.Exists(Path.Combine(recordingDirectory, "WaveSoundOccurrence.cs")));
            Assert.IsFalse(File.Exists(Path.Combine(
                Path.GetDirectoryName(recordingDirectory), "Capture", "ChimeRemovalEngine.cs")));
        }

        [TestMethod]
        public void GameOnly_MissingGameTreeNeverTreatsExcludeTreeAsSafe()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = service.IndexOf("if (gamePcm == null)", StringComparison.Ordinal);
            var end = service.IndexOf("var purgeOutcome", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start);
            var missingGame = service.Substring(start, end - start);

            StringAssert.Contains(missingGame, "cannot be proven free of game audio");
            StringAssert.Contains(missingGame, "return null;");
            Assert.IsFalse(
                missingGame.Contains("return reference;"),
                "An empty game-tree capture is indistinguishable from a process-tree miss.");
        }

        [TestMethod]
        public void ChimeComposite_IsAddedOnlyWhenTheLiveSoundWasRemoved()
        {
            // One chime per clip, never zero and never two: the composite is mixed only after the
            // reference subtraction verifiably took the live sound out; otherwise the live sound
            // stays and no copy is added.
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "public bool LiveSoundRemoved;");
            StringAssert.Contains(service, "request.LiveSoundRemoved = true;");
            StringAssert.Contains(service, "if (chimePcm != null && !liveSoundRemoved)");
            StringAssert.Contains(service, "so no composited");

            // A partial commit ships its cleaned blocks but a block restored as recorded still
            // carries the live sound, so completeness (no restored blocks) gates the composite.
            var set = service.IndexOf("request.LiveSoundRemoved = true;", StringComparison.Ordinal);
            var complete = service.LastIndexOf("if (complete)", set, StringComparison.Ordinal);
            var defined = service.IndexOf(
                "ReferenceCancellationPolicy.IsComplete(cancellation)", StringComparison.Ordinal);
            Assert.IsTrue(defined >= 0 && complete > defined && complete < set,
                "Only a complete, verified subtraction may mark the live sound removed.");
        }

        [TestMethod]
        public void FullSystem_ReferenceSubtractionRelocksPerBlock()
        {
            // A recorder tear inside the chime leaves its tail at another lag; the sound-host
            // reference re-locks per block, while the Game Only reference keeps one lag.
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "kind == ReferenceTrackKind.IncludeSoundHostTree");
            StringAssert.Contains(service, "ReferenceCancellationPolicy.BlockRelockRadiusFrames");
            StringAssert.Contains(service, "blockLagRadiusFrames: relockFrames");

            var policy = File.ReadAllText(FindRepoFile(
                "source", "Services", "Capture", "ReferenceCancellationPolicy.cs"));
            StringAssert.Contains(policy, "public const int BlockRelockRadiusFrames = 480;");
        }

        [TestMethod]
        public void UnlockScreenshot_CurrentSegmentRetriesTheSameAnchorBeforeLiveFallback()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf(
                "internal System.Drawing.Bitmap TryCaptureAnchorFrame", StringComparison.Ordinal);
            var end = source.IndexOf(
                "private void OnAchievementUnlocked", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start);
            var capture = source.Substring(start, end - start);

            StringAssert.Contains(capture, "var retryCeilingUtc");
            StringAssert.Contains(capture, "var nominalCloseUtc");
            StringAssert.Contains(capture, "Thread.Sleep(100)");
            StringAssert.Contains(capture, "covering.Path");
            StringAssert.Contains(capture, "offsetSeconds");
            Assert.IsFalse(capture.Contains("ReferenceEquals(covering"),
                "A pre-created next segment makes newest-file identity an invalid open-file test.");
        }

        [TestMethod]
        public void CancellationPrimitive_HasNoMutingPath()
        {
            var pcm = File.ReadAllText(FindRepoFile(
                "source", "Services", "Capture", "PcmAudio.cs"));
            StringAssert.Contains(pcm, "RestoreBlock(");
            Assert.IsFalse(pcm.Contains("MuteBlock("));
            Assert.IsFalse(pcm.Contains("MutedBlocks"));
            Assert.IsFalse(pcm.Contains("muteUnverifiedBlocks"));
            // The chime-gate diagnostics (weakest audible block) went with the removal engine.
            Assert.IsFalse(pcm.Contains("WeakestBlock"));
        }

        [TestMethod]
        public void EpicInGameCapture_UsesTheLocalObservationClock()
        {
            var epic = File.ReadAllText(FindRepoFile(
                "source", "Providers", "Epic", "EpicDataProvider.cs"));
            StringAssert.Contains(
                epic,
                "UnlockAnchorPolicy = InGameUnlockAnchorPolicy.SourceObservation");
        }

        [TestMethod]
        public void ControllerDefaultOutput_KeepsProgramChannelsAndDropsActuatorChannels()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            StringAssert.Contains(recorder, "RenderEndpointScan.IsHapticEndpoint(speaker)");
            StringAssert.Contains(recorder, "ProcessLoopbackCapture.ForEndpointNative");
            StringAssert.Contains(recorder, "ExtractDualSenseProgramAudio");
            StringAssert.Contains(recorder, "native front L/R channels");

            var capture = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "ProcessLoopbackCapture.cs"));
            var start = capture.IndexOf("internal static byte[] ExtractDualSenseProgramAudio", StringComparison.Ordinal);
            var end = capture.IndexOf("private IAudioClient ActivateProcessLoopbackClient", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start);
            var extraction = capture.Substring(start, end - start);
            StringAssert.Contains(extraction, "Buffer.BlockCopy(source, sourceOffset, output");
            Assert.IsFalse(extraction.Contains("sourceOffset + 2 * sizeof(float)"));
        }

        [TestMethod]
        public void GameOnly_KeepsTheGameWitnessForIsolationSafety()
        {
            // The gam_ witness stays in Game Only only: an empty witness means the game renders
            // outside the tracked tree (so the reference holds the game and must not be
            // subtracted), and a mirrored game copy is purged against it. Full System has no use
            // for it, so it is created only under the Game Only branch.
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            var gameOnly = recorder.IndexOf(
                "_source == RecordingAudioSource.GameOnly && gamePid.HasValue", StringComparison.Ordinal);
            var witness = recorder.IndexOf(
                "_gameReferenceCapture = new ProcessLoopbackCapture(gamePid.Value, includeProcessTree: true)",
                StringComparison.Ordinal);
            var hostReference = recorder.IndexOf("ReferenceTrackKind.IncludeSoundHostTree;", StringComparison.Ordinal);
            Assert.IsTrue(gameOnly >= 0 && witness > gameOnly && hostReference > witness);
            StringAssert.Contains(recorder, "_writeGameReference = true");
            StringAssert.Contains(recorder, "RecordingPaths.GameReferenceChunkFilePrefix");

            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "if (kind != ReferenceTrackKind.ExcludeGameTree)");
        }

        [TestMethod]
        public void ChimeComposite_MixesTheResolvedFileExactlyOnce()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var reencodeStart = service.IndexOf(
                "private async Task<string> ReencodeWithTrackAsync", StringComparison.Ordinal);
            var reencodeEnd = service.IndexOf(
                "private static string SaveClipToUniquePath", reencodeStart, StringComparison.Ordinal);
            Assert.IsTrue(reencodeStart >= 0 && reencodeEnd > reencodeStart);
            var reencode = service.Substring(reencodeStart, reencodeEnd - reencodeStart);
            Assert.AreEqual(1, reencode.Split(new[] { "TryReadChimePcm(" },
                StringSplitOptions.None).Length - 1);
            Assert.AreEqual(1, reencode.Split(new[] { "reencoder.Export(" },
                StringSplitOptions.None).Length - 1);
            StringAssert.Contains(reencode, "request.LiveSoundRemoved");
            StringAssert.Contains(reencode, "chimePcm, chimeStartSeconds");
            // The mixed file has no launch-to-audible latency, so the live alignment delay is
            // always subtracted from the measured stamp gap.
            StringAssert.Contains(reencode, "(alignmentMs ?? ChimeAlignmentFallbackMs) / 1000.0");
        }

        [TestMethod]
        public void ChimeComposite_MixesTheFileAndGainTheHostPlayed()
        {
            // The composited chime is the exact file the sound host played, at the gain it played
            // it; the toast service asks the in-house host, never UniPlaySong.
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "OwnSoundFilePath");
            StringAssert.Contains(service, "ChimeSoundFile.TryReadPcm(soundFilePath, MaxChimePlaybackSeconds, soundFileGain, _logger)");
            StringAssert.Contains(service, "soundMatch.OwnSoundFileGain = e.SoundFileGain ?? 1.0;");

            var toastPath = FindRepoFile("source", "Services", "UI", "ToastNotificationService.cs");
            var toast = File.ReadAllText(toastPath);
            StringAssert.Contains(toast, "_unlockSounds.Play(");
            StringAssert.Contains(toast, "SoundAlignmentDelayMs");
            Assert.IsFalse(toast.IndexOf("uniplaysong", StringComparison.OrdinalIgnoreCase) >= 0,
                "Unlock sounds are in-house; nothing may route through UniPlaySong.");
            Assert.IsFalse(toast.Contains("TryTriggerExternalEvent"));
            Assert.IsFalse(
                File.Exists(Path.Combine(Path.GetDirectoryName(toastPath), "UniPlaySongBridge.cs")),
                "The UniPlaySong bridge was removed with the in-house sound host.");
        }

        [TestMethod]
        public void ChimeComposite_IsBoundedByFileDurationCappedAtMaxPlayback()
        {
            // TryReadPcm stops at end of file, so the composite is exactly the file up to the
            // toast-slot cap; no occurrence registry bounds it any more, and only a capped file is
            // faded.
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = service.IndexOf("private byte[] TryReadChimePcm(", StringComparison.Ordinal);
            var end = service.IndexOf("private double ResolveChimeLeadSeconds", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var body = end > start ? service.Substring(start, end - start) : service.Substring(start);
            StringAssert.Contains(body, "soundFilePath, MaxChimePlaybackSeconds, soundFileGain, _logger");
            StringAssert.Contains(body, "PcmAudio.FadeOutTail(pcm, ChimeFadeOutSeconds)");
            Assert.IsFalse(service.Contains("TryGetDurationSeconds"));

            var chimeFile = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "ChimeSoundFile.cs"));
            Assert.IsFalse(chimeFile.Contains("TryGetDurationSeconds"));
        }

        /// <summary>
        /// The composited card plays its own recorded animation from its first frame, so the chime
        /// has to lead that frame by the gap the two had live. Modelling it from the sound-align
        /// delay plus the slide duration measured to the SETTLED card instead, which placed every
        /// chime a slide-length early — and twice that on the fast path, whose align delay differs.
        /// </summary>
        [TestMethod]
        public void ChimeLead_IsMeasuredFromLiveStampsRatherThanModelled()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));

            StringAssert.Contains(service, "ResolveChimeLeadSeconds");
            StringAssert.Contains(service, "(track.StartUtc - ownSound.Value).TotalSeconds");
            StringAssert.Contains(service, "toastStartSeconds - chimeLeadSeconds");
            Assert.IsFalse(
                service.Contains("ChimeLeadBeforeToastSeconds"),
                "The fixed sound-to-settled-card lead double-counted the slide-in; measure instead.");
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
                "Both sparse auxiliary chunks and pump-paced chunks must use the shared frame grid.");
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
            StringAssert.Contains(capture, "AudioTimelineAnchorConsensus");
            StringAssert.Contains(capture, "_timelineFramesDelivered + gapFrames");
            StringAssert.Contains(recorder, "TryGetTimelineOrigin(");
            StringAssert.Contains(recorder, "allowPartial: timedOut");
            Assert.IsFalse(
                recorder.Contains("stamped?.FirstPacketCaptureUtc"),
                "The pump must not anchor already-buffered audio to one later packet stamp.");

            var paths = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "RecordingPaths.cs"));
            StringAssert.Contains(paths, "yyyyMMdd-HHmmssfffffff'Z'");
        }

        [TestMethod]
        public void ObsoleteHapticReferencePipeline_IsRemoved()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            Assert.IsFalse(recorder.Contains("StartHapticReference"));
            Assert.IsFalse(recorder.Contains("HapticEndpointCapture"));
            Assert.IsFalse(recorder.Contains("WriteStampedHapticPacket"));

            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            Assert.IsFalse(service.Contains("TryRemoveHapticAudio"));
            Assert.IsFalse(service.Contains("HapticReferenceChunkFilePrefix"));
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
            StringAssert.Contains(export, "request.LiveSoundRemoved = false;");
            StringAssert.Contains(export, "no composited chime");
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
        public void EndpointClassifier_IsUsedBeforeNativeControllerChannelSplitting()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            var scan = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "RenderEndpointScan.cs"));

            var classify = recorder.IndexOf("RenderEndpointScan.IsHapticEndpoint(speaker)", StringComparison.Ordinal);
            var native = recorder.IndexOf("ProcessLoopbackCapture.ForEndpointNative(speaker.Id)", classify, StringComparison.Ordinal);
            Assert.IsTrue(classify >= 0 && native > classify);
            StringAssert.Contains(scan, "HapticEndpointClassifier.IsHapticEndpoint");
        }

        /// <summary>
        /// NAudio declares its own managed coclass for the MMDeviceEnumerator CLSID, and the CLR's
        /// CLSID-to-type map is process-wide and first-writer-wins. A second Playnite extension
        /// shipping NAudio therefore makes every NAudio device-enumeration entry point throw
        /// InvalidCastException, which once cost a reporting user the audio on every clip. Endpoint
        /// discovery has to stay on AudioEndpointEnumerator, which activates from the CLSID and
        /// casts only to interfaces.
        /// </summary>
        [TestMethod]
        public void AudioCapture_NeverReachesEndpointsThroughNAudio()
        {
            foreach (var file in new[] { "AudioLoopbackRecorder", "MicrophoneSelector", "RenderEndpointScan" })
            {
                var text = File.ReadAllText(FindRepoFile("source", "Services", "Recording", file + ".cs"));
                Assert.IsFalse(
                    text.Contains("NAudio.CoreAudioApi"),
                    file + " must not use NAudio's device enumeration; see AudioEndpointEnumerator.");
                foreach (var banned in new[] { "new MMDeviceEnumerator(", "new WasapiLoopbackCapture(", "new WasapiCapture(" })
                {
                    Assert.IsFalse(
                        text.Contains(banned),
                        file + " must not construct " + banned + ": it activates NAudio's coclass for the " +
                        "MMDeviceEnumerator CLSID and throws whenever a second NAudio is loaded.");
                }
            }
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
