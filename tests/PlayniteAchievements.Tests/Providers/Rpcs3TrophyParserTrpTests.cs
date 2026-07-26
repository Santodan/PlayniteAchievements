using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RPCS3;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class Rpcs3TrophyParserTrpTests
    {
        [TestMethod]
        public void ParseTrophyDefinitionsFromTrp_BinaryTrp_ParsesDefinitions()
        {
            var tropconf = BuildTrophyConfXml(
                "NPWR00950_00",
                (0, "Bronze Trophy", "Do the thing"),
                (1, "Silver Trophy", "Do the other thing"));
            var trpPath = WriteTempTrp(BuildBinaryTrp(1, ("TROPCONF.SFM", Utf8(tropconf))));

            try
            {
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, null, null);

                Assert.AreEqual(2, trophies.Count);
                Assert.AreEqual(0, trophies[0].Id);
                Assert.AreEqual("Bronze Trophy", trophies[0].Name);
                Assert.AreEqual("Do the thing", trophies[0].Description);
                Assert.AreEqual("B", trophies[0].TrophyType);
                Assert.IsFalse(trophies[0].Hidden);
                Assert.IsTrue(trophies.All(t => !t.Unlocked && t.UnlockTimeUtc == null));
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitionsFromTrp_BinaryTrpVersion2Header_ParsesDefinitions()
        {
            var tropconf = BuildTrophyConfXml("NPWR00950_00", (0, "Trophy", "Detail"));
            var trpPath = WriteTempTrp(BuildBinaryTrp(2, ("TROPCONF.SFM", Utf8(tropconf))));

            try
            {
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, null, null);

                Assert.AreEqual(1, trophies.Count);
                Assert.AreEqual("Trophy", trophies[0].Name);
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitionsFromTrp_BinaryTrp_PrefersLocaleSfm()
        {
            var tropconf = BuildTrophyConfXml("NPWR00950_00", (0, "Config Name", "Config Detail"));
            var tropDefault = BuildTrophyConfXml("NPWR00950_00", (0, "Default Name", "Default Detail"));
            var tropFrench = BuildTrophyConfXml("NPWR00950_00", (0, "Nom Francais", "Detail Francais"));
            var trpBytes = BuildBinaryTrp(
                1,
                ("TROPCONF.SFM", Utf8(tropconf)),
                ("TROP.SFM", Utf8(tropDefault)),
                ("TROP_02.SFM", Utf8(tropFrench)));
            var trpPath = WriteTempTrp(trpBytes);

            try
            {
                var french = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, "fr", null);
                Assert.AreEqual("Nom Francais", french[0].Name);
                Assert.AreEqual("Detail Francais", french[0].Description);

                var defaulted = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, null, null);
                Assert.AreEqual("Default Name", defaulted[0].Name);
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitionsFromTrp_UnmappedLocale_FallsBackToTropSfm()
        {
            var tropconf = BuildTrophyConfXml("NPWR00950_00", (0, "Config Name", "Config Detail"));
            var tropDefault = BuildTrophyConfXml("NPWR00950_00", (0, "Default Name", "Default Detail"));
            var trpPath = WriteTempTrp(BuildBinaryTrp(
                1,
                ("TROPCONF.SFM", Utf8(tropconf)),
                ("TROP.SFM", Utf8(tropDefault))));

            try
            {
                // "cs" (Czech) has no PS3 numeric language id.
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, "cs", null);

                Assert.AreEqual(1, trophies.Count);
                Assert.AreEqual("Default Name", trophies[0].Name);
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitionsFromTrp_TruncatedTrp_ReturnsEmptyWithoutThrowing()
        {
            var tropconf = BuildTrophyConfXml("NPWR00950_00", (0, "Trophy", "Detail"));
            var full = BuildBinaryTrp(1, ("TROPCONF.SFM", Utf8(tropconf)));
            var truncated = full.Take(0x30 + 10).ToArray();
            var trpPath = WriteTempTrp(truncated);

            try
            {
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, null, null);

                Assert.AreEqual(0, trophies.Count);
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitionsFromTrp_GarbageBytes_ReturnsEmptyWithoutThrowing()
        {
            var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
            var trpPath = WriteTempTrp(garbage);

            try
            {
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, null, null);

                Assert.AreEqual(0, trophies.Count);
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitionsFromTrp_PlaintextFakeTrp_StillParses()
        {
            // Matches the plaintext fixture format used by Rpcs3ScannerTests.CreateTrpFile.
            var xml = @"<trophyconf>
  <npcommid>NPWR11111_00</npcommid>
  <title-name>Fake Game</title-name>
  <trophy id=""0"" ttype=""B"" hidden=""no"">
    <name>Fake Trophy</name>
    <detail>Description</detail>
  </trophy>
</trophyconf>";
            var trpPath = WriteTempTrp(Utf8(xml));

            try
            {
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, null, null);

                Assert.AreEqual(1, trophies.Count);
                Assert.AreEqual("Fake Trophy", trophies[0].Name);
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ExtractNpCommId_BinaryTrp_ReturnsId()
        {
            var tropconf = BuildTrophyConfXml("NPWR00950_00", (0, "Trophy", "Detail"));
            var trpPath = WriteTempTrp(BuildBinaryTrp(1, ("TROPCONF.SFM", Utf8(tropconf))));

            try
            {
                Assert.AreEqual("NPWR00950_00", Rpcs3TrophyParser.ExtractNpCommId(trpPath, null));
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ExtractNpCommId_PlaintextTrp_ReturnsId()
        {
            var trpPath = WriteTempTrp(Utf8("<trophyconf><npcommid>NPWR22222_00</npcommid></trophyconf>"));

            try
            {
                Assert.AreEqual("NPWR22222_00", Rpcs3TrophyParser.ExtractNpCommId(trpPath, null));
            }
            finally
            {
                DeleteTempFile(trpPath);
            }
        }

        [TestMethod]
        public void ReadEntries_MalformedEntry_IsSkipped()
        {
            var tropconf = BuildTrophyConfXml("NPWR00950_00", (0, "Trophy", "Detail"));
            var bytes = BuildBinaryTrp(
                1,
                ("TROPCONF.SFM", Utf8(tropconf)),
                ("TROP.SFM", Utf8(tropconf)));

            // Corrupt the second entry's data offset to point past end of file.
            WriteUInt64BE(bytes, 0x30 + 64 + 32, (ulong)bytes.Length + 100);

            var entries = Rpcs3TrpArchiveReader.ReadEntries(bytes);

            Assert.IsNotNull(entries);
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("TROPCONF.SFM", entries[0].Name);
        }

        [TestMethod]
        public void ReadEntries_ExcessiveEntryCount_ReturnsNull()
        {
            var tropconf = BuildTrophyConfXml("NPWR00950_00", (0, "Trophy", "Detail"));
            var bytes = BuildBinaryTrp(1, ("TROPCONF.SFM", Utf8(tropconf)));

            WriteUInt32BE(bytes, 0x10, 100000);

            Assert.IsNull(Rpcs3TrpArchiveReader.ReadEntries(bytes));
        }

        private static string BuildTrophyConfXml(string npCommId, params (int Id, string Name, string Detail)[] trophies)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<trophyconf version=\"1.1\">");
            builder.AppendLine($"  <npcommid>{npCommId}</npcommid>");
            builder.AppendLine("  <title-name>Test Game</title-name>");

            foreach (var trophy in trophies)
            {
                builder.AppendLine(
                    $"  <trophy id=\"{trophy.Id}\" hidden=\"no\" ttype=\"B\" pid=\"0\">" +
                    $"<name>{trophy.Name}</name><detail>{trophy.Detail}</detail></trophy>");
            }

            builder.AppendLine("</trophyconf>");
            return builder.ToString();
        }

        private static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }

        private static byte[] BuildBinaryTrp(uint version, params (string Name, byte[] Data)[] entries)
        {
            const int entrySize = 64;
            var headerSize = version >= 2 ? 0x40 : 0x30;
            var dataStart = headerSize + (entries.Length * entrySize);
            var totalSize = dataStart + entries.Sum(entry => entry.Data.Length);
            var bytes = new byte[totalSize];

            WriteUInt32BE(bytes, 0x00, 0xDCA24D00); // magic
            WriteUInt32BE(bytes, 0x04, version);
            WriteUInt64BE(bytes, 0x08, (ulong)totalSize);
            WriteUInt32BE(bytes, 0x10, (uint)entries.Length);
            WriteUInt32BE(bytes, 0x14, entrySize); // element size
            WriteUInt32BE(bytes, 0x18, 0); // dev flag; sha1/padding stay zero

            var dataOffset = (long)dataStart;
            for (var i = 0; i < entries.Length; i++)
            {
                var entryOffset = headerSize + (i * entrySize);
                var nameBytes = Encoding.ASCII.GetBytes(entries[i].Name);
                Array.Copy(nameBytes, 0, bytes, entryOffset, nameBytes.Length);
                WriteUInt64BE(bytes, entryOffset + 32, (ulong)dataOffset);
                WriteUInt64BE(bytes, entryOffset + 40, (ulong)entries[i].Data.Length);
                Array.Copy(entries[i].Data, 0, bytes, dataOffset, entries[i].Data.Length);
                dataOffset += entries[i].Data.Length;
            }

            return bytes;
        }

        private static void WriteUInt32BE(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static void WriteUInt64BE(byte[] bytes, int offset, ulong value)
        {
            WriteUInt32BE(bytes, offset, (uint)(value >> 32));
            WriteUInt32BE(bytes, offset + 4, (uint)value);
        }

        private static string WriteTempTrp(byte[] bytes)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(Rpcs3TrophyParserTrpTests));
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".TRP");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void DeleteTempFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
