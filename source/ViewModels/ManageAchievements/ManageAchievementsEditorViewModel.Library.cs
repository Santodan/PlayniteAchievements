using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.CustomProviders;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The library editor: the same editor over every game's achievements, narrowed by a Game
    /// filter. With one game picked it is that game's editor, loaded whole as the Manage window
    /// loads it. With none or several it reads rows a page at a time from the resolved database,
    /// filtered and ordered in SQL, and writes each edit through the row's own game.
    /// </summary>
    /// <remarks>
    /// In the paged mode <see cref="AchievementRows"/> stays empty, so every pass that rebuilds a
    /// game's whole list from it has nothing to rebuild. Categories, tags, filters, icons and
    /// revert are written as patches of the rows edited, one store update per game, which keep
    /// every other achievement's entries as stored. What needs a game's every row (order, adding
    /// and deleting authored achievements, an authored achievement's key, the resets) refuses in
    /// this mode (<see cref="RefuseInPagedMode"/>), and the rows lock the key's editor
    /// (<see cref="AchievementEditorRow.IsListEditLocked"/>).
    /// </remarks>
    public sealed partial class ManageAchievementsEditorViewModel
    {
        private readonly EditorLibrarySources _librarySources;
        private PagedRows<AchievementEditorRow> _pagedRows;
        private Guid _registeredGameId;

        // Games this editor wrote since the host last asked, for the host's cache and theme
        // notifications: the library window has no game of its own to name.
        private readonly HashSet<Guid> _writtenGames = new HashSet<Guid>();

        // The rows the undo history holds changes for, so undoing one finds its row after the
        // paged list dropped the page it was on. Bounded: a history that outgrows it loses rows
        // to undo through, never correctness of the rows still held.
        private readonly Dictionary<string, AchievementEditorRow> _historyRows =
            new Dictionary<string, AchievementEditorRow>(StringComparer.OrdinalIgnoreCase);

        private const int MaxHistoryRows = 5000;

        // Rows edited since the filters last changed. They stay listed, as an edited row stays in
        // the per-game editor until a filter changes, even when the edit moved them out of it.
        private readonly List<KeyValuePair<Guid, string>> _keptRows = new List<KeyValuePair<Guid, string>>();
        private readonly HashSet<string> _keptRowKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _libraryOptionsVersion;

        // Closed while an edit runs over every match, so the list reloads once for it rather than
        // once per chunk it writes.
        private readonly Services.Cache.FeedGate _pagedFeedGate = new Services.Cache.FeedGate();

        /// <summary>
        /// The library editor, opened on every game, or locked to <paramref name="lockedGameIds"/>
        /// when given. The single-game services the Manage window hands its editor (manual
        /// linking, export, import, the Workshop) are not offered here.
        /// </summary>
        internal static ManageAchievementsEditorViewModel ForLibrary(
            EditorLibrarySources sources,
            AchievementOverridesService achievementOverridesService,
            GameCustomDataStore gameCustomDataStore,
            ManagedCustomIconService managedCustomIconService,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            CustomProviderStore customProviderStore = null,
            Func<string, string> pickColor = null,
            Func<CustomProviderEditorViewModel, CustomProviderEditorResult> showEditor = null,
            IEnumerable<Guid> lockedGameIds = null)
        {
            // The opening scope's games are the lock; InitializeLibraryScope takes them from it.
            return new ManageAchievementsEditorViewModel(
                EditorScope.Library(lockedGameIds),
                sources ?? throw new ArgumentNullException(nameof(sources)),
                achievementOverridesService,
                gameCustomDataStore,
                managedCustomIconService,
                null,
                settings,
                logger,
                customProviderStore,
                pickColor,
                showEditor,
                includeProviderAchievements: true);
        }

        public EditorScope Scope { get; private set; }

        /// <summary>Whether this is the library editor, which shows the Game filter and column.</summary>
        public bool IsLibraryScope => Scope.IsLibrary;

        /// <summary>Whether the rows come a page at a time from the database, across games.</summary>
        public bool IsPagedMode => _pagedRows != null;

        /// <summary>Whether the editor lists one game whole, which the single-game features need.</summary>
        public bool AllowsSingleGameFeatures => !IsPagedMode && Scope.AllowsSingleGameFeatures;

        /// <summary>The library editor's Game filter; null in a per-game editor.</summary>
        public ControlBarGameFilter GameFilter { get; private set; }

        /// <summary>The list the grid shows: the paged rows across games, or the one game's rows.</summary>
        public IList GridRows => (IList)_pagedRows ?? AchievementRows;

        /// <summary>The loaded rows the editor can act on: the paged rows loaded now, or every row of the one game.</summary>
        private IEnumerable<AchievementEditorRow> EditableRows =>
            _pagedRows != null
                ? _pagedRows.LoadedItems.OfType<AchievementEditorRow>().Where(row => !row.IsPlaceholder)
                : AchievementRows;

        private void SetActiveGame(Guid gameId)
        {
            _activeGameId = gameId;
            _activeGameIdText = gameId.ToString("D");
        }

        /// <summary>
        /// Points the single-game paths at <paramref name="gameId"/> for a write to one of its rows
        /// and returns the game they pointed at, to restore afterwards. An empty id leaves them.
        /// </summary>
        private Guid EnterGame(Guid gameId)
        {
            var previous = _activeGameId;
            if (gameId != Guid.Empty && gameId != _activeGameId)
            {
                SetActiveGame(gameId);
            }

            return previous;
        }

        /// <summary>The game a row's writes go to.</summary>
        private Guid GameOf(AchievementEditorRow row) => row?.GameId ?? _gameId;

        private bool IsGameInScope(Guid gameId) => Scope.IsLibrary || gameId == _activeGameId;

        private void NoteGameWritten(Guid gameId)
        {
            if (Scope.IsLibrary && gameId != Guid.Empty && (_currentUndoIntent != null || _isApplyingUndo))
            {
                _writtenGames.Add(gameId);
            }
        }

        /// <summary>The games this editor wrote since the last call, then forgets them.</summary>
        internal IReadOnlyCollection<Guid> ConsumeWrittenGames()
        {
            var games = _writtenGames.ToList();
            _writtenGames.Clear();
            if (games.Count == 0 && _activeGameId != Guid.Empty)
            {
                games.Add(_activeGameId);
            }

            return games;
        }

        private static string HistoryRowKey(Guid gameId, string apiName) =>
            gameId.ToString("N") + "\0" + (apiName ?? string.Empty).Trim();

        private void RememberHistoryRow(Guid gameId, AchievementEditorRow row)
        {
            if (!IsPagedMode || row == null || row.IsBulkRow || string.IsNullOrWhiteSpace(row.OriginalApiName))
            {
                return;
            }

            if (_historyRows.Count >= MaxHistoryRows)
            {
                _historyRows.Clear();
            }

            _historyRows[HistoryRowKey(gameId, row.OriginalApiName)] = row;
        }

        private bool TryFindHistoryRow(
            Dictionary<string, AchievementEditorRow> loaded,
            Guid gameId,
            string apiName,
            out AchievementEditorRow row)
        {
            var key = HistoryRowKey(gameId, apiName);
            return loaded.TryGetValue(key, out row) || _historyRows.TryGetValue(key, out row);
        }

        private static string OverrideWriteKey(Guid gameId, string apiName, AchievementEditableField field) =>
            gameId.ToString("N") + "\0" + apiName + "\0" + field;

        /// <summary>
        /// Refuses a gesture that writes a game's whole list from its rows while the rows span
        /// games, and says why. True when refused.
        /// </summary>
        private bool RefuseInPagedMode()
        {
            if (!IsPagedMode)
            {
                return false;
            }

            SetStatus(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Editor_SingleGameOnly"), false);
            return true;
        }

        #region Scope

        private void InitializeLibraryScope()
        {
            _lockedGameIds = Scope.FilterGameIds;
            GameFilter = new ControlBarGameFilter(ApplyLibraryScope);
            GameFilter.SetScope(_lockedGameIds);
            LoadLibraryGames();
            ApplyLibraryScope();
        }

        // The games the library editor is locked to, opened from a selection of games; null for
        // every game. The Game filter lists only these, and with nothing ticked the rows are theirs.
        private IReadOnlyList<Guid> _lockedGameIds;

        /// <summary>
        /// Locks the library editor to <paramref name="gameIds"/>, or frees it to every game when
        /// null or empty, and lists them with nothing ticked in the Game filter.
        /// </summary>
        internal void LockToGames(IEnumerable<Guid> gameIds)
        {
            if (_isDetached || !Scope.IsLibrary)
            {
                return;
            }

            _lockedGameIds = EditorScope.Library(gameIds).FilterGameIds;
            GameFilter.Restore(null);
            GameFilter.SetScope(_lockedGameIds);
            ApplyLibraryScope();
        }

        /// <summary>The games the editor lists: the ticked ones, else the locked ones, else null for every game.</summary>
        private IReadOnlyCollection<Guid> ListedGameIds =>
            GameFilter?.HasSelection == true ? GameFilter.SelectedGameIds : _lockedGameIds;

        /// <summary>
        /// Lists the games of the Game filter: every game the current user has rows of, named as
        /// Playnite names it.
        /// </summary>
        private void LoadLibraryGames()
        {
            var sources = _librarySources;
            sources.Scheduler.Run(() =>
            {
                List<GameSummaryItem> games = null;
                try
                {
                    var cachedNames = new Dictionary<Guid, string>();
                    foreach (var header in sources.Reads.LoadAllGameHeadersFast() ?? new List<GameAchievementData>())
                    {
                        if (header?.PlayniteGameId is Guid id && !cachedNames.ContainsKey(id))
                        {
                            cachedNames[id] = header.GameName;
                        }
                    }

                    var database = PlayniteAchievementsPlugin.Instance?.PlayniteApi?.Database;
                    games = sources.Reads.Reads.Keys(AchievementQuery.CurrentUser, AchievementGroup.Game)
                        .Select(key => Guid.TryParse(key, out var id) ? id : Guid.Empty)
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .Select(id => new GameSummaryItem
                        {
                            PlayniteGameId = id,
                            GameName = database?.Games?.Get(id)?.Name ??
                                       (cachedNames.TryGetValue(id, out var name) ? name : null) ??
                                       id.ToString("D")
                        })
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed to list the games for the achievement editor's Game filter.");
                }

                sources.Scheduler.Post(() =>
                {
                    if (!_isDetached && games != null)
                    {
                        GameFilter?.UpdateGames(games);
                    }
                });
            });
        }

        /// <summary>
        /// Follows the Game filter: one picked game loads whole, as the Manage window loads it;
        /// none or several read pages across games.
        /// </summary>
        private void ApplyLibraryScope()
        {
            if (_isDetached)
            {
                return;
            }

            // The open gesture belongs to the rows it was made on.
            CommitUndoStep();
            var wasVisible = _isRegisteredOpen;
            if (wasVisible)
            {
                SetVisible(false);
            }

            Scope = EditorScope.Library(ListedGameIds);
            ClearAllMatchingSelection();
            ClearKeptRows();
            SetStatus(null, false);
            if (Scope.SingleGameId is Guid single)
            {
                DisposePagedRows();
                SetActiveGame(single);
                _listedGame = ResolveListedGame(single);
                _gameDataSnapshotProvider = new ManageAchievementsDataSnapshotProvider(single, _librarySources.DataService, _logger);
                ReloadData();
            }
            else
            {
                SetActiveGame(Guid.Empty);
                _gameDataSnapshotProvider = null;
                if (AchievementRows.Count > 0)
                {
                    ReplaceRows(Array.Empty<AchievementEditorRow>());
                }

                EnsurePagedRows();
                LoadLibraryFilterOptions();
            }

            if (wasVisible)
            {
                SetVisible(true);
            }

            OnPropertyChanged(nameof(Scope));
            OnPropertyChanged(nameof(IsPagedMode));
            OnPropertyChanged(nameof(AllowsSingleGameFeatures));
            OnPropertyChanged(nameof(ShowLinkFileButton));
            OnPropertyChanged(nameof(ShowEditLinkedFileButton));
            OnPropertyChanged(nameof(GridRows));
            OnPropertyChanged(nameof(GameId));
            RaiseCommandStates();
        }

        #endregion

        #region Paged rows

        /// <summary>
        /// Skeleton rows the editor shows before its first read: enough 66px rows to fill a
        /// maximized window on a 2160px-tall display. Only the visible ones are realized.
        /// </summary>
        private const int PendingSkeletonRows = 40;

        private void EnsurePagedRows()
        {
            var query = BuildLibraryQuery();
            if (_pagedRows != null)
            {
                _pagedRows.SetQuery(query, fromStart: true);
                return;
            }

            _pagedRows = new PagedRows<AchievementEditorRow>(
                _librarySources.Reads.Feed,
                query,
                read => _librarySources.Reads.Reads.SortedKeys(read),
                LoadPagedRows,
                () => new AchievementEditorRow { IsPlaceholder = true, IsListEditLocked = true },
                _librarySources.Scheduler,
                onError: ex => _logger?.Error(ex, "Failed to load the achievement editor's rows."),
                update: UpdatePagedRow,
                gate: _pagedFeedGate,
                placed: AttachPagedRow,
                // Skeleton rows until the first read lands: across every game it is the slowest.
                pendingRows: PendingSkeletonRows);
            _pagedRows.PropertyChanged += PagedRows_PropertyChanged;
            HasRows = _pagedRows.Count > 0;
            Common.LeakWatch.Track("EditorPagedRows", _pagedRows);
        }

        private void DisposePagedRows()
        {
            if (_pagedRows == null)
            {
                return;
            }

            _pagedRows.PropertyChanged -= PagedRows_PropertyChanged;
            _pagedRows.Dispose();
            _pagedRows = null;
            _historyRows.Clear();
        }

        private void PagedRows_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(PagedRows<AchievementEditorRow>.Count))
            {
                HasRows = _pagedRows?.Count > 0;
            }

            RefreshRevealHeaderState();
        }

        /// <summary>Reads the paged rows again, after a write the feed does not carry.</summary>
        private void RefreshPagedRows() => _pagedRows?.Refresh();

        /// <summary>
        /// The library query the filters say: the current user's rows of the picked games, or of
        /// every game, with the editor's filters in SQL, the kept rows, and each game's own order.
        /// </summary>
        private AchievementQuery BuildLibraryQuery()
        {
            var query = LibraryScopeQuery();
            if (_selectedCategoryFilters.Count > 0)
            {
                query = query.WhereCategory(_selectedCategoryFilters);
            }

            if (_selectedTypeFilters.Count > 0)
            {
                query = query.WhereClassification(_selectedTypeFilters);
            }

            if (_selectedStateFilters.Count > 0)
            {
                query = query.WhereAnyKey(_selectedStateFilters);
            }

            if (_selectedCustomizationFilters.Count == 1)
            {
                query = query.WhereCustomized(_selectedCustomizationFilters.Contains(CustomizedFilterKey));
            }

            if (_filterQuery.HasValue)
            {
                query = query.WhereSearch(_filterText, AchievementSearchFields.Editor);
            }

            return query.OrKeep(_keptRows).OrderBy(AchievementSort.Display);
        }

        private AchievementQuery LibraryScopeQuery()
        {
            var query = AchievementQuery.CurrentUser.WithLatestProgressOnly();
            var games = ListedGameIds;
            return games != null ? query.ForGames(games) : query;
        }

        /// <summary>The filters changed: the kept rows go and the list reads from its top.</summary>
        private void ApplyPagedFilters()
        {
            ClearAllMatchingSelection();
            ClearKeptRows();
            _pagedRows?.SetQuery(BuildLibraryQuery(), fromStart: true);
        }

        private void ClearKeptRows()
        {
            _keptRows.Clear();
            _keptRowKeys.Clear();
        }

        /// <summary>Keeps an edited row listed until the filters change.</summary>
        private void KeepEditedRow(AchievementEditorRow row)
        {
            if (AddKeptRow(row))
            {
                _pagedRows.SetQuery(BuildLibraryQuery());
            }
        }

        // The kept rows ride in every page's SQL, so a bulk edit of many rows keeps the first of
        // them only; the rest leave the list as their edit takes them out of the filters. An
        // edit of every match keeps none: it edited the whole list.
        private const int MaxKeptRows = 250;

        private bool AddKeptRow(AchievementEditorRow row)
        {
            if (!IsPagedMode ||
                !IsFiltering ||
                _isApplyingAllMatching ||
                _keptRows.Count >= MaxKeptRows ||
                row?.GameId == null ||
                string.IsNullOrWhiteSpace(row.OriginalApiName))
            {
                return false;
            }

            var gameId = row.GameId.Value;
            if (!_keptRowKeys.Add(HistoryRowKey(gameId, row.OriginalApiName)))
            {
                return false;
            }

            _keptRows.Add(new KeyValuePair<Guid, string>(gameId, row.OriginalApiName));
            return true;
        }

        /// <summary>
        /// Builds the editor rows of some keys, off the UI thread: each row as the per-game load
        /// builds it from hydrated data, then stamped with its game's custom data and its provider
        /// values. Nothing here is bound yet, so nothing is announced.
        /// </summary>
        private IReadOnlyList<AchievementEditorRow> LoadPagedRows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys) =>
            LoadPagedRows(query, keys, new Dictionary<Guid, PagedGameState>());

        /// <param name="games">Each game's custom data as resolved for earlier reads of the same pass.</param>
        private IReadOnlyList<AchievementEditorRow> LoadPagedRows(
            AchievementQuery query,
            IReadOnlyList<AchievementQuerySql.RowKey> keys,
            Dictionary<Guid, PagedGameState> games)
        {
            using (Common.PerfScope.Start(_logger, "Editor.LoadPagedRows", thresholdMs: 25, context: "rows=" + keys.Count))
            {
                var read = _librarySources.Reads.LoadEditorRows(query, keys);
                var rows = new List<AchievementEditorRow>(read.Rows.Count);
                foreach (var record in read.Rows)
                {
                    if (!Guid.TryParse(record.PlayniteGameId, out var gameId) || string.IsNullOrWhiteSpace(record.ApiName))
                    {
                        continue;
                    }

                    if (!games.TryGetValue(gameId, out var game))
                    {
                        game = LoadPagedGameState(gameId);
                        games[gameId] = game;
                    }

                    EditorBaselineRecord baseline = null;
                    if (record.IsAuthored == 0 && record.UserId.HasValue && record.DefinitionId.HasValue)
                    {
                        read.Baselines.TryGetValue((record.UserId.Value, record.DefinitionId.Value), out baseline);
                    }

                    rows.Add(BuildPagedRow(record, baseline, gameId, game));
                }

                return rows;
            }
        }

        /// <summary>One game's custom data, resolved once for the rows of a page.</summary>
        private sealed class PagedGameState
        {
            public Dictionary<string, string> CategoryOverrides { get; set; }
            public Dictionary<string, string> TagOverrides { get; set; }
            public Dictionary<string, CustomAchievementDefinition> AutoCapstones { get; set; }
            public bool UseSeparateLockedIcons { get; set; }
        }

        private PagedGameState LoadPagedGameState(Guid gameId)
        {
            var data = _gameCustomDataStore.LoadOrDefault(gameId);
            var resolved = GameCustomDataLookup.ResolveGameCustomData(gameId, _settings?.Persisted);
            var autoCapstones = new Dictionary<string, CustomAchievementDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in data?.CustomAchievements ?? Enumerable.Empty<CustomAchievementDefinition>())
            {
                if (definition?.IsAutoCapstone == true)
                {
                    autoCapstones[CustomAchievementProjectionService.BuildApiName(definition.Id)] = definition;
                }
            }

            return new PagedGameState
            {
                CategoryOverrides = GetCurrentCategoryOverrideMap(resolved),
                TagOverrides = GetCurrentTagOverrideMap(resolved),
                AutoCapstones = autoCapstones,
                UseSeparateLockedIcons = GameCustomDataLookup.ShouldUseSeparateLockedIcons(gameId, _settings?.Persisted)
            };
        }

        private AchievementEditorRow BuildPagedRow(
            AchievementRowRecord record,
            EditorBaselineRecord baseline,
            Guid gameId,
            PagedGameState game)
        {
            var isAuthored = record.IsAuthored != 0;
            var detail = new AchievementDetail
            {
                ApiName = record.ApiName,
                IsCustom = isAuthored,
                DisplayName = record.DisplayName,
                Description = record.Description,
                UnlockTimeUtc = AchievementSqlFunctions.ParseUtc(record.UnlockTimeUtc),
                UnlockedIconPath = record.UnlockedIconPath,
                LockedIconPath = record.LockedIconPath,
                Points = record.Points,
                TrophyType = record.TrophyType,
                Hidden = record.Hidden != 0,
                Rarity = AchievementSqlFunctions.ParseStoredRarity(record.Rarity),
                GlobalPercentUnlocked = record.GlobalPercentUnlocked,
                ProgressNum = record.ProgressNum,
                ProgressDenom = record.ProgressDenom,
                Category = AchievementCategoryHelper.NormalizeCategoryOrDefault(record.Category),
                ProviderCategory = AchievementCategoryHelper.NormalizeCategoryOrDefault(record.ProviderCategory),
                Tags = AchievementTagHelper.Normalize(record.Tags),
                IsCapstone = record.IsCapstone != 0,
                AchievementNote = record.Note,
                IsGoal = record.IsGoal != 0,
                IsFiltered = record.IsFiltered != 0,
                IsFilteredFromSummaries = record.IsSummaryFiltered != 0
            };
            detail.Unlocked = record.Unlocked != 0;

            var row = AchievementEditorRow.FromAchievementDetail(detail);
            row.GameId = gameId;
            row.GameName = record.GameName;
            row.GameIconPath = record.GameIconPath;
            row.IsListEditLocked = true;
            row.IsManuallyTrackedGame = false;

            var apiName = NormalizeText(record.ApiName);
            row.CategoryLabel = game.CategoryOverrides.TryGetValue(apiName, out var category) ? category : null;
            row.TagsValue = game.TagOverrides.TryGetValue(apiName, out var tags) ? tags : null;
            // Without the names of what each capstone stands for: those need the game's whole
            // achievement list, and their category text a resource lookup this thread cannot make.
            row.SetCapstoneStateFromSource(detail.IsCapstone, null, null, null);
            if (game.AutoCapstones.TryGetValue(apiName, out var auto))
            {
                row.IsAutoCapstone = true;
                row.IsWholeGameAutoCapstone = auto.IsWholeGameAutoCapstone;
            }

            if (baseline != null)
            {
                row.ProviderUnlockedIconPath = baseline.UnlockedIconPath;
                row.ProviderLockedIconPath = baseline.LockedIconPath;
                row.ProviderHidden = baseline.Hidden != 0;
                row.ProviderTagsValue = AchievementTagHelper.Normalize(baseline.Tags);
                row.ProviderDisplayName = baseline.DisplayName;
                row.ProviderDescription = baseline.Description;
                row.ProviderPoints = baseline.Points;
                row.ProviderTrophyType = baseline.TrophyType;
                row.ProviderUnlockTimeUtc = AchievementSqlFunctions.ParseUtc(baseline.UnlockTimeUtc);
                row.ProviderIsCapstone = baseline.IsCapstone != 0;
                row.MarkProviderBaselinesKnown();
            }
            else if (isAuthored)
            {
                row.MarkProviderBaselinesKnown();
            }

            ApplyRowDisplaySettings(row, game.UseSeparateLockedIcons);
            row.RefreshCustomizationState();
            return row;
        }

        // The one game the library editor lists whole: its name and icon for the Game column,
        // which the per-game load does not put on its rows.
        private KeyValuePair<string, string> _listedGame;

        private KeyValuePair<string, string> ResolveListedGame(Guid gameId)
        {
            string name = null;
            string icon = null;
            try
            {
                var database = PlayniteAchievementsPlugin.Instance?.PlayniteApi?.Database;
                var game = database?.Games?.Get(gameId);
                name = game?.Name;
                if (!string.IsNullOrWhiteSpace(game?.Icon))
                {
                    icon = database.GetFullFilePath(game.Icon) ?? game.Icon;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed resolving the name and icon of gameId={gameId}.");
            }

            return new KeyValuePair<string, string>(name, icon);
        }

        /// <summary>Puts the listed game's name and icon on a row of the one-game load.</summary>
        private void StampListedGame(AchievementEditorRow row)
        {
            if (row == null || !IsLibraryScope || IsPagedMode)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(row.GameName))
            {
                // A game Playnite no longer has keeps the name the cache stored for it.
                row.GameName = _listedGame.Key ?? _gameDataSnapshotProvider?.GetRawGameData()?.GameName;
            }

            if (string.IsNullOrWhiteSpace(row.GameIconPath))
            {
                row.GameIconPath = _listedGame.Value;
            }
        }

        /// <summary>A read row put in place in the list: attached as the per-game load attaches its rows.</summary>
        private void AttachPagedRow(AchievementEditorRow row)
        {
            if (row == null || row.IsPlaceholder)
            {
                return;
            }

            AttachRow(row, row.UseSeparateLockedIcons);
            SeedOverrideWriteCache(row);
        }

        /// <summary>
        /// A placeholder handed out becomes the row it stood for; a row already showing takes a
        /// new read of its key unless it already shows the same thing, which is what an edit made
        /// here reads back as.
        /// </summary>
        private void UpdatePagedRow(AchievementEditorRow kept, AchievementEditorRow fresh)
        {
            if (kept == null || fresh == null)
            {
                return;
            }

            if (kept.IsPlaceholder)
            {
                kept.CopyStateFrom(fresh, notify: true);
                AttachPagedRow(kept);
                return;
            }

            fresh.CarryRevealStateFrom(kept);
            if (Common.ObservableStateCopier.StateEquals(kept, fresh))
            {
                return;
            }

            var wasCommitting = _isCommittingRows;
            _isCommittingRows = true;
            try
            {
                kept.CopyStateFrom(fresh, notify: IsRowRealized?.Invoke(kept) ?? true);
            }
            finally
            {
                _isCommittingRows = wasCommitting;
            }

            SeedOverrideWriteCache(kept);
            _searchIndex.Invalidate(kept);
        }

        /// <summary>
        /// The filter choices across the listed games: their categories and their tags, read from
        /// the database rather than from the rows, which are only ever partly loaded.
        /// </summary>
        private void LoadLibraryFilterOptions()
        {
            var version = ++_libraryOptionsVersion;
            var scope = LibraryScopeQuery();
            var sources = _librarySources;
            sources.Scheduler.Run(() =>
            {
                List<string> categories = null;
                List<string> tags = null;
                Dictionary<string, string> userTagNames = null;
                try
                {
                    categories = sources.Reads.Reads.Keys(scope, AchievementGroup.Category)
                        .Select(AchievementCategoryHelper.NormalizeCategoryOrDefault)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(CategoryPathHelper.LabelComparer)
                        .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    tags = sources.Reads.Reads.Keys(scope, AchievementGroup.Tag)
                        .Where(tag => !string.IsNullOrWhiteSpace(tag))
                        .ToList();
                    userTagNames = ReadUserTagNames(sources, scope, tags);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed to read the achievement editor's filter choices.");
                }

                sources.Scheduler.Post(() =>
                {
                    if (_isDetached || version != _libraryOptionsVersion || !IsPagedMode || categories == null)
                    {
                        return;
                    }

                    _categoryFilterOptions = categories;
                    _libraryTagNames = userTagNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    // One reset, not an add per label: each change rebuilds every picker bound to the
                    // list, and across games it holds thousands of labels.
                    if (!AssignableCategoryOptions.SequenceEqual(categories, CategoryPathHelper.LabelComparer))
                    {
                        AssignableCategoryOptions.ReplaceAll(categories);
                    }

                    // None leads, always offered: whether some listed row has no tag would take
                    // another read across the games.
                    var present = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase)
                    {
                        AchievementClassificationFilter.NoTagsKey
                    };
                    _typeFilterOptions = new[] { AchievementClassificationFilter.NoTagsKey }
                        .Concat(AchievementTagRegistry.GetAssignableKeys(null))
                        .Concat(new[] { AchievementTagHelper.Softcore, AchievementTagHelper.Hardcore })
                        .Concat(tags)
                        .Where(present.Contains)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    OnPropertyChanged(nameof(FilterOptionsChanged));
                    CategoryFilter?.Refresh();
                    TypeFilter?.Refresh();
                });
            });
        }

        // The names of the games' own tags the listed rows carry. A game's own tag is named in
        // that game's list, and the filter across games has no one game to look it up in.
        private Dictionary<string, string> _libraryTagNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the names of the games' own tags among <paramref name="tags"/>, from the lists of
        /// the games whose rows carry them. Off the UI thread.
        /// </summary>
        private static Dictionary<string, string> ReadUserTagNames(
            EditorLibrarySources sources,
            AchievementQuery scope,
            IReadOnlyCollection<string> tags)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var userTags = tags
                .Where(tag => tag.StartsWith(AchievementTagHelper.UserKeyPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (userTags.Count == 0)
            {
                return names;
            }

            var wanted = new HashSet<string>(userTags, StringComparer.OrdinalIgnoreCase);
            foreach (var key in sources.Reads.Reads.Keys(scope.WhereClassification(userTags), AchievementGroup.Game))
            {
                if (!Guid.TryParse(key, out var gameId))
                {
                    continue;
                }

                foreach (var definition in AchievementTagRegistry.GetGameTags(gameId))
                {
                    if (definition != null &&
                        wanted.Contains(definition.Key) &&
                        !string.IsNullOrWhiteSpace(definition.Name) &&
                        !names.ContainsKey(definition.Key))
                    {
                        names[definition.Key] = definition.Name;
                    }
                }
            }

            return names;
        }

        /// <summary>A tag's name in the Tags filter: across games a game's own tag by the name its game gives it.</summary>
        private string TagFilterName(string key) =>
            string.Equals(key, AchievementClassificationFilter.NoTagsKey, StringComparison.OrdinalIgnoreCase)
                ? AchievementClassificationFilter.GetDisplayName(key)
                : IsPagedMode && key != null && _libraryTagNames.TryGetValue(key, out var name)
                    ? name
                    : ManageAchievementsCategoryViewModel.GetTagDisplayName(key, _gameId);

        #endregion

        #region List writes across games

        /// <summary>
        /// Writes the category and tag overrides of rows just staged: the game's whole maps while
        /// it is listed whole, these rows' entries only across games.
        /// </summary>
        private void PersistAssignments(IReadOnlyList<AchievementEditorRow> rows)
        {
            if (!IsPagedMode)
            {
                PersistCategoryAssignmentsFromRows();
                return;
            }

            WriteAcrossGames("category assignments", () =>
            {
                foreach (var game in RowsByGame(rows))
                {
                    var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in game)
                    {
                        categories[row.OriginalApiName] = row.CategoryLabel;
                        tags[row.OriginalApiName] = row.TagsValue;
                    }

                    _achievementOverridesService.PatchAchievementAssignments(game.Key, categories, tags);
                }
            });

            KeepEditedRows(rows);
            RaiseAssignmentsChanged();

            // A category first filed here joins the pickers and the filter; once for a gesture
            // applied over every match.
            if (!_isApplyingAllMatching)
            {
                LoadLibraryFilterOptions();
            }
        }

        /// <summary>
        /// Runs a gesture's writes across games as one store batch: one transaction and one change
        /// for all the games, where each game's own write paid both.
        /// </summary>
        private void WriteAcrossGames(string what, Action write)
        {
            try
            {
                using (Common.PerfScope.Start(_logger, "Editor.WriteAcrossGames", thresholdMs: 10, context: what))
                using (_gameCustomDataStore?.BeginBatch())
                {
                    write();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving achievement {what} across games.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Writes the filter lists for rows just staged: the game's whole lists while it is listed
        /// whole, these rows' entries only across games.
        /// </summary>
        private void PersistFilters(IReadOnlyList<AchievementEditorRow> rows)
        {
            if (!IsPagedMode)
            {
                PersistFiltersFromRows();
                return;
            }

            WriteAcrossGames("filters", () =>
            {
                foreach (var game in RowsByGame(rows))
                {
                    var filtered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    var summaryFiltered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in game)
                    {
                        filtered[row.OriginalApiName] = row.IsFiltered;
                        summaryFiltered[row.OriginalApiName] = row.IsSummaryFiltered;
                    }

                    _achievementOverridesService.PatchAchievementFilters(game.Key, filtered, summaryFiltered);
                }
            });

            KeepEditedRows(rows);
            RaiseAssignmentsChanged();
        }

        /// <summary>
        /// Writes the icon overrides of provider rows across games, by the rule the whole-map write
        /// uses: an entry where the row's icon differs from the provider's own, none where it
        /// matches. A row whose provider values were not read is left as stored.
        /// </summary>
        private void PersistIconOverridesAcrossGames(IReadOnlyList<AchievementEditorRow> rows)
        {
            // Announced once the batch is written: the cache apply each announcement starts
            // reads the games' stored overrides.
            var announced = new List<KeyValuePair<Guid, List<AchievementEditorRow>>>();
            WriteAcrossGames("icon overrides", () =>
            {
                foreach (var game in RowsByGame(rows?.Where(row => row != null && row.IsProviderRow)))
                {
                    var unlocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var locked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var written = new List<AchievementEditorRow>();
                    foreach (var row in game)
                    {
                        if (!row.HasProviderBaselines)
                        {
                            _logger?.Warn($"Skipped writing icon overrides for gameId={game.Key}: no provider data to compare against.");
                            continue;
                        }

                        var apiName = NormalizeText(row.OriginalApiName);
                        var unlockedPath = NormalizeText(row.UnlockedIconPath);
                        unlocked[apiName] = !string.IsNullOrWhiteSpace(unlockedPath) &&
                            !string.Equals(unlockedPath, NormalizeText(row.ProviderUnlockedIconPath), StringComparison.OrdinalIgnoreCase)
                                ? unlockedPath
                                : null;

                        var lockedPath = NormalizeText(row.LockedIconPath);
                        locked[apiName] = !string.IsNullOrWhiteSpace(lockedPath) &&
                            !string.Equals(lockedPath, NormalizeText(row.ProviderLockedIconPath), StringComparison.OrdinalIgnoreCase) &&
                            AchievementIconResolver.HasExplicitLockedIcon(lockedPath, row.UnlockedIconPath)
                                ? lockedPath
                                : null;
                        written.Add(row);
                    }

                    if (written.Count == 0)
                    {
                        continue;
                    }

                    _achievementOverridesService.PatchIconOverrides(game.Key, unlocked, locked);
                    announced.Add(new KeyValuePair<Guid, List<AchievementEditorRow>>(game.Key, written));
                }
            });

            foreach (var game in announced)
            {
                RaiseIconOverridesSaved(game.Value, gameId: game.Key);
            }

            KeepEditedRows(rows);
            RaiseAssignmentsChanged();
        }

        /// <summary>
        /// Copies a chosen image into the managed icon cache for authored rows across games and
        /// writes each one's definition, which carries its icons.
        /// </summary>
        private async System.Threading.Tasks.Task PersistAuthoredIconsAcrossGamesAsync(
            IReadOnlyList<AchievementEditorRow> rows,
            AchievementIconVariant variant,
            ICollection<string> errors)
        {
            foreach (var row in rows ?? Array.Empty<AchievementEditorRow>())
            {
                var apiName = NormalizeText(row?.OriginalApiName);
                if (row == null || row.IsProviderRow || string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                // As the whole save materializes authored art: a locked slot that only copies the
                // unlocked art is not made a file of its own.
                var current = NormalizeText(ReadIcon(row, variant));
                var ownArt = variant == AchievementIconVariant.Unlocked ||
                    AchievementIconResolver.HasExplicitLockedIcon(current, row.UnlockedIconPath);
                if (!string.IsNullOrWhiteSpace(current) && ownArt &&
                    AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName }).TryGetValue(apiName, out var stem))
                {
                    var materialized = await MaterializeIconSourceAsync(
                        current,
                        stem,
                        variant,
                        errors,
                        GameOf(row).ToString("D")).ConfigureAwait(true);
                    if (!string.Equals(materialized, current, StringComparison.Ordinal))
                    {
                        StageAcross(new[] { row }, target => WriteIcon(target, variant, materialized));
                    }
                }

                PersistAuthoredRowDefinition(row);
                KeepEditedRow(row);
            }
        }

        /// <summary>
        /// The file stem a provider row's icon is cached under, worked out from its game's
        /// achievement names as the per-game load works it out; memoized per game for a gesture.
        /// </summary>
        private string ResolvePagedIconFileStem(
            AchievementEditorRow row,
            Dictionary<Guid, IReadOnlyDictionary<string, string>> stemsByGame)
        {
            var gameId = GameOf(row);
            var apiName = NormalizeText(row?.OriginalApiName);
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(apiName))
            {
                return null;
            }

            if (!stemsByGame.TryGetValue(gameId, out var stems))
            {
                try
                {
                    var names = _librarySources.Reads.Reads
                        .Rows(AchievementQuery.CurrentUser.WithLatestProgressOnly().ForGames(new[] { gameId }).OrderBy(AchievementSort.Display))
                        .Select(record => record.ApiName);
                    stems = AchievementIconCachePathBuilder.BuildFileStems(names.Concat(new[] { apiName }));
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed reading the achievement names of gameId={gameId}.");
                    stems = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName });
                }

                stemsByGame[gameId] = stems;
            }

            return stems.TryGetValue(apiName, out var stem) ? stem : null;
        }

        /// <summary>
        /// Reverts rows across games to the provider's values: one patch per game that drops their
        /// records and takes them out of the filter lists and the goals, and a capstone write for
        /// those holding one. The games' order is left as stored.
        /// </summary>
        private void RevertAcrossGames(IReadOnlyList<AchievementEditorRow> targets)
        {
            var games = RowsByGame(targets);
            WriteAcrossGames("reverts", () =>
            {
                foreach (var game in games)
                {
                    _achievementOverridesService.RevertAchievements(game.Key, game.Select(row => row.OriginalApiName));
                }
            });

            // After the batch: the capstone write reads the game's stored record to resolve it.
            foreach (var game in games)
            {
                try
                {
                    var clearedCapstones = game
                        .Where(row => row.IsCapstone)
                        .Select(row => (NormalizeText(row.OriginalApiName), false))
                        .ToList();
                    if (clearedCapstones.Count > 0)
                    {
                        _achievementOverridesService.SetCapstones(game.Key, clearedCapstones);
                    }

                    // The dropped records took their icon overrides with them; the cached rows
                    // take the provider's own art back.
                    RaiseIconOverridesSaved(game.ToList(), editorRowsStale: true, gameId: game.Key);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed reverting achievements for gameId={game.Key}.");
                    SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
                }
            }

            KeepEditedRows(targets);
            RaiseAssignmentsChanged();
            SetStatus(null, false);
        }

        /// <summary>The rows with a game and an ApiName, by game.</summary>
        private IReadOnlyList<IGrouping<Guid, AchievementEditorRow>> RowsByGame(IEnumerable<AchievementEditorRow> rows) =>
            (rows ?? Enumerable.Empty<AchievementEditorRow>())
                .Where(row => row != null &&
                              !row.IsBulkRow &&
                              !row.IsPlaceholder &&
                              GameOf(row) != Guid.Empty &&
                              !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .GroupBy(GameOf)
                .ToList();

        /// <summary>Keeps edited rows listed until the filters change, with one new query for all of them.</summary>
        private void KeepEditedRows(IEnumerable<AchievementEditorRow> rows)
        {
            var added = false;
            foreach (var row in rows ?? Enumerable.Empty<AchievementEditorRow>())
            {
                added |= AddKeptRow(row);
            }

            if (added)
            {
                _pagedRows.SetQuery(BuildLibraryQuery());
            }
        }

        /// <summary>
        /// The tags a row takes from the ticks. Across games the ticks show the tags the games
        /// share, plus a game's own only when the selection is that one game's, so a row's own
        /// tags the ticks do not show are kept rather than taken off.
        /// </summary>
        private string WithUnofferedTags(AchievementEditorRow row, string tags)
        {
            if (!IsPagedMode || row == null)
            {
                return tags;
            }

            var offered = new HashSet<string>(
                TagSelectionOptions.Select(option => option.Value),
                StringComparer.OrdinalIgnoreCase);
            var kept = AchievementTagHelper.Parse(AchievementTagHelper.StripDerived(row.EffectiveTagsValue))
                .Where(tag => !offered.Contains(tag));
            return AchievementTagHelper.Normalize(
                AchievementTagHelper.Combine(AchievementTagHelper.Parse(tags).Concat(kept)));
        }

        /// <summary>
        /// Creates a tag of the name in each selected row's game (or finds the one already named
        /// that there) and adds it to the row, since a game's own tags are kept per game.
        /// </summary>
        private void CreateAndAssignTagAcrossGames(string name)
        {
            MarkUndoIntent(EditorEditIntent.Command("Tags", "LOCPlayAch_Common_Tags"));
            var targets = ResolveSelectionTargets()
                .Where(row => row.CanEditAssignments && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .ToList();
            var games = RowsByGame(targets);
            var keys = new Dictionary<Guid, string>();
            WriteAcrossGames("tags", () =>
            {
                foreach (var game in games)
                {
                    keys[game.Key] = _achievementOverridesService.CreateGameTag(game.Key, name);
                }
            });

            var staged = new List<AchievementEditorRow>();
            foreach (var game in games)
            {
                if (!keys.TryGetValue(game.Key, out var key) || string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                StageAcross(game, row => row.TagsValue = AchievementTagHelper.OverrideOrNull(
                    AchievementTagHelper.WithTag(AchievementTagHelper.Normalize(row.EffectiveTagsValue), key, true),
                    row.ProviderTagsValue));
                staged.AddRange(game);
            }

            if (staged.Count > 0)
            {
                PersistAssignments(staged);
                SyncBulkRowFromSelection();
            }
        }

        // The game whose own tags the ticks offer; null for only the shared tags.
        private Guid? _tagOptionsGame;
        private bool _tagOptionsSynced;

        /// <summary>
        /// Points the library editor's tag ticks at the game whose own tags they can offer: the
        /// listed game, or across games the selection's one game, else only the shared tags.
        /// </summary>
        private void SyncLibraryTagOptions(bool force = false)
        {
            if (!IsLibraryScope || TagSelectionOptions == null)
            {
                return;
            }

            Guid? game = null;
            if (!IsPagedMode)
            {
                game = _gameId == Guid.Empty ? (Guid?)null : _gameId;
            }
            else
            {
                var games = _selectedRows
                    .Where(row => row != null && !row.IsBulkRow)
                    .Select(GameOf)
                    .Distinct()
                    .Take(2)
                    .ToList();
                game = games.Count == 1 && games[0] != Guid.Empty ? games[0] : (Guid?)null;
            }

            if (!force && _tagOptionsSynced && game == _tagOptionsGame)
            {
                return;
            }

            _tagOptionsGame = game;
            _tagOptionsSynced = true;
            TagSelectionOption.Sync(
                TagSelectionOptions,
                AchievementTagRegistry.GetAssignableKeys(game),
                key => ManageAchievementsCategoryViewModel.GetTagDisplayName(key, game),
                option => option.PropertyChanged += TypeSelectionOption_PropertyChanged);
        }

        #endregion

        #region Select all matching

        // Rows read and built per step while selecting or editing every match; the grid's page size.
        private const int AllMatchingChunk = 100;

        private int _selectAllMatchingVersion;

        // The query whose every match is selected, and how many it matched; null when the
        // selection is what the grid reported.
        private AchievementQuery _allMatchingQuery;
        private int _allMatchingCount;

        // Set while a gesture runs over one chunk of every match, so it applies there instead of
        // deferring again.
        private bool _isApplyingAllMatching;

        // Gestures made over every match while a run was writing, in the order they were made.
        private readonly Queue<Action> _queuedAllMatchingGestures = new Queue<Action>();

        /// <summary>
        /// Whether the selection is every row the filters match across games. The view model then
        /// holds a few rows that stand for the rest, which is all the details pane needs, and a
        /// gesture reads the matches from the database a chunk at a time; the grid shows only the
        /// part it has loaded, so its own reports of what is selected do not replace it.
        /// </summary>
        public bool IsAllMatchingSelected => _allMatchingQuery != null;

        /// <summary>
        /// Selects every row the filters match across games without loading the grid's pages or
        /// holding a row for each match.
        /// </summary>
        public void SelectAllMatching()
        {
            if (!IsPagedMode)
            {
                return;
            }

            var version = ++_selectAllMatchingVersion;
            var query = BuildLibraryQuery();
            ReadRepresentatives(query, version, (representatives, count) =>
            {
                // One match or none is all loaded, so the grid's own selection already holds it.
                if (count <= 1)
                {
                    return;
                }

                _allMatchingQuery = query;
                _allMatchingCount = count;
                ApplySelection(representatives);
                OnPropertyChanged(nameof(IsAllMatchingSelected));
            });
        }

        /// <summary>Drops a selection of every match, and any still being read, when the filters change.</summary>
        private void ClearAllMatchingSelection()
        {
            _selectAllMatchingVersion++;
            if (IsAllMatchingSelected)
            {
                SetSelectedRows(Array.Empty<AchievementEditorRow>());
            }
        }

        /// <summary>
        /// The fields the details pane shows for a selection. A selection's rows agree on a field
        /// exactly when its representatives do, see <see cref="RepresentativeRows"/>.
        /// </summary>
        private static readonly Func<AchievementEditorRow, object>[] SelectionFields =
        {
            row => row.Unlocked,
            row => row.DisplayName,
            row => row.Description,
            row => row.PointsText,
            row => row.TrophyType,
            row => row.EffectiveCategoryLabel,
            row => row.EffectiveTagsValue,
            row => row.AchievementNote,
            row => row.RarityInput,
            row => row.ProgressNumText,
            row => row.ProgressDenomText,
            row => row.UnlockedIconPath,
            row => row.LockedIconPath,
            row => row.UnlockTime,
            row => row.IsGoal,
            row => row.Hidden,
            row => row.FilterScope,
            row => row.IsCapstone,
            row => row.IsProviderRow,
            row => IsRevertible(row),
            row => row.CanEditUnlocked
        };

        /// <summary>
        /// A few rows standing for many: for each field, the first row and the first whose value
        /// differs from it. Every comparison the pane makes is against its first row, so these
        /// agree on a field exactly when all the rows do, and contain a row of each kind a command
        /// asks after.
        /// </summary>
        private sealed class RepresentativeRows
        {
            private AchievementEditorRow _first;
            private readonly object[] _firstValues = new object[SelectionFields.Length];
            private readonly AchievementEditorRow[] _differing = new AchievementEditorRow[SelectionFields.Length];

            public void Offer(AchievementEditorRow row)
            {
                if (row == null)
                {
                    return;
                }

                if (_first == null)
                {
                    _first = row;
                    for (var index = 0; index < SelectionFields.Length; index++)
                    {
                        _firstValues[index] = SelectionFields[index](row);
                    }

                    return;
                }

                for (var index = 0; index < SelectionFields.Length; index++)
                {
                    if (_differing[index] == null && !Equals(SelectionFields[index](row), _firstValues[index]))
                    {
                        _differing[index] = row;
                    }
                }
            }

            public List<AchievementEditorRow> Rows
            {
                get
                {
                    var rows = new List<AchievementEditorRow>();
                    if (_first != null)
                    {
                        rows.Add(_first);
                    }

                    foreach (var row in _differing)
                    {
                        if (row != null && !rows.Contains(row))
                        {
                            rows.Add(row);
                        }
                    }

                    return rows;
                }
            }
        }

        /// <summary>
        /// Reads every match of <paramref name="query"/> off the UI thread a chunk at a time,
        /// keeping only the rows that stand for the rest, and hands them over attached.
        /// </summary>
        private void ReadRepresentatives(
            AchievementQuery query,
            int version,
            Action<List<AchievementEditorRow>, int> done)
        {
            var sources = _librarySources;
            sources.Scheduler.Run(() =>
            {
                List<AchievementEditorRow> picked = null;
                var count = 0;
                try
                {
                    using (var scope = Common.PerfScope.Start(_logger, "Editor.SelectAllMatching", thresholdMs: 50))
                    {
                        var keys = sources.Reads.Reads.SortedKeys(query);
                        count = keys.Count;
                        scope?.SetContext("matches=" + count);
                        var representatives = new RepresentativeRows();
                        var games = new Dictionary<Guid, PagedGameState>();
                        for (var start = 0; start < keys.Count; start += AllMatchingChunk)
                        {
                            if (version != _selectAllMatchingVersion)
                            {
                                return;
                            }

                            var chunk = keys.Skip(start).Take(AllMatchingChunk).ToList();
                            foreach (var row in LoadPagedRows(query, chunk, games))
                            {
                                representatives.Offer(row);
                            }
                        }

                        picked = representatives.Rows;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to select every achievement the editor's filters match.");
                }

                sources.Scheduler.Post(() =>
                {
                    if (picked == null || _isDetached || version != _selectAllMatchingVersion || !IsPagedMode)
                    {
                        return;
                    }

                    foreach (var row in picked)
                    {
                        AttachPagedRow(row);
                    }

                    done(picked, count);
                });
            });
        }

        /// <summary>
        /// Runs a gesture over every match instead of over the rows held, when every match is
        /// selected; true when it was taken over. The gesture runs again for each chunk with that
        /// chunk as the selection.
        /// </summary>
        private bool DeferToAllMatching(Action gesture)
        {
            if (!IsAllMatchingSelected || _isApplyingAllMatching)
            {
                return false;
            }

            _ = ApplyAcrossAllMatchingAsync(gesture);
            return true;
        }

        /// <summary>
        /// Reads every match a chunk at a time off the UI thread, builds that chunk's rows, and
        /// runs the gesture with them as the selection, so memory holds one chunk however many
        /// match. Saving holds the undo step open, so the whole run undoes as one step.
        /// </summary>
        private async System.Threading.Tasks.Task ApplyAcrossAllMatchingAsync(Action gesture)
        {
            // A gesture made while a run is writing waits its turn rather than being dropped: the
            // tag menu stays open, and each tick in it is a gesture of its own.
            if (IsSaving)
            {
                _queuedAllMatchingGestures.Enqueue(gesture);
                return;
            }

            var query = _allMatchingQuery;
            var games = new Dictionary<Guid, PagedGameState>();
            IsSaving = true;
            _pagedFeedGate.IsOpen = false;
            try
            {
                using (var scope = Common.PerfScope.Start(_logger, "Editor.ApplyAcrossAllMatching", thresholdMs: 50))
                {
                    var keys = await ReadAsync(() => _librarySources.Reads.Reads.SortedKeys(query)).ConfigureAwait(true);
                    scope?.SetContext("matches=" + keys.Count);
                    for (var start = 0; start < keys.Count && !_isDetached; start += AllMatchingChunk)
                    {
                        var chunk = keys.Skip(start).Take(AllMatchingChunk).ToList();
                        var rows = await ReadAsync(() => LoadPagedRows(query, chunk, games)).ConfigureAwait(true);
                        if (_isDetached)
                        {
                            break;
                        }

                        ApplyToChunk(rows, gesture);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to apply an edit to every achievement the editor's filters match.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
            finally
            {
                _pagedFeedGate.IsOpen = true;
                IsSaving = false;
            }

            if (_isDetached || !IsAllMatchingSelected || !ReferenceEquals(query, _allMatchingQuery))
            {
                _queuedAllMatchingGestures.Clear();
                return;
            }

            // The next waiting gesture runs now; the pane is re-seeded once the last has run.
            if (_queuedAllMatchingGestures.Count > 0)
            {
                _ = ApplyAcrossAllMatchingAsync(_queuedAllMatchingGestures.Dequeue());
                return;
            }

            // The pane shows what the gesture left, and a category it filed joins the choices.
            LoadLibraryFilterOptions();
            var version = _selectAllMatchingVersion;
            ReadRepresentatives(query, version, (representatives, count) =>
            {
                if (ReferenceEquals(query, _allMatchingQuery))
                {
                    _allMatchingCount = count;
                    ApplySelection(representatives);
                }
            });
        }

        private void ApplyToChunk(IReadOnlyList<AchievementEditorRow> rows, Action gesture)
        {
            var held = _selectedRows.ToList();
            foreach (var row in rows)
            {
                AttachPagedRow(row);
            }

            _isApplyingAllMatching = true;
            _selectedRows.Clear();
            _selectedRows.AddRange(rows);
            try
            {
                gesture();
            }
            finally
            {
                _selectedRows.Clear();
                _selectedRows.AddRange(held);
                _isApplyingAllMatching = false;
                foreach (var row in rows)
                {
                    ForgetOverrideWriteCache(row);
                }
            }
        }

        /// <summary>Runs a read off the UI thread and resumes on it with the result.</summary>
        private System.Threading.Tasks.Task<T> ReadAsync<T>(Func<T> read)
        {
            var completion = new System.Threading.Tasks.TaskCompletionSource<T>();
            var scheduler = _librarySources.Scheduler;
            scheduler.Run(() =>
            {
                try
                {
                    var result = read();
                    scheduler.Post(() => completion.SetResult(result));
                }
                catch (Exception ex)
                {
                    scheduler.Post(() => completion.SetException(ex));
                }
            });
            return completion.Task;
        }

        #endregion

        #region Per-achievement writes across games

        /// <summary>
        /// Writes one authored achievement's definition in place, leaving the game's others as
        /// stored. The paged rows hold only some of a game's achievements, so the whole-list save
        /// would drop the rest.
        /// </summary>
        private void PersistAuthoredRowDefinition(AchievementEditorRow row)
        {
            if (row == null || row.IsProviderRow)
            {
                return;
            }

            var gameId = GameOf(row);
            var definition = row.ToDefinition(new HashSet<string>(StringComparer.OrdinalIgnoreCase), out var errors);
            if (definition == null || errors?.Count > 0)
            {
                row.ValidationMessage = errors?.FirstOrDefault();
                return;
            }

            try
            {
                row.ValidationMessage = null;
                _achievementOverridesService.UpsertCustomAchievement(gameId, definition);
                RaiseAssignmentsChanged();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving an authored achievement for gameId={gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        #endregion
    }
}
