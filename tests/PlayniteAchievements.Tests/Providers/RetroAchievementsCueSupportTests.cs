using DiscUtils.Iso9660;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsCueSupportTests
    {
        [TestMethod]
        public void CueSheetParser_ParsesQuotedUnquotedIndexesPregapAndSession()
        {
            var lines = new[]
            {
                "REM SESSION 02",
                "FILE \"track 01.bin\" BINARY",
                "  TRACK 01 MODE1/2352",
                "    PREGAP 00:02:00",
                "    INDEX 00 00:00:00",
                "    INDEX 01 00:02:00",
                "FILE unquoted track.bin BINARY",
                "  TRACK 02 AUDIO",
                "    INDEX 00 10:00:00",
                "    INDEX 01 10:02:00"
            };

            var parsed = CueSheetParser.TryParse("game.cue", lines, out var sheet, out var error);

            Assert.IsTrue(parsed, error);
            Assert.AreEqual(2, sheet.Files.Count);
            Assert.AreEqual("track 01.bin", sheet.Files[0].FileName);
            Assert.AreEqual("unquoted track.bin", sheet.Files[1].FileName);
            Assert.AreEqual(2, sheet.Tracks.Count);
            Assert.AreEqual("MODE1/2352", sheet.Tracks[0].Mode);
            Assert.AreEqual(150, sheet.Tracks[0].PregapFrames);
            Assert.AreEqual(150, sheet.Tracks[0].Index01Frames);
            Assert.AreEqual("02", sheet.Tracks[0].Session);
            Assert.AreEqual(45150, sheet.Tracks[1].Index01Frames);
        }

        [TestMethod]
        public void CueTrackReader_MissingTrack_ReturnsFalse()
        {
            var dir = CreateTempDir();
            try
            {
                var cuePath = Path.Combine(dir, "missing.cue");
                WriteCue(cuePath, "missing.bin", "MODE1/2048");

                Assert.IsFalse(CueTrackReader.HasReadableDataTrack(cuePath));
                Assert.IsFalse(CueTrackReader.TryGetDataTrackDependencies(cuePath, out _, out _));
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [DataTestMethod]
        [DataRow("MODE1/2048")]
        [DataRow("MODE1/2352")]
        [DataRow("MODE2/2352")]
        public void CueTrackReader_ExposesLogical2048PayloadAndSupportsCrossSectorReads(string mode)
        {
            var dir = CreateTempDir();
            try
            {
                var sector0 = CreateLogicalSector(0x10);
                var sector1 = CreateLogicalSector(0x80);
                var binPath = Path.Combine(dir, "game.bin");
                File.WriteAllBytes(binPath, CreatePhysicalTrack(mode, sector0, sector1));

                var cuePath = Path.Combine(dir, "game.cue");
                WriteCue(cuePath, "game.bin", mode);

                Assert.IsTrue(CueTrackReader.TryOpenFirstDataTrackStream(cuePath, out var stream, out var error), error);
                using (stream)
                {
                    Assert.AreEqual(4096, stream.Length);
                    stream.Seek(2040, SeekOrigin.Begin);

                    var buffer = new byte[16];
                    var read = stream.Read(buffer, 0, buffer.Length);

                    Assert.AreEqual(16, read);
                    var expected = sector0.Skip(2040).Take(8).Concat(sector1.Take(8)).ToArray();
                    CollectionAssert.AreEqual(expected, buffer);
                }
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [TestMethod]
        public async Task SegaCdSaturnHasher_CueHashesLogicalHeader()
        {
            var dir = CreateTempDir();
            try
            {
                var sector0 = CreateLogicalSector(0);
                Encoding.ASCII.GetBytes("SEGADISCSYSTEM  ").CopyTo(sector0, 0);

                var binPath = Path.Combine(dir, "sega.bin");
                File.WriteAllBytes(binPath, CreatePhysicalTrack("MODE1/2352", sector0));

                var cuePath = Path.Combine(dir, "sega.cue");
                WriteCue(cuePath, "sega.bin", "MODE1/2352");

                var hashes = await new SegaCdSaturnCustomHasher(null)
                    .ComputeHashesAsync(cuePath, CancellationToken.None)
                    .ConfigureAwait(false);

                var expected = HashUtils.ComputeMd5Hex(sector0, 0, 512);
                Assert.AreEqual(1, hashes.Count);
                Assert.AreEqual(expected, hashes[0]);
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [TestMethod]
        public async Task PceCdHasher_CueHashesTitleAndBootProgramSectors()
        {
            var dir = CreateTempDir();
            try
            {
                var sector0 = CreateLogicalSector(0);
                var sector1 = CreateLogicalSector(0);
                var sector2 = CreateLogicalSector(0x40);

                sector1[0] = 0;
                sector1[1] = 0;
                sector1[2] = 2; // program sector, big-endian 3 bytes
                sector1[3] = 1; // sector count
                Encoding.ASCII.GetBytes("PC Engine CD-ROM SYSTEM").CopyTo(sector1, 32);
                for (var i = 0; i < 22; i++)
                {
                    sector1[106 + i] = (byte)('A' + i);
                }

                var binPath = Path.Combine(dir, "pce.bin");
                File.WriteAllBytes(binPath, CreatePhysicalTrack("MODE1/2352", sector0, sector1, sector2));

                var cuePath = Path.Combine(dir, "pce.cue");
                WriteCue(cuePath, "pce.bin", "MODE1/2352");

                var hashes = await new PceCdCustomHasher(null)
                    .ComputeHashesAsync(cuePath, CancellationToken.None)
                    .ConfigureAwait(false);

                var expected = ComputePceExpectedHash(sector1, sector2);
                Assert.AreEqual(1, hashes.Count);
                Assert.AreEqual(expected, hashes[0]);
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [TestMethod]
        public async Task PsxHasher_CueBackedIsoResolvesSystemCnfAndExecutable()
        {
            var dir = CreateTempDir();
            try
            {
                var exeBytes = new byte[2064];
                Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(exeBytes, 0);
                WriteUInt32LE(exeBytes, 28, 16);
                for (var i = 32; i < exeBytes.Length; i++)
                {
                    exeBytes[i] = (byte)(i & 0xff);
                }

                var isoPath = Path.Combine(dir, "psx.iso");
                var builder = new CDBuilder
                {
                    UseJoliet = true,
                    VolumeIdentifier = "PSXTEST"
                };
                builder.AddFile("SYSTEM.CNF", Encoding.ASCII.GetBytes("BOOT = cdrom:\\SLUS_123.45;1\r\n"));
                builder.AddFile("SLUS_123.45", exeBytes);
                builder.Build(isoPath);

                var cuePath = Path.Combine(dir, "psx.cue");
                WriteCue(cuePath, "psx.iso", "MODE1/2048");

                var hashes = await new PsxCustomHasher(null)
                    .ComputeHashesAsync(cuePath, CancellationToken.None)
                    .ConfigureAwait(false);

                var expected = ComputePsxExpectedHash("SLUS_123.45", exeBytes);
                Assert.AreEqual(1, hashes.Count);
                Assert.AreEqual(expected, hashes[0]);
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [TestMethod]
        public void HashCache_CueDependencySnapshotInvalidatesWhenReferencedTrackChanges()
        {
            var dir = CreateTempDir();
            try
            {
                var binPath = Path.Combine(dir, "game.bin");
                File.WriteAllBytes(binPath, CreatePhysicalTrack("MODE1/2048", CreateLogicalSector(0)));

                var cuePath = Path.Combine(dir, "game.cue");
                WriteCue(cuePath, "game.bin", "MODE1/2048");

                var dependencies = RetroAchievementsHashCacheStore.CaptureDependencySnapshot(cuePath);

                Assert.AreEqual(2, dependencies.Count);
                Assert.IsTrue(RetroAchievementsHashCacheStore.ValidateDependencySnapshot(dependencies));

                File.SetLastWriteTimeUtc(binPath, File.GetLastWriteTimeUtc(binPath).AddMinutes(5));

                Assert.IsFalse(RetroAchievementsHashCacheStore.ValidateDependencySnapshot(dependencies));
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsCueTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static void WriteCue(string cuePath, string fileName, string mode)
        {
            File.WriteAllText(
                cuePath,
                $"FILE \"{fileName}\" BINARY\r\n  TRACK 01 {mode}\r\n    INDEX 01 00:00:00\r\n",
                Encoding.ASCII);
        }

        private static byte[] CreateLogicalSector(byte seed)
        {
            var sector = new byte[2048];
            for (var i = 0; i < sector.Length; i++)
            {
                sector[i] = (byte)(seed + i);
            }
            return sector;
        }

        private static byte[] CreatePhysicalTrack(string mode, params byte[][] logicalSectors)
        {
            GetPhysicalLayout(mode, out var physicalSectorSize, out var dataOffset);

            var data = new byte[physicalSectorSize * logicalSectors.Length];
            for (var i = 0; i < logicalSectors.Length; i++)
            {
                Buffer.BlockCopy(logicalSectors[i], 0, data, (i * physicalSectorSize) + dataOffset, logicalSectors[i].Length);
            }

            return data;
        }

        private static void GetPhysicalLayout(string mode, out int physicalSectorSize, out int dataOffset)
        {
            switch (mode.ToUpperInvariant())
            {
                case "MODE1/2048":
                case "MODE2/2048":
                    physicalSectorSize = 2048;
                    dataOffset = 0;
                    return;
                case "MODE1/2352":
                    physicalSectorSize = 2352;
                    dataOffset = 16;
                    return;
                case "MODE2/2352":
                    physicalSectorSize = 2352;
                    dataOffset = 24;
                    return;
                default:
                    throw new ArgumentException("Unsupported test mode.", nameof(mode));
            }
        }

        private static string ComputePceExpectedHash(byte[] sector1, byte[] sector2)
        {
            using (var md5 = MD5.Create())
            {
                md5.TransformBlock(sector1, 106, 22, null, 0);
                md5.TransformFinalBlock(sector2, 0, sector2.Length);
                return HashUtils.ToHexLower(md5.Hash);
            }
        }

        private static string ComputePsxExpectedHash(string exeName, byte[] exeBytes)
        {
            using (var md5 = MD5.Create())
            {
                var exeNameBytes = Encoding.ASCII.GetBytes(exeName);
                md5.TransformBlock(exeNameBytes, 0, exeNameBytes.Length, null, 0);
                md5.TransformFinalBlock(exeBytes, 0, exeBytes.Length);
                return HashUtils.ToHexLower(md5.Hash);
            }
        }

        private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xff);
            buffer[offset + 1] = (byte)((value >> 8) & 0xff);
            buffer[offset + 2] = (byte)((value >> 16) & 0xff);
            buffer[offset + 3] = (byte)((value >> 24) & 0xff);
        }
    }
}
