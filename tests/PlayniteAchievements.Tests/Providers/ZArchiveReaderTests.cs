using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.ShadPS4;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ZArchiveReaderTests
    {
        [TestMethod]
        public void TryReadFile_CompressedBlock_ReturnsNestedFile()
        {
            var npbind = Encoding.ASCII.GetBytes("....UP9000-NPWR10146_00....");
            var archive = ZArchiveTestWriter.Build(new Dictionary<string, byte[]>
            {
                ["eboot.bin"] = new byte[300],
                ["sce_sys/param.sfo"] = Encoding.ASCII.GetBytes("PSF"),
                ["sce_sys/npbind.dat"] = npbind
            });

            var bytes = ZArchiveReader.TryReadFile(new MemoryStream(archive), "sce_sys/npbind.dat", 1024);

            CollectionAssert.AreEqual(npbind, bytes);
        }

        [TestMethod]
        public void TryReadFile_RawBlocks_ReadsAcrossBlockBoundary()
        {
            // Random data does not shrink under zstd, so every block is stored raw, and a file
            // larger than one block spans a boundary.
            var random = new Random(7);
            var big = new byte[100 * 1024];
            random.NextBytes(big);
            var archive = ZArchiveTestWriter.Build(new Dictionary<string, byte[]>
            {
                ["a.bin"] = new byte[10],
                ["sce_sys/big.dat"] = big
            });

            var bytes = ZArchiveReader.TryReadFile(new MemoryStream(archive), "sce_sys/big.dat", big.Length);

            CollectionAssert.AreEqual(big, bytes);
        }

        [TestMethod]
        public void TryReadFile_LookupIgnoresCaseAndSeparators()
        {
            var archive = ZArchiveTestWriter.Build(new Dictionary<string, byte[]>
            {
                ["sce_sys/npbind.dat"] = new byte[] { 1, 2, 3 }
            });

            var bytes = ZArchiveReader.TryReadFile(new MemoryStream(archive), @"SCE_SYS\NPBIND.DAT", 1024);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
        }

        [TestMethod]
        public void TryReadFile_MissingPathOrDirectory_ReturnsNull()
        {
            var archive = ZArchiveTestWriter.Build(new Dictionary<string, byte[]>
            {
                ["sce_sys/param.sfo"] = new byte[] { 1 }
            });

            Assert.IsNull(ZArchiveReader.TryReadFile(new MemoryStream(archive), "sce_sys/npbind.dat", 1024));
            Assert.IsNull(ZArchiveReader.TryReadFile(new MemoryStream(archive), "sce_sys", 1024));
            Assert.IsNull(ZArchiveReader.TryReadFile(new MemoryStream(archive), "sce_sys/param.sfo/x", 1024));
        }

        [TestMethod]
        public void TryReadFile_FileOverLimit_ReturnsNull()
        {
            var archive = ZArchiveTestWriter.Build(new Dictionary<string, byte[]>
            {
                ["sce_sys/npbind.dat"] = new byte[2048]
            });

            Assert.IsNull(ZArchiveReader.TryReadFile(new MemoryStream(archive), "sce_sys/npbind.dat", 1024));
        }

        [TestMethod]
        public void TryReadFile_BadMagicOrTruncated_ReturnsNull()
        {
            var archive = ZArchiveTestWriter.Build(new Dictionary<string, byte[]>
            {
                ["sce_sys/npbind.dat"] = new byte[] { 1 }
            });

            var badMagic = (byte[])archive.Clone();
            badMagic[badMagic.Length - 1] ^= 0xFF;
            Assert.IsNull(ZArchiveReader.TryReadFile(new MemoryStream(badMagic), "sce_sys/npbind.dat", 1024));

            var truncated = archive.Take(archive.Length - 10).ToArray();
            Assert.IsNull(ZArchiveReader.TryReadFile(new MemoryStream(truncated), "sce_sys/npbind.dat", 1024));

            Assert.IsNull(ZArchiveReader.TryReadFile(new MemoryStream(new byte[50]), "sce_sys/npbind.dat", 1024));
        }
    }

    /// <summary>
    /// Writes a ZArchive the way Exzap/ZArchive's writer does: file data packed into 64 KiB
    /// blocks (zstd, or raw when compression does not shrink the block), offset records, a
    /// name table, a breadth-first file tree, and a big-endian footer.
    /// </summary>
    internal static class ZArchiveTestWriter
    {
        private const int BlockSize = 64 * 1024;

        private sealed class Node
        {
            public string Name;
            public byte[] Data;
            public readonly List<Node> Children = new List<Node>();
            public bool IsFile => Data != null;
            public long DataOffset;
            public int NameOffset;
            public int FirstChildIndex;
        }

        public static byte[] Build(IDictionary<string, byte[]> files)
        {
            var root = new Node { Name = string.Empty };
            foreach (var file in files)
            {
                var parts = file.Key.Split('/');
                var current = root;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var next = current.Children.FirstOrDefault(c => !c.IsFile && c.Name == parts[i]);
                    if (next == null)
                    {
                        next = new Node { Name = parts[i] };
                        current.Children.Add(next);
                    }

                    current = next;
                }

                current.Children.Add(new Node { Name = parts[parts.Length - 1], Data = file.Value });
            }

            // Breadth-first order, as the writer stores it: every directory's children are contiguous.
            var order = new List<Node> { root };
            for (var i = 0; i < order.Count; i++)
            {
                order[i].FirstChildIndex = order.Count;
                order.AddRange(order[i].Children);
            }

            var raw = new MemoryStream();
            foreach (var node in order.Where(n => n.IsFile))
            {
                node.DataOffset = raw.Position;
                raw.Write(node.Data, 0, node.Data.Length);
            }

            var padded = raw.ToArray();
            Array.Resize(ref padded, Math.Max(BlockSize, (padded.Length + BlockSize - 1) / BlockSize * BlockSize));

            var output = new MemoryStream();
            var blockSizes = new List<int>();
            var blockOffsets = new List<long>();
            using (var zstd = new ZstdSharp.Compressor(6))
            {
                for (var offset = 0; offset < padded.Length; offset += BlockSize)
                {
                    var block = new byte[BlockSize];
                    Buffer.BlockCopy(padded, offset, block, 0, BlockSize);
                    var compressed = zstd.Wrap(block).ToArray();
                    var stored = compressed.Length >= BlockSize ? block : compressed;
                    blockOffsets.Add(output.Position);
                    blockSizes.Add(stored.Length);
                    output.Write(stored, 0, stored.Length);
                }
            }

            var compressedDataSize = output.Position;

            var recordsOffset = output.Position;
            for (var r = 0; r < blockSizes.Count; r += 16)
            {
                WriteUInt64(output, (ulong)blockOffsets[r]);
                for (var i = 0; i < 16; i++)
                {
                    WriteUInt16(output, r + i < blockSizes.Count ? (ushort)(blockSizes[r + i] - 1) : (ushort)0);
                }
            }

            var recordsSize = output.Position - recordsOffset;

            var namesOffset = output.Position;
            foreach (var node in order.Skip(1))
            {
                node.NameOffset = (int)(output.Position - namesOffset);
                var name = Encoding.UTF8.GetBytes(node.Name);
                output.WriteByte((byte)name.Length);
                output.Write(name, 0, name.Length);
            }

            var namesSize = output.Position - namesOffset;

            var treeOffset = output.Position;
            foreach (var node in order)
            {
                var nameField = node == root ? 0x7FFFFFFFu : (uint)node.NameOffset;
                if (node.IsFile)
                {
                    WriteUInt32(output, nameField | 0x80000000u);
                    WriteUInt32(output, (uint)node.DataOffset);
                    WriteUInt32(output, (uint)node.Data.Length);
                    WriteUInt32(output, 0);
                }
                else
                {
                    WriteUInt32(output, nameField);
                    WriteUInt32(output, (uint)node.FirstChildIndex);
                    WriteUInt32(output, (uint)node.Children.Count);
                    WriteUInt32(output, 0);
                }
            }

            var treeSize = output.Position - treeOffset;
            var metaOffset = output.Position;

            var sections = new[]
            {
                (0L, compressedDataSize),
                (recordsOffset, recordsSize),
                (namesOffset, namesSize),
                (treeOffset, treeSize),
                (metaOffset, 0L),
                (metaOffset, 0L)
            };
            foreach (var (offset, size) in sections)
            {
                WriteUInt64(output, (ulong)offset);
                WriteUInt64(output, (ulong)size);
            }

            output.Write(new byte[32], 0, 32);
            WriteUInt64(output, (ulong)(output.Position + 8 + 4 + 4));
            WriteUInt32(output, 0x61bf3a01);
            WriteUInt32(output, 0x169f52d6);
            return output.ToArray();
        }

        private static void WriteUInt16(Stream s, ushort v)
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        private static void WriteUInt32(Stream s, uint v)
        {
            WriteUInt16(s, (ushort)(v >> 16));
            WriteUInt16(s, (ushort)v);
        }

        private static void WriteUInt64(Stream s, ulong v)
        {
            WriteUInt32(s, (uint)(v >> 32));
            WriteUInt32(s, (uint)v);
        }
    }
}
