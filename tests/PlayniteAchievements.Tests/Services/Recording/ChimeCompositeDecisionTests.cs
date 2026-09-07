using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class ChimeCompositeDecisionTests
    {
        [TestMethod]
        public void NoRecordedAudio_AllowsTheCompositeWhateverTheTrackSays()
        {
            // With no audio track there is no live sound to double, so the composited chime is
            // the clip's only one.
            foreach (var track in new[] { ClipTrackKind.EndpointMix, ClipTrackKind.ExcludeSoundHost, ClipTrackKind.IncludeGame })
            {
                var verdict = ChimeCompositeDecision.Decide(
                    audioRecorded: false, clipTrack: track, usedFallbackTrack: false,
                    excludedHostProcessId: null, currentHostProcessId: null);
                Assert.AreEqual(ChimeCompositeVerdict.NoRecordedAudio, verdict, track.ToString());
                Assert.IsTrue(ChimeCompositeDecision.AllowsComposite(verdict));
            }
        }

        [TestMethod]
        public void EndpointMix_KeepsTheLiveSoundAndAddsNoCopy()
        {
            var verdict = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.EndpointMix, usedFallbackTrack: false,
                excludedHostProcessId: null, currentHostProcessId: 4242);
            Assert.AreEqual(ChimeCompositeVerdict.HostNotExcluded, verdict);
            Assert.IsFalse(ChimeCompositeDecision.AllowsComposite(verdict));
        }

        [TestMethod]
        public void ExcludeSoundHost_AllowsTheCompositeWhileTheHostPidIsUnchanged()
        {
            var verdict = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.ExcludeSoundHost, usedFallbackTrack: false,
                excludedHostProcessId: 4242, currentHostProcessId: 4242);
            Assert.AreEqual(ChimeCompositeVerdict.HostExcluded, verdict);
            Assert.IsTrue(ChimeCompositeDecision.AllowsComposite(verdict));
        }

        [TestMethod]
        public void ExcludeSoundHost_RefusesTheCompositeOnceTheHostRestartedOrDied()
        {
            // A sound played by a host with a different pid was not excluded by the running
            // capture, so the clip may already hold it live.
            var restarted = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.ExcludeSoundHost, usedFallbackTrack: false,
                excludedHostProcessId: 4242, currentHostProcessId: 4243);
            var down = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.ExcludeSoundHost, usedFallbackTrack: false,
                excludedHostProcessId: 4242, currentHostProcessId: null);
            Assert.AreEqual(ChimeCompositeVerdict.HostChanged, restarted);
            Assert.AreEqual(ChimeCompositeVerdict.HostChanged, down);
            Assert.IsFalse(ChimeCompositeDecision.AllowsComposite(restarted));
            Assert.IsFalse(ChimeCompositeDecision.AllowsComposite(down));
        }

        [TestMethod]
        public void IncludeGame_AllowsTheCompositeRegardlessOfTheHostPid()
        {
            // The host is never inside the game's process tree, whatever its pid did since.
            var verdict = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.IncludeGame, usedFallbackTrack: false,
                excludedHostProcessId: 4242, currentHostProcessId: null);
            Assert.AreEqual(ChimeCompositeVerdict.HostExcluded, verdict);
            Assert.IsTrue(ChimeCompositeDecision.AllowsComposite(verdict));
        }

        [TestMethod]
        public void IncludeGame_WithTheFallbackTrack_NeedsTheHostPidToBeUnchanged()
        {
            // The fallback is the exclude-host capture, so it inherits that capture's condition.
            var stable = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.IncludeGame, usedFallbackTrack: true,
                excludedHostProcessId: 4242, currentHostProcessId: 4242);
            var changed = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.IncludeGame, usedFallbackTrack: true,
                excludedHostProcessId: 4242, currentHostProcessId: 99);
            Assert.AreEqual(ChimeCompositeVerdict.HostExcluded, stable);
            Assert.AreEqual(ChimeCompositeVerdict.HostChanged, changed);
        }

        [TestMethod]
        public void ExclusionGapInsideTheWindow_RefusesTheCompositeEvenWithAStablePid()
        {
            // The host restarted and the capture was re-bound: the pid matches again at export,
            // but sounds played between the restart and the re-bind were never excluded.
            var verdict = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.ExcludeSoundHost, usedFallbackTrack: false,
                excludedHostProcessId: 4243, currentHostProcessId: 4243, exclusionCoveredWindow: false);
            Assert.AreEqual(ChimeCompositeVerdict.HostChanged, verdict);

            // The game-tree track never held the host, gap or not.
            var gameTree = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.IncludeGame, usedFallbackTrack: false,
                excludedHostProcessId: 4243, currentHostProcessId: 4243, exclusionCoveredWindow: false);
            Assert.AreEqual(ChimeCompositeVerdict.HostExcluded, gameTree);
        }

        [TestMethod]
        public void ExcludeSoundHost_WithoutARecordedPid_NeverAllowsTheComposite()
        {
            var verdict = ChimeCompositeDecision.Decide(
                audioRecorded: true, clipTrack: ClipTrackKind.ExcludeSoundHost, usedFallbackTrack: false,
                excludedHostProcessId: null, currentHostProcessId: 4242);
            Assert.AreEqual(ChimeCompositeVerdict.HostChanged, verdict);
            Assert.IsFalse(ChimeCompositeDecision.AllowsComposite(verdict));
        }
    }
}
