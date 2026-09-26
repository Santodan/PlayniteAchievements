using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace PlayniteAchievements.Tests.Providers.RetroAchievements
{
    /// <summary>
    /// Byte-equivalence tests for the CSO and RVZ random-access readers: every read must return
    /// exactly the bytes of the source ISO, at any offset and length, across block and group edges.
    /// Images are encoded here following each format's writer (maxcso layout for CSO,
    /// Dolphin's WIARVZFileReader::Convert and RVZPack for RVZ).
    /// </summary>
    [TestClass]
    public class ContainerStreamTests
    {
        private string _dir;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PA_ContainerStreamTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ---------------------------------------------------------------- CSO

        [TestMethod]
        public void Cso_ReadsMatchSourceIso_AtRandomOffsets()
        {
            var iso = BuildMixedImage(3 * 1024 * 1024 + 1234, seed: 1);
            var cso = Path.Combine(_dir, "game.cso");
            File.WriteAllBytes(cso, EncodeCso(iso, blockSize: 2048, version: 1));

            using (var stream = CsoStream.Open(cso))
            {
                Assert.AreEqual(iso.LongLength, stream.Length);
                AssertStreamMatches(iso, stream, seed: 11);
            }
        }

        [TestMethod]
        public void Cso_V2_TreatsFullSizeBlocksAsStored()
        {
            var iso = BuildMixedImage(512 * 1024 + 77, seed: 2);
            var cso = Path.Combine(_dir, "game_v2.cso");
            File.WriteAllBytes(cso, EncodeCso(iso, blockSize: 4096, version: 2));

            using (var stream = CsoStream.Open(cso))
            {
                AssertStreamMatches(iso, stream, seed: 12);
            }
        }

        [TestMethod]
        public void Cso_DecompressToTempFile_MatchesSourceIso()
        {
            var iso = BuildMixedImage(1024 * 1024 + 5, seed: 3);
            var cso = Path.Combine(_dir, "game.cso");
            File.WriteAllBytes(cso, EncodeCso(iso, blockSize: 2048, version: 1));

            using (var tmp = CsoUtils.DecompressToTempFile(cso))
            {
                CollectionAssert.AreEqual(iso, File.ReadAllBytes(tmp.Path));
            }
        }

        // ---------------------------------------------------------------- RVZ

        [TestMethod]
        public void Rvz_Zstd_WithJunkPacking_ReadsMatchSourceIso()
        {
            var junk = new List<JunkRegion>();
            var iso = BuildGameCubeLikeImage(3 * 1024 * 1024 + 4096, junk, seed: 4);
            var rvz = Path.Combine(_dir, "game.rvz");
            File.WriteAllBytes(rvz, EncodeRvz(iso, junk, RvzTestCompression.Zstd, chunkSize: 0x20000));

            using (var stream = RvzStream.Open(rvz))
            {
                Assert.AreEqual(iso.LongLength, stream.Length);
                AssertStreamMatches(iso, stream, seed: 13);
            }
        }

        [TestMethod]
        public void Rvz_Uncompressed_WithJunkPacking_ReadsMatchSourceIso()
        {
            var junk = new List<JunkRegion>();
            var iso = BuildGameCubeLikeImage(1024 * 1024 + 300, junk, seed: 5);
            var rvz = Path.Combine(_dir, "game_none.rvz");
            File.WriteAllBytes(rvz, EncodeRvz(iso, junk, RvzTestCompression.None, chunkSize: 0x8000));

            using (var stream = RvzStream.Open(rvz))
            {
                AssertStreamMatches(iso, stream, seed: 14);
            }
        }

        [TestMethod]
        public void Rvz_DecompressToTempFile_MatchesSourceIso()
        {
            var junk = new List<JunkRegion>();
            var iso = BuildGameCubeLikeImage(768 * 1024, junk, seed: 6);
            var rvz = Path.Combine(_dir, "game.rvz");
            File.WriteAllBytes(rvz, EncodeRvz(iso, junk, RvzTestCompression.Zstd, chunkSize: 0x20000));

            using (var tmp = RvzUtils.DecompressToTempFile(rvz))
            {
                CollectionAssert.AreEqual(iso, File.ReadAllBytes(tmp.Path));
            }
        }

        [TestMethod]
        public void Rvz_CorruptHeaderHash_IsRejected()
        {
            var junk = new List<JunkRegion>();
            var iso = BuildGameCubeLikeImage(256 * 1024, junk, seed: 7);
            var bytes = EncodeRvz(iso, junk, RvzTestCompression.Zstd, chunkSize: 0x20000);
            bytes[40] ^= 0xFF; // inside wia_file_size, covered by header 1 hash
            var rvz = Path.Combine(_dir, "corrupt.rvz");
            File.WriteAllBytes(rvz, bytes);

            Assert.ThrowsException<InvalidDataException>(() => RvzStream.Open(rvz).Dispose());
        }

        [TestMethod]
        public void LaggedFibonacci_SeedIsRecoverableFromOutput()
        {
            // Dolphin's writer finds junk by reconstructing a seed from the bytes (GetSeed), which
            // runs the recurrence backwards and re-checks it against Initialize. A port whose
            // Initialize or output differs from Dolphin's cannot round-trip its own output.
            var rng = new Random(8);
            for (var trial = 0; trial < 5; trial++)
            {
                var seed = new byte[RvzLaggedFibonacciGenerator.SeedBytes];
                rng.NextBytes(seed);

                var lfg = new RvzLaggedFibonacciGenerator();
                lfg.SetSeed(seed, 0);
                var output = new byte[0x8000];
                lfg.GetBytes(output.Length, output, 0);

                var recovered = LfgSeedRecovery.GetSeed(output, 0);
                Assert.IsNotNull(recovered, "seed could not be recovered from generator output");

                var regenerated = new RvzLaggedFibonacciGenerator();
                regenerated.SetSeed(recovered, 0);
                var again = new byte[output.Length];
                regenerated.GetBytes(again.Length, again, 0);
                CollectionAssert.AreEqual(output, again);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static void AssertStreamMatches(byte[] expected, Stream stream, int seed)
        {
            // Whole image, read in odd-sized pieces.
            stream.Position = 0;
            var all = new byte[expected.Length];
            var total = 0;
            while (total < all.Length)
            {
                var n = stream.Read(all, total, Math.Min(7777, all.Length - total));
                Assert.IsTrue(n > 0, $"stream ended at {total} of {all.Length}");
                total += n;
            }
            Assert.AreEqual(0, stream.Read(new byte[16], 0, 16), "read past end must return 0");
            CollectionAssert.AreEqual(expected, all);

            // Random seeks, including ranges that span block/group boundaries.
            var rng = new Random(seed);
            for (var i = 0; i < 300; i++)
            {
                var offset = rng.Next(0, expected.Length);
                var length = Math.Min(rng.Next(1, 70000), expected.Length - offset);
                stream.Seek(offset, SeekOrigin.Begin);
                var buf = new byte[length];
                var got = 0;
                while (got < length)
                {
                    var n = stream.Read(buf, got, length - got);
                    Assert.IsTrue(n > 0);
                    got += n;
                }

                for (var j = 0; j < length; j++)
                {
                    if (buf[j] != expected[offset + j])
                    {
                        Assert.Fail($"mismatch at 0x{offset + j:X} (read at 0x{offset:X}+{length})");
                    }
                }
            }
        }

        // Compressible text, random bytes and zero runs, so encoders exercise every block kind.
        private static byte[] BuildMixedImage(int size, int seed)
        {
            var rng = new Random(seed);
            var data = new byte[size];
            var pos = 0;
            while (pos < size)
            {
                var run = Math.Min(rng.Next(500, 20000), size - pos);
                switch (rng.Next(3))
                {
                    case 0:
                        for (var i = 0; i < run; i++) data[pos + i] = (byte)("RETROACHIEVEMENTS "[(pos + i) % 18]);
                        break;
                    case 1:
                        var chunk = new byte[run];
                        rng.NextBytes(chunk);
                        Buffer.BlockCopy(chunk, 0, data, pos, run);
                        break;
                    default:
                        break; // zeros
                }
                pos += run;
            }
            return data;
        }

        internal sealed class JunkRegion
        {
            public long Start;
            public int Length;
            public byte[] Seed;
        }

        // GameCube-shaped image: disc magic, then mixed content with junk regions that start and
        // end inside 0x8000 blocks, as Dolphin's junk detection produces, plus an all-zero stretch.
        internal static byte[] BuildGameCubeLikeImage(int size, List<JunkRegion> junk, int seed)
        {
            var data = BuildMixedImage(size, seed);
            data[0x1C] = 0xC2; data[0x1D] = 0x33; data[0x1E] = 0x9F; data[0x1F] = 0x3D;

            var rng = new Random(seed * 31);
            for (long block = 0x10000; block + 0x8000 <= size; block += 0x8000 * rng.Next(2, 5))
            {
                var start = block + rng.Next(0, 0x2000) & ~3L;
                var length = rng.Next(0x100, 0x8000 - (int)(start - block));
                var s = new byte[RvzLaggedFibonacciGenerator.SeedBytes];
                rng.NextBytes(s);

                var lfg = new RvzLaggedFibonacciGenerator();
                lfg.SetSeed(s, 0);
                lfg.Forward(start % 0x8000);
                lfg.GetBytes(length, data, (int)start);

                junk.Add(new JunkRegion { Start = start, Length = length, Seed = s });
            }

            // A whole group of zeros exercises data_size == 0 groups.
            var zeroStart = Math.Min(0x40000, size - 0x20000);
            Array.Clear(data, zeroStart, 0x20000);
            junk.RemoveAll(j => j.Start < zeroStart + 0x20000 && j.Start + j.Length > zeroStart);

            return data;
        }

        internal static byte[] EncodeCso(byte[] iso, int blockSize, byte version)
        {
            var blocks = (iso.Length + blockSize - 1) / blockSize;
            var index = new uint[blocks + 1];
            var body = new MemoryStream();
            var headerAndIndex = 24 + index.Length * 4;

            for (var i = 0; i < blocks; i++)
            {
                index[i] = (uint)(headerAndIndex + body.Length);
                var len = Math.Min(blockSize, iso.Length - i * blockSize);

                byte[] compressed;
                using (var ms = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        deflate.Write(iso, i * blockSize, len);
                    }
                    compressed = ms.ToArray();
                }

                if (version == 2)
                {
                    // v2: stored size >= block size means uncompressed.
                    if (compressed.Length < blockSize && len == blockSize)
                    {
                        body.Write(compressed, 0, compressed.Length);
                    }
                    else
                    {
                        body.Write(iso, i * blockSize, len);
                        if (len < blockSize) body.Write(new byte[blockSize - len], 0, blockSize - len);
                    }
                }
                else if (compressed.Length < len)
                {
                    body.Write(compressed, 0, compressed.Length);
                }
                else
                {
                    index[i] |= 0x80000000;
                    body.Write(iso, i * blockSize, len);
                }
            }
            index[blocks] = (uint)(headerAndIndex + body.Length);

            var output = new MemoryStream();
            var w = new BinaryWriter(output);
            w.Write(0x4F534943u);
            w.Write((uint)24);
            w.Write((ulong)iso.Length);
            w.Write((uint)blockSize);
            w.Write(version);
            w.Write((byte)0); // index shift
            w.Write((ushort)0);
            foreach (var entry in index) w.Write(entry);
            body.Position = 0;
            body.CopyTo(output);
            return output.ToArray();
        }

        internal enum RvzTestCompression : uint
        {
            None = 0,
            Zstd = 5
        }

        // Follows WIARVZFileReader<true>::Convert for a GameCube disc: one raw data entry starting
        // at 0x80, groups of chunk_size from the rounded-down offset 0, RVZ pack records for junk,
        // zero groups stored with data_size 0, and zstd-compressed tables when compressing.
        internal static byte[] EncodeRvz(byte[] iso, List<JunkRegion> junk, RvzTestCompression compression, int chunkSize)
        {
            const int header1Size = 0x48;
            const int header2Size = 0xDC;

            var groupCount = (iso.Length + chunkSize - 1) / chunkSize;
            var groupBody = new MemoryStream();
            var groupEntries = new List<(uint Offset, uint Size, uint Packed)>();

            // File layout: header 1, header 2, group data, raw data entries, group entries.
            long groupDataStart = header1Size + header2Size;
            groupDataStart = (groupDataStart + 3) & ~3L;

            for (var g = 0; g < groupCount; g++)
            {
                var start = g * chunkSize;
                var length = Math.Min(chunkSize, iso.Length - start);

                if (iso.Skip(start).Take(length).All(b => b == 0))
                {
                    groupEntries.Add((0, 0, 0));
                    continue;
                }

                var packed = PackGroup(iso, start, length, junk, out var usedPacking);
                var payload = usedPacking ? packed : Slice(iso, start, length);

                byte[] stored;
                uint sizeField;
                if (compression == RvzTestCompression.Zstd && g % 3 != 2)
                {
                    using (var zstd = new ZstdSharp.Compressor(5))
                    {
                        stored = zstd.Wrap(payload).ToArray();
                    }
                    sizeField = (uint)stored.Length | 0x80000000;
                }
                else
                {
                    stored = payload;
                    sizeField = (uint)stored.Length;
                }

                while ((groupDataStart + groupBody.Length) % 4 != 0) groupBody.WriteByte(0);
                var fileOffset = groupDataStart + groupBody.Length;
                groupBody.Write(stored, 0, stored.Length);
                groupEntries.Add(((uint)(fileOffset >> 2), sizeField, usedPacking ? (uint)packed.Length : 0));
            }

            var rawEntries = new MemoryStream();
            WriteU64(rawEntries, 0x80);
            WriteU64(rawEntries, (ulong)(iso.Length - 0x80));
            WriteU32(rawEntries, 0);
            WriteU32(rawEntries, (uint)groupCount);

            var groupTable = new MemoryStream();
            foreach (var e in groupEntries)
            {
                WriteU32(groupTable, e.Offset);
                WriteU32(groupTable, e.Size);
                WriteU32(groupTable, e.Packed);
            }

            var rawTable = CompressTable(rawEntries.ToArray(), compression);
            var groupTableBytes = CompressTable(groupTable.ToArray(), compression);

            var rawTableOffset = groupDataStart + groupBody.Length;
            var groupTableOffset = rawTableOffset + rawTable.Length;
            var fileSize = groupTableOffset + groupTableBytes.Length;

            var partitionEntries = new byte[0];

            var h2 = new byte[header2Size];
            PutU32(h2, 0, 1); // disc type: GameCube
            PutU32(h2, 4, (uint)compression);
            PutU32(h2, 8, 5);
            PutU32(h2, 12, (uint)chunkSize);
            Buffer.BlockCopy(iso, 0, h2, 16, 0x80);
            PutU32(h2, 0x90, 0);
            PutU32(h2, 0x94, 0x30);
            PutU64(h2, 0x98, (ulong)fileSize); // no partition entries
            Buffer.BlockCopy(Sha1(partitionEntries), 0, h2, 0xA0, 20);
            PutU32(h2, 0xB4, 1);
            PutU64(h2, 0xB8, (ulong)rawTableOffset);
            PutU32(h2, 0xC0, (uint)rawTable.Length);
            PutU32(h2, 0xC4, (uint)groupEntries.Count);
            PutU64(h2, 0xC8, (ulong)groupTableOffset);
            PutU32(h2, 0xD0, (uint)groupTableBytes.Length);
            h2[0xD4] = 0;

            var h1 = new byte[header1Size];
            h1[0] = (byte)'R'; h1[1] = (byte)'V'; h1[2] = (byte)'Z'; h1[3] = 1;
            PutU32(h1, 4, 0x01000000);
            PutU32(h1, 8, 0x00030000);
            PutU32(h1, 12, header2Size);
            Buffer.BlockCopy(Sha1(h2), 0, h1, 16, 20);
            PutU64(h1, 36, (ulong)iso.Length);
            PutU64(h1, 44, (ulong)fileSize);
            Buffer.BlockCopy(Sha1(Slice(h1, 0, 52)), 0, h1, 52, 20);

            var output = new MemoryStream();
            output.Write(h1, 0, h1.Length);
            output.Write(h2, 0, h2.Length);
            while (output.Length < groupDataStart) output.WriteByte(0);
            groupBody.Position = 0;
            groupBody.CopyTo(output);
            output.Write(rawTable, 0, rawTable.Length);
            output.Write(groupTableBytes, 0, groupTableBytes.Length);
            Assert.AreEqual(fileSize, output.Length);
            return output.ToArray();
        }

        // RVZPack: literal records around each junk region inside this group. Packing is skipped
        // when the group holds no junk, as Dolphin stores such chunks without RVZ packing.
        private static byte[] PackGroup(byte[] iso, int start, int length, List<JunkRegion> junk, out bool usedPacking)
        {
            var end = start + length;
            var regions = junk
                .Where(j => j.Start >= start && j.Start + j.Length <= end)
                .OrderBy(j => j.Start)
                .ToList();

            usedPacking = regions.Count > 0;
            if (!usedPacking)
            {
                return null;
            }

            var packed = new MemoryStream();
            long pos = start;
            foreach (var region in regions)
            {
                if (region.Start > pos)
                {
                    var literal = (int)(region.Start - pos);
                    WriteU32(packed, (uint)literal);
                    packed.Write(iso, (int)pos, literal);
                }

                WriteU32(packed, (uint)region.Length | 0x80000000);
                packed.Write(region.Seed, 0, region.Seed.Length);
                pos = region.Start + region.Length;
            }

            if (pos < end)
            {
                WriteU32(packed, (uint)(end - pos));
                packed.Write(iso, (int)pos, (int)(end - pos));
            }

            return packed.ToArray();
        }

        private static byte[] CompressTable(byte[] table, RvzTestCompression compression)
        {
            if (compression != RvzTestCompression.Zstd)
            {
                return table;
            }

            using (var zstd = new ZstdSharp.Compressor(5))
            {
                return zstd.Wrap(table).ToArray();
            }
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(source, offset, result, 0, length);
            return result;
        }

        private static byte[] Sha1(byte[] data)
        {
            using (var sha = SHA1.Create()) return sha.ComputeHash(data);
        }

        private static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        private static void WriteU64(Stream s, ulong v)
        {
            WriteU32(s, (uint)(v >> 32)); WriteU32(s, (uint)v);
        }

        private static void PutU32(byte[] b, int p, uint v)
        {
            b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v;
        }

        private static void PutU64(byte[] b, int p, ulong v)
        {
            PutU32(b, p, (uint)(v >> 32)); PutU32(b, p + 4, (uint)v);
        }

        /// <summary>
        /// Port of LaggedFibonacciGenerator::GetSeed for data_offset 0 (Dolphin DiscIO, CC0-1.0):
        /// Backward four times, undo the byte swap, rebuild the shifted bits, then re-run Initialize
        /// in checking mode. Returns null when the bytes are not generator output.
        /// </summary>
        private static class LfgSeedRecovery
        {
            private const int K = 521;
            private const int J = 32;

            public static byte[] GetSeed(byte[] data, int offset)
            {
                if (data.Length - offset < K * 4) return null;

                var buffer = new uint[K];
                for (var i = 0; i < K; i++)
                {
                    // Dolphin copies the bytes into u32 words in host (little-endian) order.
                    buffer[i] = BitConverter.ToUInt32(data, offset + i * 4);
                }

                for (var i = 0; i < 4; i++) Backward(buffer);

                for (var i = 0; i < K; i++) buffer[i] = Swap32(buffer[i]);

                for (var i = 0; i < 17; i++)
                {
                    buffer[i] = (buffer[i] & 0xFF00FFFF) | (buffer[i] << 2 & 0x00FC0000) |
                                ((buffer[i + 16] ^ buffer[i + 15]) << 9 & 0x00030000);
                }

                var seed = new byte[17 * 4];
                for (var i = 0; i < 17; i++)
                {
                    seed[i * 4] = (byte)(buffer[i] >> 24);
                    seed[i * 4 + 1] = (byte)(buffer[i] >> 16);
                    seed[i * 4 + 2] = (byte)(buffer[i] >> 8);
                    seed[i * 4 + 3] = (byte)buffer[i];
                }

                for (var i = 17; i < K; i++)
                {
                    var calculated = (buffer[i - 17] << 23) ^ (buffer[i - 16] >> 9) ^ buffer[i - 1];
                    var actual = (buffer[i] & 0xFF00FFFF) | (buffer[i] << 2 & 0x00FC0000);
                    if ((calculated & 0xFFFCFFFF) != actual) return null;
                    buffer[i] = calculated;
                }

                return seed;
            }

            private static void Backward(uint[] b)
            {
                for (var i = K; i > J; i--) b[i - 1] ^= b[i - 1 - J];
                for (var i = J; i > 0; i--) b[i - 1] ^= b[i - 1 + K - J];
            }

            private static uint Swap32(uint v)
            {
                return (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
            }
        }
    }
}
