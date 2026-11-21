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
    /// game's whole list from it has nothing to rebuild; the writers of those lists refuse in this
    /// mode (<see cref="RefuseInPagedMode"/>) and the rows lock their editors
    /// (<see cref="AchievementEditorRow.IsListEditLocked"/>). What is written per achievement
    /// works on any row: names, descriptions, points, trophies, hidden, unlock times, notes,
    /// goals, capstones, and an authored achievement's own definition.
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

        /// <summary>
        /// The library editor, opened on every game. The single-game services the Manage window
        /// hands its editor (manual linking, export, import, the Workshop) are not offered here.
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
            Func<CustomProviderEditorViewModel, CustomProviderEditorResult> showEditor = null)
        {
            return new ManageAchievementsEditorViewModel(
                EditorScope.Library(),
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
            GameFilter = new ControlBarGameFilter(ApplyLibraryScope);
            LoadLibraryGames();
            ApplyLibraryScope();
        }

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

            Scope = EditorScope.Library(GameFilter?.SelectedGameIds);
            ClearKeptRows();
            SetStatus(null, false);
            if (Scope.SingleGameId is Guid single)
            {
                DisposePagedRows();
                SetActiveGame(single);
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
            OnPropertyChanged(nameof(GridRows));
            OnPropertyChanged(nameof(GameId));
            RaiseCommandStates();
        }

        #endregion

        #region Paged rows

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
                placed: AttachPagedRow);
            _pagedRows.PropertyChanged += PagedRows_PropertyChanged;
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
            return GameFilter?.HasSelection == true ? query.ForGames(GameFilter.SelectedGameIds) : query;
        }

        /// <summary>The filters changed: the kept rows go and the list reads from its top.</summary>
        private void ApplyPagedFilters()
        {
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
            if (!IsPagedMode || !IsFiltering || row?.GameId == null || string.IsNullOrWhiteSpace(row.OriginalApiName))
            {
                return;
            }

            var gameId = row.GameId.Value;
            if (_keptRowKeys.Add(HistoryRowKey(gameId, row.OriginalApiName)))
            {
                _keptRows.Add(new KeyValuePair<Guid, string>(gameId, row.OriginalApiName));
                _pagedRows.SetQuery(BuildLibraryQuery());
            }
        }

        /// <summary>
        /// Builds the editor rows of some keys, off the UI thread: each row as the per-game load
        /// builds it from hydrated data, then stamped with its game's custom data and its provider
        /// values. Nothing here is bound yet, so nothing is announced.
        /// </summary>
        private IReadOnlyList<AchievementEditorRow> LoadPagedRows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys)
        {
            using (Common.PerfScope.Start(_logger, "Editor.LoadPagedRows", thresholdMs: 25, context: "rows=" + keys.Count))
            {
                var read = _librarySources.Reads.LoadEditorRows(query, keys);
                var games = new Dictionary<Guid, PagedGameState>();
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
                    var present = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
                    _typeFilterOptions = AchievementTagRegistry.GetAssignableKeys(null)
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
