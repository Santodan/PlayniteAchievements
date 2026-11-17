using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class BinaryTableReaderTests
    {
        private static readonly LinkedFileSchema Binary = new LinkedFileSchema { Format = LinkedFileFormat.Binary };

        private static LinkedFileReadResult Parse(byte[] bytes)
        {
            return LinkedFileTableReaders.Parse(bytes, Binary);
        }

        private static string[] Column(LinkedFileTable table, string name)
        {
            var index = table.IndexOfColumn(name);
            Assert.IsTrue(index >= 0, "Missing column " + name + "; have " + string.Join(", ", table.Columns));
            return table.Rows.Select(row => row[index]).ToArray();
        }

        private static byte[] Build(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void Name(BinaryWriter writer, string name)
        {
            writer.Write((uint)name.Length);
            writer.Write(Encoding.ASCII.GetBytes(name));
        }

        // A header, then per record: a type tag, a length-prefixed name, a zero tag, a double.
        private static byte[] NamedRecords()
        {
            return Build(writer =>
            {
                writer.Write(402U);
                writer.Write(3U);
                foreach (var (name, value) in new[] { ("achi0", 0.0), ("achi1", 1.0), ("achi12", 2.0) })
                {
                    writer.Write(1U);
                    Name(writer, name);
                    writer.Write(0U);
                    writer.Write(value);
                }
            });
        }

        [TestMethod]
        public void NamedRecords_ReadNameAndValueColumns()
        {
            var result = Parse(NamedRecords());

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "achi0", "achi1", "achi12" }, Column(result.Table, "name"));
            CollectionAssert.AreEqual(new[] { "0", "1", "2" }, Column(result.Table, "value@4"));
        }

        [TestMethod]
        public void NamedRecords_DecodeFromHexAndBase64Text()
        {
            var bytes = NamedRecords();
            var hex = Encoding.ASCII.GetBytes(BitConverter.ToString(bytes).Replace("-", string.Empty) + "\r\n");
            var base64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes));

            CollectionAssert.AreEqual(new[] { "0", "1", "2" }, Column(Parse(hex).Table, "value@4"));
            CollectionAssert.AreEqual(new[] { "achi0", "achi1", "achi12" }, Column(Parse(base64).Table, "name"));
        }

        [TestMethod]
        public void NamedRecords_WithUnevenGaps_AreNotRecognized()
        {
            var bytes = Build(writer =>
            {
                Name(writer, "first");
                writer.Write(7U);
                Name(writer, "second");
                writer.Write(7U);
                writer.Write(9U);
                Name(writer, "third");
                writer.Write(7U);
            });

            var result = Parse(bytes);
            Assert.IsFalse(result.Success && result.Table.IndexOfColumn("name") >= 0, "Uneven gaps must not read as named records.");
        }

        [TestMethod]
        public void CountedRecords_ReadIdAndValueColumns()
        {
            var bytes = Build(writer =>
            {
                writer.Write(Encoding.ASCII.GetBytes("SAVE.DATA"));
                writer.Write((byte)0);
                writer.Write((byte)1);
                writer.Write(1U);
                Name(writer, "Achievements");
                writer.Write(4U + 3 * 8);
                writer.Write(3U);
                foreach (var (id, value) in new[] { (1U, 1U), (0x501U, 1U), (0x1601U, 0U) })
                {
                    writer.Write(id);
                    writer.Write(value);
                }
            });

            var result = Parse(bytes);

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "1", "1281", "5633" }, Column(result.Table, "id"));
            CollectionAssert.AreEqual(new[] { "1", "1", "0" }, Column(result.Table, "value@4"));
        }

        [TestMethod]
        public void FixedSlots_ReadFilledRecordsWithIdAndTime()
        {
            var bytes = Build(writer =>
            {
                writer.Write(Encoding.ASCII.GetBytes("ACH "));
                writer.Write(1U);
                writer.Write(3U);
                writer.Write(0U);
                foreach (var (id, time) in new[] { ((ushort)45, 1740900000U), ((ushort)7, 1741000000U), ((ushort)0, 0U), ((ushort)3, 1741100000U) })
                {
                    writer.Write(id);
                    writer.Write(time);
                    writer.Write(new byte[10]);
                }
            });

            var result = Parse(bytes);

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "45", "7", "3" }, Column(result.Table, "id"));
            CollectionAssert.AreEqual(new[] { "1740900000", "1741000000", "1741100000" }, Column(result.Table, "time"));
        }

        [TestMethod]
        public void FlagText_ReadsOneRowPerDigit()
        {
            var result = Parse(Encoding.ASCII.GetBytes("1002\r\n"));

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "0", "1", "2", "3" }, Column(result.Table, "index"));
            CollectionAssert.AreEqual(new[] { "1", "0", "0", "2" }, Column(result.Table, "value"));
        }

        [TestMethod]
        public void UnknownLayouts_FailWithoutGuessing()
        {
            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, Parse(Encoding.ASCII.GetBytes("plain words, not a save file at all")).Status);
            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, Parse(new byte[64]).Status);
            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, Parse(new byte[0]).Status);
        }

        [TestMethod]
        public void DecodeText_RejectsTextWithInnerWhitespace()
        {
            Assert.IsNull(BinaryTableReader.DecodeText(Encoding.ASCII.GetBytes("45 1792954109\r\n1 1792954482\r\n")));
            Assert.IsNotNull(BinaryTableReader.DecodeText(Encoding.ASCII.GetBytes("  0A0B0C0D0E0F10111213\r\n")));
        }

        [TestMethod]
        public void SniffFormat_ReadsTheContent()
        {
            LinkedFileFormat? Sniff(string text) => LinkedFileTableReaders.SniffFormat(Encoding.UTF8.GetBytes(text));

            Assert.AreEqual(LinkedFileFormat.Json, Sniff("\n\t{ \"a\": 1 }"));
            Assert.AreEqual(LinkedFileFormat.Json, Sniff("[{\"a\":1}]"));
            Assert.AreEqual(LinkedFileFormat.Json, Sniff("[ 1, 2 ]"));
            Assert.AreEqual(LinkedFileFormat.Ini, Sniff("[section]\nkey=1"));
            Assert.AreEqual(LinkedFileFormat.Xml, Sniff("<root/>"));
            Assert.AreEqual(LinkedFileFormat.Binary, Sniff("1000000000"));
            Assert.AreEqual(LinkedFileFormat.Binary, Sniff("0A0B0C0D0E0F10111213"));
            Assert.IsNull(Sniff("id,done\na,1\n"));
            Assert.IsNull(Sniff("45 1792954109\r\n1 1792954482\r\n"));
            Assert.AreEqual(LinkedFileFormat.Binary, LinkedFileTableReaders.SniffFormat(new byte[] { 0x41, 0x00, 0x01 }));
            Assert.AreEqual(LinkedFileFormat.Xlsx, LinkedFileTableReaders.SniffFormat(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 }));
        }

        [TestMethod]
        public void DetectFormat_SniffsAmbiguousExtensions()
        {
            var folder = Path.Combine(Path.GetTempPath(), "pa-linked-sniff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var json = Path.Combine(folder, "shared.dat");
                File.WriteAllText(json, "{\n\t\"achievements\": []\n}");
                var flags = Path.Combine(folder, "achievements.sav");
                File.WriteAllText(flags, "1000");
                var text = Path.Combine(folder, "unlocked.txt");
                File.WriteAllText(text, "45 1792954109\r\n");

                Assert.AreEqual(LinkedFileFormat.Json, LinkedFileTableReaders.DetectFormat(json));
                Assert.AreEqual(LinkedFileFormat.Binary, LinkedFileTableReaders.DetectFormat(flags));
                Assert.AreEqual(LinkedFileFormat.Delimited, LinkedFileTableReaders.DetectFormat(text));
                Assert.AreEqual(LinkedFileFormat.Binary, LinkedFileTableReaders.DetectFormat(Path.Combine(folder, "missing.sav")));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
