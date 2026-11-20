using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSavesProvider
    {
        private bool TryGetNativeConfiguration(Game game, out NativeSourceConfiguration config, out string install)
        {
            config = null;
            install = null;
            if (game == null) return false;
            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.NativeSourcesEnabled == false) return false;
            settings?.NativeSourceOverrides?.TryGetValue(game.Id, out config);
            install = ResolveNativeInstall(game, config, settings);
            var source = config?.Source;
            if (source == "disabled") return false;
            if (string.IsNullOrWhiteSpace(source) || source == "auto") source = NativeSourceReader.Detect(string.IsNullOrWhiteSpace(config?.InstallPath) ? install : ExpandNativePath(game, config.InstallPath));
            if (string.IsNullOrWhiteSpace(source) || settings?.DisabledNativeSources?.Contains(source) == true) return false;
            config = new NativeSourceConfiguration
            {
                Source = source,
                InstallPath = ExpandNativePath(game, config?.InstallPath),
                SavePath = ExpandNativePath(game, config?.SavePath),
                SchemaPath = ExpandNativePath(game, config?.SchemaPath),
                Profile = config?.Profile ?? "", CollectionGame = config?.CollectionGame ?? "", UserId = config?.UserId ?? ""
            };
            return true;
        }

        internal string InspectNativeSource(Game game, NativeSourceConfiguration draft)
        {
            if (game == null) return "No game selected.";
            var config = new NativeSourceConfiguration
            {
                Source = draft?.Source ?? "auto", Profile = draft?.Profile ?? "", CollectionGame = draft?.CollectionGame ?? "", UserId = draft?.UserId ?? "",
                InstallPath = ExpandNativePath(game, draft?.InstallPath),
                SavePath = ExpandNativePath(game, draft?.SavePath),
                SchemaPath = ExpandNativePath(game, draft?.SchemaPath)
            };
            var settings = ProviderRegistry.Settings<LocalSettings>();
            var install = ResolveNativeInstall(game, config, settings);
            var source = config.Source == "auto" ? NativeSourceReader.Detect(string.IsNullOrWhiteSpace(config.InstallPath) ? install : config.InstallPath) : config.Source;
            var context = CreateNativeReadContext(game, source, settings);
            var result = NativeSourceReader.Read(install, config, null, context: context);
            var enabled = settings?.NativeSourcesEnabled != false && settings?.DisabledNativeSources?.Contains(result.Source) != true;
            return (enabled ? "" : "This source is disabled in Santodan-Fork → Platforms.\n") + result.Description +
                "\nConfigured save folders: " + (string.IsNullOrWhiteSpace(context.Locations.SaveFolders) ? "Automatic" : context.Locations.SaveFolders) +
                "\nConfigured definition folders: " + (string.IsNullOrWhiteSpace(context.Locations.DefinitionFolders) ? "Game install / bundled definitions" : context.Locations.DefinitionFolders) +
                "\nConfigured emulator folders: " + (string.IsNullOrWhiteSpace(context.Locations.EmulatorFolders) ? "None" : context.Locations.EmulatorFolders) +
                "\nProfile preference: " + (!string.IsNullOrWhiteSpace(config.Profile) ? config.Profile : string.IsNullOrWhiteSpace(context.Locations.Profile) ? "Automatic; exact choice required if ambiguous" : context.Locations.Profile) +
                (source == "kytyps5" ? "\nKyty user preference: " + (!string.IsNullOrWhiteSpace(config.UserId) ? config.UserId : string.IsNullOrWhiteSpace(context.Locations.UserId) ? "Follow Kyty.ini" : context.Locations.UserId) : "");
        }

        private async Task<GameAchievementData> ReadNativeSourceAsync(Game game, NativeSourceConfiguration config, string install)
        {
            var dataRoot = PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath();
            var cache = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "icon_cache", game.Id.ToString(), "original", "native", config.Source);
            var context = CreateNativeReadContext(game, config.Source, ProviderRegistry.Settings<LocalSettings>());
            var result = await Task.Run(() => NativeSourceReader.Read(install, config, cache, context: context)).ConfigureAwait(false);
            if (!result.Success && config.Source == "x360recomp" && string.IsNullOrWhiteSpace(config.SchemaPath) &&
                string.IsNullOrWhiteSpace(context.Locations.DefinitionFolders) && string.IsNullOrWhiteSpace(result.Schema))
            {
                var schema = await GetNativeXboxDefinitionsAsync(result.Save, dataRoot).ConfigureAwait(false);
                if (schema != null)
                {
                    config.SchemaPath = schema;
                    result = await Task.Run(() => NativeSourceReader.Read(install, config, cache, context: context)).ConfigureAwait(false);
                }
            }
            Log("NATIVE SOURCE: game='" + game.Name + "' " + result.Description);
            if (!result.Success) return null;
            var data = new GameAchievementData
            {
                ProviderKey = ProviderKey, ProviderGameKey = NativeSourceIdentity.Key(config.Source, result.Save, result.Schema, config.CollectionGame),
                PlayniteGameId = game.Id, GameName = game.Name, LastUpdatedUtc = DateTime.UtcNow,
                Achievements = result.Achievements.Select((a, i) => new AchievementDetail
                {
                    ApiName = "native:" + config.Source + ":" + a.Id, DisplayName = a.Name, Description = a.Description,
                    Unlocked = a.Unlocked, UnlockTimeUtc = a.Time, Hidden = a.Hidden,
                    UnlockedIconPath = a.Icon, LockedIconPath = a.Icon, HasSourceUnlockedIcon = !string.IsNullOrWhiteSpace(a.Icon),
                    DisplayOrder = i, Points = a.Points, ProgressNum = a.Progress, ProgressDenom = a.Goal,
                    TrophyType = a.TrophyType, IsCapstone = a.TrophyType == "platinum"
                }).ToList()
            };
            return data;
        }

        private async Task<string> GetNativeXboxDefinitionsAsync(string save, string dataRoot)
        {
            if (string.IsNullOrWhiteSpace(save) || string.IsNullOrWhiteSpace(dataRoot)) return null;
            var title = Path.GetFileNameWithoutExtension(save);
            if (!Regex.IsMatch(title, "^[0-9a-fA-F]{8}$")) title = Path.GetFileName(Path.GetDirectoryName(save));
            if (!Regex.IsMatch(title ?? "", "^[0-9a-fA-F]{8}$")) return null;
            title = title.ToUpperInvariant();
            var target = Path.Combine(dataRoot, "local_schema_cache", "native-xbox", title + ".json");
            if (File.Exists(target))
            {
                try { if (NativeSourceReader.JsonSchema(JToken.Parse(NativeSourceReader.Text(target)), Path.GetDirectoryName(target)).Count > 0) return target; }
                catch (Exception ex) { Log("NATIVE XBOX SCHEMA CACHE: " + ex.Message); }
            }
            if (ProviderRegistry.Settings<LocalSettings>()?.DownloadNativeXboxDefinitions == false || !await CanUseLocalSchemaNetworkAsync().ConfigureAwait(false)) return null;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = NativeSourceReader.MaxTextBytes })
                {
                    var response = JArray.Parse(await client.GetStringAsync("https://dbox.tools/api/achievements/v1/" + title).ConfigureAwait(false));
                    var rows = new JArray();
                    foreach (var entry in response.Take(2048))
                    {
                        if (!string.Equals((string)entry["title_id"], title, StringComparison.OrdinalIgnoreCase)) continue;
                        var translations = entry["localizations"] as JArray;
                        var text = translations?.FirstOrDefault(t => (string)t["locale"]?["locale"] == "en-US") ?? translations?.FirstOrDefault();
                        if (text == null || entry["achievement_id"] == null) continue;
                        rows.Add(new JObject { ["id"] = entry["achievement_id"], ["displayName"] = text["name"], ["description"] = text["description"], ["gamerscore"] = entry["gamerscore"], ["hidden"] = entry["is_secret"] });
                    }
                    if (NativeSourceReader.JsonSchema(rows, Path.GetDirectoryName(target)).Count == 0) return null;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(temporary, rows.ToString()); if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return target;
                }
            }
            catch (Exception ex) { Log("NATIVE XBOX SCHEMA: " + ex.Message); return null; }
        }

        // Return unknown so both monitors refresh native sources. This also sees the first save
        // created after startup and avoids fingerprints that omit log rotation or moved schemas.
        private bool HasNativeSource(Game game) => TryGetNativeConfiguration(game, out _, out _);

        private string ExpandNativePath(Game game, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var expanded = ExpandFolderPaths(_api, game, path).Where(p => !string.IsNullOrWhiteSpace(p)).Take(2).ToArray();
            return expanded.Length == 1 ? expanded[0] : path;
        }

        private string ExpandNativeFolders(Game game, string paths) => string.Join(Environment.NewLine,
            NativeSourceLocations.Split(paths).SelectMany(p =>
            {
                var expanded = ExpandFolderPaths(_api, game, p).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
                return expanded.Length == 0 ? new[] { p } : expanded;
            }));

        private NativeSourceReadContext CreateNativeReadContext(Game game, string source, LocalSettings settings)
        {
            NativeSourceLocations saved = null;
            if (source != null) settings?.NativeSourceLocations?.TryGetValue(source, out saved);
            var locations = (saved ?? new NativeSourceLocations()).Copy();
            locations.InstallFolders = ExpandNativeFolders(game, locations.InstallFolders);
            locations.SaveFolders = ExpandNativeFolders(game, locations.SaveFolders);
            locations.DefinitionFolders = ExpandNativeFolders(game, locations.DefinitionFolders);
            locations.EmulatorFolders = ExpandNativeFolders(game, locations.EmulatorFolders);
            locations.SettingsFile = string.IsNullOrWhiteSpace(locations.SettingsFile) ? "" : ExpandNativeFolders(game, locations.SettingsFile);
            return new NativeSourceReadContext { GameName = game?.Name, GameId = game?.GameId, Locations = locations };
        }

        private string ResolveNativeInstall(Game game, NativeSourceConfiguration config, LocalSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(config?.InstallPath)) return ExpandNativePath(game, config.InstallPath);
            var install = PathExpansion.ExpandGamePath(_api, game, game.InstallDirectory)?.Trim();
            var source = config?.Source ?? "auto";
            if (Directory.Exists(install) && (source != "auto" || NativeSourceReader.Detect(install) != null)) return install;
            try
            {
                var roots = NativeSourceLocations.Split(ExpandNativeFolders(game, settings?.NativeGameLibraryFolders)).ToList();
                foreach (var option in NativeSourceCatalog.Sources.Where(s => s.Key != "auto" && (source == "auto" || s.Key == source)))
                {
                    var context = CreateNativeReadContext(game, option.Key, settings);
                    roots.AddRange(NativeSourceLocations.Split(context.Locations.InstallFolders));
                    if (option.Key == "kytyps5") roots.AddRange(NativeLocationFiles.KytyGameFolders(context.Locations));
                }
                var found = NativeLocationDiscovery.FindInstall(null, game.Name, game.GameId, source, roots);
                if (found != null) return found;
                if (settings?.NativeUseQuiver != false)
                    return NativeQuiverDiscovery.FindInstall(game.Name, NativeSourceLocations.Split(ExpandNativeFolders(game, settings?.NativeQuiverFolders))) ?? install;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException)
            { Log("NATIVE LOCATION DISCOVERY: " + ex.Message); }
            return install;
        }
    }
}
