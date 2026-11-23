using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.Local
{
    // Recompiled Xbox 360 games known by their executable and where each keeps its unlock list,
    // ported from Achievement Watcher Next's x360Ports.js (layoutTarget). Data: NativeX360Ports.json.
    internal sealed class NativeX360Port
    {
        public string Name, Layout, App, UserFolder, Legacy, LocalFallback, AchFile, AppFolder, Mods;
        public string[] TitleIds = new string[0], Exes = new string[0], Portable, SaveSetting, ProfileFolders;
        public bool Fixed, Release, Profiles;
        public int Records;
        public Dictionary<string, string> IdAliases = new Dictionary<string, string>();
    }

    internal sealed class NativeX360Match
    {
        public NativeX360Port Port;
        public string TitleId, Format;
        public List<string> Saves = new List<string>();
    }

    internal static class NativeX360PortTable
    {
        internal const string ReBlueTitleId = "4D5307DF";
        private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private static readonly Lazy<List<NativeX360Port>> Ports = new Lazy<List<NativeX360Port>>(Load);

        internal static IReadOnlyList<NativeX360Port> All => Ports.Value;

        private static List<NativeX360Port> Load()
        {
            using (var stream = typeof(NativeX360PortTable).Assembly.GetManifestResourceStream("PlayniteAchievements.NativeX360Ports.json"))
            {
                if (stream == null) return new List<NativeX360Port>();
                using (var reader = new StreamReader(stream))
                {
                    var rows = JObject.Parse(reader.ReadToEnd())["ports"] as JArray ?? new JArray();
                    string[] List(JToken t) => (t as JArray)?.Select(v => (string)v).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
                    return rows.OfType<JObject>().Select(t => new NativeX360Port
                    {
                        Name = (string)t["name"], Layout = (string)t["layout"], App = (string)t["app"], UserFolder = (string)t["userFolder"],
                        Legacy = (string)t["legacy"], LocalFallback = (string)t["localFallback"], AchFile = (string)t["achFile"],
                        AppFolder = (string)t["appFolder"], Mods = (string)t["mods"],
                        TitleIds = List(t["titleIds"]) ?? new string[0], Exes = List(t["exes"]) ?? new string[0],
                        Portable = List(t["portable"]), SaveSetting = List(t["saveSetting"]), ProfileFolders = List(t["profileFolders"]),
                        Fixed = (bool?)t["fixed"] == true, Release = (bool?)t["release"] == true, Profiles = (bool?)t["profiles"] == true,
                        Records = (int?)t["records"] ?? 50,
                        IdAliases = (t["idAliases"] as JObject)?.Properties().ToDictionary(p => p.Name, p => p.Value.ToString()) ?? new Dictionary<string, string>()
                    }).Where(p => p.Exes.Length > 0 && p.TitleIds.Length > 0).ToList();
                }
            }
        }

        // The port an install folder holds, by its executables; null when none is known.
        internal static NativeX360Match Find(string install)
        {
            if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) return null;
            HashSet<string> exes;
            try { exes = new HashSet<string>(Directory.EnumerateFiles(install, "*.exe").Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            var candidates = All.Where(p => p.Exes.Any(exes.Contains)).ToList();
            if (candidates.Count == 0) return null;
            // Several rows can share an exe (Army of Two builds): keep every list they name, the
            // existing file decides; a title is reported only when the rows agree on it.
            var match = new NativeX360Match { Port = candidates[0] };
            foreach (var port in candidates)
            {
                if (port.Layout == "none") continue;
                foreach (var titleId in port.TitleIds)
                {
                    var exe = port.Exes.First(exes.Contains);
                    var saves = Targets(port, titleId, install, exe, out var format);
                    match.Format = match.Format ?? format;
                    match.Saves.AddRange(saves);
                }
            }
            var titles = candidates.SelectMany(p => p.TitleIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existingTitle = titles.FirstOrDefault(t => match.Saves.Any(s => File.Exists(s) && s.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));
            match.TitleId = existingTitle ?? (titles.Count == 1 ? titles[0] : null);
            match.Saves = match.Saves.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return match;
        }

        private static string TomlIn(string folder, string titleId) => Path.Combine(folder, "achievements", titleId + ".toml");

        private static IEnumerable<string> Subfolders(string folder)
        {
            try { return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).Take(64).ToList() : new List<string>(); }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private static string Beside(string install, string folder) => Path.GetFullPath(Path.Combine(install, (folder ?? "").Replace('/', Path.DirectorySeparatorChar)));

        private static string FirstLine(string file)
        {
            try { return File.Exists(file) && new FileInfo(file).Length <= 4096 ? (File.ReadLines(file).FirstOrDefault() ?? "").Trim().TrimStart('﻿') : ""; }
            catch (IOException) { return ""; }
        }

        // A TOML string value (`key = 'literal'` or `key = "basic"`), "" when unset.
        internal static string TomlString(string file, string key)
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 128 * 1024) return "";
                var m = Regex.Match(File.ReadAllText(file), @"(?m)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*(?:'([^'\r\n]*)'|""((?:[^""\\\r\n]|\\.)*)"")");
                if (!m.Success) return "";
                return (m.Groups[1].Success ? m.Groups[1].Value : Regex.Replace(m.Groups[2].Value, @"\\([""\\])", "$1")).Trim();
            }
            catch (IOException) { return ""; }
        }

        // The folder a port saves under (userRootFor).
        private static string UserRoot(NativeX360Port port, string install, string exe)
        {
            var userFolder = port.Release
                ? (Path.GetFileName(install).Equals("runtime", StringComparison.OrdinalIgnoreCase) ? "../userdata" : Directory.Exists(Path.Combine(install, "resources")) ? "resources/userdata" : "userdata")
                : port.UserFolder;
            if (port.Fixed) return Beside(install, userFolder);
            if (port.Portable != null && port.Portable.Length == 2 && File.Exists(Path.Combine(install, port.Portable[0]))) return Beside(install, port.Portable[1]);
            if (port.Layout == "user") return Beside(install, userFolder);
            if (port.SaveSetting != null && port.SaveSetting.Length == 2)
            {
                var value = Environment.ExpandEnvironmentVariables(TomlString(Path.Combine(install, port.SaveSetting[0]), port.SaveSetting[1]));
                if (value.Length > 0 && value.IndexOfAny(Path.GetInvalidPathChars()) < 0) return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(install, value));
            }
            if (port.Layout == "localappdata") return string.IsNullOrWhiteSpace(Local) || port.App == null ? null : Path.Combine(Local, port.App.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(Documents)) return null;
            return Path.Combine(Documents, port.App ?? Path.GetFileNameWithoutExtension(exe));
        }

        private static IEnumerable<string> Targets(NativeX360Port port, string titleId, string install, string exe, out string format)
        {
            format = "toml";
            var files = new List<string>();
            switch (port.Layout)
            {
                case "record":
                    var recordRoot = Path.GetFileName(install).Equals("program", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(install) : install;
                    files.Add(Path.Combine(recordRoot, "user", "saves", "achievements.toml"));
                    return files;
                case "profiles":
                    files.AddRange(Subfolders(Path.Combine(install, "profiles")).Select(p => TomlIn(p, titleId)));
                    return files;
                case "unleashed":
                    format = "unleashed";
                    var fileName = port.AchFile ?? "ACH-DATA";
                    files.Add(File.Exists(Path.Combine(install, "portable.txt"))
                        ? Path.Combine(install, "save", fileName)
                        : Path.Combine(Roaming, port.AppFolder ?? "UnleashedRecomp", "save", fileName));
                    return files;
                case "earned-ini":
                    format = "ini-earned";
                    files.Add(Path.Combine(install, "profile", "achievements.ini"));
                    files.AddRange(Subfolders(Path.Combine(install, "profiles")).Select(p => Path.Combine(p, "achievements.ini")));
                    return files;
                case "renut":
                    var renut = Path.Combine(install, "renut.cfg");
                    if (!File.Exists(renut) || new FileInfo(renut).Length > 128 * 1024) return files;
                    foreach (var line in File.ReadLines(renut))
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0 && line.Substring(0, eq).Trim() == "user_data_root")
                        {
                            var value = line.Substring(eq + 1).Trim().Trim('"');
                            if (Path.IsPathRooted(value) && value.IndexOfAny(Path.GetInvalidPathChars()) < 0) files.Add(TomlIn(value, titleId));
                        }
                    }
                    return files;
                case "saves-path":
                    var fallback = string.IsNullOrWhiteSpace(Documents) ? null : Path.Combine(Documents, port.App ?? Path.GetFileNameWithoutExtension(exe));
                    foreach (var root in new[] { install }.Concat(Subfolders(Path.Combine(install, "mods"))))
                    {
                        var marker = FirstLine(Path.Combine(root, "saves.path"));
                        var saveRoot = marker.Length > 0 && marker.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? Path.GetFullPath(Path.Combine(root, marker)) : fallback;
                        if (saveRoot != null) files.Add(TomlIn(saveRoot, titleId));
                    }
                    return files;
                case "reeot":
                    var roots = new List<string>();
                    try
                    {
                        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\reeot\Install", false))
                        {
                            var recorded = key?.GetValue("InstallRoot") as string;
                            if (!string.IsNullOrWhiteSpace(recorded) && Directory.Exists(recorded)) roots.Add(recorded);
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
                    roots.Add(install);
                    foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                        files.AddRange(Subfolders(Path.Combine(root, "profiles")).Select(p => TomlIn(p, titleId)));
                    return files;
                case "pinyon":
                    var named = Environment.GetEnvironmentVariable("PINYON_SHIFT_STATE_ROOT");
                    var build = Path.GetDirectoryName(install) ?? "";
                    var built = Path.GetFileName(build).Equals("build", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(build) ?? "").Equals("out", StringComparison.OrdinalIgnoreCase);
                    var derived = built ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(build)), ".local", "preview") : Path.Combine(install, "pinyon_shift_state");
                    foreach (var state in new[] { !string.IsNullOrWhiteSpace(named) && Path.IsPathRooted(named) ? named : null, derived }.Where(s => s != null))
                    {
                        files.Add(TomlIn(Path.Combine(state, "user"), titleId));
                        files.AddRange(Subfolders(state).Where(d => Path.GetFileName(d).StartsWith("user-", StringComparison.OrdinalIgnoreCase)).Select(d => TomlIn(d, titleId)));
                    }
                    return files;
                case "estacado":
                    var inGame = Path.Combine(install, "runtime_data", "content", "achievements", titleId + ".toml");
                    var profile = Environment.GetEnvironmentVariable("USERPROFILE");
                    if (!string.IsNullOrWhiteSpace(profile) && !File.Exists(Path.Combine(install, "portable.txt")))
                        files.Add(Path.Combine(profile, "Saved Games", "Estacado", "content", "achievements", titleId + ".toml"));
                    files.Add(inGame);
                    return files;
            }

            var userRoot = UserRoot(port, install, exe);
            if (userRoot == null) return files;
            if (port.Layout == "tsv")
            {
                format = "tsv";
                var dir = Path.Combine(userRoot, "Achievements", titleId);
                try { if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*.tsv").Take(32)); }
                catch (IOException) { }
                return files;
            }
            files.Add(TomlIn(userRoot, titleId));
            if (port.Profiles) files.AddRange(Subfolders(Path.Combine(userRoot, "profiles")).Select(p => TomlIn(p, titleId)));
            if (!string.IsNullOrWhiteSpace(port.Legacy) && !string.IsNullOrWhiteSpace(Documents)) files.Add(TomlIn(Path.Combine(Documents, port.Legacy), titleId));
            if (!string.IsNullOrWhiteSpace(port.LocalFallback) && !string.IsNullOrWhiteSpace(Local)) files.Add(TomlIn(Path.Combine(Local, port.LocalFallback.Replace('/', Path.DirectorySeparatorChar)), titleId));
            if (!string.IsNullOrWhiteSpace(port.Mods)) files.AddRange(Subfolders(Path.Combine(userRoot, port.Mods)).Select(m => TomlIn(Path.Combine(m, "userdata"), titleId)));
            foreach (var folder in port.ProfileFolders ?? new string[0])
                foreach (var root in new[] { Documents, Local }.Where(r => !string.IsNullOrWhiteSpace(r)))
                    files.AddRange(Subfolders(Path.Combine(root, folder)).Select(p => TomlIn(p, titleId)));
            return files;
        }

        // Unleashed/Marathon ACH-DATA: "ACH " header, version, XOR checksum, then packed records of
        // a u16 id and a signed 64-bit time_t; a half-written file throws instead of relocking.
        internal static Dictionary<string, DateTime?> ParseUnleashed(byte[] buffer, int records)
        {
            var size = 16 + records * 16;
            if (buffer == null || buffer.Length != size) throw new InvalidDataException("ACH-DATA has the wrong size (" + (buffer?.Length ?? 0) + " bytes, not " + size + ").");
            if (buffer[0] != 'A' || buffer[1] != 'C' || buffer[2] != 'H' || buffer[3] != ' ' || BitConverter.ToUInt32(buffer, 4) > 1) throw new InvalidDataException("ACH-DATA has an unknown header.");
            uint checksum = 0;
            for (var at = 16; at < buffer.Length; at++) checksum ^= buffer[at];
            if (checksum != BitConverter.ToUInt32(buffer, 8)) throw new InvalidDataException("ACH-DATA fails its checksum.");
            var unlocked = new Dictionary<string, DateTime?>();
            var ended = false;
            for (var at = 16; at < buffer.Length; at += 16)
            {
                var id = BitConverter.ToUInt16(buffer, at);
                if (id == 0) { ended = true; continue; }
                var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (ended || unlocked.ContainsKey(key)) throw new InvalidDataException("ACH-DATA has a misplaced or repeated record for " + id + ".");
                unlocked[key] = NativeSourceReader.Unix(BitConverter.ToInt64(buffer, at + 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return unlocked;
        }

        // Lost Odyssey's profile/achievements.ini: a "# Achievements earned: id=unix time" header,
        // then id=seconds lines; strict, since a torn list read as shorter would replay unlocks.
        internal static Dictionary<string, DateTime?> ParseEarnedIni(string text)
        {
            if (!Regex.IsMatch(text ?? "", @"(?im)^\s*#\s*Achievements earned:\s*id=unix time\s*$")) throw new InvalidDataException("Earned list has no header.");
            var unlocked = new Dictionary<string, DateTime?>();
            foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                if (line.Trim().Length == 0 || Regex.IsMatch(line, @"^\s*[#;]")) continue;
                var row = Regex.Match(line, @"^\s*(\d{1,5})\s*=\s*(\d{1,12})\s*$");
                if (!row.Success || !int.TryParse(row.Groups[1].Value, out var id) || id < 1 || id > 0xffff) throw new InvalidDataException("Earned list has an incomplete entry.");
                if (unlocked.ContainsKey(row.Groups[1].Value)) throw new InvalidDataException("Earned list has a repeated id " + id + ".");
                unlocked[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = NativeSourceReader.Unix(row.Groups[2].Value);
            }
            return unlocked;
        }

        // re:Blue's own achievements (ids from 0x10001), from its src/engine/achievements/achievements.cpp
        // (https://github.com/zolaware/reblue, BSD 3-Clause), as AWN's reblueBonus.js lists them.
        internal static IEnumerable<NativeAchievement> ReBlueBonus()
        {
            const string icons = "https://raw.githubusercontent.com/zolaware/reblue/v1.3.1/res/embed/achievements/";
            var rows = new[]
            {
                new object[] { 0x10001, "Where is the bathroom?", "Where does Zola sh**? (Dedicated to InfernoZotza)", 69 },
                new object[] { 0x10002, "Le WOooAH", "Use a Corporeal attack. (Dedicated to wolfaeterni)", 50 },
                new object[] { 0x10003, "Shuffle Baus", "Enter the shuffle dungeon. (Dedicated to baus.98)", 50 },
                new object[] { 0x10004, "The Fungus Collector", "Collect 27 Nothings. (Dedicated to Fungus, the king of Nothings)", 50 },
                new object[] { 0x10005, "The Cashman", "Obtain 666 gold. (Dedicated to griever666)", 50 },
                new object[] { 0x10006, "Cloaca Maxima", "Slay a Poo Snake and purge the Great Sewer. (Dedicated to rcold)", 420 },
                new object[] { 0x10007, "y u do dis", "Landshark... y u do dis (Dedicated to Graine25)", 50 },
                new object[] { 0x10008, "Please Stop Shaving Me", "Where did the bald people use to be? (Dedicated to crack - by rcold)", 7 },
            };
            return rows.Select(r => new NativeAchievement
            {
                Id = ((int)r[0]).ToString(System.Globalization.CultureInfo.InvariantCulture), Name = (string)r[1], Description = (string)r[2],
                Points = (int)r[3], Icon = icons + "0x" + ((int)r[0]).ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ".png"
            });
        }
    }
}
