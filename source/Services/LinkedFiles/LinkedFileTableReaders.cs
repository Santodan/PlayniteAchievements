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
                    default:
                        return LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "Unknown file format.");
                }
            }
            catch (Exception ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, ex.Message);
            }
        }

        /// <summary>The format a file's extension suggests, or null when it suggests none.</summary>
        public static LinkedFileFormat? DetectFormat(string path)
        {
            string extension;
            try
            {
                extension = Path.GetExtension(path ?? string.Empty);
            }
            catch (ArgumentException)
            {
                return null;
            }

            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".csv":
                case ".tsv":
                case ".txt":
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
                default:
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
