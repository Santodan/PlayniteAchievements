using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.Local
{
    internal static class NativeLocationFiles
    {
        internal static IEnumerable<string> Definitions(NativeSourceResult result, NativeSourceReadContext context)
        {
            var source = result.Source;
            var patterns = source == "kytyps5" ? new[] { "trophy*.ucp" } : source == "ps3recomp" ? new[] { "TROPHY.TRP", "TROP.XML" } :
                source == "ps4recomp" ? new[] { "trophy*.trp", "TROP.XML", "trophies.tsv" } :
                source == "markerpatch" || source == "madnesspatch" ? new[] { "en.txt" } : source == "ladxhd" ? new[] { "achieve_eng.lng" } : new[] { "achievements-schema.json", "Achievements.json" };
            foreach (var root in NativeSourceLocations.Split(context.Locations.DefinitionFolders))
                foreach (var pattern in patterns)
                    foreach (var file in NativeSourceReader.Files(root, pattern, 5))
                        if (NativeLocationDiscovery.IsSingleGameSource(source) || Belongs(file, root, result, context)) yield return file;
        }

        internal static IEnumerable<string> Saves(NativeSourceResult result, NativeSourceConfiguration config, NativeSourceReadContext context)
        {
            if (result.Source == "kytyps5") return KytySaves(result, config, context);
            var patterns = SavePatterns(result.Source);
            var candidates = new List<string>();
            foreach (var root in NativeSourceLocations.Split(context.Locations.SaveFolders))
                foreach (var pattern in patterns)
                    foreach (var file in NativeSourceReader.Files(root, pattern, 4))
                        if (NativeLocationDiscovery.IsSingleGameSource(result.Source) || Belongs(file, root, result, context)) candidates.Add(file);
            return candidates;
        }
        private static string[] SavePatterns(string source)
        {
            switch (source)
            {
                case "ps3recomp": return new[] { "NPWR*.json" };
                case "ps4recomp": return new[] { "trophies.log", "last_run.log", "unlocked.txt", "trophy*.txt", "progress_user*.txt", "trophies.json", "trophies.dat", "CUSA*-user*.bin" };
                case "x360recomp": return new[] { "*.toml", "*.tsv", "Achievements.json" };
                case "markerpatch": return new[] { "settings.txt" };
                case "madnesspatch": return new[] { "Achievements.txt" };
                case "ladxhd": return new[] { "achievements" };
                case "dusklight": return new[] { "achievements.json" };
                case "ghostship": return new[] { "save_*.json" };
                case "dkrr": return new[] { "progress-v1.json" };
                case "smb1r": return new[] { "achievements.sav" };
                case "sms": return new[] { "achievements.txt" };
                case "smseclipse": return new[] { "achievements-eclipse.txt" };
                default: return new string[0];
            }
        }
        private static bool Belongs(string file, string root, NativeSourceResult result, NativeSourceReadContext context)
        {
            if (NativeLocationDiscovery.FileMatches(file, root, context.GameName, context.GameId)) return true;
            var identity = Identity(result, context);
            if (string.IsNullOrWhiteSpace(identity)) return false;
            var normalized = file.Replace('\\', '/');
            return normalized.Split('/').Any(p => p.Equals(identity, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(p).Equals(identity, StringComparison.OrdinalIgnoreCase) ||
                (result.Source == "ps3recomp" && p.StartsWith(identity + "_", StringComparison.OrdinalIgnoreCase)) ||
                (result.Source == "ps4recomp" && p.StartsWith(identity + "-user", StringComparison.OrdinalIgnoreCase)));
        }
        private static string Identity(NativeSourceResult result, NativeSourceReadContext context)
        {
            if (result.Source == "ps3recomp")
            {
                var match = Regex.Match(result.Schema ?? "", @"NPWR\d{5}", RegexOptions.IgnoreCase);
                if (match.Success) return match.Value;
            }
            if (result.Source == "kytyps5")
            {
                var param = Path.Combine(result.Root, "sce_sys", "param.json");
                if (File.Exists(param)) return (string)JObject.Parse(NativeSourceReader.Text(param))["titleId"];
            }
            return Regex.IsMatch(context.GameId ?? "", @"^(?:[A-Z]{4}\d{5}|[0-9a-fA-F]{8})$") ? context.GameId : null;
        }
        internal static IEnumerable<string> KytyGameFolders(NativeSourceLocations locations)
        {
            foreach (var root in NativeSourceLocations.Split(locations.EmulatorFolders))
                foreach (var folder in NativeKytySettings.Read(root, locations.SettingsFile, locations.UseDefaultLocations).GameFolders) yield return folder;
        }
        private static IEnumerable<string> KytySaves(NativeSourceResult result, NativeSourceConfiguration config, NativeSourceReadContext context)
        {
            var title = Identity(result, context);
            if (string.IsNullOrWhiteSpace(title)) yield break; // Never borrow another title's only save.
            var location = context.Locations;
            var roots = NativeSourceLocations.Split(location.EmulatorFolders).ToList();
            if (roots.Count == 0 && !string.IsNullOrWhiteSpace(location.SettingsFile)) roots.Add(Path.GetDirectoryName(location.SettingsFile));
            foreach (var root in roots)
            {
                var settings = NativeKytySettings.Read(root, location.SettingsFile, location.UseDefaultLocations);
                var user = !string.IsNullOrWhiteSpace(config.UserId) ? NativeKytySettings.User(config.UserId) : !string.IsNullOrWhiteSpace(location.UserId) ? NativeKytySettings.User(location.UserId) : settings.ActiveUser(result.Root);
                if (string.IsNullOrWhiteSpace(location.SaveFolders))
                {
                    foreach (var file in NativeSourceReader.Files(Path.Combine(settings.Root, "_Trophies", title), "trophies_*.json", 0))
                        if (KytyFile(file, result.Schema, user)) yield return file;
                }
                else
                    foreach (var saveRoot in NativeSourceLocations.Split(location.SaveFolders))
                        foreach (var file in NativeSourceReader.Files(saveRoot, "trophies_*.json", 3))
                            if (Path.GetFileName(Path.GetDirectoryName(file)).Equals(title, StringComparison.OrdinalIgnoreCase) && KytyFile(file, result.Schema, user)) yield return file;
            }
            // A configured trophy root may be separate from the emulator. It uses an explicit user
            // preference, otherwise ambiguity remains visible instead of merging user accounts.
            var defaultUser = !string.IsNullOrWhiteSpace(config.UserId) ? NativeKytySettings.User(config.UserId) :
                !string.IsNullOrWhiteSpace(location.UserId) ? NativeKytySettings.User(location.UserId) : "";
            if (roots.Count == 0)
                foreach (var root in NativeSourceLocations.Split(location.SaveFolders))
                    foreach (var file in NativeSourceReader.Files(root, "trophies_*.json", 3))
                        if (Path.GetFileName(Path.GetDirectoryName(file)).Equals(title, StringComparison.OrdinalIgnoreCase) && KytyFile(file, result.Schema, defaultUser)) yield return file;
        }
        internal static bool KytyFile(string file, string schema, string user)
        {
            var match = Regex.Match(Path.GetFileName(file), @"^trophies_(\d+)_(\d+)\.json$", RegexOptions.IgnoreCase);
            if (!match.Success || (!string.IsNullOrWhiteSpace(user) && NativeKytySettings.User(match.Groups[1].Value) != user)) return false;
            var slot = Regex.Match(Path.GetFileName(schema ?? ""), @"^trophy(\d+)\.ucp$", RegexOptions.IgnoreCase);
            return !slot.Success || uint.Parse(slot.Groups[1].Value) == uint.Parse(match.Groups[2].Value);
        }
    }
}
