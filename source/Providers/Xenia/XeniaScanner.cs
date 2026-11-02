using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.EmuLibrary;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Xenia.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xenia
{
    internal class XeniaScanner
    {
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly XeniaSettings _providerSettings;
        private readonly string _pluginUserDataPath;
        private readonly PlayniteAchievementsSettings _settings;

        List<KeyValuePair<Guid, string>> _titleIDCache = new List<KeyValuePair<Guid, string>>();

        public XeniaScanner(
            ILogger logger,
            IPlayniteAPI playniteApi,
            XeniaSettings providerSettings,
            string pluginUserDataPath,
            PlayniteAchievementsSettings settings = null)
        {
            _logger = logger;
            _playniteApi = playniteApi;
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            _settings = settings;
            _titleIDCache = LoadTitleIdCache(_pluginUserDataPath, logger);
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (ProviderPathList.Normalize(_providerSettings.AccountPaths).Count == 0)
            {
                _logger?.Warn("[Xenia] No account paths configured - cannot scan achievements.");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }

            if (gamesToRefresh is null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) =>
                {
                    var data = GetAchievementData(game, out var gpdTitle);
                    if (data != null)
                    {
                        // The GPD's title is searched before the Playnite name; the achievements are Xbox 360's.
                        data.MetadataHints = new MetadataHints { SearchName = gpdTitle, PlatformSlug = "xbox-360" };
                    }

                    return Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    });
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Warn(ex, $"[Xenia] Failed to scan game '{game?.Name}'");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        /// <param name="gpdTitle">
        /// The game's title as its GPD records it (the first non-empty one across account
        /// folders), or null when no GPD carries one.
        /// </param>
        private GameAchievementData GetAchievementData(Game game, out string gpdTitle)
        {
            gpdTitle = null;
            var accountDirectories = XeniaAccountResolver.ResolveAccountDirectories(game, _providerSettings, _playniteApi);
            if (!ResolveTitleID(game, accountDirectories, out var titleID))
            {
                _playniteApi.Notifications.Add(new NotificationMessage("PA_Xenia", string.Format(ResourceProvider.GetString("LOCPlayAch_Xenia_NotFoundWarning"), "TitleID", game.Name), NotificationType.Error));
                return null;
            }

            GameAchievementData data = null;
            var gpdPaths = GetGpdPaths(accountDirectories, titleID);

            if (gpdPaths.Count == 0)
            {
                _playniteApi.Notifications.Add(new NotificationMessage("PA_Xenia", string.Format(ResourceProvider.GetString("LOCPlayAch_Xenia_NotFoundWarning"), $"{titleID}.gpd", game.Name), NotificationType.Info));
                _logger.Warn($"[Xenia] {titleID}.gpd file not found for {game.Name} in [{string.Join(", ", accountDirectories)}]!");
                data = new GameAchievementData
                {
                    AppId = int.Parse(titleID, System.Globalization.NumberStyles.HexNumber),
                    GameName = game?.Name,
                    ProviderKey = "Xenia",
                    LibrarySourceName = game?.Source?.Name,
                    LastUpdatedUtc = DateTime.UtcNow,
                    HasAchievements = false,
                    PlayniteGameId = game?.Id,
                };
            }
            else
            {
                var gpdFiles = gpdPaths.Select(path => new GPDResolver().LoadGPD(path)).ToList();
                gpdTitle = gpdFiles
                    .Select(file => file.StringData?.Trim())
                    .FirstOrDefault(title => !string.IsNullOrEmpty(title));

                // Write icon data to icon cache; the first file carrying an icon id wins
                var iconDirectory = $"{_pluginUserDataPath}\\icon_cache\\{game.Id}\\";
                Directory.CreateDirectory(iconDirectory);
                var writtenIconIds = new HashSet<int>();
                foreach (var icon in gpdFiles.SelectMany(file => file.IconData))
                {
                    if (!writtenIconIds.Add(icon.Key))
                    {
                        continue;
                    }

                    using (var fs = new FileStream($"{iconDirectory}{icon.Key}.png", FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(icon.Value, 0, icon.Value.Length);
                    }
                }

                // Definitions come from the first file listing each id; unlock state is merged
                // across every build's copy.
                var definitions = new Dictionary<uint, XdbfAchievement>();
                foreach (var achievement in gpdFiles.SelectMany(file => file.Achievements))
                {
                    if (!definitions.ContainsKey(achievement.id))
                    {
                        definitions[achievement.id] = achievement;
                    }
                }

                var progress = XeniaAccountResolver.MergeProgress(gpdFiles.Select(file => file.Achievements.Select(achievement =>
                    new XeniaAchievementProgress
                    {
                        Id = achievement.id,
                        Unlocked = achievement.earned,
                        UnlockTime = achievement.unlock_time
                    })));

                List<AchievementDetail> achievements = new List<AchievementDetail>();
                foreach (var merged in progress)
                {
                    var achievement = definitions[merged.Id];
                    var iconPath = $"{iconDirectory}{achievement.icon_id}.png";
                    if (!File.Exists(iconPath))
                    {
                        iconPath = null;
                    }

                    achievements.Add(new AchievementDetail
                    {
                        ApiName = achievement.id.ToString(),
                        DisplayName = achievement.title,
                        Description = merged.UnlockTime == 0 ? achievement.description : achievement.unlockDescription,
                        Category = ((XdbfAchievementTypes)(achievement.flags & 7)).ToString(),
                        UnlockedIconPath = iconPath,
                        LockedIconPath = iconPath,
                        Points = (int?)achievement.gamerscore,
                        Rarity = GetRarityFromXboxPoints((int?)achievement.gamerscore),
                        Unlocked = merged.Unlocked,
                        UnlockTimeUtc = merged.UnlockTime != 0
                            ? DateTime.FromFileTimeUtc((Int64)merged.UnlockTime)
                            : (DateTime?)null,
                        Hidden = ((achievement.flags & 8) == 0)
                    });
                }

                data = new GameAchievementData
                {
                    AppId = int.Parse(titleID, System.Globalization.NumberStyles.HexNumber),
                    GameName = game?.Name,
                    ProviderKey = "Xenia",
                    LibrarySourceName = game?.Source?.Name,
                    LastUpdatedUtc = DateTime.UtcNow,
                    HasAchievements = achievements.Count > 0,
                    RaritySource = RaritySource.Tier,
                    PlayniteGameId = game?.Id,
                    Achievements = achievements
                };
            }

            SaveTitleIdCache();

            return data;
        }

        /// <summary>
        /// Existing {titleID}.gpd files across the account folders, in folder order.
        /// </summary>
        internal static List<string> GetGpdPaths(IEnumerable<string> accountDirectories, string titleID)
        {
            return (accountDirectories ?? Enumerable.Empty<string>())
                .Select(directory => Path.Combine(directory, $"{titleID}.gpd"))
                .Where(File.Exists)
                .ToList();
        }

        internal bool ResolveTitleID(Game game, out string titleID)
        {
            return ResolveTitleID(
                game,
                XeniaAccountResolver.ResolveAccountDirectories(game, _providerSettings, _playniteApi),
                out titleID);
        }

        private bool ResolveTitleID(Game game, IReadOnlyList<string> accountDirectories, out string titleID)
        {
            if (game == null)
            {
                titleID = string.Empty;
                return false;
            }

            if (GameCustomDataLookup.TryGetXeniaTitleIdOverride(game.Id, out var overrideTitleId))
            {
                CacheTitleId(game.Id, overrideTitleId);
                titleID = overrideTitleId;
                return true;
            }

            // Skip titleID search if it has been cached
            if (TryGetCachedTitleId(game.Id, out titleID))
            {
                return true;
            }

            var candidatePaths = GetCandidateRomPaths(game);

            // Read the TitleID from the executable's XEX header
            foreach (var path in candidatePaths)
            {
                if (TryReadTitleIdFromXexHeader(path, out var headerTitleId))
                {
                    titleID = headerTitleId;
                    CacheTitleId(game.Id, headerTitleId);
                    return true;
                }
            }

            // Try to find game in each build's recent.toml
            foreach (var path in candidatePaths)
            {
                foreach (var accountDirectory in accountDirectories ?? Array.Empty<string>())
                {
                    if (TryResolveTitleIdFromRecent(path, accountDirectory, out titleID))
                    {
                        CacheTitleId(game.Id, titleID);
                        return true;
                    }
                }
            }

            titleID = "";
            return false;
        }

        /// <summary>
        /// Reads the TitleID from the execution info header of a .xex, or of default.xex inside
        /// an .iso. False for other files and for any file the header read cannot parse, which
        /// leaves the later lookups to try it.
        /// </summary>
        private bool TryReadTitleIdFromXexHeader(string path, out string titleID)
        {
            titleID = null;
            if (!File.Exists(path))
            {
                return false;
            }

            var isIso = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
            if (!isIso && !path.EndsWith(".xex", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var executionInfo = isIso
                    ? XeniaTitleIDExtractor.GetFromIsoFile(path)
                    : XeniaTitleIDExtractor.GetFromXexFile(path);
                if (executionInfo == null ||
                    executionInfo.TitleId == 0 ||
                    !XeniaTitleIdHelper.TryNormalize(executionInfo.TitleIdHex, out titleID))
                {
                    titleID = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                _logger?.Debug($"[Xenia] XEX header read failed for '{path}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Looks the rom up in recent.toml at the account folder's Xenia root, then finds the
        /// account's .gpd whose title string matches the recorded title.
        /// </summary>
        private static bool TryResolveTitleIdFromRecent(string romPath, string accountDirectory, out string titleID)
        {
            titleID = null;
            var recentPath = Path.Combine(XeniaAccountResolver.GetXeniaRoot(accountDirectory) ?? string.Empty, "recent.toml");
            if (!File.Exists(recentPath))
            {
                return false;
            }

            bool foundROM = false;
            string ROMTitle = "";

            // Read all lines in toml file
            foreach (string line in File.ReadLines(recentPath))
            {
                if (foundROM)
                {
                    var quoteMarks = line.IndexOf('"');
                    if (quoteMarks == -1)
                    {
                        quoteMarks = line.IndexOf('\'');
                    }

                    if (quoteMarks >= 0)
                    {
                        quoteMarks++;

                        ROMTitle = line.Substring(quoteMarks, (line.Length - quoteMarks) - 1);
                        break;
                    }
                }

                if (line.StartsWith("path", StringComparison.OrdinalIgnoreCase))
                {
                    var linepath = line.Replace("\\\\", "\\");
                    if (linepath.Contains(romPath))
                    {
                        foundROM = true;
                        continue;
                    }
                }
            }

            if (!foundROM)
            {
                return false;
            }

            // Read all gpd files
            foreach (var gpdFilePath in Directory.EnumerateFiles(accountDirectory, "*.gpd"))
            {
                // Skip base account data
                if (gpdFilePath.EndsWith("FFFE07D1.gpd"))
                    continue;

                if (!GPDResolver.TryReadTitleString(gpdFilePath, out var stringData))
                    continue;

                var gameName = stringData.Replace("\0", "");
                gameName = gameName.Replace("\"", "");

                if (gameName == ROMTitle)
                {
                    titleID = Path.GetFileNameWithoutExtension(gpdFilePath);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Collects normalized candidate rom paths for a game: explicit rom entries plus,
        /// for uninstalled EmuLibrary games, the source file decoded from the game id.
        /// </summary>
        private List<string> GetCandidateRomPaths(Game game)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (game?.Roms != null)
            {
                foreach (var rom in game.Roms)
                {
                    AddCandidateRomPath(paths, seen, PathExpansion.ExpandGamePath(_playniteApi, game, rom?.Path));
                }
            }

            if (EmuLibraryPathResolver.TryResolveSourceFilePath(_playniteApi, game, out var emuLibrarySourceFile))
            {
                AddCandidateRomPath(paths, seen, emuLibrarySourceFile);
            }

            return paths;
        }

        private static void AddCandidateRomPath(List<string> paths, HashSet<string> seen, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = path.Replace("\\\\", "\\").Trim('"');
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                paths.Add(normalized);
            }
        }

        internal bool TryGetCachedTitleId(Guid gameId, out string titleId)
        {
            var cached = _titleIDCache.FirstOrDefault(pair => pair.Key == gameId);
            titleId = cached.Key == Guid.Empty
                ? null
                : XeniaTitleIdHelper.Normalize(cached.Value);
            return !string.IsNullOrWhiteSpace(titleId);
        }

        internal static void ClearCachedTitleId(string pluginUserDataPath, Guid gameId, ILogger logger)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                return;
            }

            var cachePath = GetTitleIdCachePath(pluginUserDataPath);
            var cache = LoadTitleIdCache(pluginUserDataPath, logger);
            var removedCount = cache.RemoveAll(pair => pair.Key == gameId);
            if (removedCount <= 0)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(cachePath, JsonConvert.SerializeObject(cache));
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[Xenia] Failed to clear cached TitleID for gameId={gameId}");
            }
        }

        private void CacheTitleId(Guid gameId, string titleId)
        {
            if (gameId == Guid.Empty || !XeniaTitleIdHelper.TryNormalize(titleId, out var normalizedTitleId))
            {
                return;
            }

            _titleIDCache.RemoveAll(pair => pair.Key == gameId);
            _titleIDCache.Add(new KeyValuePair<Guid, string>(gameId, normalizedTitleId));
        }

        private void SaveTitleIdCache()
        {
            var cachePath = GetTitleIdCachePath(_pluginUserDataPath);
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(cachePath, JsonConvert.SerializeObject(_titleIDCache));
        }

        private static List<KeyValuePair<Guid, string>> LoadTitleIdCache(string pluginUserDataPath, ILogger logger)
        {
            var cachePath = GetTitleIdCachePath(pluginUserDataPath);
            if (!File.Exists(cachePath))
            {
                return new List<KeyValuePair<Guid, string>>();
            }

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonConvert.DeserializeObject<List<KeyValuePair<Guid, string>>>(json);
                return cache?
                    .Where(pair => pair.Key != Guid.Empty && XeniaTitleIdHelper.TryNormalize(pair.Value, out _))
                    .Select(pair => new KeyValuePair<Guid, string>(pair.Key, XeniaTitleIdHelper.Normalize(pair.Value)))
                    .ToList() ?? new List<KeyValuePair<Guid, string>>();
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "Failed to load titleID cache!");
                return new List<KeyValuePair<Guid, string>>();
            }
        }

        private static string GetTitleIdCachePath(string pluginUserDataPath)
        {
            return Path.Combine(pluginUserDataPath ?? string.Empty, "xenia", "titleID_cache.json");
        }

        private static RarityTier GetRarityFromXboxPoints(int? points)
        {
            var value = Math.Max(0, points ?? 0);
            if (value >= 100)
            {
                return RarityTier.UltraRare;
            }

            if (value >= 50)
            {
                return RarityTier.Rare;
            }

            if (value >= 25)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }
    }
}
