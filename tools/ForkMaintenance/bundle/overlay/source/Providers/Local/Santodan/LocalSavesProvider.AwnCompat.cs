using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Providers.Local
{
    // Local save compatibility ported from Achievement Watcher Next (AWN). The AWN commit this
    // matches and the source-to-fork map are kept in AWN_SYNC.md beside this file.
    public partial class LocalSavesProvider
    {
        // SmartSteamEmu stores CRC32(api name) instead of the name; these keys are resolved
        // against the schema in CorrelateLocalEntriesWithSchema and dropped when no schema exists.
        internal const string SseCrcKeyPrefix = "__sse_crc:";

        private static readonly Regex RldBlobPattern = new Regex("^[0-9a-fA-F]{10}$", RegexOptions.Compiled);
        private static readonly Regex Razor1911LinePattern = new Regex(@"^(\S+)\s+([01])\s+(\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex SceneSaveNotePattern = new Regex(@"game data is stored at[ \t]+(\S.*?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex InlineProgressPattern = new Regex(@"(?:progress|value)\s*=\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> AwnNonAchievementSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SteamAchievements", "Steam64", "Steam", "UserStats", "Stats", "State", "Time",
            "Achievements", "AchievementsUnlockTimes", "ACHIEVE_DATA", "STATS", "Game", "Settings", "GameSettings"
        };

        private static readonly string[] AwnPortableSceneTrees = { @"Steam\RUNE", @"Steam\CODEX", @"Steam\CPY", "RUNE", "CODEX" };

        private static readonly HashSet<string> AwnInstallConfigFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ALI213.ini", "valve.ini", "SteamConfig.ini", "hlm.ini", "ds.ini", "steam_api.ini",
            "steam_emu.ini", "cpy.ini", "tenoke.ini"
        };

        private static readonly string[] GreenLumaRegistryKeys = { "GLR", "GL2020", "GL2024", "GL2025" };

        // Save roots AWN reads that the original Local root list lacks (emuSaveRoots.js).
        internal static IEnumerable<string> GetAwnCompatSaveRoots()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(roaming))
            {
                yield return Path.Combine(roaming, "Steam", "RUNE");
                yield return Path.Combine(roaming, "Steam", "RLD!");
                yield return Path.Combine(roaming, ".1911");
                // Saves keyed by a GOG or Ubisoft product id (see LocalSavesProvider.AwnPlatforms.cs).
                yield return Path.Combine(roaming, "NemirtingasGalaxyEmu");
                yield return Path.Combine(roaming, "Goldberg UplayEmu Saves");
                yield return Path.Combine(roaming, "R1 UplayEmu Saves");
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "UniverseLAN");
                // anadius' LSX emulator exports achievement_watcher\<steam appid>chievements.ini.
                yield return Path.Combine(localAppData, "anadius", "LSX emu", "achievement_watcher");
            }

            if (!string.IsNullOrWhiteSpace(publicFolder))
            {
                yield return Path.Combine(publicFolder, "Documents", "EMPRESS");
                yield return Path.Combine(publicFolder, "Documents", "Steam", "CPY");
                yield return Path.Combine(publicFolder, "Documents", "Steam", "RLD!");
            }

            if (!string.IsNullOrWhiteSpace(documents))
            {
                yield return Path.Combine(documents, "DARKSiDERS");
            }

            // GBE reads GseSavePath before its own configs, moving every game's saves there.
            var gseSavePath = Environment.GetEnvironmentVariable("GseSavePath")?.Trim();
            if (!string.IsNullOrWhiteSpace(gseSavePath) && Path.IsPathRooted(gseSavePath))
            {
                yield return gseSavePath;
            }
        }

        // Every save file name the Local provider reads, for the folder importer.
        internal static IEnumerable<string> GetRecognizedLocalSaveFileNames()
        {
            return LocalAchievementIniFileNames.Concat(new[] { "achievements.json" });
        }

        private sealed class AwnStatLink
        {
            public string StatName;
            public int Maximum;
        }

        // CODEX/RUNE ([UserStats]) and OnlineFix ([Stats]) keep counters in a Stats.ini beside
        // achievements.ini; the schema's progress block (operand1) says which counter drives which
        // achievement (steam.js mergeSiblingStats + statProgress.js).
        private Dictionary<string, LocalEntry> ApplyAwnStatProgress(Dictionary<string, LocalEntry> entries, IEnumerable<string> iniPaths, int appId, Playnite.SDK.Models.Game game)
        {
            if (entries == null)
            {
                return entries;
            }

            var stats = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in (iniPaths ?? Enumerable.Empty<string>())
                .Where(path => string.Equals(Path.GetFileName(path), "achievements.ini", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var statsPath = Path.Combine(directory ?? string.Empty, "Stats.ini");
                if (!File.Exists(statsPath))
                {
                    continue;
                }

                try
                {
                    var sections = ReadAwnIniSections(File.ReadLines(statsPath).Take(20000));
                    foreach (var sectionName in new[] { "UserStats", "Stats" })
                    {
                        if (!sections.TryGetValue(sectionName, out var values))
                        {
                            continue;
                        }

                        foreach (var pair in values)
                        {
                            if (double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number) && !double.IsInfinity(number))
                            {
                                stats[pair.Key] = number;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log($"AWN STATS READ ERROR: file={statsPath} msg={ex.Message}");
                }
            }

            if (stats.Count == 0)
            {
                return entries;
            }

            var links = GetAwnStatLinks(appId, game);
            var applied = 0;
            foreach (var link in links)
            {
                if (!stats.TryGetValue(link.Value.StatName, out var raw))
                {
                    continue;
                }

                // A stat keeps counting past the goal: cap it there.
                var value = ClampToInt((long)Math.Round(link.Value.Maximum > 0 ? Math.Min(raw, link.Value.Maximum) : raw));
                entries.TryGetValue(link.Key, out var entry);
                if (!entry.progress.HasValue || value > entry.progress.Value)
                {
                    entry.progress = value;
                }

                if (!(entry.max_progress > 0) && link.Value.Maximum > 0)
                {
                    entry.max_progress = link.Value.Maximum;
                }

                if (!entry.earned && link.Value.Maximum > 0 && value >= link.Value.Maximum)
                {
                    entry.earned = true;
                }

                entries[link.Key] = entry;
                applied++;
            }

            if (applied > 0)
            {
                Log($"AWN STAT PROGRESS: appId={appId} stats={stats.Count} links={links.Count} applied={applied}");
            }

            return entries;
        }

        // achievement api name -> driving stat, from Steam's appcache schema and the install's
        // Goldberg achievements.json (progress.value.operand1 / max_val).
        private Dictionary<string, AwnStatLink> GetAwnStatLinks(int appId, Playnite.SDK.Models.Game game)
        {
            var links = new Dictionary<string, AwnStatLink>(StringComparer.OrdinalIgnoreCase);
            if (appId > 0)
            {
                foreach (var schemaPath in GetSteamAppCacheSchemaFilePaths(appId))
                {
                    if (!PlayniteAchievements.Providers.Steam.Local.SteamBinaryKeyValuesReader.TryRead(schemaPath, out var root))
                    {
                        continue;
                    }

                    foreach (var bit in EnumerateKvNodes(root).Where(node => node.Child("progress") != null && node.Child("name") != null))
                    {
                        var value = bit.Child("progress").Child("value");
                        var apiName = bit.Child("name").StringValue?.Trim();
                        var statName = value?.Child("operand1")?.StringValue?.Trim();
                        if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(statName) || links.ContainsKey(apiName))
                        {
                            continue;
                        }

                        var maxNode = bit.Child("progress").Child("max_val");
                        int.TryParse(maxNode?.StringValue ?? maxNode?.IntegerValue?.ToString(CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum);
                        links[apiName] = new AwnStatLink { StatName = statName, Maximum = maximum };
                    }
                }
            }

            var installDirectory = game == null ? null : PlayniteAchievements.Common.PathExpansion.ExpandGamePath(_api, game, game.InstallDirectory)?.Trim();
            if (!string.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory))
            {
                foreach (var relative in new[] { @"steam_settings\achievements.json", @"steam_settings\settings\achievements.json" })
                {
                    var path = Path.Combine(installDirectory, relative);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        foreach (var item in (JToken.Parse(File.ReadAllText(path)) as JArray ?? new JArray()).OfType<JObject>())
                        {
                            var apiName = (string)item["name"];
                            var progress = item["progress"] as JObject;
                            var statName = (string)progress?["value"]?["operand1"];
                            if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(statName) || links.ContainsKey(apiName))
                            {
                                continue;
                            }

                            int.TryParse(Convert.ToString(progress["max_val"] ?? progress["max"], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum);
                            links[apiName] = new AwnStatLink { StatName = statName, Maximum = maximum };
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                    {
                        Log($"AWN STAT LINKS ERROR: file={path} msg={ex.Message}");
                    }
                }
            }

            return links;
        }

        private static IEnumerable<PlayniteAchievements.Providers.Steam.Local.SteamKvNode> EnumerateKvNodes(PlayniteAchievements.Providers.Steam.Local.SteamKvNode root)
        {
            var pending = new Stack<PlayniteAchievements.Providers.Steam.Local.SteamKvNode>();
            if (root != null)
            {
                pending.Push(root);
            }

            while (pending.Count > 0)
            {
                var node = pending.Pop();
                yield return node;
                foreach (var child in node.Children)
                {
                    pending.Push(child);
                }
            }
        }

        // File names too generic to mean a save outside their own emulator root.
        internal static bool IsAwnSaveFileAccepted(string filePath)
        {
            var name = Path.GetFileName(filePath);
            if (string.Equals(name, "achievement", StringComparison.OrdinalIgnoreCase))
            {
                return ContainsPathSegment(filePath, ".1911");
            }

            if (string.Equals(name, "stats.bin", StringComparison.OrdinalIgnoreCase))
            {
                return ContainsPathSegment(filePath, "SmartSteamEmu");
            }

            if (string.Equals(name, "achievement.dat", StringComparison.OrdinalIgnoreCase))
            {
                // Only FINAL FANTASY VII (2013): the name is far too generic otherwise.
                return IsFf7Folder(Path.GetDirectoryName(filePath));
            }

            if (string.Equals(name, "stats.ini", StringComparison.OrdinalIgnoreCase))
            {
                // Only the 3DM [State]/[Time] layout is a save; CODEX/OnlineFix stats.ini only holds counters.
                try
                {
                    var sections = ReadAwnIniSections(File.ReadLines(filePath).Take(4000));
                    return sections.ContainsKey("State") && sections.ContainsKey("Time");
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsPathSegment(string filePath, string segment)
        {
            return (filePath ?? string.Empty)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
        }

        internal static Dictionary<string, LocalEntry> ParseLocalSaveFileWithAwnFormats(string filePath)
        {
            var name = Path.GetFileName(filePath);
            if (string.Equals(name, "stats.bin", StringComparison.OrdinalIgnoreCase))
            {
                return ParseSmartSteamEmuStats(File.ReadAllBytes(filePath));
            }

            if (string.Equals(name, "achievement.dat", StringComparison.OrdinalIgnoreCase))
            {
                return ParseFf7AchievementState(File.ReadAllBytes(filePath));
            }

            var lines = File.ReadAllLines(filePath);
            if (string.Equals(name, "achievement", StringComparison.OrdinalIgnoreCase))
            {
                return ParseRazor1911Save(lines);
            }

            if (string.Equals(name, "stats.ini", StringComparison.OrdinalIgnoreCase))
            {
                var threeDm = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
                ApplyAwnIniShapes(lines, threeDm);
                return threeDm;
            }

            var entries = ParseIniEntries(lines);
            ApplyAwnIniShapes(lines, entries);
            return entries;
        }

        // RAZOR1911: "<apiname> <0|1> <epoch seconds>" per line.
        internal static Dictionary<string, LocalEntry> ParseRazor1911Save(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines ?? Array.Empty<string>())
            {
                var match = Razor1911LinePattern.Match((line ?? string.Empty).Trim());
                if (!match.Success)
                {
                    continue;
                }

                long.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var time);
                result[match.Groups[1].Value] = new LocalEntry { earned = match.Groups[2].Value == "1", earned_time = time };
            }

            return result;
        }

        // SmartSteamEmu stats.bin: int32 count, then 24-byte records (CRC32 of api name, ..., unlock
        // time at 8, value at 20). A value above 1 is a stat, not an achievement.
        internal static Dictionary<string, LocalEntry> ParseSmartSteamEmuStats(byte[] buffer)
        {
            var result = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            if (buffer == null || buffer.Length < 4)
            {
                return result;
            }

            var count = BitConverter.ToInt32(buffer, 0);
            const int recordLength = 24;
            var records = (buffer.Length - 4 + recordLength - 1) / recordLength;
            if (count != records)
            {
                throw new InvalidDataException("Unexpected SmartSteamEmu stats count.");
            }

            for (var i = 0; i < records; i++)
            {
                var offset = 4 + i * recordLength;
                if (offset + recordLength > buffer.Length)
                {
                    break;
                }

                var value = BitConverter.ToInt32(buffer, offset + 20);
                if (value > 1 || value < 0)
                {
                    continue;
                }

                var crc = BitConverter.ToUInt32(buffer, offset);
                result[SseCrcKeyPrefix + crc.ToString("x8", CultureInfo.InvariantCulture)] = new LocalEntry
                {
                    earned = value == 1,
                    earned_time = BitConverter.ToInt32(buffer, offset + 8)
                };
            }

            return result;
        }

        private static IReadOnlyDictionary<string, LocalEntry> RemapAwnCrcEntries(
            IReadOnlyDictionary<string, LocalEntry> localEntries,
            IReadOnlyList<SchemaAchievement> schemaAchievements)
        {
            if (localEntries == null || !localEntries.Keys.Any(IsSseCrcKey))
            {
                return localEntries;
            }

            var byCrc = new Dictionary<uint, string>();
            foreach (var schemaName in (schemaAchievements ?? Array.Empty<SchemaAchievement>())
                .Select(a => a?.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                var crc = ComputeCrc32(System.Text.Encoding.UTF8.GetBytes(schemaName));
                if (!byCrc.ContainsKey(crc))
                {
                    byCrc[crc] = schemaName;
                }
            }

            var remapped = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in localEntries)
            {
                if (!IsSseCrcKey(pair.Key))
                {
                    remapped[pair.Key] = pair.Value;
                    continue;
                }

                if (uint.TryParse(pair.Key.Substring(SseCrcKeyPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc) &&
                    byCrc.TryGetValue(crc, out var schemaName))
                {
                    remapped[schemaName] = pair.Value;
                }
            }

            return remapped;
        }

        private static Dictionary<string, LocalEntry> WithoutUnmappedSseEntries(Dictionary<string, LocalEntry> entries)
        {
            if (entries == null || !entries.Keys.Any(IsSseCrcKey))
            {
                return entries;
            }

            return entries.Where(pair => !IsSseCrcKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsSseCrcKey(string key)
        {
            return key != null && key.StartsWith(SseCrcKeyPrefix, StringComparison.Ordinal);
        }

        private static uint[] _crc32Table;

        internal static uint ComputeCrc32(byte[] data)
        {
            var table = _crc32Table;
            if (table == null)
            {
                table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    var c = i;
                    for (var k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    }

                    table[i] = c;
                }

                _crc32Table = table;
            }

            var crc = 0xFFFFFFFFu;
            foreach (var b in data ?? Array.Empty<byte>())
            {
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        internal static Dictionary<string, Dictionary<string, string>> ReadAwnIniSections(IEnumerable<string> lines)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var current = string.Empty;
            foreach (var rawLine in lines ?? Array.Empty<string>())
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[' && line.EndsWith("]", StringComparison.Ordinal) && line.Length > 2)
                {
                    current = TrimIniToken(line.Substring(1, line.Length - 2));
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (!sections.TryGetValue(current, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[current] = values;
                }

                values[TrimIniToken(line.Substring(0, separator))] = StripIniInlineComment(line.Substring(separator + 1)).Trim().Trim('"').TrimEnd(',').Trim();
            }

            return sections;
        }

        // Layouts the generic INI reader cannot express (emuSave.js decodeSaveEntries/normalizeSaveEntry):
        // RLD! hex blobs, 3DM [State]/[Time], Hoodlum unlock times, ALI213 HaveAchieved, CODEX
        // progress, SKIDROW ACHIEVE_DATA, CreamAPI 7-digit times and TENOKE progress.
        internal static void ApplyAwnIniShapes(IEnumerable<string> lines, Dictionary<string, LocalEntry> entries)
        {
            var sections = ReadAwnIniSections(lines);

            if (sections.TryGetValue("Achievements", out var hoodlumState) &&
                sections.TryGetValue("AchievementsUnlockTimes", out var hoodlumTimes))
            {
                foreach (var pair in hoodlumState)
                {
                    entries.TryGetValue(pair.Key, out var entry);
                    entry.earned = pair.Value == "1";
                    if (entry.earned && hoodlumTimes.TryGetValue(pair.Key, out var rawTime) &&
                        long.TryParse(rawTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var time))
                    {
                        entry.earned_time = time;
                    }

                    entries[pair.Key] = entry;
                }
            }

            if (sections.TryGetValue("State", out var threeDmState) && sections.TryGetValue("Time", out var threeDmTime))
            {
                foreach (var pair in threeDmState)
                {
                    entries.TryGetValue(pair.Key, out var entry);
                    entry.earned = pair.Value == "0101";
                    entry.earned_time = entry.earned && threeDmTime.TryGetValue(pair.Key, out var blob) && TryDecodeRldBlob(blob, out var time) ? time : 0;
                    entries[pair.Key] = entry;
                }
            }

            if (sections.TryGetValue("ACHIEVE_DATA", out var skidrow))
            {
                foreach (var pair in skidrow)
                {
                    if (TryParseUnlockedValue(pair.Value, out var unlocked))
                    {
                        entries.TryGetValue(pair.Key, out var entry);
                        entry.earned = unlocked;
                        entries[pair.Key] = entry;
                    }
                }
            }

            if (sections.TryGetValue("ACHIEVEMENTS", out var tenoke))
            {
                sections.TryGetValue("STATS", out var tenokeStats);
                if (tenokeStats != null && tenoke.Values.Any(value => (value ?? string.Empty).StartsWith("{", StringComparison.Ordinal)))
                {
                    // The generic reader takes [STATS] counters for unlock times; they are only progress.
                    foreach (var statName in tenokeStats.Keys.Where(name => !tenoke.ContainsKey(name)))
                    {
                        entries.Remove(statName);
                    }
                }

                foreach (var pair in tenoke)
                {
                    if (!entries.TryGetValue(pair.Key, out var entry))
                    {
                        continue;
                    }

                    var unlockedMatch = Regex.Match(pair.Value ?? string.Empty, @"unlocked\s*=\s*(true|false)", RegexOptions.IgnoreCase);
                    if (unlockedMatch.Success)
                    {
                        entry.earned = string.Equals(unlockedMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                        var timeMatch = Regex.Match(pair.Value, @"time\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                        entry.earned_time = timeMatch.Success && long.TryParse(timeMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inlineTime) ? inlineTime : 0;
                    }

                    var match = InlineProgressPattern.Match(pair.Value ?? string.Empty);
                    if (match.Success && TryParseAwnNumber(match.Groups[1].Value, out var progress))
                    {
                        entry.progress = progress;
                    }
                    else if (tenokeStats != null && tenokeStats.TryGetValue(pair.Key, out var stat) && TryParseAwnNumber(stat, out progress))
                    {
                        entry.progress = progress;
                    }

                    entries[pair.Key] = entry;
                }
            }

            foreach (var section in sections)
            {
                if (section.Key.Length == 0 || AwnNonAchievementSections.Contains(section.Key) || IsGenericIniSection(section.Key))
                {
                    continue;
                }

                ApplyAwnAchievementSection(entries, NormalizeAchievementSectionName(section.Key), section.Value);
            }
        }

        private static void ApplyAwnAchievementSection(Dictionary<string, LocalEntry> entries, string name, Dictionary<string, string> fields)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var exists = entries.TryGetValue(name, out var entry);
            var touched = false;
            bool? explicitState = null;
            long? time = null;
            int? current = null;
            int? maximum = null;

            fields.TryGetValue("State", out var state);
            fields.TryGetValue("Time", out var rawTime);
            fields.TryGetValue("CurProgress", out var rawCurrent);
            fields.TryGetValue("MaxProgress", out var rawMaximum);

            if (state != null && RldBlobPattern.IsMatch(state))
            {
                // RLD!: a State key proves the blob layout, so every field is a blob.
                explicitState = TryDecodeRldBlob(state, out var decodedState) && decodedState != 0;
                time = TryDecodeRldBlob(rawTime, out var decodedTime) ? decodedTime : 0;
                current = TryDecodeRldBlob(rawCurrent, out var decodedCurrent) ? ClampToInt(decodedCurrent) : (int?)null;
                maximum = TryDecodeRldBlob(rawMaximum, out var decodedMaximum) ? ClampToInt(decodedMaximum) : (int?)null;
            }
            else
            {
                if (IsUnambiguousRldBlob(rawTime) && TryDecodeRldBlob(rawTime, out var timeOnly))
                {
                    // RLD! build with no State key: an unlock is carried by Time alone.
                    time = timeOnly;
                    if (IsUnambiguousRldBlob(rawCurrent) && TryDecodeRldBlob(rawCurrent, out var blobCurrent)) current = ClampToInt(blobCurrent);
                    if (IsUnambiguousRldBlob(rawMaximum) && TryDecodeRldBlob(rawMaximum, out var blobMaximum)) maximum = ClampToInt(blobMaximum);
                }

                if (state != null && TryParseUnlockedValue(state, out var stateFlag)) explicitState = stateFlag;
                foreach (var key in new[] { "Achieved", "HaveAchieved", "Unlocked", "earned" })
                {
                    if (fields.TryGetValue(key, out var flag) && TryParseUnlockedValue(flag, out var parsedFlag))
                    {
                        explicitState = parsedFlag;
                    }
                }

                if (!time.HasValue)
                {
                    foreach (var key in new[] { "HaveAchievedTime", "HaveHaveAchievedTime" })
                    {
                        if (fields.TryGetValue(key, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTime))
                        {
                            time = parsedTime;
                        }
                    }

                    // CreamAPI writes truncated 7-digit unlock times.
                    if (fields.TryGetValue("unlocktime", out var creamTime) && creamTime.Length == 7 &&
                        long.TryParse(creamTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var truncated))
                    {
                        time = truncated * 1000;
                    }
                }

                if (!current.HasValue && (TryGetAwnNumber(fields, "CurProgress", out var parsedCurrent) || TryGetAwnNumber(fields, "progress", out parsedCurrent))) current = parsedCurrent;
                if (!maximum.HasValue && (TryGetAwnNumber(fields, "MaxProgress", out var parsedMaximum) || TryGetAwnNumber(fields, "max_progress", out parsedMaximum))) maximum = parsedMaximum;
            }

            if (time.HasValue)
            {
                entry.earned_time = time.Value;
                touched = true;
            }

            if (current.HasValue)
            {
                entry.progress = current;
                touched = true;
            }

            if (maximum.HasValue && maximum.Value > 0)
            {
                entry.max_progress = maximum;
                touched = true;
            }

            if (explicitState.HasValue)
            {
                // An explicit flag wins over a non-zero time (fixes Achieved=0 with UnlockTime set).
                entry.earned = explicitState.Value;
                touched = true;
            }
            else if (entry.earned_time > 0)
            {
                entry.earned = true;
            }

            // CODEX Gears 5 / Gears Tactics: progress maxed out but the Achieved flag never written.
            if (!entry.earned && entry.max_progress > 0 && entry.progress > 0 && entry.progress == entry.max_progress)
            {
                entry.earned = true;
                touched = true;
            }

            if (exists || touched)
            {
                entries[name] = entry;
            }
        }

        private static bool TryGetAwnNumber(Dictionary<string, string> fields, string key, out int value)
        {
            value = 0;
            return fields.TryGetValue(key, out var raw) && TryParseAwnNumber(raw, out value);
        }

        private static bool TryParseAwnNumber(string raw, out int value)
        {
            value = 0;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                double.IsNaN(number) || double.IsInfinity(number))
            {
                return false;
            }

            value = ClampToInt((long)Math.Round(number));
            return true;
        }

        private static int ClampToInt(long value)
        {
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value));
        }

        private static bool IsUnambiguousRldBlob(string value)
        {
            return value != null && RldBlobPattern.IsMatch(value) && value.Any(c => (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        // RLD! blob: 5 hex bytes, a little-endian uint32 plus a discarded trailing byte.
        internal static bool TryDecodeRldBlob(string value, out long decoded)
        {
            decoded = 0;
            if (value == null || !RldBlobPattern.IsMatch(value))
            {
                return false;
            }

            uint result = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                result |= (uint)b << (8 * i);
            }

            decoded = result;
            return true;
        }

        // Achievements.json readers: the typed path first, then a lenient one for saves that write
        // flags as strings/numbers, use other key spellings, or store a list instead of an object.
        internal static Dictionary<string, LocalEntry> ParseLocalJsonSave(string json, bool unlockedWithoutFlag = false)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            json = json.TrimStart('﻿');
            if (!unlockedWithoutFlag)
            {
                try
                {
                    return JsonConvert.DeserializeObject<Dictionary<string, LocalEntry>>(json);
                }
                catch (JsonException)
                {
                }
            }

            var token = JToken.Parse(json);
            var result = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Value is JObject entryObject)
                    {
                        result[property.Name] = ReadLenientJsonEntry(entryObject, unlockedWithoutFlag);
                    }
                    else if (TryParseUnlockedValue(property.Value.ToString(), out var flag))
                    {
                        result[property.Name] = new LocalEntry { earned = flag };
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array.OfType<JObject>())
                {
                    var name = (string)(item["name"] ?? item["apiname"] ?? item["id"] ?? item["AchievementId"]);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result[name] = ReadLenientJsonEntry(item, unlockedWithoutFlag);
                    }
                }
            }

            return result;
        }

        private static LocalEntry ReadLenientJsonEntry(JObject item, bool unlockedWithoutFlag = false)
        {
            string Field(params string[] names)
            {
                foreach (var name in names)
                {
                    var property = item.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (property != null && property.Value.Type != JTokenType.Null)
                    {
                        return property.Value.ToString();
                    }
                }

                return null;
            }

            var entry = new LocalEntry
            {
                displayName = Field("displayName", "display_name", "name_display"),
                description = Field("description", "desc"),
                icon = Field("icon"),
                iconGray = Field("iconGray", "icon_gray", "icongray")
            };

            if (TryParseUnlockedValue(Field("hidden"), out var hidden)) entry.hidden = hidden;
            if (long.TryParse(Field("earned_time", "unlock_time", "unlocktime", "UnlockTime", "time", "timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var time))
            {
                entry.earned_time = time;
            }

            var flag = Field("earned", "achieved", "unlocked", "Achieved", "Unlocked");
            entry.earned = flag != null && TryParseUnlockedValue(flag, out var earned) ? earned : unlockedWithoutFlag || entry.earned_time > 0;
            if (TryParseAwnNumber(Field("progress", "CurProgress"), out var progress)) entry.progress = progress;
            if (TryParseAwnNumber(Field("max_progress", "MaxProgress"), out var maximum) && maximum > 0) entry.max_progress = maximum;
            return entry;
        }

        // GreenLuma keeps unlocks in HKCU\SOFTWARE\<GL key>\AppID\<appid>\Achievements, only when
        // the AppID key sets SkipStatsAndAchievements=0 (greenluma.js).
        private Dictionary<string, LocalEntry> TryGetGreenLumaRegistryEntries(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit) || !IsWindowsPlatform())
            {
                return null;
            }

            foreach (var keyName in GreenLumaRegistryKeys)
            {
                try
                {
                    using (var appKey = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\{keyName}\AppID\{appId}", false))
                    {
                        if (appKey == null || !int.TryParse(Convert.ToString(appKey.GetValue("SkipStatsAndAchievements"), CultureInfo.InvariantCulture), out var skip) || skip != 0)
                        {
                            continue;
                        }

                        using (var achievementsKey = appKey.OpenSubKey("Achievements", false))
                        {
                            var names = achievementsKey?.GetValueNames() ?? Array.Empty<string>();
                            var entries = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
                            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n) && !n.EndsWith("_Time", StringComparison.OrdinalIgnoreCase)))
                            {
                                int.TryParse(Convert.ToString(achievementsKey.GetValue(name), CultureInfo.InvariantCulture), out var achieved);
                                long.TryParse(Convert.ToString(achievementsKey.GetValue(name + "_Time"), CultureInfo.InvariantCulture), out var time);
                                entries[name] = new LocalEntry { earned = achieved != 0, earned_time = achieved != 0 ? time : 0 };
                            }

                            if (entries.Count > 0)
                            {
                                Log($"GREENLUMA REGISTRY: appId={appId} key={keyName} values={entries.Count}");
                                return entries;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"GREENLUMA REGISTRY ERROR: appId={appId} key={keyName} msg={ex.Message}");
                }
            }

            return null;
        }

        // Save folders that emulator configs inside the install redirect to (userDir.js, goldberg.js).
        // Callers keep only folders that actually hold a recognized save file.
        private static IEnumerable<string> EnumerateAwnInstallConfigSaveFolders(string installDirectory, IReadOnlyList<string> excludedPaths)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return results;
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var pending = new Stack<KeyValuePair<string, int>>();
            pending.Push(new KeyValuePair<string, int>(installDirectory, 0));
            var visited = 0;
            while (pending.Count > 0 && visited < 3000)
            {
                var item = pending.Pop();
                var directory = item.Key;
                visited++;

                if (string.Equals(Path.GetFileName(directory), "steam_settings", StringComparison.OrdinalIgnoreCase))
                {
                    results.AddRange(GetGoldbergConfiguredSaveFolders(directory));
                }
                else if (string.Equals(Path.GetFileName(directory), "SteamData", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(directory);
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        if (AwnInstallConfigFileNames.Contains(Path.GetFileName(file)))
                        {
                            results.AddRange(GetEmulatorConfigSaveFolders(file, installDirectory, documents));
                        }
                    }
                }
                catch
                {
                    continue;
                }

                if (item.Value >= 6)
                {
                    continue;
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        try
                        {
                            if (IsPathExcludedFromLocalScan(child, excludedPaths) ||
                                (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }
                        }
                        catch
                        {
                            continue;
                        }

                        pending.Push(new KeyValuePair<string, int>(child, item.Value + 1));
                    }
                }
                catch
                {
                }
            }

            return results.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> GetEmulatorConfigSaveFolders(string configPath, string installDirectory, string documents)
        {
            var results = new List<string>();
            string text;
            try
            {
                text = File.ReadAllText(configPath);
            }
            catch
            {
                return results;
            }

            var sections = ReadAwnIniSections(text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            var directory = Path.GetDirectoryName(configPath);
            var fileName = Path.GetFileName(configPath);
            string Value(string section, string key) =>
                sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : null;

            if (fileName.Equals("ALI213.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("valve.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("SteamConfig.ini", StringComparison.OrdinalIgnoreCase))
            {
                var appId = Value("Settings", "AppID");
                var player = Value("Settings", "PlayerName");
                var saveType = Value("Settings", "SaveType");
                if (!string.IsNullOrWhiteSpace(appId))
                {
                    if (!string.IsNullOrWhiteSpace(player) && saveType == "1" && !string.IsNullOrWhiteSpace(documents))
                    {
                        results.Add(Path.Combine(documents, "VALVE", appId, player, "Stats"));
                    }
                    else if (!string.IsNullOrWhiteSpace(player) && saveType == "0")
                    {
                        results.AddRange(FindInAncestors(directory, installDirectory, Path.Combine("Profile", player, "Stats")));
                    }
                    else if (string.IsNullOrWhiteSpace(saveType))
                    {
                        results.AddRange(FindInAncestors(directory, installDirectory, Path.Combine("Profile", "Stats")));
                    }
                }
            }
            else if (fileName.Equals("ds.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("hlm.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("steam_api.ini", StringComparison.OrdinalIgnoreCase))
            {
                var appId = Value("GameSettings", "AppId");
                var userDataFolder = Value("GameSettings", "UserDataFolder");
                var userName = Value("GameSettings", "UserName");
                if (!string.IsNullOrWhiteSpace(appId) && userDataFolder == ".")
                {
                    results.AddRange(FindInAncestors(directory, installDirectory, @"SteamEmu\UserStats"));
                    results.AddRange(FindInAncestors(directory, installDirectory, "SteamEmu"));
                    if (fileName.Equals("hlm.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        results.AddRange(FindInAncestors(directory, installDirectory, @"Profile\VALVE\Stats"));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(appId) && string.Equals(userDataFolder, "mydocs", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(documents))
                {
                    var steamEmu = Path.Combine(documents, userName, appId, "SteamEmu");
                    results.Add(Path.Combine(steamEmu, "UserStats"));
                    results.Add(steamEmu);
                }

                // Catherine-style steam_api.ini: [Settings] AppId + SteamID.
                var steamId = Value("Settings", "SteamID");
                if (fileName.Equals("steam_api.ini", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(Value("Settings", "AppId")) && !string.IsNullOrWhiteSpace(steamId))
                {
                    results.AddRange(FindInAncestors(directory, installDirectory, Path.Combine("SteamProfile", steamId)));
                }
            }
            else if (fileName.Equals("tenoke.ini", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Path.Combine(directory, "SteamData"));
            }
            else if (fileName.Equals("steam_emu.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("cpy.ini", StringComparison.OrdinalIgnoreCase))
            {
                var appId = sections.Values
                    .SelectMany(values => values)
                    .Where(pair => Regex.IsMatch(pair.Key, "^app_?id$", RegexOptions.IgnoreCase))
                    .Select(pair => Regex.Match(pair.Value ?? string.Empty, @"\d+").Value)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(appId))
                {
                    foreach (var tree in AwnPortableSceneTrees)
                    {
                        results.Add(Path.Combine(directory, tree, appId));
                    }

                    // CODEX/RUNE note the save folder in a "### Game data is stored at ..." header.
                    var note = SceneSaveNotePattern.Match(text);
                    if (note.Success)
                    {
                        var noted = Environment.ExpandEnvironmentVariables(note.Groups[1].Value.Trim().TrimEnd('\\', '/'));
                        var last = Path.GetFileName(noted);
                        var root = Regex.IsMatch(last ?? string.Empty, @"^\d+$|[<{%]") ? Path.GetDirectoryName(noted) : noted;
                        if (!string.IsNullOrWhiteSpace(root) && Path.IsPathRooted(root) && root.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                        {
                            results.Add(Path.Combine(root, appId));
                        }
                    }
                }
            }

            return results;
        }

        // Goldberg/GBE redirected saves: configs.user.ini local_save_path or saves_folder_name, then
        // classic local_save.txt (in steam_settings or beside the dll). Relative paths start at the dll.
        private static IEnumerable<string> GetGoldbergConfiguredSaveFolders(string steamSettings)
        {
            var results = new List<string>();
            var dllDirectory = Path.GetDirectoryName(steamSettings);
            string configured = null;
            try
            {
                var userIni = Path.Combine(steamSettings, "configs.user.ini");
                if (File.Exists(userIni))
                {
                    var text = File.ReadAllText(userIni);
                    var match = Regex.Match(text, @"^[ \t]*local_save_path[ \t]*=[ \t]*(\S.*?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (match.Success)
                    {
                        configured = match.Groups[1].Value.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(configured))
                    {
                        var renamed = Regex.Match(text, @"^[ \t]*saves_folder_name[ \t]*=[ \t]*(\S.*?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        if (renamed.Success && !string.IsNullOrWhiteSpace(appData))
                        {
                            configured = Path.Combine(appData, renamed.Groups[1].Value.Trim());
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(configured))
                {
                    foreach (var marker in new[] { Path.Combine(steamSettings, "local_save.txt"), Path.Combine(dllDirectory ?? string.Empty, "local_save.txt") })
                    {
                        if (File.Exists(marker))
                        {
                            configured = File.ReadLines(marker).FirstOrDefault()?.Trim();
                            if (!string.IsNullOrWhiteSpace(configured))
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                return results;
            }

            if (string.IsNullOrWhiteSpace(configured) ||
                Regex.IsMatch(configured, @"^[.\\/]*path[\\/]relative[\\/]to[\\/]dll[\\/]*$", RegexOptions.IgnoreCase) ||
                configured.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return results;
            }

            string root;
            try
            {
                root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(dllDirectory ?? string.Empty, configured));
            }
            catch
            {
                return results;
            }

            var appId = ReadSteamAppIdFile(Path.Combine(steamSettings, "steam_appid.txt")) ??
                ReadSteamAppIdFile(Path.Combine(dllDirectory ?? string.Empty, "steam_appid.txt"));
            if (!string.IsNullOrWhiteSpace(appId))
            {
                results.Add(Path.Combine(root, appId));
            }

            results.Add(root);
            return results;
        }

        private static string ReadSteamAppIdFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var digits = Regex.Match(File.ReadAllText(path), @"\d+").Value;
                return string.IsNullOrWhiteSpace(digits) ? null : digits;
            }
            catch
            {
                return null;
            }
        }

        // The relative folder in the config's directory or a parent, never above the install root.
        private static IEnumerable<string> FindInAncestors(string directory, string installDirectory, string relative)
        {
            var root = (installDirectory ?? string.Empty).TrimEnd('\\', '/');
            var current = directory;
            while (!string.IsNullOrWhiteSpace(current))
            {
                var candidate = Path.Combine(current, relative);
                if (Directory.Exists(candidate))
                {
                    yield return candidate;
                    yield break;
                }

                if (string.Equals(current.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase))
                {
                    yield break;
                }

                current = Path.GetDirectoryName(current);
            }
        }
    }
}
