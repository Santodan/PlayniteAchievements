using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Local
{
    internal sealed class NativeKytySettings
    {
        internal string Root, GlobalUser = "";
        internal List<string> GameFolders = new List<string>();
        internal Dictionary<string, string> GameUsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string ActiveUser(string gameFolder) => GameUsers.TryGetValue(Path.GetFullPath(gameFolder).TrimEnd('\\', '/'), out var user) ? user : GlobalUser;
        internal static NativeKytySettings Read(string root, string settingsFile, bool defaults)
        {
            root = EmulatorRoot(root);
            var result = new NativeKytySettings { Root = root };
            var files = !string.IsNullOrWhiteSpace(settingsFile) ? new[] { settingsFile } :
                new[] { Path.Combine(root, "Kyty.ini"), defaults ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kyty", "Kyty.ini") : "" };
            var file = files.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(settingsFile) && file == null) throw new FileNotFoundException("Configured Kyty.ini not found: " + settingsFile);
            if (file == null) return result;
            if (new FileInfo(file).Length > 256 * 1024) throw new InvalidDataException("Kyty.ini is too large.");
            return Parse(root, NativeSourceReader.Text(file));
        }
        internal static string EmulatorRoot(string folder)
        {
            var root = (folder ?? "").TrimEnd('\\', '/');
            if (Path.GetFileName(root).Equals("_Trophies", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(root);
            var parent = Path.GetDirectoryName(root);
            if (parent != null && Path.GetFileName(parent).Equals("_Trophies", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(parent);
            return root;
        }
        internal static NativeKytySettings Parse(string root, string text)
        {
            var result = new NativeKytySettings { Root = root };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var section = "";
            foreach (var line in text.Split('\n'))
            {
                var clean = line.Trim(); if (clean.StartsWith(";") || clean.StartsWith("#")) continue;
                if (clean.StartsWith("[") && clean.EndsWith("]")) { section = clean.Substring(1, clean.Length - 2); continue; }
                var split = clean.IndexOf('=');
                if (split > 0) values[(section + "/" + clean.Substring(0, split).Trim()).Replace('\\', '/')] = clean.Substring(split + 1).Trim();
            }
            if (values.TryGetValue("GlobalConfiguration/user_id", out var global)) result.GlobalUser = User(global);
            if (values.TryGetValue("Launcher/game_dirs", out var dirs)) result.GameFolders.AddRange(List(dirs).Select(p => Resolve(root, p)));
            var games = new Dictionary<string, Dictionary<string, string>>();
            foreach (var value in values)
            {
                var match = Regex.Match(value.Key, @"^GameConfigurations/(\d+)/(game_path|basedir|user_id)$", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                if (!games.TryGetValue(match.Groups[1].Value, out var game)) games[match.Groups[1].Value] = game = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                game[match.Groups[2].Value] = value.Value;
            }
            foreach (var game in games.Values)
            {
                if (!game.TryGetValue("game_path", out var path) && !game.TryGetValue("basedir", out path)) continue;
                var first = List(path).FirstOrDefault(); if (string.IsNullOrWhiteSpace(first)) continue;
                var folder = Resolve(root, first); result.GameFolders.Add(folder);
                if (game.TryGetValue("user_id", out var user)) result.GameUsers[folder.TrimEnd('\\', '/')] = User(user);
            }
            result.GameFolders = result.GameFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }
        internal static string User(string raw)
        {
            if (!uint.TryParse(raw?.Trim().Trim('"'), out var user) || user > int.MaxValue) throw new InvalidDataException("Kyty user ID is invalid; choose a numeric user ID or fix Kyty.ini.");
            return user.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        private static string Resolve(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        internal static IEnumerable<string> List(string text)
        {
            var result = new List<string>(); var token = new StringBuilder(); var quoted = false;
            for (var i = 0; i < (text ?? "").Length; i++)
            {
                var ch = text[i];
                if (ch == '"') { quoted = !quoted; continue; }
                if (ch == '\\' && i + 2 < text.Length && text[i + 1] == 'x')
                {
                    var hex = Regex.Match(text.Substring(i + 2), "^[0-9a-fA-F]{1,4}");
                    if (hex.Success) { token.Append((char)Convert.ToInt32(hex.Value, 16)); i += 1 + hex.Length; continue; }
                }
                if (ch == '\\' && i + 1 < text.Length && (text[i + 1] == '\\' || text[i + 1] == '"' || text[i + 1] == ',')) { token.Append(text[++i]); continue; }
                if (ch == ',' && !quoted) { if (token.ToString().Trim().Length > 0) result.Add(token.ToString().Trim()); token.Clear(); }
                else token.Append(ch);
            }
            if (token.ToString().Trim().Length > 0) result.Add(token.ToString().Trim());
            return result;
        }
    }
}
