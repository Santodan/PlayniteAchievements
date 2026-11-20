using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.Local
{
    // Independent readers: game files are only opened for reading, never executed or repaired.
    internal sealed class NativeAchievement
    {
        public string Id, Name, Description, Icon, TrophyType;
        public bool Hidden, Unlocked;
        public DateTime? Time;
        public int? Progress, Goal, Points;
    }

    internal sealed class NativeSourceResult
    {
        public string Source, Root, Save, Schema, Message, CollectionGame;
        public List<NativeAchievement> Achievements = new List<NativeAchievement>();
        public bool Success;
        public string Description => (NativeSourceCatalog.Find(Source)?.Name ?? Source) + Environment.NewLine +
            "Install: " + Root + Environment.NewLine + "Unlock file: " + (Save ?? "Not found") + Environment.NewLine +
            "Definitions: " + (Schema ?? "Not found") + Environment.NewLine + Message;
    }

    internal static class NativeSourceReader
    {
        internal const int MaxTextBytes = 4 * 1024 * 1024;
        private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private static readonly Lazy<JObject> PortDefinitions = new Lazy<JObject>(() =>
        {
            using (var stream = typeof(NativeSourceReader).Assembly.GetManifestResourceStream("PlayniteAchievements.NativePortDefinitions.json"))
            {
                if (stream == null) return new JObject();
                using (var reader = new StreamReader(stream)) return JObject.Parse(reader.ReadToEnd());
            }
        });

        internal static string Text(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new IOException("File not found: " + path);
            var info = new FileInfo(path);
            if (info.Length > MaxTextBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("File is too large or is a link: " + path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                // Also bound a file that grows between stat and read.
                var buffer = new char[(int)Math.Min(MaxTextBytes + 1L, info.Length + 1)];
                int count = 0, read;
                while (count < buffer.Length && (read = reader.Read(buffer, count, buffer.Length - count)) > 0) count += read;
                if (count > MaxTextBytes || reader.Peek() >= 0) throw new IOException("File grew while being read; retry after the game saves.");
                return new string(buffer, 0, count).TrimStart('\uFEFF');
            }
        }

        internal static IEnumerable<string> Files(string root, string pattern, int depth = 3)
        {
            if (!Directory.Exists(root)) yield break;
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            var visited = 0;
            while (queue.Count > 0 && visited++ < 400)
            {
                var item = queue.Dequeue();
                string[] files, dirs;
                try
                {
                    if ((File.GetAttributes(item.Key) & FileAttributes.ReparsePoint) != 0) continue;
                    files = Directory.EnumerateFiles(item.Key, pattern).Take(4096).ToArray();
                    dirs = item.Value < depth ? Directory.EnumerateDirectories(item.Key).Take(400).ToArray() : new string[0];
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var file in files) yield return file;
                foreach (var dir in dirs)
                    if (!Regex.IsMatch(Path.GetFileName(dir), @"^(\.git|node_modules|shadercache|cache|bin|obj)$", RegexOptions.IgnoreCase))
                        queue.Enqueue(new KeyValuePair<string, int>(dir, item.Value + 1));
            }
        }

        internal static string Detect(string root)
        {
            if (!Directory.Exists(root)) return null;
            if (File.Exists(Path.Combine(root, "MarkerPatch.ini"))) return "markerpatch";
            if (File.Exists(Path.Combine(root, "MadnessPatch.ini")) || File.Exists(Path.Combine(root, "Binaries", "Win32", "MadnessPatch.ini"))) return "madnesspatch";
            if (File.Exists(Path.Combine(root, "Data", "Languages", "achieve_eng.lng"))) return "ladxhd";
            var executables = Files(root, "*.exe", 0).Select(Path.GetFileNameWithoutExtension).ToArray();
            foreach (var entry in new[] { "dusklight:dusklight", "ghostship:Ghostship", "dkrr:DKR-R", "smb1r:SMB1R", "smb1r:SuperMarioBrosRemastered", "sms:SMS-Launcher", "sms:SMS Launcher" })
            {
                var pair = entry.Split(':');
                if (executables.Any(e => string.Equals(e, pair[1], StringComparison.OrdinalIgnoreCase) ||
                    (pair[0] == "sms" && Regex.IsMatch(e, @"^SMS[- ]Launcher", RegexOptions.IgnoreCase)))) return pair[0];
            }
            if (File.Exists(Path.Combine(root, "ps3recomp.toml")) || File.Exists(Path.Combine(root, "ps3recomp", "libs", "network", "sceNpTrophy.c"))) return "ps3recomp";
            if (Directory.Exists(Path.Combine(root, "sce_sys", "trophy2"))) return "kytyps5";
            if (Directory.Exists(Path.Combine(root, "sce_sys", "trophy")) &&
                new[] { "run.py", "trophies.log", "trophies.tsv", "unlocked.txt" }.Any(n => File.Exists(Path.Combine(root, n)))) return "ps4recomp";
            if (Files(Path.Combine(root, "achievements"), "*.toml", 1).Any() || File.Exists(Path.Combine(root, "SaveData", "Achievements.json"))) return "x360recomp";
            return null;
        }

        internal static NativeSourceResult Read(string install, NativeSourceConfiguration config, string cacheDirectory, bool readState = true, NativeSourceReadContext context = null)
        {
            config = config ?? new NativeSourceConfiguration();
            var root = string.IsNullOrWhiteSpace(config.InstallPath) ? install : config.InstallPath;
            var result = new NativeSourceResult { Root = root, Source = config.Source == "auto" ? Detect(root) : config.Source, CollectionGame = config.CollectionGame };
            if (string.IsNullOrWhiteSpace(result.Source)) { result.Message = "No supported native source detected. Choose a source and its install folder."; return result; }
            try
            {
                if (NativeSourceCatalog.Find(result.Source) == null) throw new InvalidDataException("Unknown native source.");
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose the game's install folder.");
                Resolve(result, config, context);
                ValidateIdentity(result);
                result.Achievements = LoadSchema(result, config, cacheDirectory);
                if (result.Achievements.Count == 0) throw new InvalidDataException("No definitions found. Choose a schema file; see the expected files above.");
                if (readState)
                {
                    if (string.IsNullOrWhiteSpace(result.Save) || !File.Exists(result.Save))
                        throw new FileNotFoundException("Unlock file is missing. Start the game with achievement support enabled, or select its save file.");
                    ReadState(result);
                }
                result.Success = true;
                result.Message = result.Achievements.Count + " definitions loaded" + (readState ? "; " + result.Achievements.Count(a => a.Unlocked) + " unlocked. Missing historical dates remain unknown." : ".");
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is FormatException || ex is OverflowException || ex is ArgumentException || ex is System.Xml.XmlException || ex is System.Security.Cryptography.CryptographicException)
            { result.Message = ex.Message + " Existing cached achievements are preserved on a failed read."; }
            return result;
        }

        private static string Unique(IEnumerable<string> files, string label, string profile = "")
        {
            var candidates = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => string.IsNullOrWhiteSpace(profile) || Path.GetFileNameWithoutExtension(f).Equals(profile, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(Path.GetDirectoryName(f)).Equals(profile, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            if (candidates.Count > 1) throw new InvalidDataException("Several " + label + " files found. Select the exact file in Local Overrides.\n" + string.Join("\n", candidates));
            return candidates.FirstOrDefault();
        }

        private static void Resolve(NativeSourceResult r, NativeSourceConfiguration c, NativeSourceReadContext context)
        {
            var root = r.Root;
            IEnumerable<string> saves = new string[0], schemas = new string[0];
            switch (r.Source)
            {
                case "markerpatch":
                    EnsurePatchEnabled(Path.Combine(root, "MarkerPatch.ini"));
                    schemas = new[] { Path.Combine(root, "achievements", "txt", "en.txt") };
                    saves = new[] { Path.Combine(Local, "EA Games", "Dead Space 2", "settings.txt") }; break;
                case "madnesspatch":
                    if (!File.Exists(Path.Combine(root, "MadnessPatch.ini"))) root = Path.Combine(root, "Binaries", "Win32");
                    r.Root = root;
                    EnsurePatchEnabled(Path.Combine(root, "MadnessPatch.ini"));
                    schemas = new[] { Path.Combine(root, "Achievements", "txt", "en.txt") };
                    saves = Files(Path.Combine(Documents, "My Games", "Alice Madness Returns", "AliceGame", "CheckPoint"), "Achievements.txt", 1); break;
                case "ladxhd":
                    schemas = new[] { Path.Combine(root, "Data", "Languages", "achieve_eng.lng") };
                    saves = new[] { File.Exists(Path.Combine(root, "portable.txt")) ? Path.Combine(root, "achievements") : Path.Combine(Local, "Zelda_LA", "achievements") }; break;
                case "dusklight":
                    var dataRoot = Path.Combine(Roaming, "TwilitRealm", "Dusklight");
                    var descriptor = new[] { Path.Combine(root, "data_location.json"), Path.Combine(dataRoot, "data_location.json") }.FirstOrDefault(File.Exists);
                    if (descriptor != null)
                    {
                        var location = JObject.Parse(Text(descriptor));
                        if ((string)location["mode"] == "portable") dataRoot = Path.Combine(root, "data");
                        else if ((string)location["mode"] == "custom" && Path.IsPathRooted((string)location["customPath"] ?? "")) dataRoot = (string)location["customPath"];
                    }
                    saves = new[] { Path.Combine(dataRoot, "achievements.json") }; break;
                case "ghostship": saves = Files(Path.Combine(root, "saves"), "save_*.json", 0); break;
                case "dkrr": saves = new[] { Path.Combine(File.Exists(Path.Combine(root, "portable.txt")) ? Path.Combine(root, "dkr-runtime-data") : Path.Combine(Roaming, "DKRPort"), "achievements", "progress-v1.json") }; break;
                case "smb1r": saves = new[] { Path.Combine(File.Exists(Path.Combine(root, "portable.txt")) ? Path.Combine(root, "config") : Path.Combine(Roaming, "SMB1R"), "achievements.sav") }; break;
                case "sms": case "smseclipse":
                    var sms = Directory.Exists(Path.Combine(root, "SMS-Launcher-Portable")) ? Path.Combine(root, "SMS-Launcher-Portable") : Path.Combine(Roaming, "sms-launcher");
                    saves = new[] { Path.Combine(sms, r.Source == "sms" ? "achievements.txt" : "achievements-eclipse.txt") }; break;
                case "ps3recomp":
                    saves = Files(Path.Combine(root, "gamedata", "trophies"), "NPWR*.json", 0);
                    schemas = Files(root, "TROPHY.TRP", 5); break;
                case "kytyps5":
                    schemas = Files(Path.Combine(root, "sce_sys", "trophy2"), "*.ucp", 0);
                    saves = Files(Path.Combine(root, "_Trophies"), "trophies_*.json", 2); break;
                case "ps4recomp":
                    schemas = Files(Path.Combine(root, "sce_sys", "trophy"), "*.trp", 0).Concat(Files(root, "TROP.XML", 4)).Concat(Files(root, "trophies.tsv", 1));
                    saves = new[] { "trophies.log", "unlocked.txt", "last_run.log" }.Select(n => Path.Combine(root, n)).Concat(Files(Path.Combine(root, "trophy"), "trophy*.txt", 0))
                        .Concat(Files(Path.Combine(root, "user"), "trophies.json", 3)).Concat(Files(Path.Combine(root, "user"), "trophies.dat", 3))
                        .Concat(Files(Path.Combine(root, "user"), "progress_user*.txt", 3)).Concat(Files(Path.Combine(root, "user", "trophies"), "CUSA*-user*.bin", 0)); break;
                case "x360recomp":
                    saves = Files(Path.Combine(root, "achievements"), "*.toml", 1)
                        .Concat(Files(Path.Combine(root, "Achievements"), "*.tsv", 2))
                        .Concat(new[] { Path.Combine(root, "SaveData", "Achievements.json") });
                    // Only a game-specific Documents folder is considered; never adopt another title's state.
                    saves = saves.Concat(Files(Path.Combine(Documents, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))), "*.toml", 2));
                    schemas = new[] { Path.Combine(root, "SaveData", "Achievements.json"), Path.Combine(root, "achievements-schema.json") }; break;
            }
            if (context != null && !string.IsNullOrWhiteSpace(context.Locations.DefinitionFolders))
                schemas = NativeLocationFiles.Definitions(r, context);
            r.Schema = string.IsNullOrWhiteSpace(c.SchemaPath) ? Unique(schemas, "definition/trophy set") : c.SchemaPath;
            if (context != null)
            {
                var locations = context.Locations;
                if (!locations.UseDefaultLocations)
                    saves = saves.Where(p => Path.GetFullPath(p).StartsWith(Path.GetFullPath(r.Root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(locations.SaveFolders)) saves = NativeLocationFiles.Saves(r, c, context);
                else if (r.Source == "kytyps5" && !string.IsNullOrWhiteSpace(locations.EmulatorFolders)) saves = NativeLocationFiles.Saves(r, c, context);
                if (r.Source == "kytyps5" && (!string.IsNullOrWhiteSpace(c.UserId) || !string.IsNullOrWhiteSpace(locations.UserId)))
                    saves = saves.Where(f => NativeLocationFiles.KytyFile(f, r.Schema, NativeKytySettings.User(!string.IsNullOrWhiteSpace(c.UserId) ? c.UserId : locations.UserId)));
            }
            // Overrides are authoritative. A missing explicit file must not silently select another profile.
            var profile = string.IsNullOrWhiteSpace(c.Profile) ? context?.Locations.Profile : c.Profile;
            r.Save = string.IsNullOrWhiteSpace(c.SavePath) ? Unique(saves, "unlock/profile", profile) : c.SavePath;
        }

        private static List<NativeAchievement> LoadSchema(NativeSourceResult r, NativeSourceConfiguration c, string cache)
        {
            if (!string.IsNullOrWhiteSpace(r.Schema))
            {
                var ext = Path.GetExtension(r.Schema).ToLowerInvariant();
                if (ext == ".trp" || ext == ".ucp" || ext == ".xml" || ext == ".sfm" || ext == ".esfm") return NativeTrophyReader.Read(r.Schema, cache);
                if (ext == ".json") return JsonSchema(JToken.Parse(Text(r.Schema)), Path.GetDirectoryName(r.Schema));
                if (ext == ".tsv") return TsvSchema(Text(r.Schema));
                if (r.Source == "markerpatch" || r.Source == "madnesspatch")
                {
                    var lines = Text(r.Schema).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                    while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines.Last())) lines.RemoveAt(lines.Count - 1);
                    if (lines.Count > 64 || lines.Count == 0 || lines.Any(l => !l.Contains("|"))) throw new InvalidDataException("Invalid patch achievement definitions.");
                    return lines.Select((l, i) => new NativeAchievement { Id = i.ToString(CultureInfo.InvariantCulture), Name = l.Split('|')[0], Description = l.Substring(l.IndexOf('|') + 1), Icon = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(r.Schema), "..", "img", i + ".png")) }).ToList();
                }
                if (r.Source == "ladxhd")
                {
                    var text = Text(r.Schema);
                    return Enumerable.Range(0, 110).Select(i => new NativeAchievement { Id = i.ToString(CultureInfo.InvariantCulture), Name = Lng(text, "Name", i), Description = Lng(text, "Desc", i), Icon = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(r.Schema), "..", "Achievements", "achievement" + i + ".png")) }).Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
                }
            }
            var data = PortDefinitions.Value[r.Source];
            if (data == null) return new List<NativeAchievement>();
            r.Schema = "Bundled port definitions (" + r.Source + ")";
            return JsonSchema(data, r.Root);
        }

        private static void EnsurePatchEnabled(string file)
        {
            if (File.Exists(file) && Regex.IsMatch(Text(file), @"(?im)^\s*AchievementSupport\s*=\s*(?:0|false)\s*(?:[;#].*)?$"))
                throw new InvalidDataException("AchievementSupport is disabled in " + file + ". Enable it in the patch before tracking.");
        }

        private static void ValidateIdentity(NativeSourceResult r)
        {
            if (string.IsNullOrWhiteSpace(r.Save) || string.IsNullOrWhiteSpace(r.Schema)) return;
            if (r.Source == "ps3recomp")
            {
                var expected = Regex.Match(r.Schema, @"NPWR\d{5}", RegexOptions.IgnoreCase).Value;
                var actual = Regex.Match(Path.GetFileName(r.Save), @"NPWR\d{5}", RegexOptions.IgnoreCase).Value;
                if (expected.Length > 0 && actual.Length > 0 && !expected.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The PS3 save and trophy definitions belong to different NPWR sets.");
            }
            if (r.Source == "kytyps5")
            {
                var slot = Regex.Match(Path.GetFileName(r.Schema), @"^trophy(\d+)\.ucp$", RegexOptions.IgnoreCase);
                var save = Regex.Match(Path.GetFileName(r.Save), @"^trophies_\d+_(\d+)\.json$", RegexOptions.IgnoreCase);
                if (slot.Success && save.Success && int.Parse(slot.Groups[1].Value) != int.Parse(save.Groups[1].Value)) throw new InvalidDataException("The Kyty save and UCP belong to different service labels.");
                var param = Path.Combine(r.Root, "sce_sys", "param.json");
                if (File.Exists(param))
                {
                    var title = (string)JObject.Parse(Text(param))["titleId"];
                    var savedTitle = Path.GetFileName(Path.GetDirectoryName(r.Save));
                    if (!string.IsNullOrWhiteSpace(title) && !title.Equals(savedTitle, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The Kyty save folder does not match this game's titleId.");
                }
            }
        }

        private static string Lng(string text, string kind, int i) => Regex.Match(text, @"(?m)^achieve" + kind + i + @" (.*)$").Groups[1].Value.TrimEnd('\r');

        internal static List<NativeAchievement> JsonSchema(JToken token, string root)
        {
            if (token is JObject) token = token["achievements"] ?? token["Achievements"] ?? token;
            var array = token as JArray;
            var rows = array != null ? array.Select((t, i) => new KeyValuePair<string, JToken>(i.ToString(CultureInfo.InvariantCulture), t)) :
                (token as JObject)?.Properties().Select(p => new KeyValuePair<string, JToken>(p.Name, p.Value)) ?? Enumerable.Empty<KeyValuePair<string, JToken>>();
            return rows.Take(4096).Where(p => p.Value is JObject).Select(p =>
            {
                var t = p.Value;
                var iconToken = t["icon"] ?? t["Icon"];
                var icon = iconToken?.Type == JTokenType.String ? (string)iconToken : null;
                if (!string.IsNullOrWhiteSpace(icon) && !Path.IsPathRooted(icon) && !Uri.IsWellFormedUriString(icon, UriKind.Absolute)) icon = Path.Combine(root, icon);
                if (!string.IsNullOrWhiteSpace(icon) && !File.Exists(icon) && !Uri.IsWellFormedUriString(icon, UriKind.Absolute)) icon = null;
                return new NativeAchievement { Id = (string)(t["name"] ?? t["id"] ?? t["Id"] ?? t["ID"]) ?? p.Key,
                    Name = (string)(t["displayName"] ?? t["title"] ?? t["Name"] ?? t["Title"]) ?? (string)t["name"],
                    Description = (string)(t["description"] ?? t["Description"]) ?? "", Icon = icon,
                    Goal = (int?)(t["goal"] ?? t["max_progress"]), Points = (int?)(t["gamerscore"] ?? t["Gamerscore"]),
                    Hidden = t["hidden"]?.ToString() == "true" || t["hidden"]?.ToString() == "1" };
            }).Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Name)).GroupBy(a => a.Id).Select(g => g.First()).ToList();
        }

        private static List<NativeAchievement> TsvSchema(string text)
        {
            var rows = new List<NativeAchievement>();
            foreach (var line in text.Split('\n'))
            {
                var cells = line.TrimEnd('\r').Split('\t');
                if (cells.Length >= 5 && int.TryParse(cells[0], out _)) rows.Add(new NativeAchievement { Id = cells[0], Name = cells[3], Description = cells[4], Hidden = cells[2] == "yes" || cells[2] == "1", TrophyType = cells[1] == "P" ? "platinum" : cells[1] == "G" ? "gold" : cells[1] == "S" ? "silver" : "bronze" });
            }
            return rows;
        }

        internal static void ReadState(NativeSourceResult r)
        {
            if (r.Source == "ps4recomp" && (Path.GetExtension(r.Save).Equals(".bin", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(r.Save).Equals(".dat", StringComparison.OrdinalIgnoreCase)))
            {
                if (new FileInfo(r.Save).Length != 16) throw new InvalidDataException("PS4 trophy flags must be exactly 16 bytes.");
                var bytes = File.ReadAllBytes(r.Save);
                if (bytes.Length != 16) throw new InvalidDataException("PS4 trophy flags must be exactly 16 bytes.");
                foreach (var a in r.Achievements)
                    if (int.TryParse(a.Id, out var id) && id < 128) a.Unlocked = (bytes[id / 8] & (1 << (id % 8))) != 0;
                return;
            }
            var text = Text(r.Save);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Unlock file is empty or still being written. Retry after the game saves.");
            var byId = r.Achievements.ToDictionary(a => a.Id, StringComparer.Ordinal);
            Action<string, bool, DateTime?, int?> set = (id, unlocked, time, progress) =>
            {
                if (!byId.TryGetValue(id, out var a)) return;
                a.Unlocked = unlocked;
                a.Time = unlocked ? time : null;
                a.Progress = progress.HasValue && a.Goal > 0 ? Math.Max(0, Math.Min(progress.Value, a.Goal.Value)) : (int?)null;
            };
            switch (r.Source)
            {
                case "markerpatch": case "madnesspatch":
                    ulong mask;
                    if (r.Source == "markerpatch")
                    {
                        var x = Regex.Match(text, @"(?im)^\s*Controls\.AcL\.X\s*=\s*(0x[0-9a-f]+|-?\d+)\s*(?:[;#].*)?$");
                        var y = Regex.Match(text, @"(?im)^\s*Controls\.AcL\.Y\s*=\s*(0x[0-9a-f]+|-?\d+)\s*(?:[;#].*)?$");
                        if (!x.Success || !y.Success) throw new InvalidDataException("Missing Controls.AcL.X/Y unlock flags.");
                        mask = Word(x.Groups[1].Value) | ((ulong)Word(y.Groups[1].Value) << 32);
                    }
                    else
                    {
                        var flag = Regex.Match(text, @"(?im)^\s*UnlockFlag\s*=\s*(\d+)\s*(?:[;#].*)?$");
                        if (!flag.Success || !ulong.TryParse(flag.Groups[1].Value, out mask)) throw new InvalidDataException("Missing or invalid UnlockFlag.");
                    }
                    foreach (var a in r.Achievements) set(a.Id, (mask & (1UL << int.Parse(a.Id, CultureInfo.InvariantCulture))) != 0, null, null);
                    break;
                case "ladxhd":
                    var flags = Regex.Matches(text, @"(?m)^achievement(\d+) (true|false)\s*$");
                    if (flags.Count == 0) throw new InvalidDataException("Not a Link's Awakening achievement save.");
                    foreach (Match m in flags) set(m.Groups[1].Value, m.Groups[2].Value == "true", null, null);
                    break;
                case "smb1r":
                    var bits = text.Trim();
                    if (!Regex.IsMatch(bits, "^[01]{1,512}$")) throw new InvalidDataException("Not an achievement bit string.");
                    for (int i = 0; i < Math.Min(bits.Length, r.Achievements.Count); i++) set(r.Achievements[i].Id, bits[i] == '1', null, null);
                    break;
                case "sms": case "smseclipse":
                    foreach (var line in text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                    {
                        var m = Regex.Match(line, @"^\s*(\d+)\s+(\d+)\s*$");
                        if (!m.Success) throw new InvalidDataException("Invalid SMS unlock line; retry after the game finishes saving.");
                        set(m.Groups[1].Value, true, Unix(m.Groups[2].Value), null);
                    }
                    break;
                case "ps3recomp":
                    var trophies = JObject.Parse(text)["trophies"] as JArray;
                    if (trophies == null) throw new InvalidDataException("Missing trophies array.");
                    foreach (var t in trophies) set((string)t["id"], true, Unix((string)t["timestamp"]), null);
                    break;
                case "kytyps5":
                    var state = JObject.Parse(text);
                    if (!(state["unlockedTrophies"] is JArray ids)) throw new InvalidDataException("Missing unlockedTrophies array.");
                    foreach (var id in ids)
                    {
                        DateTime? time = null;
                        if (long.TryParse((string)state["unlockedAt"]?[id.ToString()], out var ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks / 10)
                            time = new DateTime(ticks * 10, DateTimeKind.Utc);
                        set(id.ToString(), true, time, null);
                    }
                    break;
                case "dusklight": case "dkrr": case "ghostship":
                    var parsed = JObject.Parse(text);
                    if (r.Source == "ghostship")
                    {
                        var entries = parsed["shipSaveData"]?["achievementSaveData"]?["entries"] as JArray;
                        if (entries == null) throw new InvalidDataException("This slot has no achievement data.");
                        foreach (var e in entries)
                        {
                            var id = (string)e["id"];
                            if (id != null && byId.TryGetValue(id, out var a)) set(id, (int?)e["progress"] >= (a.Goal ?? 1), null, (int?)e["progress"]);
                        }
                    }
                    else
                    {
                        var entries = r.Source == "dkrr" ? parsed["entries"] as JObject : parsed;
                        if (entries == null || (r.Source == "dkrr" && (int?)parsed["schema"] != 1)) throw new InvalidDataException("Invalid port progress file.");
                        foreach (var p in entries.Properties())
                            if (p.Value is JObject) set(p.Name, (bool?)p.Value["unlocked"] == true, Unix((string)p.Value["when"]), (int?)p.Value["progress"]);
                    }
                    break;
                case "x360recomp": ReadXbox(text, Path.GetExtension(r.Save), set); break;
                case "ps4recomp": ReadPs4(text, set, r.CollectionGame, r.Root); break;
                default: throw new InvalidDataException("Unsupported source.");
            }
        }

        internal static DateTime? Unix(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 946684800 && seconds <= 4102444800)
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            return null;
        }

        private static uint Word(string value)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return uint.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var number = long.Parse(value, CultureInfo.InvariantCulture);
            if (number < int.MinValue || number > uint.MaxValue) throw new InvalidDataException("Patch unlock word is outside the 32-bit range.");
            return unchecked((uint)number);
        }

        private static DateTime? Filetime(string value)
        {
            if (!long.TryParse(value, out var ticks) || ticks <= 0) return null;
            try { var date = DateTime.FromFileTimeUtc(ticks); return date.Year >= 2000 && date.Year <= 2100 ? date : (DateTime?)null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static void ReadXbox(string text, string extension, Action<string, bool, DateTime?, int?> set)
        {
            if (extension.Equals(".toml", StringComparison.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(text, @"(?m)^\s*(\[unlocked|unlocked\s*=)")) throw new InvalidDataException("No ReXGlue unlock table.");
                foreach (Match m in Regex.Matches(text, @"(?ms)^\s*\[unlocked\.(\d+)\]\s*\r?\n(.*?)(?=^\s*\[|\z)"))
                    set(m.Groups[1].Value, true, Filetime(Regex.Match(m.Groups[2].Value, @"filetime\s*=\s*(\d+)").Groups[1].Value), null);
                var list = Regex.Match(text, @"(?m)^\s*unlocked\s*=\s*\[([\d,\s]*)\]");
                if (list.Success) foreach (Match id in Regex.Matches(list.Groups[1].Value, @"\d+")) set(id.Value, true, null, null);
                var flat = Regex.Match(text, @"(?ms)^\s*\[unlocked\]\s*\r?\n(.*?)(?=^\s*\[|\z)");
                if (flat.Success)
                    foreach (Match m in Regex.Matches(flat.Groups[1].Value, @"(?m)^\s*(\d+)\s*=\s*([0-9T:.+Z-]+)"))
                    {
                        var when = Unix(m.Groups[2].Value);
                        if (!when.HasValue && DateTimeOffset.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) when = parsed.UtcDateTime;
                        set(m.Groups[1].Value, true, when, null);
                    }
            }
            else if (extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(text.Split('\n')[0], @"^[^\t]+\t[0-9a-fA-F]{8}(?:\t[^\r\n]+)?\r?$")) throw new InvalidDataException("Invalid Xbox profile TSV header.");
                foreach (Match m in Regex.Matches(text, @"(?m)^(\d+)\t(\d+)\s*$")) set(m.Groups[1].Value, true, Filetime(m.Groups[2].Value), null);
            }
            else
            {
                var token = JToken.Parse(text);
                var rows = token as JArray ?? token["achievements"] as JArray ?? token["Achievements"] as JArray;
                if (rows == null) throw new InvalidDataException("Missing Xbox achievement list.");
                foreach (var t in rows) set((string)(t["id"] ?? t["Id"] ?? t["ID"]), (bool?)(t["unlocked"] ?? t["Unlocked"]) == true, Unix((string)(t["timestamp"] ?? t["earned_time"])), null);
            }
        }

        private static void ReadPs4(string text, Action<string, bool, DateTime?, int?> set, string collectionGame, string root)
        {
            if (text.TrimStart().StartsWith("{"))
            {
                var entries = JObject.Parse(text)["unlocked"] as JArray;
                if (entries == null) throw new InvalidDataException("Missing PS4 unlocked array.");
                foreach (var t in entries) set((string)t["id"], true, Unix((string)t["unix"]), null);
                return;
            }
            var snapshot = Regex.IsMatch(text, @"^[A-Z0-9]{2,8}-TROPHIES 1\r?\nnpcommid NPWR\d{5}_\d{2}");
            var recognized = snapshot || text.TrimStart().StartsWith("#");
            var shared = Regex.IsMatch((root ?? "") + "\n" + text, @"uncharted|prototype|big[23]-ps4_Shipping|prototype2_ps4f", RegexOptions.IgnoreCase);
            var context = "";
            // Accept literal IDs or the explicit trophy-unlock event, never arbitrary numbers in logs.
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (Regex.IsMatch(line, @"\b(?:Preparing |Switching to |The game started |Runtime: SystemService: load executable )|^Output: "))
                {
                    var next = CollectionContext(line, collectionGame);
                    if (next.Length > 0) context = next;
                }
                if (Regex.IsMatch(line, @"^\d{1,4}$")) { recognized = true; set(line, true, null, null); continue; }
                var pair = Regex.Match(line, @"^(\d{1,4})\s+(\d{9,12})$");
                if (pair.Success) { recognized = true; set(pair.Groups[1].Value, true, Unix(pair.Groups[2].Value), null); continue; }
                if (snapshot)
                {
                    var row = Regex.Match(line, @"^unlocked (\d{1,4}) (\d{1,13})$");
                    if (row.Success) set(row.Groups[1].Value, true, Unix(row.Groups[2].Value), null);
                    continue;
                }
                var match = Regex.Match(line, @"(?:Runtime|Trophies): trophy (\d{1,4}) (?:\(platinum\) )?unlocked\b", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    recognized = true;
                    if (shared || !string.IsNullOrWhiteSpace(collectionGame))
                    {
                        if (!Regex.IsMatch(collectionGame ?? "", "^(u[123]|p[12])$")) throw new InvalidDataException("This is a shared PS4 collection log. Select collection game u1/u2/u3 (Uncharted) or p1/p2 (Prototype), or choose a per-game trophy file.");
                        if (context.Length == 0) throw new InvalidDataException("The collection log has no game context before an unlock. Choose a per-game trophy file.");
                        if (context != collectionGame) continue;
                    }
                    set(match.Groups[1].Value, true, null, null);
                }
            }
            if (!recognized) throw new InvalidDataException("No supported PS4 trophy state recognized. Choose its trophy log, snapshot, JSON or flag file.");
        }
        private static string CollectionContext(string line, string selected)
        {
            if (Regex.IsMatch(line, @"prototype2_ps4f", RegexOptions.IgnoreCase)) return "p2";
            if (Regex.IsMatch(line, @"big2(?:-ps4_Shipping\.elf|[\\/].*boot-linked\.bin)", RegexOptions.IgnoreCase)) return "u2";
            if (Regex.IsMatch(line, @"big3(?:-ps4_Shipping\.elf|[\\/].*boot-linked\.bin)", RegexOptions.IgnoreCase)) return "u3";
            if (Regex.IsMatch(line, @"eboot\.bin|[\\/]eboot[\\/].*boot-linked\.bin|[\\/]out[\\/]boot-linked\.bin", RegexOptions.IgnoreCase)) return selected?.StartsWith("p", StringComparison.Ordinal) == true ? "p1" : "u1";
            return "";
        }
    }
}
