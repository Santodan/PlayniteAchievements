using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.RPCS3.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class Rpcs3TrophyParserTropusrTests
    {
        [TestMethod]
        public void TryParseTrophyUnlockData_UsesRecordedTrophyIdAndOfficialTimestamp()
        {
            var unlockTime = new DateTime(638400000000000000L, DateTimeKind.Utc);
            var path = WriteTempTropusr(BuildTropusr(
                (5u, 1u, (ulong)(unlockTime.Ticks / 10)),
                (0u, 0u, 0ul)));
            var trophies = new List<Rpcs3Trophy>
            {
                new Rpcs3Trophy { Id = 0 },
                new Rpcs3Trophy { Id = 5 }
            };

            try
            {
                Assert.IsTrue(Rpcs3TrophyParser.TryParseTrophyUnlockData(path, trophies, null));
                Assert.IsFalse(trophies[0].Unlocked);
                Assert.IsTrue(trophies[1].Unlocked);
                Assert.AreEqual(unlockTime, trophies[1].UnlockTimeUtc);
            }
            finally
            {
                DeleteTempFile(path);
            }
        }

        [TestMethod]
        public void TryParseTrophyUnlockData_ValidZeroProgress_IsAuthoritative()
        {
            var path = WriteTempTropusr(BuildTropusr((0u, 0u, 0ul)));
            var trophies = new List<Rpcs3Trophy> { new Rpcs3Trophy { Id = 0, Unlocked = true } };

            try
            {
                Assert.IsTrue(Rpcs3TrophyParser.TryParseTrophyUnlockData(path, trophies, null));
                Assert.IsFalse(trophies[0].Unlocked);
            }
            finally
            {
                DeleteTempFile(path);
            }
        }

        [TestMethod]
        public void TryParseTrophyUnlockData_LogsMatchedStateDiagnostics()
        {
            var path = WriteTempTropusr(BuildTropusr(
                (0u, 1u, 0ul),
                (99u, 0u, 0ul)));
            var trophies = new List<Rpcs3Trophy> { new Rpcs3Trophy { Id = 0 } };
            var logger = new CapturingLogger();

            try
            {
                Assert.IsTrue(Rpcs3TrophyParser.TryParseTrophyUnlockData(path, trophies, logger));
                StringAssert.Contains(logger.WarningMessages.Single(), "not present in its trophy definitions: [99]");
            }
            finally
            {
                DeleteTempFile(path);
            }
        }

        [TestMethod]
        public void TryParseTrophyUnlockData_InvalidOrDuplicateFile_LeavesExistingProgressUntouched()
        {
            var path = WriteTempTropusr(BuildTropusr(
                (0u, 1u, 0ul),
                (0u, 0u, 0ul)));
            var trophies = new List<Rpcs3Trophy> { new Rpcs3Trophy { Id = 0, Unlocked = true } };

            try
            {
                Assert.IsFalse(Rpcs3TrophyParser.TryParseTrophyUnlockData(path, trophies, null));
                Assert.IsTrue(trophies[0].Unlocked);

                File.WriteAllBytes(path, new byte[] { 0x81, 0x8F, 0x54 });
                Assert.IsFalse(Rpcs3TrophyParser.TryParseTrophyUnlockData(path, trophies, null));
                Assert.IsTrue(trophies[0].Unlocked);
            }
            finally
            {
                DeleteTempFile(path);
            }
        }

        private static byte[] BuildTropusr(params (uint TrophyId, uint State, ulong Timestamp2)[] records)
        {
            const int headerSize = 0x30;
            const int tableHeaderSize = 0x20;
            const int entrySize = 0x70;
            var entriesOffset = headerSize + tableHeaderSize;
            var bytes = new byte[entriesOffset + (records.Length * entrySize)];

            WriteUInt32BE(bytes, 0, 0x818F54AD);
            WriteUInt32BE(bytes, 8, 1); // table count
            WriteUInt32BE(bytes, headerSize, 6);
            WriteUInt32BE(bytes, headerSize + 4, 0x60);
            WriteUInt32BE(bytes, headerSize + 8, 1);
            WriteUInt32BE(bytes, headerSize + 12, (uint)records.Length);
            WriteUInt64BE(bytes, headerSize + 16, (ulong)entriesOffset);

            for (var index = 0; index < records.Length; index++)
            {
                var offset = entriesOffset + (index * entrySize);
                WriteUInt32BE(bytes, offset, 6);
                WriteUInt32BE(bytes, offset + 4, 0x60);
                WriteUInt32BE(bytes, offset + 8, (uint)index);
                WriteUInt32BE(bytes, offset + 16, records[index].TrophyId);
                WriteUInt32BE(bytes, offset + 20, records[index].State);
                WriteUInt64BE(bytes, offset + 40, records[index].Timestamp2);
            }

            return bytes;
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<string> InfoMessages { get; } = new List<string>();
            public List<string> WarningMessages { get; } = new List<string>();

            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) => InfoMessages.Add(message);
            public void Info(Exception exception, string message) => InfoMessages.Add(message);
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) => WarningMessages.Add(message);
            public void Warn(Exception exception, string message) => WarningMessages.Add(message);
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

        private static string WriteTempTropusr(byte[] bytes)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", nameof(Rpcs3TrophyParserTropusrTests));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".DAT");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void DeleteTempFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
