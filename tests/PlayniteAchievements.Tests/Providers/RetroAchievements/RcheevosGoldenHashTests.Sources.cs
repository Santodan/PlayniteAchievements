using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using static PlayniteAchievements.Tests.Providers.RetroAchievements.RcheevosFixtureData;

namespace PlayniteAchievements.Tests.Providers.RetroAchievements
{
    /// <summary>
    /// The same golden hashes, reached through the non-file inputs the scanner uses:
    /// CSO and RVZ images read in place, and forward-only archive entry streams.
    /// </summary>
    public partial class RcheevosGoldenHashTests
    {
        [TestMethod]
        public void Psp_StandardIso_FromCso()
        {
            var cso = WriteFile("game.cso", ContainerStreamTests.EncodeCso(RepackAsStandardIso(PspImage()), blockSize: 2048, version: 1));
            AssertSourceHash(ConsolePsp, RaHashSource.FromSeekableStream(cso, CsoUtils.OpenStream(cso)), "27ec2f9b7238b2ef29af31ddd254f201");
        }

        [TestMethod]
        public void Ps2_Iso_StandardIso_FromCso()
        {
            var cso = WriteFile("game.cso", ContainerStreamTests.EncodeCso(RepackAsStandardIso(GeneratePs2Bin("SLUS_200.64", 0x07D800)), blockSize: 2048, version: 1));
            AssertSourceHash(ConsolePlayStation2, RaHashSource.FromSeekableStream(cso, CsoUtils.OpenStream(cso)), "01a517e4ad72c6c2654d1b839be7579d");
        }

        [TestMethod]
        public void GameCube_FromRvz()
        {
            var iso = GenerateGameCubeIso(32);
            var rvz = WriteFile("game.rvz", ContainerStreamTests.EncodeRvz(
                iso, new List<ContainerStreamTests.JunkRegion>(), ContainerStreamTests.RvzTestCompression.Zstd, chunkSize: 0x20000));
            AssertSourceHash(ConsoleGameCube, RaHashSource.FromSeekableStream(rvz, RvzUtils.OpenStream(rvz)), "c7803b704fa43d22d8f6e55f4789cb45");
        }

        [TestMethod]
        public void GameCube_FromRvz_WithJunkPacking_MatchesIso()
        {
            var junk = new List<ContainerStreamTests.JunkRegion>();
            var iso = ContainerStreamTests.BuildGameCubeLikeImage(4 * 1024 * 1024, junk, seed: 21);
            var dol = BuildMinimalGameCubeBoot(iso);
            var isoPath = WriteFile("junk.iso", dol);
            var rvz = WriteFile("junk.rvz", ContainerStreamTests.EncodeRvz(dol, junk, ContainerStreamTests.RvzTestCompression.Zstd, chunkSize: 0x20000));

            var expected = Hash(ConsoleGameCube, isoPath);
            Assert.AreEqual(1, expected.Count, "ISO fixture must hash");
            AssertSourceHash(ConsoleGameCube, RaHashSource.FromSeekableStream(rvz, RvzUtils.OpenStream(rvz)), expected[0]);
        }

        // A zipped cue sheet is extracted with the track it references (by relative name, from a
        // subfolder) while audio tracks are skipped; the hash is the cue's golden hash.
        [TestMethod]
        public void Psx_Cd_StandardIso_FromZippedCue()
        {
            var zip = Path.Combine(_dir, "disc.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string name, byte[] data)
                {
                    using (var s = archive.CreateEntry(name).Open()) s.Write(data, 0, data.Length);
                }

                Add("Disc/game.cue", Encoding.ASCII.GetBytes("FILE \"game.bin\" BINARY\r\n  TRACK 01 MODE1/2048\r\n    INDEX 01 00:00:00\r\n"));
                Add("Disc/game.bin", RepackAsStandardIso(GeneratePsxBin("SLUS_007.45", 0x07D800)));
                Add("Disc/track02.wav", new byte[4096]);
            }

            var hasher = RaHasherFactory.Create(ConsolePlayStation, new PlayniteAchievementsSettings(), logger: null);
            var hashes = new List<string>();
            foreach (var input in ArchiveUtils.EnumerateHashInputs(zip, hasher.SupportsForwardOnlyInput))
            {
                using (input)
                {
                    StringAssert.EndsWith(input.EntryKey, "game.cue");
                    hashes.AddRange(hasher.ComputeHashesAsync(input.Source, CancellationToken.None).GetAwaiter().GetResult());
                }
            }

            CollectionAssert.Contains(hashes, "db433fb038cde4fb15c144e8c7dea6e3");
        }

        // Forward-only input (an archive entry stream) must hash exactly like the file itself.
        [DataTestMethod]
        [DataRow(1, "md", "full")]
        [DataRow(7, "nes", "nes-header")]
        [DataRow(81, "fds", "fds-header")]
        [DataRow(51, "a78", "7800")]
        [DataRow(13, "lnx", "lynx")]
        [DataRow(3, "sfc", "snes-header")]
        [DataRow(8, "pce", "pce-header")]
        [DataRow(2, "v64", "n64-v64")]
        [DataRow(2, "n64", "n64-n64")]
        [DataRow(2, "z64", "n64-z64")]
        [DataRow(71, "hex", "arduboy")]
        [DataRow(29, "dsk", "full")]
        public void ForwardOnlySource_MatchesFileHash(int consoleId, string extension, string kind)
        {
            var bytes = ForwardOnlyFixture(kind);
            var path = WriteFile("rom." + extension, bytes);

            var hasher = RaHasherFactory.Create(consoleId, new PlayniteAchievementsSettings(), logger: null);
            Assert.IsTrue(hasher.SupportsForwardOnlyInput, $"{hasher.Name} should accept forward-only input");

            var expected = Hash(consoleId, path);
            var source = RaHashSource.FromForwardOnlyStream(path, bytes.Length, () => new ForwardOnlyStream(File.OpenRead(path)));
            var actual = hasher.ComputeHashesAsync(source, CancellationToken.None).GetAwaiter().GetResult();

            CollectionAssert.AreEqual(expected.ToList(), actual.ToList());
        }

        private static byte[] ForwardOnlyFixture(string kind)
        {
            switch (kind)
            {
                case "nes-header": return GenerateNesFile(32, withHeader: true);
                case "fds-header": return GenerateFdsFile(2, withHeader: true);
                case "7800": return GenerateAtari7800File(16, withHeader: true);
                case "lynx":
                    var lynx = GenerateGenericFile(64 + 0x20000);
                    CopyAscii("LYNX\0", lynx, 0);
                    return lynx;
                case "snes-header": return GenerateGenericFile(0x2000 * 16 + 512);
                case "pce-header": return GenerateGenericFile(0x40000 + 512);
                case "n64-v64": return TestRomV64;
                case "n64-n64": return TestRomN64;
                case "n64-z64": return TestRomZ64;
                case "arduboy": return Encoding.ASCII.GetBytes(":100000000C94690D0C94910D0C94910D0C94910D20\r\n:00000001FF\r\n");
                default: return GenerateGenericFile(300000);
            }
        }

        // Places a DOL header and one text segment in a GameCube-like image so the hasher reads
        // bytes spread across several groups, including packed junk regions.
        private static byte[] BuildMinimalGameCubeBoot(byte[] image)
        {
            void PutU32(int p, uint v)
            {
                image[p] = (byte)(v >> 24); image[p + 1] = (byte)(v >> 16); image[p + 2] = (byte)(v >> 8); image[p + 3] = (byte)v;
            }

            PutU32(0x2440 + 0x14, 0x2000);  // apploader body size
            PutU32(0x2440 + 0x18, 0);       // apploader trailer size
            const int dolOffset = 0x100000;
            PutU32(0x420, dolOffset);
            for (var i = 0; i < 0xD8; i++) image[dolOffset + i] = 0;
            PutU32(dolOffset + 0x00, 0x180000); // segment 0 offset
            PutU32(dolOffset + 0x90, 0x200000); // segment 0 size, crosses many 128 KiB groups
            return image;
        }

        private static void AssertSourceHash(int consoleId, RaHashSource source, string expectedMd5)
        {
            using (source)
            {
                var hasher = RaHasherFactory.Create(consoleId, new PlayniteAchievementsSettings(), logger: null);
                var hashes = hasher.ComputeHashesAsync(source, CancellationToken.None).GetAwaiter().GetResult() ?? Array.Empty<string>();
                Assert.IsTrue(
                    hashes.Contains(expectedMd5, StringComparer.OrdinalIgnoreCase),
                    $"Expected {expectedMd5}; plugin returned [{string.Join(", ", hashes)}].");
            }
        }

        private sealed class ForwardOnlyStream : Stream
        {
            private readonly Stream _inner;

            public ForwardOnlyStream(Stream inner) => _inner = inner;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            // Returns short reads to exercise callers that must loop.
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1000));

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
