using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Local
{
    // XLiveLessNess (Games for Windows - LIVE replacement), ported from Achievement Watcher Next's
    // xlln.js and xllnSpa.js: <game>\xlive.dll, <game>\<exe>.cfg naming the title, the achievement
    // list in the exe's SPAFILE resource (XDBF), and per-profile achievements.dat unlock records.
    internal static class NativeXllnReader
    {
        private const int MaxExecutableBytes = 512 * 1024 * 1024;
        private const int StateRecordBytes = 16;

        internal sealed class XllnInstall
        {
            public string Exe, Config, TitleId;
        }

        internal static XllnInstall Find(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "xlive.dll"))) return null;
            foreach (var config in NativeSourceReader.Files(root, "*.exe.cfg", 0))
            {
                var exe = config.Substring(0, config.Length - 4);
                if (!File.Exists(exe)) continue;
                var title = TitleIdFromConfig(config);
                if (title != null) return new XllnInstall { Exe = exe, Config = config, TitleId = title };
            }
            return null;
        }

        // <titleid> of the XML config, which may be UTF-16 with or without a byte order mark.
        internal static string TitleIdFromConfig(string configPath)
        {
            try
            {
                if (new FileInfo(configPath).Length > 1024 * 1024) return null;
                var bytes = File.ReadAllBytes(configPath);
                string text;
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                else text = bytes.Contains((byte)0) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
                var match = Regex.Match(text, @"<titleid\b[^>]*>\s*([0-9a-fA-F]{1,8})\s*</titleid>", RegexOptions.IgnoreCase);
                return match.Success ? match.Groups[1].Value.ToUpperInvariant().PadLeft(8, '0') : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Every profile's achievements.dat for the title, newest first: beside the game, one folder
        // up, and in %LOCALAPPDATA%\XLiveLessNess.
        internal static List<string> StateFiles(string root, string titleId)
        {
            var roots = new[] { Path.Combine(root, "XLiveLessNess"), Path.Combine(Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root, "XLiveLessNess"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XLiveLessNess") };
            var files = new List<string>();
            foreach (var storage in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var titleRoot = Path.Combine(storage, "profile", "title", titleId);
                try
                {
                    if (!Directory.Exists(titleRoot)) continue;
                    files.AddRange(Directory.EnumerateDirectories(titleRoot).Take(64).Select(d => Path.Combine(d, "achievements.dat")).Where(File.Exists));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        }

        // One state file: 16-byte records (id u32, FILETIME, flags), little-endian. Only unlocks are
        // appended, so a file that is not a whole number of records is refused.
        internal static Dictionary<string, DateTime?> ParseState(byte[] buffer)
        {
            if (buffer == null || buffer.Length > 1024 * 1024 || buffer.Length % StateRecordBytes != 0) throw new InvalidDataException("XLiveLessNess achievements.dat is not a whole number of records.");
            var unlocked = new Dictionary<string, DateTime?>();
            for (var at = 0; at < buffer.Length; at += StateRecordBytes)
            {
                var id = BitConverter.ToUInt32(buffer, at).ToString(System.Globalization.CultureInfo.InvariantCulture);
                DateTime? time = null;
                var ticks = BitConverter.ToInt64(buffer, at + 4);
                if (ticks > 0)
                {
                    try { var value = DateTime.FromFileTimeUtc(ticks); if (value.Year >= 2000 && value.Year <= 2100) time = value; }
                    catch (ArgumentOutOfRangeException) { }
                }
                // An id can appear twice across a rewrite: keep the earliest real time.
                if (!unlocked.TryGetValue(id, out var previous) || (time.HasValue && (!previous.HasValue || time < previous))) unlocked[id] = time ?? previous;
            }
            return unlocked;
        }

        internal static List<NativeAchievement> Schema(string exe, string titleId, string iconCache)
        {
            var info = new FileInfo(exe);
            if (!info.Exists || info.Length > MaxExecutableBytes) throw new InvalidDataException("The game executable is missing or too large to read.");
            var spa = ExtractSpa(File.ReadAllBytes(exe));
            var parsed = ParseSpa(spa);
            if (parsed.TitleId != null && titleId != null && !parsed.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The executable declares title " + parsed.TitleId + " but its .cfg says " + titleId + ".");
            var strings = parsed.Strings.ContainsKey(1) ? parsed.Strings[1] : parsed.Strings.OrderBy(p => p.Key).First().Value;
            string Text(int id) => strings.TryGetValue(id, out var value) ? value.Trim() : "";
            if (!string.IsNullOrWhiteSpace(iconCache)) Directory.CreateDirectory(iconCache);
            return parsed.Achievements.Select(a =>
            {
                string icon = null;
                if (!string.IsNullOrWhiteSpace(iconCache) && parsed.Images.TryGetValue(a.ImageId, out var png))
                {
                    icon = Path.Combine(iconCache, "xlln-" + (titleId ?? "title") + "-" + a.ImageId + ".png");
                    try { if (!File.Exists(icon)) File.WriteAllBytes(icon, png); }
                    catch (IOException) { icon = null; }
                    catch (UnauthorizedAccessException) { icon = null; }
                }
                return new NativeAchievement
                {
                    Id = a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = Text(a.TitleStringId).Length > 0 ? Text(a.TitleStringId) : a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Description = Text(a.UnlockedDescriptionId).Length > 0 ? Text(a.UnlockedDescriptionId) : Text(a.LockedDescriptionId),
                    // Bit 3 shows an achievement before it is earned; without it the achievement is secret.
                    Hidden = (a.Flags & 0x8) == 0, Points = a.Gamerscore, Icon = icon
                };
            }).ToList();
        }

        private sealed class SpaAchievement
        {
            public int Id, TitleStringId, UnlockedDescriptionId, LockedDescriptionId, Gamerscore;
            public uint ImageId, Flags;
        }

        private sealed class SpaContents
        {
            public string TitleId;
            public List<SpaAchievement> Achievements = new List<SpaAchievement>();
            public Dictionary<uint, Dictionary<int, string>> Strings = new Dictionary<uint, Dictionary<int, string>>();
            public Dictionary<uint, byte[]> Images = new Dictionary<uint, byte[]>();
        }

        private static void Need(byte[] b, long offset, long length, string what)
        {
            if (offset < 0 || length < 0 || offset + length > b.Length) throw new InvalidDataException(what + " is outside the file.");
        }

        private static ushort U16Be(byte[] b, int at) { Need(b, at, 2, "SPA field"); return (ushort)((b[at] << 8) | b[at + 1]); }
        private static uint U32Be(byte[] b, int at) { Need(b, at, 4, "SPA field"); return (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]); }
        private static ushort U16Le(byte[] b, int at) { Need(b, at, 2, "PE field"); return BitConverter.ToUInt16(b, at); }
        private static uint U32Le(byte[] b, int at) { Need(b, at, 4, "PE field"); return BitConverter.ToUInt32(b, at); }
        private static bool Magic(byte[] b, int at, string magic) => at + 4 <= b.Length && Encoding.ASCII.GetString(b, at, 4) == magic;

        // The SPAFILE RT_RCDATA resource of a PE image, or the buffer itself when it is already XDBF.
        internal static byte[] ExtractSpa(byte[] b)
        {
            if (Magic(b, 0, "XDBF")) return b;
            if (b.Length < 0x100 || b[0] != 'M' || b[1] != 'Z') throw new InvalidDataException("Not a PE executable.");
            var pe = (int)U32Le(b, 0x3C);
            if (U32Le(b, pe) != 0x00004550) throw new InvalidDataException("Invalid PE signature.");
            var sectionCount = U16Le(b, pe + 6);
            var optionalSize = U16Le(b, pe + 20);
            var optional = pe + 24;
            var magic = U16Le(b, optional);
            var directories = magic == 0x10b ? optional + 96 : magic == 0x20b ? optional + 112 : 0;
            if (directories == 0 || sectionCount == 0 || sectionCount > 96) throw new InvalidDataException("Unsupported PE header.");
            var resourceRva = U32Le(b, directories + 16);
            if (resourceRva == 0) throw new InvalidDataException("The executable has no resources.");
            var sections = Enumerable.Range(0, sectionCount).Select(i => optional + optionalSize + i * 40)
                .Select(at => new { VirtualSize = U32Le(b, at + 8), VirtualAddress = U32Le(b, at + 12), RawSize = U32Le(b, at + 16), RawOffset = U32Le(b, at + 20) }).ToList();
            int ToOffset(uint rva)
            {
                foreach (var s in sections)
                {
                    var span = Math.Max(s.VirtualSize, s.RawSize);
                    if (rva >= s.VirtualAddress && rva < s.VirtualAddress + span)
                    {
                        var offset = (long)s.RawOffset + (rva - s.VirtualAddress);
                        if (offset < b.Length) return (int)offset;
                    }
                }
                if (rva < b.Length) return (int)rva;
                throw new InvalidDataException("PE resource address is outside the image.");
            }
            var resourceBase = ToOffset(resourceRva);
            List<Tuple<int, string, bool, int>> Entries(int directoryOffset)
            {
                var at = resourceBase + directoryOffset;
                var count = U16Le(b, at + 12) + U16Le(b, at + 14);
                if (count > 0x4000) throw new InvalidDataException("PE resource directory is implausibly large.");
                var list = new List<Tuple<int, string, bool, int>>();
                for (var i = 0; i < count; i++)
                {
                    var entry = at + 16 + i * 8;
                    var name = U32Le(b, entry);
                    var data = U32Le(b, entry + 4);
                    string label = "";
                    if ((name & 0x80000000) != 0)
                    {
                        var nameAt = resourceBase + (int)(name & 0x7FFFFFFF);
                        var length = U16Le(b, nameAt);
                        Need(b, nameAt + 2, length * 2, "PE resource name");
                        label = Encoding.Unicode.GetString(b, nameAt + 2, length * 2);
                    }
                    list.Add(Tuple.Create((int)(name & 0xFFFF), label, (data & 0x80000000) != 0, (int)(data & 0x7FFFFFFF)));
                }
                return list;
            }
            var rcData = Entries(0).FirstOrDefault(e => e.Item3 && e.Item1 == 10) ?? throw new InvalidDataException("The executable has no RT_RCDATA resources.");
            var spa = Entries(rcData.Item4).FirstOrDefault(e => e.Item3 && e.Item2.TrimStart('#').Trim().Equals("SPAFILE", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The executable has no SPAFILE resource.");
            var language = Entries(spa.Item4).FirstOrDefault(e => !e.Item3) ?? throw new InvalidDataException("SPAFILE resource has no data entry.");
            var dataAt = resourceBase + language.Item4;
            var size = U32Le(b, dataAt + 4);
            if (size == 0 || size > 64 * 1024 * 1024) throw new InvalidDataException("SPAFILE has an implausible size.");
            var start = ToOffset(U32Le(b, dataAt));
            Need(b, start, size, "SPAFILE data");
            var result = new byte[size];
            Buffer.BlockCopy(b, start, result, 0, (int)size);
            if (!Magic(result, 0, "XDBF")) throw new InvalidDataException("SPAFILE resource is not an XDBF container.");
            return result;
        }

        // XDBF: XACH achievements, XTHD title id, XSTR string tables per language, PNG images.
        private static SpaContents ParseSpa(byte[] b)
        {
            if (!Magic(b, 0, "XDBF") || b.Length < 24) throw new InvalidDataException("Invalid XDBF container.");
            var tableLength = U32Be(b, 8);
            var entryCount = U32Be(b, 12);
            var freeLength = U32Be(b, 16);
            if (entryCount > tableLength || tableLength > 65536 || freeLength > 65536) throw new InvalidDataException("XDBF table counts are inconsistent.");
            var dataOffset = 24L + tableLength * 18 + freeLength * 8;
            if (dataOffset > b.Length) throw new InvalidDataException("XDBF data area is outside the file.");
            var result = new SpaContents();
            for (var i = 0; i < entryCount; i++)
            {
                var at = 24 + i * 18;
                var space = U16Be(b, at);
                var id = U32Be(b, at + 6);
                var offset = dataOffset + U32Be(b, at + 10);
                var length = U32Be(b, at + 14);
                if (length == 0 || offset + length > b.Length) continue;
                var data = new byte[length];
                Buffer.BlockCopy(b, (int)offset, data, 0, (int)length);
                if (space == 1 && Magic(data, 0, "XACH") && data.Length >= 14)
                {
                    var count = U16Be(data, 12);
                    if (count > 10000) throw new InvalidDataException("XACH count is implausible.");
                    Need(data, 14, count * 36L, "XACH records");
                    for (var n = 0; n < count; n++)
                    {
                        var r = 14 + n * 36;
                        result.Achievements.Add(new SpaAchievement { Id = U16Be(data, r), TitleStringId = U16Be(data, r + 2), UnlockedDescriptionId = U16Be(data, r + 4),
                            LockedDescriptionId = U16Be(data, r + 6), ImageId = U32Be(data, r + 8), Gamerscore = U16Be(data, r + 12), Flags = U32Be(data, r + 16) });
                    }
                }
                else if (space == 1 && Magic(data, 0, "XTHD") && data.Length >= 16)
                    result.TitleId = U32Be(data, 12).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                else if (space == 3 && Magic(data, 0, "XSTR") && data.Length >= 14)
                {
                    try
                    {
                        var table = new Dictionary<int, string>();
                        var count = U16Be(data, 12);
                        var cursor = 14;
                        for (var n = 0; n < count && n < 10000; n++)
                        {
                            var stringId = U16Be(data, cursor);
                            var stringLength = U16Be(data, cursor + 2);
                            cursor += 4;
                            Need(data, cursor, stringLength, "XSTR string");
                            table[stringId] = Encoding.UTF8.GetString(data, cursor, stringLength).TrimEnd('\0');
                            cursor += stringLength;
                        }
                        result.Strings[id] = table;
                    }
                    catch (InvalidDataException) { }
                }
                else if (space == 2 && data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                    result.Images[id] = data;
            }
            if (result.Achievements.Count == 0) throw new InvalidDataException("SPAFILE carries no achievement table.");
            if (result.Strings.Count == 0) throw new InvalidDataException("SPAFILE carries no string table.");
            return result;
        }
    }
}
