using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using static PlayniteAchievements.Tests.Providers.RetroAchievements.RcheevosFixtureData;

namespace PlayniteAchievements.Tests.Providers.RetroAchievements
{
    /// <summary>
    /// Candidate hashing as the scanner drives it: containers, archive strategies, and reuse of
    /// recorded hashes for unchanged files.
    /// </summary>
    [TestClass]
    public class RetroAchievementsCandidateHasherTests
    {
        private const int ConsoleNes = 7;

        // rcheevos test_hash_rom.c: 32 KB NES ROM with and without its iNES header.
        private const string Nes32kMd5 = "6a2305a2b6675a97ff792709be1ca857";

        private string _dir;
        private RetroAchievementsCandidateHasher _hasher;
        private RetroAchievementsSettings _settings;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PA_CandidateHasherTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _hasher = new RetroAchievementsCandidateHasher(logger: null);
            _settings = new RetroAchievementsSettings { EnableArchiveScanning = true };
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [TestMethod]
        public void ZipEntry_HashesFromEntryStream_WithoutTempFiles()
        {
            var zip = Path.Combine(_dir, "game.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddEntry(archive, "readme.txt", new byte[] { 1, 2, 3 });
                AddEntry(archive, "Game (USA).nes", GenerateNesFile(32, withHeader: true));
            }

            var before = CountTempExtracts();
            var result = Hash(zip, ConsoleNes, h => h == Nes32kMd5);

            Assert.AreEqual(Nes32kMd5, result.MatchedHash);
            Assert.AreEqual(before, CountTempExtracts(), "cartridge entries must not be extracted to %TEMP%");
        }

        [TestMethod]
        public void SolidSevenZip_HashesEntriesInOnePass()
        {
            var sevenZip = @"C:\Program Files\7-Zip\7z.exe";
            if (!File.Exists(sevenZip))
            {
                Assert.Inconclusive("7-Zip is required to build a solid 7z fixture.");
            }

            var src = Path.Combine(_dir, "src");
            Directory.CreateDirectory(src);
            File.WriteAllBytes(Path.Combine(src, "a.nes"), GenerateGenericFile(40000));
            File.WriteAllBytes(Path.Combine(src, "b.nes"), GenerateNesFile(32, withHeader: true));
            File.WriteAllBytes(Path.Combine(src, "c.nes"), GenerateGenericFile(50000));

            var archive = Path.Combine(_dir, "game.7z");
            RunSevenZip(sevenZip, $"a -t7z -ms=on \"{archive}\" \"{Path.Combine(src, "*")}\"");

            var result = Hash(archive, ConsoleNes, h => h == Nes32kMd5);
            Assert.AreEqual(Nes32kMd5, result.MatchedHash);
        }

        [TestMethod]
        public void UnchangedFile_ReusesRecordedHashes_WithoutReading()
        {
            var rom = Path.Combine(_dir, "game.nes");
            File.WriteAllBytes(rom, GenerateNesFile(32, withHeader: true));

            var first = Hash(rom, ConsoleNes, _ => false);
            Assert.IsFalse(first.FromCache);
            CollectionAssert.Contains(first.Record.Hashes, Nes32kMd5);

            var entry = new RaHashCacheEntry { Resolution = RaHashCacheResolution.None, Candidates = new List<RaHashCacheCandidate> { first.Record } };

            // An exclusive lock makes any read fail: a reused record must not touch the file.
            using (new FileStream(rom, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var second = Hash(rom, ConsoleNes, h => h == Nes32kMd5, entry);
                Assert.IsTrue(second.FromCache);
                Assert.AreEqual(Nes32kMd5, second.MatchedHash, "a recorded miss becomes a match once the index has the hash");
            }
        }

        [TestMethod]
        public void ChangedFile_IsHashedAgain()
        {
            var rom = Path.Combine(_dir, "game.nes");
            File.WriteAllBytes(rom, GenerateNesFile(32, withHeader: true));
            var first = Hash(rom, ConsoleNes, _ => false);
            var entry = new RaHashCacheEntry { Candidates = new List<RaHashCacheCandidate> { first.Record } };

            File.WriteAllBytes(rom, GenerateGenericFile(4096));

            var second = Hash(rom, ConsoleNes, _ => false, entry);
            Assert.IsFalse(second.FromCache);
            CollectionAssert.DoesNotContain(second.Record.Hashes, Nes32kMd5);
        }

        [TestMethod]
        public void RecordFromOlderHashRules_IsHashedAgain()
        {
            var rom = Path.Combine(_dir, "game.nes");
            File.WriteAllBytes(rom, GenerateNesFile(32, withHeader: true));
            var first = Hash(rom, ConsoleNes, _ => false);
            Assert.AreEqual(RetroAchievementsCandidateHasher.HashRulesVersion, first.Record.RulesVersion);

            first.Record.RulesVersion = RetroAchievementsCandidateHasher.HashRulesVersion - 1;
            var entry = new RaHashCacheEntry { Candidates = new List<RaHashCacheCandidate> { first.Record } };

            var second = Hash(rom, ConsoleNes, _ => false, entry);
            Assert.IsFalse(second.FromCache, "a hasher fix must reach unchanged files");
        }

        [TestMethod]
        public void PartialRecordWithoutMatch_IsHashedAgain()
        {
            var rom = Path.Combine(_dir, "game.nes");
            File.WriteAllBytes(rom, GenerateNesFile(32, withHeader: true));
            var first = Hash(rom, ConsoleNes, _ => false);
            first.Record.Complete = false;
            var entry = new RaHashCacheEntry { Candidates = new List<RaHashCacheCandidate> { first.Record } };

            var second = Hash(rom, ConsoleNes, _ => false, entry);
            Assert.IsFalse(second.FromCache, "a partial record may be missing the matching hash");
        }

        [TestMethod]
        public void LockedFile_RecordsNothing()
        {
            var rom = Path.Combine(_dir, "game.nes");
            File.WriteAllBytes(rom, GenerateNesFile(32, withHeader: true));

            using (new FileStream(rom, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = Hash(rom, ConsoleNes, _ => false, allowNull: true);
                Assert.IsNull(result, "a sharing violation is transient and must not be recorded as a miss");
            }
        }

        [TestMethod]
        public void Cancellation_Propagates()
        {
            var rom = Path.Combine(_dir, "game.nes");
            File.WriteAllBytes(rom, GenerateNesFile(32, withHeader: true));
            var hasher = RaHasherFactory.Create(ConsoleNes, new PlayniteAchievementsSettings(), logger: null);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                try
                {
                    _hasher.HashAsync(rom, hasher, _settings, null, _ => false, cts.Token).GetAwaiter().GetResult();
                    Assert.Fail("cancellation must propagate, not be recorded as a miss");
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private RetroAchievementsCandidateHasher.CandidateResult Hash(
            string candidate,
            int consoleId,
            Func<string, bool> isMatch,
            RaHashCacheEntry cached = null,
            bool allowNull = false)
        {
            var hasher = RaHasherFactory.Create(consoleId, new PlayniteAchievementsSettings(), logger: null);
            var result = _hasher.HashAsync(candidate, hasher, _settings, cached, isMatch, CancellationToken.None).GetAwaiter().GetResult();
            if (!allowNull)
            {
                Assert.IsNotNull(result, "candidate hasher returned no result");
            }

            return result;
        }

        private static void AddEntry(ZipArchive archive, string name, byte[] data)
        {
            using (var stream = archive.CreateEntry(name).Open())
            {
                stream.Write(data, 0, data.Length);
            }
        }

        private static int CountTempExtracts()
        {
            return Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "PlayniteAchievements_ra_*").Count();
        }

        private static void RunSevenZip(string exe, string arguments)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(psi))
            {
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode, "7-Zip failed");
            }
        }
    }
}
