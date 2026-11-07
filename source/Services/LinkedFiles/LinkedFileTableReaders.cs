using PlayniteAchievements.Models.Settings;
using System;
using System.IO;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Reads a linked file into a <see cref="LinkedFileTable"/>. The only part of the linked
    /// file path that touches the file system; never throws, and never returns a table from a
    /// file that was still being written, so a torn read can never be applied.
    /// </summary>
    public static class LinkedFileTableReaders
    {
        public const long MaxFileBytes = 64L * 1024 * 1024;

        public static LinkedFileReadResult Read(string path, LinkedFileSchema schema)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.NoFilePath, "No file is linked.");
            }

            if (schema == null)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "The link has no mapping.");
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return LinkedFileReadResult.Failed(LinkedFileReadStatus.FileMissing, "The file was not found.");
                }

                var lengthBefore = info.Length;
                var writeBefore = info.LastWriteTimeUtc;
                if (lengthBefore == 0)
                {
                    return LinkedFileReadResult.Failed(LinkedFileReadStatus.FileUnstable, "The file is empty.");
                }

                if (lengthBefore > MaxFileBytes)
                {
                    return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The file is larger than 64 MB.");
                }

                var bytes = ReadAllBytesShared(path);
                info.Refresh();
                if (!info.Exists || info.Length != lengthBefore || info.LastWriteTimeUtc != writeBefore || bytes.Length != lengthBefore)
                {
                    return LinkedFileReadResult.Failed(LinkedFileReadStatus.FileUnstable, "The file changed while it was being read.");
                }

                return Parse(bytes, schema);
            }
            catch (Exception ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, ex.Message);
            }
        }

        /// <summary>Parses file bytes with the schema's format. Never throws.</summary>
        public static LinkedFileReadResult Parse(byte[] bytes, LinkedFileSchema schema)
        {
            if (schema == null)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "The link has no mapping.");
            }

            try
            {
                switch (schema.Format)
                {
                    case LinkedFileFormat.Delimited:
                        return DelimitedTableReader.Read(LinkedFileTextDecoder.Decode(bytes), schema);
                    case LinkedFileFormat.Json:
                        return JsonTableReader.Read(LinkedFileTextDecoder.Decode(bytes), schema);
                    case LinkedFileFormat.Toml:
                        return TomlTableReader.Read(LinkedFileTextDecoder.Decode(bytes), schema);
                    case LinkedFileFormat.Xlsx:
                        return XlsxTableReader.Read(bytes, schema);
                    case LinkedFileFormat.Xml:
                        return XmlTableReader.Read(bytes, schema);
                    case LinkedFileFormat.Ini:
                        return IniTableReader.Read(LinkedFileTextDecoder.Decode(bytes), schema);
                    case LinkedFileFormat.Binary:
                        return BinaryTableReader.Read(bytes);
                    default:
                        return LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "Unknown file format.");
                }
            }
            catch (Exception ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, ex.Message);
            }
        }

        private const int SniffBytes = 64 * 1024;

        /// <summary>
        /// The format a file is read as, from its extension when that is unambiguous and from
        /// its first bytes otherwise (text files, common save extensions, no extension). Falls
        /// back to the extension when the file cannot be read; null when nothing decides it.
        /// </summary>
        public static LinkedFileFormat? DetectFormat(string path)
        {
            string extension;
            try
            {
                extension = (Path.GetExtension(path ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return null;
            }

            switch (extension)
            {
                case ".csv":
                case ".tsv":
                case ".tab":
                case ".psv":
                    return LinkedFileFormat.Delimited;
                case ".json":
                    return LinkedFileFormat.Json;
                case ".toml":
                    return LinkedFileFormat.Toml;
                case ".xlsx":
                case ".xlsm":
                    return LinkedFileFormat.Xlsx;
                case ".xml":
                    return LinkedFileFormat.Xml;
                case ".ini":
                case ".cfg":
                case ".conf":
                    return LinkedFileFormat.Ini;
            }

            var byExtension = extension == ".txt"
                ? LinkedFileFormat.Delimited
                : extension == ".bin" || extension == ".dat" || extension == ".sav"
                    ? LinkedFileFormat.Binary
                    : (LinkedFileFormat?)null;

            var head = ReadHead(path);
            return head == null || head.Length == 0 ? byExtension : SniffFormat(head) ?? byExtension;
        }

        /// <summary>
        /// The format the first bytes of a file show, or null when they show none: a zip is a
        /// workbook, control bytes are binary, a leading brace or list is JSON, a leading angle
        /// bracket is XML, and flag, hex or base64 text is binary.
        /// </summary>
        internal static LinkedFileFormat? SniffFormat(byte[] head)
        {
            if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
            {
                return LinkedFileFormat.Xlsx;
            }

            var start = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
            if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
            {
                return null;
            }

            for (var i = start; i < head.Length; i++)
            {
                var b = head[i];
                if (b < 0x09 || (b > 0x0D && b < 0x20))
                {
                    return LinkedFileFormat.Binary;
                }
            }

            var first = NextNonSpace(head, start);
            if (first >= 0)
            {
                switch ((char)head[first])
                {
                    case '{':
                        return LinkedFileFormat.Json;
                    case '<':
                        return LinkedFileFormat.Xml;
                    case '[':
                        // A list opens with a value; an INI section opens with its name.
                        var next = NextNonSpace(head, first + 1);
                        if (next < 0 || "{[]\"-0123456789".IndexOf((char)head[next]) >= 0)
                        {
                            return LinkedFileFormat.Json;
                        }

                        return LinkedFileFormat.Ini;
                }
            }

            if (head.Length < SniffBytes && BinaryTableReader.IsEncodedText(head))
            {
                return LinkedFileFormat.Binary;
            }

            return null;
        }

        private static int NextNonSpace(byte[] bytes, int from)
        {
            for (var i = from; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)' ' && (bytes[i] < 0x09 || bytes[i] > 0x0D))
                {
                    return i;
                }
            }

            return -1;
        }

        private static byte[] ReadHead(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[SniffBytes];
                    var read = 0;
                    int chunk;
                    while (read < buffer.Length && (chunk = stream.Read(buffer, read, buffer.Length - read)) > 0)
                    {
                        read += chunk;
                    }

                    Array.Resize(ref buffer, read);
                    return buffer;
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static byte[] ReadAllBytesShared(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
    }
}
