using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Overrides;
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
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// Result of RPCS3 path validation for UI display.
    /// </summary>
    public class Rpcs3ValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public string UserId { get; set; }
        public int TrophyFolderCount { get; set; }
    }

    /// <summary>
    /// Data provider for RPCS3 PlayStation 3 emulator trophy tracking.
    /// Parses local trophy files (TROPCONF.SFM + TROPUSR.DAT) from RPCS3 installation.
    /// </summary>
    internal sealed class Rpcs3DataProvider : DataProviderBase<Rpcs3Settings>, IDataProvider, IProviderOverride, IInGameProgressSource
    {
        private sealed class Rpcs3InGameSource
        {
            public string NpCommId { get; set; }
            public string Path { get; set; }
            public bool IsCollection { get; set; }
        }

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_RPCS3",
            raw => Rpcs3MatchIdHelper.TryNormalize(raw, out var matchId)
                ? ProviderOverrideValidation.Valid(matchId)
                : ProviderOverrideValidation.Invalid(
                    "LOCPlayAch_Menu_Rpcs3MatchId_InvalidId"));

        private readonly Rpcs3Scanner _scanner;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;

        private Dictionary<string, string> _trophyFolderCache;
        private readonly object _cacheLock = new object();

        public Rpcs3DataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
            : this(logger, settings, playniteApi, string.Empty)
        {
        }

        public Rpcs3DataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _settings = settings;
            _logger = logger;
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;

            _scanner = new Rpcs3Scanner(_logger, _settings, ProviderSettings, this, _playniteApi, _pluginUserDataPath);
        }

        public string ProviderName
        {
            get
            {
                var value = ResourceProvider.GetString("LOCPlayAch_Provider_RPCS3");
                return string.IsNullOrWhiteSpace(value) ? "RPCS3" : value;
            }
        }

        public string ProviderKey => "RPCS3";

        public string ProviderIconKey => "ProviderIconRPCS3";

        public string ProviderColorHex => "#686DE0";

        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        /// <summary>
        /// Validates an RPCS3 installation path has the expected structure.
        /// Returns validation result with error message, discovered user ID, and trophy folder count.
        /// </summary>
        public Rpcs3ValidationResult ValidateRpcs3Path(string path)
        {
            var result = new Rpcs3ValidationResult();

            if (string.IsNullOrWhiteSpace(path))
            {
                result.ErrorMessage = ResourceProvider.GetString("LOCPlayAch_InvalidPath");
                return result;
            }

            if (!Directory.Exists(path))
            {
                result.ErrorMessage = ResourceProvider.GetString("LOCPlayAch_InvalidPath");
                return result;
            }

            var context = Rpcs3InstallationResolver.ResolveFromRoot(path, _logger);
            if (context == null)
            {
                result.ErrorMessage = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Rpcs3Validation_NoTrophyFolder")
                        ?? "The active RPCS3 user profile or trophy folder could not be resolved for '{0}'.",
                    path);
                return result;
            }

            var trophyPath = context.TrophyFolder;

            // Count trophy folders
            try
            {
                var count = Directory.GetDirectories(trophyPath)
                    .Count(d => File.Exists(Path.Combine(d, "TROPCONF.SFM")));
                result.TrophyFolderCount = count;
            }
            catch
            {
                result.TrophyFolderCount = 0;
            }

            result.IsValid = true;
            result.UserId = context.UserId;
            return result;
        }

        /// <summary>
        /// Gets the game's selected RPCS3 emulator root for capability checks. Full
        /// profile resolution is deferred until refresh, where it can fail closed.
        /// </summary>
        private string GetEmulatorRootFromGame(Game game)
        {
            var actions = game?.GameActions?
                .Where(action => action?.Type == GameActionType.Emulator && action.EmulatorId != Guid.Empty)
                .OrderByDescending(action => action.IsPlayAction)
                .ToList();
            if (actions == null)
            {
                return null;
            }

            foreach (var action in actions)
            {
                var emulator = _playniteApi?.Database?.Emulators?.Get(action.EmulatorId);
                if (Rpcs3InstallationResolver.IsRpcs3Emulator(emulator) && !string.IsNullOrWhiteSpace(emulator.InstallDir))
                {
                    return emulator.InstallDir;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the RPCS3 emulator root using priority order:
        /// 1. User settings (validated)
        /// 2. Game's emulator config
        /// 3. First RPCS3 emulator in database
        /// </summary>
        internal string GetEmulatorRoot(Game game = null)
        {
            return Rpcs3InstallationResolver.ResolveEmulatorRoot(game, ProviderSettings, _playniteApi, _logger);
        }

        /// <summary>
        /// Resolves the exact RPCS3 install, VFS layout and active profile used by
        /// a game. No lowest-numbered-profile or cross-install fallback is used.
        /// </summary>
        internal Rpcs3InstallationContext GetInstallationContext(Game game = null)
        {
            return Rpcs3InstallationResolver.Resolve(game, ProviderSettings, _playniteApi, _logger);
        }

        /// <summary>
        /// Gets the trophy folder path for the resolved emulator root and user profile.
        /// </summary>
        public string GetTrophyFolder(Game game = null)
        {
            return GetInstallationContext(game)?.TrophyFolder;
        }

        public bool IsAuthenticated
        {
            get
            {
                var trophyFolder = GetTrophyFolder();

                if (string.IsNullOrWhiteSpace(trophyFolder))
                {
                    return false;
                }

                var exists = Directory.Exists(trophyFolder);
                return exists;
            }
        }

        public bool IsCapable(Game game)
        {
            if (game == null)
            {
                return false;
            }

            if (TryGetMatchIdOverride(game.Id, out _))
            {
                return true;
            }

            // Check source name
            var src = game.Source?.Name ?? string.Empty;
            if (src.IndexOf("RPCS3", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Source matches, but still verify trophy data exists
                return CanFindTrophyDataForGame(game);
            }

            // Check if game uses RPCS3 emulator
            var emulatorRoot = GetEmulatorRootFromGame(game);
            if (!string.IsNullOrWhiteSpace(emulatorRoot))
            {
                // Emulator matches, but still verify trophy data exists
                return CanFindTrophyDataForGame(game);
            }

            return false;
        }

        internal static bool TryGetMatchIdOverride(Guid gameId, out string matchIdOverride)
        {
            return GameCustomDataLookup.TryGetRpcs3MatchIdOverride(gameId, out matchIdOverride);
        }

        internal static bool TrySetMatchIdOverride(Guid gameId, string matchId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (!Rpcs3MatchIdHelper.TryNormalize(matchId, out var normalizedMatchId))
            {
                return false;
            }

            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore == null)
            {
                return false;
            }

            customDataStore.Update(gameId, customData =>
            {
                customData.ProviderOverride = new ProviderOverrideData
                {
                    ProviderKey = "RPCS3",
                    Value = normalizedMatchId
                };
            });

            persistSettingsForUi?.Invoke();
            logger?.Info($"Set RPCS3 match ID override for '{gameName}' to {normalizedMatchId}");
            return true;
        }

        internal static bool TryClearMatchIdOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore == null ||
                !customDataStore.TryLoad(gameId, out var customData) ||
                customData?.ProviderOverride == null ||
                !string.Equals(customData.ProviderOverride.ProviderKey, "RPCS3", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            customDataStore.Update(gameId, data =>
            {
                data.ProviderOverride = null;
            });

            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared RPCS3 match ID override for '{gameName}'");
            return true;
        }

        /// <summary>
        /// Checks if trophy data can be found for a game by verifying the npcommid exists in cache.
        /// Returns true if the game's npcommid is found in cache, or falls back to true if
        /// we can't verify (allows name-based matching during the actual scan).
        /// Also checks for TROPHY.TRP as pre-launch fallback when cache doesn't exist.
        /// </summary>
        private bool CanFindTrophyDataForGame(Game game)
        {
            // Capability must inspect the same install/profile that refresh will
            // inspect for this game; a provider-global cache can belong to another
            // RPCS3 action or user.
            var cache = BuildTrophyFolderCache(game);

            var resolvedSources = _scanner.ResolveTrophySourcesForGame(
                game,
                cache,
                CancellationToken.None,
                allowRawIsoScan: false);
            if (resolvedSources.Any(source =>
                source != null &&
                !string.IsNullOrWhiteSpace(source.NpCommId) &&
                ((cache != null && cache.ContainsKey(source.NpCommId)) ||
                 (!string.IsNullOrWhiteSpace(source.TrpPath) && File.Exists(source.TrpPath)))))
            {
                return true;
            }

            var installDir = ExpandGamePath(game, game?.InstallDirectory);
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                // First try to find npcommid directly (e.g., NPWR05920_00 in TROPDIR path)
                var npcommidMatch = NpCommIdPathPattern.Match(installDir);
                if (npcommidMatch.Success)
                {
                    var npcommid = npcommidMatch.Groups[1].Value.ToUpperInvariant();
                    if (cache != null && cache.ContainsKey(npcommid))
                    {
                        return true;
                    }
                    // npcommid found in path but not in cache - check for TROPHY.TRP fallback
                    var trpPath = FindTrpPathForGameDirectory(installDir);
                    if (!string.IsNullOrWhiteSpace(trpPath) && File.Exists(trpPath))
                    {
                        return true; // Pre-launch detection possible
                    }
                    return false;
                }

                // Check for npcommid in TROPDIR subdirectories (PKG games)
                var npcommidFromTropdir = FindNpCommIdInTropdir(installDir, cache);
                if (!string.IsNullOrWhiteSpace(npcommidFromTropdir))
                {
                    return true;
                }
            }

            // If we can't verify by ID, fall back to true
            // This allows name-based matching during the actual scan
            return true;
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

            foreach (var dir in directoriesToCheck)
            {
                // PKG games: TROPDIR contains subdirectories named after npcommid
                var tropdir = Path.Combine(dir, "TROPDIR");
                if (Directory.Exists(tropdir))
                {
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(tropdir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
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
        /// Looks for npcommid in TROPDIR subdirectories and checks if it exists in cache.
        /// PKG games have structure: {game_root}/TROPDIR/{npcommid}/TROPHY.TRP
        /// </summary>
        private string FindNpCommIdInTropdir(string gameDirectory, Dictionary<string, string> cache)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory) || cache == null)
            {
                return null;
            }

            // Build list of directories to check
            // Playnite may point to USRDIR, but TROPDIR folder is in the game root
            var directoriesToCheck = new List<string> { gameDirectory };

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
                var tropdir = Path.Combine(dir, "TROPDIR");
                if (!Directory.Exists(tropdir))
                {
                    continue;
                }

                try
                {
                    // TROPDIR subdirectories are named after npcommid (e.g., NPWR05920_00)
                    foreach (var subDir in Directory.GetDirectories(tropdir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        var dirName = Path.GetFileName(subDir);
                        if (string.IsNullOrWhiteSpace(dirName))
                        {
                            continue;
                        }

                        // Check if directory name matches npcommid pattern and exists in cache
                        var npcommidMatch = NpCommIdPathPattern.Match(dirName);
                        if (npcommidMatch.Success)
                        {
                            var npcommid = npcommidMatch.Groups[1].Value.ToUpperInvariant();
                            if (cache.ContainsKey(npcommid))
                            {
                                return npcommid;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore errors scanning TROPDIR
                }
            }

            return null;
        }

        // npcommid pattern: NPWR05920_00 format (in TROPDIR subdirectory names)
        private static readonly System.Text.RegularExpressions.Regex NpCommIdPathPattern =
            new System.Text.RegularExpressions.Regex(@"\b([A-Z]{4}\d{5}_\d{2})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Expands path variables in game paths using Playnite's variable expansion.
        /// </summary>
        internal string ExpandGamePath(Game game, string path)
        {
            return PathExpansion.ExpandGamePath(_playniteApi, game, path);
        }

        internal Dictionary<string, string> GetOrBuildTrophyFolderCache()
        {
            lock (_cacheLock)
            {
                if (_trophyFolderCache != null)
                {
                    return _trophyFolderCache;
                }
                _trophyFolderCache = BuildTrophyFolderCache();
                return _trophyFolderCache;
            }
        }

        internal Dictionary<string, string> RebuildTrophyFolderCache()
        {
            lock (_cacheLock)
            {
                _trophyFolderCache = BuildTrophyFolderCache();
                return _trophyFolderCache;
            }
        }

        internal void ClearTrophyFolderCache()
        {
            lock (_cacheLock)
            {
                _trophyFolderCache = null;
            }
        }

        private Dictionary<string, string> BuildTrophyFolderCache(Game game = null)
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var trophyPath = GetTrophyFolder(game);
            if (string.IsNullOrWhiteSpace(trophyPath))
            {
                return cache;
            }

            if (!Directory.Exists(trophyPath))
            {
                return cache;
            }

            try
            {
                var npcommidDirectories = Directory.GetDirectories(trophyPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

                foreach (var npcommidDir in npcommidDirectories)
                {
                    var npcommid = Rpcs3MatchIdHelper.Normalize(Path.GetFileName(npcommidDir));
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
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Failed to enumerate trophy directories at '{trophyPath}'");
            }

            return cache;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        InGameProgressRegistration IInGameProgressSource.TryRegister(
            Game game,
            GameAchievementData cachedSchema)
        {
            if (game == null ||
                cachedSchema?.Achievements == null ||
                cachedSchema.Achievements.Count == 0 ||
                !string.Equals(cachedSchema.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(cachedSchema.ProviderGameKey))
            {
                return null;
            }

            var npCommIds = cachedSchema.ProviderGameKey
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Rpcs3MatchIdHelper.Normalize)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var trophyRoot = GetTrophyFolder(game);
            if (npCommIds.Count == 0 || string.IsNullOrWhiteSpace(trophyRoot))
            {
                return null;
            }

            var isCollection = npCommIds.Count > 1;
            var sources = npCommIds
                .Select(id => new Rpcs3InGameSource
                {
                    NpCommId = id,
                    Path = Path.Combine(trophyRoot, id, "TROPUSR.DAT"),
                    IsCollection = isCollection
                })
                .Where(source => Directory.Exists(Path.GetDirectoryName(source.Path)))
                .ToList();
            if (sources.Count != npCommIds.Count)
            {
                return null;
            }

            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                WatchTargets = sources.Select(source => source.Path).ToList(),
                PollInterval = TimeSpan.FromSeconds(60),
                State = sources
            };
        }

        Task<IReadOnlyList<InGameProgressQueryResult>> IInGameProgressSource.QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            var results = new List<InGameProgressQueryResult>();
            foreach (var context in games ?? Array.Empty<InGameTrackingContext>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameId = context?.Game?.Id ?? Guid.Empty;
                var sources = context?.Registration?.State as List<Rpcs3InGameSource>;
                var schema = context?.CachedSchema?.Achievements;
                if (sources == null || schema == null)
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "registration_missing"));
                    continue;
                }

                var observations = new List<AchievementProgressObservation>();
                var success = true;
                foreach (var source in sources)
                {
                    var prefix = source.NpCommId + ":";
                    var ids = schema
                        .Select(achievement => achievement?.ApiName)
                        .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                        .Select(apiName =>
                            source.IsCollection && apiName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                ? apiName.Substring(prefix.Length)
                                : source.IsCollection ? null : apiName)
                        .Where(apiName => int.TryParse(apiName, out _))
                        .Select(int.Parse)
                        .ToList();

                    if (!Rpcs3TrophyParser.TryParseTrophyProgress(source.Path, ids, out var unlocked))
                    {
                        success = false;
                        break;
                    }

                    observations.AddRange(unlocked.Select(pair => new AchievementProgressObservation
                    {
                        ApiName = source.IsCollection
                            ? source.NpCommId + ":" + pair.Key
                            : pair.Key.ToString(),
                        Unlocked = true,
                        UnlockTimeUtc = pair.Value
                    }));
                }

                results.Add(success
                    ? InGameProgressQueryResult.Succeeded(gameId, observations)
                    : InGameProgressQueryResult.Failed(gameId, "file_unstable"));
            }

            return Task.FromResult<IReadOnlyList<InGameProgressQueryResult>>(results);
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new Rpcs3SettingsView(_playniteApi);
    }
}

