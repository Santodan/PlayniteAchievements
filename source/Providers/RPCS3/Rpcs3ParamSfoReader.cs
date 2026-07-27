using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.RPCS3
{
    internal static class Rpcs3ParamSfoReader
    {
        public static string ReadStringValue(string paramSfoPath, string key, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var values = ReadStringValues(paramSfoPath, logger);
            return values.TryGetValue(key, out var value) ? value : null;
        }

        public static IReadOnlyDictionary<string, string> ReadStringValues(string paramSfoPath, ILogger logger = null)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(paramSfoPath) || !File.Exists(paramSfoPath))
            {
                return values;
            }

            try
            {
                using (var stream = new FileStream(paramSfoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadUInt32() != 0x46535000)
                    {
                        return values;
                    }

                    reader.ReadUInt32();
                    var keyTableOffset = reader.ReadUInt32();
                    var dataTableOffset = reader.ReadUInt32();
                    var entryCount = reader.ReadUInt32();

                    for (var i = 0; i < entryCount; i++)
                    {
                        var entryOffset = 20 + (i * 16);
                        if (entryOffset + 16 > stream.Length)
                        {
                            break;
                        }

                        stream.Position = entryOffset;
                        var keyOffset = reader.ReadUInt16();
                        var format = reader.ReadUInt16();
                        var dataLength = reader.ReadUInt32();
                        reader.ReadUInt32();
                        var dataOffset = reader.ReadUInt32();

                        var key = ReadNullTerminatedString(stream, keyTableOffset + keyOffset);
                        if (string.IsNullOrWhiteSpace(key) || dataLength == 0)
                        {
                            continue;
                        }

                        if (format != 0x0204 && format != 0x0004)
                        {
                            continue;
                        }

                        var valueOffset = dataTableOffset + dataOffset;
                        if (valueOffset >= stream.Length)
                        {
                            continue;
                        }

                        var bytesToRead = (int)Math.Min(dataLength, stream.Length - valueOffset);
                        stream.Position = valueOffset;
                        var data = reader.ReadBytes(bytesToRead);
                        var value = Encoding.UTF8.GetString(data).TrimEnd('\0').Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values[key] = value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[RPCS3] Failed to parse PARAM.SFO at '{paramSfoPath}'");
            }

            return values;
        }

        private static string ReadNullTerminatedString(Stream stream, long offset)
        {
            if (offset < 0 || offset >= stream.Length)
            {
                return null;
            }

            stream.Position = offset;
            var bytes = new List<byte>();
            int value;
            while ((value = stream.ReadByte()) > 0)
            {
                bytes.Add((byte)value);
            }

            return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
