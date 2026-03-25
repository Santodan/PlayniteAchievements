using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using PlayniteAchievements.Providers.RPCS3.Models;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// Represents the trophy source for a game, including both RPCS3 cache and fallback paths.
    /// </summary>
    internal class GameTrophySource
    {
        /// <summary>
        /// The npcommid for the game (e.g., NPWR05920_00).
        /// </summary>
        public string NpCommId { get; set; }

        /// <summary>
        /// Path to TROPHY.TRP file for pre-launch fallback.
        /// </summary>
        public string TrpPath { get; set; }
    }

    /// <summary>
    /// Scanner for RPCS3 PlayStation 3 emulator trophy data.
    /// Orchestrates trophy folder discovery and game matching.
    /// </summary>
    internal sealed class Rpcs3Scanner
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Rpcs3Settings _providerSettings;
        private readonly Rpcs3DataProvider _provider;
        private readonly IPlayniteAPI _playniteApi;

        public Rpcs3Scanner(ILogger logger, PlayniteAchievementsSettings settings, Rpcs3Settings providerSettings, Rpcs3DataProvider provider = null, IPlayniteAPI playniteApi = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _provider = provider;
            _playniteApi = playniteApi;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            // Use the provider's cache if available, otherwise build our own
            Dictionary<string, string> trophyFolderCache;
            if (_provider != null)
            {
                trophyFolderCache = _provider.GetOrBuildTrophyFolderCache();
            }
            else
            {
                trophyFolderCache = await BuildTrophyFolderCacheAsync(cancel).ConfigureAwait(false);
            }

            if (trophyFolderCache == null || trophyFolderCache.Count == 0)
            {
                _logger?.Warn("[RPCS3] No trophy folders found in RPCS3 trophy directory.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            _logger?.Info($"[RPCS3] Scanning {trophyFolderCache.Count} cached trophy folders.");

            var payload = await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                game =>
                {
                    onGameStarting?.Invoke(game);
                },
                async (game, token) =>
                {
                    var data = await FetchGameDataAsync(game, trophyFolderCache, token).ConfigureAwait(false);
                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Error(ex, $"[RPCS3] Failed to scan '{game?.Name}'");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);

            return payload ?? new RebuildPayload { Summary = new RebuildSummary() };
        }

        /// <summary>
        /// Builds a cache mapping npcommid to trophy folder path.
        /// Trophy folder structure: rpcs3_install/trophy/npcommid/
        /// </summary>
        private async Task<Dictionary<string, string>> BuildTrophyFolderCacheAsync(CancellationToken cancel)
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var exePath = _providerSettings?.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return cache;
            }

            // Derive installation folder from executable path
            var installFolder = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(installFolder))
            {
                return cache;
            }

            var trophyPath = Path.Combine(installFolder, "trophy");

            if (!Directory.Exists(trophyPath))
            {
                _logger?.Warn($"[RPCS3] Trophy folder not found at '{trophyPath}'");
                return cache;
            }

            try
            {
                var npcommidDirectories = Directory.GetDirectories(trophyPath);

                foreach (var npcommidDir in npcommidDirectories)
                {
                    cancel.ThrowIfCancellationRequested();

                    var npcommid = Path.GetFileName(npcommidDir);
                    if (string.IsNullOrWhiteSpace(npcommid))
                    {
                        continue;
                    }

                    // Verify TROPCONF.SFM exists
                    var tropconfPath = Path.Combine(npcommidDir, "TROPCONF.SFM");
                    if (File.Exists(tropconfPath))
                    {
                        cache[npcommid] = npcommidDir;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Failed to enumerate trophy directories at '{trophyPath}'");
            }

            return await Task.FromResult(cache).ConfigureAwait(false);
        }

        private Task<GameAchievementData> FetchGameDataAsync(
            Game game,
            Dictionary<string, string> trophyFolderCache,
            CancellationToken cancel)
        {
            if (game == null)
            {
                return Task.FromResult<GameAchievementData>(null);
            }

            // Find npcommid and TRP path for this game
            var source = FindNpCommIdForGame(game, trophyFolderCache, cancel);

            if (source == null || string.IsNullOrWhiteSpace(source.NpCommId))
            {
                return Task.FromResult(BuildNoAchievementsData(game));
            }

            cancel.ThrowIfCancellationRequested();

            // Look up trophy folder in RPCS3 cache
            if (!trophyFolderCache.TryGetValue(source.NpCommId, out var trophyFolderPath))
            {
                // Cache miss - try TROPHY.TRP fallback for pre-launch detection
                if (!string.IsNullOrWhiteSpace(source.TrpPath))
                {
                    _logger?.Debug($"[RPCS3] No cache for '{source.NpCommId}', falling back to TROPHY.TRP at '{source.TrpPath}'");
                    return FetchFromTrpAsync(game, source.NpCommId, source.TrpPath);
                }

                return Task.FromResult(BuildNoAchievementsData(game));
            }

            var tropconfPath = Path.Combine(trophyFolderPath, "TROPCONF.SFM");
            var tropusrPath = Path.Combine(trophyFolderPath, "TROPUSR.DAT");

            if (!File.Exists(tropconfPath))
            {
                return Task.FromResult(BuildNoAchievementsData(game));
            }

            try
            {
                // Map global language to PS3 locale
                var ps3Locale = Rpcs3TrophyParser.MapGlobalLanguageToPs3Locale(_settings?.Persisted?.GlobalLanguage);
                // Parse trophy definitions with language support
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitions(tropconfPath, ps3Locale, _logger);

                // Parse unlock data
                if (File.Exists(tropusrPath))
                {
                    Rpcs3TrophyParser.ParseTrophyUnlockData(tropusrPath, trophies, _logger);
                }

                if (trophies.Count == 0)
                {
                    return Task.FromResult(BuildNoAchievementsData(game));
                }

                // Convert to achievements
                var achievements = new List<AchievementDetail>();

                foreach (var trophy in trophies)
                {
                    var iconPath = GetTrophyIconPath(trophyFolderPath, source.NpCommId, trophy.Id);

                    var normalizedTrophyType = NormalizeTrophyType(trophy.TrophyType);
                    achievements.Add(new AchievementDetail
                    {
                        ApiName = trophy.Id.ToString(),
                        DisplayName = trophy.Name,
                        Description = trophy.Description,
                        UnlockedIconPath = iconPath,
                        LockedIconPath = iconPath,
                        Hidden = trophy.Hidden,
                        Unlocked = trophy.Unlocked,
                        UnlockTimeUtc = trophy.UnlockTimeUtc,
                        GlobalPercentUnlocked = null,
                        Rarity = GetRarityFromTrophyType(normalizedTrophyType),
                        TrophyType = normalizedTrophyType,
                        IsCapstone = normalizedTrophyType == "platinum",
                        CategoryType = MapGroupIdToCategoryType(trophy.GroupId),
                        Category = trophy.GroupName
                    });
                }

                return Task.FromResult(new GameAchievementData
                {
                    ProviderKey = "RPCS3",
                    LibrarySourceName = game?.Source?.Name,
                    GameName = game?.Name,
                    PlayniteGameId = game?.Id,
                    HasAchievements = achievements.Count > 0,
                    Achievements = achievements,
                    LastUpdatedUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Failed to parse trophy data for '{game.Name}'");
                return Task.FromResult(BuildNoAchievementsData(game));
            }
        }

        /// <summary>
        /// Fetches trophy data from TROPHY.TRP file for pre-launch detection.
        /// Used when RPCS3 cache doesn't exist yet (game not launched).
        /// All trophies are marked as locked since unlock data isn't available.
        /// </summary>
        private Task<GameAchievementData> FetchFromTrpAsync(Game game, string npcommid, string trpPath)
        {
            _logger?.Debug($"[RPCS3] FetchFromTrpAsync - Loading pre-launch trophies for '{game?.Name}' from '{trpPath}'");

            if (string.IsNullOrWhiteSpace(trpPath) || !File.Exists(trpPath))
            {
                _logger?.Debug($"[RPCS3] FetchFromTrpAsync - TRP file not found: '{trpPath}'");
                return Task.FromResult(BuildNoAchievementsData(game));
            }

            try
            {
                // Map global language to PS3 locale
                var ps3Locale = Rpcs3TrophyParser.MapGlobalLanguageToPs3Locale(_settings?.Persisted?.GlobalLanguage);

                // Parse trophy definitions from TROPHY.TRP (all locked)
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(trpPath, ps3Locale, _logger);

                if (trophies.Count == 0)
                {
                    _logger?.Debug($"[RPCS3] FetchFromTrpAsync - No trophies found in '{trpPath}'");
                    return Task.FromResult(BuildNoAchievementsData(game));
                }

                // Convert to achievements (all Unlocked = false, icons = null for placeholder)
                var achievements = new List<AchievementDetail>();

                foreach (var trophy in trophies)
                {
                    var normalizedTrophyType = NormalizeTrophyType(trophy.TrophyType);
                    achievements.Add(new AchievementDetail
                    {
                        ApiName = trophy.Id.ToString(),
                        DisplayName = trophy.Name,
                        Description = trophy.Description,
                        // Icons not available pre-launch - UI shows placeholder
                        UnlockedIconPath = null,
                        LockedIconPath = null,
                        Hidden = trophy.Hidden,
                        Unlocked = false, // Pre-launch: all locked
                        UnlockTimeUtc = null,
                        GlobalPercentUnlocked = null,
                        Rarity = GetRarityFromTrophyType(normalizedTrophyType),
                        TrophyType = normalizedTrophyType,
                        IsCapstone = normalizedTrophyType == "platinum",
                        Category = trophy.GroupName
                    });
                }

                _logger?.Info($"[RPCS3] FetchFromTrpAsync - Loaded {achievements.Count} pre-launch trophies for '{game?.Name}' (npcommid: {npcommid})");

                return Task.FromResult(new GameAchievementData
                {
                    ProviderKey = "RPCS3",
                    LibrarySourceName = game?.Source?.Name,
                    GameName = game?.Name,
                    PlayniteGameId = game?.Id,
                    HasAchievements = achievements.Count > 0,
                    Achievements = achievements,
                    LastUpdatedUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] FetchFromTrpAsync - Failed to parse TROPHY.TRP for '{game?.Name}'");
                return Task.FromResult(BuildNoAchievementsData(game));
            }
        }

        // PS3 title/serial ID patterns: BLUS, BLES, BCES, NPUB, NPEB, etc.
        private static readonly System.Text.RegularExpressions.Regex Ps3IdPattern =
            new System.Text.RegularExpressions.Regex(@"\b([A-Z]{2,4}\d{5})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Pattern to extract npcommid from TROPHY.TRP file content
        private static readonly System.Text.RegularExpressions.Regex NpCommIdPattern =
            new System.Text.RegularExpressions.Regex(@"<npcommid>(.*?)<\/npcommid>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Pattern to extract title-name from TROPCONF.SFM
        private static readonly System.Text.RegularExpressions.Regex TitleNamePattern =
            new System.Text.RegularExpressions.Regex(@"<title-name>(.*?)<\/title-name>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Finds the npcommid for a game using multiple strategies:
        /// 1. Extract PS3 ID from the install path (e.g., BLUS12345)
        /// 2. Extract npcommid from PS3 ISO file
        /// 3. Match by game name against TROPCONF.SFM titles
        /// Also returns the TROPHY.TRP path for pre-launch fallback.
        /// </summary>
        private GameTrophySource FindNpCommIdForGame(Game game, Dictionary<string, string> trophyFolderCache, CancellationToken cancel)
        {
            var rawInstallDir = game?.InstallDirectory;
            var gameDirectory = ExpandGamePath(game, rawInstallDir);

            // Strategy 1: Extract PS3 ID from the path and look it up in cache
            if (!string.IsNullOrWhiteSpace(gameDirectory))
            {
                var match = Ps3IdPattern.Match(gameDirectory);

                if (match.Success)
                {
                    var ps3Id = match.Groups[1].Value.ToUpperInvariant();

                    if (trophyFolderCache.ContainsKey(ps3Id))
                    {
                        // Found in cache, but also try to find TRP for fallback
                        var trpPath = FindTrpPathForGameDirectory(gameDirectory);
                        return new GameTrophySource { NpCommId = ps3Id, TrpPath = trpPath };
                    }
                }
            }

            // Strategy 1.5: For installed PKG games, check for TROPHY.TRP in game directory
            if (!string.IsNullOrWhiteSpace(gameDirectory))
            {
                var (npcommid, trpPath) = FindNpCommIdAndTrpFromInstalledGame(gameDirectory, trophyFolderCache);
                if (!string.IsNullOrWhiteSpace(npcommid))
                {
                    return new GameTrophySource { NpCommId = npcommid, TrpPath = trpPath };
                }
            }

            // Strategy 2: Extract npcommid from PS3 ISO file
            var npcommidFromIso = FindNpCommIdFromIso(game, gameDirectory, trophyFolderCache);
            if (!string.IsNullOrWhiteSpace(npcommidFromIso))
            {
                // For ISO games, we can't use TRP fallback (it's inside the ISO)
                return new GameTrophySource { NpCommId = npcommidFromIso, TrpPath = null };
            }

            // Strategy 3: Match by game name against TROPCONF.SFM titles
            var npcommidFromName = FindNpCommIdByName(game, trophyFolderCache);
            if (!string.IsNullOrWhiteSpace(npcommidFromName))
            {
                return new GameTrophySource { NpCommId = npcommidFromName, TrpPath = null };
            }

            return null;
        }

        /// <summary>
        /// Extracts the npcommid from a PS3 ISO file by reading the embedded TROPHY.TRP.
        /// </summary>
        private string FindNpCommIdFromIso(Game game, string gameDirectory,
            Dictionary<string, string> trophyFolderCache)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                return null;
            }

            try
            {
                // Find ISO files in the game directory
                var isoFiles = FindIsoFiles(gameDirectory);
                if (isoFiles.Count == 0)
                {
                    return null;
                }

                foreach (var isoPath in isoFiles)
                {
                    string npcommid = null;

                    // First try DiscUtils (works for ISO9660)
                    try
                    {
                        using (var disc = new DiscUtilsFacade(isoPath))
                        {
                            // Try PS3_GAME/TROPHY/TROPHY.TRP (standard PS3 structure)
                            npcommid = ExtractNpCommIdFromDisc(disc, "PS3_GAME/TROPHY/TROPHY.TRP");

                            // If not found, try just TROPHY/TROPHY.TRP (some structures)
                            if (string.IsNullOrWhiteSpace(npcommid))
                            {
                                npcommid = ExtractNpCommIdFromDisc(disc, "TROPHY/TROPHY.TRP");
                            }
                        }
                    }
                    catch
                    {
                        // DiscUtils failed, likely UDF format
                    }

                    // If DiscUtils failed, try raw byte scanning (works for UDF ISOs)
                    if (string.IsNullOrWhiteSpace(npcommid))
                    {
                        npcommid = ExtractNpCommIdFromRawScan(isoPath);
                    }

                    if (!string.IsNullOrWhiteSpace(npcommid) && trophyFolderCache.ContainsKey(npcommid))
                    {
                        return npcommid;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Error searching for ISO files in '{gameDirectory}'");
            }

            return null;
        }

        /// <summary>
        /// Extracts the npcommid and TROPHY.TRP path from an installed PKG game.
        /// PKG-installed games have TROPHY.TRP at {gameDir}/TROPHY/TROPHY.TRP or
        /// {gameDir}/PS3_GAME/TROPHY/TROPHY.TRP.
        /// Note: Playnite's InstallDirectory often points to USRDIR, but TROPHY is
        /// in the parent game folder (sibling to USRDIR).
        /// </summary>
        private (string npcommid, string trpPath) FindNpCommIdAndTrpFromInstalledGame(string gameDirectory,
            Dictionary<string, string> trophyFolderCache)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                return (null, null);
            }

            // Build list of directories to check
            // Playnite may point to USRDIR, but TROPHY folder is in the game root
            var directoriesToCheck = new List<string> { gameDirectory };

            // If path ends with USRDIR, also check parent directory
            var normalizedPath = gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalizedPath.EndsWith("USRDIR", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Path.GetDirectoryName(normalizedPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    directoriesToCheck.Add(parentDir);
                }
            }

            // Possible TROPHY.TRP locations for installed games
            // PKG games: {game_root}/TROPDIR/{npcommid}/TROPHY.TRP
            // Disc-based games: {game_root}/TROPHY/TROPHY.TRP or {game_root}/PS3_GAME/TROPHY/TROPHY.TRP
            var trpPaths = new List<string>();
            foreach (var dir in directoriesToCheck)
            {
                // PKG games: TROPDIR contains subdirectories named after npcommid
                var tropdir = Path.Combine(dir, "TROPDIR");
                if (Directory.Exists(tropdir))
                {
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(tropdir))
                        {
                            var trpPath = Path.Combine(subDir, "TROPHY.TRP");
                            if (File.Exists(trpPath))
                            {
                                trpPaths.Add(trpPath);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore errors scanning TROPDIR
                    }
                }

                // Disc-based game paths
                trpPaths.Add(Path.Combine(dir, "TROPHY", "TROPHY.TRP"));
                trpPaths.Add(Path.Combine(dir, "PS3_GAME", "TROPHY", "TROPHY.TRP"));
            }

            foreach (var trpPath in trpPaths)
            {
                if (!File.Exists(trpPath))
                {
                    continue;
                }

                try
                {
                    var npcommid = ExtractNpCommIdFromTrpFile(trpPath);
                    // Return if found in cache OR if we just have a valid TRP file (for pre-launch fallback)
                    if (!string.IsNullOrWhiteSpace(npcommid))
                    {
                        if (trophyFolderCache.ContainsKey(npcommid))
                        {
                            return (npcommid, trpPath);
                        }
                        // Not in cache but valid TRP - still return for pre-launch fallback
                        return (npcommid, trpPath);
                    }
                }
                catch
                {
                    // Ignore errors reading TROPHY.TRP
                }
            }

            return (null, null);
        }

        /// <summary>
        /// Finds the TROPHY.TRP path for a game directory.
        /// Used for pre-launch trophy detection when RPCS3 cache doesn't exist yet.
        /// </summary>
        /// <param name="gameDirectory">The game installation directory.</param>
        /// <returns>Path to TROPHY.TRP file, or null if not found.</returns>
        internal string FindTrpPathForGameDirectory(string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                return null;
            }

            // Build list of directories to check
            var directoriesToCheck = new List<string> { gameDirectory };

            // If path ends with USRDIR, also check parent directory
            var normalizedPath = gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalizedPath.EndsWith("USRDIR", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Path.GetDirectoryName(normalizedPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    directoriesToCheck.Add(parentDir);
                }
            }

            foreach (var dir in directoriesToCheck)
            {
                // PKG games: TROPDIR contains subdirectories named after npcommid
                var tropdir = Path.Combine(dir, "TROPDIR");
                if (Directory.Exists(tropdir))
                {
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(tropdir))
                        {
                            var trpPath = Path.Combine(subDir, "TROPHY.TRP");
                            if (File.Exists(trpPath))
                            {
                                return trpPath;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore errors scanning TROPDIR
                    }
                }

                // Disc-based game: TROPHY/TROPHY.TRP
                var discTrpPath = Path.Combine(dir, "TROPHY", "TROPHY.TRP");
                if (File.Exists(discTrpPath))
                {
                    return discTrpPath;
                }

                // Alternative disc structure: PS3_GAME/TROPHY/TROPHY.TRP
                var altDiscTrpPath = Path.Combine(dir, "PS3_GAME", "TROPHY", "TROPHY.TRP");
                if (File.Exists(altDiscTrpPath))
                {
                    return altDiscTrpPath;
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts the npcommid from a TROPHY.TRP file on disk.
        /// </summary>
        private string ExtractNpCommIdFromTrpFile(string trpPath)
        {
            using (var stream = new FileStream(trpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                // TROPHY.TRP contains XML with <npcommid> tag
                // Read enough lines to find the npcommid (usually near the top)
                for (int i = 0; i < 30; i++)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;

                    var match = NpCommIdPattern.Match(line);
                    if (match.Success)
                    {
                        return match.Groups[1].Value?.Trim();
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Scans a raw ISO file for the npcommid pattern.
        /// This works for UDF-format PS3 ISOs without needing UDF parsing libraries.
        /// The npcommid appears in TROPHY.TRP as XML: &lt;npcommid>NPWR05636_00&lt;/npcommid>
        /// </summary>
        private string ExtractNpCommIdFromRawScan(string isoPath)
        {
            try
            {
                // Read the ISO file in chunks, looking for <npcommid>...</npcommid>
                // The TROPHY.TRP file is typically within the first 100MB of the ISO
                var searchPattern = System.Text.Encoding.ASCII.GetBytes("<npcommid>");
                var endPattern = System.Text.Encoding.ASCII.GetBytes("</npcommid>");
                var maxSearchBytes = 100L * 1024 * 1024; // 100MB max search

                using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var fileSize = fs.Length;
                    var searchLimit = Math.Min(fileSize, maxSearchBytes);
                    var buffer = new byte[64 * 1024]; // 64KB buffer
                    var overlap = new byte[256]; // For patterns spanning buffer boundaries
                    var overlapCount = 0;

                    long position = 0;
                    while (position < searchLimit)
                    {
                        var bytesToRead = (int)Math.Min(buffer.Length, searchLimit - position);
                        var bytesRead = fs.Read(buffer, 0, bytesToRead);
                        if (bytesRead == 0) break;

                        // Combine overlap from previous chunk with current buffer
                        var searchBuffer = new byte[overlapCount + bytesRead];
                        if (overlapCount > 0)
                        {
                            Array.Copy(overlap, 0, searchBuffer, 0, overlapCount);
                        }
                        Array.Copy(buffer, 0, searchBuffer, overlapCount, bytesRead);

                        // Search for <npcommid> pattern using byte-by-byte comparison
                        for (int i = 0; i <= searchBuffer.Length - searchPattern.Length - 20; i++)
                        {
                            // Check for start pattern
                            bool match = true;
                            for (int p = 0; p < searchPattern.Length; p++)
                            {
                                if (searchBuffer[i + p] != searchPattern[p])
                                {
                                    match = false;
                                    break;
                                }
                            }

                            if (match)
                            {
                                // Find end pattern
                                for (int j = searchPattern.Length; j < searchBuffer.Length - i - endPattern.Length; j++)
                                {
                                    bool endMatch = true;
                                    for (int e = 0; e < endPattern.Length; e++)
                                    {
                                        if (searchBuffer[i + j + e] != endPattern[e])
                                        {
                                            endMatch = false;
                                            break;
                                        }
                                    }

                                    if (endMatch)
                                    {
                                        // Extract the npcommid value
                                        var startIndex = i + searchPattern.Length;
                                        var length = j - searchPattern.Length;
                                        var npcommidBytes = new byte[length];
                                        Array.Copy(searchBuffer, startIndex, npcommidBytes, 0, length);
                                        var npcommid = System.Text.Encoding.ASCII.GetString(npcommidBytes).Trim();

                                        return npcommid;
                                    }
                                }
                            }
                        }

                        // Save last bytes for overlap handling
                        overlapCount = Math.Min(256, bytesRead);
                        Array.Copy(buffer, bytesRead - overlapCount, overlap, 0, overlapCount);

                        position += bytesRead;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Error scanning ISO '{isoPath}'");
            }

            return null;
        }

        /// <summary>
        /// Extracts the npcommid from a TROPHY.TRP file inside a disc image.
        /// </summary>
        private string ExtractNpCommIdFromDisc(DiscUtilsFacade disc, string trpPath)
        {
            if (!disc.FileExists(trpPath))
            {
                return null;
            }

            using (var stream = disc.OpenFileOrNull(trpPath))
            {
                if (stream == null)
                {
                    return null;
                }

                using (var reader = new StreamReader(stream))
                {
                    // TROPHY.TRP contains XML with <npcommid> tag
                    // Read enough to find the npcommid (usually near the top)
                    for (int i = 0; i < 20; i++)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;

                        var match = NpCommIdPattern.Match(line);
                        if (match.Success)
                        {
                            return match.Groups[1].Value?.Trim();
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Finds ISO files in the specified directory.
        /// </summary>
        private List<string> FindIsoFiles(string directory)
        {
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return results;
            }

            try
            {
                // Check if the directory itself is pointing to an ISO
                var files = Directory.GetFiles(directory, "*.iso");
                results.AddRange(files);
            }
            catch
            {
                // Ignore errors
            }

            return results;
        }

        /// <summary>
        /// Attempts to match a game by name against the titles in TROPCONF.SFM files.
        /// This is useful for ISO-based games where the PS3 ID cannot be extracted from the path.
        /// </summary>
        private string FindNpCommIdByName(Game game, Dictionary<string, string> trophyFolderCache)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            var normalizedGameName = NormalizeGameName(game.Name);
            if (string.IsNullOrWhiteSpace(normalizedGameName))
            {
                return null;
            }

            string bestMatch = null;
            int bestScore = 0;

            foreach (var kvp in trophyFolderCache)
            {
                var npcommid = kvp.Key;
                var trophyFolder = kvp.Value;

                var titleName = ExtractTitleNameFromTropconf(trophyFolder);
                if (string.IsNullOrWhiteSpace(titleName))
                {
                    continue;
                }

                var normalizedTitle = NormalizeGameName(titleName);
                if (string.IsNullOrWhiteSpace(normalizedTitle))
                {
                    continue;
                }

                // Check if one name contains the other (handles subtitle differences)
                var score = CalculateNameSimilarity(normalizedGameName, normalizedTitle);
                if (score > bestScore && score >= 70) // Require at least 70% similarity
                {
                    bestScore = score;
                    bestMatch = npcommid;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Extracts the title-name from a TROPCONF.SFM file.
        /// </summary>
        private string ExtractTitleNameFromTropconf(string trophyFolder)
        {
            if (string.IsNullOrWhiteSpace(trophyFolder))
            {
                return null;
            }

            try
            {
                var tropconfPath = Path.Combine(trophyFolder, "TROPCONF.SFM");
                if (!File.Exists(tropconfPath))
                {
                    return null;
                }

                // Read only the first few KB to find the title (title is near the top)
                using (var stream = new FileStream(tropconfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    // Read enough lines to find the title
                    for (int i = 0; i < 20; i++)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;

                        var match = TitleNamePattern.Match(line);
                        if (match.Success)
                        {
                            return match.Groups[1].Value?.Trim();
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        }

        /// <summary>
        /// Normalizes a game name for comparison by removing special characters,
        /// normalizing whitespace, and converting to lowercase.
        /// </summary>
        private static string NormalizeGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            // Remove common suffixes/prefixes
            var normalized = System.Text.RegularExpressions.Regex.Replace(
                name,
                @"\s*[-:]\s*(PlayStation\s*)?(PS[1234])\s*(Edition|Version|Demo|Beta|Trial|Region\s*Free)?",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remove registered/trademark symbols
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[®™©]", "");

            // Remove content in parentheses (region info, language codes, etc.)
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\([^)]*\)", "");

            // Remove content in brackets
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\[[^\]]*\]", "");

            // Remove file extensions
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\.(iso|pkg|rap)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remove special characters, keep alphanumeric and spaces
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^a-zA-Z0-9\s]", " ");

            // Separate digits from letters (e.g., "PlayStation3" -> "PlayStation 3")
            // This handles cases like "PlayStation3 Edition" vs "PlayStation 3 Edition"
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"(\d)([a-zA-Z])", "$1 $2");
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"([a-zA-Z])(\d)", "$1 $2");

            // Normalize whitespace
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();

            return normalized.ToLowerInvariant().Trim();
        }

        /// <summary>
        /// Calculates a similarity score (0-100) between two normalized game names.
        /// </summary>
        private static int CalculateNameSimilarity(string name1, string name2)
        {
            if (string.IsNullOrWhiteSpace(name1) || string.IsNullOrWhiteSpace(name2))
            {
                return 0;
            }

            // Exact match
            if (string.Equals(name1, name2, StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            // Check if one contains the other (handles subtitle differences)
            if (name1.Contains(name2) || name2.Contains(name1))
            {
                var longerLength = Math.Max(name1.Length, name2.Length);
                var shorterLength = Math.Min(name1.Length, name2.Length);
                return (int)((double)shorterLength / longerLength * 100);
            }

            // Word-based matching
            var words1 = name1.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var words2 = name2.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (words1.Length == 0 || words2.Length == 0)
            {
                return 0;
            }

            var set1 = new HashSet<string>(words1, StringComparer.OrdinalIgnoreCase);
            var set2 = new HashSet<string>(words2, StringComparer.OrdinalIgnoreCase);
            var intersection = new HashSet<string>(set1, StringComparer.OrdinalIgnoreCase);
            intersection.IntersectWith(set2);

            if (intersection.Count == 0)
            {
                return 0;
            }

            // Calculate Jaccard-like similarity with bonus for matching key words
            var union = new HashSet<string>(set1, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(set2);

            var jaccardScore = (double)intersection.Count / union.Count * 100;

            // Bonus if the first word matches (usually the main title)
            if (string.Equals(words1[0], words2[0], StringComparison.OrdinalIgnoreCase))
            {
                jaccardScore = Math.Min(100, jaccardScore * 1.3);
            }

            return (int)jaccardScore;
        }

        /// <summary>
        /// Expands path variables in game paths using Playnite's variable expansion.
        /// </summary>
        private string ExpandGamePath(Game game, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            // Use provider's expansion if available
            if (_provider != null)
            {
                return _provider.ExpandGamePath(game, path);
            }

            // Fallback: use Playnite API directly if available
            try
            {
                return _playniteApi?.ExpandGameVariables(game, path) ?? path;
            }
            catch
            {
                return path;
            }
        }

        private static GameAchievementData BuildNoAchievementsData(Game game)
        {
            return new GameAchievementData
            {
                ProviderKey = "RPCS3",
                LibrarySourceName = game?.Source?.Name,
                GameName = game?.Name,
                PlayniteGameId = game?.Id,
                HasAchievements = false,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        private static string NormalizeTrophyType(string trophyType)
        {
            if (string.IsNullOrWhiteSpace(trophyType))
            {
                return null;
            }

            return trophyType.ToUpperInvariant() switch
            {
                "P" => "platinum",
                "G" => "gold",
                "S" => "silver",
                "B" => "bronze",
                _ => null
            };
        }

        private static RarityTier GetRarityFromTrophyType(string trophyType)
        {
            switch ((trophyType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "platinum":
                case "p":
                    return RarityTier.UltraRare;
                case "gold":
                case "g":
                    return RarityTier.Rare;
                case "silver":
                case "s":
                    return RarityTier.Uncommon;
                default:
                    return RarityTier.Common;
            }
        }

        private static string MapGroupIdToCategoryType(string groupId)
        {
            var normalized = (groupId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "000", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "base", StringComparison.OrdinalIgnoreCase))
            {
                return "Base";
            }

            return "DLC";
        }

        /// <summary>
        /// Gets the trophy icon path from the RPCS3 trophy folder.
        /// Returns the direct source path; icon caching is handled centrally by DiskImageService.
        /// </summary>
        private string GetTrophyIconPath(string trophyFolderPath, string npcommid, int trophyId)
        {
            if (string.IsNullOrWhiteSpace(trophyFolderPath))
            {
                return null;
            }

            try
            {
                // Trophy icons follow TROP###.PNG format with zero-padded ID
                var iconFileName = $"TROP{trophyId.ToString().PadLeft(3, '0')}.PNG";
                var sourcePath = Path.Combine(trophyFolderPath, iconFileName);

                if (!File.Exists(sourcePath))
                {
                    return null;
                }

                return sourcePath;
            }
            catch
            {
                return null;
            }
        }
    }
}
