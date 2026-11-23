using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Tests.Providers
{
    // Ports added from Achievement Watcher Next's recompPortTable.js / recompPortFormats.js.
    [TestClass]
    public class NativeSourceAwnPortTests
    {
        private static readonly string[] NewPorts = { "smas", "rr64", "gdiffuser", "nectar", "nectar2", "fh2azimuth", "sanctuary", "issn", "issnfl", "deadnation" };
        private string _root;

        [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "PlayAchAwnPorts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
        [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }

        private string Write(string name, string text)
        {
            var file = Path.GetFullPath(Path.Combine(_root, name));
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, text);
            return file;
        }

        private NativeSourceResult State(string source, string save, params NativeAchievement[] achievements)
        {
            var r = new NativeSourceResult { Source = source, Save = save, Root = _root, Achievements = achievements.ToList() };
            NativeSourceReader.ReadState(r);
            return r;
        }

        private static NativeAchievement A(string id, int? goal = null, string name = null) => new NativeAchievement { Id = id, Name = name ?? id, Goal = goal };

        [TestMethod]
        public void NewPortsHaveBundledDefinitionsAndCatalogEntries()
        {
            foreach (var source in NewPorts)
            {
                Assert.IsNotNull(NativeSourceCatalog.Find(source), source);
                var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = source }, null, false);
                Assert.IsTrue(result.Success, source + ": " + result.Message);
                Assert.IsTrue(result.Achievements.Count > 10, source);
            }
        }

        [TestMethod]
        public void PortPicturesBecomeUrlsFromTheirListedIds()
        {
            var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "deadnation" }, null, false);
            StringAssert.StartsWith(result.Achievements[0].Icon, "https://m.exophase.com/psn/awards/m/");
        }

        [TestMethod]
        public void DetectsPortsFromTheirExecutable()
        {
            Write("SuperMarioAllStars.exe", "");
            Assert.AreEqual("smas", NativeSourceReader.Detect(_root));
        }

        [TestMethod]
        public void IssnDetectsFirstLightOnlyWhenOnlyItsTrophiesExist()
        {
            Write("inFAMOUS Second Son.exe", "");
            Assert.AreEqual("issn", NativeSourceReader.Detect(_root));
            Write("user/trophies-CUSA00575.txt", "0 2026-01-02 03:04:05\n");
            Assert.AreEqual("issnfl", NativeSourceReader.Detect(_root));
        }

        [TestMethod]
        public void SmasReadsDatesAndMaskProgress()
        {
            var r = State("smas", Write("achievements.ini", "[notice]\nx=1\n[unlocked]\nsmb1_toad=2026-01-02 03:04:05\n[progress]\nlocked=3\n"),
                A("smb1_toad"), A("locked", 10));
            Assert.IsTrue(r.Achievements[0].Unlocked);
            Assert.AreEqual(DateTimeKind.Utc, r.Achievements[0].Time.Value.Kind);
            Assert.IsFalse(r.Achievements[1].Unlocked);
            Assert.AreEqual(3, r.Achievements[1].Progress);
        }

        [TestMethod]
        public void SmasRejectsTornLine() =>
            Assert.ThrowsException<InvalidDataException>(() => State("smas", Write("achievements.ini", "[unlocked]\nsmb1_toad=2026-01"), A("smb1_toad")));

        [TestMethod]
        public void IdListNeedsHeaderAndClosingNewline()
        {
            var r = State("rr64", Write("achievements.txt", "# unlocked\n157994\n"), A("157994"), A("1"));
            Assert.IsTrue(r.Achievements[0].Unlocked);
            Assert.IsFalse(r.Achievements[1].Unlocked);
            Assert.ThrowsException<InvalidDataException>(() => State("nectar", Write("p.txt", "# unlocked\n0"), A("0")));
        }

        [TestMethod]
        public void GDiffuserReadsUnlockPairs()
        {
            var r = State("gdiffuser", Write("saves/achievements.txt", "version=1\nplaytime_seconds=50\nunlock=gp.jack.clear 1792512020\n"), A("gp.jack.clear"));
            Assert.IsTrue(r.Achievements[0].Unlocked);
            Assert.IsNotNull(r.Achievements[0].Time);
        }

        [TestMethod]
        public void SanctuaryIgnoresALineStillBeingWritten()
        {
            var r = State("sanctuary", Write("user/trophies.txt", "1 1792512020\n2"), A("1"), A("2"));
            Assert.IsTrue(r.Achievements[0].Unlocked);
            Assert.IsFalse(r.Achievements[1].Unlocked);
        }

        [TestMethod]
        public void IssnKeepsUnlockWhenDateIsOdd()
        {
            var r = State("issn", Write("user/trophies-CUSA00004.txt", "# list\n3 2026-01-02 03:04:05\n4 garbage\n"), A("3"), A("4"));
            Assert.IsNotNull(r.Achievements[0].Time);
            Assert.IsTrue(r.Achievements[1].Unlocked);
            Assert.IsNull(r.Achievements[1].Time);
        }

        [TestMethod]
        public void DeadNationReadsSixteenByteFlagsFromPathsIni()
        {
            Write("paths.ini", "save=profile\n");
            var save = Path.Combine(_root, "profile", "trophies.dat");
            Assert.AreEqual(save, NativeSourceReader.DeadNationSave(_root));
            Directory.CreateDirectory(Path.GetDirectoryName(save));
            var bytes = new byte[16];
            bytes[4] = 1; // trophy 32
            File.WriteAllBytes(save, bytes);
            var r = State("deadnation", save, A("0"), A("32"));
            Assert.IsFalse(r.Achievements[0].Unlocked);
            Assert.IsTrue(r.Achievements[1].Unlocked);
        }

        [TestMethod]
        public void Fh2ProfileUsesUtcDatesAndReportProgress()
        {
            Write("fh2-mods/Achievements/Achievements.txt",
                "Forza Horizon 2 achievements: 1 of 2 unlocked\n[ ] Road Trip  20G  Drive far  [4 / 10 km]\n");
            var profile = Write("fh2-mods/Achievements/User_1.ini", "[unlocked]\nwelcome=2026-01-02 03:04:05 UTC\n");
            var r = State("fh2azimuth", profile, A("welcome"), A("trip", 10, "Road Trip"));
            Assert.AreEqual(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), r.Achievements[0].Time);
            Assert.IsFalse(r.Achievements[1].Unlocked);
            Assert.AreEqual(4, r.Achievements[1].Progress);
        }
    }
}
