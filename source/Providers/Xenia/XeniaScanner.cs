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
using System.Text;
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
        List<string> KnownPublishers = new List<string>() { "5444", "464F", "4143", "4156", "4158", "4142", "4144", "4150", "4151", "4157", "414B", "4148", "4153", "4159", "4154", "424D", "4241", "4257", "4253", "4242", "4248", "4246", "4245", "4247", "4254", "4244", "4252", "4256", "4255", "4343", "434D", "4356", "4354", "4458", "4445", "4443", "4546", "4553", "4541", "454D", "4543", "454C", "4556", "464C", "4649", "4653", "4746", "4745", "4756", "4857", "4850", "4845", "4855", "4946", "494F", "494D", "4947", "494C", "4950", "4958", "4A41", "4A57", "4B59", "4B4F", "4B4E", "4B41", "4B54", "4C41", "4D4A", "4D45", "4D44", "4D53", "4D57", "4D4D", "4E4D", "4E4B", "4E4C", "4F47", "4F58", "5058", "504C", "5043", "5241", "5341", "5343", "5345", "5353", "534E", "5350", "5351", "5354", "5355", "5357", "5441", "5454", "544B", "544D", "5443", "5451", "5453", "5553", "5647", "5656", "5643", "5655", "5745", "5752", "584B", "584C", "5841", "5849", "5850", "5942", "5A44", "4450", "394F", "4C53", "4656", "3734", "4133", "545A", "435A", "4346", "4D4B", "434E", "4436", "5A45", "4645" };

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

            var rarityEnricher = await CreateRarityEnricherAsync(cancel).ConfigureAwait(false);

            try
            {
                return await ProviderRefreshExecutor.RunProviderGamesAsync(
                    gamesToRefresh,
                    onGameStarting,
                    async (game, token) =>
                    {
                        var data = GetAchievementData(game);
                        await EnrichRarityAsync(game, data, rarityEnricher, token).ConfigureAwait(false);
                        return new ProviderRefreshExecutor.ProviderGameResult
                        {
                            Data = data
                        };
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
            finally
            {
                rarityEnricher?.Dispose();
            }
        }

        private GameAchievementData GetAchievementData(Game game)
        {
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
                    PlayniteGameId = game?.Id,
                    Achievements = achievements
                };
            }

            SaveTitleIdCache();

            return data;
        }

        private async Task<ExophaseMetadataEnricher> CreateRarityEnricherAsync(CancellationToken cancel)
        {
            if (_providerSettings?.UseExophaseForRarity != true)
            {
                return null;
            }

            var enricher = new ExophaseMetadataEnricher(_playniteApi, _logger, _settings, _pluginUserDataPath);
            await enricher.InitializeAsync(cancel).ConfigureAwait(false);
            return enricher;
        }

        private static async Task EnrichRarityAsync(
            Game game,
            GameAchievementData data,
            ExophaseMetadataEnricher rarityEnricher,
            CancellationToken cancel)
        {
            if (rarityEnricher == null || data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            await rarityEnricher.EnrichAsync(game, data.Achievements, "xbox-360", "Xbox", cancel).ConfigureAwait(false);
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

            // Try to find TitleID in file
            int exeAreaSize = 300;
            foreach (var path in candidatePaths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(Path.GetExtension(path)))
                {
                    var chunksize = 8 * 1024; // 8 KB buffer
                    var buffer = new byte[chunksize];
                    // Carries the tail of the previous read so a marker straddling a read boundary is still found
                    var previousbuffer = new byte[chunksize];
                    byte[] combinedbuffer = new byte[chunksize * 2];
                    byte[] exeChunk = new byte[exeAreaSize];
                    byte[] exeMarker = Encoding.UTF8.GetBytes(".exe");
                    byte[] peMarker = Encoding.UTF8.GetBytes(".pe");

                    // Playnite is a 32-bit process; the file must be streamed, never memory-mapped,
                    // because a large contiguous view reservation exhausts virtual address space
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, chunksize, FileOptions.SequentialScan);

                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        Array.Copy(previousbuffer, combinedbuffer, previousbuffer.Length);
                        Array.Copy(buffer, 0, combinedbuffer, chunksize, bytesRead);

                        var combinedLength = previousbuffer.Length + bytesRead;
                        var foundexe = IndexOf(combinedbuffer, combinedLength, exeMarker);
                        var foundpe = IndexOf(combinedbuffer, combinedLength, peMarker);

                        if (foundexe >= exeAreaSize)
                        {
                            // Pull the previous 300 characters and convert to char array (300 is arbitry just to account for possible lots of data between titleID and .exe entry)
                            Array.Copy(combinedbuffer, foundexe - exeAreaSize, exeChunk, 0, exeAreaSize);

                            var temptitleID = CheckChunk(ref exeChunk);
                            if (!string.IsNullOrEmpty(temptitleID))
                            {
                                titleID = temptitleID;
                                CacheTitleId(game.Id, temptitleID);
                                return true;

                            }
                        }
                        if (foundpe >= exeAreaSize)
                        {
                            Array.Copy(combinedbuffer, foundpe - exeAreaSize, exeChunk, 0, exeAreaSize);

                            var temptitleID = CheckChunk(ref exeChunk);
                            if (!string.IsNullOrEmpty(temptitleID))
                            {
                                titleID = temptitleID;
                                CacheTitleId(game.Id, temptitleID);
                                return true;
                            }
                        }

                        Array.Clear(previousbuffer, 0, previousbuffer.Length);
                        var tailCount = Math.Min(previousbuffer.Length, bytesRead);
                        Array.Copy(buffer, bytesRead - tailCount, previousbuffer, previousbuffer.Length - tailCount, tailCount);
                    }
                }
                else
                {
                    _logger.Error("[Xenia] Unsupported ROM only .xex, .iso, or extensionless package files are supported!");
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

        private string CheckChunk(ref byte[] chunk)
        {
            byte[] publisherCheck = new byte[4];
            // Im not sure if this is 100% accurate but out of 28/28 ROMs tested passed taking on average 100ms to find! (.iso)
            // This will take longer with larger files and if the title ID is at the end of the file (Longest i've seen is 11s, maybe it could be multi-threaded?)
            for (int i = 0; i < chunk.Length; i++)
            {
                if (i + 8 > chunk.Length - 1)
                {
                    break;
                }

                // Check for publisher code
                publisherCheck[0] = chunk[i];
                publisherCheck[1] = chunk[i + 1];
                publisherCheck[2] = chunk[i + 2];
                publisherCheck[3] = chunk[i + 3];
                bool passedcheck = KnownPublishers.Any(x => x == System.Text.Encoding.UTF8.GetString(publisherCheck, 0, 4));
                if (!passedcheck)
                    continue;       

                passedcheck &= char.IsDigit((char)chunk[i + 4]) || char.IsUpper((char)chunk[i + 4]);
                passedcheck &= char.IsDigit((char)chunk[i + 5]) || char.IsUpper((char)chunk[i + 5]);
                passedcheck &= char.IsDigit((char)chunk[i + 6]) || char.IsUpper((char)chunk[i + 6]);
                passedcheck &= char.IsDigit((char)chunk[i + 7]) || char.IsUpper((char)chunk[i + 7]);

                if (!passedcheck)
                {
                    continue;
                }
                else
                {
                    return System.Text.Encoding.UTF8.GetString(chunk, i, 8);
                }
            }

            return "";
        }

        private static int IndexOf(byte[] buffer, int bytesRead, byte[] pattern)
        {
            if (buffer == null || pattern == null || pattern.Length == 0 || bytesRead < pattern.Length)
            {
                return -1;
            }

            for (var i = 0; i <= bytesRead - pattern.Length; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
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
