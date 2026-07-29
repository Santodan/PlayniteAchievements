using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.Steam.Local
{
    internal sealed class SteamKvNode
    {
        public string Name { get; set; }
        public string StringValue { get; set; }
        public long? IntegerValue { get; set; }
        public List<SteamKvNode> Children { get; } = new List<SteamKvNode>();

        public SteamKvNode Child(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return Children.Find(child =>
                string.Equals(child?.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static class SteamBinaryKeyValuesReader
    {
        private const int MaxDepth = 64;
        private const int MaxNodes = 1_000_000;
        private const int MaxStringBytes = 64 * 1024;
        private const long MaxFileBytes = 128L * 1024L * 1024L;

        public static bool TryRead(string path, out SteamKvNode root)
        {
            root = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.SequentialScan))
                {
                    if (stream.Length <= 0 || stream.Length > MaxFileBytes)
                    {
                        return false;
                    }

                    using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
                    {
                        var syntheticRoot = new SteamKvNode { Name = string.Empty };
                        var count = 0;
                        if (!TryReadChildren(reader, syntheticRoot, 0, ref count, requireEndMarker: false))
                        {
                            return false;
                        }

                        root = syntheticRoot;
                        return syntheticRoot.Children.Count > 0;
                    }
                }
            }
            catch
            {
                root = null;
                return false;
            }
        }

        private static bool TryReadChildren(
            BinaryReader reader,
            SteamKvNode parent,
            int depth,
            ref int nodeCount,
            bool requireEndMarker)
        {
            if (depth > MaxDepth)
            {
                return false;
            }

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var type = reader.ReadByte();
                if (type == 0x08 || type == 0x0B)
                {
                    return true;
                }

                var name = ReadNullTerminatedUtf8(reader);
                if (name == null || ++nodeCount > MaxNodes)
                {
                    return false;
                }

                var node = new SteamKvNode { Name = name };
                parent.Children.Add(node);

                switch (type)
                {
                    case 0x00:
                        if (!TryReadChildren(reader, node, depth + 1, ref nodeCount, requireEndMarker: true))
                        {
                            return false;
                        }
                        break;

                    case 0x01:
                        node.StringValue = ReadNullTerminatedUtf8(reader);
                        if (node.StringValue == null)
                        {
                            return false;
                        }
                        break;

                    case 0x02:
                    case 0x04:
                    case 0x06:
                        EnsureRemaining(reader, 4);
                        node.IntegerValue = reader.ReadInt32();
                        break;

                    case 0x03:
                        EnsureRemaining(reader, 4);
                        reader.ReadSingle();
                        break;

                    case 0x05:
                        node.StringValue = ReadWideString(reader);
                        if (node.StringValue == null)
                        {
                            return false;
                        }
                        break;

                    case 0x07:
                    case 0x09:
                        EnsureRemaining(reader, 8);
                        node.IntegerValue = reader.ReadInt64();
                        break;

                    case 0x0A:
                        node.IntegerValue = 0;
                        break;

                    default:
                        return false;
                }
            }

            return !requireEndMarker;
        }

        private static string ReadNullTerminatedUtf8(BinaryReader reader)
        {
            var bytes = new List<byte>();
            while (reader.BaseStream.Position < reader.BaseStream.Length && bytes.Count <= MaxStringBytes)
            {
                var value = reader.ReadByte();
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }

            return null;
        }

        private static string ReadWideString(BinaryReader reader)
        {
            EnsureRemaining(reader, 2);
            var characterCount = reader.ReadUInt16();
            if (characterCount > MaxStringBytes / 2)
            {
                return null;
            }

            var byteCount = characterCount * 2;
            EnsureRemaining(reader, byteCount);
            return Encoding.Unicode.GetString(reader.ReadBytes(byteCount)).TrimEnd('\0');
        }

        private static void EnsureRemaining(BinaryReader reader, long count)
        {
            if (count < 0 || reader.BaseStream.Length - reader.BaseStream.Position < count)
            {
                throw new EndOfStreamException();
            }
        }
    }
}
