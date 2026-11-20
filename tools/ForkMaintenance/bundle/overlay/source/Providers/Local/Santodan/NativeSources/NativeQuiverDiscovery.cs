using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.Local
{
    internal static class NativeQuiverDiscovery
    {
        private static readonly object Gate = new object();
        private static DateTime _checked;
        private static string _rootsKey;
        private static List<KeyValuePair<string, string>> _installs = new List<KeyValuePair<string, string>>();
        internal static string FindInstall(string gameName, IEnumerable<string> extraFolders = null)
        {
            lock (Gate)
            {
                var roots = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuiverLauncher"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.quiverlauncher.app") }
                    .Concat(extraFolders ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var rootsKey = string.Join("\n", roots);
                if (_rootsKey != rootsKey || DateTime.UtcNow - _checked > TimeSpan.FromSeconds(30))
                {
                    var entries = new List<KeyValuePair<string, string>>();
                    foreach (var root in roots)
                    {
                        try { entries.AddRange(ReadLibrary(root)); }
                        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException) { }
                    }
                    _installs = entries; _checked = DateTime.UtcNow; _rootsKey = rootsKey;
                }
                var matches = _installs.Where(p => Normalize(p.Key) == Normalize(gameName) && Directory.Exists(p.Value)).Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                return matches.Count == 1 ? matches[0] : null;
            }
        }
        private static string Normalize(string value) => Regex.Replace(value ?? "", @"[^\p{L}\p{Nd}]", "").ToLowerInvariant();
        private static JToken Json(string file) => File.Exists(file) ? JToken.Parse(NativeSourceReader.Text(file)) : null;
        internal static List<KeyValuePair<string, string>> ReadLibrary(string root)
        {
            var result = new List<KeyValuePair<string, string>>();
            var apps = Json(Path.Combine(root, "apps.json"));
            if (apps != null)
            {
                var settings = Json(Path.Combine(root, "settings.json"));
                var basePath = (string)settings?["AppsPath"];
                if (string.IsNullOrWhiteSpace(basePath) || !Path.IsPathRooted(basePath)) basePath = Path.Combine(root, "Apps");
                var rows = apps as JArray ?? new JArray(new[] { "apps", "standard", "experimental", "custom" }.SelectMany(k => apps[k] as JArray ?? new JArray()));
                foreach (var app in rows.Take(5000).OfType<JObject>())
                {
                    var folder = (string)app["folderName"];
                    var install = (string)app["installPath"];
                    if (string.IsNullOrWhiteSpace(install) || !Path.IsPathRooted(install)) install = Segment(folder) ? Path.Combine(basePath, folder) : null;
                    if (install != null) result.Add(new KeyValuePair<string, string>((string)app["name"] ?? folder, install));
                }
            }
            var installs = Json(Path.Combine(root, "installs.json")) as JObject;
            if (installs != null)
            {
                var names = new Dictionary<string, string>();
                var library = Json(Path.Combine(root, "library.json")) as JArray;
                if (library != null)
                    foreach (var app in library.Take(5000).OfType<JObject>())
                        if (app["id"] != null) names[(string)app["id"]] = (string)app["overrides"]?["name"];
                var basePath = (string)Json(Path.Combine(root, "apps-folder.json"))?["path"];
                if (string.IsNullOrWhiteSpace(basePath) || !Path.IsPathRooted(basePath)) basePath = Path.Combine(root, "apps");
                foreach (var prop in installs.Properties().Take(5000).Where(p => p.Value is JObject))
                {
                    var folder = (string)prop.Value["folder"];
                    var group = ((string)prop.Value["group"] ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    var install = (string)prop.Value["dir"];
                    if (string.IsNullOrWhiteSpace(install) || !Path.IsPathRooted(install))
                        install = Segment(folder) && group.All(Segment) ? Path.Combine(new[] { basePath }.Concat(group).Concat(new[] { folder }).ToArray()) : null;
                    if (install != null) result.Add(new KeyValuePair<string, string>(names.TryGetValue(prop.Name, out var name) && !string.IsNullOrWhiteSpace(name) ? name : folder, install));
                }
            }
            return result;
        }
        private static bool Segment(string value) => !string.IsNullOrWhiteSpace(value) && value != "." && value != ".." && value.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0;
    }
}
