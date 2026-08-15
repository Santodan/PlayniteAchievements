using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.EmuLibrary;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Common.Disc;
using PlayniteAchievements.Providers.RPCS3.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
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
    /// Per-refresh memo for one concrete RPCS3 installation and one user profile.
    /// The cache must never be shared across profiles or emulator installations.
    /// </summary>
    internal sealed class Rpcs3RefreshContext
    {
        public Rpcs3InstallationContext Installation { get; set; }
        public Dictionary<string, string> TrophyFolderCache { get; set; }
        public Rpcs3SerialNpwrBridge SerialBridge { get; set; }
    }

    internal sealed class SourceAchievements
    {
        public List<AchievementDetail> Achievements { get; set; } = new List<AchievementDetail>();
        public bool PreserveExisting { get; set; }

        /// <summary>
        /// The category label the source's trophies were grouped under (the set title,
        /// falling back to the NPWR id), and the set's ICON0.PNG on disk when one exists —
        /// the art RPCS3's own trophy manager shows per trophy set. Used to publish
        /// per-sub-game default category art for collections.
        /// </summary>
        public string CategoryLabel { get; set; }
        public string CategoryArtPath { get; set; }
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

            var refreshContexts = new Dictionary<string, Rpcs3RefreshContext>(StringComparer.OrdinalIgnoreCase);

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
                        var data = await FetchGameDataAsync(game, refreshContexts, token).ConfigureAwait(false);
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

        private Task<GameAchievementData> FetchGameDataAsync(
            Game game,
            Dictionary<string, Rpcs3RefreshContext> refreshContexts,
            CancellationToken cancel)
        {
            if (game == null)
            {
                return Task.FromResult<GameAchievementData>(null);
            }

            var refreshContext = GetOrCreateRefreshContext(game, refreshContexts, cancel);
            var trophyFolderCache = refreshContext?.TrophyFolderCache ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var serialBridge = refreshContext?.SerialBridge ?? new Rpcs3SerialNpwrBridge((Rpcs3InstallationContext)null, _logger);

            var sources = ResolveTrophySourcesForGame(game, trophyFolderCache, cancel, serialBridge: serialBridge)
                .Where(source => source != null && !string.IsNullOrWhiteSpace(source.NpCommId))
                .ToList();

            // A null result means "trophy data not located"; the refresh pipeline skips
            // persistence for null results so previously cached achievements are preserved.
            if (sources.Count == 0)
            {
                _logger?.Info($"[RPCS3] '{game.Name}': no trophy sources resolved; cached achievements preserved.");
                return Task.FromResult<GameAchievementData>(null);
            }

            cancel.ThrowIfCancellationRequested();

            var isCollection = sources.Count > 1;
            var achievements = new List<AchievementDetail>();
            var categoryArt = new List<(string Label, string ArtPath)>();
            foreach (var source in sources)
            {
                cancel.ThrowIfCancellationRequested();
                var sourceAchievements = BuildAchievementsForSource(source, trophyFolderCache, isCollection);
                if (sourceAchievements.PreserveExisting)
                {
                    _logger?.Warn($"[RPCS3] '{game.Name}': progress for '{source.NpCommId}' was not trustworthy; cached achievements preserved.");
                    return Task.FromResult<GameAchievementData>(null);
                }

                achievements.AddRange(sourceAchievements.Achievements);
                if (isCollection && !string.IsNullOrWhiteSpace(sourceAchievements.CategoryArtPath))
                {
                    categoryArt.Add((sourceAchievements.CategoryLabel, sourceAchievements.CategoryArtPath));
                }
            }

            if (achievements.Count == 0)
            {
                return Task.FromResult<GameAchievementData>(null);
            }

            ApplyDefaultCategoryArt(game, categoryArt);

            // Record the resolved trophy source identity so the refresh pipeline can
            // detect match changes and overwrite stale cached icons (ApiNames are bare
            // trophy indexes shared by every RPCS3 game).
            var providerGameKey = string.Join(
                "+",
                sources.Select(source => source.NpCommId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

            var unlockedCount = achievements.Count(achievement => achievement?.Unlocked == true);
            _logger?.Info(
                $"[RPCS3] '{game.Name}': produced {achievements.Count} trophies " +
                $"({unlockedCount} unlocked) from [{providerGameKey}].");

            return Task.FromResult(new GameAchievementData
            {
                ProviderKey = "RPCS3",
                ProviderGameKey = providerGameKey,
                LibrarySourceName = game?.Source?.Name,
                GameName = game?.Name,
                PlayniteGameId = game?.Id,
                HasAchievements = achievements.Count > 0,
                Achievements = achievements,
                LastUpdatedUtc = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Publishes each sub-game's ICON0.PNG as the default category art for its
        /// category label, so a collection's category groups render with the same
        /// art RPCS3's trophy manager shows per trophy set. Uses the shared
        /// provider-default convention read by CategoryDefaultImageResolver:
        /// existing art is kept, and user overrides always win over defaults.
        /// </summary>
        private void ApplyDefaultCategoryArt(Game game, List<(string Label, string ArtPath)> entries)
        {
            if (entries == null || entries.Count == 0 || game?.Id == null || game.Id == Guid.Empty)
            {
                return;
            }

            var diskImageService = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (diskImageService == null)
            {
                return;
            }

            var gameIdText = game.Id.ToString("D");
            foreach (var entry in entries)
            {
                var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(entry.Label);
                if (string.Equals(
                    label,
                    AchievementCategoryTypeHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    diskImageService.SaveDefaultCategoryImageFromFile(gameIdText, label, entry.ArtPath);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[RPCS3] Default category image copy failed for '{entry.Label}'.");
                }
            }
        }

        private Rpcs3RefreshContext GetOrCreateRefreshContext(
            Game game,
            Dictionary<string, Rpcs3RefreshContext> refreshContexts,
            CancellationToken cancel)
        {
            var installation = _provider?.GetInstallationContext(game) ??
                Rpcs3InstallationResolver.Resolve(game, _providerSettings, _playniteApi, _logger);
            if (installation == null)
            {
                // Without an installation there is no trophy folder to read, so
                // every source resolves against an empty cache and is dropped.
                // Say so here; downstream this is indistinguishable from a game
                // whose trophy set could not be matched.
                _logger?.Warn(
                    $"[RPCS3] '{game.Name}': no RPCS3 installation or user profile could be resolved; " +
                    "no trophy folders were scanned for this game.");
                return null;
            }

            if (refreshContexts.TryGetValue(installation.CacheKey, out var existing))
            {
                return existing;
            }

            var cache = BuildTrophyFolderCache(installation.TrophyFolder, cancel);
            var context = new Rpcs3RefreshContext
            {
                Installation = installation,
                TrophyFolderCache = cache,
                SerialBridge = new Rpcs3SerialNpwrBridge(installation, _logger)
            };
            refreshContexts[installation.CacheKey] = context;

            _logger?.Info(
                $"[RPCS3] Context: emulator '{installation.EmulatorRoot}', config '{installation.ConfigurationRoot}', " +
                $"dev_hdd0 '{installation.DevHdd0Root}', user '{installation.UserId}' ({installation.UserIdSource}), " +
                $"trophy folder '{installation.TrophyFolder}', sets [{string.Join(", ", cache.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}].");
            return context;
        }

        private Dictionary<string, string> BuildTrophyFolderCache(string trophyFolder, CancellationToken cancel)
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(trophyFolder) || !Directory.Exists(trophyFolder))
            {
                return cache;
            }

            try
            {
                foreach (var directory in Directory.GetDirectories(trophyFolder).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancel.ThrowIfCancellationRequested();
                    var npCommId = Rpcs3MatchIdHelper.Normalize(Path.GetFileName(directory));
                    if (!string.IsNullOrWhiteSpace(npCommId) && File.Exists(Path.Combine(directory, "TROPCONF.SFM")))
                    {
                        cache[npCommId] = directory;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Error(ex, $"[RPCS3] Failed to enumerate active-user trophy directories at '{trophyFolder}'");
            }

            return cache;
        }

        /// <summary>
        /// Emits the exact source that this refresh will use. This deliberately runs
        /// after all deterministic resolution stages so a support log can distinguish
        /// a wrong-source/profile problem from a valid TROPUSR.DAT that reports no
        /// unlocked trophies.
        /// </summary>
        private SourceAchievements BuildAchievementsForSource(
            GameTrophySource source,
            Dictionary<string, string> trophyFolderCache,
            bool isCollection)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.NpCommId))
            {
                return new SourceAchievements { PreserveExisting = true };
            }

            if (trophyFolderCache != null &&
                trophyFolderCache.TryGetValue(source.NpCommId, out var trophyFolderPath))
            {
                var tropconfPath = Path.Combine(trophyFolderPath, "TROPCONF.SFM");
                var tropusrPath = Path.Combine(trophyFolderPath, "TROPUSR.DAT");

                if (!File.Exists(tropconfPath))
                {
                    return new SourceAchievements { PreserveExisting = true };
                }

                try
                {
                    var ps3Locale = Rpcs3TrophyParser.MapGlobalLanguageToPs3Locale(_settings?.Persisted?.GlobalLanguage);
                    var trophies = Rpcs3TrophyParser.ParseTrophyDefinitions(tropconfPath, ps3Locale, _logger);

                    if (File.Exists(tropusrPath))
                    {
                        if (!Rpcs3TrophyParser.TryParseTrophyUnlockData(tropusrPath, trophies, _logger))
                        {
                            return new SourceAchievements { PreserveExisting = true };
                        }
                    }
                    else
                    {
                        _logger?.Info($"[RPCS3] '{source.NpCommId}': no TROPUSR.DAT at '{tropusrPath}'; using trophy definitions with no local progress record.");
                    }

                    if (trophies.Count == 0)
                    {
                        return new SourceAchievements { PreserveExisting = true };
                    }

                    var sourceTitle = ExtractTitleNameFromTropconf(trophyFolderPath);
                    if (string.IsNullOrWhiteSpace(sourceTitle))
                    {
                        sourceTitle = source.SourceTitle;
                    }

                    return new SourceAchievements
                    {
                        Achievements = ConvertTrophiesToAchievements(
                            trophies,
                            source,
                            trophyFolderPath,
                            sourceTitle,
                            isCollection,
                            forceLocked: false),
                        CategoryLabel = string.IsNullOrWhiteSpace(sourceTitle) ? source.NpCommId : sourceTitle,
                        CategoryArtPath = FindExistingIcon0Path(trophyFolderPath)
                    };
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"[RPCS3] Failed to parse trophy data for '{source.NpCommId}'");
                    return new SourceAchievements { PreserveExisting = true };
                }
            }

            if (!string.IsNullOrWhiteSpace(source.TrpPath))
            {
                return BuildAchievementsFromTrp(source, isCollection);
            }

            return new SourceAchievements { PreserveExisting = true };
        }

        private SourceAchievements BuildAchievementsFromTrp(GameTrophySource source, bool isCollection)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.TrpPath) || !File.Exists(source.TrpPath))
            {
                return new SourceAchievements { PreserveExisting = true };
            }

            try
            {
                var ps3Locale = Rpcs3TrophyParser.MapGlobalLanguageToPs3Locale(_settings?.Persisted?.GlobalLanguage);
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitionsFromTrp(source.TrpPath, ps3Locale, _logger);

                if (trophies.Count == 0)
                {
                    return new SourceAchievements { PreserveExisting = true };
                }

                var iconDirectory = ExtractTrpIcons(source.TrpPath, source.NpCommId);
                return new SourceAchievements
                {
                    Achievements = ConvertTrophiesToAchievements(
                        trophies,
                        source,
                        trophyFolderPath: iconDirectory,
                        sourceTitle: source.SourceTitle,
                        isCollection: isCollection,
                        forceLocked: true),
                    CategoryLabel = string.IsNullOrWhiteSpace(source.SourceTitle) ? source.NpCommId : source.SourceTitle,
                    CategoryArtPath = FindExistingIcon0Path(iconDirectory)
                };
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Failed to parse TROPHY.TRP for '{source.NpCommId}'");
                return new SourceAchievements { PreserveExisting = true };
            }
        }

        /// <summary>
        /// Extracts the TROP###.PNG entries from a TROPHY.TRP into the plugin's
        /// icon cache so TRP-sourced trophies get icons like trophy-folder ones.
        /// Returns the directory laid out like an RPCS3 trophy folder (probed by
        /// GetTrophyIconPath), or null when nothing could be extracted. Extraction
        /// runs once per trophy set; an already-populated directory is reused.
        /// </summary>
        private string ExtractTrpIcons(string trpPath, string npCommId)
        {
            if (string.IsNullOrWhiteSpace(_pluginUserDataPath) ||
                string.IsNullOrWhiteSpace(trpPath) ||
                string.IsNullOrWhiteSpace(npCommId))
            {
                return null;
            }

            try
            {
                var iconDirectory = Path.Combine(_pluginUserDataPath, "icon_cache", "rpcs3", npCommId);
                if (Directory.Exists(iconDirectory) &&
                    Directory.EnumerateFiles(iconDirectory, "TROP*.PNG").Any())
                {
                    return iconDirectory;
                }

                var trpBytes = File.ReadAllBytes(trpPath);
                var entries = Rpcs3TrpArchiveReader.ReadEntries(trpBytes, _logger);
                if (entries == null)
                {
                    return null;
                }

                var pngEntries = entries
                    .Where(entry => entry.Name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (pngEntries.Count == 0)
                {
                    return null;
                }

                Directory.CreateDirectory(iconDirectory);
                foreach (var entry in pngEntries)
                {
                    var data = Rpcs3TrpArchiveReader.ExtractEntry(trpBytes, entries, entry.Name);
                    if (data != null)
                    {
                        File.WriteAllBytes(Path.Combine(iconDirectory, entry.Name.ToUpperInvariant()), data);
                    }
                }

                return iconDirectory;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[RPCS3] Failed to extract icons from '{trpPath}'");
                return null;
            }
        }

        /// <summary>
        /// The set-level ICON0.PNG inside a trophy folder (or an extracted TRP icon
        /// directory), or null when the directory or file is absent. RPCS3's trophy
        /// manager shows this image per trophy set.
        /// </summary>
        private static string FindExistingIcon0Path(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            var icon0Path = Path.Combine(directory, "ICON0.PNG");
            return File.Exists(icon0Path) ? icon0Path : null;
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
            bool allowRawIsoScan = true,
            Rpcs3SerialNpwrBridge serialBridge = null)
        {
            if (game == null)
            {
                return Array.Empty<GameTrophySource>();
            }

            serialBridge = serialBridge ?? CreateSerialBridge(game);

            if (GameCustomDataLookup.TryGetRpcs3MatchIdOverride(game.Id, out var overrideMatchId))
            {
                var normalizedOverride = Rpcs3MatchIdHelper.Normalize(overrideMatchId) ?? overrideMatchId;
                return new[]
                {
                    ResolveOverrideSource(game, normalizedOverride, trophyFolderCache, cancel, allowRawIsoScan, serialBridge)
                };
            }

            var collectionSources = FindCollectionTrophySourcesForGame(game, trophyFolderCache, cancel, allowRawIsoScan, serialBridge);
            if (collectionSources.Count > 1)
            {
                return collectionSources;
            }

            var singleSource = FindSingleNpCommIdForGame(game, trophyFolderCache, cancel, allowRawIsoScan, serialBridge);
            if (singleSource != null && !string.IsNullOrWhiteSpace(singleSource.NpCommId))
            {
                return new[] { singleSource };
            }

            return collectionSources;
        }

        /// <summary>
        /// The trophy source for a user-set match ID override. A set RPCS3 has
        /// already created a trophy folder for needs nothing further; otherwise
        /// the game's own files are searched for that set's TROPHY.TRP, so an
        /// override still yields the full (locked) trophy list for a game that
        /// has never been booted in RPCS3.
        /// </summary>
        private GameTrophySource ResolveOverrideSource(
            Game game,
            string npCommId,
            Dictionary<string, string> trophyFolderCache,
            CancellationToken cancel,
            bool allowRawIsoScan,
            Rpcs3SerialNpwrBridge serialBridge)
        {
            if (trophyFolderCache?.ContainsKey(npCommId) == true)
            {
                return new GameTrophySource { NpCommId = npCommId, TrpPath = null };
            }

            var discovered = FindCollectionTrophySourcesForGame(game, trophyFolderCache, cancel, allowRawIsoScan, serialBridge)
                .Concat(new[] { FindSingleNpCommIdForGame(game, trophyFolderCache, cancel, allowRawIsoScan, serialBridge) })
                .FirstOrDefault(source =>
                    source != null &&
                    string.Equals(Rpcs3MatchIdHelper.Normalize(source.NpCommId), npCommId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(source.TrpPath) &&
                    File.Exists(source.TrpPath));

            if (discovered != null)
            {
                _logger?.Info(
                    $"[RPCS3] '{game?.Name}': override trophy set '{npCommId}' has no trophy folder; " +
                    $"reading definitions from '{discovered.TrpPath}'.");
                return discovered;
            }

            _logger?.Warn(
                $"[RPCS3] '{game?.Name}': override trophy set '{npCommId}' has no trophy folder in the resolved " +
                $"RPCS3 profile ({trophyFolderCache?.Count ?? 0} set(s) scanned) and no TROPHY.TRP for it was " +
                "found in the game's files.");

            return new GameTrophySource { NpCommId = npCommId, TrpPath = null };
        }

        private List<GameTrophySource> FindCollectionTrophySourcesForGame(
            Game game,
            Dictionary<string, string> trophyFolderCache,
            CancellationToken cancel,
            bool allowRawIsoScan,
            Rpcs3SerialNpwrBridge serialBridge)
        {
            var sources = new List<GameTrophySource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = ResolveGamePathCandidates(game).ToList();

            foreach (var candidate in candidates)
            {
                cancel.ThrowIfCancellationRequested();

                foreach (var source in FindFolderCollectionSources(candidate.Path))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache, game?.Name);
                }

                foreach (var source in FindTropdirCollectionSources(candidate.Path))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache, game?.Name);
                }

                foreach (var source in FindIsoCollectionSourcesForCandidate(candidate, game?.Name, trophyFolderCache, allowRawIsoScan))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache, game?.Name);
                }
            }

            foreach (var isoPath in ResolveSharedIsoPathsFromGamesYml(candidates, serialBridge))
            {
                cancel.ThrowIfCancellationRequested();

                foreach (var source in FindIsoTrophySources(isoPath, trophyFolderCache, allowRawIsoScan))
                {
                    AddStrictTrophySource(sources, seen, source, trophyFolderCache, game?.Name);
                }
            }

            // Renamed multi-set collection ISOs registered in games.yml: resolve the
            // game's serials and feed multi-NPWR results through the same strict gates.
            foreach (var serialCandidate in DiscoverSerialCandidates(candidates, serialBridge))
            {
                cancel.ThrowIfCancellationRequested();

                var serialSources = ResolveSerialTrophySources(
                    serialCandidate.Serial,
                    serialBridge,
                    trophyFolderCache,
                    allowRawIsoScan);
                if (serialSources.Count > 1)
                {
                    foreach (var source in serialSources)
                    {
                        AddStrictTrophySource(sources, seen, source, trophyFolderCache, game?.Name);
                    }
                }
            }

            return ApplySourceTitleAmbiguityGuard(sources, trophyFolderCache, game?.Name, "game path");
        }

        private void AddStrictTrophySource(
            List<GameTrophySource> sources,
            HashSet<string> seen,
            GameTrophySource source,
            Dictionary<string, string> trophyFolderCache,
            string gameName)
        {
            if (source == null)
            {
                return;
            }

            var normalized = Rpcs3MatchIdHelper.Normalize(source.NpCommId);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            var hasCachedTrophyData = trophyFolderCache != null && trophyFolderCache.ContainsKey(normalized);
            var hasTrpFallback = !string.IsNullOrWhiteSpace(source.TrpPath) && File.Exists(source.TrpPath);
            if (!hasCachedTrophyData && !hasTrpFallback)
            {
                _logger?.Info($"[RPCS3] '{gameName}': trophy set '{normalized}' has no trophy folder or TRP fallback; dropped.");
                return;
            }

            if (!seen.Add(normalized))
            {
                return;
            }

            source.NpCommId = normalized;
            sources.Add(source);
        }

        private List<GameTrophySource> ApplySourceTitleAmbiguityGuard(
            List<GameTrophySource> sources,
            Dictionary<string, string> trophyFolderCache,
            string gameName,
            string origin)
        {
            if (sources == null || sources.Count <= 1)
            {
                return sources ?? new List<GameTrophySource>();
            }

            var kept = new List<GameTrophySource>();
            foreach (var group in sources.GroupBy(source =>
            {
                var title = source.SourceTitle;
                if (string.IsNullOrWhiteSpace(title) && trophyFolderCache != null && trophyFolderCache.TryGetValue(source.NpCommId, out var folder))
                {
                    title = ExtractTitleNameFromTropconf(folder);
                }

                return string.IsNullOrWhiteSpace(title)
                    ? source.NpCommId
                    : NormalizeGameName(title);
            }, StringComparer.OrdinalIgnoreCase))
            {
                var members = group.OrderBy(source => source.NpCommId, StringComparer.OrdinalIgnoreCase).ToList();
                if (members.Count == 1)
                {
                    kept.Add(members[0]);
                    continue;
                }

                var profileBacked = members.Where(source =>
                    trophyFolderCache != null && trophyFolderCache.ContainsKey(source.NpCommId)).ToList();
                if (profileBacked.Count == 1)
                {
                    kept.Add(profileBacked[0]);
                    continue;
                }

                _logger?.Info($"[RPCS3] {origin} resolution for '{gameName}' found same-title trophy sets [{string.Join(", ", members.Select(source => source.NpCommId))}]; ambiguous, none selected.");
            }

            return kept;
        }

        private IEnumerable<GameTrophySource> FindFolderCollectionSources(string candidatePath)
        {
            var collectionRoot = ResolveFolderCollectionRoot(candidatePath);
            if (string.IsNullOrWhiteSpace(collectionRoot))
            {
                yield break;
            }

            var subgameDirectories = GetCollectionSubgameDirectories(collectionRoot);
            if (subgameDirectories.Count <= 1)
            {
                yield break;
            }

            foreach (var subgameDirectory in subgameDirectories)
            {
                var trpPath = Path.Combine(subgameDirectory, "TROPHY", "TROPHY.TRP");
                if (!File.Exists(trpPath))
                {
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
                    continue;
                }

                var sourceTitle = ReadParamSfoTitle(subgameDirectory);
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
        /// sets under a single PS3_GAME (e.g. The Sly Collection, Jak and Daxter Trilogy).
        /// Every set is returned with its on-disk TROPHY.TRP as fallback and its TRP title,
        /// so sub-games RPCS3 has never created a trophy folder for still surface with a
        /// locked list, mirroring the ISO scan. Same-title region variants of one game are
        /// collapsed or dropped downstream by ApplySourceTitleAmbiguityGuard.
        /// </summary>
        private IEnumerable<GameTrophySource> FindTropdirCollectionSources(string candidatePath)
        {
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

            var trpPaths = directoriesToCheck
                .SelectMany(EnumerateTropdirTrpPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var trpPath in trpPaths)
            {
                var source = ReadTrophySourceFromTrpFile(trpPath);
                if (source != null)
                {
                    yield return source;
                }
            }
        }

        /// <summary>
        /// TROPHY.TRP paths under a game directory's TROPDIR, one per trophy set
        /// directory. Both the directory itself and its PS3_GAME child are
        /// probed, so a dump root and the PS3_GAME inside it resolve alike.
        /// Ordered for deterministic selection; yields nothing when no TROPDIR
        /// exists or it cannot be read.
        /// </summary>
        private static IEnumerable<string> EnumerateTropdirTrpPaths(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                yield break;
            }

            foreach (var root in new[] { directory, Path.Combine(directory, "PS3_GAME") })
            {
                var tropdir = Path.Combine(root, "TROPDIR");
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
                    if (File.Exists(trpPath))
                    {
                        yield return trpPath;
                    }
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

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dropped = new List<string>();

            try
            {
                using (var disc = new DiscFileSystemReader(isoPath))
                {
                    var rootDirectoryNames = disc.GetRootDirectoryNames();
                    var enumeratedTrpPaths = 0;

                    foreach (var trpImagePath in GetIsoTrophyTrpPaths(disc, rootDirectoryNames))
                    {
                        enumeratedTrpPaths++;

                        var trpBytes = disc.ReadAllBytesOrNull(trpImagePath);
                        if (trpBytes == null ||
                            !Rpcs3TrophyParser.TryReadTrpIdentity(trpBytes, out var npCommId, out var titleName, _logger))
                        {
                            // The TRP contents could not be read or parsed (e.g. an image
                            // whose file data the filesystem reader cannot surface). A
                            // TROPDIR set directory is named after its NPWR id, so the
                            // set can still be matched against a trophy folder RPCS3 has
                            // already created for it.
                            var fallbackId = ExtractNpCommIdFromImagePath(trpImagePath);
                            if (string.IsNullOrWhiteSpace(fallbackId) || !seen.Add(fallbackId))
                            {
                                continue;
                            }

                            if (trophyFolderCache?.ContainsKey(fallbackId) == true)
                            {
                                _logger?.Info(
                                    $"[RPCS3] ISO '{isoPath}': '{trpImagePath}' could not be parsed; matched " +
                                    $"'{fallbackId}' from its TROPDIR directory name via the RPCS3 trophy folder.");
                                sources.Add(new GameTrophySource { NpCommId = fallbackId, TrpPath = null });
                            }
                            else
                            {
                                _logger?.Info(
                                    $"[RPCS3] ISO '{isoPath}': '{trpImagePath}' could not be parsed and set " +
                                    $"'{fallbackId}' has no RPCS3 trophy folder; booting it once in RPCS3 surfaces it.");
                                dropped.Add(fallbackId);
                            }

                            continue;
                        }

                        var normalized = Rpcs3MatchIdHelper.Normalize(npCommId);
                        if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                        {
                            continue;
                        }

                        if (trophyFolderCache?.ContainsKey(normalized) == true)
                        {
                            sources.Add(new GameTrophySource { NpCommId = normalized, TrpPath = null, SourceTitle = titleName });
                            continue;
                        }

                        // Never booted in RPCS3: keep the set readable by caching the
                        // embedded TRP locally, so the game still gets a locked list.
                        var materialized = MaterializeTrpSource(trpBytes, normalized, titleName, isoPath);
                        if (materialized != null)
                        {
                            sources.Add(materialized);
                        }
                        else
                        {
                            dropped.Add(normalized);
                        }
                    }

                    if (enumeratedTrpPaths == 0)
                    {
                        // Distinguishes "the image holds no trophy set" from a
                        // filesystem the reader could not walk: parse failures in
                        // the disc reader surface only as empty listings.
                        var rootSummary = rootDirectoryNames.Count > 0
                            ? string.Join(", ", rootDirectoryNames)
                            : "none";
                        var errorSummary = string.IsNullOrWhiteSpace(disc.LastError)
                            ? string.Empty
                            : $"; first filesystem read error: {disc.LastError}";
                        _logger?.Info(
                            $"[RPCS3] ISO '{isoPath}': structured read found no TROPHY.TRP paths; " +
                            $"root directories [{rootSummary}]{errorSummary}.");
                    }
                }
            }
            catch (Exception ex)
            {
                // Unreadable disc image; the raw scan below is the only option
                // left, and it only matches sets RPCS3 has already booted. A PS3
                // image the UDF reader cannot open is the difference between a
                // matched game and a silent miss, so a refresh reports it;
                // capability probes (raw scan off) stay quiet since they run
                // across the whole library.
                if (allowRawIsoScan)
                {
                    _logger?.Warn(
                        $"[RPCS3] Could not read ISO '{isoPath}' as a filesystem ({ex.GetType().Name}: {ex.Message}); " +
                        "falling back to a raw scan.");
                }
                else
                {
                    _logger?.Debug(ex, $"[RPCS3] Could not read ISO '{isoPath}' as a filesystem.");
                }
            }

            // The structured read is authoritative; raw scanning is the fallback for
            // images no filesystem reader handles, and can only match already-booted
            // sets since it yields ids without TRP data.
            if (allowRawIsoScan && seen.Count == 0)
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
                        dropped.Add(normalized);
                        continue;
                    }

                    sources.Add(new GameTrophySource { NpCommId = normalized, TrpPath = null });
                }

                if (seen.Count == 0)
                {
                    LogRawScanExhausted(isoPath);
                }
            }

            sources = ApplyTitleAmbiguityGuard(sources, dropped, isoPath);

            if (dropped.Count > 0)
            {
                _logger?.Info($"[RPCS3] ISO '{isoPath}' holds trophy sets that could not be used: [{string.Join(", ", dropped)}]; dropped.");
            }

            return sources;
        }

        /// <summary>
        /// Records that the raw scan reached its byte cap without finding an
        /// NPWR id, which is the expected outcome for a large image whose
        /// trophy data sits past the cap. Distinguishes "scanned, found
        /// nothing" from "image holds no trophy set".
        /// </summary>
        private void LogRawScanExhausted(string isoPath)
        {
            long length;
            try
            {
                length = new FileInfo(isoPath).Length;
            }
            catch
            {
                return;
            }

            if (length <= Rpcs3NpCommIdExtractor.DefaultMaxSearchBytes)
            {
                return;
            }

            const long megabyte = 1024 * 1024;
            _logger?.Info(
                $"[RPCS3] Raw scan of ISO '{isoPath}' found no NPWR id in its first " +
                $"{Rpcs3NpCommIdExtractor.DefaultMaxSearchBytes / megabyte} MB ({length / megabyte} MB image); " +
                "no trophy set was matched from it.");
        }

        /// <summary>
        /// TROPHY.TRP locations inside a PS3 disc image: the TROPDIR sets a PS3
        /// disc carries (one directory per NPWR id), and the bare TROPHY layout
        /// some structures use, under the standard PS3_GAME, multi-game
        /// collection PS3_GMxx sub-games, or the image root. Probes only
        /// directories present in the image root so the per-image cost stays at
        /// the sets that actually exist.
        /// </summary>
        private static IEnumerable<string> GetIsoTrophyTrpPaths(
            DiscFileSystemReader disc,
            IReadOnlyCollection<string> rootDirectoryNames)
        {
            var rootDirs = new HashSet<string>(rootDirectoryNames, StringComparer.OrdinalIgnoreCase);

            foreach (var subgameDirectory in GetIsoSubgameDirectoryNames())
            {
                if (!rootDirs.Contains(subgameDirectory))
                {
                    continue;
                }

                foreach (var trpPath in GetIsoTropdirTrpPaths(disc, subgameDirectory))
                {
                    yield return trpPath;
                }

                yield return $"{subgameDirectory}/TROPHY/TROPHY.TRP";
            }

            if (rootDirs.Contains("TROPDIR"))
            {
                foreach (var trpPath in GetIsoTropdirTrpPaths(disc, null))
                {
                    yield return trpPath;
                }
            }

            if (rootDirs.Contains("TROPHY"))
            {
                yield return "TROPHY/TROPHY.TRP";
            }
        }

        /// <summary>
        /// The NPWR id carried in a trophy path's own segments, or null when no
        /// segment is NPWR-shaped. TROPDIR set directories are named after their
        /// NPWR id, so the id survives even when the TRP contents are unreadable.
        /// </summary>
        private static string ExtractNpCommIdFromImagePath(string pathInsideImage)
        {
            if (string.IsNullOrWhiteSpace(pathInsideImage))
            {
                return null;
            }

            return pathInsideImage
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Rpcs3MatchIdHelper.Normalize)
                .FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment));
        }

        /// <summary>
        /// TROPHY.TRP paths under a TROPDIR inside a disc image, one per trophy
        /// set directory. <paramref name="parentDirectory"/> is null for a
        /// TROPDIR at the image root.
        /// </summary>
        private static IEnumerable<string> GetIsoTropdirTrpPaths(DiscFileSystemReader disc, string parentDirectory)
        {
            var tropdir = string.IsNullOrWhiteSpace(parentDirectory)
                ? "TROPDIR"
                : $"{parentDirectory}/TROPDIR";

            foreach (var setDirectory in disc.GetDirectoryNames(tropdir).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"{tropdir}/{setDirectory}/TROPHY.TRP";
            }
        }

        /// <summary>
        /// Persists an ISO-embedded TROPHY.TRP into the set's icon_cache folder so
        /// the standard TrpPath pipeline (pre-launch parsing, icon extraction)
        /// applies to sets RPCS3 has never created a trophy folder for. Living in
        /// icon_cache means the standard cache clearing covers it, and the next
        /// refresh re-materializes it from the ISO. Returns null when no plugin
        /// data path is configured or the write fails.
        /// </summary>
        private GameTrophySource MaterializeTrpSource(byte[] trpBytes, string normalizedNpCommId, string titleName, string isoPath)
        {
            if (string.IsNullOrWhiteSpace(_pluginUserDataPath))
            {
                return null;
            }

            try
            {
                var cacheDirectory = Path.Combine(_pluginUserDataPath, "icon_cache", "rpcs3", normalizedNpCommId);
                var cachedTrpPath = Path.Combine(cacheDirectory, "TROPHY.TRP");
                var changed = !File.Exists(cachedTrpPath) || !File.ReadAllBytes(cachedTrpPath).SequenceEqual(trpBytes ?? Array.Empty<byte>());
                if (changed)
                {
                    Directory.CreateDirectory(cacheDirectory);
                    var temporaryPath = cachedTrpPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllBytes(temporaryPath, trpBytes);
                        if (File.Exists(cachedTrpPath))
                        {
                            File.Replace(temporaryPath, cachedTrpPath, null);
                        }
                        else
                        {
                            File.Move(temporaryPath, cachedTrpPath);
                        }
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }

                    foreach (var staleIcon in Directory.EnumerateFiles(cacheDirectory, "TROP*.PNG"))
                    {
                        File.Delete(staleIcon);
                    }

                    _logger?.Info($"[RPCS3] Refreshed cached trophy set '{normalizedNpCommId}' ('{titleName}') from ISO '{isoPath}'.");
                }

                return new GameTrophySource
                {
                    NpCommId = normalizedNpCommId,
                    TrpPath = cachedTrpPath,
                    SourceTitle = titleName
                };
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[RPCS3] Failed to cache trophy set '{normalizedNpCommId}' from '{isoPath}'");
                return null;
            }
        }

        /// <summary>
        /// Rejects same-title trophy sets surfaced from one ISO. Distinct titles are
        /// a genuine multi-game collection; identical titles are region variants of
        /// one game, where picking a set would risk wrong trophy data. A set RPCS3
        /// itself created a trophy folder for (TrpPath null here) wins over cached
        /// duplicates; otherwise every duplicate is dropped.
        /// </summary>
        private List<GameTrophySource> ApplyTitleAmbiguityGuard(List<GameTrophySource> sources, List<string> dropped, string isoPath)
        {
            if (sources.Count <= 1)
            {
                return sources;
            }

            var kept = new List<GameTrophySource>();
            foreach (var group in sources.GroupBy(
                source => string.IsNullOrWhiteSpace(source.SourceTitle)
                    ? source.NpCommId
                    : NormalizeGameName(source.SourceTitle),
                StringComparer.OrdinalIgnoreCase))
            {
                var members = group.ToList();
                if (members.Count == 1)
                {
                    kept.Add(members[0]);
                    continue;
                }

                var trophyFolderBacked = members.Where(member => member.TrpPath == null).ToList();
                if (trophyFolderBacked.Count == 1)
                {
                    kept.Add(trophyFolderBacked[0]);
                    dropped.AddRange(members.Where(member => member.TrpPath != null).Select(member => member.NpCommId));
                    continue;
                }

                _logger?.Info($"[RPCS3] ISO '{isoPath}' holds same-title trophy sets [{string.Join(", ", members.Select(member => member.NpCommId))}]; ambiguous, none selected.");
                dropped.AddRange(members.Select(member => member.NpCommId));
            }

            return kept;
        }

        private IEnumerable<string> ResolveSharedIsoPathsFromGamesYml(
            IReadOnlyList<GamePathCandidate> candidatePaths,
            Rpcs3SerialNpwrBridge serialBridge)
        {
            var rpcs3Root = serialBridge?.Root;
            if (string.IsNullOrWhiteSpace(rpcs3Root))
            {
                yield break;
            }

            var map = serialBridge.GamesYmlMap;
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
                    continue;
                }

                var matchesCandidate = candidatePaths.Any(candidate => PathsEqual(candidate?.Path, isoPath));
                if (matchesCandidate)
                {
                    yield return isoPath;
                }
            }
        }

        private IReadOnlyDictionary<string, string> ReadRpcs3GamesYmlTitlePathMap(string rpcs3Root)
        {
            return Rpcs3SerialNpwrBridge.ReadTitlePathMap(rpcs3Root, _logger);
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
        /// 3. Resolve PS3 serials from paths/PARAM.SFO through RPCS3's own records
        ///    (dev_hdd0/game installs, games.yml registrations)
        /// 4. Match by game name against TROPCONF.SFM titles
        /// Also returns the TROPHY.TRP path for pre-launch fallback.
        /// </summary>
        private GameTrophySource FindSingleNpCommIdForGame(
            Game game,
            Dictionary<string, string> trophyFolderCache,
            CancellationToken cancel,
            bool allowRawIsoScan,
            Rpcs3SerialNpwrBridge serialBridge)
        {
            var candidates = ResolveGamePathCandidates(game);

            foreach (var candidate in candidates)
            {
                cancel.ThrowIfCancellationRequested();

                var gameDirectory = candidate.Path;

                // Strategy 1: For installed games, check for TROPHY.TRP in game directory.
                // Multi-set results are collection territory and handled by the
                // collection pass, which runs before this method.
                if (!string.IsNullOrWhiteSpace(gameDirectory))
                {
                    var installedSources = FindTrophySourcesFromInstalledGame(gameDirectory, trophyFolderCache);
                    if (installedSources.Count == 1)
                    {
                        return installedSources[0];
                    }
                }

                // Strategy 2: Extract the trophy set from a PS3 ISO file
                var sourceFromIso = FindTrophySourceFromIso(
                    game,
                    gameDirectory,
                    trophyFolderCache,
                    allowRawIsoScan,
                    candidate.AllowDirectoryIsoEnumeration);
                if (sourceFromIso != null)
                {
                    return sourceFromIso;
                }
            }

            // Strategy 3: Resolve PS3 serials through RPCS3's own records
            var bridgeSource = FindNpCommIdFromSerialBridge(game, candidates, serialBridge, trophyFolderCache, allowRawIsoScan);
            if (bridgeSource != null)
            {
                return bridgeSource;
            }

            // Strategy 4: Match by game name against TROPCONF.SFM titles
            var npcommidFromName = FindNpCommIdByName(game, trophyFolderCache);
            if (!string.IsNullOrWhiteSpace(npcommidFromName))
            {
                return new GameTrophySource { NpCommId = npcommidFromName, TrpPath = null };
            }

            return null;
        }

        /// <summary>
        /// Resolves a single trophy source from a game's PS3 ISO files. ISOs whose
        /// usable sets number anything other than one are skipped here; multi-set
        /// ISOs are collection territory.
        /// </summary>
        private GameTrophySource FindTrophySourceFromIso(Game game, string gameDirectory,
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
                foreach (var isoPath in ResolveIsoFilesForCandidate(gameDirectory, game?.Name, allowDirectoryIsoEnumeration))
                {
                    var isoSources = FindIsoTrophySources(isoPath, trophyFolderCache, allowRawIsoScan);
                    if (isoSources.Count == 1)
                    {
                        return isoSources[0];
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RPCS3] Error searching for ISO files in '{gameDirectory}'");
            }

            return null;
        }

        private Rpcs3SerialNpwrBridge CreateSerialBridge(Game game = null)
        {
            var context = _provider?.GetInstallationContext(game) ??
                Rpcs3InstallationResolver.Resolve(game, _providerSettings, _playniteApi, _logger);
            return new Rpcs3SerialNpwrBridge(context, _logger);
        }

        /// <summary>
        /// Resolves a single trophy source for the game from its PS3 serials via
        /// RPCS3's own records. Serials that resolve to zero or several trophy sets
        /// are ignored here (collection detection covers multi-set ISOs); if the
        /// remaining serials disagree on the NPWR id the result is ambiguous and no
        /// match is returned.
        /// </summary>
        private GameTrophySource FindNpCommIdFromSerialBridge(
            Game game,
            IReadOnlyList<GamePathCandidate> candidates,
            Rpcs3SerialNpwrBridge serialBridge,
            Dictionary<string, string> trophyFolderCache,
            bool allowRawIsoScan)
        {
            var serials = DiscoverSerialCandidates(candidates, serialBridge);
            if (serials.Count == 0)
            {
                return null;
            }

            var resolved = new List<(string Serial, GameTrophySource Source)>();
            foreach (var serialCandidate in serials)
            {
                var sources = ResolveSerialTrophySources(
                    serialCandidate.Serial,
                    serialBridge,
                    trophyFolderCache,
                    allowRawIsoScan);
                if (sources.Count == 1)
                {
                    resolved.Add((serialCandidate.Serial, sources[0]));
                }
            }

            if (resolved.Count == 0)
            {
                return null;
            }

            var distinctNpwrs = resolved
                .Select(entry => entry.Source.NpCommId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (distinctNpwrs > 1)
            {
                _logger?.Info($"[RPCS3] Serial bridge for '{game?.Name}' is ambiguous: [{string.Join(", ", resolved.Select(entry => $"{entry.Serial}->{entry.Source.NpCommId}"))}]; no match selected.");
                return null;
            }

            return resolved[0].Source;
        }

        /// <summary>
        /// Extracts candidate PS3 serials for a game: PARAM.SFO TITLE_ID entries
        /// first (the game's own metadata), then serial-shaped tokens from each
        /// candidate path string, then reverse games.yml lookup (entries whose
        /// registered path equals a candidate path).
        /// </summary>
        private IReadOnlyList<(string Serial, string Origin)> DiscoverSerialCandidates(
            IReadOnlyList<GamePathCandidate> candidates,
            Rpcs3SerialNpwrBridge serialBridge)
        {
            var results = new List<(string Serial, string Origin)>();
            var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (candidates == null || candidates.Count == 0)
            {
                return results;
            }

            foreach (var candidate in candidates)
            {
                foreach (var paramSfoPath in EnumerateParamSfoProbes(candidate?.Path))
                {
                    if (!File.Exists(paramSfoPath))
                    {
                        continue;
                    }

                    var titleId = Rpcs3ParamSfoReader.ReadStringValue(paramSfoPath, "TITLE_ID", _logger);
                    if (Rpcs3SerialNpwrBridge.TryNormalizeSerial(titleId, out var serial) &&
                        seenSerials.Add(serial))
                    {
                        results.Add((serial, $"PARAM.SFO '{paramSfoPath}'"));
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                foreach (var serial in Rpcs3SerialNpwrBridge.ExtractSerials(candidate?.Path))
                {
                    if (seenSerials.Add(serial))
                    {
                        results.Add((serial, $"path '{candidate.Path}'"));
                    }
                }
            }

            if (serialBridge != null)
            {
                List<(string Serial, string ResolvedPath)> registeredGames = null;

                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate?.Path))
                    {
                        continue;
                    }

                    if (registeredGames == null)
                    {
                        registeredGames = serialBridge.GamesYmlMap
                            .Select(entry => (Serial: entry.Key, ResolvedPath: ResolvePathAgainstRoot(entry.Value, serialBridge.Root)))
                            .ToList();
                    }

                    foreach (var entry in registeredGames)
                    {
                        if (PathsEqual(candidate.Path, entry.ResolvedPath) &&
                            Rpcs3SerialNpwrBridge.TryNormalizeSerial(entry.Serial, out var serial) &&
                            seenSerials.Add(serial))
                        {
                            results.Add((serial, $"games.yml '{entry.ResolvedPath}'"));
                        }
                    }
                }
            }

            return results;
        }

        private static IEnumerable<string> EnumerateParamSfoProbes(string candidatePath)
        {
            var current = candidatePath?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(current))
            {
                yield break;
            }

            if (File.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
            {
                yield break;
            }

            yield return Path.Combine(current, "PARAM.SFO");
            yield return Path.Combine(current, "PS3_GAME", "PARAM.SFO");

            // Playnite may point at USRDIR; PARAM.SFO is a sibling in the game root.
            var normalized = TrimTrailingSeparators(current);
            if (normalized.EndsWith("USRDIR", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(normalized);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    yield return Path.Combine(parent, "PARAM.SFO");
                }
            }
        }

        /// <summary>
        /// Resolves a serial to its trophy sources through RPCS3's own records,
        /// memoized per bridge instance. Returns clones so per-game consumers never
        /// share mutable source instances.
        /// </summary>
        private IReadOnlyList<GameTrophySource> ResolveSerialTrophySources(
            string serial,
            Rpcs3SerialNpwrBridge serialBridge,
            Dictionary<string, string> trophyFolderCache,
            bool allowRawIsoScan)
        {
            if (string.IsNullOrWhiteSpace(serial) || serialBridge == null)
            {
                return Array.Empty<GameTrophySource>();
            }

            if (!serialBridge.TryGetMemoizedSources(serial, out var sources))
            {
                sources = ResolveSerialTrophySourcesCore(serial, serialBridge, trophyFolderCache, allowRawIsoScan);
                serialBridge.MemoizeSources(serial, sources);
            }

            return sources.Select(CloneTrophySource).ToList();
        }

        private IReadOnlyList<GameTrophySource> ResolveSerialTrophySourcesCore(
            string serial,
            Rpcs3SerialNpwrBridge serialBridge,
            Dictionary<string, string> trophyFolderCache,
            bool allowRawIsoScan)
        {
            // PKG installs live under the emulator's own dev_hdd0/game/{serial}.
            if (!string.IsNullOrWhiteSpace(serialBridge.DevHdd0Root))
            {
                var installedDir = Path.Combine(serialBridge.DevHdd0Root, "game", serial);
                if (Directory.Exists(installedDir))
                {
                    var installedSources = FindTrophySourcesFromInstalledGame(installedDir, trophyFolderCache);
                    if (installedSources.Count > 0)
                    {
                        return installedSources;
                    }
                }
            }

            if (!serialBridge.GamesYmlMap.TryGetValue(serial, out var registeredPath))
            {
                return Array.Empty<GameTrophySource>();
            }

            var resolvedPath = ResolvePathAgainstRoot(registeredPath, serialBridge.Root);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                return Array.Empty<GameTrophySource>();
            }

            if (Directory.Exists(resolvedPath))
            {
                var installedSources = FindTrophySourcesFromInstalledGame(resolvedPath, trophyFolderCache);
                if (installedSources.Count > 0)
                {
                    return installedSources;
                }

                _logger?.Info($"[RPCS3] Serial bridge: '{serial}' games.yml directory '{resolvedPath}' holds no usable trophy set.");
                return Array.Empty<GameTrophySource>();
            }

            if (File.Exists(resolvedPath) &&
                resolvedPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                return FindIsoTrophySources(resolvedPath, trophyFolderCache, allowRawIsoScan);
            }

            _logger?.Info($"[RPCS3] Serial bridge: '{serial}' games.yml path '{resolvedPath}' does not exist.");
            return Array.Empty<GameTrophySource>();
        }

        private static GameTrophySource CloneTrophySource(GameTrophySource source)
        {
            return new GameTrophySource
            {
                NpCommId = source.NpCommId,
                TrpPath = source.TrpPath,
                SourceTitle = source.SourceTitle
            };
        }

        /// <summary>
        /// Extracts the trophy sources from an installed PKG or extracted disc game.
        /// TROPHY.TRP lives at {gameDir}/TROPDIR/{npcommid}/TROPHY.TRP (one directory
        /// per set; multipacks carry several), {gameDir}/TROPHY/TROPHY.TRP, or the same
        /// layouts under PS3_GAME. Playnite's InstallDirectory often points to USRDIR,
        /// so its parent is probed too. Same-title sets (multi-region dumps) are
        /// collapsed to the booted region or dropped by the shared ambiguity guard;
        /// distinct titles are all returned so a multipack resolves as a collection.
        /// </summary>
        private List<GameTrophySource> FindTrophySourcesFromInstalledGame(string gameDirectory,
            Dictionary<string, string> trophyFolderCache)
        {
            var sources = new List<GameTrophySource>();

            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                return sources;
            }

            // Rom paths may point at a file (e.g. USRDIR\EBOOT.BIN); start from its directory.
            if (File.Exists(gameDirectory))
            {
                gameDirectory = Path.GetDirectoryName(gameDirectory);
                if (string.IsNullOrWhiteSpace(gameDirectory))
                {
                    return sources;
                }
            }

            // Playnite may point to USRDIR, but TROPHY folder is in the game root.
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

            var trpPaths = new List<string>();
            foreach (var dir in directoriesToCheck)
            {
                trpPaths.AddRange(EnumerateTropdirTrpPaths(dir));
                trpPaths.Add(Path.Combine(dir, "TROPHY", "TROPHY.TRP"));
                trpPaths.Add(Path.Combine(dir, "PS3_GAME", "TROPHY", "TROPHY.TRP"));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var trpPath in trpPaths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var source = ReadTrophySourceFromTrpFile(trpPath);
                if (source != null && seen.Add(source.NpCommId))
                {
                    sources.Add(source);
                }
            }

            return ApplySourceTitleAmbiguityGuard(sources, trophyFolderCache, gameDirectory, "installed game");
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
                // PKG and disc games: TROPDIR contains subdirectories named after npcommid
                var tropdirTrpPath = EnumerateTropdirTrpPaths(dir).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(tropdirTrpPath))
                {
                    return tropdirTrpPath;
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
        /// Reads a trophy set's identity (NPWR id and title) from an on-disk
        /// TROPHY.TRP in one pass and returns a TRP-backed source, or null when
        /// no NPWR id can be extracted. The title feeds per-set categories and
        /// the same-title ambiguity guard for sets RPCS3 has never created a
        /// trophy folder for.
        /// </summary>
        private GameTrophySource ReadTrophySourceFromTrpFile(string trpPath)
        {
            if (string.IsNullOrWhiteSpace(trpPath) || !File.Exists(trpPath))
            {
                return null;
            }

            string npCommId = null;
            string titleName = null;
            try
            {
                var trpBytes = File.ReadAllBytes(trpPath);
                Rpcs3TrophyParser.TryReadTrpIdentity(trpBytes, out npCommId, out titleName, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[RPCS3] Failed to extract NPWR ID from '{trpPath}'");
            }

            if (string.IsNullOrWhiteSpace(npCommId))
            {
                // Raw byte scan for anything the container reader could not handle.
                npCommId = Rpcs3NpCommIdExtractor.ExtractFirstNpCommIdFromRawFile(trpPath, _logger);
                titleName = null;
            }

            var normalized = Rpcs3MatchIdHelper.Normalize(npCommId);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return new GameTrophySource
            {
                NpCommId = normalized,
                TrpPath = trpPath,
                SourceTitle = string.IsNullOrWhiteSpace(titleName) ? null : titleName
            };
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
        /// any best-score tie is treated as ambiguous and yields no match. This
        /// includes identical-title regional variants: deterministic selection is
        /// still unsafe when it can choose the wrong trophy set.
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
            if (best.Count > 1)
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
