using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Sound;

namespace PlayniteAchievements.Tests.Services.Sound
{
    [TestClass]
    public class SoundHostProtocolTests
    {
        [TestMethod]
        public void Play_RoundTripsIdGainAndAPathWithSpacesAndUnicode()
        {
            var path = @"C:\Users\Jürgen\Meine Sounds\rare (final).wav";
            var line = SoundHostProtocol.EncodePlay(42, path, 0.35, 0);

            Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
            Assert.AreEqual(SoundHostProtocol.PlayVerb, message.Verb);
            Assert.AreEqual(42, message.Id);
            Assert.AreEqual(0.35, message.Gain, 1e-9);
            Assert.AreEqual(path, message.Path);
        }

        [TestMethod]
        public void Play_GainIsInvariantRegardlessOfThreadCulture()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var line = SoundHostProtocol.EncodePlay(1, @"C:\a.wav", 0.5, 0);

                StringAssert.Contains(line, "\t0.5\t");
                Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
                Assert.AreEqual(0.5, message.Gain, 1e-9);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void Preload_RoundTripsASetOfPathsAndDropsBlanks()
        {
            var line = SoundHostProtocol.EncodePreload(new[] { @"C:\one.mp3", "", null, @"D:\two two.flac" });

            Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
            Assert.AreEqual(SoundHostProtocol.PreloadVerb, message.Verb);
            CollectionAssert.AreEqual(new[] { @"C:\one.mp3", @"D:\two two.flac" }, message.Paths);
        }

        [TestMethod]
        public void Preload_WithNoPathsIsAValidEmptySet()
        {
            Assert.IsTrue(SoundHostProtocol.TryParse(SoundHostProtocol.EncodePreload(null), out var message));
            Assert.AreEqual(0, message.Paths.Length);
        }

        [TestMethod]
        public void StopAndQuit_AreBareVerbs()
        {
            Assert.IsTrue(SoundHostProtocol.TryParse("stop", out var stop));
            Assert.AreEqual(SoundHostProtocol.StopVerb, stop.Verb);
            Assert.IsTrue(SoundHostProtocol.TryParse("quit", out var quit));
            Assert.AreEqual(SoundHostProtocol.QuitVerb, quit.Verb);
        }

        [TestMethod]
        public void ReadyStartedAndError_RoundTrip()
        {
            Assert.IsTrue(SoundHostProtocol.TryParse(SoundHostProtocol.EncodeReady(4321), out var ready));
            Assert.AreEqual(4321, ready.Id);

            Assert.IsTrue(SoundHostProtocol.TryParse(SoundHostProtocol.EncodeStarted(7, 123456789012345L), out var started));
            Assert.AreEqual(7, started.Id);
            Assert.AreEqual(123456789012345L, started.Qpc);

            Assert.IsTrue(SoundHostProtocol.TryParse(SoundHostProtocol.EncodeError(-1, "device\tlost\r\nreopening"), out var error));
            Assert.AreEqual(-1, error.Id);
            Assert.AreEqual("device lost  reopening", error.Text);
        }

        [TestMethod]
        public void TryParse_RejectsBlankUnknownAndMalformedLines()
        {
            Assert.IsFalse(SoundHostProtocol.TryParse(null, out _));
            Assert.IsFalse(SoundHostProtocol.TryParse("   ", out _));
            Assert.IsFalse(SoundHostProtocol.TryParse("dance\t1", out _));
            Assert.IsFalse(SoundHostProtocol.TryParse("play\tx\t0.5\tC:\\a.wav", out _));
            Assert.IsFalse(SoundHostProtocol.TryParse("play\t1\tloud\tC:\\a.wav", out _));
            Assert.IsFalse(SoundHostProtocol.TryParse("play\t1\t0.5", out _));
            Assert.IsFalse(SoundHostProtocol.TryParse("started\t1", out _));
            Assert.IsFalse(SoundHostProtocol.TryParse("ready\tpid", out _));
        }

        [TestMethod]
        public void Play_PathContainingAVerbWordIsStillAPath()
        {
            var path = @"C:\stop\quit\play.wav";
            Assert.IsTrue(SoundHostProtocol.TryParse(SoundHostProtocol.EncodePlay(3, path, 1.0, 0), out var message));
            Assert.AreEqual(path, message.Path);
        }

        [TestMethod]
        public void Play_RoundTripsTheDurationCapAsWholeMilliseconds()
        {
            var path = @"C:\sounds\rare (final).wav";
            var line = SoundHostProtocol.EncodePlay(7, path, 0.5, 6.25);

            StringAssert.Contains(line, "\t6250\t");
            Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
            Assert.AreEqual(6.25, message.MaxSeconds, 1e-9);
            Assert.AreEqual(path, message.Path);
        }

        [TestMethod]
        public void Play_ZeroCapMeansTheWholeFile()
        {
            var line = SoundHostProtocol.EncodePlay(1, @"C:\a.wav", 0.5, 0);

            Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
            Assert.AreEqual(0.0, message.MaxSeconds, 1e-9);
        }

        [TestMethod]
        public void Play_CapIsInvariantRegardlessOfThreadCulture()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var line = SoundHostProtocol.EncodePlay(1, @"C:\a.wav", 0.5, 6.0);

                StringAssert.Contains(line, "\t6000\t");
                Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
                Assert.AreEqual(6.0, message.MaxSeconds, 1e-9);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void Play_AcceptsTheOlderFourFieldFormAsUncapped()
        {
            // A host binary left over from a previous build must keep playing rather than reject
            // the line and go silent, so the cap field is optional on the way in.
            var path = @"C:\sounds\common.mp3";
            var line = string.Join("\t", SoundHostProtocol.PlayVerb, "9", "0.25", path);

            Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
            Assert.AreEqual(9, message.Id);
            Assert.AreEqual(0.25, message.Gain, 1e-9);
            Assert.AreEqual(0.0, message.MaxSeconds, 1e-9);
            Assert.AreEqual(path, message.Path);
        }

        [TestMethod]
        public void Play_RejectsANegativeOrUnparsableCap()
        {
            var path = @"C:\sounds\common.mp3";
            Assert.IsFalse(SoundHostProtocol.TryParse(
                string.Join("\t", SoundHostProtocol.PlayVerb, "1", "0.5", "-1", path), out _));
            Assert.IsFalse(SoundHostProtocol.TryParse(
                string.Join("\t", SoundHostProtocol.PlayVerb, "1", "0.5", "soon", path), out _));
        }
    }
}
