using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Providers.Local
{
    // Emulator saves keyed by something other than a Steam App ID, ported from Achievement Watcher
    // Next: GOG (UniverseLAN, Nemirtingas Galaxy) and Ubisoft (Goldberg Uplay R1/R2) folders named
    // after the store's product id, Goldberg SocialClub folders named after the game, and FINAL
    // FANTASY VII (2013). Each is read with the Steam schema of the same game. See AWN_SYNC.md.
    public partial class LocalSavesProvider
    {
        private const string Ff7SteamAppId = "39140";
        private static readonly string[] Ff7ConfigFiles = { "ff7input.cfg", "ff7sound.cfg", "ff7video.cfg" };
        private static readonly object AwnPlatformMapLock = new object();
        private static readonly Lazy<Dictionary<string, int>> UplaySteamTable = new Lazy<Dictionary<string, int>>(LoadUplaySteamTable);

        // FINAL FANTASY VII (2013) achievement.dat: an 8-byte bitfield, bit N (MSB first) per achievement.
        private static readonly Dictionary<int, string> Ff7Bits = new Dictionary<int, string>
        {
            [28] = "USE_1ST_LIMIT_CAITSITH", [29] = "USE_1ST_LIMIT_REDXIII", [30] = "USE_1ST_LIMIT_YUFFIE", [31] = "USE_1ST_LIMIT_TIFA",
            [32] = "USE_1ST_LIMIT_CID", [33] = "USE_1ST_LIMIT_AERITH", [34] = "USE_1ST_LIMIT_VINCENT", [35] = "USE_1ST_LIMIT_BARRET",
            [36] = "USE_1ST_LIMIT_CLOUD", [37] = "WON_1ST_BATTLE", [38] = "GET_GOLD_CHOCOBO", [39] = "FIGHT_IN_BATTLE_SQUARE",
            [40] = "MASTER_ALL_MATERIA", [41] = "GET_YUFFIE_IN_TEAM", [42] = "GET_VINCENT_IN_TEAM", [43] = "BEAT_EMERALD_WEAPON",
            [44] = "BEAT_RUBY_WEAPON", [45] = "BEAT_DIAMOND_WEAPON", [46] = "BEAT_ULTIMATE_WEAPON", [47] = "GET_MATERIA_BAHAMUT_ZERO",
            [48] = "GET_FOURTH_CAITSITH_LAST_LIMIT", [49] = "GET_FOURTH_REDXIII_LAST_LIMIT", [50] = "GET_FOURTH_YUFFIE_LAST_LIMIT",
            [51] = "GET_FOURTH_TIFA_LAST_LIMIT", [52] = "GET_FOURTH_CID_LAST_LIMIT", [53] = "GET_FOURTH_AERITH_LAST_LIMIT",
            [54] = "GET_FOURTH_VINCENT_LAST_LIMIT", [55] = "GET_FOURTH_BARRET_LAST_LIMIT", [56] = "GET_FOURTH_CLOUD_LAST_LIMIT",
            [57] = "GET_MATERIA_KOTR", [58] = "GET_LEVEL_99_WITH_A_CHAR", [59] = "GET_99999999_GILS", [60] = "LEVEL_UP_MATERIA_LVL5",
            [61] = "END_OF_GAME", [62] = "SHINRA_ANNIHILATED", [63] = "DEATH_OF_AERITH"
        };

        // The emulator a save folder belongs to when its id is not a Steam App ID, or null.
        internal static string GetAwnPlatformOfFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            if (ContainsPathSegment(folder, "NemirtingasGalaxyEmu") || ContainsPathSegment(folder, "UniverseLAN")) return "gog";
            if (ContainsPathSegment(folder, "Goldberg UplayEmu Saves") || ContainsPathSegment(folder, "R1 UplayEmu Saves")) return "uplay";
            return null;
        }

        // Legacy GOG emulator saves list only unlocked achievements, with no flag (emuSave.js).
        private static bool IsListOnlyUnlockedSave(string path) => ContainsPathSegment(path, "NemirtingasGalaxyEmu");

        internal static bool IsFf7Folder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
            if (!Ff7ConfigFiles.All(name => File.Exists(Path.Combine(folder, name)))) return false;
            var declared = ReadSteamAppIdFile(Path.Combine(folder, "steam_appid.txt"));
            if (!string.IsNullOrWhiteSpace(declared) && declared != Ff7SteamAppId) return false;
            return declared == Ff7SteamAppId || Regex.IsMatch(Path.GetFileName(folder.TrimEnd('\\', '/')), @"^final\s*fantasy\s*(vii|7)$", RegexOptions.IgnoreCase) ||
                SafeFiles(folder).Any(f => Regex.IsMatch(Path.GetFileName(f), @"^save\d*\.ff7$", RegexOptions.IgnoreCase));
        }

        private static IEnumerable<string> SafeFiles(string folder)
        {
            try { return Directory.EnumerateFiles(folder).Take(512).ToList(); }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        internal static Dictionary<string, LocalEntry> ParseFf7AchievementState(byte[] buffer)
        {
            if (buffer == null || buffer.Length != 8) throw new InvalidDataException("FINAL FANTASY VII achievement.dat must be 8 bytes.");
            var result = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Ff7Bits)
            {
                result[pair.Value] = new LocalEntry { earned = (buffer[pair.Key >> 3] & (1 << (7 - (pair.Key % 8)))) != 0 };
            }

            return result;
        }

        private static string NormalizeAwnGameName(string name)
        {
            var value = Regex.Replace((name ?? string.Empty).ToLowerInvariant().Replace("™", "").Replace("®", "").Replace("©", ""), "[^a-z0-9]+", " ");
            return Regex.Replace(value, @"\s+", " ").Trim();
        }

        // Save folders found by the game's name (Goldberg SocialClub) or by a fixed layout (FF7).
        private IEnumerable<string> GetAwnNameKeyedSaveFolders(Game game, string appId)
        {
            var results = new List<string>();
            if (game == null) return results;
            var excluded = GetExcludedLocalPathEntries(game).ToList();
            var gameName = NormalizeAwnGameName(game.Name);
            var installName = NormalizeAwnGameName(Path.GetFileName((game.InstallDirectory ?? string.Empty).TrimEnd('\\', '/')));

            var socialClub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Goldberg SocialClub Emu Saves");
            if (gameName.Length > 0 && Directory.Exists(socialClub) && !IsPathExcludedFromLocalScan(socialClub, excluded))
            {
                try
                {
                    foreach (var gameFolder in Directory.EnumerateDirectories(socialClub))
                    {
                        var folderName = NormalizeAwnGameName(Path.GetFileName(gameFolder));
                        if (folderName.Length == 0 || (folderName != gameName && folderName != installName) || IsPathExcludedFromLocalScan(gameFolder, excluded)) continue;
                        // <GameName>\<hex profile>\... : the first folders below that hold a save.
                        var pending = new Queue<KeyValuePair<string, int>>();
                        pending.Enqueue(new KeyValuePair<string, int>(gameFolder, 0));
                        while (pending.Count > 0 && results.Count < 16)
                        {
                            var item = pending.Dequeue();
                            if (HasLocalAchievementFiles(item.Key)) results.Add(item.Key);
                            if (item.Value >= 4) continue;
                            foreach (var child in Directory.EnumerateDirectories(item.Key).Take(64)) pending.Enqueue(new KeyValuePair<string, int>(child, item.Value + 1));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log($"AWN SOCIALCLUB SCAN ERROR: {ex.Message}");
                }
            }

            if (appId == Ff7SteamAppId || Regex.IsMatch(gameName, @"^final fantasy (vii|7)\b"))
            {
                var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                foreach (var folder in new[] { string.IsNullOrWhiteSpace(documents) ? null : Path.Combine(documents, "FINAL FANTASY VII"),
                    PlayniteAchievements.Common.PathExpansion.ExpandGamePath(_api, game, game.InstallDirectory)?.Trim() })
                {
                    if (!string.IsNullOrWhiteSpace(folder) && !IsPathExcludedFromLocalScan(folder, excluded) && IsFf7Folder(folder)) results.Add(folder);
                }
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private bool HasAwnNameKeyedSave(Game game) => GetAwnNameKeyedSaveFolders(game, null).Any();

        // The Steam App ID whose schema a GOG/Ubisoft emulator save is read with: the shipped Ubisoft
        // table (AWN uplay-steam.json), gamesdb.gog.com for GOG (cached), then the store search by name.
        private async Task<int> ResolveAwnPlatformSteamAppIdAsync(Game game, int platformId, string folder, bool allowNetwork)
        {
            var platform = GetAwnPlatformOfFolder(folder);
            if (platform == null || platformId <= 0) return 0;
            var id = platformId.ToString(CultureInfo.InvariantCulture);
            if (platform == "uplay" && UplaySteamTable.Value.TryGetValue(id, out var fromTable))
            {
                Log($"AWN PLATFORM MAP: uplay={id} steam={fromTable} source=table");
                return fromTable;
            }

            if (platform == "gog")
            {
                var mapped = await ResolveGogSteamAppIdAsync(id, allowNetwork).ConfigureAwait(false);
                if (mapped > 0)
                {
                    Log($"AWN PLATFORM MAP: gog={id} steam={mapped} source=gamesdb");
                    return mapped;
                }
            }

            var byName = game == null || string.IsNullOrWhiteSpace(game.Name) ? 0 : await ResolveLocalSchemaAppIdAsync(game, platformId, allowNetwork).ConfigureAwait(false);
            if (byName > 0) Log($"AWN PLATFORM MAP: {platform}={id} steam={byName} source=name");
            return byName;
        }

        private static Dictionary<string, int> LoadUplaySteamTable()
        {
            var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var stream = typeof(LocalSavesProvider).Assembly.GetManifestResourceStream("PlayniteAchievements.AwnUplaySteam.json"))
            {
                if (stream == null) return table;
                using (var reader = new StreamReader(stream))
                {
                    foreach (var row in (JToken.Parse(reader.ReadToEnd()) as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var uplayId = ((string)row["uplay_id"])?.Trim();
                        if (!string.IsNullOrWhiteSpace(uplayId) && int.TryParse(Convert.ToString(row["steam_appid"], CultureInfo.InvariantCulture), out var steamAppId) && steamAppId > 0 && !table.ContainsKey(uplayId))
                            table[uplayId] = steamAppId;
                    }
                }
            }

            return table;
        }

        private async Task<int> ResolveGogSteamAppIdAsync(string gogId, bool allowNetwork)
        {
            var dataRoot = PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath();
            var cacheFile = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "local_schema_cache", "gog-steam.json");
            JObject cache = null;
            lock (AwnPlatformMapLock)
            {
                try { cache = cacheFile != null && File.Exists(cacheFile) ? JObject.Parse(File.ReadAllText(cacheFile)) : new JObject(); }
                catch (Exception ex) when (ex is IOException || ex is JsonException) { cache = new JObject(); }
            }

            if (cache[gogId] != null && int.TryParse(cache[gogId].ToString(), out var cached)) return cached;
            if (!allowNetwork || !await CanUseLocalSchemaNetworkAsync().ConfigureAwait(false)) return 0;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024 })
                {
                    var info = JObject.Parse(await client.GetStringAsync("https://gamesdb.gog.com/platforms/gog/external_releases/" + Uri.EscapeDataString(gogId)).ConfigureAwait(false));
                    var steam = (info["game"]?["releases"] as JArray ?? new JArray())
                        .FirstOrDefault(r => string.Equals((string)r["platform_id"], "steam", StringComparison.OrdinalIgnoreCase));
                    if (!int.TryParse((string)steam?["external_id"], out var steamAppId) || steamAppId <= 0) return 0;
                    if (cacheFile != null)
                    {
                        lock (AwnPlatformMapLock)
                        {
                            cache[gogId] = steamAppId;
                            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                            File.WriteAllText(cacheFile, cache.ToString());
                        }
                    }

                    return steamAppId;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Log($"AWN GOG MAP ERROR: gog={gogId} msg={ex.Message}");
                return 0;
            }
        }

        // Ubisoft emulators save under bare objective ids (or a prefix + id); many Ubisoft Steam
        // ports end each api name in that id (uplayR2Live.js objectiveResolver).
        private static IReadOnlyDictionary<string, LocalEntry> RemapAwnObjectiveIdEntries(
            IReadOnlyDictionary<string, LocalEntry> localEntries,
            IReadOnlyList<SchemaAchievement> schemaAchievements)
        {
            if (localEntries == null || schemaAchievements == null || localEntries.Count == 0) return localEntries;
            var names = new HashSet<string>(schemaAchievements.Select(a => a?.Name).Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            var numericKeys = localEntries.Keys.Where(k => Regex.IsMatch(k ?? string.Empty, @"^\d{1,9}$") && !names.Contains(k)).ToList();
            if (numericKeys.Count == 0 || numericKeys.Count < localEntries.Count / 2) return localEntries;

            var byId = new Dictionary<int, string>();
            var collisions = new HashSet<int>();
            foreach (var name in names)
            {
                var digits = Regex.Match(name, @"(\d+)$");
                if (!digits.Success || !int.TryParse(digits.Groups[1].Value, out var id)) continue;
                if (byId.ContainsKey(id)) collisions.Add(id);
                else byId[id] = name;
            }

            if (byId.Count == 0) return localEntries;
            var remapped = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in localEntries)
            {
                var key = pair.Key;
                if (numericKeys.Contains(key) && int.TryParse(key, out var id) && !collisions.Contains(id) && byId.TryGetValue(id, out var name)) key = name;
                if (!remapped.TryGetValue(key, out var existing) || (pair.Value.earned && !existing.earned)) remapped[key] = pair.Value;
            }

            return remapped;
        }
    }
}
