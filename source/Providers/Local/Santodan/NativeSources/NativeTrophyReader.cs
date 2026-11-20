using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.Local
{
    internal static class NativeTrophyReader
    {
        private const int MaxPackage = 128 * 1024 * 1024;
        private const int MaxEntry = 4 * 1024 * 1024;
        internal static List<NativeAchievement> Read(string file, string cache)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension == ".xml" || extension == ".sfm")
                return Xml(NativeSourceReader.Text(file), id => FindExtractedIcon(file, id));
            if (extension != ".trp" && extension != ".ucp") throw new InvalidDataException("Select a TRP/UCP package or extracted TROP.XML.");
            var entries = Container(file, extension == ".ucp");
            Func<string, string> icon = id => CacheIcon(entries, extension == ".ucp" ? "trop" + int.Parse(id).ToString("D4") + ".png" : "TROP" + int.Parse(id).ToString("D3") + ".PNG", cache);
            if (extension == ".ucp")
            {
                if (!entries.TryGetValue("tropconf.json", out var conf)) throw new InvalidDataException("UCP has no tropconf.json.");
                var config = JObject.Parse(Encoding.UTF8.GetString(conf));
                var locale = "tropmeta_en-US.json";
                if (!entries.ContainsKey(locale)) locale = "tropmeta_" + (string)config["defaultLanguage"] + ".json";
                if (!entries.TryGetValue(locale, out var metadata)) throw new InvalidDataException("UCP has no readable trophy texts.");
                var texts = JObject.Parse(Encoding.UTF8.GetString(metadata))["metadata"]?["trophyMetadata"] as JArray;
                if (texts == null || !(config["trophies"] is JArray definitions)) throw new InvalidDataException("Invalid UCP trophy definitions.");
                return definitions.Take(2048).Select(t =>
                {
                    var id = (string)t["id"];
                    if (!Regex.IsMatch(id ?? "", "^\\d{1,4}$")) return null;
                    var text = texts.FirstOrDefault(row => (string)row["id"] == id);
                    return new NativeAchievement { Id = id, Name = (string)text?["name"] ?? "Trophy " + id, Description = (string)text?["detail"] ?? "", Hidden = (bool?)t["hidden"] == true, TrophyType = Grade((string)t["grade"]), Icon = icon(id) };
                }).Where(t => t != null).GroupBy(t => t.Id).Select(g => g.First()).ToList();
            }
            // Prefer English, then the default language. npbind supplies identity, never trophy texts.
            var xml = entries.Keys.Where(n => Regex.IsMatch(n, @"^TROP(?:_\d{2})?\.(XML|SFM|ESFM)$", RegexOptions.IgnoreCase))
                .OrderBy(n => n.StartsWith("TROP_01.", StringComparison.OrdinalIgnoreCase) ? 0 : n.StartsWith("TROP.", StringComparison.OrdinalIgnoreCase) ? 1 : 2).FirstOrDefault();
            if (xml == null) throw new InvalidDataException("TRP has no trophy definition document.");
            var bytes = entries[xml];
            if (xml.EndsWith(".ESFM", StringComparison.OrdinalIgnoreCase)) bytes = Decrypt(bytes, BindingFor(file));
            return Xml(Encoding.UTF8.GetString(bytes).TrimEnd('\0'), icon);
        }

        internal static Dictionary<string, byte[]> Container(string file, bool ucp)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked trophy packages are not read.");
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || stream.Length > MaxPackage) throw new InvalidDataException("Invalid trophy package size.");
                var header = reader.ReadBytes(64);
                if (U32(header, 0) != (ucp ? 0xb228c60aU : 0xdca24d00U)) throw new InvalidDataException("Invalid trophy package signature.");
                if (ucp && (U32(header, 4) != 1 || U64(header, 8) < 64 || U64(header, 8) > (ulong)stream.Length)) throw new InvalidDataException("Invalid UCP header.");
                var count = U32(header, 16);
                var stride = ucp ? 64U : U32(header, 20);
                var start = ucp ? (long)U32(header, 20) + 32 : Path.GetFileName(file).Equals("TROPHY.TRP", StringComparison.OrdinalIgnoreCase) ? 64 : 96;
                if (count > 4096 || stride < 64 || stride > 4096 || start + count * (long)stride > stream.Length) throw new InvalidDataException("Invalid trophy entry table.");
                long total = 0;
                for (var i = 0; i < count; i++)
                {
                    stream.Position = start + i * (long)stride;
                    var entry = reader.ReadBytes(64);
                    var name = Encoding.ASCII.GetString(entry, 0, 32).Split('\0')[0];
                    var offset = U64(entry, 32); var length = U64(entry, 40);
                    if (offset > (ulong)stream.Length || length > (ulong)stream.Length - offset) throw new InvalidDataException("Trophy entry points outside its package.");
                    if (!Regex.IsMatch(name, @"^(?:TROP(?:_\d{2})?\.(?:XML|SFM|ESFM)|TROP\d{3,4}\.PNG|tropconf\.json|tropmeta_[a-zA-Z-]+\.json)$", RegexOptions.IgnoreCase)) continue;
                    if (length > MaxEntry || (total += (long)length) > 64 * 1024 * 1024) throw new InvalidDataException("Trophy resources exceed extraction limits.");
                    stream.Position = (long)offset;
                    var data = reader.ReadBytes((int)length);
                    if (data.Length != (int)length) throw new InvalidDataException("Trophy package is truncated.");
                    result[name] = data;
                }
            }
            return result;
        }

        internal static uint U32(byte[] data, int offset) => (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3];
        private static ulong U64(byte[] data, int offset) => (ulong)U32(data, offset) << 32 | U32(data, offset + 4);

        private static string BindingFor(string file)
        {
            var slotMatch = Regex.Match(Path.GetFileName(file), @"trophy(\d{2})", RegexOptions.IgnoreCase);
            var slot = slotMatch.Success ? int.Parse(slotMatch.Groups[1].Value) : 0;
            var bind = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(file)), "npbind.dat");
            if (!File.Exists(bind)) throw new InvalidDataException("Encrypted PS4 definitions require sce_sys/npbind.dat, or an extracted TROP.XML override.");
            if (new FileInfo(bind).Length > 1024 * 1024) throw new InvalidDataException("Invalid npbind.dat size.");
            var bytes = File.ReadAllBytes(bind);
            if (bytes.Length < 128 || bytes.Length > 1024 * 1024 || U32(bytes, 0) != 0xd294a018) throw new InvalidDataException("Invalid npbind.dat.");
            var stride = U64(bytes, 16); var count = U64(bytes, 24);
            if (stride < 4 || stride > 4096 || count > 100) throw new InvalidDataException("Invalid npbind entry table.");
            for (ulong i = 0; i < count; i++)
            {
                var start = 128UL + i * stride;
                if (start + stride > (ulong)bytes.Length) break;
                string id = null; int label = -1;
                for (var at = (int)start; at + 4 <= (int)(start + stride);)
                {
                    var type = bytes[at] * 256 + bytes[at + 1]; var size = bytes[at + 2] * 256 + bytes[at + 3]; at += 4;
                    if (at + size > (int)(start + stride)) break;
                    if (type == 0x10) id = Encoding.ASCII.GetString(bytes, at, size).Split('\0')[0];
                    if (type == 0x11 && size == 4)
                    {
                        var literal = Encoding.ASCII.GetString(bytes, at, size).Trim('\0', ' ');
                        label = int.TryParse(literal, out var parsed) ? parsed : (int)U32(bytes, at);
                    }
                    at += size;
                }
                if (label == slot && Regex.IsMatch(id ?? "", @"^NPWR\d{5}_\d{2}$")) return id;
            }
            throw new InvalidDataException("No npbind identity for this trophy set. Select extracted TROP.XML.");
        }

        private static byte[] Decrypt(byte[] bytes, string identity)
        {
            if (bytes.Length < 32 || bytes.Length % 16 != 0) throw new InvalidDataException("Invalid encrypted trophy document.");
            var literal = "21F41A6BAD8A1D3ECA7AD586C101B7A9";
            var key = Enumerable.Range(0, 16).Select(i => Convert.ToByte(literal.Substring(i * 2, 2), 16)).ToArray();
            var id = new byte[16]; Encoding.ASCII.GetBytes(identity).CopyTo(id, 0);
            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.IV = new byte[16];
                using (var encrypt = aes.CreateEncryptor()) aes.Key = encrypt.TransformFinalBlock(id, 0, id.Length);
                aes.IV = bytes.Take(16).ToArray();
                using (var decrypt = aes.CreateDecryptor()) return decrypt.TransformFinalBlock(bytes, 16, bytes.Length - 16);
            }
        }

        internal static List<NativeAchievement> Xml(string text, Func<string, string> icon)
        {
            using (var input = new StringReader(text))
            using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxEntry }))
            {
                var doc = XDocument.Load(reader);
                return doc.Descendants("trophy").Take(2048).Where(t => Regex.IsMatch((string)t.Attribute("id") ?? "", @"^\d{1,4}$"))
                    .Select(t =>
                    {
                        var id = int.Parse((string)t.Attribute("id")).ToString();
                        return new NativeAchievement { Id = id, Name = (string)t.Element("name") ?? "Trophy " + id, Description = (string)t.Element("detail") ?? "", TrophyType = Grade((string)t.Attribute("ttype")), Hidden = (string)t.Attribute("hidden") == "yes", Icon = icon(id) };
                    }).GroupBy(t => t.Id).Select(g => g.First()).ToList();
            }
        }
        private static string Grade(string grade) => grade == "P" ? "platinum" : grade == "G" ? "gold" : grade == "S" ? "silver" : grade == "B" ? "bronze" : null;
        private static string FindExtractedIcon(string file, string id)
        {
            var root = Path.GetDirectoryName(file);
            return new[] { Path.Combine(root, "TROP" + int.Parse(id).ToString("D3") + ".PNG"), Path.Combine(root, "..", "Icons", "TROP" + int.Parse(id).ToString("D3") + ".PNG") }.FirstOrDefault(File.Exists);
        }
        private static string CacheIcon(Dictionary<string, byte[]> entries, string name, string cache)
        {
            if (string.IsNullOrWhiteSpace(cache) || !entries.TryGetValue(name, out var bytes)) return null;
            if (bytes.Length < 33 || bytes.Length > 1024 * 1024 || !bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || U32(bytes, 16) > 2048 || U32(bytes, 20) > 2048) return null;
            Directory.CreateDirectory(cache);
            // Names come from a fixed numeric pattern; no package path is used as a write path.
            var target = Path.Combine(cache, Path.GetFileName(name));
            if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(bytes)) File.WriteAllBytes(target, bytes);
            return target;
        }
    }
}
