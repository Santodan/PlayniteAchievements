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
            var start = service.IndexOf("PrepareClipAudioAsync(", StringComparison.Ordinal);
            var end = service.IndexOf("private void SetChimeCompositeAuthorization", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start);
            var cleanup = service.Substring(start, end - start);
            StringAssert.Contains(cleanup, "return (audioPlan, null);");
            StringAssert.Contains(cleanup, "recordedMixture.Clone()");
            StringAssert.Contains(cleanup, "keeping the haptic-free");
        }

        [TestMethod]
        public void GameOnly_RemovesChimesFromBothTracksBeforeDesktopIsolation()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = service.IndexOf("PrepareClipAudioAsync(", StringComparison.Ordinal);
            var end = service.IndexOf("private void SetChimeCompositeAuthorization", start, StringComparison.Ordinal);
            var cleanup = service.Substring(start, end - start);

            var endpointCleanup = cleanup.IndexOf("GetOrCreateChimeCleanupTask", StringComparison.Ordinal);
            var gameOnly = cleanup.IndexOf("if (gameOnly)", StringComparison.Ordinal);
            var purge = cleanup.IndexOf("Game-only chime-reference purge", StringComparison.Ordinal);
            var isolation = cleanup.IndexOf("[Recording] Game-only isolation:", StringComparison.Ordinal);
            Assert.IsTrue(endpointCleanup >= 0 && gameOnly > endpointCleanup);
            Assert.IsTrue(purge > gameOnly && isolation > purge);
            StringAssert.Contains(cleanup, "could not be verified free of the UPS");
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
        public void ChimeCleanup_IsTransactionalAndGatesTheOneReplacement()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "var chimeCandidate = (byte[])mixture.Clone();");
            StringAssert.Contains(service, "mixture = (byte[])recordedMixture.Clone();");
            StringAssert.Contains(service, "SetChimeCompositeAuthorization(request, allChimesVerified)");
            StringAssert.Contains(service, "request.ChimeCompositeAuthorized");
            StringAssert.Contains(service, "request.ChimeCompositeClaimed");
            StringAssert.Contains(service, "request.ChimeCompositeClaimed = true");
            Assert.IsFalse(
                service.Contains("SetChimeCompositeAuthorization(request, true)"),
                "Generic Game Only subtraction is weaker than the occurrence remover and must " +
                "never authorize a replacement after dedicated chime proof failed.");
            Assert.IsFalse(service.Contains("_liveChimeRemovalByUtc"));
        }

        [TestMethod]
        public void ChimeCleanup_UsesOccurrenceIdentityAndCachedBoundedClusters()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "_soundOccurrences.Register(");
            StringAssert.Contains(service, "e.OccurrenceId");
            StringAssert.Contains(service, "GetOverlappingClusters(");
            StringAssert.Contains(service, "session.ChimeCleanupTasks");
            StringAssert.Contains(service, "BuildChimeCleanupPatchAsync");
            StringAssert.Contains(service, "ChimeSoundFile.TryGetDurationSeconds(");
            StringAssert.Contains(service, "SetNaturalPlaybackSeconds(");
            StringAssert.Contains(service, "ChimeRemovalEngine.RemoveAll(");
            StringAssert.Contains(service, "_soundOccurrences.Find(session.SessionId, occurrenceId.Value)");
            StringAssert.Contains(service, "!sessions.Contains(_session)");
            Assert.IsFalse(service.Contains("_firedChimes"));
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
        public void GameOnly_PreservesTheTimestampedChimeSidecar()
        {
            var recorder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "AudioLoopbackRecorder.cs"));
            StringAssert.Contains(recorder, "includeProcessTree: true");
            StringAssert.Contains(recorder, "_writeGameReference = true");
            StringAssert.Contains(recorder, "PlayniteChimeCaptureMode.CancelGameReference");
            StringAssert.Contains(recorder, "RecordingPaths.GameReferenceChunkFilePrefix");
        }

        [TestMethod]
        public void ReTimedChime_RemovalMustVerifyBeforeExactlyOneComposite()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var reencodeStart = service.IndexOf(
                "private async Task<string> ReencodeWithTrackAsync", StringComparison.Ordinal);
            var reencodeEnd = service.IndexOf(
                "private static string SaveClipToUniquePath", reencodeStart, StringComparison.Ordinal);
            Assert.IsTrue(reencodeStart >= 0 && reencodeEnd > reencodeStart);
            var reencode = service.Substring(reencodeStart, reencodeEnd - reencodeStart);
            Assert.AreEqual(1, reencode.Split(new[] { "TryReadChimePcmAsync(" },
                StringSplitOptions.None).Length - 1);
            Assert.AreEqual(1, reencode.Split(new[] { "reencoder.Export(" },
                StringSplitOptions.None).Length - 1);
            StringAssert.Contains(reencode, "ChimeCompositeAuthorized");
            StringAssert.Contains(reencode, "ChimeCompositeClaimed");
            StringAssert.Contains(reencode, "request.ChimeCompositeClaimed = true");
            StringAssert.Contains(reencode, "chimePcm, chimeStartSeconds");

            var engine = File.ReadAllText(FindRepoFile(
                "source", "Services", "Capture", "ChimeRemovalEngine.cs"));
            StringAssert.Contains(engine, "var working = (byte[])endpointPcm.Clone();");
            StringAssert.Contains(engine, "captured-game-purge");
            StringAssert.Contains(engine, "uncorroboratedFileAbsences");
            StringAssert.Contains(engine, "ChimeRemovalResult.Failure(");
        }

        [TestMethod]
        public void ChimeComposite_PrefersTheResolvedFileAndRespectsUniPlaySongGates()
        {
            // The composited chime comes from the exact file UniPlaySong resolved at fire time —
            // Capture remains the fallback/residual proof for older UniPlaySong or a transformed
            // render; the resolved file is also the primary removal reference.
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "OwnSoundFilePath");
            StringAssert.Contains(service, "ChimeSoundFile.TryReadPcm");
            StringAssert.Contains(service, "_soundOccurrences");

            var toast = File.ReadAllText(FindRepoFile(
                "source", "Services", "UI", "ToastNotificationService.cs"));
            StringAssert.Contains(toast, "TryResolveAchievementSound");
            StringAssert.Contains(toast, "TryTriggerExternalEvent");
            StringAssert.Contains(toast, "playnite://uniplaysong/");

            var bridge = File.ReadAllText(FindRepoFile(
                "source", "Services", "UI", "UniPlaySongBridge.cs"));
            StringAssert.Contains(bridge, "soundDisabled = true");
            StringAssert.Contains(bridge, "\"enabled\"");
            StringAssert.Contains(bridge, "\"exists\"");
            StringAssert.Contains(bridge, "apiVersion",
                "The bridge should stay documented against UniPlaySong's version-stamped JSON.");
            // UniPlaySong plays jingles at MusicVolume / 100 (its JingleService); the mixed chime
            // must be as loud as the live one the user heard, not a full-scale decode.
            StringAssert.Contains(bridge, "MusicVolume");
            StringAssert.Contains(service, "soundFileGain ?? ChimeUnknownVolumeGain");
        }

        [TestMethod]
        public void LiveChimeRemoval_IsBoundedAndUsesOrdinaryFloorsBeforeResidualPasses()
        {
            var engine = File.ReadAllText(FindRepoFile(
                "source", "Services", "Capture", "ChimeRemovalEngine.cs"));
            StringAssert.Contains(engine, "for (var pass = 0; pass < 3; pass++)");
            StringAssert.Contains(engine, "var residualPass = pass == 2");
            StringAssert.Contains(engine, "var timeLocalPass = pass == 1");
            StringAssert.Contains(engine, "blockFrames: timeLocalPass");
            StringAssert.Contains(engine, "!requireResidualAbsenceProof || residualPass");
            StringAssert.Contains(engine, "ReferenceCancellationPolicy.ChimeBlockFrames");

            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            StringAssert.Contains(service, "var startUtc = cluster.StartUtc;");
            StringAssert.Contains(service, "var endUtc = cluster.EndUtc;");
            StringAssert.Contains(service, "TryReadAudioWindow(");
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
            StringAssert.Contains(export, "SetChimeCompositeAuthorization(request, false)");
            StringAssert.Contains(export, "without a replacement chime");
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
