using Playnite.SDK;
using PlayniteAchievements.Services.Database.Rows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Centralized read-side service for cached achievement data and hydration overlays.
    /// </summary>
    public sealed class AchievementDataService
    {
        private readonly ICacheManager _cacheService;
        private readonly ICacheReadOptimizations _cacheReadOptimizations;
        private readonly IAchievementOverrideMirror _overrideMirror;
        private readonly GameDataHydrator _hydrator;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly GameCustomDataStore _gameCustomDataStore;
        // The settings wrapper, not its PersistedSettings: CancelEdit replaces the
        // Persisted instance, so a captured instance would keep serving the values the
        // user reverted.
        private readonly PlayniteAchievementsSettings _settings;
        private PersistedSettings Persisted => _settings.Persisted;

        public AchievementDataService(
            ICacheManager cacheService,
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            GameCustomDataStore gameCustomDataStore = null)
        {
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _logger = logger;
            _api = api;
            _gameCustomDataStore = gameCustomDataStore;
            _settings = settings;
            _cacheReadOptimizations = cacheService as ICacheReadOptimizations;
            _overrideMirror = cacheService as IAchievementOverrideMirror;
            _hydrator = new GameDataHydrator(api, settings, _gameCustomDataStore);
            if (_gameCustomDataStore != null)
            {
                _gameCustomDataStore.CustomDataChanged += OnCustomDataChangedForOverview;
            }
        }

        public GameAchievementData GetGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return GetMergedGameAchievementData(playniteGameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetVisibleGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return ProjectVisibleGameAchievementData(
                    GetMergedGameAchievementData(playniteGameId));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get visible achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetRawGameAchievementData(Guid playniteGameId)
        {
            return GetRawGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetRawGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return _cacheService.LoadGameData(playniteGameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        /// <summary>
        /// Raw cached data plus the game's custom achievement projections, with no overlays
        /// applied. A game with only custom achievements yields the synthetic custom data.
        /// </summary>
        public GameAchievementData GetRawGameAchievementDataWithCustomAchievements(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                var data = _cacheService.LoadGameData(playniteGameId.ToString());
                if (data == null)
                {
                    return CreateSyntheticCustomGameData(playniteGameId, LoadCustomData(playniteGameId));
                }

                _hydrator.AppendCustomAchievements(data);
                return data;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public List<string> GetCachedGameIds()
        {
            try
            {
                return _cacheService.GetCachedGameIds() ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get cached game ids");
                return new List<string>();
            }
        }

        public bool HasCachedGameData()
        {
            return GetCachedGameIds().Count > 0;
        }

        public GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GetGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetVisibleGameAchievementData(Guid playniteGameId)
        {
            return GetVisibleGameAchievementData(playniteGameId.ToString());
        }

        /// <summary>
        /// Every cached game without its achievements (custom-only games keep theirs): enough for
        /// identity lookups (provider key, app id, Playnite id, name) at a fraction of loading
        /// every achievement.
        /// </summary>
        public List<GameAchievementData> GetAllCachedGameDataForLookup()
        {
            try
            {
                if (_cacheReadOptimizations == null)
                {
                    return LoadAllCachedGameData();
                }

                var result = _cacheReadOptimizations.LoadAllGameHeadersFast() ?? new List<GameAchievementData>();
                AppendSyntheticCustomOnlyGameData(result);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load cached game identities");
                return new List<GameAchievementData>();
            }
        }
        /// <summary>
        /// The library summary from the resolved rows, with games Playnite hides left out when
        /// summaries leave hidden games out.
        /// </summary>
        /// <param name="days">Whether to read the unlock days the Overview's charts count.</param>
        internal CachedSummaryData GetCachedSummaryDataForOverview(int recentAchievementDetailLimit = 0, bool days = true)
        {
            try
            {
                var hidden = new HashSet<Guid>();
                AddPlayniteHiddenGameIds(hidden);
                var summary = _cacheReadOptimizations?.LoadCachedSummaryDataFast(
                    Math.Max(0, recentAchievementDetailLimit),
                    hidden,
                    AuthoredGame,
                    days);
                var separateLockedIcons = new Dictionary<Guid, bool>();
                foreach (var row in summary?.RecentUnlocks ?? Enumerable.Empty<CachedRecentUnlockData>())
                {
                    var gameId = row.PlayniteGameId ?? Guid.Empty;
                    if (!separateLockedIcons.TryGetValue(gameId, out var separate))
                    {
                        separate = GameCustomDataLookup.ShouldUseSeparateLockedIcons(gameId, Persisted, _gameCustomDataStore);
                        separateLockedIcons[gameId] = separate;
                    }

                    row.UseSeparateLockedIconsWhenAvailable = separate;
                }

                return summary;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get cached summary data");
                return null;
            }
        }

        /// <summary>The resolved reads and the change feed; null without the SQL cache.</summary>
        internal ICacheReadOptimizations CacheReads => _cacheReadOptimizations;

        /// <summary>The library summary without the unlock days, which no theme list counts.</summary>
        internal CachedSummaryData GetCachedSummaryDataForTheme(int recentAchievementDetailLimit = 0) =>
            GetCachedSummaryDataForOverview(recentAchievementDetailLimit, days: false);

        /// <summary>The current user's rows under the summary rules, without the games Playnite hides when summaries leave them out.</summary>
        internal AchievementQuery SummaryQuery()
        {
            var hidden = new HashSet<Guid>();
            AddPlayniteHiddenGameIds(hidden);
            return AchievementQuery.CurrentUser.WithSummaryRules().ExceptGames(hidden);
        }

        /// <summary>Every row <paramref name="query"/> reads, in its order, as the summary maps them; none without the SQL cache.</summary>
        internal List<CachedRecentUnlockData> ReadRows(AchievementQuery query) =>
            _cacheReadOptimizations?.LoadRows(query, null) ?? new List<CachedRecentUnlockData>();

        /// <summary>A game with only authored achievements: named by Playnite, shown as its custom provider.</summary>
        private CachedGameSummaryData AuthoredGame(Guid gameId)
        {
            var customData = LoadCustomData(gameId);
            return new CachedGameSummaryData
            {
                ProviderKey = CustomAchievementProjectionService.ProviderKey,
                ProviderPlatformKey = ResolveCustomProviderPlatformKey(customData?.CustomProviderId),
                GameName = GetGame(gameId)?.Name,
                RaritySource = CustomAchievementProjectionService.ResolveRaritySource(customData?.CustomAchievements) ?? RaritySource.None,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        private Dictionary<Guid, GameCustomDataFile> LoadCustomDataByGameId() =>
            (_gameCustomDataStore?.LoadAll() ?? new List<GameCustomDataFile>())
                .Where(row => row != null && row.PlayniteGameId != Guid.Empty)
                .GroupBy(row => row.PlayniteGameId)
                .ToDictionary(group => group.Key, group => group.Last());

        /// <summary>
        /// With IncludeHiddenGamesInSummaries off, games hidden in Playnite are left out of
        /// summaries the same way as Excluded from Summaries. They join only this resolved set,
        /// never the custom-data flag, so the game menu and Manage Achievements keep showing
        /// the user's own exclusion.
        /// </summary>
        private void AddPlayniteHiddenGameIds(HashSet<Guid> excluded)
        {
            if (excluded == null || Persisted?.IncludeHiddenGamesInSummaries != false)
            {
                return;
            }

            try
            {
                var games = _api?.Database?.Games;
                if (games == null)
                {
                    return;
                }

                foreach (var game in games)
                {
                    if (game?.Hidden == true)
                    {
                        excluded.Add(game.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to resolve Playnite-hidden games for summary exclusion.");
            }
        }

        private static Dictionary<string, CategoryImageOverrideData> CloneCategoryImageOverrideMap(
            IReadOnlyDictionary<string, CategoryImageOverrideData> source)
        {
            var map = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            if (source == null || source.Count == 0)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = AchievementCategoryHelper.NormalizeCategoryOrDefault(pair.Key);
                var art = NormalizeText(pair.Value?.Art);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(art))
                {
                    continue;
                }

                map[key] = new CategoryImageOverrideData
                {
                    Art = art
                };
            }

            return map;
        }


        private GameAchievementData ProjectVisibleGameAchievementData(GameAchievementData source)
        {
            if (source == null)
            {
                return null;
            }

            var sourceAchievements = source.Achievements ?? new List<AchievementDetail>();
            var visibleAchievements = sourceAchievements.Where(achievement => achievement != null && !achievement.IsFiltered).ToList();
            if (visibleAchievements.Count == sourceAchievements.Count)
            {
                return source;
            }

            return new GameAchievementData
            {
                LastUpdatedUtc = source.LastUpdatedUtc,
                ProviderKey = source.ProviderKey,
                ProviderPlatformKey = source.ProviderPlatformKey,
                LibrarySourceName = source.LibrarySourceName,
                RaritySource = source.RaritySource,
                PlatformFields = source.PlatformFields,
                MetadataFields = source.MetadataFields,
                HasAchievements = source.HasAchievements && visibleAchievements.Count > 0,
                ExcludedByUser = source.ExcludedByUser,
                IsAppIdOverridden = source.IsAppIdOverridden,
                GameName = source.GameName,
                AppId = source.AppId,
                ProviderGameKey = source.ProviderGameKey,
                PlayniteGameId = source.PlayniteGameId,
                Game = source.Game,
                AchievementOrder = source.AchievementOrder != null
                    ? new List<string>(source.AchievementOrder)
                    : null,
                AchievementCategoryOrder = source.AchievementCategoryOrder != null
                    ? new List<string>(source.AchievementCategoryOrder)
                    : null,
                AchievementCategoryImageOverrides = CloneCategoryImageOverrideMap(source.AchievementCategoryImageOverrides),
                GameSummaryCategory = source.GameSummaryCategory,
                ExcludedFromSummaries = source.ExcludedFromSummaries,
                UseSeparateLockedIconsWhenAvailable = source.UseSeparateLockedIconsWhenAvailable,
                Achievements = visibleAchievements
            };
        }

        /// <summary>A stored icon override as the plugin displays it.</summary>
        private static string ResolveCustomIconOverridePath(string value, Guid playniteGameId)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            return PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService?
                .ResolveManagedDisplayPath(normalized, playniteGameId.ToString("D"))
                ?? normalized;
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        // Written synchronously inside CustomDataChanged, so the mirror is current before any
        // reader that handles the same event reads the resolved views.
        private void OnCustomDataChangedForOverview(object sender, GameCustomDataChangedEventArgs e)
        {
            // The custom layer mirror carries every field the resolved view shows, notes, goals
            // and order included, so every write resyncs it; the resync diffs and stays silent
            // when nothing moved.
            SyncAchievementFiltersForGame(e?.PlayniteGameId ?? Guid.Empty);
        }

        private void SyncAchievementFiltersForGame(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty || _overrideMirror == null || _gameCustomDataStore == null)
            {
                return;
            }

            // Runs on the caller's thread inside the synchronous CustomDataChanged, which for an
            // editor write is the UI thread, and its cost scales with how many overrides the
            // game has -- it re-reads them all, diffs, then deletes and re-inserts the set. A
            // reset of every row is the largest case there is, so it carries the count.
            using var scope = Common.PerfScope.Start(
                _logger,
                "Filters.SyncAchievementFiltersForGame",
                thresholdMs: 10);

            // A missing custom-data row (deleted) maps to an empty entry list, which removes
            // the game's mirror rows.
            _gameCustomDataStore.TryLoad(playniteGameId, out var customData);
            var entries = BuildOverrideMirrorEntries(customData);
            scope?.SetContext("entries=" + (entries?.Count ?? 0));
            _overrideMirror.ReplaceGameCustomLayer(playniteGameId, entries, BuildGameCustomState(customData));
        }

        /// <summary>
        /// Reconciles the whole AchievementFilters mirror against the custom-data store. Called at startup (covers legacy migrations that bypass
        /// CustomDataChanged) and after full cache invalidations.
        /// </summary>
        internal void SyncAllAchievementFiltersFromCustomData()
        {
            if (_overrideMirror == null)
            {
                return;
            }

            // Built straight off the cached records. Going through LoadCustomDataByGameId
            // deep-cloned every stored record first, and the mirror entries are fresh objects
            // holding scalars, so the copies were read once and dropped -- a full copy of the
            // library's custom data on every full invalidation, for a user who has customized
            // all of it.
            var states = new Dictionary<Guid, GameCustomStateEntry>();
            var entriesByGameId = _gameCustomDataStore?.QueryAll(rows =>
            {
                var map = new Dictionary<Guid, IReadOnlyList<AchievementOverrideMirrorEntry>>();
                foreach (var row in rows)
                {
                    if (row == null || row.PlayniteGameId == Guid.Empty)
                    {
                        continue;
                    }

                    var built = BuildOverrideMirrorEntries(row);
                    if (built.Count > 0)
                    {
                        map[row.PlayniteGameId] = built;
                    }

                    states[row.PlayniteGameId] = BuildGameCustomState(row);
                }

                return map;
            }) ?? new Dictionary<Guid, IReadOnlyList<AchievementOverrideMirrorEntry>>();

            _overrideMirror.ResyncAllAchievementOverrides(entriesByGameId);
            _overrideMirror.ReplaceGameCustomStates(states);
        }

        /// <summary>
        /// Builds a game's desired mirror rows from its custom data: the custom layer of each
        /// override, the user's per-achievement state, and a row per authored achievement, so the
        /// resolved views need nothing from the custom-data store.
        /// </summary>
        private static List<AchievementOverrideMirrorEntry> BuildOverrideMirrorEntries(GameCustomDataFile customData)
        {
            var entries = new Dictionary<string, AchievementOverrideMirrorEntry>(StringComparer.OrdinalIgnoreCase);
            if (customData == null)
            {
                return new List<AchievementOverrideMirrorEntry>();
            }

            var gameId = customData.PlayniteGameId;
            var hasManualLink = customData.ManualLink != null;
            var overrides = customData.AchievementOverrides ??
                new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            MarkPositions(entries, customData.FilteredAchievementApiNames, (entry, _) => entry.IsFiltered = true);
            MarkPositions(entries, customData.SummaryFilteredAchievementApiNames, (entry, _) => entry.IsSummaryFiltered = true);
            MarkPositions(entries, customData.GoalAchievementApiNames, (entry, index) => entry.GoalOrder = index);
            MarkPositions(entries, customData.AchievementOrder, (entry, index) => entry.CustomOrder = index);
            if (customData.CapstonesMaterialized)
            {
                MarkPositions(entries, customData.Capstones?.Select(capstone => capstone?.ApiName), (entry, _) => entry.CapstoneAssigned = true);
            }

            foreach (var pair in overrides)
            {
                var apiName = NormalizeText(pair.Key);
                var userOverride = pair.Value;
                if (apiName == null || userOverride == null)
                {
                    continue;
                }

                var layer = userOverride.ToLayer();
                var unlockedIcon = ResolveCustomIconOverridePath(userOverride.UnlockedIconPath, gameId);
                var lockedIcon = ResolveCustomIconOverridePath(userOverride.LockedIconPath, gameId);
                var note = NormalizeText(userOverride.Note);
                var unlockTime = !hasManualLink ? userOverride.UnlockTimeUtc : null;
                var clearUnlockTime = !hasManualLink && !unlockTime.HasValue && userOverride.ClearUnlockTime;
                if (layer.IsEmpty && unlockedIcon == null && lockedIcon == null && note == null && !unlockTime.HasValue && !clearUnlockTime)
                {
                    continue;
                }

                var entry = ResolveMirrorEntry(entries, apiName);
                entry.SetLayer(layer);
                // Derived tags are the platform's; the views add them back, as the resolver does.
                entry.Tags = AchievementTagHelper.StripDerived(entry.Tags);
                entry.UnlockedIconPath = unlockedIcon;
                entry.LockedIconPath = lockedIcon;
                entry.Note = note;
                entry.UnlockTimeUtc = unlockTime;
                entry.Cleared |= clearUnlockTime ? (long)AchievementField.UnlockTime : 0;
            }

            // Authored achievements exist only in custom data; each row carries what the plugin
            // shows for it, with the user's overrides and capstone set already applied.
            var managedIcons = PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService;
            foreach (var row in CustomAchievementProjectionService.ProjectAchievements(gameId, customData.CustomAchievements, managedIcons))
            {
                overrides.TryGetValue(row.ApiName, out var userOverride);
                row.CustomLayer = userOverride?.ToLayer();
                AchievementLayers.Resolve(row);
                userOverride?.ApplyUnlockTimeTo(row, hasManualLink);
                row.UnlockedIconPath = ResolveCustomIconOverridePath(userOverride?.UnlockedIconPath, gameId) ?? row.UnlockedIconPath;
                row.LockedIconPath = ResolveCustomIconOverridePath(userOverride?.LockedIconPath, gameId) ?? row.LockedIconPath;
                row.Category = CategoryPathHelper.NormalizePath(row.Category);
                row.CategoryKind = CategoryKindHelper.ResolveEffective(
                    CategoryEditor.ResolveStoredCategoryKind(customData.AchievementCategoryKinds, row.Category),
                    row.CategoryKind);
                var entry = ResolveMirrorEntry(entries, row.ApiName);
                entry.SetAuthored(row);
                entry.Note = NormalizeText(userOverride?.Note);
                entry.IsCapstone = customData.CapstonesMaterialized ? entry.CapstoneAssigned : row.IsCapstone;
            }

            return entries.Values.Where(entry => !entry.IsEmpty).ToList();
        }

        /// <summary>The game-level state the resolved views read from the custom-data store.</summary>
        private static GameCustomStateEntry BuildGameCustomState(GameCustomDataFile customData)
        {
            var state = new GameCustomStateEntry
            {
                CapstonesMaterialized = customData?.CapstonesMaterialized == true,
                HasManualLink = customData?.ManualLink != null,
                ExcludedFromSummaries = customData?.ExcludedFromSummaries == true,
                ExcludedFromRefreshes = customData?.ExcludedFromRefreshes == true,
                UseSeparateLockedIcons = customData?.UseSeparateLockedIconsOverride == true
            };
            foreach (var pair in customData?.AchievementCategoryKinds ?? new Dictionary<string, string>())
            {
                var category = CategoryPathHelper.NormalizePath(pair.Key);
                var kind = CategoryKindHelper.NormalizeStored(pair.Value);
                if (category != null && kind != null)
                {
                    state.Category(category).Kind = kind;
                }
            }

            var order = customData?.AchievementCategoryOrder ?? new List<string>();
            for (var i = 0; i < order.Count; i++)
            {
                var category = CategoryPathHelper.NormalizePath(order[i]);
                if (category != null && !state.Category(category).SortIndex.HasValue)
                {
                    state.Category(category).SortIndex = i;
                }
            }

            foreach (var pair in customData?.AchievementCategoryImageOverrides ?? new Dictionary<string, CategoryImageOverrideData>())
            {
                var category = CategoryPathHelper.NormalizePath(pair.Key);
                var art = ResolveCustomIconOverridePath(pair.Value?.Art, customData.PlayniteGameId);
                if (category != null && art != null)
                {
                    state.Category(category).ImagePath = art;
                }
            }

            return state;
        }

        private static void MarkPositions(
            Dictionary<string, AchievementOverrideMirrorEntry> entries,
            IEnumerable<string> apiNames,
            Action<AchievementOverrideMirrorEntry, int> mark)
        {
            var index = 0;
            foreach (var apiName in apiNames ?? Enumerable.Empty<string>())
            {
                var normalized = NormalizeText(apiName);
                if (normalized != null)
                {
                    mark(ResolveMirrorEntry(entries, normalized), index);
                }

                index++;
            }
        }

        private static AchievementOverrideMirrorEntry ResolveMirrorEntry(
            Dictionary<string, AchievementOverrideMirrorEntry> entries,
            string apiName)
        {
            if (!entries.TryGetValue(apiName, out var entry) || entry == null)
            {
                entry = new AchievementOverrideMirrorEntry { ApiName = apiName };
                entries[apiName] = entry;
            }

            return entry;
        }

        private List<GameAchievementData> LoadAllCachedGameData()
        {
            List<GameAchievementData> result;
            if (_cacheReadOptimizations != null)
            {
                result = _cacheReadOptimizations.LoadAllGameDataFast() ?? new List<GameAchievementData>();
                AppendSyntheticCustomOnlyGameData(result);
                return result;
            }

            var gameIds = _cacheService.GetCachedGameIds();
            result = new List<GameAchievementData>();
            foreach (var gameId in gameIds)
            {
                var gameData = _cacheService.LoadGameData(gameId);
                if (gameData != null)
                {
                    result.Add(gameData);
                }
            }

            AppendSyntheticCustomOnlyGameData(result);
            return result;
        }

        private GameAchievementData GetMergedGameAchievementData(string playniteGameId)
        {
            var data = _cacheService.LoadGameData(playniteGameId);
            if (data == null && Guid.TryParse(playniteGameId, out var parsedGameId))
            {
                data = CreateSyntheticCustomGameData(parsedGameId, LoadCustomData(parsedGameId));
            }

            _hydrator.Hydrate(data);

            return data;
        }

        private void AppendSyntheticCustomOnlyGameData(ICollection<GameAchievementData> result)
        {
            if (result == null || _gameCustomDataStore == null)
            {
                return;
            }

            var existingIds = new HashSet<Guid>(
                result
                    .Where(data => data?.PlayniteGameId.HasValue == true && data.PlayniteGameId.Value != Guid.Empty)
                    .Select(data => data.PlayniteGameId.Value));

            foreach (var customData in LoadCustomDataByGameId().Values)
            {
                if (customData == null ||
                    customData.PlayniteGameId == Guid.Empty ||
                    existingIds.Contains(customData.PlayniteGameId) ||
                    !CustomAchievementProjectionService.HasCustomAchievements(customData))
                {
                    continue;
                }

                var synthetic = CreateSyntheticCustomGameData(customData.PlayniteGameId, customData);
                if (synthetic == null)
                {
                    continue;
                }

                result.Add(synthetic);
                existingIds.Add(customData.PlayniteGameId);
            }
        }

        private GameCustomDataFile LoadCustomData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty || _gameCustomDataStore == null)
            {
                return null;
            }

            return _gameCustomDataStore.TryLoad(playniteGameId, out var data)
                ? data
                : null;
        }

        private GameAchievementData CreateSyntheticCustomGameData(
            Guid playniteGameId,
            GameCustomDataFile customData)
        {
            if (!CustomAchievementProjectionService.HasCustomAchievements(customData))
            {
                return null;
            }

            return CustomAchievementProjectionService.CreateSyntheticGameData(
                playniteGameId,
                GetGame(playniteGameId),
                customData.CustomAchievements,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                ResolveCustomProviderPlatformKey(customData.CustomProviderId));
        }

        /// <summary>
        /// The display key for an assigned custom provider, or null when the id is blank or no
        /// longer names a stored provider (the game then displays as plain Custom).
        /// </summary>
        private static string ResolveCustomProviderPlatformKey(string customProviderId)
        {
            if (string.IsNullOrWhiteSpace(customProviderId))
            {
                return null;
            }

            return PlayniteAchievementsPlugin.Instance?.CustomProviderStore?.ResolveDisplayKey(customProviderId);
        }

        private Playnite.SDK.Models.Game GetGame(Guid playniteGameId)
        {
            try
            {
                return _api?.Database?.Games?.Get(playniteGameId);
            }
            catch
            {
                return null;
            }
        }
    }
}
