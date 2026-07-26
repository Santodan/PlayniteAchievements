using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.EmuLibrary;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using PlayniteAchievements.Providers.RPCS3.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        /// <summary>
        /// Optional display title from collection metadata.
        /// </summary>
        public string SourceTitle { get; set; }
    }

    internal sealed class GamePathCandidate
    {
        public string Path { get; set; }

        public bool AllowDirectoryIsoEnumeration { get; set; } = true;
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
        private readonly string _pluginUserDataPath;

        public Rpcs3Scanner(ILogger logger, PlayniteAchievementsSettings settings, Rpcs3Settings providerSettings, Rpcs3DataProvider provider = null, IPlayniteAPI playniteApi = null, string pluginUserDataPath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _provider = provider;
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
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
                trophyFolderCache = _provider.RebuildTrophyFolderCache();
            }
            else
            {
                trophyFolderCache = await BuildTrophyFolderCacheAsync(cancel).ConfigureAwait(false);
            }

            trophyFolderCache = trophyFolderCache ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (trophyFolderCache.Count == 0)
            {
                _logger?.Warn("[RPCS3] No cached trophy folders found; scanning game paths for TROPHY.TRP fallback.");
            }

            _logger?.Info($"[RPCS3] Scanning {trophyFolderCache.Count} cached trophy folders.");

            var rarityEnricher = await CreateRarityEnricherAsync(cancel).ConfigureAwait(false);

            RebuildPayload payload;
            try
            {
                payload = await ProviderRefreshExecutor.RunProviderGamesAsync(
                    gamesToRefresh,
                    game =>
                    {
                        onGameStarting?.Invoke(game);
                    },
                    async (game, token) =>
                    {
                        var data = await FetchGameDataAsync(game, trophyFolderCache, token).ConfigureAwait(false);
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
                        _logger?.Error(ex, $"[RPCS3] Failed to scan '{game?.Name}'");
                    },
                    delayBetweenGamesAsync: null,
                    delayAfterErrorAsync: null,
                    cancel).ConfigureAwait(false);
            }
            finally
            {
                rarityEnricher?.Dispose();
            }

            return payload ?? new RebuildPayload { Summary = new RebuildSummary() };
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

            await rarityEnricher.EnrichAsync(game, data.Achievements, "ps3", "PSN", cancel).ConfigureAwait(false);
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

            var sources = ResolveTrophySourcesForGame(game, trophyFolderCache, cancel)
                .Where(source => source != null && !string.IsNullOrWhiteSpace(source.NpCommId))
                .ToList();

            // A null result means "trophy data not located"; the refresh pipeline skips
            // persistence for null results so previously cached achievements are preserved.
            if (sources.Count == 0)
            {
                _logger?.Info($"[RPCS3-DIAG] FetchGameDataAsync: game='{game.Name}' - NO trophy sources resolved, returning null (cached achievements preserved)");
                return Task.FromResult<GameAchievementData>(null);
            }

            cancel.ThrowIfCancellationRequested();

            var isCollection = sources.Count > 1;
            _logger?.Info($"[RPCS3-DIAG] FetchGameDataAsync: game='{game.Name}', sources={sources.Count}, isCollection={isCollection}"
                + (isCollection ? " (each source's title becomes an achievement Category)" : " (Category falls back to trophy group name, null for base trophies)"));
            var achievements = new List<AchievementDetail>();

            foreach (var source in sources)
            {
                cancel.ThrowIfCancellationRequested();
                var sourceAchievements = BuildAchievementsForSource(source, trophyFolderCache, isCollection);
                var sampleCategory = sourceAchievements.FirstOrDefault()?.Category;
                _logger?.Info($"[RPCS3-DIAG] FetchGameDataAsync: source '{source.NpCommId}' produced {sourceAchievements.Count} achievements, sample Category='{sampleCategory ?? "(null)"}'");
                achievements.AddRange(sourceAchievements);
            }

            if (achievements.Count == 0)
            {
                _logger?.Info($"[RPCS3-DIAG] FetchGameDataAsync: game='{game.Name}' - sources resolved but 0 achievements parsed, returning null (cached achievements preserved)");
                return Task.FromResult<GameAchievementData>(null);
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

        private List<AchievementDetail> BuildAchievementsForSource(
            GameTrophySource source,
            Dictionary<string, string> trophyFolderCache,
            bool isCollection)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.NpCommId))
            {
                return new List<AchievementDetail>();
            }

            if (trophyFolderCache != null &&
                trophyFolderCache.TryGetValue(source.NpCommId, out var trophyFolderPath))
            {
                var tropconfPath = Path.Combine(trophyFolderPath, "TROPCONF.SFM");
                var tropusrPath = Path.Combine(trophyFolderPath, "TROPUSR.DAT");

                if (!File.Exists(tropconfPath))
                {
                    return new List<AchievementDetail>();
                }

                try
                {
                    var ps3Locale = Rpcs3TrophyParser.MapGlobalLanguageToPs3Locale(_settings?.Persisted?.GlobalLanguage);
                    var trophies = Rpcs3TrophyParser.ParseTrophyDefinitions(tropconfPath, ps3Locale, _logger);

                    if (File.Exists(tropusrPath))
                    {
                        Rpcs3TrophyParser.ParseTrophyUnlockData(tropusrPath, trophies, _logger);
                    }

                    if (trophies.Count == 0)
                    {
                        return new List<AchievementDetail>();
                    }

                    var sourceTitle = ExtractTitleNameFromTropconf(trophyFolderPath);
                    if (string.IsNullOrWhiteSpace(sourceTitle))
                    {
                        sourceTitle = source.SourceTitle;
                    }

                    _logger?.Info($"[RPCS3-DIAG] BuildAchievementsForSource: '{source.NpCommId}' parsed from trophy cache '{trophyFolderPath}', {trophies.Count} trophies, TROPUSR.DAT exists={File.Exists(tropusrPath)}, resolved title='{sourceTitle}'");
                    return ConvertTrophiesToAchievements(
                        trophies,
                        source,
                        trophyFolderPath,
                        sourceTitle,
                        isCollection,
                        forceLocked: false);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"[RPCS3] Failed to parse trophy data for '{source.NpCommId}'");
                    return new List<AchievementDetail>();
                }
            }

            if (!string.IsNullOrWhiteSpace(source.TrpPath))
            {
                _logger?.Info($"[RPCS3-DIAG] BuildAchievementsForSource: '{source.NpCommId}' not in trophy cache, falling back to TROPHY.TRP at '{source.TrpPath}' (all trophies forced locked)");
                return BuildAchievementsFromTrp(source, isCollection);
            }

            _logger?.Info($"[RPCS3-DIAG] BuildAchievementsForSource: '{source.NpCommId}' not in trophy cache and no TrpPath - 0 achievements");
            return new List<AchievementDetail>();
        }

        private List<AchievementDetail> BuildAchievementsFromTrp(GameTrophySource source, bool isCollection)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.TrpPath) || !File.Exists(source.TrpPath))
            {
                return new List<AchievementDetail>();
            }

            try
            {
                var ps3Locale = Rpcs3TrophyParser.MapGlobalLanguageToPs3Locale(_settings?.Persisted?.GlobalLanguage);
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(source.TrpPath, ps3Locale, _logger);

                if (trophies.Count == 0)
                {
                    return new List<AchievementDetail>();
                }

                return ConvertTrophiesToAchievements(
                    trophies,
                    source,
                    trophyFolderPath: null,
                    sourceTitle: source.SourceTitle,
                    isCollection: isCollection,
                    forceLocked: true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Failed to parse TROPHY.TRP for '{source.NpCommId}'");
                return new List<AchievementDetail>();
            }
        }

        private List<AchievementDetail> ConvertTrophiesToAchievements(
            List<Rpcs3Trophy> trophies,
            GameTrophySource source,
            string trophyFolderPath,
            string sourceTitle,
            bool isCollection,
            bool forceLocked)
        {
            var achievements = new List<AchievementDetail>();
            var collectionTitle = string.IsNullOrWhiteSpace(sourceTitle) ? source.NpCommId : sourceTitle;

            foreach (var trophy in trophies)
            {
                var iconPath = string.IsNullOrWhiteSpace(trophyFolderPath)
                    ? null
                    : GetTrophyIconPath(trophyFolderPath, source.NpCommId, trophy.Id);

                var normalizedTrophyType = NormalizeTrophyType(trophy.TrophyType);
                achievements.Add(new AchievementDetail
                {
                    ApiName = isCollection ? $"{source.NpCommId}:{trophy.Id}" : trophy.Id.ToString(),
                    DisplayName = trophy.Name,
                    Description = trophy.Description,
                    UnlockedIconPath = iconPath,
                    LockedIconPath = iconPath,
                    Hidden = trophy.Hidden,
                    Unlocked = !forceLocked && trophy.Unlocked,
                    UnlockTimeUtc = forceLocked ? null : trophy.UnlockTimeUtc,
                    GlobalPercentUnlocked = null,
                    Rarity = GetRarityFromTrophyType(normalizedTrophyType),
                    TrophyType = normalizedTrophyType,
                    IsCapstone = normalizedTrophyType == "platinum",
                    CategoryType = MapGroupIdToCategoryType(trophy.GroupId),
                    Category = BuildAchievementCategory(trophy, collectionTitle, isCollection)
                });
            }

            return achievements;
        }

        private static string BuildAchievementCategory(Rpcs3Trophy trophy, string sourceTitle, bool isCollection)
        {
            if (!isCollection)
            {
                return trophy?.GroupName;
            }

            var title = string.IsNullOrWhiteSpace(sourceTitle) ? null : sourceTitle.Trim();
            var groupName = trophy?.GroupName?.Trim();
            var categoryType = MapGroupIdToCategoryType(trophy?.GroupId);

            if (string.Equals(categoryType, "DLC", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(groupName))
            {
                return string.IsNullOrWhiteSpace(title)
                    ? groupName
                    : $"{title} - {groupName}";
            }

            return title;
        }

        // PS3 title/serial ID patterns: BLUS, BLES, BCES, NPUB, NPEB, etc.
        private static readonly System.Text.RegularExpressions.Regex Ps3IdPattern =
            new System.Text.RegularExpressions.Regex(@"\b([A-Z]{2,4}\d{5})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // npcommid pattern: NPWR05920_00 format (in TROPDIR subdirectory names)
        private static readonly System.Text.RegularExpressions.Regex NpCommIdPathPattern =
            new System.Text.RegularExpressions.Regex(@"\b([A-Z]{4}\d{5}_\d{2})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Pattern to extract title-name from TROPCONF.SFM
        private static readonly System.Text.RegularExpressions.Regex TitleNamePattern =
            new System.Text.RegularExpressions.Regex(@"<title-name>(.*?)<\/title-name>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex CollectionSubgameDirectoryPattern =
            new System.Text.RegularExpressions.Regex(@"^PS3_GM\d{2}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex QuotedPathPattern =
            new System.Text.RegularExpressions.Regex("\"([^\"]+)\"|'([^']+)'",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Patterns used by NormalizeGameName.
        private static readonly Regex PlayStationSuffixRegex = new Regex(
            @"\s*[-:]\s*(PlayStation\s*)?(PS[1234])\s*(Edition|Version|Demo|Beta|Trial|Region\s*Free)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FileExtensionSuffixRegex = new Regex(@"\.(iso|pkg|rap)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NonAlphanumericRegex = new Regex(@"[^a-zA-Z0-9\s]", RegexOptions.Compiled);
        private static readonly Regex DigitLetterBoundaryRegex = new Regex(@"(\d)([a-zA-Z])", RegexOptions.Compiled);
        private static readonly Regex LetterDigitBoundaryRegex = new Regex(@"([a-zA-Z])(\d)", RegexOptions.Compiled);

        internal IReadOnlyList<GameTrophySource> ResolveTrophySourcesForGame(
            Game game,
            Dictionary<string, string> trophyFolderCache,
            CancellationToken cancel,
            bool allowRawIsoScan = true)
        {
            if (game == null)
            {
                return Array.Empty<GameTrophySource>();
            }

            _logger?.Info($"[RPCS3-DIAG] ResolveTrophySourcesForGame: game='{game.Name}', trophy cache has {trophyFolderCache?.Count ?? 0} NPWR sets");

            if (GameCustomDataLookup.TryGetRpcs3MatchIdOverride(game.Id, out var overrideMatchId))
            {
                var normalizedOverride = Rpcs3MatchIdHelper.Normalize(overrideMatchId) ?? overrideMatchId;
                _logger?.Info($"[RPCS3-DIAG] ResolveTrophySourcesForGame: game='{game.Name}' has match-ID OVERRIDE '{normalizedOverride}' - collection detection SKIPPED, single source forced");
                return new[] { new GameTrophySource { NpCommId = normalizedOverride, TrpPath = null } };
            }

            var collectionSources = FindCollectionTrophySourcesForGame(game, trophyFolderCache, cancel, allowRawIsoScan);
            _logger?.Info($"[RPCS3-DIAG] ResolveTrophySourcesForGame: game='{game.Name}' collection detection found {collectionSources.Count} surviving sources: [{string.Join(", ", collectionSources.Select(s => s.NpCommId))}]"
                + (collectionSources.Count > 1 ? " -> COLLECTION" : " -> not a collection, falling back to single-source resolution"));
            if (collectionSources.Count > 1)
            {
                return collectionSources;
            }

            var singleSource = FindSingleNpCommIdForGame(game, trophyFolderCache, cancel, allowRawIsoScan);
            if (singleSource != null && !string.IsNullOrWhiteSpace(singleSource.NpCommId))
            {
                _logger?.Info($"[RPCS3-DIAG] ResolveTrophySourcesForGame: game='{game.Name}' resolved SINGLE source '{singleSource.NpCommId}' (TrpPath='{singleSource.TrpPath}')");
                return new[] { singleSource };
            }

            _logger?.Info($"[RPCS3-DIAG] ResolveTrophySourcesForGame: game='{game.Name}' single-source resolution found nothing, returning {collectionSources.Count} collection sources");
            return collectionSources;
        }

        private List<GameTrophySource> FindCollectionTrophySourcesForGame(
            Game game,
            Dictionary<string, string> trophyFolderCache,
            CancellationToken cancel,
            bool allowRawIsoScan)
        {
            var sources = new List<GameTrophySource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = ResolveGamePathCandidates(game).ToList();
            _logger?.Info($"[RPCS3-DIAG] FindCollectionTrophySources: game='{game?.Name}' has {candidates.Count} path candidates: [{string.Join(" | ", candidates.Select(c => c?.Path))}]");

            foreach (var candidate in candidates)
            {
                cancel.ThrowIfCancellationRequested();

                foreach (var source in FindFolderCollectionSources(candidate.Path))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache);
                }

                foreach (var source in FindTropdirCollectionSources(candidate.Path, trophyFolderCache))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache);
                }

                foreach (var source in FindIsoCollectionSourcesForCandidate(candidate, game?.Name, trophyFolderCache, allowRawIsoScan))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache);
                }
            }

            foreach (var isoPath in ResolveSharedIsoPathsFromGamesYml(candidates))
            {
                cancel.ThrowIfCancellationRequested();

                foreach (var source in FindIsoTrophySources(isoPath, trophyFolderCache, allowRawIsoScan))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache);
                }
            }

            return sources;
        }

        private void AddStrictTrophySource(
            List<GameTrophySource> sources,
            HashSet<string> seen,
            GameTrophySource source,
            Dictionary<string, string> trophyFolderCache)
        {
            if (source == null)
            {
                return;
            }

            var normalized = Rpcs3MatchIdHelper.Normalize(source.NpCommId);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                _logger?.Info($"[RPCS3-DIAG] AddStrictTrophySource: REJECTED - could not normalize NPWR id '{source.NpCommId}'");
                return;
            }

            var hasCachedTrophyData = trophyFolderCache != null && trophyFolderCache.ContainsKey(normalized);
            var hasTrpFallback = !string.IsNullOrWhiteSpace(source.TrpPath) && File.Exists(source.TrpPath);
            if (!hasCachedTrophyData && !hasTrpFallback)
            {
                _logger?.Info($"[RPCS3-DIAG] AddStrictTrophySource: REJECTED '{normalized}' - not in RPCS3 trophy cache (sub-game never booted in RPCS3?) and no on-disk TROPHY.TRP (TrpPath='{source.TrpPath}')");
                return;
            }

            if (!seen.Add(normalized))
            {
                _logger?.Info($"[RPCS3-DIAG] AddStrictTrophySource: DUPLICATE '{normalized}' - already added");
                return;
            }

            _logger?.Info($"[RPCS3-DIAG] AddStrictTrophySource: ACCEPTED '{normalized}' - cached={hasCachedTrophyData}, trpFallback={hasTrpFallback} (TrpPath='{source.TrpPath}'), title='{source.SourceTitle}'");
            source.NpCommId = normalized;
            sources.Add(source);
        }

        private IEnumerable<GameTrophySource> FindFolderCollectionSources(string candidatePath)
        {
            var collectionRoot = ResolveFolderCollectionRoot(candidatePath);
            if (string.IsNullOrWhiteSpace(collectionRoot))
            {
                _logger?.Info($"[RPCS3-DIAG] FindFolderCollectionSources: candidate '{candidatePath}' - no collection root found (no ancestor dir with PS3_DISC.SFB + >1 PS3_GAME/PS3_GMxx sub-dirs)");
                yield break;
            }

            var subgameDirectories = GetCollectionSubgameDirectories(collectionRoot);
            _logger?.Info($"[RPCS3-DIAG] FindFolderCollectionSources: candidate '{candidatePath}' -> collection root '{collectionRoot}' with {subgameDirectories.Count} sub-game dirs: [{string.Join(", ", subgameDirectories.Select(Path.GetFileName))}]");
            if (subgameDirectories.Count <= 1)
            {
                yield break;
            }

            foreach (var subgameDirectory in subgameDirectories)
            {
                var trpPath = Path.Combine(subgameDirectory, "TROPHY", "TROPHY.TRP");
                if (!File.Exists(trpPath))
                {
                    _logger?.Info($"[RPCS3-DIAG] FindFolderCollectionSources: sub-game '{Path.GetFileName(subgameDirectory)}' skipped - no TROPHY.TRP at '{trpPath}'");
                    continue;
                }

                var npCommId = Rpcs3TrophyParser.ExtractNpCommId(trpPath, _logger);
                if (string.IsNullOrWhiteSpace(npCommId))
                {
                    try
                    {
                        npCommId = ExtractNpCommIdFromTrpFile(trpPath);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"[RPCS3] Failed to extract NPWR ID from '{trpPath}'");
                    }
                }

                if (string.IsNullOrWhiteSpace(npCommId))
                {
                    _logger?.Info($"[RPCS3-DIAG] FindFolderCollectionSources: sub-game '{Path.GetFileName(subgameDirectory)}' skipped - could not extract NPWR id from '{trpPath}'");
                    continue;
                }

                var sourceTitle = ReadParamSfoTitle(subgameDirectory);
                _logger?.Info($"[RPCS3-DIAG] FindFolderCollectionSources: sub-game '{Path.GetFileName(subgameDirectory)}' -> NPWR='{npCommId}', PARAM.SFO title='{sourceTitle}'");
                yield return new GameTrophySource
                {
                    NpCommId = npCommId,
                    TrpPath = trpPath,
                    SourceTitle = sourceTitle
                };
            }
        }

        /// <summary>
        /// Finds trophy sources for folder installs whose TROPDIR carries several trophy
        /// sets under a single PS3_GAME (e.g. The Sly Collection). Only sets RPCS3 has
        /// created a trophy folder for are returned, mirroring the ISO scan's cache gate;
        /// a multi-region dump therefore surfaces at most one set and resolution stays on
        /// the single-source path.
        /// </summary>
        private IEnumerable<GameTrophySource> FindTropdirCollectionSources(
            string candidatePath,
            Dictionary<string, string> trophyFolderCache)
        {
            if (trophyFolderCache == null || trophyFolderCache.Count == 0)
            {
                yield break;
            }

            var current = candidatePath?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
            {
                yield break;
            }

            var directoriesToCheck = new List<string> { current };

            // Playnite may point at USRDIR; TROPDIR is a sibling in the game root.
            var normalizedPath = TrimTrailingSeparators(current);
            if (normalizedPath.EndsWith("USRDIR", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Path.GetDirectoryName(normalizedPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    directoriesToCheck.Add(parentDir);
                }
            }

            var tropdirs = directoriesToCheck
                .SelectMany(dir => new[]
                {
                    Path.Combine(dir, "TROPDIR"),
                    Path.Combine(dir, "PS3_GAME", "TROPDIR")
                })
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var tropdir in tropdirs)
            {
                if (!Directory.Exists(tropdir))
                {
                    continue;
                }

                string[] setDirectories;
                try
                {
                    setDirectories = Directory.GetDirectories(tropdir);
                }
                catch
                {
                    continue;
                }

                foreach (var setDirectory in setDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var trpPath = Path.Combine(setDirectory, "TROPHY.TRP");
                    if (!File.Exists(trpPath))
                    {
                        continue;
                    }

                    string npCommId = null;
                    try
                    {
                        npCommId = ExtractNpCommIdFromTrpFile(trpPath);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"[RPCS3] Failed to extract NPWR ID from '{trpPath}'");
                    }

                    var normalized = Rpcs3MatchIdHelper.Normalize(npCommId);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    if (!trophyFolderCache.ContainsKey(normalized))
                    {
                        _logger?.Info($"[RPCS3-DIAG] FindTropdirCollectionSources: set '{Path.GetFileName(setDirectory)}' in '{tropdir}' skipped - '{normalized}' not in RPCS3 trophy cache (multi-region duplicate or never booted)");
                        continue;
                    }

                    _logger?.Info($"[RPCS3-DIAG] FindTropdirCollectionSources: set '{normalized}' accepted from '{trpPath}' (present in RPCS3 trophy cache)");
                    yield return new GameTrophySource
                    {
                        NpCommId = normalized,
                        TrpPath = trpPath
                    };
                }
            }
        }

        private IEnumerable<GameTrophySource> FindIsoCollectionSourcesForCandidate(
            GamePathCandidate candidate,
            string gameName,
            Dictionary<string, string> trophyFolderCache,
            bool allowRawIsoScan)
        {
            if (candidate == null)
            {
                yield break;
            }

            foreach (var isoPath in ResolveIsoFilesForCandidate(
                candidate.Path,
                gameName,
                candidate.AllowDirectoryIsoEnumeration))
            {
                foreach (var source in FindIsoTrophySources(isoPath, trophyFolderCache, allowRawIsoScan))
                {
                    yield return source;
                }
            }
        }

        private IReadOnlyList<GameTrophySource> FindIsoTrophySources(
            string isoPath,
            Dictionary<string, string> trophyFolderCache,
            bool allowRawIsoScan)
        {
            var sources = new List<GameTrophySource>();

            if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
            {
                return sources;
            }

            _logger?.Info($"[RPCS3-DIAG] FindIsoTrophySources: probing ISO '{isoPath}' (rawScanAllowed={allowRawIsoScan})");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rejectedNotCached = new List<string>();

            try
            {
                using (var disc = new DiscUtilsFacade(isoPath))
                {
                    foreach (var subgameDirectory in GetIsoSubgameDirectoryNames())
                    {
                        var npCommId = ExtractNpCommIdFromDisc(disc, $"{subgameDirectory}/TROPHY/TROPHY.TRP");
                        var normalized = Rpcs3MatchIdHelper.Normalize(npCommId);
                        if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                        {
                            continue;
                        }

                        if (trophyFolderCache?.ContainsKey(normalized) != true)
                        {
                            rejectedNotCached.Add($"{normalized} (from {subgameDirectory})");
                            continue;
                        }

                        _logger?.Info($"[RPCS3-DIAG] FindIsoTrophySources: found '{normalized}' in ISO at '{subgameDirectory}/TROPHY/TROPHY.TRP' - in trophy cache, KEPT");
                        sources.Add(new GameTrophySource { NpCommId = normalized, TrpPath = null });
                    }
                }
            }
            catch (Exception ex)
            {
                // DiscUtils cannot read UDF PS3 images; raw NPWR scanning below handles those.
                _logger?.Info($"[RPCS3-DIAG] FindIsoTrophySources: DiscUtils could not read '{isoPath}' ({ex.GetType().Name}: {ex.Message}) - relying on raw scan");
            }

            if (allowRawIsoScan)
            {
                foreach (var npCommId in Rpcs3NpCommIdExtractor.ExtractNpCommIdsFromRawFile(isoPath, _logger))
                {
                    var normalized = Rpcs3MatchIdHelper.Normalize(npCommId);
                    if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    {
                        continue;
                    }

                    if (trophyFolderCache?.ContainsKey(normalized) != true)
                    {
                        rejectedNotCached.Add($"{normalized} (raw scan)");
                        continue;
                    }

                    _logger?.Info($"[RPCS3-DIAG] FindIsoTrophySources: found '{normalized}' via raw scan - in trophy cache, KEPT");
                    sources.Add(new GameTrophySource { NpCommId = normalized, TrpPath = null });
                }
            }

            if (rejectedNotCached.Count > 0)
            {
                _logger?.Info($"[RPCS3-DIAG] FindIsoTrophySources: REJECTED {rejectedNotCached.Count} NPWR ids found in ISO but missing from RPCS3 trophy cache (sub-game never booted in RPCS3?): [{string.Join(", ", rejectedNotCached)}]");
            }
            _logger?.Info($"[RPCS3-DIAG] FindIsoTrophySources: ISO '{isoPath}' yielded {sources.Count} usable sources");

            return sources;
        }

        private IEnumerable<string> ResolveSharedIsoPathsFromGamesYml(IReadOnlyList<GamePathCandidate> candidatePaths)
        {
            var rpcs3Root = GetRpcs3Root();
            if (string.IsNullOrWhiteSpace(rpcs3Root))
            {
                _logger?.Info("[RPCS3-DIAG] ResolveSharedIsoPathsFromGamesYml: no RPCS3 root resolved, games.yml lookup SKIPPED");
                yield break;
            }

            foreach (var gamesYmlPath in EnumerateRpcs3GamesYmlPaths(rpcs3Root))
            {
                _logger?.Info($"[RPCS3-DIAG] ResolveSharedIsoPathsFromGamesYml: '{gamesYmlPath}' exists={File.Exists(gamesYmlPath)}");
            }

            var map = ReadRpcs3GamesYmlTitlePathMap(rpcs3Root);
            _logger?.Info($"[RPCS3-DIAG] ResolveSharedIsoPathsFromGamesYml: games.yml contains {map.Count} title-id entries");
            if (map.Count == 0)
            {
                yield break;
            }

            foreach (var group in map.Values
                .Select(path => ResolvePathAgainstRoot(path, rpcs3Root))
                .Where(path => !string.IsNullOrWhiteSpace(path) && path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                .GroupBy(NormalizePathForComparison, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                var isoPath = group.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(isoPath))
                {
                    _logger?.Info($"[RPCS3-DIAG] ResolveSharedIsoPathsFromGamesYml: shared ISO group '{group.Key}' ({group.Count()} title-ids) skipped - file does not exist");
                    continue;
                }

                var matchesCandidate = candidatePaths.Any(candidate => PathsEqual(candidate?.Path, isoPath));
                _logger?.Info($"[RPCS3-DIAG] ResolveSharedIsoPathsFromGamesYml: shared ISO '{isoPath}' referenced by {group.Count()} title-ids, matches a game path candidate={matchesCandidate}");
                if (matchesCandidate)
                {
                    yield return isoPath;
                }
            }
        }

        private IReadOnlyDictionary<string, string> ReadRpcs3GamesYmlTitlePathMap(string rpcs3Root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var gamesYmlPath in EnumerateRpcs3GamesYmlPaths(rpcs3Root))
            {
                foreach (var kvp in Rpcs3GamesYmlReader.ReadTitlePathMap(gamesYmlPath, _logger))
                {
                    map[kvp.Key] = kvp.Value;
                }
            }

            return map;
        }

        private static IEnumerable<string> EnumerateRpcs3GamesYmlPaths(string rpcs3Root)
        {
            if (string.IsNullOrWhiteSpace(rpcs3Root))
            {
                yield break;
            }

            yield return Path.Combine(rpcs3Root, "games.yml");
            yield return Path.Combine(rpcs3Root, "config", "games.yml");
        }

        private string ResolveFolderCollectionRoot(string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return null;
            }

            var current = candidatePath.Trim().Trim('"');
            if (File.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
            {
                return null;
            }

            for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (LooksLikeCollectionRoot(current))
                {
                    return current;
                }

                if (IsCollectionSubgameDirectory(current))
                {
                    var parent = Path.GetDirectoryName(TrimTrailingSeparators(current));
                    if (LooksLikeCollectionRoot(parent))
                    {
                        return parent;
                    }
                }

                current = Path.GetDirectoryName(TrimTrailingSeparators(current));
            }

            return null;
        }

        private bool LooksLikeCollectionRoot(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory) &&
                   Directory.Exists(directory) &&
                   File.Exists(Path.Combine(directory, "PS3_DISC.SFB")) &&
                   GetCollectionSubgameDirectories(directory).Count > 1;
        }

        private List<string> GetCollectionSubgameDirectories(string collectionRoot)
        {
            var directories = new List<string>();

            if (string.IsNullOrWhiteSpace(collectionRoot) || !Directory.Exists(collectionRoot))
            {
                return directories;
            }

            var ps3Game = Path.Combine(collectionRoot, "PS3_GAME");
            if (Directory.Exists(ps3Game))
            {
                directories.Add(ps3Game);
            }

            try
            {
                directories.AddRange(Directory.GetDirectories(collectionRoot)
                    .Where(IsCollectionSubgameDirectory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                // Ignore unreadable game directories.
            }

            return directories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsCollectionSubgameDirectory(string directory)
        {
            var name = Path.GetFileName(TrimTrailingSeparators(directory));
            return string.Equals(name, "PS3_GAME", StringComparison.OrdinalIgnoreCase) ||
                   CollectionSubgameDirectoryPattern.IsMatch(name ?? string.Empty);
        }

        private static IEnumerable<string> GetIsoSubgameDirectoryNames()
        {
            yield return "PS3_GAME";

            for (var i = 0; i <= 99; i++)
            {
                yield return $"PS3_GM{i:00}";
            }
        }

        private string ReadParamSfoTitle(string subgameDirectory)
        {
            var paramSfoPath = Path.Combine(subgameDirectory, "PARAM.SFO");
            return Rpcs3ParamSfoReader.ReadStringValue(paramSfoPath, "TITLE", _logger);
        }

        private IReadOnlyList<GamePathCandidate> ResolveGamePathCandidates(Game game)
        {
            var candidates = new List<GamePathCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var installDir = ExpandGamePath(game, game?.InstallDirectory);

            if (game?.Roms != null)
            {
                foreach (var rom in game.Roms)
                {
                    AddCandidate(candidates, seen, ExpandGamePath(game, rom?.Path), installDir);
                }
            }

            if (game?.GameActions != null)
            {
                foreach (var action in game.GameActions)
                {
                    AddActionArgumentPathCandidates(game, action, candidates, seen, installDir);
                }
            }

            var hasExplicitGamePath = candidates.Any(candidate => IsGameSpecificCandidate(candidate?.Path));
            if (!hasExplicitGamePath || IsGameSpecificCandidate(installDir))
            {
                AddCandidate(candidates, seen, installDir, installDir);
            }

            if (game?.GameActions != null)
            {
                foreach (var action in game.GameActions)
                {
                    AddActionExecutablePathCandidates(game, action, candidates, seen, installDir, hasExplicitGamePath);
                }
            }

            // Uninstalled EmuLibrary games carry no rom or install paths; recover the
            // original source path from the serialized EmuLibrary game id as a last resort.
            if (_playniteApi != null &&
                EmuLibraryPathResolver.TryResolveSourcePath(_playniteApi, game, out var emuLibrarySourcePath))
            {
                AddCandidate(candidates, seen, emuLibrarySourcePath, installDir);
            }

            return candidates;
        }

        private void AddActionArgumentPathCandidates(
            Game game,
            GameAction action,
            List<GamePathCandidate> candidates,
            HashSet<string> seen,
            string installDir)
        {
            if (action == null)
            {
                return;
            }

            AddCandidatesFromArgumentText(candidates, seen, ExpandGamePath(game, action.Arguments), installDir);
            AddCandidatesFromArgumentText(candidates, seen, ExpandGamePath(game, action.AdditionalArguments), installDir);
        }

        private void AddActionExecutablePathCandidates(
            Game game,
            GameAction action,
            List<GamePathCandidate> candidates,
            HashSet<string> seen,
            string installDir,
            bool hasExplicitGamePath)
        {
            if (action == null)
            {
                return;
            }

            AddCandidateIfAllowed(candidates, seen, ExpandGamePath(game, action.Path), installDir, hasExplicitGamePath);
            AddCandidateIfAllowed(candidates, seen, ExpandGamePath(game, action.WorkingDir), installDir, hasExplicitGamePath);
        }

        private void AddCandidatesFromArgumentText(
            List<GamePathCandidate> candidates,
            HashSet<string> seen,
            string text,
            string installDir)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            AddCandidate(candidates, seen, text, installDir);

            foreach (System.Text.RegularExpressions.Match match in QuotedPathPattern.Matches(text))
            {
                var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                AddCandidate(candidates, seen, value, installDir);
            }

            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddCandidate(candidates, seen, token, installDir);
            }
        }

        private static void AddCandidate(
            List<GamePathCandidate> candidates,
            HashSet<string> seen,
            string path,
            string installDir,
            bool allowDirectoryIsoEnumeration = true)
        {
            var normalized = NormalizeCandidatePath(path, installDir);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (seen.Add(normalized))
            {
                candidates.Add(new GamePathCandidate
                {
                    Path = normalized,
                    AllowDirectoryIsoEnumeration = allowDirectoryIsoEnumeration
                });
            }
        }

        private static void AddCandidateIfAllowed(
            List<GamePathCandidate> candidates,
            HashSet<string> seen,
            string path,
            string installDir,
            bool hasExplicitGamePath)
        {
            if (!hasExplicitGamePath || IsGameSpecificCandidate(path))
            {
                AddCandidate(candidates, seen, path, installDir);
            }
        }

        private static string NormalizeCandidatePath(string path, string installDir)
        {
            var normalized = (path ?? string.Empty)
                .Trim()
                .Trim('"', '\'')
                .TrimEnd(',', ';');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (!Path.IsPathRooted(normalized) && !string.IsNullOrWhiteSpace(installDir))
            {
                normalized = Path.Combine(installDir, normalized);
            }

            try
            {
                return Path.GetFullPath(normalized);
            }
            catch
            {
                return normalized;
            }
        }

        private IEnumerable<string> ResolveIsoFilesForCandidate(
            string candidatePath,
            string gameName,
            bool allowDirectoryIsoEnumeration = true)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                yield break;
            }

            if (File.Exists(candidatePath) &&
                candidatePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                yield return candidatePath;
                yield break;
            }

            if (!allowDirectoryIsoEnumeration || !Directory.Exists(candidatePath))
            {
                yield break;
            }

            var isoFiles = FindIsoFiles(candidatePath);
            if (isoFiles.Count <= 1)
            {
                foreach (var isoPath in isoFiles)
                {
                    yield return isoPath;
                }

                yield break;
            }

            var selected = SelectIsoMatchingGameName(candidatePath, isoFiles, gameName);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                yield return selected;
            }
        }

        /// <summary>
        /// Picks the single ISO whose filename uniquely matches the game name from a
        /// directory holding several ISOs. Directories shared by multiple games would
        /// otherwise resolve every game to the first ISO with cached trophy data.
        /// </summary>
        private string SelectIsoMatchingGameName(string directory, IReadOnlyList<string> isoFiles, string gameName)
        {
            var normalizedGameName = NormalizeGameName(gameName);
            if (string.IsNullOrWhiteSpace(normalizedGameName))
            {
                _logger?.Info($"[RPCS3] Directory '{directory}' contains {isoFiles.Count} ISOs and the game has no usable name; skipping directory ISO scan.");
                return null;
            }

            string bestIso = null;
            var bestScore = 0;
            var bestIsUnique = true;

            foreach (var isoPath in isoFiles)
            {
                var normalizedIsoName = NormalizeGameName(Path.GetFileNameWithoutExtension(isoPath));
                var score = CalculateNameSimilarity(normalizedGameName, normalizedIsoName);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIso = isoPath;
                    bestIsUnique = true;
                }
                else if (score == bestScore && score > 0)
                {
                    bestIsUnique = false;
                }
            }

            if (bestScore < 80 || !bestIsUnique)
            {
                _logger?.Info($"[RPCS3] Directory '{directory}' contains {isoFiles.Count} ISOs and none uniquely matches '{gameName}' (best score {bestScore}, unique={bestIsUnique}); skipping directory ISO scan.");
                return null;
            }

            _logger?.Info($"[RPCS3] Directory '{directory}' contains {isoFiles.Count} ISOs; selected '{Path.GetFileName(bestIso)}' for '{gameName}' (score {bestScore}).");
            return bestIso;
        }

        private string GetRpcs3Root()
        {
            var exePath = _providerSettings?.ExecutablePath;
            return string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
        }

        private static string ResolvePathAgainstRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim().Trim('"', '\'');
            if (!Path.IsPathRooted(trimmed) && !string.IsNullOrWhiteSpace(root))
            {
                trimmed = Path.Combine(root, trimmed);
            }

            try
            {
                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return trimmed;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                NormalizePathForComparison(left),
                NormalizePathForComparison(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim().Trim('"', '\'')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim().Trim('"', '\'').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static string TrimTrailingSeparators(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? path
                : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsGameSpecificCandidate(string path)
        {
            var normalized = NormalizeCandidatePath(path, null);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (Ps3IdPattern.IsMatch(normalized) ||
                Rpcs3MatchIdHelper.Normalize(NpCommIdPathPattern.Match(normalized).Groups[1].Value) != null)
            {
                return true;
            }

            var extension = Path.GetExtension(normalized);
            if (string.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".pkg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!Directory.Exists(normalized))
            {
                return false;
            }

            if (File.Exists(Path.Combine(normalized, "PS3_DISC.SFB")) ||
                File.Exists(Path.Combine(normalized, "PARAM.SFO")) ||
                Directory.Exists(Path.Combine(normalized, "PS3_GAME")) ||
                Directory.Exists(Path.Combine(normalized, "TROPDIR")) ||
                Directory.Exists(Path.Combine(normalized, "TROPHY")))
            {
                return true;
            }

            var trimmed = TrimTrailingSeparators(normalized);
            var directoryName = Path.GetFileName(trimmed);
            if (string.Equals(directoryName, "USRDIR", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(trimmed);
                return IsGameSpecificCandidate(parent);
            }

            return IsCollectionSubgameDirectory(normalized);
        }

        /// <summary>
        /// Finds the npcommid for a game using multiple strategies:
        /// 1. Extract the trophy set from an installed game's on-disk TROPHY.TRP
        /// 2. Extract npcommid from PS3 ISO file
        /// 3. Match by game name against TROPCONF.SFM titles
        /// Also returns the TROPHY.TRP path for pre-launch fallback.
        /// </summary>
        private GameTrophySource FindSingleNpCommIdForGame(
            Game game,
            Dictionary<string, string> trophyFolderCache,
            CancellationToken cancel,
            bool allowRawIsoScan)
        {
            foreach (var candidate in ResolveGamePathCandidates(game))
            {
                cancel.ThrowIfCancellationRequested();

                var gameDirectory = candidate.Path;

                // Strategy 1: For installed games, check for TROPHY.TRP in game directory
                if (!string.IsNullOrWhiteSpace(gameDirectory))
                {
                    var (npcommid, trpPath) = FindNpCommIdAndTrpFromInstalledGame(gameDirectory, trophyFolderCache);
                    if (!string.IsNullOrWhiteSpace(npcommid))
                    {
                        _logger?.Info($"[RPCS3-DIAG] FindSingleNpCommId: game='{game?.Name}' matched via installed-game TROPHY.TRP '{npcommid}' (TrpPath='{trpPath}') under '{gameDirectory}'");
                        return new GameTrophySource { NpCommId = npcommid, TrpPath = trpPath };
                    }
                }

                // Strategy 2: Extract npcommid from PS3 ISO file
                var npcommidFromIso = FindNpCommIdFromIso(
                    game,
                    gameDirectory,
                    trophyFolderCache,
                    allowRawIsoScan,
                    candidate.AllowDirectoryIsoEnumeration);
                if (!string.IsNullOrWhiteSpace(npcommidFromIso))
                {
                    _logger?.Info($"[RPCS3-DIAG] FindSingleNpCommId: game='{game?.Name}' matched via ISO '{npcommidFromIso}' from candidate '{gameDirectory}'");
                    return new GameTrophySource { NpCommId = npcommidFromIso, TrpPath = null };
                }
            }

            // Strategy 3: Match by game name against TROPCONF.SFM titles
            var npcommidFromName = FindNpCommIdByName(game, trophyFolderCache);
            if (!string.IsNullOrWhiteSpace(npcommidFromName))
            {
                _logger?.Info($"[RPCS3-DIAG] FindSingleNpCommId: game='{game?.Name}' matched via name similarity to cached TROPCONF title -> '{npcommidFromName}'");
                return new GameTrophySource { NpCommId = npcommidFromName, TrpPath = null };
            }

            _logger?.Info($"[RPCS3-DIAG] FindSingleNpCommId: game='{game?.Name}' - no strategy matched (installed TROPHY.TRP, ISO, name)");
            return null;
        }

        /// <summary>
        /// Extracts the npcommid from a PS3 ISO file by reading the embedded TROPHY.TRP.
        /// </summary>
        private string FindNpCommIdFromIso(Game game, string gameDirectory,
            Dictionary<string, string> trophyFolderCache,
            bool allowRawIsoScan,
            bool allowDirectoryIsoEnumeration = true)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                return null;
            }

            try
            {
                var isoFiles = ResolveIsoFilesForCandidate(gameDirectory, game?.Name, allowDirectoryIsoEnumeration).ToList();
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
                    if (string.IsNullOrWhiteSpace(npcommid) && allowRawIsoScan)
                    {
                        npcommid = Rpcs3NpCommIdExtractor.ExtractFirstNpCommIdFromRawFile(isoPath, _logger);
                    }

                    var normalized = Rpcs3MatchIdHelper.Normalize(npcommid);
                    if (!string.IsNullOrWhiteSpace(normalized) && trophyFolderCache.ContainsKey(normalized))
                    {
                        return normalized;
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

            // Rom paths may point at a file (e.g. USRDIR\EBOOT.BIN); start from its directory.
            if (File.Exists(gameDirectory))
            {
                gameDirectory = Path.GetDirectoryName(gameDirectory);
                if (string.IsNullOrWhiteSpace(gameDirectory))
                {
                    return (null, null);
                }
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

            // Multi-region dumps carry several trophy sets (e.g. TROPDIR/NPWR_EUR, NPWR_US, NPWR_JAP).
            // Prefer the set RPCS3 actually created a trophy folder for; only fall back to the
            // first valid TRP when none match the cache (pre-launch case).
            string fallbackNpcommid = null;
            string fallbackTrpPath = null;

            foreach (var trpPath in trpPaths)
            {
                if (!File.Exists(trpPath))
                {
                    continue;
                }

                try
                {
                    var npcommid = ExtractNpCommIdFromTrpFile(trpPath);
                    if (string.IsNullOrWhiteSpace(npcommid))
                    {
                        continue;
                    }

                    var normalized = Rpcs3MatchIdHelper.Normalize(npcommid) ?? npcommid;
                    if (trophyFolderCache?.ContainsKey(normalized) == true)
                    {
                        _logger?.Info($"[RPCS3-DIAG] FindNpCommIdAndTrpFromInstalledGame: '{normalized}' from '{trpPath}' matches RPCS3 trophy cache - selected");
                        return (normalized, trpPath);
                    }

                    if (fallbackNpcommid == null)
                    {
                        fallbackNpcommid = normalized;
                        fallbackTrpPath = trpPath;
                    }
                }
                catch
                {
                    // Ignore errors reading TROPHY.TRP
                }
            }

            if (fallbackNpcommid != null)
            {
                _logger?.Info($"[RPCS3-DIAG] FindNpCommIdAndTrpFromInstalledGame: no trophy set under '{gameDirectory}' matches the RPCS3 trophy cache - falling back to first valid TRP '{fallbackNpcommid}' at '{fallbackTrpPath}' (pre-launch)");
            }


            return (fallbackNpcommid, fallbackTrpPath);
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

            // Rom paths may point at a file (e.g. USRDIR\EBOOT.BIN); start from its directory.
            if (File.Exists(gameDirectory))
            {
                gameDirectory = Path.GetDirectoryName(gameDirectory);
                if (string.IsNullOrWhiteSpace(gameDirectory))
                {
                    return null;
                }
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
            // Container-aware extraction first (real binary TRPs), then a raw
            // byte scan for anything the container reader could not handle.
            var npCommId = Rpcs3TrophyParser.ExtractNpCommId(trpPath, _logger);
            if (!string.IsNullOrWhiteSpace(npCommId))
            {
                return npCommId;
            }

            return Rpcs3NpCommIdExtractor.ExtractFirstNpCommIdFromRawFile(trpPath, _logger);
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

                // Bounded byte scan: the npcommid XML sits in plaintext near the
                // start of the TRP container, after the binary header/entry table.
                return Rpcs3NpCommIdExtractor.ExtractFirstNpCommIdFromStream(stream, _logger);
            }
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
                results.AddRange(files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                // Ignore errors
            }

            return results;
        }

        /// <summary>
        /// Attempts to match a game by name against the titles in TROPCONF.SFM files.
        /// This is useful for ISO-based games where the trophy set cannot be resolved
        /// from the path. Only exact (100) and prefix (80) score tiers are accepted;
        /// a best score shared by two distinct titles is treated as ambiguous and
        /// yields no match. Ties among identical titles (regional duplicates) resolve
        /// to the ordinal-smallest NPWR id so results are deterministic.
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

            var scored = new List<(string NpCommId, string NormalizedTitle, int Score)>();

            foreach (var kvp in trophyFolderCache.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var titleName = ExtractTitleNameFromTropconf(kvp.Value);
                if (string.IsNullOrWhiteSpace(titleName))
                {
                    continue;
                }

                var normalizedTitle = NormalizeGameName(titleName);
                if (string.IsNullOrWhiteSpace(normalizedTitle))
                {
                    continue;
                }

                var score = CalculateNameSimilarity(normalizedGameName, normalizedTitle);
                if (score >= 80)
                {
                    scored.Add((kvp.Key, normalizedTitle, score));
                }
            }

            if (scored.Count == 0)
            {
                return null;
            }

            var bestScore = scored.Max(candidate => candidate.Score);
            var best = scored.Where(candidate => candidate.Score == bestScore).ToList();
            var distinctTitles = best
                .Select(candidate => candidate.NormalizedTitle)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (distinctTitles > 1)
            {
                _logger?.Info($"[RPCS3] Name fallback for '{game.Name}' is ambiguous at score {bestScore}: [{string.Join(", ", best.Select(candidate => $"{candidate.NpCommId} '{candidate.NormalizedTitle}'"))}]; no match selected.");
                return null;
            }

            var selected = best[0];
            _logger?.Info($"[RPCS3] Name fallback matched '{game.Name}' to '{selected.NpCommId}' (title '{selected.NormalizedTitle}', score {selected.Score}).");
            return selected.NpCommId;
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
            var normalized = PlayStationSuffixRegex.Replace(name, "");

            normalized = GameNameNormalizer.StripTrademarkSymbols(normalized);

            // Remove region/language parentheticals and bracket tags
            normalized = GameNameNormalizer.StripParentheticals(normalized);
            normalized = GameNameNormalizer.StripBrackets(normalized);

            // Remove file extensions
            normalized = FileExtensionSuffixRegex.Replace(normalized, "");

            // Remove special characters, keep alphanumeric and spaces
            normalized = NonAlphanumericRegex.Replace(normalized, " ");

            // Separate digits from letters (e.g., "PlayStation3" -> "PlayStation 3")
            // This handles cases like "PlayStation3 Edition" vs "PlayStation 3 Edition"
            normalized = DigitLetterBoundaryRegex.Replace(normalized, "$1 $2");
            normalized = LetterDigitBoundaryRegex.Replace(normalized, "$1 $2");

            return GameNameNormalizer.CollapseWhitespace(normalized).ToLowerInvariant();
        }

        /// <summary>
        /// Calculates a similarity score (0-100) between two normalized game names.
        /// </summary>
        private static int CalculateNameSimilarity(string name1, string name2)
        {
            return GameNameNormalizer.ComputeMatchScore(name1, name2);
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
