using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal sealed class RetroAchievementsScanner
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly RetroAchievementsApiClient _api;
        private readonly RetroAchievementsHashIndexStore _hashIndexStore;
        private readonly RetroAchievementsPathResolver _pathResolver;
        private readonly RetroAchievementsHashCacheStore _hashCache;
        private readonly Func<DiskImageService> _diskImageServiceResolver;
        private readonly RetroAchievementsCandidateHasher _candidateHasher;
        private readonly Dictionary<int, List<Models.RaGameListWithTitle>> _gameListCache = new();

        // Unsaved cache writes that trigger a mid-scan save, so progress survives a crash or cancel.
        private const int HashCacheSaveBatch = 25;

        public RetroAchievementsScanner(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            RetroAchievementsApiClient api,
            RetroAchievementsHashIndexStore hashIndexStore,
            RetroAchievementsPathResolver pathResolver,
            RetroAchievementsHashCacheStore hashCache,
            Func<DiskImageService> diskImageServiceResolver = null)
        {
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _hashIndexStore = hashIndexStore ?? throw new ArgumentNullException(nameof(hashIndexStore));
            _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
            _hashCache = hashCache ?? throw new ArgumentNullException(nameof(hashCache));
            _diskImageServiceResolver = diskImageServiceResolver;
            _candidateHasher = new RetroAchievementsCandidateHasher(logger);
        }

        /// <summary>
        /// Attempts to validate a cached RA game ID by checking file metadata.
        /// Returns true if cache is valid and hashing can be skipped.
        /// </summary>
        private bool TryValidateCache(
            Game game,
            RaHashCacheEntry entry,
            IReadOnlyList<string> candidates,
            out int raGameId)
        {
            raGameId = 0;

            if (entry == null || entry.Resolution != RaHashCacheResolution.HashMatch || entry.RaGameId <= 0)
            {
                return false;
            }

            // Check if the previously matched path is still a candidate
            var matchedPath = candidates.FirstOrDefault(c =>
                string.Equals(c, entry.MatchedRomPath, StringComparison.OrdinalIgnoreCase));

            if (matchedPath == null)
            {
                return false;
            }

            if (entry.Dependencies != null && entry.Dependencies.Count > 0)
            {
                if (!RetroAchievementsHashCacheStore.ValidateDependencySnapshot(entry.Dependencies))
                {
                    return false;
                }
            }
            else
            {
                // Validate legacy single-file cache entries.
                try
                {
                    var fileInfo = new FileInfo(matchedPath);
                    if (!fileInfo.Exists)
                    {
                        return false;
                    }

                    if (fileInfo.Length != entry.FileSize)
                    {
                        return false;
                    }

                    if (fileInfo.LastWriteTimeUtc.Ticks != entry.LastWriteTicksUtc)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            raGameId = entry.RaGameId;
            _logger?.Info($"[RA] Cache hit for '{game.Name}': skipping hash");
            return true;
        }

        private static bool TryGetCachedNameMatch(
            RaHashCacheEntry entry,
            Game game,
            int? consoleId,
            out int raGameId,
            out int matchConsoleId)
        {
            raGameId = 0;
            matchConsoleId = 0;

            if (entry == null ||
                entry.Resolution != RaHashCacheResolution.NameMatch ||
                entry.RaGameId <= 0 ||
                !entry.NameMatchedConsoleId.HasValue ||
                !string.Equals(entry.NameMatchedGameName, game?.Name, StringComparison.Ordinal))
            {
                return false;
            }

            if (consoleId.HasValue && consoleId.Value != entry.NameMatchedConsoleId.Value)
            {
                return false;
            }

            raGameId = entry.RaGameId;
            matchConsoleId = entry.NameMatchedConsoleId.Value;
            return true;
        }

        /// <summary>
        /// Records how a game resolved, together with the hashes computed for its candidates.
        /// Skips the write when nothing differs from the cached entry, so unchanged games
        /// do not trigger cache saves.
        /// </summary>
        private void StoreHashCacheEntry(
            Game game,
            RaHashCacheEntry previous,
            RaHashCacheResolution resolution,
            int raGameId,
            string matchedPath,
            int? nameMatchConsoleId,
            List<RaHashCacheCandidate> candidateRecords,
            bool candidatesChanged)
        {
            if (!candidatesChanged &&
                previous != null &&
                previous.Resolution == resolution &&
                previous.RaGameId == raGameId &&
                string.Equals(previous.MatchedRomPath, matchedPath, StringComparison.OrdinalIgnoreCase) &&
                previous.NameMatchedConsoleId == nameMatchConsoleId &&
                (resolution != RaHashCacheResolution.NameMatch ||
                 string.Equals(previous.NameMatchedGameName, game?.Name, StringComparison.Ordinal)))
            {
                return;
            }

            try
            {
                var entry = new RaHashCacheEntry
                {
                    Resolution = resolution,
                    RaGameId = raGameId,
                    Candidates = candidateRecords
                };

                if (resolution == RaHashCacheResolution.HashMatch && !string.IsNullOrWhiteSpace(matchedPath))
                {
                    var fi = new FileInfo(matchedPath);
                    entry.MatchedRomPath = matchedPath;
                    entry.FileSize = fi.Length;
                    entry.LastWriteTicksUtc = fi.LastWriteTimeUtc.Ticks;
                    entry.Dependencies = candidateRecords?
                        .FirstOrDefault(c => string.Equals(c.Path, matchedPath, StringComparison.OrdinalIgnoreCase))?
                        .Dependencies
                        ?? RetroAchievementsHashCacheStore.CaptureDependencySnapshot(matchedPath);
                }
                else if (resolution == RaHashCacheResolution.NameMatch)
                {
                    entry.NameMatchedGameName = game?.Name;
                    entry.NameMatchedConsoleId = nameMatchConsoleId;
                }

                _hashCache.Set(game.Id, entry);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[RA] Failed to record hash cache entry for '{game?.Name}'.");
            }
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            if (string.IsNullOrWhiteSpace(providerSettings.RaUsername) || string.IsNullOrWhiteSpace(providerSettings.RaWebApiKey))
            {
                _logger?.Warn("[RA] Missing RetroAchievements credentials - cannot scan achievements.");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }

            if (gamesToRefresh is null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            // Create rate limiter with exponential backoff
            var rateLimiter = new RateLimiter(
                _settings.Persisted.ScanDelayMs,
                _settings.Persisted.MaxRetryAttempts);

            try
            {
                return await RunScanAsync(gamesToRefresh, onGameStarting, onGameCompleted, providerSettings, rateLimiter, cancel).ConfigureAwait(false);
            }
            finally
            {
                _hashCache.Save();
            }
        }

        private Task<RebuildPayload> RunScanAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            RetroAchievementsSettings providerSettings,
            RateLimiter rateLimiter,
            CancellationToken cancel)
        {
            return ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    if (game == null)
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    if (!IsStillSelectedProvider(game))
                    {
                        _logger?.Info($"[RA] Skipping '{game.Name}' because its provider changed before scanning started.");
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var hasOverride = RetroAchievementsDataProvider.TryGetGameIdOverride(game.Id, out _);
                    var hasResolvedConsole = RaConsoleIdResolver.TryResolve(game, out var resolvedConsoleId);
                    var consoleId = hasResolvedConsole ? (int?)resolvedConsoleId : null;
                    var hasher = consoleId.HasValue
                        ? RaHasherFactory.Create(consoleId.Value, _settings, _logger)
                        : null;
                    var canUseNameFallback = RetroAchievementsCapabilityHelper.CanUseNameFallback(game, providerSettings, hasResolvedConsole);

                    if (!hasOverride && !canUseNameFallback && (!consoleId.HasValue || hasher == null))
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var data = await rateLimiter.ExecuteWithRetryAsync(
                        () => ScanGameAsync(game, consoleId, hasher, token),
                        IsTransientError,
                        token).ConfigureAwait(false);

                    _hashCache.SaveIfPending(HashCacheSaveBatch);

                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Warn(ex, $"[RA] Failed to scan game '{game?.Name}' after {consecutiveErrors} consecutive errors.");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: (consecutiveErrors, token) => rateLimiter.DelayAfterErrorAsync(consecutiveErrors, token),
                cancel);
        }

        /// <summary>
        /// Determines if an exception is a transient error that should trigger retry.
        /// </summary>
        private static bool IsTransientError(Exception ex)
        {
            return TransientErrorClassifier.IsTransient(ex);
        }

        private async Task<GameAchievementData> ScanGameAsync(Game game, int? consoleId, IRaHasher hasher, CancellationToken cancel)
        {
            var raSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();

            // Check for manual override first (survives cache clears)
            if (RetroAchievementsDataProvider.TryGetGameIdOverride(game.Id, out var overriddenId))
            {
                _logger?.Info($"[RA] Using manual RA ID override: '{game.Name}' -> {overriddenId}");
                var result = await FetchGameInfoAsync(game, overriddenId, consoleId, cancel).ConfigureAwait(false);
                if (result != null)
                {
                    result.IsAppIdOverridden = true;
                }
                return result;
            }

            _hashCache.TryGet(game.Id, out var cachedEntry);
            var candidateRecords = new List<RaHashCacheCandidate>();
            var candidatesChanged = false;

            if (consoleId.HasValue && hasher != null)
            {
                var candidates = _pathResolver.ResolveCandidateFilePaths(game).ToList();
                _logger?.Info($"[RA] Scanning '{game?.Name}' consoleId={consoleId.Value} hasher={hasher.Name} candidates={candidates.Count}.");

                // Try to skip hashing if file unchanged
                if (TryValidateCache(game, cachedEntry, candidates, out var cachedRaGameId))
                {
                    // Still verify with RA API
                    var cached = await FetchGameInfoWithOutcomeAsync(game, cachedRaGameId, consoleId, cancel).ConfigureAwait(false);
                    if (cached.Outcome != FetchOutcome.NotFound)
                    {
                        // A fetch failure keeps the entry: the file still matches, only the request failed.
                        return cached.Data;
                    }

                    _logger?.Info($"[RA] Cached gameId={cachedRaGameId} no longer exists for '{game.Name}', re-matching");
                    _hashCache.Remove(game.Id);
                    cachedEntry = null;
                }

                var hashMatch = await TryResolveByHashAsync(
                    consoleId.Value, hasher, raSettings, candidates, cachedEntry, candidateRecords, cancel).ConfigureAwait(false);
                candidatesChanged = hashMatch.CandidatesChanged;

                if (hashMatch.GameId > 0)
                {
                    StoreHashCacheEntry(game, cachedEntry, RaHashCacheResolution.HashMatch, hashMatch.GameId,
                        hashMatch.MatchedPath, null, candidateRecords, candidatesChanged);
                    return await FetchGameInfoAsync(game, hashMatch.GameId, consoleId, cancel).ConfigureAwait(false);
                }
            }
            else
            {
                _logger?.Info($"[RA] Scanning '{game?.Name}' without hash stage (consoleId={(consoleId.HasValue ? consoleId.Value.ToString() : "n/a")}, hasher={(hasher?.Name ?? "(none)")}).");
            }

            // Try name-based fallback if enabled
            if (raSettings.EnableRaNameFallback)
            {
                var nameMatchId = 0;
                var nameMatchConsoleId = 0;

                if (TryGetCachedNameMatch(cachedEntry, game, consoleId, out var cachedNameId, out var cachedNameConsoleId))
                {
                    _logger?.Info($"[RA] Using cached name match for '{game.Name}' -> gameId={cachedNameId}");
                    nameMatchId = cachedNameId;
                    nameMatchConsoleId = cachedNameConsoleId;
                }
                else if (consoleId.HasValue)
                {
                    nameMatchId = await TryMatchGameByNameAsync(game, consoleId.Value, cancel).ConfigureAwait(false);
                    nameMatchConsoleId = consoleId.Value;
                    if (nameMatchId > 0)
                    {
                        _logger?.Info($"[RA] Name-based fallback matched gameId={nameMatchId} for '{game.Name}'");
                    }
                    else
                    {
                        _logger?.Info($"[RA] Name-based fallback found no match for '{game.Name}'");
                    }
                }
                else if (RetroAchievementsCapabilityHelper.CanUsePlatformlessNameFallback(game, raSettings))
                {
                    var nameMatch = await TryMatchGameByNameAcrossConsolesAsync(game, cancel).ConfigureAwait(false);
                    if (nameMatch != null)
                    {
                        _logger?.Info($"[RA] Platformless name fallback matched '{game.Name}' -> gameId={nameMatch.GameId} consoleId={nameMatch.ConsoleId}");
                        nameMatchId = nameMatch.GameId;
                        nameMatchConsoleId = nameMatch.ConsoleId;
                    }
                    else
                    {
                        _logger?.Info($"[RA] Platformless name fallback found no unambiguous match for '{game.Name}'");
                    }
                }

                if (nameMatchId > 0)
                {
                    var named = await FetchGameInfoWithOutcomeAsync(game, nameMatchId, nameMatchConsoleId, cancel).ConfigureAwait(false);
                    if (named.Outcome == FetchOutcome.NotFound)
                    {
                        _hashCache.Remove(game.Id);
                    }
                    else
                    {
                        StoreHashCacheEntry(game, cachedEntry, RaHashCacheResolution.NameMatch, nameMatchId,
                            null, nameMatchConsoleId, candidateRecords, candidatesChanged);
                    }

                    return named.Data;
                }
            }

            if (candidateRecords.Count > 0)
            {
                StoreHashCacheEntry(game, cachedEntry, RaHashCacheResolution.None, 0,
                    null, null, candidateRecords, candidatesChanged);
            }

            return BuildNoAchievements(game, appId: 0);
        }

        private readonly struct HashResolution
        {
            public HashResolution(int gameId, string matchedPath, bool candidatesChanged)
            {
                GameId = gameId;
                MatchedPath = matchedPath;
                CandidatesChanged = candidatesChanged;
            }

            public int GameId { get; }
            public string MatchedPath { get; }
            public bool CandidatesChanged { get; }
        }

        /// <summary>
        /// Matches candidate hashes against the console's hash index. Hashes recorded for an
        /// unchanged file are re-matched without reading it; only new or changed files are hashed.
        /// Every candidate examined is appended to <paramref name="candidateRecords"/>.
        /// </summary>
        private async Task<HashResolution> TryResolveByHashAsync(
            int consoleId,
            IRaHasher hasher,
            RetroAchievementsSettings raSettings,
            IReadOnlyList<string> candidates,
            RaHashCacheEntry cachedEntry,
            List<RaHashCacheCandidate> candidateRecords,
            CancellationToken cancel)
        {
            var changed = false;

            // Without a file to read there is nothing to match, so skip the index fetch.
            if (!candidates.Any(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c)))
            {
                return new HashResolution(0, null, false);
            }

            Dictionary<string, int> index;
            try
            {
                index = await _hashIndexStore.GetHashIndexAsync(consoleId, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RA] Failed to load hash index for consoleId={consoleId}: {ex.Message}");
                return new HashResolution(0, null, false);
            }

            if (index == null)
            {
                return new HashResolution(0, null, false);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                cancel.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
                {
                    continue;
                }

                var result = await _candidateHasher.HashAsync(
                    candidate, hasher, raSettings, cachedEntry, index.ContainsKey, cancel).ConfigureAwait(false);
                if (result == null)
                {
                    continue;
                }

                candidateRecords.Add(result.Record);
                changed |= !result.FromCache;

                if (result.MatchedHash != null && index.TryGetValue(result.MatchedHash, out var gameId))
                {
                    if (result.FromCache)
                    {
                        _logger?.Info($"[RA] Recorded hash of '{candidate}' matched gameId={gameId}: skipping hash");
                    }

                    return new HashResolution(gameId, candidate, changed);
                }
            }

            return new HashResolution(0, null, changed);
        }

        private enum FetchOutcome
        {
            Ok,
            NotFound,
            Failed
        }

        private async Task<GameAchievementData> FetchGameInfoAsync(Game game, int gameId, int? consoleId, CancellationToken cancel)
        {
            var fetched = await FetchGameInfoWithOutcomeAsync(game, gameId, consoleId, cancel).ConfigureAwait(false);
            return fetched.Data;
        }

        /// <summary>
        /// Fetches a game's sets. <see cref="FetchOutcome.NotFound"/> means RA returned no game for
        /// the ID; <see cref="FetchOutcome.Failed"/> means the request itself failed, which says
        /// nothing about whether the ID is still valid.
        /// </summary>
        private async Task<(GameAchievementData Data, FetchOutcome Outcome)> FetchGameInfoWithOutcomeAsync(
            Game game,
            int gameId,
            int? consoleId,
            CancellationToken cancel)
        {
            try
            {
                var raSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
                var gameInfo = await _api.GetGameInfoAndUserProgressAsync(gameId, cancel).ConfigureAwait(false);
                if (gameInfo == null || (gameInfo.GameId <= 0 && string.IsNullOrWhiteSpace(gameInfo.GameTitle)))
                {
                    _logger?.Warn($"[RA] RetroAchievements returned no game for gameId={gameId}.");
                    return (BuildNoAchievements(game, appId: gameId), FetchOutcome.NotFound);
                }

                var subsetConsoleId = RetroAchievementsSubsetConsoleResolver.Resolve(gameInfo, consoleId);
                var selectedSubsetGameIds = game != null
                    ? GameCustomDataLookup.GetRetroAchievementsSelectedSubsetGameIds(game.Id)
                    : Array.Empty<int>();
                var hasGameIdOverride = game != null &&
                    RetroAchievementsDataProvider.TryGetGameIdOverride(game.Id, out _);

                var sets = await RetroAchievementsSetAssembler.AssembleAsync(
                    gameInfo,
                    gameId,
                    subsetConsoleId,
                    _hashIndexStore,
                    raSettings,
                    (setId, ct) => _api.GetGameInfoAndUserProgressAsync(setId, ct),
                    _logger,
                    cancel,
                    selectedSubsetGameIds: selectedSubsetGameIds,
                    scanAllSubsetsWhenSelectionEmpty: hasGameIdOverride).ConfigureAwait(false);

                await DownloadCategoryImagesAsync(game?.Id, sets.CategoryImageSources, cancel).ConfigureAwait(false);

                var data = new GameAchievementData
                {
                    AppId = gameId,
                    GameName = game?.Name,
                    ProviderKey = "RetroAchievements",
                    LibrarySourceName = game?.Source?.Name,
                    LastUpdatedUtc = DateTime.UtcNow,
                    HasAchievements = sets.Achievements.Count > 0,
                    PlayniteGameId = game?.Id,
                    IsAchievementSchemaAuthoritative = selectedSubsetGameIds.Count > 0,
                    Achievements = sets.Achievements
                };
                return (data, FetchOutcome.Ok);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RA] Failed to fetch game info for gameId={gameId}: {ex.Message}");
                return (BuildNoAchievements(game, appId: gameId), FetchOutcome.Failed);
            }
        }

        // Downloads default category art for the base set and subsets to deterministic per-game
        // cache paths. Best-effort: failures never fail the scan. Existing targets are skipped,
        // so re-scans cost nothing.
        private async Task DownloadCategoryImagesAsync(
            Guid? playniteGameId,
            IReadOnlyList<(string CategoryLabel, Models.RaGameInfoUserProgress Info)> sources,
            CancellationToken cancel)
        {
            if (playniteGameId == null || playniteGameId.Value == Guid.Empty)
            {
                return;
            }

            var diskImageService = _diskImageServiceResolver?.Invoke();
            if (diskImageService == null)
            {
                return;
            }

            var plan = RetroAchievementsCategoryImagePlanner.BuildCategoryImagePlan(sources);
            if (plan.Count == 0)
            {
                return;
            }

            var gameIdText = playniteGameId.Value.ToString("D");
            foreach (var entry in plan)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var artTarget = diskImageService.GetDefaultCategoryImagePath(
                        gameIdText, entry.Label);
                    // decodeSize 0 stores the original bytes: no square crop, original aspect.
                    await diskImageService.GetOrDownloadIconToPathAsync(
                        entry.ArtUrl, artTarget, decodeSize: 0, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[RA] Default category image download failed for category '{entry.Label}'.");
                }
            }
        }

        private static GameAchievementData BuildNoAchievements(Game game, int appId)
        {
            return null;
        }

        private sealed class PlatformlessNameMatch
        {
            public int GameId { get; set; }
            public int ConsoleId { get; set; }
            public string Title { get; set; }
        }

        private async Task<int> TryMatchGameByNameAsync(Game game, int consoleId, CancellationToken cancel)
        {
            var games = await GetOrFetchGameListAsync(consoleId, cancel).ConfigureAwait(false);
            if (games == null || games.Count == 0)
            {
                return 0;
            }

            var normalizedName = NormalizeGameName(game.Name);

            // Name fallback is intentionally restricted to base sets only.
            // Subset/event-style sets are excluded to avoid incorrect non-base matches.
            var orderedGames = games.OrderByDescending(g => g.Title?.Length ?? 0).ToList();
            var baseSetGames = orderedGames.Where(g => !IsSubsetLikeTitle(g?.Title)).ToList();

            var baseSetMatch = TryFindNameMatch(game, normalizedName, baseSetGames);
            if (baseSetMatch > 0)
            {
                return baseSetMatch;
            }

            return 0;
        }

        private async Task<PlatformlessNameMatch> TryMatchGameByNameAcrossConsolesAsync(Game game, CancellationToken cancel)
        {
            var normalizedName = NormalizeGameName(game?.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            PlatformlessNameMatch exactMatch = null;

            foreach (var consoleId in ConsoleMappingRegistry.Instance
                .GetAllConsoles()
                .Select(console => console.Id)
                .Distinct())
            {
                cancel.ThrowIfCancellationRequested();

                if (!IsStillSelectedProvider(game))
                {
                    _logger?.Info($"[RA] Stopping platformless name fallback for '{game?.Name}' because its provider changed.");
                    return null;
                }

                var games = await GetOrFetchGameListAsync(consoleId, cancel).ConfigureAwait(false);
                if (games == null || games.Count == 0)
                {
                    continue;
                }

                var orderedGames = games.OrderByDescending(g => g.Title?.Length ?? 0).ToList();
                var baseSetGames = orderedGames.Where(g => !IsSubsetLikeTitle(g?.Title)).ToList();

                if (!TryFindExactNameMatch(normalizedName, baseSetGames, out var gameId, out var matchedTitle))
                {
                    continue;
                }

                if (exactMatch != null)
                {
                    _logger?.Warn($"[RA] Platformless name fallback is ambiguous for '{game?.Name}' between consoleId={exactMatch.ConsoleId} and consoleId={consoleId}.");
                    return null;
                }

                exactMatch = new PlatformlessNameMatch
                {
                    GameId = gameId,
                    ConsoleId = consoleId,
                    Title = matchedTitle
                };
            }

            if (exactMatch != null)
            {
                _logger?.Info($"[RA] Platformless name fallback exact match: '{game?.Name}' -> '{exactMatch.Title}' (gameId={exactMatch.GameId}, consoleId={exactMatch.ConsoleId})");
            }

            return exactMatch;
        }

        /// <summary>
        /// Minimum similarity threshold for fuzzy matching (0.0 to 1.0).
        /// A value of 0.85 means titles must be 85% similar to be considered a match.
        /// </summary>
        private const double FuzzyMatchThreshold = 0.85;

        private int TryFindNameMatch(Game game, string normalizedName, IReadOnlyList<Models.RaGameListWithTitle> games)
        {
            if (games == null || games.Count == 0)
            {
                return 0;
            }

            if (TryFindExactNameMatch(normalizedName, games, out var exactMatchId, out var exactMatchTitle))
            {
                _logger?.Info($"[RA] Name fallback: Matched '{game.Name}' -> '{exactMatchTitle}' (ID={exactMatchId})");
                return exactMatchId;
            }

            var raSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            if (raSettings?.EnableFuzzyNameMatching != true)
            {
                return 0;
            }

            // Track best fuzzy match in case no exact match is found
            int bestFuzzyMatchId = 0;
            double bestFuzzyScore = 0;
            string bestFuzzyTitle = null;

            foreach (var raGame in games)
            {
                if (string.IsNullOrWhiteSpace(raGame.Title)) continue;

                var normalizedRaTitle = NormalizeGameName(raGame.Title);

                // Try fuzzy match with Jaro-Winkler similarity
                if (!string.IsNullOrWhiteSpace(normalizedRaTitle) && !string.IsNullOrWhiteSpace(normalizedName))
                {
                    var similarity = StringSimilarity.JaroWinklerSimilarityIgnoreCase(normalizedName, normalizedRaTitle);
                    if (similarity >= FuzzyMatchThreshold && similarity > bestFuzzyScore)
                    {
                        bestFuzzyScore = similarity;
                        bestFuzzyMatchId = raGame.ID;
                        bestFuzzyTitle = raGame.Title;
                    }
                }
            }

            // Return best fuzzy match if one was found above threshold
            if (bestFuzzyMatchId > 0)
            {
                _logger?.Info($"[RA] Name fallback: Fuzzy matched '{game.Name}' -> '{bestFuzzyTitle}' (ID={bestFuzzyMatchId}, score={bestFuzzyScore:F2})");
                return bestFuzzyMatchId;
            }

            return 0;
        }

        private static bool TryFindExactNameMatch(
            string normalizedName,
            IReadOnlyList<Models.RaGameListWithTitle> games,
            out int gameId,
            out string matchedTitle)
        {
            gameId = 0;
            matchedTitle = null;

            if (games == null || games.Count == 0 || string.IsNullOrWhiteSpace(normalizedName))
            {
                return false;
            }

            foreach (var raGame in games)
            {
                if (string.IsNullOrWhiteSpace(raGame?.Title))
                {
                    continue;
                }

                var normalizedRaTitle = NormalizeGameName(raGame.Title);
                if (string.Equals(normalizedName, normalizedRaTitle, StringComparison.OrdinalIgnoreCase))
                {
                    gameId = raGame.ID;
                    matchedTitle = raGame.Title;
                    return true;
                }

                var titleParts = raGame.Title.Split('|');
                if (titleParts.Length <= 1)
                {
                    continue;
                }

                foreach (var part in titleParts)
                {
                    var normalizedPart = NormalizeGameName(part);
                    if (!string.Equals(normalizedName, normalizedPart, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    gameId = raGame.ID;
                    matchedTitle = part.Trim();
                    return true;
                }
            }

            return false;
        }

        private static bool IsSubsetLikeTitle(string title)
        {
            return RetroAchievementsSubsetTitleResolver.IsSubsetLikeTitle(title);
        }

        private async Task<List<Models.RaGameListWithTitle>> GetOrFetchGameListAsync(int consoleId, CancellationToken cancel)
        {
            if (_gameListCache.TryGetValue(consoleId, out var cached))
            {
                return cached;
            }

            try
            {
                // Use the same API call as hash index store to get raw JSON
                var json = await _api.GetGameListPageAsync(consoleId, offset: 0, count: 99999, cancel).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<Models.RaGameListWithTitle>();
                }

                // Try to deserialize as array first (API returns array when h=1 is used)
                List<Models.RaGameListWithTitle> games = null;
                try
                {
                    var arrayItems = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.RaGameListWithTitle[]>(json);
                    if (arrayItems != null)
                    {
                        games = new List<Models.RaGameListWithTitle>(arrayItems);
                    }
                }
                catch (Exception)
                {
                    // Try object format
                }

                // Try as object if array failed
                if (games == null)
                {
                    try
                    {
                        var response = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.RaGameListWithTitleResponse>(json);
                        if (response?.Results != null && response.Results.Count > 0)
                        {
                            games = response.Results;
                        }
                        else if (response?.GameList != null && response.GameList.Count > 0)
                        {
                            games = response.GameList;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Failed to parse
                        _logger?.Debug(ex, "[RA] Failed to parse game list response in both array and object formats.");
                    }
                }

                if (games == null)
                {
                    return new List<Models.RaGameListWithTitle>();
                }

                if (games.Count > 0)
                {
                    _gameListCache[consoleId] = games;
                    _logger?.Info($"[RA] Cached {games.Count} games for consoleId={consoleId}");
                }

                return games;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RA] Failed to fetch game list for consoleId={consoleId}: {ex.Message}");
                return new List<Models.RaGameListWithTitle>();
            }
        }

        private bool IsStillSelectedProvider(Game game)
        {
            if (game == null)
            {
                return false;
            }

            if (GameCustomDataLookup.TryGetProviderOverride(game.Id, out var providerOverride))
            {
                return string.Equals(
                    providerOverride?.ProviderKey,
                    "RetroAchievements",
                    StringComparison.OrdinalIgnoreCase);
            }

            if (_settings?.Persisted?.PreferredProviderOverrides != null &&
                _settings.Persisted.PreferredProviderOverrides.TryGetValue(game.Id, out var preferredProvider) &&
                !string.IsNullOrWhiteSpace(preferredProvider))
            {
                return string.Equals(
                    preferredProvider.Trim(),
                    "RetroAchievements",
                    StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static string NormalizeGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            // Remove RetroAchievements tilde-wrapped tags ("~Homebrew~"), region
            // parentheticals ("(USA)"), and dump-tag brackets ("[T+Eng]"), then
            // collapse separator runs for matching purposes.
            var normalized = GameNameNormalizer.StripTildeTags(name);
            normalized = GameNameNormalizer.StripParentheticals(normalized);
            normalized = GameNameNormalizer.StripBrackets(normalized);
            return GameNameNormalizer.CollapseSeparators(normalized);
        }
    }
}





