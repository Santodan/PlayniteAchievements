using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Common;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.ShadPS4
{
    internal sealed class ShadPS4DataProvider : IDataProvider
    {
        private readonly ShadPS4Scanner _scanner;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private ShadPS4Settings _providerSettings;

        private Dictionary<string, string> _titleCache;
        private readonly object _cacheLock = new object();

        private Dictionary<string, string> _npCommIdCache;
        private readonly object _npCommIdCacheLock = new object();

        private static readonly Regex NpCommIdPattern =
            new Regex(@"(NPWR\d{5}_\d{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ShadPS4DataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _settings = settings;
            _logger = logger;
            _playniteApi = playniteApi;

            _providerSettings = ProviderRegistry.Settings<ShadPS4Settings>();
            _scanner = new ShadPS4Scanner(_logger, _settings, _providerSettings, this, _playniteApi);
        }

        public string ProviderName
        {
            get
            {
                var value = ResourceProvider.GetString("LOCPlayAch_Provider_ShadPS4");
                return string.IsNullOrWhiteSpace(value) ? "ShadPS4" : value;
            }
        }

        public string ProviderKey => "ShadPS4";

        public string ProviderIconKey => "ProviderIconShadPS4";

        public string ProviderColorHex => "#752bfd";

        public ISessionManager AuthSession => null;

        public bool IsAuthenticated
        {
            get
            {
                var gameDataPath = GetGameDataPath();
                if (!string.IsNullOrWhiteSpace(gameDataPath) && Directory.Exists(gameDataPath))
                {
                    return true;
                }

                var appDataPath = GetAppDataPath();
                if (!string.IsNullOrWhiteSpace(appDataPath))
                {
                    var trophyUserPath = GetTrophyUserPath(appDataPath);
                    if (!string.IsNullOrWhiteSpace(trophyUserPath) && Directory.Exists(trophyUserPath))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Gets the game data path using priority order:
        /// 1. User settings (validated game_data folder)
        /// 2. Game's emulator config
        /// 3. First ShadPS4 emulator in database
        /// </summary>
        public string GetGameDataPath(Game game = null)
        {
            // Priority 1: From provider settings (user-configured game_data path)
            var settingsGameDataPath = _providerSettings?.GameDataPath;
            if (!string.IsNullOrWhiteSpace(settingsGameDataPath) && Directory.Exists(settingsGameDataPath))
            {
                return settingsGameDataPath;
            }

            // Priority 2: From game's emulator config
            var emulatorRoot = GetEmulatorRootFromGame(game);
            if (!string.IsNullOrWhiteSpace(emulatorRoot))
            {
                var gameDataPath = Path.Combine(emulatorRoot, "user", "game_data");
                if (Directory.Exists(gameDataPath))
                {
                    return gameDataPath;
                }
            }

            // Priority 3: From first ShadPS4 emulator in database
            emulatorRoot = FindAnyShadps4EmulatorRoot();
            if (!string.IsNullOrWhiteSpace(emulatorRoot))
            {
                var gameDataPath = Path.Combine(emulatorRoot, "user", "game_data");
                return gameDataPath;
            }

            return null;
        }

        /// <summary>
        /// Gets the ShadPS4 emulator root from the game's emulator action.
        /// </summary>
        private string GetEmulatorRootFromGame(Game game)
        {
            if (game?.GameActions == null) return null;

            foreach (var action in game.GameActions)
            {
                if (action?.Type == GameActionType.Emulator && action.EmulatorId != Guid.Empty)
                {
                    var emulator = _playniteApi?.Database?.Emulators?.Get(action.EmulatorId);
                    if (emulator != null && !string.IsNullOrWhiteSpace(emulator.InstallDir))
                    {
                        // Check if this looks like ShadPS4
                        if (IsShadps4Emulator(emulator))
                        {
                            return emulator.InstallDir;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Checks if an emulator is ShadPS4 by built-in ID, name, or install directory.
        /// </summary>
        private bool IsShadps4Emulator(Emulator emulator)
        {
            var builtInId = emulator.BuiltInConfigId ?? string.Empty;
            var name = emulator.Name ?? string.Empty;
            var installDir = emulator.InstallDir ?? string.Empty;
            return builtInId.IndexOf("shadps4", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("shadps4", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   installDir.IndexOf("shadps4", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Finds any ShadPS4 emulator in the Playnite database.
        /// </summary>
        private string FindAnyShadps4EmulatorRoot()
        {
            var emulators = _playniteApi?.Database?.Emulators;
            if (emulators == null) return null;

            foreach (var emulator in emulators)
            {
                if (IsShadps4Emulator(emulator) && !string.IsNullOrWhiteSpace(emulator.InstallDir))
                {
                    return emulator.InstallDir;
                }
            }
            return null;
        }

        // PS4 title ID patterns: CUSA (US), BCAS (Asia), PCAS (Asia digital), CUSA (Europe), etc.
        private static readonly System.Text.RegularExpressions.Regex TitleIdPattern =
            new System.Text.RegularExpressions.Regex(@"\b([A-Z]{4}\d{5})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public bool IsCapable(Game game)
        {
            if (game == null) return false;

            // If game is configured to use ShadPS4 emulator, it's capable
            if (UsesShadPS4Emulator(game))
            {
                return true;
            }

            // Otherwise, check if we can find trophy data by title ID or npcommid
            var titleId = ExtractTitleIdFromGame(game);
            if (!string.IsNullOrWhiteSpace(titleId))
            {
                var cache = GetOrBuildTitleCache();
                if (cache != null && cache.ContainsKey(titleId))
                {
                    return true;
                }
            }

            var npcommid = ResolveNpCommIdForGame(game);
            if (!string.IsNullOrWhiteSpace(npcommid))
            {
                var npCommIdCache = GetOrBuildNpCommIdCache();
                if (npCommIdCache != null && npCommIdCache.ContainsKey(npcommid))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if any game action uses a ShadPS4 emulator.
        /// Checks by: 1) Path matching settings, 2) Emulator name containing "shadps4"
        /// </summary>
        private bool UsesShadPS4Emulator(Game game)
        {
            if (game?.GameActions == null) return false;

            // Get settings path for comparison - derive emulator root from game_data path
            var shadps4GameDataPath = _providerSettings?.GameDataPath;
            string shadps4InstallFolder = null;
            if (!string.IsNullOrWhiteSpace(shadps4GameDataPath))
            {
                // game_data is under user/, so go up two levels to get emulator root
                shadps4InstallFolder = Path.GetDirectoryName(Path.GetDirectoryName(shadps4GameDataPath));
            }

            foreach (var action in game.GameActions)
            {
                if (action?.Type == GameActionType.Emulator && action.EmulatorId != Guid.Empty)
                {
                    var emulator = _playniteApi?.Database?.Emulators?.Get(action.EmulatorId);
                    if (emulator == null || string.IsNullOrWhiteSpace(emulator.InstallDir))
                    {
                        continue;
                    }

                    // Check by emulator name (more reliable than path matching)
                    if (IsShadps4Emulator(emulator))
                    {
                        return true;
                    }

                    // Also check by path matching settings (if settings path is configured)
                    if (!string.IsNullOrWhiteSpace(shadps4InstallFolder) &&
                        PathsEqual(emulator.InstallDir, shadps4InstallFolder))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool PathsEqual(string path1, string path2)
        {
            if (string.IsNullOrWhiteSpace(path1) || string.IsNullOrWhiteSpace(path2))
            {
                return false;
            }

            try
            {
                var full1 = Path.GetFullPath(path1.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var full2 = Path.GetFullPath(path2.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.Equals(full1, full2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(path1.Trim(), path2.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Extracts the PS4 title ID from the game's install directory path.
        /// PS4 title IDs follow pattern: AAAA12345 (e.g., CUSA00432)
        /// </summary>
        private string ExtractTitleIdFromGame(Game game)
        {
            var rawInstallDir = game?.InstallDirectory;
            var installDir = ExpandGamePath(game, rawInstallDir);

            if (string.IsNullOrWhiteSpace(installDir))
            {
                return null;
            }

            // Search for title ID pattern in the path
            var match = TitleIdPattern.Match(installDir);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpperInvariant();
            }

            return null;
        }

        internal Dictionary<string, string> GetOrBuildTitleCache()
        {
            lock (_cacheLock)
            {
                if (_titleCache != null)
                {
                    return _titleCache;
                }
                _titleCache = BuildTitleIdCache();
                return _titleCache;
            }
        }

        internal void ClearTitleCache()
        {
            lock (_cacheLock)
            {
                _titleCache = null;
            }
        }

        /// <summary>
        /// Builds a cache of title ID to trophy data directory path.
        /// Cache structure: title_id (e.g., "CUSA00432") -> full path to game_data directory
        /// </summary>
        private Dictionary<string, string> BuildTitleIdCache()
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var gameDataPath = GetGameDataPath();
            if (string.IsNullOrWhiteSpace(gameDataPath))
            {
                return cache;
            }

            if (!Directory.Exists(gameDataPath))
            {
                return cache;
            }

            try
            {
                var titleDirectories = Directory.GetDirectories(gameDataPath);
                foreach (var titleDir in titleDirectories)
                {
                    var titleId = Path.GetFileName(titleDir);
                    if (string.IsNullOrWhiteSpace(titleId))
                    {
                        continue;
                    }

                    // Verify trophy data exists
                    var xmlPath = Path.Combine(titleDir, "trophyfiles", "trophy00", "Xml", "TROP.XML");
                    if (File.Exists(xmlPath))
                    {
                        cache[titleId.ToUpperInvariant()] = titleDir;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ShadPS4] Failed to enumerate title directories.");
            }

            return cache;
        }

        /// <summary>
        /// Expands path variables in game paths using Playnite's variable expansion.
        /// </summary>
        internal string ExpandGamePath(Game game, string path)
        {
            return PathExpansion.ExpandGamePath(_playniteApi, game, path);
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is ShadPS4Settings shadps4Settings)
            {
                _providerSettings.CopyFrom(shadps4Settings);
            }
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new ShadPS4SettingsView(_playniteApi);

        /// <summary>
        /// Auto-discovers the shadPS4 AppData directory.
        /// </summary>
        internal string GetAppDataPath()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var shadps4Path = Path.Combine(appData, "shadPS4");
                if (Directory.Exists(shadps4Path))
                {
                    return shadps4Path;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Discovers the shadPS4 user ID by scanning the home directory.
        /// Defaults to "1000" if no users are found.
        /// </summary>
        internal string GetUserId()
        {
            var appDataPath = GetAppDataPath();
            if (!string.IsNullOrWhiteSpace(appDataPath))
            {
                var homePath = Path.Combine(appDataPath, "home");
                if (Directory.Exists(homePath))
                {
                    try
                    {
                        var dirs = Directory.GetDirectories(homePath);
                        if (dirs.Length > 0)
                        {
                            return Path.GetFileName(dirs[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        }
                    }
                    catch { }
                }
            }

            return "1000";
        }

        /// <summary>
        /// Returns the per-user trophy XML directory path: home/{userId}/trophy/
        /// </summary>
        internal string GetTrophyUserPath(string appDataPath = null)
        {
            var basePath = appDataPath ?? GetAppDataPath();
            if (string.IsNullOrWhiteSpace(basePath)) return null;

            var userId = GetUserId();
            return Path.Combine(basePath, "home", userId, "trophy");
        }

        /// <summary>
        /// Returns the shared trophy base directory path: trophy/
        /// Contains subdirectories per npcommid with Xml/ and Icons/.
        /// </summary>
        internal string GetTrophyBasePath(string appDataPath = null)
        {
            var basePath = appDataPath ?? GetAppDataPath();
            if (string.IsNullOrWhiteSpace(basePath)) return null;

            return Path.Combine(basePath, "trophy");
        }

        internal Dictionary<string, string> GetOrBuildNpCommIdCache()
        {
            lock (_npCommIdCacheLock)
            {
                if (_npCommIdCache != null)
                {
                    return _npCommIdCache;
                }
                _npCommIdCache = BuildNpCommIdCache();
                return _npCommIdCache;
            }
        }

        internal void ClearNpCommIdCache()
        {
            lock (_npCommIdCacheLock)
            {
                _npCommIdCache = null;
            }
        }

        /// <summary>
        /// Builds a cache of npcommid to per-user trophy XML path.
        /// Scans home/{userId}/trophy/ for XML files named by npcommid.
        /// </summary>
        private Dictionary<string, string> BuildNpCommIdCache()
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var userTrophyPath = GetTrophyUserPath();
            if (string.IsNullOrWhiteSpace(userTrophyPath) || !Directory.Exists(userTrophyPath))
            {
                return cache;
            }

            try
            {
                var files = Directory.GetFiles(userTrophyPath, "*.xml");
                foreach (var file in files)
                {
                    var npcommid = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrWhiteSpace(npcommid) && NpCommIdPattern.IsMatch(npcommid))
                    {
                        cache[npcommid.ToUpperInvariant()] = file;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ShadPS4] Failed to enumerate new-format trophy files.");
            }

            return cache;
        }

        /// <summary>
        /// Resolves the npcommid for a game by parsing its sce_sys/npbind.dat file.
        /// </summary>
        internal string ResolveNpCommIdForGame(Game game)
        {
            var rawInstallDir = game?.InstallDirectory;
            var installDir = ExpandGamePath(game, rawInstallDir);
            if (string.IsNullOrWhiteSpace(installDir)) return null;

            var searchDirs = new List<string> { installDir };

            var normalized = installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentDir = Path.GetDirectoryName(normalized);
            if (!string.IsNullOrWhiteSpace(parentDir) && !PathsEqual(parentDir, installDir))
            {
                searchDirs.Add(parentDir);
            }

            foreach (var dir in searchDirs)
            {
                var npbindPath = Path.Combine(dir, "sce_sys", "npbind.dat");
                if (File.Exists(npbindPath))
                {
                    return ExtractNpCommIdFromNpbind(npbindPath);
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts the first npcommid from a binary npbind.dat file
        /// by searching for the NPWR pattern in raw bytes.
        /// </summary>
        private string ExtractNpCommIdFromNpbind(string npbindPath)
        {
            try
            {
                var bytes = File.ReadAllBytes(npbindPath);
                var content = Encoding.ASCII.GetString(bytes);
                var match = NpCommIdPattern.Match(content);
                if (match.Success)
                {
                    return match.Groups[1].Value.ToUpperInvariant();
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[ShadPS4] Failed to parse npbind.dat at '{npbindPath}'");
            }
            return null;
        }
    }
}






