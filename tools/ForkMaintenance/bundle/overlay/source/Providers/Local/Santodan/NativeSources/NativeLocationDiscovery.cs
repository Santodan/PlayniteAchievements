using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.Local
{
    internal static class NativeLocationDiscovery
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, KeyValuePair<DateTime, string>> Cache = new Dictionary<string, KeyValuePair<DateTime, string>>();
        internal static bool IsSingleGameSource(string source) => new[] { "markerpatch", "madnesspatch", "ladxhd", "dusklight", "ghostship", "dkrr", "smb1r", "sms", "smseclipse",
            "smas", "rr64", "gdiffuser", "nectar", "nectar2", "fh2azimuth", "sanctuary", "issn", "issnfl", "deadnation", "xlln" }.Contains(source);
        // Sources that share one detected install (Eclipse uses SMS Launcher, First Light the issn_pc launcher).
        internal static string Family(string source) => source == "smseclipse" ? "sms" : source == "issnfl" ? "issn" : source;
        internal static string FindInstall(string current, string name, string gameId, string source, IEnumerable<string> folders)
        {
            if (Directory.Exists(current)) return current;
            var roots = folders.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
            var key = source + "\n" + name + "\n" + gameId + "\n" + string.Join("\n", roots);
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Key < TimeSpan.FromSeconds(5)) return cached.Value;
            }
            var found = new List<string>();
            foreach (var root in roots)
            {
                foreach (var dir in Directories(root))
                {
                    var belongs = Matches(dir, name, gameId);
                    if (!belongs && !(IsSingleGameSource(source) && Family(NativeSourceReader.Detect(dir)) == Family(source))) continue;
                    var detected = NativeSourceReader.Detect(dir);
                    if (source == "auto" ? detected != null : belongs || (detected != null && Family(detected) == Family(source))) found.Add(dir);
                }
            }
            var unique = found.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var result = unique.Count == 1 ? unique[0] : null;
            lock (Gate)
            {
                if (Cache.Count > 512) Cache.Clear();
                Cache[key] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, result);
            }
            return result;
        }
        internal static IEnumerable<string> Directories(string root, int depth = 3)
        {
            if (!Directory.Exists(root)) yield break;
            var queue = new Queue<KeyValuePair<string, int>>(); queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            var count = 0;
            while (queue.Count > 0 && count++ < 400)
            {
                var entry = queue.Dequeue(); string[] children;
                try
                {
                    if ((File.GetAttributes(entry.Key) & FileAttributes.ReparsePoint) != 0) continue;
                    children = entry.Value < depth ? Directory.EnumerateDirectories(entry.Key).Take(400).ToArray() : new string[0];
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                yield return entry.Key;
                foreach (var child in children)
                    if (!Regex.IsMatch(Path.GetFileName(child), @"^(\.git|node_modules|cache|shadercache|obj)$", RegexOptions.IgnoreCase)) queue.Enqueue(new KeyValuePair<string, int>(child, entry.Value + 1));
            }
        }
        internal static bool Matches(string directory, string name, string gameId)
        {
            var folder = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (Same(folder, name) || Same(folder, gameId)) return true;
            var param = Path.Combine(directory, "sce_sys", "param.json");
            if (!File.Exists(param)) return false;
            try
            {
                var json = JObject.Parse(NativeSourceReader.Text(param));
                if (Same((string)json["titleId"], gameId)) return true;
                var languages = json["localizedParameters"] as JObject;
                return languages?.Properties().Any(p => p.Value is JObject && Same((string)p.Value["titleName"], name)) == true;
            }
            catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException || ex is UnauthorizedAccessException || ex is ArgumentException) { return false; }
        }
        internal static bool FileMatches(string file, string root, string name, string gameId)
        {
            var dir = Path.GetDirectoryName(file);
            for (var i = 0; !string.IsNullOrEmpty(dir) && i < 8; i++)
            {
                if (Matches(dir, name, gameId)) return true;
                if (string.Equals(dir.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) break;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }
        private static bool Same(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Normalize(a) == Normalize(b);
        private static string Normalize(string value) => Regex.Replace(value ?? "", @"[^\p{L}\p{Nd}]", "").ToLowerInvariant();
    }
}
