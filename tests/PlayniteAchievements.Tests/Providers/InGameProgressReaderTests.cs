using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Steam.Local;
using PlayniteAchievements.Providers.Xenia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class InGameProgressReaderTests
    {
        [TestMethod]
        public void SteamIdHelper_ConvertsSteamId64ToAccountId3()
        {
            Assert.IsTrue(
                SteamIdHelper.TryGetAccountId3("76561198012345678", out var accountId));
            Assert.AreEqual(52079950U, accountId);
            Assert.IsFalse(SteamIdHelper.TryGetAccountId3("123", out _));
        }

        [TestMethod]
        public void SteamLocalStatsReader_MapsOnlyUnlockedBitsAndTimestamp()
        {
            var directory = CreateTempDirectory();
            try
            {
                var schemaPath = Path.Combine(directory, "schema.bin");
                var statsPath = Path.Combine(directory, "stats.bin");
                WriteSteamSchema(schemaPath);
                WriteSteamStats(statsPath, 1, 1700000000);

                var result = new SteamLocalStatsReader().TryRead(statsPath, schemaPath);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(1, result.UnlockByApiName.Count);
                Assert.IsTrue(result.UnlockByApiName.ContainsKey("ACH_ONE"));
                Assert.AreEqual(
                    DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime,
                    result.UnlockByApiName["ACH_ONE"]);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void SteamLocalStatsReader_ReadsFileHeldOpenForConcurrentWrite()
        {
            var directory = CreateTempDirectory();
            try
            {
                var schemaPath = Path.Combine(directory, "schema.bin");
                var statsPath = Path.Combine(directory, "stats.bin");
                WriteSteamSchema(schemaPath);
                WriteSteamStats(statsPath, 1, 1700000000);

                using (new FileStream(
                    statsPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    Assert.IsTrue(
                        new SteamLocalStatsReader().TryRead(statsPath, schemaPath).Success);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void SteamLocalStatsReader_RejectsTruncatedFile()
        {
            var directory = CreateTempDirectory();
            try
            {
                var schemaPath = Path.Combine(directory, "schema.bin");
                var statsPath = Path.Combine(directory, "stats.bin");
                WriteSteamSchema(schemaPath);
                WriteSteamStats(statsPath, 1, 1700000000);
                using (var stream = new FileStream(statsPath, FileMode.Open, FileAccess.Write))
                {
                    stream.SetLength(Math.Max(1, stream.Length - 8));
                }

                Assert.IsFalse(
                    new SteamLocalStatsReader().TryRead(statsPath, schemaPath).Success);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void ShadPs4ProgressReader_StreamsNewAndLegacyTimestamps()
        {
            var directory = CreateTempDirectory();
            try
            {
                var path = Path.Combine(directory, "TROP.XML");
                var ps4Microseconds = 500000000000000UL;
                File.WriteAllText(
                    path,
                    "<trophyconf>" +
                    "<trophy id=\"001\" unlockstate=\"true\" timestamp=\"1700000000\" />" +
                    $"<trophy id=\"002\" unlockstate=\"1\" timestamp=\"{ps4Microseconds}\" />" +
                    "<trophy id=\"003\" unlockstate=\"false\" timestamp=\"0\" />" +
                    "</trophyconf>");

                Assert.IsTrue(ShadPS4ProgressReader.TryRead(path, out var unlocked));
                Assert.AreEqual(2, unlocked.Count);
                Assert.AreEqual(
                    DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime,
                    unlocked["001"]);
                Assert.AreEqual(
                    new DateTime(2008, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddMilliseconds((long)(ps4Microseconds / 1000UL)),
                    unlocked["002"]);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void ShadPs4ProgressReader_RejectsTruncatedXml()
        {
            var directory = CreateTempDirectory();
            try
            {
                var path = Path.Combine(directory, "TROP.XML");
                File.WriteAllText(
                    path,
                    "<trophyconf><trophy id=\"001\" unlockstate=\"true\"");

                Assert.IsFalse(ShadPS4ProgressReader.TryRead(path, out var unlocked));
                Assert.AreEqual(0, unlocked.Count);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void XeniaProgressReader_ReadsOnlyAchievementRecord()
        {
            var directory = CreateTempDirectory();
            try
            {
                var path = Path.Combine(directory, "game.gpd");
                var unlockTime = (ulong)new DateTime(
                    2025,
                    1,
                    2,
                    3,
                    4,
                    5,
                    DateTimeKind.Utc).ToFileTimeUtc();
                WriteXeniaGpd(path, 42U, 0x20000U, unlockTime);

                Assert.IsTrue(
                    GPDResolver.TryLoadAchievementProgress(path, out var achievements));
                Assert.AreEqual(1, achievements.Count);
                Assert.AreEqual(42U, achievements[0].Id);
                Assert.IsTrue(achievements[0].Unlocked);
                Assert.AreEqual(unlockTime, achievements[0].UnlockTime);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void XeniaProgressReader_RejectsTruncatedRecord()
        {
            var directory = CreateTempDirectory();
            try
            {
                var path = Path.Combine(directory, "game.gpd");
                WriteXeniaGpd(path, 42U, 0x20000U, 1UL);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                {
                    stream.SetLength(stream.Length - 1);
                }

                Assert.IsFalse(
                    GPDResolver.TryLoadAchievementProgress(path, out var achievements));
                Assert.AreEqual(0, achievements.Count);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void Rpcs3ProgressReader_RequiresCompleteCachedIdSet()
        {
            var directory = CreateTempDirectory();
            try
            {
                var path = Path.Combine(directory, "TROPUSR.DAT");
                WriteRpcs3TropUsr(path, new[] { (0, true), (1, false) });

                Assert.IsTrue(
                    Rpcs3TrophyParser.TryParseTrophyProgress(
                        path,
                        new[] { 0, 1 },
                        out var unlocked));
                CollectionAssert.AreEqual(new[] { 0 }, unlocked.Keys.ToArray());

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                {
                    stream.SetLength(stream.Length - 3);
                }

                Assert.IsFalse(
                    Rpcs3TrophyParser.TryParseTrophyProgress(
                        path,
                        new[] { 0, 1 },
                        out unlocked));
                Assert.AreEqual(0, unlocked.Count);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                nameof(InGameProgressReaderTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteSteamSchema(string path)
        {
            using (var writer = new BinaryWriter(File.Create(path), Encoding.UTF8))
            {
                WriteObject(writer, "schema", () =>
                    WriteObject(writer, "stats", () =>
                        WriteObject(writer, "0", () =>
                            WriteObject(writer, "bits", () =>
                            {
                                WriteObject(writer, "0", () =>
                                    WriteString(writer, "name", "ACH_ONE"));
                                WriteObject(writer, "1", () =>
                                    WriteString(writer, "name", "ACH_TWO"));
                            }))));
                writer.Write((byte)8);
            }
        }

        private static void WriteSteamStats(string path, int bits, int timestamp)
        {
            using (var writer = new BinaryWriter(File.Create(path), Encoding.UTF8))
            {
                WriteObject(writer, "stats", () =>
                    WriteObject(writer, "cache", () =>
                        WriteObject(writer, "0", () =>
                        {
                            WriteInt32(writer, "data", bits);
                            WriteObject(writer, "AchievementTimes", () =>
                                WriteInt32(writer, "0", timestamp));
                        })));
                writer.Write((byte)8);
            }
        }

        private static void WriteObject(
            BinaryWriter writer,
            string name,
            Action writeChildren)
        {
            writer.Write((byte)0);
            WriteCString(writer, name);
            writeChildren();
            writer.Write((byte)8);
        }

        private static void WriteString(BinaryWriter writer, string name, string value)
        {
            writer.Write((byte)1);
            WriteCString(writer, name);
            WriteCString(writer, value);
        }

        private static void WriteInt32(BinaryWriter writer, string name, int value)
        {
            writer.Write((byte)2);
            WriteCString(writer, name);
            writer.Write(value);
        }

        private static void WriteCString(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.UTF8.GetBytes(value));
            writer.Write((byte)0);
        }

        private static void WriteXeniaGpd(
            string path,
            uint achievementId,
            uint flags,
            ulong unlockTime)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                WriteBigEndian(writer, 0x58444246U);
                WriteBigEndian(writer, 1U);
                WriteBigEndian(writer, 1U);
                WriteBigEndian(writer, 1U);
                WriteBigEndian(writer, 0U);
                WriteBigEndian(writer, 0U);

                WriteBigEndian(writer, (ushort)1);
                WriteBigEndian(writer, 1UL);
                WriteBigEndian(writer, 0U);
                WriteBigEndian(writer, 28U);

                WriteBigEndian(writer, 1U);
                WriteBigEndian(writer, achievementId);
                WriteBigEndian(writer, 0U);
                WriteBigEndian(writer, 10U);
                WriteBigEndian(writer, flags);
                WriteBigEndian(writer, unlockTime);
            }
        }

        private static void WriteRpcs3TropUsr(
            string path,
            IEnumerable<(int Id, bool Unlocked)> trophies)
        {
            var magic = HexToBytes("0000000400000050000000");
            using (var stream = File.Create(path))
            {
                foreach (var trophy in trophies)
                {
                    stream.Write(magic, 0, magic.Length);
                    var entry = new byte[29];
                    entry[0] = (byte)trophy.Id;
                    if (trophy.Unlocked)
                    {
                        entry[12] = 1;
                    }
                    stream.Write(entry, 0, entry.Length);
                }
            }
        }

        private static byte[] HexToBytes(string value)
        {
            return Enumerable.Range(0, value.Length / 2)
                .Select(index => Convert.ToByte(value.Substring(index * 2, 2), 16))
                .ToArray();
        }

        private static void WriteBigEndian(BinaryWriter writer, ushort value)
        {
            writer.Write(new[] { (byte)(value >> 8), (byte)value });
        }

        private static void WriteBigEndian(BinaryWriter writer, uint value)
        {
            writer.Write(new[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            });
        }

        private static void WriteBigEndian(BinaryWriter writer, ulong value)
        {
            WriteBigEndian(writer, (uint)(value >> 32));
            WriteBigEndian(writer, (uint)value);
        }
    }
}
