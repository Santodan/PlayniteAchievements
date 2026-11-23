using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Tests.Providers
{
    // Xbox 360 recomp port table (AWN x360Ports.js / x360RecompFormats.js) and XLiveLessNess (xlln.js, xllnSpa.js).
    [TestClass]
    public class NativeSourceAwnXboxTests
    {
        private string _root;

        [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "PlayAchAwnXbox-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
        [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }

        private string Write(string name, string text) { var file = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, text); return file; }
        private string WriteBytes(string name, byte[] bytes) { var file = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, bytes); return file; }

        [TestMethod]
        public void PortTableLoadsAndResolvesAUserFolderPort()
        {
            Assert.IsTrue(NativeX360PortTable.All.Count > 80);
            Write("downpour.exe", "");
            var match = NativeX360PortTable.Find(_root);
            Assert.IsNotNull(match);
            Assert.AreEqual("4B4E0823", match.TitleId);
            CollectionAssert.Contains(match.Saves, Path.Combine(_root, "user", "achievements", "4B4E0823.toml"));
            Assert.AreEqual("x360recomp", NativeSourceReader.Detect(_root));
        }

        [TestMethod]
        public void TablePortMergesEveryProfileList()
        {
            Write("reblue.exe", "");
            Write("profiles/a/achievements/4D5307DF.toml", "[unlocked.3]\nfiletime = 133500000000000000\n");
            Write("profiles/b/achievements/4D5307DF.toml", "[unlocked.65537]\nfiletime = 133500000000000000\n");
            var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "x360recomp", SchemaPath = Write("schema.json", "[{\"id\":\"3\",\"displayName\":\"Three\"},{\"id\":\"4\",\"displayName\":\"Four\"}]") }, null);
            Assert.IsTrue(result.Success, result.Message);
            Assert.IsTrue(result.Achievements.Single(a => a.Id == "3").Unlocked);
            // re:Blue's own achievements are added to the list and read from the second profile.
            Assert.IsTrue(result.Achievements.Single(a => a.Id == "65537").Unlocked);
            Assert.AreEqual(2 + 8, result.Achievements.Count);
        }

        [TestMethod]
        public void UnleashedAchDataChecksumAndRecords()
        {
            var buffer = new byte[16 + 50 * 16];
            Encoding.ASCII.GetBytes("ACH ").CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)24).CopyTo(buffer, 16);
            BitConverter.GetBytes(1792512020L).CopyTo(buffer, 18);
            uint checksum = 0;
            for (var i = 16; i < buffer.Length; i++) checksum ^= buffer[i];
            BitConverter.GetBytes(checksum).CopyTo(buffer, 8);
            var unlocked = NativeX360PortTable.ParseUnleashed(buffer, 50);
            Assert.AreEqual(1, unlocked.Count);
            Assert.IsNotNull(unlocked["24"]);
            buffer[20] ^= 1;
            Assert.ThrowsException<InvalidDataException>(() => NativeX360PortTable.ParseUnleashed(buffer, 50));
        }

        [TestMethod]
        public void LostOdysseyEarnedIniIsStrict()
        {
            var unlocked = NativeX360PortTable.ParseEarnedIni("# Achievements earned: id=unix time\n5=1792512020\n");
            Assert.IsTrue(unlocked.ContainsKey("5"));
            Assert.ThrowsException<InvalidDataException>(() => NativeX360PortTable.ParseEarnedIni("5=1792512020\n"));
            Assert.ThrowsException<InvalidDataException>(() => NativeX360PortTable.ParseEarnedIni("# Achievements earned: id=unix time\n5="));
        }

        [TestMethod]
        public void XllnReadsSpaDefinitionsAndMergesProfiles()
        {
            Write("xlive.dll", "");
            WriteBytes("game.exe", BuildSpa("4D5307E6"));
            File.WriteAllText(Path.Combine(_root, "game.exe.cfg"), "<?xml version=\"1.0\"?><cfg><titleid>4D5307E6</titleid></cfg>", Encoding.Unicode);
            Assert.AreEqual("xlln", NativeSourceReader.Detect(_root));
            WriteBytes("XLiveLessNess/profile/title/4D5307E6/E0000001/achievements.dat", Record(1, DateTime.UtcNow.AddDays(-2)));
            WriteBytes("XLiveLessNess/profile/title/4D5307E6/E0000002/achievements.dat", Record(2, DateTime.UtcNow.AddDays(-1)));
            var result = NativeSourceReader.Read(_root, new NativeSourceConfiguration { Source = "xlln" }, Path.Combine(_root, "icons"));
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(3, result.Achievements.Count);
            Assert.AreEqual("First", result.Achievements[0].Name);
            Assert.AreEqual(10, result.Achievements[0].Points);
            Assert.IsTrue(result.Achievements[0].Hidden, "no 0x8 flag means secret");
            Assert.IsTrue(result.Achievements[0].Unlocked && result.Achievements[1].Unlocked);
            Assert.IsFalse(result.Achievements[2].Unlocked);
        }

        [TestMethod]
        public void XllnRefusesATornStateFile() =>
            Assert.ThrowsException<InvalidDataException>(() => NativeXllnReader.ParseState(new byte[20]));

        private static byte[] Record(uint id, DateTime time)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(id).CopyTo(bytes, 0);
            BitConverter.GetBytes(time.ToFileTimeUtc()).CopyTo(bytes, 4);
            return bytes;
        }

        private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        // A minimal XDBF: XACH (3 achievements), XTHD (title id) and an English XSTR table.
        private static byte[] BuildSpa(string titleId)
        {
            var xach = new List<byte>(Encoding.ASCII.GetBytes("XACH")); xach.AddRange(new byte[8]); xach.AddRange(Be16(3));
            for (var i = 1; i <= 3; i++)
            {
                var record = new byte[36];
                Be16(i).CopyTo(record, 0); Be16(100 + i).CopyTo(record, 2); Be16(200 + i).CopyTo(record, 4); Be16(200 + i).CopyTo(record, 6);
                Be32(0).CopyTo(record, 8); Be16(10 * i).CopyTo(record, 12); Be32(i == 1 ? 0u : 8u).CopyTo(record, 16);
                xach.AddRange(record);
            }
            var xthd = new List<byte>(Encoding.ASCII.GetBytes("XTHD")); xthd.AddRange(new byte[8]); xthd.AddRange(Be32(Convert.ToUInt32(titleId, 16)));
            var strings = new[] { "First", "Second", "Third" };
            var xstr = new List<byte>(Encoding.ASCII.GetBytes("XSTR")); xstr.AddRange(new byte[8]); xstr.AddRange(Be16(6));
            for (var i = 0; i < 3; i++)
            {
                foreach (var pair in new[] { Tuple.Create(101 + i, strings[i]), Tuple.Create(201 + i, strings[i] + " text") })
                {
                    var text = Encoding.UTF8.GetBytes(pair.Item2);
                    xstr.AddRange(Be16(pair.Item1)); xstr.AddRange(Be16(text.Length)); xstr.AddRange(text);
                }
            }
            var sections = new[] { Tuple.Create(1, 0u, xach.ToArray()), Tuple.Create(1, 1u, xthd.ToArray()), Tuple.Create(3, 1u, xstr.ToArray()) };
            var header = new List<byte>(Encoding.ASCII.GetBytes("XDBF")); header.AddRange(Be32(0x10000));
            header.AddRange(Be32((uint)sections.Length)); header.AddRange(Be32((uint)sections.Length)); header.AddRange(Be32(0)); header.AddRange(Be32(0));
            uint offset = 0;
            foreach (var s in sections)
            {
                header.AddRange(Be16(s.Item1)); header.AddRange(Be32(0)); header.AddRange(Be32(s.Item2));
                header.AddRange(Be32(offset)); header.AddRange(Be32((uint)s.Item3.Length));
                offset += (uint)s.Item3.Length;
            }
            foreach (var s in sections) header.AddRange(s.Item3);
            return header.ToArray();
        }
    }
}
