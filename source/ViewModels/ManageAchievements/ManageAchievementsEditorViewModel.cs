using Microsoft.Win32;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.CustomProviders;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed class ManageAchievementsEditorViewModel : ObservableObject
    {
        // The merged editor lists provider achievements alongside authored ones; the Custom tab
        // lists only authored ones. Everything else about the two surfaces is identical, so they
        // share this view model rather than duplicating its editing affordances.
        private readonly bool _includeProviderAchievements;
        private readonly Guid _gameId;
        private readonly string _gameIdText;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly CustomProviderStore _customProviderStore;
        private readonly Func<string, string> _pickColor;
        private readonly Func<CustomProviderEditorViewModel, CustomProviderEditorResult> _showEditor;
        // The host owns the manual-tracking plumbing (refresh runtime, cache, sources); the editor
        // only needs to ask for the projection and the dialog, so it takes delegates rather than
        // growing that whole dependency set.
        private readonly Action<Guid> _manualLinkApplier;
        private readonly Func<bool> _showManualLinkDialog;
        private readonly Action _unlinkManualTracking;
        private bool _isRefreshingAssignments;
        private bool _isCommittingRows;
        private bool _isSyncingTypeOptions;
        private bool _isSyncingCustomProvider;
        private bool _isCustomOnlyGame;
        private CustomProviderOption _selectedCustomProviderOption;
        private CustomProviderDefinition _selectedCustomProvider;

        private AchievementEditorRow _selectedRow;
        private AchievementEditorRow _bulkRow;
        private readonly List<AchievementEditorRow> _selectedRows = new List<AchievementEditorRow>();
        private bool _isApplyingBulk;
        private bool _providerIconBaselinesResolved;
        private DispatcherTimer _assignmentsChangedDebounce;
        private bool _assignmentsChangedPending;
        private DispatcherTimer _manualUnlockDebounce;
        private bool _manualUnlocksPending;
        private DateTime _manualUnlockFirstPendingUtc;
        private bool _isManuallyTrackedGame;
        private bool _canLinkManualTracking;
        private string _filterText;
        private string _sourceHeading;
        private ProviderOverrideChoice _selectedDisplayPlatform;
        private bool _isSyncingDisplayPlatform;
        private CategoryPickerOption _selectedCategoryFilter;
        private EditorFilterOption _selectedTypeFilter;
        private SearchQuery _filterQuery;
        private readonly SearchTextIndex<AchievementEditorRow> _searchIndex =
            new SearchTextIndex<AchievementEditorRow>(row => SearchTextBuilder.ForManualEdit(
                row?.DisplayName,
                row?.Description,
                row?.OriginalApiName));
        private static readonly TimeSpan ManualUnlockMaxStaleness = TimeSpan.FromSeconds(2);
        private readonly Dictionary<string, object> _lastWrittenOverrides =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private bool _hasChanges;
        private bool _hasRows;
        private bool _hasValidationErrors;
        private bool _isSaving;
        private string _statusText;
        private bool _statusIsError;
        private string _baselineCollectionSignature;

        public ManageAchievementsEditorViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            GameCustomDataStore gameCustomDataStore,
            ManagedCustomIconService managedCustomIconService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            CustomProviderStore customProviderStore = null,
            Func<string, string> pickColor = null,
            Func<CustomProviderEditorViewModel, CustomProviderEditorResult> showEditor = null,
            bool includeProviderAchievements = false,
            Action<Guid> manualLinkApplier = null,
            Func<bool> showManualLinkDialog = null,
            Action unlinkManualTracking = null)
        {
            _includeProviderAchievements = includeProviderAchievements;
            _gameId = gameId;
            _gameIdText = gameId.ToString("D");
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameCustomDataStore = gameCustomDataStore ?? throw new ArgumentNullException(nameof(gameCustomDataStore));
            _managedCustomIconService = managedCustomIconService;
            _gameDataSnapshotProvider = gameDataSnapshotProvider;
            _settings = settings;
            _logger = logger;
            _customProviderStore = customProviderStore;
            _pickColor = pickColor;
            _showEditor = showEditor;
            _manualLinkApplier = manualLinkApplier;
            _showManualLinkDialog = showManualLinkDialog;
            _unlinkManualTracking = unlinkManualTracking;
            if (_customProviderStore != null)
            {
                _customProviderStore.Changed += CustomProviderStore_Changed;
            }

            CustomProviderOptions = new ObservableCollection<CustomProviderOption>();
            AddCustomProviderCommand = new RelayCommand(_ => AddCustomProvider(), _ => IsCustomOnlyGame && _customProviderStore != null && !IsSaving);
            EditCustomProviderCommand = new RelayCommand(_ => EditCustomProvider(), _ => HasSelectedCustomProvider && _showEditor != null && !IsSaving);

            AchievementRows = new ObservableCollection<AchievementEditorRow>();
            AssignableCategoryOptions = new ObservableCollection<string>();
            TypeSelectionOptions = new ObservableCollection<CategoryTypeSelectionOption>(
                AchievementCategoryTypeHelper.AssignableCategoryTypes.Select(type =>
                    new CategoryTypeSelectionOption(type, ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName(type))));
            foreach (var option in TypeSelectionOptions)
            {
                option.PropertyChanged += TypeSelectionOption_PropertyChanged;
            }

            AddCommand = new RelayCommand(_ => AddRow(), _ => !IsSaving);
            DuplicateCommand = new RelayCommand(_ => DuplicateSelected(), _ => HasSelection && !IsSaving);
            // Only an authored achievement can be deleted: a provider one would come straight back
            // on the next refresh, so removing it from the list would be a lie. A mixed selection
            // therefore disables delete rather than silently skipping the provider rows.
            DeleteCommand = new RelayCommand(
                _ => DeleteSelected(),
                _ => HasSelection && ResolveSelectionTargets().All(row => !row.IsProviderRow) && !IsSaving);
            RevertCommand = new RelayCommand(
                _ => RevertSelected(),
                _ => HasSelection && !IsSaving);
            ManualLinkCommand = new RelayCommand(_ => OpenManualLinkDialog(), _ => CanLinkManualTracking && !IsSaving);
            UnlinkManualTrackingCommand = new RelayCommand(
                _ => UnlinkManualTracking(),
                _ => IsManuallyTrackedGame && _unlinkManualTracking != null && !IsSaving);
            ImportFileCommand = new RelayCommand(_ => ImportFile(), _ => !IsSaving);
            ExportTemplateCommand = new RelayCommand(_ => ExportTemplate(), _ => !IsSaving);
            ExportAchievementsCommand = new RelayCommand(_ => ExportAchievements(), _ => HasRows && !IsSaving);
            ResetCommand = new RelayCommand(_ => ResetRows(), _ => HasRows && !IsSaving);

            ReloadData();
        }

        public event EventHandler CustomAchievementsSaved;

        /// <summary>Raised after a category or type assignment was persisted for a custom row.</summary>
        public event EventHandler AssignmentsChanged;

        /// <summary>Raised after the game's manual capstone was changed from this tab.</summary>
        public event EventHandler<CapstoneChangedEventArgs> CapstoneChanged;

        public ObservableCollection<AchievementEditorRow> AchievementRows { get; }

        /// <summary>Every category the Category tab shows, in tree order, for the details picker.</summary>
        public ObservableCollection<string> AssignableCategoryOptions { get; }

        /// <summary>Category-type toggles for the selected row; changes persist immediately.</summary>
        public ObservableCollection<CategoryTypeSelectionOption> TypeSelectionOptions { get; }

        public RelayCommand AddCommand { get; }

        public RelayCommand DuplicateCommand { get; }

        public RelayCommand DeleteCommand { get; }

        /// <summary>
        /// Drops the user's customization for the selected achievements, returning them to what the
        /// provider supplies. An authored achievement has no provider value to fall back to, so
        /// this clears the facets it shares with provider rows and leaves the definition alone.
        /// </summary>
        public RelayCommand RevertCommand { get; }

        /// <summary>Opens the manual-link dialog for this game.</summary>
        public RelayCommand ManualLinkCommand { get; }

        public RelayCommand ImportFileCommand { get; }

        public RelayCommand ExportTemplateCommand { get; }

        public RelayCommand ExportAchievementsCommand { get; }

        /// <summary>
        /// Drops every customization this game carries -- the authored achievements, the
        /// per-achievement overrides, and the game-level lists -- leaving the providers' own data.
        /// </summary>
        public RelayCommand ResetCommand { get; }

        public RelayCommand AddCustomProviderCommand { get; }

        public RelayCommand EditCustomProviderCommand { get; }

        /// <summary>
        /// Default first, then every stored custom provider. Only shown for custom-only games.
        /// </summary>
        public ObservableCollection<CustomProviderOption> CustomProviderOptions { get; }

        /// <summary>
        /// True when the game's achievements exist only as custom definitions (no cached provider
        /// data), which is the only case where a custom provider can be assigned.
        /// </summary>
        public bool IsCustomOnlyGame
        {
            get => _isCustomOnlyGame;
            private set
            {
                if (SetValueAndReturn(ref _isCustomOnlyGame, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public CustomProviderOption SelectedCustomProviderOption
        {
            get => _selectedCustomProviderOption;
            set
            {
                if (!SetValueAndReturn(ref _selectedCustomProviderOption, value))
                {
                    return;
                }

                if (!_isSyncingCustomProvider && value != null)
                {
                    ApplyCustomProviderAssignment(value.Id);
                }

                LoadSelectedProviderEditor();
            }
        }

        public bool HasSelectedCustomProvider => _selectedCustomProvider != null;

        public AchievementEditorRow SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (SetValueAndReturn(ref _selectedRow, value))
                {
                    OnPropertyChanged(nameof(HasSelectedRow));
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(HasEditTarget));
                    OnPropertyChanged(nameof(EditTarget));
                    SyncTypeOptionsToEditTarget();
                    RaiseCommandStates();
                }
            }
        }

        public bool HasSelectedRow => SelectedRow != null;

        /// <summary>
        /// True when at least one achievement is selected, by either the multi-selection or the
        /// single current row. Duplicate, revert and delete all act on that selection, so they stay
        /// disabled until there is one.
        /// </summary>
        public bool HasSelection => _selectedRows.Count > 0 || SelectedRow != null;

        /// <summary>
        /// The rows the row-level commands act on: the whole multi-selection when there is one,
        /// otherwise the single current row.
        /// </summary>
        private List<AchievementEditorRow> ResolveSelectionTargets()
        {
            var targets = _selectedRows.Count > 0
                ? _selectedRows.ToList()
                : new List<AchievementEditorRow> { SelectedRow };
            return targets.Where(row => row != null).ToList();
        }

        /// <summary>
        /// The row the details pane edits: the single selected row, or the bulk proxy when several
        /// are selected.
        /// </summary>
        public AchievementEditorRow EditTarget => IsBulkEditing ? BulkRow : SelectedRow;

        /// <summary>
        /// A stand-in row the details pane binds to while several achievements are selected. Fields
        /// the selection agrees on show that value; fields it disagrees on are blank, and editing
        /// one applies it to every selected row.
        /// </summary>
        public AchievementEditorRow BulkRow
        {
            get => _bulkRow;
            private set => SetValue(ref _bulkRow, value);
        }

        public bool IsBulkEditing => _selectedRows.Count > 1;

        public int BulkSelectionCount => _selectedRows.Count;

        public bool HasEditTarget => EditTarget != null;

        /// <summary>
        /// Heading for the details pane while several achievements are selected, so it is obvious
        /// an edit lands on all of them rather than on one.
        /// </summary>
        public string BulkEditHeader => string.Format(
            L("LOCPlayAch_ManageAchievements_Editor_BulkHeader", "Editing {0} achievements"),
            BulkSelectionCount);

        /// <summary>
        /// Tracks the grid's selection. A blank field on the bulk proxy means "these rows disagree",
        /// not "clear this", so only fields the user actually edits are applied.
        /// </summary>
        public void SetSelectedRows(IEnumerable<AchievementEditorRow> rows)
        {
            _selectedRows.Clear();
            foreach (var row in rows ?? Enumerable.Empty<AchievementEditorRow>())
            {
                if (row != null)
                {
                    _selectedRows.Add(row);
                }
            }

            RebuildBulkRow();
            SyncTypeOptionsToEditTarget();
            RaiseCommandStates();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsBulkEditing));
            OnPropertyChanged(nameof(BulkSelectionCount));
            OnPropertyChanged(nameof(BulkEditHeader));
            OnPropertyChanged(nameof(EditTarget));
            OnPropertyChanged(nameof(HasEditTarget));
        }

        /// <summary>
        /// Seeds the bulk proxy from the selection: a field every selected row agrees on is shown,
        /// anything they disagree on is left blank so it reads as "mixed" rather than as a value
        /// that would be applied.
        /// </summary>
        private void RebuildBulkRow()
        {
            if (_bulkRow != null)
            {
                _bulkRow.PropertyChanged -= BulkRow_PropertyChanged;
            }

            if (_selectedRows.Count <= 1)
            {
                BulkRow = null;
                return;
            }

            var row = new AchievementEditorRow
            {
                SuppressNotifications = true
            };

            // Only a selection that is entirely authored may edit the authored-only fields; one
            // provider row in the selection locks rarity, unlock status and progress for all of it.
            row.IsProviderRow = _selectedRows.Any(r => r.IsProviderRow);
            row.IsBulkRow = true;
            // Rebuilt on every selection change, so the game-level flag has to be stamped here too;
            // without it the proxy refuses unlock edits on a manually tracked game.
            row.IsManuallyTrackedGame = IsManuallyTrackedGame;
            row.SetUnlockedStateFromSource(SharedFlagOrNull(r => r.Unlocked));
            row.DisplayName = SharedValue(r => r.DisplayName);
            row.Description = SharedValue(r => r.Description);
            row.PointsText = SharedValue(r => r.PointsText);
            row.TrophyType = SharedValue(r => r.TrophyType);
            row.CategoryLabel = SharedValue(r => r.CategoryLabel);
            row.CategoryTypeValue = SharedValue(r => r.CategoryTypeValue);
            row.AchievementNote = SharedValue(r => r.AchievementNote);
            row.RarityInput = SharedValue(r => r.RarityInput);
            row.ProgressNumText = SharedValue(r => r.ProgressNumText);
            row.ProgressDenomText = SharedValue(r => r.ProgressDenomText);
            row.UnlockedIconPath = SharedValue(r => r.UnlockedIconPath);
            row.LockedIconPath = SharedValue(r => r.LockedIconPath);
            row.UnlockTime = SharedUnlockTime();
            row.SetGoalFromSource(SharedFlagOrNull(r => r.IsGoal));
            row.SetHiddenFromSource(SharedFlagOrNull(r => r.Hidden));
            row.SetFilterScopeFromSource(SharedScope());
            row.SuppressNotifications = false;

            row.PropertyChanged += BulkRow_PropertyChanged;
            BulkRow = row;
        }

        /// <summary>
        /// Re-seeds the bulk proxy from the rows after a selection-level edit made somewhere other
        /// than the details pane, such as the grid's context menu.
        /// </summary>
        /// <remarks>
        /// The proxy's fields are assigned rather than the proxy replaced, so the pane keeps its
        /// focus and scroll position; the applying flag keeps those assignments from being read
        /// back as a fresh bulk edit.
        /// </remarks>
        private void SyncBulkRowFromSelection()
        {
            if (_bulkRow == null || _selectedRows.Count <= 1)
            {
                SyncTypeOptionsToEditTarget();
                return;
            }

            var wasApplying = _isApplyingBulk;
            _isApplyingBulk = true;
            try
            {
                _bulkRow.CategoryLabel = SharedValue(r => r.CategoryLabel);
                _bulkRow.CategoryTypeValue = SharedValue(r => r.CategoryTypeValue);
                _bulkRow.SetFilterScopeFromSource(SharedScope());
                _bulkRow.SetGoalFromSource(SharedFlagOrNull(r => r.IsGoal));
                _bulkRow.SetHiddenFromSource(SharedFlagOrNull(r => r.Hidden));
                _bulkRow.SetUnlockedStateFromSource(SharedFlagOrNull(r => r.Unlocked));
            }
            finally
            {
                _isApplyingBulk = wasApplying;
            }

            SyncTypeOptionsToEditTarget();
        }

        private string SharedValue(Func<AchievementEditorRow, string> selector)
        {
            var first = selector(_selectedRows[0]);
            return _selectedRows.All(r => string.Equals(selector(r), first, StringComparison.Ordinal))
                ? first
                : null;
        }

        /// <summary>
        /// A flag the selection agrees on, or null when it disagrees, so the proxy checkbox can
        /// tell "all unchecked" apart from "these rows differ".
        /// </summary>
        private bool? SharedFlagOrNull(Func<AchievementEditorRow, bool> selector)
        {
            var first = selector(_selectedRows[0]);
            return _selectedRows.All(r => selector(r) == first) ? first : (bool?)null;
        }

        /// <summary>The timestamp the selection agrees on, or none when it disagrees.</summary>
        private DateTime? SharedUnlockTime()
        {
            var first = _selectedRows[0].UnlockTime;
            return _selectedRows.All(r => Nullable.Equals(r.UnlockTime, first)) ? first : null;
        }

        private AchievementFilterScope SharedScope()
        {
            var first = _selectedRows[0].FilterScope;
            return _selectedRows.All(r => r.FilterScope == first) ? first : AchievementFilterScope.Mixed;
        }

        public bool HasRows
        {
            get => _hasRows;
            private set
            {
                if (SetValueAndReturn(ref _hasRows, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasChanges
        {
            get => _hasChanges;
            private set
            {
                if (SetValueAndReturn(ref _hasChanges, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasValidationErrors
        {
            get => _hasValidationErrors;
            private set
            {
                if (SetValueAndReturn(ref _hasValidationErrors, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (SetValueAndReturn(ref _isSaving, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool CanSave => HasChanges && !HasValidationErrors && !IsSaving;

        public static IReadOnlyList<CustomAchievementSelectionOption> RarityOptions { get; } =
            new[]
            {
                RarityTier.Common,
                RarityTier.Uncommon,
                RarityTier.Rare,
                RarityTier.UltraRare
            }
            .Select(tier => new CustomAchievementSelectionOption(tier.ToString(), tier.ToDisplayText()))
            .ToList();

        public IReadOnlyList<CustomAchievementSelectionOption> TrophyTypeOptions { get; } =
            new[]
            {
                new CustomAchievementSelectionOption(string.Empty, L("LOCPlayAch_Common_None", "None")),
                new CustomAchievementSelectionOption("bronze", L("LOCPlayAch_Trophy_Bronze", "Bronze")),
                new CustomAchievementSelectionOption("silver", L("LOCPlayAch_Trophy_Silver", "Silver")),
                new CustomAchievementSelectionOption("gold", L("LOCPlayAch_Trophy_Gold", "Gold")),
                new CustomAchievementSelectionOption("platinum", L("LOCPlayAch_Trophy_Platinum", "Platinum"))
            };

        /// <summary>
        /// The filter scale as three choices. Reuses the Filters tab's own wording so the option
        /// names match what that tab called the two flags.
        /// </summary>
        public IReadOnlyList<AchievementFilterScopeOption> FilterScopeOptions { get; } =
            new[]
            {
                new AchievementFilterScopeOption(
                    AchievementFilterScope.None,
                    L("LOCPlayAch_Common_None", "None")),
                new AchievementFilterScopeOption(
                    AchievementFilterScope.Summary,
                    L("LOCPlayAch_ManageAchievements_Filters_FilterOutOfSummaries", "Summaries")),
                new AchievementFilterScopeOption(
                    AchievementFilterScope.All,
                    L("LOCPlayAch_ManageAchievements_Filters_FilterOut", "All"))
            };

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetValueAndReturn(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatusText));
                }
            }
        }

        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        public bool StatusIsError
        {
            get => _statusIsError;
            private set => SetValue(ref _statusIsError, value);
        }

        public void ReloadData()
        {
            // The reload re-reads the cache, so a staged unlock has to be written first or the
            // checkbox the user just ticked visibly reverts.
            FlushManualUnlocks();

            // The snapshot caches hydrated data until something invalidates it, and the only
            // invalidation runs through the host on a debounced notification. A reload that follows
            // one of this view model's own writes -- revert, link, unlink -- would otherwise re-read
            // the state from before the write and show it unchanged.
            _gameDataSnapshotProvider?.Invalidate();
            try
            {
                var data = _gameCustomDataStore.LoadOrDefault(_gameId);
                if (_includeProviderAchievements)
                {
                    // Hydration merges custom achievements in but only stamps each row's order
                    // index; it leaves the collection in provider order. The order has to be
                    // applied here or a saved reorder would persist and then be ignored on reload.
                    // Custom and provider achievements sort together, which is what lets an
                    // authored achievement sit between two provider ones.
                    var hydrated = _gameDataSnapshotProvider?.GetHydratedGameData();
                    var achievements = (hydrated?.Achievements ?? new List<AchievementDetail>())
                        .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                        .ToList();
                    // Captured before the overlay is applied: AchievementDetail.DefaultOrderIndex
                    // is the position under the user's order, so the provider's own position is
                    // only available here, and reverting a row's order needs it.
                    var providerPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < achievements.Count; i++)
                    {
                        providerPositions[achievements[i].ApiName] = i;
                    }

                    var ordered = AchievementOrderHelper.ApplyOrder(
                        achievements,
                        a => a.ApiName,
                        hydrated?.AchievementOrder);

                    ReplaceRows(ordered
                        .Select(achievement =>
                        {
                            var row = AchievementEditorRow.FromAchievementDetail(achievement);
                            if (row != null &&
                                providerPositions.TryGetValue(achievement.ApiName, out var position))
                            {
                                row.ProviderOrderIndex = position;
                            }

                            return row;
                        })
                        .Where(row => row != null));
                }
                else
                {
                    ReplaceRows((data?.CustomAchievements ?? new List<CustomAchievementDefinition>())
                        .Select(AchievementEditorRow.FromDefinition));
                }
                CaptureCollectionBaseline();
                ApplyProviderIconBaselines();
                RefreshAssignmentState();
                RefreshCustomProviderState();
                ApplyManualTrackingToRows();
                RebuildSearchIndex();
                RebuildFilterOptions();
                SeedOverrideWriteCache();
                SetStatus(null, false);
                RefreshComputedState();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading custom achievements for gameId={_gameId}.");
                ReplaceRows(Array.Empty<AchievementEditorRow>());
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Stamps each row with the provider's own icons and the managed-cache file stem, read
        /// from the raw snapshot because the rows themselves show the overridden values.
        /// </summary>
        private void ApplyProviderIconBaselines()
        {
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                AchievementRows.Select(row => row?.OriginalApiName));
            var rawByApiName = _gameDataSnapshotProvider?.GetRawGameData()?.Achievements?
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .GroupBy(a => NormalizeText(a.ApiName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);

            // Without the raw snapshot every row's icon would read as different from a provider
            // path of null, so the override maps are only safe to rebuild once it has been seen.
            _providerIconBaselinesResolved = rawByApiName.Count > 0;

            foreach (var row in AchievementRows)
            {
                var apiName = NormalizeText(row?.OriginalApiName);
                if (row == null || string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                row.IconFileStem = fileStems.TryGetValue(apiName, out var stem) ? stem : null;
                if (rawByApiName.TryGetValue(apiName, out var raw))
                {
                    row.ProviderUnlockedIconPath = raw.UnlockedIconPath;
                    row.ProviderLockedIconPath = raw.LockedIconPath;
                    row.ProviderHidden = raw.Hidden;
                }
            }
        }

        public void RefreshData()
        {
            if (!HasChanges)
            {
                ReloadData();
            }
            else
            {
                RefreshCustomProviderState();
            }
        }

        /// <summary>
        /// Drops the store subscription when the Manage window discards this view model.
        /// </summary>
        public void Detach()
        {
            // Before the notification flush, which it ends by raising itself.
            FlushManualUnlocks();
            // A pending notification must not be lost when the tab closes.
            FlushAssignmentsChanged();
            if (_customProviderStore != null)
            {
                _customProviderStore.Changed -= CustomProviderStore_Changed;
            }
        }

        private void RefreshCustomProviderState()
        {
            // The raw snapshot is the synthetic custom projection only when the cache holds no
            // provider data for the game; a real provider key means the assignment does not apply.
            var rawData = _gameDataSnapshotProvider?.GetRawGameData();
            IsCustomOnlyGame = _customProviderStore != null &&
                               rawData != null &&
                               CustomProviderKeys.IsBaseKey(rawData.ProviderKey);

            RefreshManualTrackingState(rawData);
            RebuildCustomProviderOptions();
        }

        private void RebuildCustomProviderOptions()
        {
            _isSyncingCustomProvider = true;
            try
            {
                var assignedId = CustomProviderKeys.NormalizeId(_gameCustomDataStore.LoadOrDefault(_gameId)?.CustomProviderId);
                CustomProviderOptions.Clear();

                ProviderRegistry.TryResolveProviderVisuals(CustomProviderKeys.BaseKey, out var defaultIconKey, out var defaultColorHex);
                var defaultOption = new CustomProviderOption(
                    null,
                    ResourceProvider.GetString("LOCPlayAch_Common_Default"),
                    defaultIconKey,
                    defaultColorHex);
                CustomProviderOptions.Add(defaultOption);

                var selected = defaultOption;
                foreach (var definition in _customProviderStore?.GetAll() ?? (IReadOnlyList<CustomProviderDefinition>)Array.Empty<CustomProviderDefinition>())
                {
                    // Resolved through the registry so a provider without an imported SVG lists
                    // with the default icon, exactly as the rest of the UI renders it.
                    if (!ProviderRegistry.TryResolveProviderVisuals(CustomProviderKeys.Build(definition.Id), out var iconKey, out var colorHex))
                    {
                        iconKey = CustomProviderKeys.BaseIconKey;
                        colorHex = definition.ColorHex;
                    }

                    var option = new CustomProviderOption(
                        definition.Id,
                        definition.Name,
                        iconKey,
                        colorHex);
                    CustomProviderOptions.Add(option);
                    if (assignedId != null && string.Equals(option.Id, assignedId, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = option;
                    }
                }

                SelectedCustomProviderOption = selected;
            }
            finally
            {
                _isSyncingCustomProvider = false;
            }

            LoadSelectedProviderEditor();
        }

        private void LoadSelectedProviderEditor()
        {
            var id = SelectedCustomProviderOption?.Id;
            _selectedCustomProvider = id != null &&
                                      _customProviderStore != null &&
                                      _customProviderStore.TryGet(id, out var definition)
                ? definition
                : null;

            OnPropertyChanged(nameof(HasSelectedCustomProvider));
            RaiseCommandStates();
        }

        private void ApplyCustomProviderAssignment(string customProviderId)
        {
            try
            {
                _achievementOverridesService.SetCustomProvider(_gameId, customProviderId);
                SetStatus(null, false);
                // The Manage window header re-resolves the provider name on this event.
                CustomAchievementsSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed assigning custom provider '{customProviderId}' for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        private void CustomProviderStore_Changed(object sender, CustomProviderChangedEventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(RebuildCustomProviderOptions));
                return;
            }

            RebuildCustomProviderOptions();
        }

        private void AddCustomProvider()
        {
            if (_customProviderStore == null || _showEditor == null)
            {
                return;
            }

            var editor = new CustomProviderEditorViewModel(null, _pickColor, _logger)
            {
                Name = ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_ProviderNewName")
            };
            if (_showEditor(editor) != CustomProviderEditorResult.Saved)
            {
                return;
            }

            try
            {
                var definition = editor.BuildDefinition();
                definition.Id = _customProviderStore.GenerateUniqueId();
                var stored = _customProviderStore.Upsert(definition);

                // Changed already rebuilt the options; selecting the new one assigns it to this game.
                var option = CustomProviderOptions.FirstOrDefault(candidate =>
                    string.Equals(candidate?.Id, stored.Id, StringComparison.OrdinalIgnoreCase));
                if (option != null)
                {
                    SelectedCustomProviderOption = option;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed creating a custom provider.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        private void EditCustomProvider()
        {
            var definition = _selectedCustomProvider;
            if (definition == null || _customProviderStore == null || _showEditor == null)
            {
                return;
            }

            var editor = new CustomProviderEditorViewModel(definition, _pickColor, _logger);
            var result = _showEditor(editor);
            try
            {
                switch (result)
                {
                    case CustomProviderEditorResult.Saved:
                        // The store raises Changed, which rebuilds the options and repaints every
                        // game assigned to this provider.
                        _customProviderStore.Upsert(editor.BuildDefinition());
                        SetStatus(null, false);
                        break;
                    case CustomProviderEditorResult.Deleted:
                        DeleteCustomProvider(definition);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving custom provider '{definition.Id}'.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        private void DeleteCustomProvider(CustomProviderDefinition definition)
        {
            if (definition == null || _customProviderStore == null)
            {
                return;
            }

            var otherGameCount = _gameCustomDataStore.LoadAll()
                .Count(data => data != null &&
                               data.PlayniteGameId != _gameId &&
                               string.Equals(data.CustomProviderId, definition.Id, StringComparison.OrdinalIgnoreCase));
            var result = ShowConfirmation(
                string.Format(
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_ProviderDeleteConfirm"),
                    definition.Name,
                    otherGameCount),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            // The plugin clears every assignment of a deleted provider through the store, so this
            // game falls back to Default through the same path as the others.
            _customProviderStore.Delete(definition.Id);
        }

        private void AddRow()
        {
            var row = AchievementEditorRow.CreateNew(AchievementRows.Count + 1);
            AssignStableId(row);
            AttachRow(row);
            AchievementRows.Add(row);
            SelectedRow = row;
            SetStatus(null, false);
            RefreshComputedState();
            _ = SaveAsync();
        }

        /// <summary>
        /// The ID is never shown or edited: it is derived once from the initial title, kept unique
        /// against the other rows, and then stays fixed so every ApiName-keyed customization the
        /// other tabs write (category, capstone, notes, order) survives later renames.
        /// </summary>
        private void AssignStableId(AchievementEditorRow row)
        {
            if (row == null || !string.IsNullOrWhiteSpace(row.NormalizedId))
            {
                return;
            }

            var usedIds = new HashSet<string>(
                AchievementRows
                    .Select(existing => existing?.NormalizedId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            row.Id = CustomAchievementProjectionService.GenerateId(row.DisplayName, usedIds);
        }

        /// <summary>
        /// Copies every selected achievement as a new authored one. A provider row duplicates into
        /// an authored copy, which is how a provider achievement becomes a starting point for a
        /// custom one.
        /// </summary>
        private void DuplicateSelected()
        {
            var targets = ResolveSelectionTargets();
            if (targets.Count == 0)
            {
                return;
            }

            // Each copy lands immediately after the last of the originals, so a multi-row duplicate
            // keeps the selection's own order rather than interleaving copies with sources.
            var insertIndex = targets.Max(row => AchievementRows.IndexOf(row)) + 1;
            insertIndex = Math.Max(0, insertIndex);

            AchievementEditorRow lastCopy = null;
            foreach (var source in targets.OrderBy(row => AchievementRows.IndexOf(row)))
            {
                var row = source.CloneForDuplicate();
                AssignStableId(row);
                AttachRow(row);
                AchievementRows.Insert(Math.Min(insertIndex, AchievementRows.Count), row);
                insertIndex++;
                lastCopy = row;
            }

            SelectedRow = lastCopy;
            SetStatus(null, false);
            RefreshComputedState();
            _ = SaveAsync();
        }

        /// <summary>
        /// Assigns a category to every selected achievement, written as one map because categories
        /// are stored per game rather than per achievement. A null label clears the assignment.
        /// </summary>
        public void SetCategoryForSelection(string categoryLabel)
        {
            var targets = ResolveSelectionTargets()
                .Where(row => row.CanEditAssignments && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var normalized = AchievementCategoryTypeHelper.NormalizeCategory(categoryLabel);
            StageAcross(targets, row => row.CategoryLabel = normalized);
            PersistCategoryAssignmentsFromRows();
            SyncBulkRowFromSelection();
        }

        /// <summary>
        /// Adds or removes one category type across the selection. A row may carry several types, so
        /// this toggles the one named rather than replacing the set.
        /// </summary>
        public void SetCategoryTypeForSelection(string categoryType, bool isSelected)
        {
            var targets = ResolveSelectionTargets()
                .Where(row => row.CanEditAssignments && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .ToList();
            var normalizedType = AchievementCategoryTypeHelper.Normalize(categoryType);
            if (targets.Count == 0 || string.IsNullOrWhiteSpace(normalizedType))
            {
                return;
            }

            StageAcross(targets, row => row.CategoryTypeValue = AchievementCategoryTypeHelper.WithCategoryType(
                AchievementCategoryTypeHelper.NormalizeOrDefault(row.CategoryTypeValue),
                normalizedType,
                isSelected));
            PersistCategoryAssignmentsFromRows();
            SyncBulkRowFromSelection();
        }

        /// <summary>
        /// Makes one achievement the game's capstone, or clears it.
        /// </summary>
        /// <remarks>
        /// Single-selection only: the capstone is one string per game, so a multi-row set has no
        /// meaning and the menu offers it only when exactly one row is selected.
        /// </remarks>
        public void SetCapstoneForSelection(bool isCapstone)
        {
            var targets = ResolveSelectionTargets();
            if (targets.Count == 1)
            {
                SetCapstoneForRow(targets[0], isCapstone);
            }
        }

        /// <summary>Whether the selection is one row that is currently the game's capstone.</summary>
        public bool IsSingleCapstoneSelection(out bool isCapstone)
        {
            var targets = ResolveSelectionTargets();
            isCapstone = targets.Count == 1 && targets[0].IsCapstone;
            return targets.Count == 1 && targets[0].CanEditAssignments;
        }

        /// <summary>
        /// Sets the goal flag on every selected achievement, written as one list because goals are
        /// stored as a single ordered collection per game.
        /// </summary>
        public void SetGoalForSelection(bool isGoal)
        {
            var targets = ResolveSelectionTargets();
            if (targets.Count == 0)
            {
                return;
            }

            StageAcross(targets, row => row.IsGoal = isGoal);
            PersistGoalsFromRows();
            SyncBulkRowFromSelection();
        }

        /// <summary>
        /// Sets the filter scope on every selected achievement, written as one pair of sets.
        /// </summary>
        public void SetFilterScopeForSelection(AchievementFilterScope scope)
        {
            var targets = ResolveSelectionTargets();
            if (targets.Count == 0)
            {
                return;
            }

            StageAcross(targets, row => row.SetFilterScopeFromSource(scope));
            PersistFiltersFromRows();
            SyncBulkRowFromSelection();
        }

        /// <summary>
        /// Clears the user's customization for every selected achievement so it shows the
        /// provider's own values again, then reloads so the rows display what was restored.
        /// </summary>
        /// <remarks>
        /// Each facet is cleared through the setter that owns it, so the stored shapes stay
        /// consistent: the per-achievement record for the editable fields, and the whole-collection
        /// writes for categories, filters and goals. A capstone is cleared only when one of the
        /// reverted achievements currently holds it.
        /// </remarks>
        private void RevertSelected()
        {
            var targets = ResolveSelectionTargets()
                .Where(row => !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var message = targets.Count == 1
                ? string.Format(
                    L("LOCPlayAch_ManageAchievements_Editor_RevertConfirmSingle", "Revert \"{0}\" to the provider's values?"),
                    targets[0].DisplayName)
                : string.Format(
                    L("LOCPlayAch_ManageAchievements_Editor_RevertConfirmSelected", "Revert {0} achievements to the provider's values?"),
                    targets.Count);
            if (ShowConfirmation(
                    message,
                    L("LOCPlayAch_ManageAchievements_Editor_Revert", "Revert"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            ResetCustomizations(targets, deleteAuthored: false);
        }

        /// <summary>
        /// Drops every customization the game carries, authored achievements included, and reloads
        /// so the grid shows what the providers supply.
        /// </summary>
        private void ResetRows()
        {
            if (ShowConfirmation(
                    L("LOCPlayAch_ManageAchievements_Custom_ResetConfirm", "Reset all achievement customization for this game?"),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            ResetCustomizations(
                AchievementRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                    .ToList(),
                deleteAuthored: true);
        }

        /// <summary>
        /// Clears the stored customization for the given achievements, and for a whole-game reset
        /// the game-level lists and the authored achievements with them.
        /// </summary>
        /// <remarks>
        /// Each facet is cleared through the writer that owns it, so the stored shapes stay
        /// consistent: the per-achievement record for the editable fields, the icons and the note,
        /// and the whole-collection writes for categories, filters, goals and the order. The rows
        /// are reloaded rather than emptied, because the provider's achievements are not this
        /// editor's to delete -- clearing them from the grid only made it disagree with the store
        /// until the window was reopened.
        /// </remarks>
        private void ResetCustomizations(IReadOnlyList<AchievementEditorRow> targets, bool deleteAuthored)
        {
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            try
            {
                var apiNames = new HashSet<string>(
                    targets.Select(row => row.OriginalApiName),
                    StringComparer.OrdinalIgnoreCase);

                // One store update drops the whole record for every target: the editable fields,
                // the note and the icon overrides together. Clearing field by field would leave
                // the unlock timestamp cleared rather than reverted, because "no timestamp" is
                // itself a stored state.
                _achievementOverridesService.ClearAchievementOverrides(_gameId, apiNames);

                // Whole-collection facets: staged across the rows, then written once each.
                StageAcross(targets, row =>
                {
                    row.CategoryLabel = null;
                    row.CategoryTypeValue = null;
                    row.IsGoal = false;
                    row.SetFilterScopeFromSource(AchievementFilterScope.None);
                });

                PersistCategoryAssignmentsFromRows();
                PersistFiltersFromRows();
                PersistGoalsFromRows();

                if (deleteAuthored || targets.Any(row => row.IsCapstone))
                {
                    _achievementOverridesService.SetCapstone(_gameId, null);
                }

                if (deleteAuthored)
                {
                    // Nothing is left to re-seat against, so the order is dropped outright rather
                    // than rewritten without the reverted rows.
                    _achievementOverridesService.SetAchievementOrderOverride(_gameId, Array.Empty<string>());
                    _achievementOverridesService.SetCustomAchievements(_gameId, Array.Empty<CustomAchievementDefinition>());
                    CustomAchievementsSaved?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    RevertOrderForRows(targets);
                }

                ReloadData();
                SetStatus(null, false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed resetting achievements for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Puts the reverted achievements back where the provider had them, then rewrites the
        /// stored order without them.
        /// </summary>
        /// <remarks>
        /// The order is one positional list for the whole game, so a single achievement cannot be
        /// dropped from it without deciding where it lands: an absent entry sorts to the end, which
        /// is not what reverting means. Each target is therefore re-seated against the remaining
        /// rows by the provider's own index. When the result is already the provider's order the
        /// list is cleared outright, so reverting the last customized row leaves no order override
        /// behind.
        /// </remarks>
        private void RevertOrderForRows(IReadOnlyList<AchievementEditorRow> targets)
        {
            var stored = ResolveCurrentCustomData()?.AchievementOrder;
            if (stored == null || stored.Count == 0)
            {
                return;
            }

            var current = AchievementRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .Select(row => new KeyValuePair<string, int>(row.OriginalApiName, row.ProviderOrderIndex))
                .ToList();
            var restored = AchievementOrderHelper.RestoreDefaultPositions(
                current,
                targets.Select(row => row.OriginalApiName));
            _achievementOverridesService.SetAchievementOrderOverride(_gameId, restored);
        }

        /// <summary>
        /// Removes every selected authored achievement. Provider rows are skipped: the command is
        /// already disabled for a selection that contains one, and a deleted provider achievement
        /// would return on the next refresh.
        /// </summary>
        private void DeleteSelected()
        {
            var targets = ResolveSelectionTargets()
                .Where(row => !row.IsProviderRow)
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            foreach (var row in targets)
            {
                row.PropertyChanged -= Row_PropertyChanged;
                AchievementRows.Remove(row);
            }

            SelectedRow = AchievementRows.FirstOrDefault();
            SetStatus(null, false);
            RefreshComputedState();
            _ = SaveAsync();
        }

        private void ImportFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = CustomAchievementsPackageFilter,
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                MergeImportedDefinitions(_gameCustomDataStore.ImportCustomAchievementsPackage(_gameId, dialog.FileName));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed importing custom achievements from '{dialog.FileName}' for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        private void MergeImportedDefinitions(CustomAchievementTextImportResult result)
        {
            if (result.HasErrors)
            {
                SetStatus(string.Join(Environment.NewLine, result.Errors.Take(8)), true);
                return;
            }

            if (result.Definitions.Count == 0)
            {
                SetStatus(L("LOCPlayAch_ManageAchievements_Custom_NoImportRows", "No importable achievements were found."), true);
                return;
            }

            var updated = 0;
            var added = 0;
            var byId = AchievementRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.NormalizedId))
                .GroupBy(row => row.NormalizedId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var definition in result.Definitions)
            {
                var id = CustomAchievementProjectionService.NormalizeId(definition.Id);
                if (!string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id, out var existing))
                {
                    existing.ApplyDefinition(definition, preserveOriginalId: true);
                    updated++;
                    continue;
                }

                var row = AchievementEditorRow.FromDefinition(definition);
                row.MarkAsNewImport();
                AttachRow(row);
                AchievementRows.Add(row);
                added++;
            }

            SelectedRow = AchievementRows.LastOrDefault();
            SetStatus(
                string.Format(
                    L("LOCPlayAch_ManageAchievements_Custom_ImportSummary", "Imported {0} rows ({1} added, {2} updated)."),
                    result.Definitions.Count,
                    added,
                    updated),
                false);
            RefreshComputedState();
            _ = SaveAsync();
        }

        private const string CustomAchievementsPackageFilter =
            "Playnite Achievements Custom Achievements (*.pacustom)|*.pacustom";

        private void ExportTemplate()
        {
            ExportPackage("custom-achievements-template.pacustom", Array.Empty<CustomAchievementDefinition>(), "custom achievement template");
        }

        private void ExportAchievements()
        {
            var definitions = BuildValidatedDefinitions(out _, out var errors);
            if (errors.Count > 0)
            {
                SetStatus(string.Join(Environment.NewLine, errors.Take(8)), true);
                RefreshComputedState();
                return;
            }

            ExportPackage("custom-achievements.pacustom", definitions, "custom achievements");
        }

        private void ExportPackage(
            string defaultFileName,
            IReadOnlyList<CustomAchievementDefinition> definitions,
            string description)
        {
            var dialog = new SaveFileDialog
            {
                Filter = CustomAchievementsPackageFilter,
                AddExtension = true,
                DefaultExt = GameCustomDataStore.CustomAchievementsPackageFileExtension,
                FileName = defaultFileName,
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var destinationPath = dialog.FileName;
                if (!destinationPath.EndsWith(GameCustomDataStore.CustomAchievementsPackageFileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    destinationPath += GameCustomDataStore.CustomAchievementsPackageFileExtension;
                }

                _gameCustomDataStore.ExportCustomAchievementsPackage(_gameId, definitions, destinationPath);
                SetStatus(L("LOCPlayAch_Status_Succeeded", "Success!"), false);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed exporting {description} to '{dialog.FileName}'.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        private async Task SaveAsync()
        {
            if (!CanSave)
            {
                return;
            }

            try
            {
                IsSaving = true;
                var definitions = BuildValidatedDefinitions(out var renameMap, out var errors);
                if (errors.Count > 0)
                {
                    SetStatus(string.Join(Environment.NewLine, errors.Take(8)), true);
                    RefreshComputedState();
                    return;
                }

                await MaterializeIconSourcesAsync(definitions, errors).ConfigureAwait(true);
                if (errors.Count > 0)
                {
                    SetStatus(string.Join(Environment.NewLine, errors.Take(8)), true);
                    RefreshComputedState();
                    return;
                }

                _achievementOverridesService.SetCustomAchievements(_gameId, definitions, renameMap);
                CommitRowsInPlace(definitions);
                RefreshComputedState();
                CustomAchievementsSaved?.Invoke(this, EventArgs.Empty);
                // The first saved achievement turns a game custom-only (and the last deleted one
                // reverts it); the snapshot was just invalidated by the save handler above.
                RefreshCustomProviderState();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving custom achievements for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
            finally
            {
                IsSaving = false;
                // Edits that landed while the write was in flight get their own save.
                if (HasChanges && !HasValidationErrors)
                {
                    _ = SaveAsync();
                }
            }
        }

        private List<CustomAchievementDefinition> BuildValidatedDefinitions(
            out Dictionary<string, string> renameMap,
            out List<string> errors)
        {
            renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            errors = new List<string>();
            var definitions = new List<CustomAchievementDefinition>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var explicitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < AchievementRows.Count; i++)
            {
                var row = AchievementRows[i];
                // A provider row has no authored definition behind it; its edits are stored as
                // overrides, so emitting one here would turn a provider achievement into a custom
                // one and duplicate it in the list.
                if (row == null || row.IsBlank || row.IsProviderRow)
                {
                    continue;
                }

                var explicitId = row.NormalizedId;
                if (!string.IsNullOrWhiteSpace(explicitId) && !explicitIds.Add(explicitId))
                {
                    errors.Add($"Row {i + 1}: duplicate custom ID '{explicitId}'.");
                    row.ValidationMessage = $"Duplicate ID '{explicitId}'.";
                    continue;
                }

                var definition = row.ToDefinition(usedIds, out var rowErrors);
                if (rowErrors.Count > 0)
                {
                    var message = string.Join(" ", rowErrors);
                    row.ValidationMessage = message;
                    errors.Add($"Row {i + 1}: {message}");
                    continue;
                }

                row.ValidationMessage = null;
                definitions.Add(definition);

                var oldApiName = row.OriginalApiName;
                var newApiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                if (!string.IsNullOrWhiteSpace(oldApiName) &&
                    !string.IsNullOrWhiteSpace(newApiName) &&
                    !string.Equals(oldApiName, newApiName, StringComparison.OrdinalIgnoreCase))
                {
                    renameMap[oldApiName] = newApiName;
                }
            }

            return definitions;
        }

        private async Task MaterializeIconSourcesAsync(
            IReadOnlyList<CustomAchievementDefinition> definitions,
            ICollection<string> errors)
        {
            if (_managedCustomIconService == null || definitions == null || definitions.Count == 0)
            {
                return;
            }

            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                definitions.Select(definition => CustomAchievementProjectionService.BuildApiName(definition.Id)));

            foreach (var definition in definitions)
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                definition.UnlockedIconPath = await MaterializeIconSourceAsync(
                    definition.UnlockedIconPath,
                    fileStem,
                    AchievementIconVariant.Unlocked,
                    errors).ConfigureAwait(true);
                definition.LockedIconPath = await MaterializeIconSourceAsync(
                    definition.LockedIconPath,
                    fileStem,
                    AchievementIconVariant.Locked,
                    errors).ConfigureAwait(true);
            }
        }

        private async Task<string> MaterializeIconSourceAsync(
            string value,
            string fileStem,
            AchievementIconVariant variant,
            ICollection<string> errors)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (_managedCustomIconService.IsManagedCustomIconPath(normalized, _gameIdText))
            {
                return normalized;
            }

            if (IsHttpUrl(normalized))
            {
                try
                {
                    await _managedCustomIconService
                        .MaterializeCustomIconAsync(
                            normalized,
                            _gameIdText,
                            fileStem,
                            variant,
                            CancellationToken.None,
                            overwriteExistingTarget: true)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed caching custom achievement icon URL '{normalized}' for gameId={_gameId}.");
                }

                return normalized;
            }

            if (!File.Exists(normalized))
            {
                errors?.Add($"Icon file does not exist: {normalized}");
                return normalized;
            }

            var managedPath = await _managedCustomIconService
                .MaterializeCustomIconAsync(
                    normalized,
                    _gameIdText,
                    fileStem,
                    variant,
                    CancellationToken.None,
                    overwriteExistingTarget: true)
                .ConfigureAwait(true);

            return string.IsNullOrWhiteSpace(managedPath) ? normalized : managedPath;
        }

        /// <summary>
        /// Marks the rows saved without rebuilding them: BuildValidatedDefinitions emits one
        /// definition per non-blank row in row order, so the two walk together.
        /// </summary>
        private void CommitRowsInPlace(IReadOnlyList<CustomAchievementDefinition> definitions)
        {
            // Committing writes back into the rows, so suppress the per-row change handler for the
            // walk: the caller refreshes the computed state once afterwards.
            _isCommittingRows = true;
            try
            {
                var next = 0;
                foreach (var row in AchievementRows)
                {
                    // Must skip exactly what BuildValidatedDefinitions skipped: the two walk
                    // together, so including a provider row here would shift every later row onto
                    // the wrong definition.
                    if (row == null || row.IsBlank || row.IsProviderRow)
                    {
                        continue;
                    }

                    if (definitions == null || next >= definitions.Count)
                    {
                        break;
                    }

                    row.CommitSaved(definitions[next++]);
                }
            }
            finally
            {
                _isCommittingRows = false;
            }

            CaptureCollectionBaseline();
            RefreshAssignmentState();
        }

        private void ReplaceRows(IEnumerable<AchievementEditorRow> rows)
        {
            var previousSelectedId = SelectedRow?.NormalizedId;
            foreach (var row in AchievementRows)
            {
                row.PropertyChanged -= Row_PropertyChanged;
            }

            // The multi-selection is made of the rows being replaced, and the grid only echoes a
            // fresh one back once it has processed the reset. Dropping it here keeps a selection
            // edit that lands in between from staging onto detached rows, where it would be
            // written out of a collection that no longer contains them.
            SetSelectedRows(Array.Empty<AchievementEditorRow>());

            AchievementRows.Clear();
            foreach (var row in rows ?? Enumerable.Empty<AchievementEditorRow>())
            {
                AttachRow(row);
                AchievementRows.Add(row);
            }

            // Keep the user's place: a save or reload rebuilds the rows, and the details pane
            // should stay on the achievement they were editing.
            SelectedRow = AchievementRows.FirstOrDefault(row =>
                              !string.IsNullOrWhiteSpace(previousSelectedId) &&
                              string.Equals(row?.NormalizedId, previousSelectedId, StringComparison.OrdinalIgnoreCase))
                          ?? AchievementRows.FirstOrDefault();
        }

        /// <summary>
        /// Rereads the per-achievement category, type, and capstone assignments for saved rows.
        /// These live in the game's custom data, edited by the Category and Capstones tabs too,
        /// so rows show the current state rather than anything staged for Save.
        /// </summary>
        private void RefreshAssignmentState()
        {
            _isRefreshingAssignments = true;
            try
            {
                // One resolve for all three: this runs after every save, and each lookup helper
                // would otherwise clone the game's whole record again.
                var resolved = ResolveCurrentCustomData();
                var categoryOverrides = GetCurrentCategoryOverrideMap(resolved);
                var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap(resolved);
                var capstoneApiName = NormalizeText(resolved?.ManualCapstoneApiName);

                foreach (var row in AchievementRows)
                {
                    var apiName = NormalizeText(row?.OriginalApiName);
                    if (row == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        row.CategoryLabel = null;
                        row.CategoryTypeValue = null;
                        row.IsCapstone = false;
                        continue;
                    }

                    // Blank, not the Default sentinel: these two fields hold the user's override
                    // and the writers rebuild the whole stored map from them, so a row standing in
                    // for "no override" has to be empty. Filling it with Default instead made every
                    // uncustomized achievement look like one deliberately filed under Default, and
                    // the next write stamped that over the category its provider gave it.
                    row.CategoryLabel = categoryOverrides.TryGetValue(apiName, out var category)
                        ? category
                        : null;
                    row.CategoryTypeValue = categoryTypeOverrides.TryGetValue(apiName, out var categoryType)
                        ? categoryType
                        : null;
                    row.IsCapstone = string.Equals(apiName, capstoneApiName, StringComparison.OrdinalIgnoreCase);
                }

                RefreshAssignableCategoryOptions(categoryOverrides);
                SyncTypeOptionsToEditTarget();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed loading custom achievement assignments for gameId={_gameId}.");
            }
            finally
            {
                _isRefreshingAssignments = false;
            }
        }

        private void RefreshAssignableCategoryOptions(IReadOnlyDictionary<string, string> categoryOverrides)
        {
            // Same sources the Category tab renders: every achievement's effective label, plus
            // labels that only carry user state (order, art, the summary pick), in tree order.
            var categoryOrder = GameCustomDataLookup.GetAchievementCategoryOrder(_gameId, _settings?.Persisted);
            var labels = new List<string>();
            var achievements = _gameDataSnapshotProvider?.GetHydratedGameData()?.Achievements;
            if (achievements != null)
            {
                foreach (var achievement in achievements)
                {
                    var apiName = NormalizeText(achievement?.ApiName);
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    labels.Add(categoryOverrides.TryGetValue(apiName, out var overrideLabel)
                        ? overrideLabel
                        : AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category));
                }
            }

            labels.AddRange(AchievementRows
                .Select(row => row?.CategoryLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label)));
            labels.AddRange(GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted)?.Keys
                ?? Enumerable.Empty<string>());
            labels.AddRange(categoryOrder ?? new List<string>());
            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted);
            if (!string.IsNullOrWhiteSpace(summaryCategory?.Label))
            {
                labels.Add(summaryCategory.Label);
            }

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(
                labels.Where(label => !string.IsNullOrWhiteSpace(label)),
                categoryOrder);
            CollectionHelper.SynchronizeCollection(AssignableCategoryOptions, ordered);
        }

        private void SyncTypeOptionsToEditTarget()
        {
            if (TypeSelectionOptions == null)
            {
                return;
            }

            _isSyncingTypeOptions = true;
            try
            {
                // The pane's other controls bind EditTarget, so seeding these from SelectedRow left
                // the type ticks describing one row while the button beside them described the
                // whole selection.
                var selectedTypes = new HashSet<string>(
                    AchievementCategoryTypeHelper.ParseValues(EditTarget?.CategoryTypeValue),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var option in TypeSelectionOptions)
                {
                    option.IsSelected = selectedTypes.Contains(option.Value);
                }
            }
            finally
            {
                _isSyncingTypeOptions = false;
            }
        }

        private void TypeSelectionOption_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isSyncingTypeOptions ||
                !string.Equals(e?.PropertyName, nameof(CategoryTypeSelectionOption.IsSelected), StringComparison.Ordinal) ||
                !(sender is CategoryTypeSelectionOption option))
            {
                return;
            }

            SetCategoryTypeForSelection(option.Value, option.IsSelected);
        }

        private void PersistAssignmentMaps(
            IReadOnlyDictionary<string, string> categoryOverrides,
            IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            try
            {
                _achievementOverridesService.SetAchievementCategoryOverrides(_gameId, categoryOverrides, categoryTypeOverrides);
                RefreshAssignmentState();
                AssignmentsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving custom achievement category assignments for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        private void SetCapstoneForRow(AchievementEditorRow row, bool isCapstone)
        {
            var apiName = NormalizeText(row?.OriginalApiName);
            if (string.IsNullOrWhiteSpace(apiName))
            {
                RefreshAssignmentState();
                return;
            }

            var currentCapstone = NormalizeText(GameCustomDataLookup.GetManualCapstone(_gameId, _settings?.Persisted));
            var isCurrent = string.Equals(currentCapstone, apiName, StringComparison.OrdinalIgnoreCase);
            if (isCapstone == isCurrent)
            {
                return;
            }

            var targetApiName = isCapstone ? apiName : null;
            try
            {
                _achievementOverridesService.SetCapstone(_gameId, targetApiName);
                RefreshAssignmentState();
                CapstoneChanged?.Invoke(this, new CapstoneChangedEventArgs(targetApiName, isCapstone ? row.DisplayName : null));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving capstone for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
                RefreshAssignmentState();
            }
        }

        /// <summary>
        /// Reads the game's customization once. Each <c>GameCustomDataLookup.Get*</c> helper
        /// resolves and clones the whole record, so a caller that needs several of them resolves
        /// once here instead of paying for it per value.
        /// </summary>
        private const string ManualProviderKey = "Manual";

        private ResolvedGameCustomData ResolveCurrentCustomData() =>
            GameCustomDataLookup.ResolveGameCustomData(_gameId, _settings?.Persisted);

        private Dictionary<string, string> GetCurrentCategoryOverrideMap(ResolvedGameCustomData resolved = null)
        {
            var source = (resolved ?? ResolveCurrentCustomData())?.AchievementCategoryOverrides
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var apiName = NormalizeText(pair.Key);
                var category = AchievementCategoryTypeHelper.NormalizeCategory(pair.Value);
                if (!string.IsNullOrWhiteSpace(apiName) && !string.IsNullOrWhiteSpace(category))
                {
                    normalized[apiName] = category;
                }
            }

            return normalized;
        }

        private Dictionary<string, string> GetCurrentCategoryTypeOverrideMap(ResolvedGameCustomData resolved = null)
        {
            var source = (resolved ?? ResolveCurrentCustomData())?.AchievementCategoryTypeOverrides
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var apiName = NormalizeText(pair.Key);
                var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                if (!string.IsNullOrWhiteSpace(apiName) && !string.IsNullOrWhiteSpace(categoryType))
                {
                    normalized[apiName] = categoryType;
                }
            }

            return normalized;
        }

        private void AttachRow(AchievementEditorRow row)
        {
            if (row == null)
            {
                return;
            }

            // Icon masking follows the same display settings as the achievement grids, so a
            // locked or hidden custom row masks its icon until clicked.
            row.ShowHiddenIcon = _settings?.Persisted?.ShowHiddenIcon ?? false;
            row.ShowLockedIcon = _settings?.Persisted?.ShowLockedIcon ?? true;
            row.ConfigureIconPathDisplay(
                path => _managedCustomIconService?.GetManagedDisplayPath(path, _gameIdText) ?? path,
                text => _managedCustomIconService?.ResolveManagedDisplayPath(text, _gameIdText) ?? text);
            row.PropertyChanged -= Row_PropertyChanged;
            row.PropertyChanged += Row_PropertyChanged;
        }

        private void Row_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            // CommitRowsInPlace writes Id and both icon paths back into every row and re-baselines
            // it, so each row raises several changes that would otherwise land here and run a whole
            // RefreshComputedState (a validation pass plus two collection-wide signature builds)
            // per row. That made one edit cost O(rows squared). The commit runs its own
            // RefreshComputedState once it has walked every row.
            if (_isCommittingRows)
            {
                return;
            }

            if (e.PropertyName == nameof(AchievementEditorRow.IsCapstone))
            {
                if (!_isRefreshingAssignments && sender is AchievementEditorRow capstoneRow)
                {
                    SetCapstoneForRow(capstoneRow, capstoneRow.IsCapstone);
                }

                return;
            }

            if (sender is AchievementEditorRow renamedRow &&
                (e.PropertyName == nameof(AchievementEditorRow.DisplayName) ||
                 e.PropertyName == nameof(AchievementEditorRow.Description)))
            {
                // The index caches each row's searchable text, so an edited row would keep matching
                // its old name until the next reload.
                _searchIndex.Invalidate(renamedRow);
            }

            // A selection edit applies the same value to every selected row and then persists the
            // facet once for the whole selection. Letting each row persist itself here as well
            // would write the same store record twice per row. The search index above is still
            // invalidated, because a bulk rename has to be searchable by its new text.
            if (_isApplyingBulk)
            {
                return;
            }

            if (e.PropertyName == nameof(AchievementEditorRow.ValidationMessage) ||
                e.PropertyName == nameof(AchievementEditorRow.IsRevealed) ||
                e.PropertyName == nameof(AchievementEditorRow.IsIconHidden) ||
                e.PropertyName == nameof(AchievementEditorRow.IsLockedIconHidden) ||
                e.PropertyName == nameof(AchievementEditorRow.CanReveal) ||
                e.PropertyName == nameof(AchievementEditorRow.DisplayIcon) ||
                e.PropertyName == nameof(AchievementEditorRow.CategoryLabel) ||
                e.PropertyName == nameof(AchievementEditorRow.CategoryTypeValue) ||
                e.PropertyName == nameof(AchievementEditorRow.CategoryTypeDisplayText))
            {
                return;
            }

            SetStatus(null, false);

            if (sender is AchievementEditorRow editedRow)
            {
                // Notes, goals and filters are ApiName-keyed for every achievement, authored or
                // not, so they persist the same way for both row kinds. Routing them by row kind
                // would silently drop a note taken on a custom achievement.
                if (PersistSharedFacet(editedRow, e.PropertyName))
                {
                    return;
                }

                // A provider row has no authored definition to rewrite: the rest of its edits are
                // stored as per-field overrides, so it never reaches the custom-definition save.
                if (editedRow.IsProviderRow)
                {
                    PersistProviderRowField(editedRow, e.PropertyName);
                    return;
                }
            }

            RefreshComputedState();
            // Like the other Manage tabs, a completed edit persists at once: text boxes commit on
            // focus loss or Enter, toggles and pickers on the click. The commit updates rows in
            // place so the grid keeps focus and selection.
            _ = SaveAsync();
        }

        private void RefreshComputedState()
        {
            HasRows = AchievementRows.Count > 0;
            var errors = new List<string>();
            _ = BuildValidatedDefinitions(out _, out errors);
            HasValidationErrors = errors.Count > 0;
            HasChanges = !string.Equals(BuildCollectionSignature(), _baselineCollectionSignature, StringComparison.Ordinal);
            RaiseCommandStates();
        }

        private void CaptureCollectionBaseline()
        {
            _baselineCollectionSignature = BuildCollectionSignature();
        }

        /// <summary>
        /// A fingerprint of the authored definitions, used to tell whether they need saving.
        /// </summary>
        /// <remarks>
        /// Provider rows are excluded deliberately, and not only because they have no definition to
        /// save: the merged editor lists a game's whole achievement set, so serializing every row
        /// made each edit cost a JSON pass over hundreds of rows to decide whether a handful of
        /// authored ones had changed.
        /// </remarks>
        private string BuildCollectionSignature()
        {
            return JsonConvert.SerializeObject(AchievementRows
                .Where(row => row != null && !row.IsProviderRow)
                .Select(row => row.StateSignature)
                .ToList());
        }

        /// <summary>
        /// Persists the facets stored the same way for every achievement, authored or provider
        /// supplied, because they key off the ApiName rather than living on a provider payload or
        /// a custom definition. Returns true when the edit was handled here.
        /// </summary>
        private bool PersistSharedFacet(AchievementEditorRow row, string propertyName)
        {
            var apiName = row.OriginalApiName;
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return false;
            }

            try
            {
                switch (propertyName)
                {
                    case nameof(AchievementEditorRow.AchievementNote):
                        _achievementOverridesService.SetAchievementNote(_gameId, apiName, row.AchievementNote);
                        RaiseAssignmentsChanged();
                        return true;

                    case nameof(AchievementEditorRow.IsGoal):
                        _achievementOverridesService.SetAchievementGoal(_gameId, apiName, row.IsGoal);
                        RaiseAssignmentsChanged();
                        return true;

                    // FilterScope sets both flags at once and raises this after them, so persisting
                    // on the scope alone writes the game's filter lists once per change.
                    case nameof(AchievementEditorRow.FilterScope):
                        PersistFiltersFromRows();
                        return true;

                    case nameof(AchievementEditorRow.IsFiltered):
                    case nameof(AchievementEditorRow.IsSummaryFiltered):
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed persisting {propertyName} for achievement {apiName}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
                return true;
            }
        }

        /// <summary>
        /// Persists one completed edit on a provider-backed row as a per-achievement override.
        /// </summary>
        /// <remarks>
        /// Unlock status and rarity are absent by design: both stay provider-owned, and the row
        /// disables their editors. The unlock timestamp is only a correction to an achievement that
        /// is already unlocked.
        /// </remarks>
        private void PersistProviderRowField(AchievementEditorRow row, string propertyName)
        {
            var apiName = row.OriginalApiName;
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return;
            }

            try
            {
                switch (propertyName)
                {
                    case nameof(AchievementEditorRow.DisplayName):
                        WriteProviderField(apiName, AchievementEditableField.DisplayName, NormalizeText(row.DisplayName));
                        break;

                    case nameof(AchievementEditorRow.Description):
                        WriteProviderField(apiName, AchievementEditableField.Description, NormalizeText(row.Description));
                        break;

                    case nameof(AchievementEditorRow.PointsText):
                        if (!AchievementEditorFieldRules.TryParsePoints(row.PointsText, out var points))
                        {
                            row.ValidationMessage = ResourceProvider.GetString(
                                "LOCPlayAch_Common_Validation_NonNegativeInteger");
                            return;
                        }

                        row.ValidationMessage = null;
                        WriteProviderField(apiName, AchievementEditableField.Points, points);
                        break;

                    case nameof(AchievementEditorRow.TrophyType):
                        WriteProviderField(apiName, AchievementEditableField.TrophyType, NormalizeText(row.TrophyType));
                        break;

                    // Hiding is a presentation choice rather than a provider fact, so both values
                    // are storable; agreeing with the provider again stores nothing, which is what
                    // keeps a record from being kept for a row that is not customized.
                    case nameof(AchievementEditorRow.Hidden):
                        WriteProviderField(
                            apiName,
                            AchievementEditableField.Hidden,
                            row.Hidden == row.ProviderHidden ? (bool?)null : row.Hidden);
                        break;

                    // Icons are stored as their own maps rather than on the override record, so
                    // they are written whole. Without this the editor accepted an icon for a
                    // provider achievement and lost it on the next reload.
                    case nameof(AchievementEditorRow.UnlockedIconPath):
                        _ = ApplyIconEditAsync(new[] { row }, AchievementIconVariant.Unlocked);
                        break;

                    case nameof(AchievementEditorRow.LockedIconPath):
                        _ = ApplyIconEditAsync(new[] { row }, AchievementIconVariant.Locked);
                        break;

                    case nameof(AchievementEditorRow.Unlocked):
                    case nameof(AchievementEditorRow.UnlockTime):
                    case nameof(AchievementEditorRow.HasUnlockTime):
                    case nameof(AchievementEditorRow.UnlockDate):
                    case nameof(AchievementEditorRow.TimeText):
                    case nameof(AchievementEditorRow.SelectedTimeModeText):
                        // On a manually tracked game the link is the sole home for both unlock
                        // state and unlock time. Writing the per-achievement override instead would
                        // mask the link in the grid while the cache -- and so every count, summary
                        // and theme surface -- kept the link value, with nothing on screen to explain
                        // the disagreement.
                        if (IsManuallyTrackedGame)
                        {
                            StageManualUnlocks();
                            break;
                        }

                        if (propertyName == nameof(AchievementEditorRow.Unlocked) || !row.Unlocked)
                        {
                            return;
                        }

                        WriteProviderField(apiName, AchievementEditableField.UnlockTimeUtc, row.UnlockTime);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed persisting {propertyName} for achievement {apiName}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Moves the dragged achievements relative to a target row, then persists the new order.
        /// Custom and provider rows are the same kind here, which is what lets an authored
        /// achievement be positioned between two provider ones.
        /// </summary>
        public bool MoveItemsByApiName(
            IReadOnlyList<string> draggedApiNames,
            string targetApiName,
            bool insertAfterTarget)
        {
            _logger?.Debug(
                $"[Editor] Reorder drop onto target: dragged={draggedApiNames?.Count ?? 0} " +
                $"target='{targetApiName}' after={insertAfterTarget}.");
            if (draggedApiNames == null || draggedApiNames.Count == 0 || string.IsNullOrWhiteSpace(targetApiName))
            {
                return false;
            }

            var source = AchievementRows.ToList();
            var targetIndex = source.FindIndex(item =>
                string.Equals(
                    (item?.OriginalApiName ?? string.Empty).Trim(),
                    targetApiName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            var selectedIndexes = ResolveSelectedIndexes(source, draggedApiNames);
            var moved = TryMoveItems(source, selectedIndexes, targetIndex, insertAfterTarget);
            if (!moved)
            {
                // A drop that resolves to nothing is silent by design, which makes a wiring mistake
                // look like the drag simply not working. Log which guard rejected it.
                _logger?.Debug(
                    $"[Editor] Reorder drop rejected: dragged={draggedApiNames.Count} " +
                    $"resolvedIndexes={selectedIndexes.Count} target='{targetApiName}' " +
                    $"targetIndex={targetIndex} rows={source.Count} after={insertAfterTarget}.");
            }

            return moved;
        }

        public bool MoveItemsToEndByApiName(IReadOnlyList<string> draggedApiNames)
        {
            // Reached when the drop lands outside any row, so it is logged too: without it a drop
            // that misses the rows is indistinguishable from one that never arrived.
            _logger?.Debug(
                $"[Editor] Reorder drop to end: dragged={draggedApiNames?.Count ?? 0} rows={AchievementRows.Count}.");
            if (draggedApiNames == null || draggedApiNames.Count == 0 || AchievementRows.Count == 0)
            {
                return false;
            }

            var source = AchievementRows.ToList();
            return TryMoveItems(
                source,
                ResolveSelectedIndexes(source, draggedApiNames),
                source.Count - 1,
                insertAfterTarget: true);
        }

        private bool TryMoveItems(
            List<AchievementEditorRow> source,
            IReadOnlyList<int> selectedIndexes,
            int targetIndex,
            bool insertAfterTarget)
        {
            if (source == null ||
                source.Count == 0 ||
                selectedIndexes == null ||
                selectedIndexes.Count == 0 ||
                targetIndex < 0)
            {
                return false;
            }

            if (!AchievementOrderHelper.TryReorder(
                source,
                selectedIndexes,
                targetIndex,
                insertAfterTarget,
                out var reordered))
            {
                return false;
            }

            CollectionHelper.SynchronizeCollection(AchievementRows, reordered);
            PersistCurrentOrder();
            return true;
        }

        private static List<int> ResolveSelectedIndexes(
            IReadOnlyList<AchievementEditorRow> source,
            IReadOnlyList<string> draggedApiNames)
        {
            var normalized = AchievementOrderHelper.NormalizeApiNames(draggedApiNames);
            if (normalized.Count == 0)
            {
                return new List<int>();
            }

            var wanted = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);
            var indexes = new List<int>();
            for (var i = 0; i < source.Count; i++)
            {
                var apiName = (source[i]?.OriginalApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && wanted.Contains(apiName))
                {
                    indexes.Add(i);
                }
            }

            return indexes;
        }

        /// <summary>
        /// Persists the current row order. Called after a drag, so the list already reflects the
        /// user's intent.
        /// </summary>
        private void PersistCurrentOrder()
        {
            try
            {
                var ordered = AchievementRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                    .Select(row => row.OriginalApiName)
                    .ToList();
                _achievementOverridesService.SetAchievementOrderOverride(_gameId, ordered);
                RaiseAssignmentsChanged();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving achievement order for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Applies one edited field from the bulk proxy to every selected row.
        /// </summary>
        /// <remarks>
        /// Only the field the user actually edited is applied, so the blank "mixed" fields are left
        /// alone rather than clearing values the rows disagreed on. Facets stored as one collection
        /// (categories, filters, goals) are staged across the rows and written once, because each
        /// of their setters rewrites the game's whole map or list.
        /// </remarks>
        private void BulkRow_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isApplyingBulk || !(sender is AchievementEditorRow bulk) || _selectedRows.Count == 0)
            {
                return;
            }

            var property = e?.PropertyName;
            if (string.IsNullOrEmpty(property))
            {
                return;
            }

            _isApplyingBulk = true;
            try
            {
                switch (property)
                {
                    case nameof(AchievementEditorRow.CategoryLabel):
                        StageAcrossSelection(row => row.CategoryLabel = bulk.CategoryLabel);
                        PersistCategoryAssignmentsFromRows();
                        return;

                    case nameof(AchievementEditorRow.CategoryTypeValue):
                        StageAcrossSelection(row => row.CategoryTypeValue = bulk.CategoryTypeValue);
                        PersistCategoryAssignmentsFromRows();
                        return;

                    case nameof(AchievementEditorRow.FilterScope):
                        // The blank stands for disagreement, not a setting: applying it would
                        // clear every selected row's filter instead of leaving them alone.
                        if (bulk.FilterScope == AchievementFilterScope.Mixed)
                        {
                            return;
                        }

                        StageAcrossSelection(row => row.SetFilterScopeFromSource(bulk.FilterScope));
                        PersistFiltersFromRows();
                        return;

                    case nameof(AchievementEditorRow.IsGoal):
                        StageAcrossSelection(row => row.IsGoal = bulk.IsGoal);
                        PersistGoalsFromRows();
                        return;

                    // Unlock state is stored per collection for both row kinds it applies to: the
                    // link for a manually tracked game, the authored definitions otherwise. Staged
                    // and written once, rather than per row.
                    case nameof(AchievementEditorRow.Unlocked):
                        StageAcrossSelection(row =>
                        {
                            if (row.CanEditUnlocked)
                            {
                                row.SetUnlockedFromSource(bulk.Unlocked);
                                if (!bulk.Unlocked)
                                {
                                    row.UnlockTime = null;
                                }
                            }
                        });
                        PersistUnlockStateFromRows();
                        return;

                    case nameof(AchievementEditorRow.UnlockedIconPath):
                        ApplyIconEditAcrossSelection(
                            row => row.UnlockedIconPath = bulk.UnlockedIconPath,
                            AchievementIconVariant.Unlocked);
                        return;

                    case nameof(AchievementEditorRow.LockedIconPath):
                        ApplyIconEditAcrossSelection(
                            row => row.LockedIconPath = bulk.LockedIconPath,
                            AchievementIconVariant.Locked);
                        return;

                    // Per-achievement fields: each row persists on its own, because they are stored
                    // per achievement rather than as one collection.
                    case nameof(AchievementEditorRow.DisplayName):
                        ApplyPerRow(row => row.DisplayName = bulk.DisplayName, property);
                        return;

                    case nameof(AchievementEditorRow.Description):
                        ApplyPerRow(row => row.Description = bulk.Description, property);
                        return;

                    case nameof(AchievementEditorRow.PointsText):
                        ApplyPerRow(row => row.PointsText = bulk.PointsText, property);
                        return;

                    case nameof(AchievementEditorRow.TrophyType):
                        ApplyPerRow(row => row.TrophyType = bulk.TrophyType, property);
                        return;

                    case nameof(AchievementEditorRow.AchievementNote):
                        ApplyPerRow(row => row.AchievementNote = bulk.AchievementNote, property);
                        return;

                    case nameof(AchievementEditorRow.Hidden):
                        ApplyPerRow(row => row.Hidden = bulk.Hidden, property);
                        return;

                    // Progress is authored state, so a provider row has nowhere to keep it and the
                    // row refuses the edit; skipping it here keeps a mixed selection from silently
                    // dropping half the values it appeared to accept.
                    case nameof(AchievementEditorRow.ProgressNumText):
                        ApplyPerRow(
                            row =>
                            {
                                if (row.CanEditProgress)
                                {
                                    row.ProgressNumText = bulk.ProgressNumText;
                                }
                            },
                            property);
                        return;

                    case nameof(AchievementEditorRow.ProgressDenomText):
                        ApplyPerRow(
                            row =>
                            {
                                if (row.CanEditProgress)
                                {
                                    row.ProgressDenomText = bulk.ProgressDenomText;
                                }
                            },
                            property);
                        return;

                    // The time picker's other properties each drive this one, so correcting a
                    // timestamp across the selection is handled once here. A locked achievement
                    // has no unlock to stamp, so it keeps its empty timestamp.
                    case nameof(AchievementEditorRow.UnlockTime):
                        ApplyPerRow(
                            row =>
                            {
                                if (row.Unlocked)
                                {
                                    row.UnlockTime = bulk.UnlockTime;
                                }
                            },
                            property);
                        return;

                    case nameof(AchievementEditorRow.RarityInput):
                        // Refused on provider rows by the row itself; a mixed selection is marked
                        // as provider-backed, so this only reaches an all-authored selection.
                        ApplyPerRow(row => row.RarityInput = bulk.RarityInput, property);
                        return;
                }
            }
            finally
            {
                _isApplyingBulk = false;
            }
        }

        /// <summary>Sets a value on every selected row without each one persisting separately.</summary>
        private void StageAcrossSelection(Action<AchievementEditorRow> apply) =>
            StageAcross(_selectedRows, apply);

        /// <summary>
        /// Applies one icon across the selection: the provider rows through the override maps, the
        /// authored ones through their own definitions.
        /// </summary>
        private void ApplyIconEditAcrossSelection(Action<AchievementEditorRow> apply, AchievementIconVariant variant)
        {
            var targets = _selectedRows.ToList();
            StageAcross(targets, apply);
            _ = ApplyIconEditAsync(targets, variant);
            if (targets.Any(row => !row.IsProviderRow))
            {
                RefreshComputedState();
                _ = SaveAsync();
            }
        }

        /// <summary>
        /// Applies a staged edit to each row without raising the per-row persist, for facets that
        /// are written once for the whole collection afterwards.
        /// </summary>
        /// <remarks>
        /// The rows still raise their changes: suppressing those left the grid showing the old
        /// value until the window was reopened, because the notification the DataGrid binds to is
        /// the same one the persist listens for. Only the persist is held off, through the flag the
        /// row handler checks.
        /// </remarks>
        private void StageAcross(IEnumerable<AchievementEditorRow> rows, Action<AchievementEditorRow> apply)
        {
            var wasApplying = _isApplyingBulk;
            _isApplyingBulk = true;
            try
            {
                foreach (var row in rows)
                {
                    apply(row);
                }
            }
            finally
            {
                _isApplyingBulk = wasApplying;
            }
        }

        /// <summary>
        /// Sets a value on every selected row and lets each persist itself, for fields stored per
        /// achievement rather than as one collection.
        /// </summary>
        private void ApplyPerRow(Action<AchievementEditorRow> apply, string propertyName)
        {
            foreach (var row in _selectedRows)
            {
                apply(row);
                if (PersistSharedFacet(row, propertyName))
                {
                    continue;
                }

                if (row.IsProviderRow)
                {
                    PersistProviderRowField(row, propertyName);
                }
            }

            // Authored rows are stored as one definition list, so a single save covers all of them.
            if (_selectedRows.Any(row => !row.IsProviderRow))
            {
                RefreshComputedState();
                _ = SaveAsync();
            }
        }

        private void PersistCategoryAssignmentsFromRows()
        {
            PersistAssignmentMaps(
                BuildAssignmentMap(row => row.CategoryLabel, row => row.ProviderCategoryLabel, CategoryPathHelper.IsSame),
                BuildAssignmentMap(
                    row => row.CategoryTypeValue,
                    row => row.ProviderCategoryTypeValue,
                    (assigned, provider) => string.Equals(
                        AchievementCategoryTypeHelper.NormalizeOrDefault(assigned),
                        AchievementCategoryTypeHelper.NormalizeOrDefault(provider),
                        StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Writes the icon overrides for the provider-backed rows as one pair of maps.
        /// </summary>
        /// <remarks>
        /// A row shows its effective icon, so an entry is written only where that differs from the
        /// provider's own art; an icon cleared back to the provider's leaves no entry, which is
        /// what removes the override. Authored rows keep their icons on their own definition and
        /// are written by the save, so they stay out of both maps.
        /// </remarks>
        private void PersistIconOverridesFromRows()
        {
            // The maps are written whole, so rebuilding them without the provider baseline would
            // both stamp every provider icon in as an override and drop the real ones already
            // stored. Refusing the write leaves the stored icons alone.
            if (!_providerIconBaselinesResolved)
            {
                _logger?.Warn($"Skipped writing icon overrides for gameId={_gameId}: no provider data to compare against.");
                return;
            }

            try
            {
                var unlockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in AchievementRows)
                {
                    var apiName = NormalizeText(row?.OriginalApiName);
                    if (row == null || !row.IsProviderRow || string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    var unlocked = NormalizeText(row.UnlockedIconPath);
                    if (!string.IsNullOrWhiteSpace(unlocked) &&
                        !string.Equals(unlocked, NormalizeText(row.ProviderUnlockedIconPath), StringComparison.OrdinalIgnoreCase))
                    {
                        unlockedOverrides[apiName] = unlocked;
                    }

                    var locked = NormalizeText(row.LockedIconPath);
                    if (!string.IsNullOrWhiteSpace(locked) &&
                        !string.Equals(locked, NormalizeText(row.ProviderLockedIconPath), StringComparison.OrdinalIgnoreCase))
                    {
                        lockedOverrides[apiName] = locked;
                    }
                }

                _achievementOverridesService.SetIconOverridesAndCustomAchievementIcons(
                    _gameId,
                    unlockedOverrides,
                    lockedOverrides,
                    new Dictionary<string, (string Unlocked, string Locked)>(StringComparer.OrdinalIgnoreCase));
                RaiseAssignmentsChanged();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving achievement icon overrides for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Copies a chosen image into the managed icon cache for each row it was set on, then
        /// writes the override maps once.
        /// </summary>
        /// <remarks>
        /// Clearing an icon puts the provider's own back on the row, which is both what the user
        /// should see and what leaves no override behind.
        /// </remarks>
        private async Task ApplyIconEditAsync(IReadOnlyList<AchievementEditorRow> rows, AchievementIconVariant variant)
        {
            var errors = new List<string>();
            foreach (var row in rows ?? Array.Empty<AchievementEditorRow>())
            {
                if (row == null || !row.IsProviderRow)
                {
                    continue;
                }

                var current = NormalizeText(ReadIcon(row, variant));
                if (string.IsNullOrWhiteSpace(current))
                {
                    StageAcross(new[] { row }, target => WriteIcon(target, variant, ReadProviderIcon(target, variant)));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.IconFileStem))
                {
                    continue;
                }

                var materialized = await MaterializeIconSourceAsync(current, row.IconFileStem, variant, errors)
                    .ConfigureAwait(true);
                if (!string.Equals(materialized, current, StringComparison.Ordinal))
                {
                    StageAcross(new[] { row }, target => WriteIcon(target, variant, materialized));
                }
            }

            if (errors.Count > 0)
            {
                SetStatus(string.Join(Environment.NewLine, errors.Take(8)), true);
            }

            PersistIconOverridesFromRows();
        }

        private static string ReadIcon(AchievementEditorRow row, AchievementIconVariant variant) =>
            variant == AchievementIconVariant.Locked ? row.LockedIconPath : row.UnlockedIconPath;

        private static string ReadProviderIcon(AchievementEditorRow row, AchievementIconVariant variant) =>
            variant == AchievementIconVariant.Locked ? row.ProviderLockedIconPath : row.ProviderUnlockedIconPath;

        private static void WriteIcon(AchievementEditorRow row, AchievementIconVariant variant, string value)
        {
            if (variant == AchievementIconVariant.Locked)
            {
                row.LockedIconPath = value;
            }
            else
            {
                row.UnlockedIconPath = value;
            }
        }

        private void PersistGoalsFromRows()
        {
            var goals = AchievementRows
                .Where(row => row != null && row.IsGoal && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .Select(row => row.OriginalApiName)
                .ToList();
            _achievementOverridesService.SetGoalAchievements(_gameId, goals);
            RaiseAssignmentsChanged();
        }

        /// <summary>
        /// Rebuilds one stored assignment map from the rows, keeping only the assignments that are
        /// really the user's.
        /// </summary>
        /// <remarks>
        /// Both maps are written whole, so every row that carries no assignment has to leave no
        /// entry. An assignment that only restates what the provider already says is dropped too,
        /// which is the same economy the Category tab applies when it reparents a row.
        /// </remarks>
        private Dictionary<string, string> BuildAssignmentMap(
            Func<AchievementEditorRow, string> selector,
            Func<AchievementEditorRow, string> providerSelector,
            Func<string, string, bool> matchesProvider)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in AchievementRows)
            {
                var apiName = row?.OriginalApiName;
                var value = selector(row);
                if (string.IsNullOrWhiteSpace(apiName) ||
                    string.IsNullOrWhiteSpace(value) ||
                    matchesProvider(value, providerSelector(row)))
                {
                    continue;
                }

                map[apiName] = value;
            }

            return map;
        }

        /// <summary>
        /// Tells the host that data other surfaces display has changed, so their snapshots reload.
        /// </summary>
        /// <remarks>
        /// Coalesced: the host responds by invalidating its snapshot and rebuilding the library-wide
        /// theme lists, which is far more expensive than the write that triggered it. Editing is
        /// bursty -- a timestamp raises the date, the time and the meridiem, and a bulk edit raises
        /// once per selected row -- so the notification is delayed briefly and collapsed into one.
        /// Flushed on <see cref="Detach"/> so a pending notification cannot be lost when the tab
        /// closes.
        /// </remarks>
        /// <summary>
        /// True when this game's achievements come from a manual link rather than a real provider.
        /// Its rows then own their unlock state, which no other provider row does.
        /// </summary>
        public bool IsManuallyTrackedGame
        {
            get => _isManuallyTrackedGame;
            private set
            {
                if (SetValueAndReturn(ref _isManuallyTrackedGame, value))
                {
                    OnPropertyChanged(nameof(CanLinkManualTracking));
                }
            }
        }

        /// <summary>
        /// Records the unlock state of every manually tracked row into the in-memory link and
        /// schedules the write, rather than writing per tick.
        /// </summary>
        /// <remarks>
        /// Both stores this touches are expensive: the link write serializes the game's whole custom
        /// data blob, and re-projecting the link onto the cache rewrites every achievement row for
        /// the game and runs the cache-changed handlers synchronously. Staging into memory keeps a
        /// tick free, and the pair is then written together so the two can never disagree.
        /// </remarks>
        private void StageManualUnlocks()
        {
            if (!IsManuallyTrackedGame)
            {
                return;
            }

            _manualUnlocksPending = true;
            if (_manualUnlockFirstPendingUtc == DateTime.MinValue)
            {
                _manualUnlockFirstPendingUtc = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _manualUnlockFirstPendingUtc > ManualUnlockMaxStaleness)
            {
                // A trailing debounce alone never fires while the user keeps ticking, so a long run
                // of edits would sit unwritten. The cap bounds how much is ever in memory only.
                FlushManualUnlocks();
                return;
            }

            if (_manualUnlockDebounce == null)
            {
                _manualUnlockDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _manualUnlockDebounce.Tick += (_, __) => FlushManualUnlocks();
            }

            _manualUnlockDebounce.Stop();
            _manualUnlockDebounce.Start();
        }

        /// <summary>
        /// Writes the staged unlock state: the link first, then the cache projected from it.
        /// </summary>
        /// <remarks>
        /// The order is load-bearing. The link is the only copy of this data that cannot be
        /// re-fetched, so it is committed first; the cache write is a projection a refresh can
        /// rebuild. Reversed, a failed link write would leave a cache the next refresh silently
        /// reverts. The cache is re-read here rather than captured when the edit was staged, so a
        /// provider refresh that landed in between contributes its definitions while the user's
        /// unlock state still wins.
        /// </remarks>
        private void FlushManualUnlocks()
        {
            _manualUnlockDebounce?.Stop();
            if (!_manualUnlocksPending)
            {
                return;
            }

            _manualUnlocksPending = false;
            _manualUnlockFirstPendingUtc = DateTime.MinValue;

            // The write replaces the link's whole unlock map from the rows, so writing it while the
            // grid holds none would erase every recorded unlock. Rows are empty when a load failed,
            // never because the user locked everything -- that is rows present and none unlocked.
            // Manual unlock state is the one thing here with no provider to re-fetch it from.
            if (AchievementRows.Count == 0)
            {
                return;
            }

            try
            {
                var unlocked = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in AchievementRows)
                {
                    // Authored achievements carry their own unlock state on their definition and are
                    // not part of the link, even on a game that also has one.
                    if (row == null ||
                        !row.IsProviderRow ||
                        !row.Unlocked ||
                        string.IsNullOrWhiteSpace(row.OriginalApiName))
                    {
                        continue;
                    }

                    unlocked[row.OriginalApiName] = row.UnlockTime;
                }

                if (!_achievementOverridesService.SetManualUnlockStates(_gameId, unlocked))
                {
                    return;
                }

                _manualLinkApplier?.Invoke(_gameId);
                RaiseAssignmentsChanged();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving manual unlock state for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Stamps the game-level manual-tracking flag onto every row, so each row can answer whether
        /// its unlock state is editable without reaching back to the game.
        /// </summary>
        private void ApplyManualTrackingToRows()
        {
            foreach (var row in AchievementRows)
            {
                if (row != null)
                {
                    row.IsManuallyTrackedGame = IsManuallyTrackedGame;
                }
            }

            if (_bulkRow != null)
            {
                _bulkRow.IsManuallyTrackedGame = IsManuallyTrackedGame;
            }
        }

        /// <summary>
        /// Persists unlock state after a bulk edit, routed by where that game stores it: the manual
        /// link, or the authored achievement definitions.
        /// </summary>
        private void PersistUnlockStateFromRows()
        {
            if (IsManuallyTrackedGame)
            {
                StageManualUnlocks();
                return;
            }

            RefreshComputedState();
            _ = SaveAsync();
        }

        /// <summary>
        /// Whether the header offers manual linking for this game. Same rule as the Manage window's
        /// nav rail, through the shared helper, so the two surfaces cannot disagree about when
        /// manual tracking is on offer.
        /// </summary>
        public bool CanLinkManualTracking
        {
            get => _canLinkManualTracking;
            private set
            {
                if (SetValueAndReturn(ref _canLinkManualTracking, value))
                {
                    ManualLinkCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private void RefreshManualTrackingState(GameAchievementData rawData)
        {
            // ProviderKey, not ProviderPlatformKey: a link with a display-platform override reports
            // the platform it stands in for (say PSN) while the provider stays Manual, so testing
            // the platform key would miss every overridden link.
            var link = _gameCustomDataStore.LoadOrDefault(_gameId)?.ManualLink;
            var hasLink = link != null;
            IsManuallyTrackedGame =
                hasLink &&
                rawData != null &&
                string.Equals(rawData.ProviderKey, ManualProviderKey, StringComparison.OrdinalIgnoreCase);

            var cachedProviderKey = (rawData?.ProviderKey ?? string.Empty).Trim();
            var hasCachedAchievements = rawData?.Achievements?.Count > 0;
            var hasNonManualProviderData =
                hasCachedAchievements &&
                !string.IsNullOrWhiteSpace(cachedProviderKey) &&
                !string.Equals(cachedProviderKey, ManualProviderKey, StringComparison.OrdinalIgnoreCase);

            CanLinkManualTracking = _showManualLinkDialog != null && ManualTrackingAvailability.CanLink(
                hasLink,
                ManualAchievementsProvider.IsTrackingOverrideEnabled(),
                GameCustomDataLookup.IsExcludedFromRefreshes(_gameId, _settings?.Persisted, _gameCustomDataStore),
                hasCachedAchievements,
                hasNonManualProviderData);

            RefreshSourceHeading(rawData, link);
            UnlinkManualTrackingCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Opens the manual-link dialog, then reloads so a new link's achievements appear as rows.
        /// </summary>
        /// <remarks>
        /// Any pending unlock edits are flushed first: linking rewrites the link wholesale, so a
        /// staged edit written afterwards would be applied against a link the user just replaced.
        /// </remarks>
        private void OpenManualLinkDialog()
        {
            if (_showManualLinkDialog == null)
            {
                return;
            }

            FlushManualUnlocks();
            if (_showManualLinkDialog())
            {
                ReloadData();
                RaiseAssignmentsChanged();
            }
        }

        /// <summary>
        /// Narrows the grid to achievements matching this text. The rows themselves are untouched:
        /// this drives the collection view, so every persist path still sees the whole ordered list.
        /// </summary>
        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetValueAndReturn(ref _filterText, value))
                {
                    _filterQuery = SearchQuery.From(value);
                    NotifyFilterChanged();
                }
            }
        }

        /// <summary>True while the grid shows a subset of the achievements.</summary>
        public bool IsFiltering =>
            _filterQuery.HasValue ||
            !string.IsNullOrWhiteSpace(SelectedCategoryFilter?.Label) ||
            !string.IsNullOrWhiteSpace(SelectedTypeFilter?.Value);

        /// <summary>Raised when the filter text changed and the collection view needs refreshing.</summary>
        public event EventHandler FilterChanged;

        /// <summary>
        /// Whether a row passes the current filter. Matching is by display name, description and
        /// ApiName, through the same index the other achievement lists search with.
        /// </summary>
        public bool MatchesFilter(AchievementEditorRow row)
        {
            if (row == null)
            {
                return false;
            }

            if (_filterQuery.HasValue && !_searchIndex.Matches(row, _filterQuery))
            {
                return false;
            }

            var category = SelectedCategoryFilter?.Label;
            if (!string.IsNullOrWhiteSpace(category) &&
                !string.Equals(row.EffectiveCategoryLabel, category, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var categoryType = SelectedTypeFilter?.Value;
            if (!string.IsNullOrWhiteSpace(categoryType) &&
                !string.Equals(NormalizeText(row.CategoryTypeValue), categoryType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private void RebuildSearchIndex()
        {
            _searchIndex.Rebuild(AchievementRows);
        }


        /// <summary>
        /// Restricts the grid to one category. Null shows every category.
        /// </summary>
        public CategoryPickerOption SelectedCategoryFilter
        {
            get => _selectedCategoryFilter;
            set
            {
                if (SetValueAndReturn(ref _selectedCategoryFilter, value))
                {
                    NotifyFilterChanged();
                }
            }
        }

        /// <summary>
        /// Restricts the grid to one category type. Null shows every type.
        /// </summary>
        public EditorFilterOption SelectedTypeFilter
        {
            get => _selectedTypeFilter;
            set
            {
                if (SetValueAndReturn(ref _selectedTypeFilter, value))
                {
                    NotifyFilterChanged();
                }
            }
        }

        /// <summary>
        /// Categories present on this game's achievements, plus an "all" entry, arranged as the tree
        /// they describe so the drop-down places a nested category rather than spelling out its path.
        /// </summary>
        public ObservableCollection<CategoryPickerOption> CategoryFilterOptions { get; } =
            new ObservableCollection<CategoryPickerOption>();

        /// <summary>Every assignable category type, plus an "all" entry.</summary>
        public ObservableCollection<EditorFilterOption> TypeFilterOptions { get; } =
            new ObservableCollection<EditorFilterOption>();

        private void NotifyFilterChanged()
        {
            OnPropertyChanged(nameof(IsFiltering));
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Rebuilds the category choices from the rows, so the list offers what this game actually
        /// uses rather than every category in the library. Ordered to match the category tree, with
        /// anything unknown to it falling in alphabetically after.
        /// </summary>
        private void RebuildFilterOptions()
        {
            var previousCategory = SelectedCategoryFilter?.Label;
            var previousType = SelectedTypeFilter?.Value;

            // Built through the shared picker resolver, so this drop-down draws the same tree the
            // category grid and the assignment pickers do. Synthesised ancestors are not selectable
            // here: a filter on a category that holds no achievements of its own matches nothing.
            // Every category the game has, in tree order -- the same list the assignment pickers
            // offer. Built from what the rows currently carry, it would have offered only the
            // categories somebody had already overridden, which is not what a filter is for.
            var options = CategoryPickerResolver.BuildOptions(
                AssignableCategoryOptions.ToList(),
                AssignableCategoryOptions.ToList(),
                synthesizedAreSelectable: false);

            CategoryFilterOptions.Clear();
            CategoryFilterOptions.Add(new CategoryPickerOption(
                null,
                L("LOCPlayAch_Common_All", "All"),
                L("LOCPlayAch_Common_All", "All")));
            foreach (var option in options)
            {
                CategoryFilterOptions.Add(option);
            }

            if (TypeFilterOptions.Count == 0)
            {
                TypeFilterOptions.Add(new EditorFilterOption(null, L("LOCPlayAch_Common_All", "All")));
                foreach (var type in AchievementCategoryTypeHelper.AssignableCategoryTypes)
                {
                    TypeFilterOptions.Add(new EditorFilterOption(
                        type,
                        ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName(type)));
                }
            }

            // A filter whose category disappeared falls back to showing everything, rather than
            // leaving the grid mysteriously empty.
            SelectedCategoryFilter = CategoryFilterOptions.FirstOrDefault(option =>
                                         option.IsSelectable &&
                                         string.Equals(option.Label, previousCategory, StringComparison.OrdinalIgnoreCase))
                                     ?? CategoryFilterOptions.FirstOrDefault();
            SelectedTypeFilter = TypeFilterOptions.FirstOrDefault(option =>
                                     string.Equals(option.Value, previousType, StringComparison.OrdinalIgnoreCase))
                                 ?? TypeFilterOptions.FirstOrDefault();
        }

        /// <summary>
        /// What this game's achievements come from, shown as the editor's heading: the manual link,
        /// the custom-provider bucket, or the provider that supplied them.
        /// </summary>
        public string SourceHeading
        {
            get => _sourceHeading;
            private set => SetValue(ref _sourceHeading, value);
        }

        /// <summary>
        /// The cover the plugin draws over a hidden achievement, so the sidebar's Hidden toggle is
        /// marked with the same image the user configured for hiding them everywhere else.
        /// </summary>
        public string HiddenCoverIcon => AchievementIconResolver.GetHiddenFallbackIcon();

        /// <summary>Display-platform choices for a manually tracked game.</summary>
        public IReadOnlyList<ProviderOverrideChoice> DisplayPlatformOptions { get; } =
            ManualDisplayPlatformResolver.BuildDisplayPlatformOptions();

        /// <summary>
        /// The provider key a manually tracked game presents as. Persists on change, then re-projects
        /// the link so the game re-attributes without waiting for a refresh.
        /// </summary>
        public ProviderOverrideChoice SelectedDisplayPlatform
        {
            get => _selectedDisplayPlatform;
            set
            {
                if (!SetValueAndReturn(ref _selectedDisplayPlatform, value) || _isSyncingDisplayPlatform)
                {
                    return;
                }

                try
                {
                    if (_achievementOverridesService.SetManualDisplayPlatform(_gameId, value?.Value))
                    {
                        _manualLinkApplier?.Invoke(_gameId);
                        RaiseAssignmentsChanged();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed setting the manual display platform for gameId={_gameId}.");
                    SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
                }
            }
        }

        /// <summary>Drops the manual link, returning the game to whatever provider supplies it.</summary>
        public RelayCommand UnlinkManualTrackingCommand { get; }

        private void RefreshSourceHeading(GameAchievementData rawData, ManualAchievementLink link)
        {
            if (link != null)
            {
                SourceHeading = ManualAchievementsProvider.GetManageAchievementsLinkSummary(link);
            }
            else if (IsCustomOnlyGame)
            {
                SourceHeading = ProviderRegistry.GetLocalizedName(CustomProviderKeys.BaseKey);
            }
            else
            {
                var providerKey = NormalizeText(rawData?.ProviderKey);
                SourceHeading = string.IsNullOrWhiteSpace(providerKey)
                    ? L("LOCPlayAch_ManageAchievements_Tab_Editor", "Editor")
                    : ProviderRegistry.GetLocalizedName(providerKey);
            }

            _isSyncingDisplayPlatform = true;
            try
            {
                var stored = ManualDisplayPlatformResolver.NormalizeOverride(link?.DisplayPlatformKeyOverride)
                             ?? string.Empty;
                SelectedDisplayPlatform = DisplayPlatformOptions.FirstOrDefault(option =>
                                              string.Equals(option.Value, stored, StringComparison.OrdinalIgnoreCase))
                                          ?? DisplayPlatformOptions.FirstOrDefault();
            }
            finally
            {
                _isSyncingDisplayPlatform = false;
            }
        }

        private void UnlinkManualTracking()
        {
            // Any staged unlock would otherwise be written back against the link just removed,
            // resurrecting it.
            _manualUnlocksPending = false;
            _manualUnlockDebounce?.Stop();
            _unlinkManualTracking?.Invoke();
            ReloadData();
            RaiseAssignmentsChanged();
        }

        private void RaiseAssignmentsChanged()
        {
            if (_assignmentsChangedDebounce == null)
            {
                _assignmentsChangedDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _assignmentsChangedDebounce.Tick += (_, __) => FlushAssignmentsChanged();
            }

            _assignmentsChangedPending = true;
            _assignmentsChangedDebounce.Stop();
            _assignmentsChangedDebounce.Start();
        }

        private void FlushAssignmentsChanged()
        {
            _assignmentsChangedDebounce?.Stop();
            if (!_assignmentsChangedPending)
            {
                return;
            }

            _assignmentsChangedPending = false;
            // The cache-changed cascade this sets off comes back as a refresh request. The editor
            // already shows its own edit, so it must not rebuild every row in response to it.
            SuppressExternalRefresh = true;
            using (Common.PerfScope.Start(_logger, "Editor.FlushAssignmentsChanged", thresholdMs: 10))
            {
                AssignmentsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Set while the editor's own write is still rippling through the cache, so the host can
        /// tell a refresh caused by this editor from one caused by anything else. Reloading the
        /// grid for its own edit both costs a full rebuild and visibly reverts the control the user
        /// just changed, because the reload re-reads the value before the write has settled.
        /// </summary>
        public bool SuppressExternalRefresh { get; set; }

        /// <summary>
        /// Writes one override, skipping the store entirely when the value already matches what is
        /// in effect.
        /// </summary>
        /// <remarks>
        /// Points, trophy type and unlock time mark summaries dirty, so every write costs an
        /// overview rebuild. One interaction can raise several changes for the same stored value --
        /// ticking the unlock-time box raises both HasUnlockTime and UnlockTime, and editing a
        /// timestamp raises the date, the time and the meridiem -- so without this each click paid
        /// for that rebuild more than once.
        /// </remarks>
        private void WriteProviderField(string apiName, AchievementEditableField field, object value)
        {
            var key = apiName + " " + field;
            if (_lastWrittenOverrides.TryGetValue(key, out var previous) && Equals(previous, value))
            {
                return;
            }

            _lastWrittenOverrides[key] = value;
            _achievementOverridesService.SetAchievementFieldOverride(_gameId, apiName, field, value);
            RaiseAssignmentsChanged();
        }

        /// <summary>
        /// Seeds the write cache from the values the rows loaded with, so setting a field to what
        /// it already shows writes nothing. The loaded values are the effective ones, so matching
        /// them needs no override stored at all.
        /// </summary>
        private void SeedOverrideWriteCache()
        {
            _lastWrittenOverrides.Clear();
            foreach (var row in AchievementRows)
            {
                var apiName = row?.OriginalApiName;
                if (string.IsNullOrWhiteSpace(apiName) || !row.IsProviderRow)
                {
                    continue;
                }

                _lastWrittenOverrides[apiName + " " + AchievementEditableField.DisplayName] =
                    NormalizeText(row.DisplayName);
                _lastWrittenOverrides[apiName + " " + AchievementEditableField.Description] =
                    NormalizeText(row.Description);
                _lastWrittenOverrides[apiName + " " + AchievementEditableField.TrophyType] =
                    NormalizeText(row.TrophyType);
                _lastWrittenOverrides[apiName + " " + AchievementEditableField.UnlockTimeUtc] =
                    row.UnlockTime;
                if (AchievementEditorFieldRules.TryParsePoints(row.PointsText, out var points))
                {
                    _lastWrittenOverrides[apiName + " " + AchievementEditableField.Points] = points;
                }
            }
        }

        /// <summary>
        /// Both filter lists are stored as whole sets, so one checkbox rewrites them from the
        /// current row state rather than patching a single entry.
        /// </summary>
        private void PersistFiltersFromRows()
        {
            var filtered = AchievementRows
                .Where(row => row != null && row.IsFiltered && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .Select(row => row.OriginalApiName)
                .ToList();
            var summaryFiltered = AchievementRows
                .Where(row => row != null && row.IsSummaryFiltered && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .Select(row => row.OriginalApiName)
                .ToList();

            _achievementOverridesService.SetAchievementFilters(_gameId, filtered, summaryFiltered);
            RaiseAssignmentsChanged();
        }

        private void RaiseCommandStates()
        {
            AddCommand.RaiseCanExecuteChanged();
            DuplicateCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            RevertCommand.RaiseCanExecuteChanged();
            ImportFileCommand.RaiseCanExecuteChanged();
            ExportTemplateCommand.RaiseCanExecuteChanged();
            ExportAchievementsCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            AddCustomProviderCommand?.RaiseCanExecuteChanged();
            EditCustomProviderCommand?.RaiseCanExecuteChanged();
        }

        // Playnite's dialog service renders themed message boxes; the WPF MessageBox is the
        // unstyled fallback for hosts without an API instance (tests).
        private static MessageBoxResult ShowConfirmation(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            return API.Instance?.Dialogs?.ShowMessage(message, title, buttons, image)
                   ?? MessageBox.Show(message, title, buttons, image);
        }

        private void SetStatus(string value, bool isError)
        {
            StatusText = value;
            StatusIsError = isError;
        }

        private static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    /// <summary>
    /// One choice in the editor's control-bar filters, pairing the stored value with its label.
    /// A null <see cref="Value"/> is the "all" entry and applies no restriction.
    /// </summary>
    public sealed class EditorFilterOption
    {
        public EditorFilterOption(string value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public string Value { get; }

        public string DisplayName { get; }
    }

    /// <summary>
    /// A row of the custom provider selector: the Default entry (null id, bare Custom visuals)
    /// or one stored custom provider.
    /// </summary>
    public sealed class CustomProviderOption
    {
        public CustomProviderOption(string id, string displayName, string iconKey, string colorHex)
        {
            Id = id;
            DisplayName = displayName;
            IconKey = iconKey;
            ColorHex = colorHex;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string IconKey { get; }

        public string ColorHex { get; }

        public bool IsDefault => Id == null;

        public override string ToString() => DisplayName;
    }

    public sealed class AchievementEditorRow : ObservableObject
    {
        private string _id;
        private string _displayName;
        private string _description;
        private bool _unlocked;
        private DateTime? _unlockTime;
        private string _unlockedIconPath;
        private string _lockedIconPath;
        private string _pointsText;
        private string _trophyType;
        private bool _hidden;
        private string _rarity;
        private string _globalPercentUnlockedText;
        private string _rarityInput;
        private bool _rarityInputInvalid;
        private string _progressNumText;
        private string _progressDenomText;
        private string _validationMessage;
        private string _baselineSignature;
        private bool _isProviderRow;
        private string _achievementNote;
        private bool _isGoal;
        private bool _isFiltered;
        private bool _isSummaryFiltered;
        private TimeMode _selectedTimeMode;
        private int _selectedHour;
        private int _selectedMinute;
        private string _timeText;
        private bool _isValidTime = true;
        private bool _isUpdatingFromText;
        private bool _isApplyingPickerUpdate;
        private bool _isManuallyTrackedGame;
        // Display-only, and only ever set on the bulk proxy: the selected rows disagree on this
        // facet, so the pane shows nothing rather than a value that would be applied.
        private bool _filterScopeIsMixed;
        private bool _unlockedIsMixed;
        private bool _hiddenIsMixed;
        private bool _isGoalIsMixed;

        private static readonly string[] TimeModeDisplayNames = { "AM", "PM", "24hr" };

        private bool _isRevealed;
        private bool _showHiddenIcon;
        private bool _showLockedIcon = true;

        public string OriginalApiName { get; private set; }

        public bool IsNew { get; private set; }

        /// <summary>
        /// Mirrors the grid display setting: when false, a locked hidden row masks its icon
        /// behind the hidden placeholder until revealed.
        /// </summary>
        public bool ShowHiddenIcon
        {
            get => _showHiddenIcon;
            set
            {
                if (SetValueAndReturn(ref _showHiddenIcon, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        /// <summary>
        /// Mirrors the grid display setting: when false, a locked row masks its icon behind
        /// the locked placeholder until revealed.
        /// </summary>
        public bool ShowLockedIcon
        {
            get => _showLockedIcon;
            set
            {
                if (SetValueAndReturn(ref _showLockedIcon, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool IsRevealed
        {
            get => _isRevealed;
            set
            {
                if (SetValueAndReturn(ref _isRevealed, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool IsIconHidden => Hidden && !Unlocked && !ShowHiddenIcon && !IsRevealed;

        public bool IsLockedIconHidden => !Unlocked && !ShowLockedIcon && !IsRevealed;

        public bool CanReveal => !Unlocked && ((Hidden && !ShowHiddenIcon) || !ShowLockedIcon);

        public void ToggleReveal()
        {
            if (CanReveal)
            {
                IsRevealed = !IsRevealed;
            }
        }

        private void NotifyRevealStateChanged()
        {
            OnPropertyChanged(nameof(IsIconHidden));
            OnPropertyChanged(nameof(IsLockedIconHidden));
            OnPropertyChanged(nameof(CanReveal));
            OnPropertyChanged(nameof(DisplayIcon));
        }

        private string _categoryLabel;
        private string _categoryTypeValue;
        private bool _isCapstone;

        /// <summary>
        /// True for the stand-in row the details pane binds to while several achievements are
        /// selected. It has no ApiName of its own, so the checks that gate on one must not read it
        /// as an unsaved row.
        /// </summary>
        public bool IsBulkRow { get; set; }

        /// <summary>
        /// Category, type, and capstone are ApiName-keyed custom data shared with the other tabs, so
        /// they are only editable once the row has been saved and has an ApiName. The bulk proxy has
        /// none of its own but every row it stands for does, so it qualifies.
        /// </summary>
        public bool CanEditAssignments => IsBulkRow || !string.IsNullOrWhiteSpace(OriginalApiName);

        /// <summary>
        /// The capstone is one achievement per game, so it has no meaning for a multi-selection.
        /// The proxy refuses it rather than accepting a click it could not apply.
        /// </summary>
        public bool CanEditCapstone => CanEditAssignments && !IsBulkRow;

        /// <summary>
        /// The icons the provider supplies, captured before any override is applied over them.
        /// A row shows its effective icon, so this is the only way to tell an override apart from
        /// the provider's own art, and the only thing to fall back to when one is cleared.
        /// </summary>
        public string ProviderUnlockedIconPath { get; internal set; }

        public string ProviderLockedIconPath { get; internal set; }

        /// <summary>
        /// Whether the provider calls this achievement hidden, captured before any override is
        /// applied, so setting it back to that value clears the override instead of storing it.
        /// </summary>
        public bool ProviderHidden { get; internal set; }

        /// <summary>
        /// The file stem an overriding image is copied to inside the plugin's icon cache, so a
        /// local file or URL survives being moved or going offline.
        /// </summary>
        public string IconFileStem { get; internal set; }

        /// <summary>
        /// The category the provider gave this achievement, kept because <see cref="CategoryLabel"/>
        /// holds the user's override and reads as the Default bucket when there is none. Filtering
        /// needs the effective value, not the override.
        /// </summary>
        public string ProviderCategoryLabel { get; set; }

        /// <summary>
        /// The category type the provider gave this achievement, kept for the same reason as
        /// <see cref="ProviderCategoryLabel"/>: an assignment matching it is not an override.
        /// </summary>
        public string ProviderCategoryTypeValue { get; set; }

        /// <summary>
        /// The category this achievement actually sits in: the user's override when they set one,
        /// otherwise the provider's own.
        /// </summary>
        public string EffectiveCategoryLabel
        {
            get
            {
                var assigned = (CategoryLabel ?? string.Empty).Trim();
                return !string.IsNullOrWhiteSpace(assigned) &&
                       !string.Equals(assigned, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase)
                    ? assigned
                    : AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(ProviderCategoryLabel);
            }
        }

        /// <summary>
        /// The achievement's position in the provider's own order, stamped by the loader before
        /// the user's order is applied so reverting a row can put it back where the provider had
        /// it rather than at the end.
        /// </summary>
        public int ProviderOrderIndex { get; set; } = int.MaxValue;

        /// <summary>
        /// True when this row stands for a provider-supplied achievement rather than one the user
        /// authored. Set by the merged editor, which lists both kinds in one grid.
        /// </summary>
        public bool IsProviderRow
        {
            get => _isProviderRow;
            set
            {
                if (SetValueAndReturn(ref _isProviderRow, value))
                {
                    OnPropertyChanged(nameof(CanEditRarity));
                    OnPropertyChanged(nameof(CanEditUnlocked));
                    OnPropertyChanged(nameof(CanEditProgress));
                }
            }
        }

        /// <summary>
        /// Rarity is user input only for an authored achievement. A provider achievement's rarity is
        /// derived from the unlock percentages the provider reports.
        /// </summary>
        public bool CanEditRarity =>
            ManageAchievements.AchievementEditorFieldRules.CanEditRarity(isCustomRow: !IsProviderRow);

        /// <summary>
        /// True when this row belongs to a game whose achievements are tracked manually. Set by the
        /// loader from the game, not from the row: it is a property of the link, and every row of a
        /// linked game shares it.
        /// </summary>
        public bool IsManuallyTrackedGame
        {
            get => _isManuallyTrackedGame;
            set
            {
                if (SetValueAndReturn(ref _isManuallyTrackedGame, value))
                {
                    OnPropertyChanged(nameof(CanEditUnlocked));
                }
            }
        }

        /// <summary>
        /// Unlock status is authored data on a custom achievement and user-recorded on a manually
        /// tracked game, but provider-owned everywhere else: editing it there would move unlocked
        /// counts and completion, and read as a real unlock to the in-game monitor.
        /// </summary>
        public bool CanEditUnlocked =>
            ManageAchievements.AchievementEditorFieldRules.CanEditUnlockStatus(
                isCustomRow: !IsProviderRow,
                isManuallyTrackedGame: IsManuallyTrackedGame);

        /// <summary>Progress totals are provider-reported; only an authored row defines its own.</summary>
        public bool CanEditProgress => !IsProviderRow;

        public string AchievementNote
        {
            get => _achievementNote;
            set
            {
                if (SetValueAndReturn(ref _achievementNote, value))
                {
                    OnPropertyChanged(nameof(HasAchievementNote));
                    OnPropertyChanged(nameof(NotePreview));
                }
            }
        }

        public bool HasAchievementNote => !string.IsNullOrWhiteSpace(AchievementNote);

        public string NotePreview => AchievementNoteHelper.GetPreviewText(AchievementNote);

        public bool IsGoal
        {
            get => _isGoal;
            set
            {
                if (SetValueAndReturn(ref _isGoal, value))
                {
                    OnPropertyChanged(nameof(IsGoalState));
                }
            }
        }

        public bool IsFiltered
        {
            get => _isFiltered;
            set
            {
                if (SetValueAndReturn(ref _isFiltered, value))
                {
                    OnPropertyChanged(nameof(FilterScope));
                }
            }
        }

        public bool IsSummaryFiltered
        {
            get => _isSummaryFiltered;
            set
            {
                if (SetValueAndReturn(ref _isSummaryFiltered, value))
                {
                    OnPropertyChanged(nameof(IsFilteredFromSummaries));
                    OnPropertyChanged(nameof(FilterScope));
                }
            }
        }

        /// <summary>
        /// The two filter flags as one choice, because they are a scale rather than independent
        /// toggles: hidden nowhere, hidden from summaries only, or hidden everywhere.
        /// </summary>
        /// <remarks>
        /// Hiding an achievement everywhere already hides it from summaries, so the "all" case
        /// sets only <see cref="IsFiltered"/>; storing both would be redundant state that could
        /// disagree with itself.
        /// </remarks>
        public AchievementFilterScope FilterScope
        {
            get
            {
                if (_filterScopeIsMixed)
                {
                    return AchievementFilterScope.Mixed;
                }

                if (IsFiltered)
                {
                    return AchievementFilterScope.All;
                }

                return IsSummaryFiltered ? AchievementFilterScope.Summary : AchievementFilterScope.None;
            }

            set
            {
                // Mixed is what the proxy shows, never something the user can pick: the dropdown
                // does not list it, so this only arrives when a binding echoes the value back.
                if (value == AchievementFilterScope.Mixed || value == FilterScope)
                {
                    return;
                }

                // Leaving the blank behind is the point of the assignment, and the scope it
                // stood for is unknowable, so every pick from a mixed proxy counts as a change.
                _filterScopeIsMixed = false;

                // Set the pair together, then raise once: the two flags persist as whole lists, so
                // letting each raise separately would write the game's filters twice per change.
                SuppressNotifications = true;
                IsFiltered = value == AchievementFilterScope.All;
                IsSummaryFiltered = value == AchievementFilterScope.Summary;
                SuppressNotifications = false;

                OnPropertyChanged(nameof(IsFiltered));
                OnPropertyChanged(nameof(IsSummaryFiltered));
            OnPropertyChanged(nameof(IsFilteredFromSummaries));
                OnPropertyChanged(nameof(FilterScope));
            }
        }

        public string CategoryLabel
        {
            get => _categoryLabel;
            set => SetValue(ref _categoryLabel, value);
        }

        public string CategoryTypeValue
        {
            get => _categoryTypeValue;
            set
            {
                if (SetValueAndReturn(ref _categoryTypeValue, value))
                {
                    OnPropertyChanged(nameof(CategoryTypeDisplayText));
                }
            }
        }

        public string CategoryTypeDisplayText
        {
            get
            {
                // The Default sentinel renders blank in grid cells; a button needs a label.
                var text = AchievementCategoryTypeHelper.ToDisplayText(CategoryTypeValue);
                return string.IsNullOrWhiteSpace(text)
                    ? AchievementCategoryTypeHelper.ToCategoryTypeDisplayText(AchievementCategoryTypeHelper.NormalizeOrDefault(null))
                    : text;
            }
        }

        public bool IsCapstone
        {
            get => _isCapstone;
            set => SetValue(ref _isCapstone, value);
        }

        public string Id
        {
            get => _id;
            set => SetValue(ref _id, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set => SetValue(ref _displayName, value);
        }

        public string Description
        {
            get => _description;
            set => SetValue(ref _description, value);
        }

        public bool Unlocked
        {
            get => _unlocked;
            set
            {
                // Provider-owned on a provider row: changing it would move unlocked counts and
                // completion, and read as a real unlock to the in-game monitor. Refused here as
                // well as disabled in the view, so no binding or code path can set it.
                if (!CanEditUnlocked && value != _unlocked)
                {
                    OnPropertyChanged(nameof(Unlocked));
                    return;
                }

                if (SetValueAndReturn(ref _unlocked, value))
                {
                    if (!value)
                    {
                        UnlockTime = null;
                    }

                    OnPropertyChanged(nameof(UnlockedState));
                    OnPropertyChanged(nameof(CanEditUnlockTime));
                    NotifyRevealStateChanged();
                }
            }
        }

        /// <summary>
        /// Alias matching the name the shared achievement templates bind, so an editor row and a
        /// display item can be rendered by the same status glyphs.
        /// </summary>
        public bool IsFilteredFromSummaries => IsSummaryFiltered;


        /// <summary>
        /// Sets the filter scope without raising the change that persists it, for seeding a row
        /// from stored data or staging a bulk edit that is written once afterwards.
        /// </summary>
        internal void SetFilterScopeFromSource(AchievementFilterScope scope)
        {
            _filterScopeIsMixed = scope == AchievementFilterScope.Mixed;
            _isFiltered = scope == AchievementFilterScope.All;
            _isSummaryFiltered = scope == AchievementFilterScope.Summary;
            OnPropertyChanged(nameof(IsFiltered));
            OnPropertyChanged(nameof(IsSummaryFiltered));
            OnPropertyChanged(nameof(IsFilteredFromSummaries));
            OnPropertyChanged(nameof(FilterScope));
        }

        /// <summary>
        /// Seeds the unlock state from the achievement being loaded, bypassing the provider-row
        /// guard on the public setter. Only the loader may call this.
        /// </summary>
        internal void SetUnlockedFromSource(bool unlocked)
        {
            _unlocked = unlocked;
            OnPropertyChanged(nameof(Unlocked));
            OnPropertyChanged(nameof(UnlockedState));
            OnPropertyChanged(nameof(CanEditUnlockTime));
            NotifyRevealStateChanged();
        }

        /// <summary>
        /// The three flags the details pane binds, as nullable so a bulk proxy can show a blank
        /// checkbox for a selection that disagrees.
        /// </summary>
        /// <remarks>
        /// A blank is only ever a display state: setting one back to blank is ignored, and picking
        /// either real value applies it even when it matches what the blank was standing in front
        /// of, which is what lets a mixed selection be set to the unchecked pole.
        /// </remarks>
        public bool? UnlockedState
        {
            get => _unlockedIsMixed ? (bool?)null : Unlocked;
            set => ApplyTriState(
                value,
                ref _unlockedIsMixed,
                () => Unlocked,
                next => Unlocked = next,
                nameof(Unlocked),
                nameof(UnlockedState));
        }

        public bool? HiddenState
        {
            get => _hiddenIsMixed ? (bool?)null : Hidden;
            set => ApplyTriState(
                value,
                ref _hiddenIsMixed,
                () => Hidden,
                next => Hidden = next,
                nameof(Hidden),
                nameof(HiddenState));
        }

        public bool? IsGoalState
        {
            get => _isGoalIsMixed ? (bool?)null : IsGoal;
            set => ApplyTriState(
                value,
                ref _isGoalIsMixed,
                () => IsGoal,
                next => IsGoal = next,
                nameof(IsGoal),
                nameof(IsGoalState));
        }

        private void ApplyTriState(
            bool? value,
            ref bool isMixed,
            Func<bool> read,
            Action<bool> apply,
            string valueProperty,
            string stateProperty)
        {
            if (!value.HasValue)
            {
                return;
            }

            var wasMixed = isMixed;
            isMixed = false;
            if (read() != value.Value)
            {
                apply(value.Value);
            }
            else if (wasMixed)
            {
                // The blank was standing in for this value, so the assignment compares as no
                // change; raise anyway or picking it would silently do nothing.
                OnPropertyChanged(valueProperty);
            }

            OnPropertyChanged(stateProperty);
        }

        /// <summary>Seeds the unlock flag, or blanks it for a selection that disagrees.</summary>
        internal void SetUnlockedStateFromSource(bool? unlocked)
        {
            _unlockedIsMixed = !unlocked.HasValue;
            SetUnlockedFromSource(unlocked ?? false);
        }

        /// <summary>Seeds the hidden flag, or blanks it for a selection that disagrees.</summary>
        internal void SetHiddenFromSource(bool? hidden)
        {
            _hiddenIsMixed = !hidden.HasValue;
            _hidden = hidden ?? false;
            OnPropertyChanged(nameof(Hidden));
            OnPropertyChanged(nameof(HiddenState));
            NotifyRevealStateChanged();
        }

        /// <summary>Seeds the goal flag, or blanks it for a selection that disagrees.</summary>
        internal void SetGoalFromSource(bool? isGoal)
        {
            _isGoalIsMixed = !isGoal.HasValue;
            _isGoal = isGoal ?? false;
            OnPropertyChanged(nameof(IsGoal));
            OnPropertyChanged(nameof(IsGoalState));
        }

        public DateTime? UnlockTime
        {
            get => _unlockTime;
            set
            {
                if (SetValueAndReturn(ref _unlockTime, value))
                {
                    OnPropertyChanged(nameof(HasUnlockTime));
                    OnPropertyChanged(nameof(CanEditUnlockTime));
                    OnPropertyChanged(nameof(UnlockTimeLocal));
                    OnPropertyChanged(nameof(UnlockDate));
                    OnPropertyChanged(nameof(UnlockTimeOfDay));

                    if (!_isApplyingPickerUpdate)
                    {
                        InitializeTimePickerFromUnlockTime();
                    }

                    if (value.HasValue && !_unlocked)
                    {
                        _unlocked = true;
                        OnPropertyChanged(nameof(Unlocked));
                        OnPropertyChanged(nameof(CanEditUnlockTime));
                        OnPropertyChanged(nameof(DisplayIcon));
                    }
                }
            }
        }

        public string UnlockedIconPath
        {
            get => _unlockedIconPath;
            set
            {
                if (SetValueAndReturn(ref _unlockedIconPath, value))
                {
                    OnPropertyChanged(nameof(UnlockedIconDisplayText));
                    OnPropertyChanged(nameof(UnlockedPreviewPath));
                    OnPropertyChanged(nameof(LockedPreviewPath));
                    OnPropertyChanged(nameof(DisplayIcon));
                }
            }
        }

        public string LockedIconPath
        {
            get => _lockedIconPath;
            set
            {
                if (SetValueAndReturn(ref _lockedIconPath, value))
                {
                    OnPropertyChanged(nameof(LockedIconDisplayText));
                    OnPropertyChanged(nameof(LockedPreviewPath));
                    OnPropertyChanged(nameof(DisplayIcon));
                }
            }
        }

        private Func<string, string> _iconPathToDisplay;
        private Func<string, string> _iconPathFromDisplay;

        /// <summary>
        /// Managed icons live under the plugin's icon cache; the editor shows them relative to
        /// that root and maps typed text back to a stored path.
        /// </summary>
        public void ConfigureIconPathDisplay(Func<string, string> toDisplay, Func<string, string> fromDisplay)
        {
            _iconPathToDisplay = toDisplay;
            _iconPathFromDisplay = fromDisplay;
            OnPropertyChanged(nameof(UnlockedIconDisplayText));
            OnPropertyChanged(nameof(LockedIconDisplayText));
        }

        public string UnlockedIconDisplayText
        {
            get => ToIconDisplayText(UnlockedIconPath);
            set => UnlockedIconPath = FromIconDisplayText(value);
        }

        public string LockedIconDisplayText
        {
            get => ToIconDisplayText(LockedIconPath);
            set => LockedIconPath = FromIconDisplayText(value);
        }

        private string ToIconDisplayText(string path)
        {
            var normalized = NormalizeText(path);
            return string.IsNullOrWhiteSpace(normalized)
                ? string.Empty
                : _iconPathToDisplay?.Invoke(normalized) ?? normalized;
        }

        private string FromIconDisplayText(string text)
        {
            var normalized = NormalizeText(text);
            return string.IsNullOrWhiteSpace(normalized)
                ? null
                : _iconPathFromDisplay?.Invoke(normalized) ?? normalized;
        }

        public string PointsText
        {
            get => _pointsText;
            set => SetValue(ref _pointsText, value);
        }

        public string TrophyType
        {
            get => _trophyType;
            set => SetValue(ref _trophyType, value);
        }

        public bool Hidden
        {
            get => _hidden;
            set
            {
                if (SetValueAndReturn(ref _hidden, value))
                {
                    OnPropertyChanged(nameof(HiddenState));
                    NotifyRevealStateChanged();
                }
            }
        }

        public string Rarity
        {
            get => _rarity;
            set
            {
                if (SetValueAndReturn(ref _rarity, value))
                {
                    OnPropertyChanged(nameof(RarityTier));
                }
            }
        }

        public string GlobalPercentUnlockedText
        {
            get => _globalPercentUnlockedText;
            set => SetValue(ref _globalPercentUnlockedText, value);
        }

        /// <summary>
        /// The single rarity editor's text. A number (optionally ending in %) sets the global
        /// unlock percent and derives the tier from the rarity thresholds; a tier name or its
        /// display text sets the tier and clears the percent. Anything else is flagged invalid.
        /// </summary>
        public string RarityInput
        {
            get => _rarityInput;
            set
            {
                // Provider-owned: rarity and its unlock percentage are derived from what the
                // provider reports, and the stored-rarity guard cannot tell a deliberate Common
                // from "never filled in". Refused here as well as disabled in the view, so no
                // binding or code path can set it on a provider row.
                if (!CanEditRarity && !string.Equals(_rarityInput, value, StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(RarityInput));
                    return;
                }

                if (SetValueAndReturn(ref _rarityInput, value))
                {
                    ApplyRarityInput(value);
                }
            }
        }

        private void ApplyRarityInput(string value)
        {
            var normalized = NormalizeText(value);
            _rarityInputInvalid = false;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                Rarity = null;
                GlobalPercentUnlockedText = null;
                return;
            }

            var percentText = normalized.TrimEnd('%').Trim();
            if (double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) &&
                percent >= 0 &&
                percent <= 100)
            {
                GlobalPercentUnlockedText = percentText;
                Rarity = PercentRarityHelper.GetRarityTier(percent).ToString();
                return;
            }

            var tier = TryMatchRarityTier(normalized);
            if (tier.HasValue)
            {
                Rarity = tier.Value.ToString();
                GlobalPercentUnlockedText = null;
                return;
            }

            _rarityInputInvalid = true;
            Rarity = null;
            GlobalPercentUnlockedText = null;
        }

        private static RarityTier? TryMatchRarityTier(string text)
        {
            if (RarityTierExtensions.TryParse(text, out var parsed))
            {
                return parsed;
            }

            foreach (RarityTier tier in Enum.GetValues(typeof(RarityTier)))
            {
                if (string.Equals(tier.ToDisplayText(), text, StringComparison.OrdinalIgnoreCase))
                {
                    return tier;
                }
            }

            return null;
        }

        private void SyncRarityInputFromState()
        {
            _rarityInputInvalid = false;
            string input;
            if (!string.IsNullOrWhiteSpace(GlobalPercentUnlockedText))
            {
                input = GlobalPercentUnlockedText.TrimEnd('%') + "%";
            }
            else if (RarityTierExtensions.TryParse(Rarity, out var tier))
            {
                input = tier.ToDisplayText();
            }
            else
            {
                input = Rarity;
            }

            SetValue(ref _rarityInput, input, nameof(RarityInput));
        }

        public string ProgressNumText
        {
            get => _progressNumText;
            set => SetValue(ref _progressNumText, value);
        }

        public string ProgressDenomText
        {
            get => _progressDenomText;
            set => SetValue(ref _progressDenomText, value);
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            set
            {
                if (SetValueAndReturn(ref _validationMessage, value))
                {
                    OnPropertyChanged(nameof(HasValidationMessage));
                }
            }
        }

        public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

        public string NormalizedId => CustomAchievementProjectionService.NormalizeId(Id);

        public bool HasUnlockTime
        {
            get => UnlockTime.HasValue;
            set
            {
                if (value)
                {
                    if (!Unlocked)
                    {
                        Unlocked = true;
                    }

                    if (!UnlockTime.HasValue)
                    {
                        UnlockTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    UnlockTime = null;
                }
            }
        }

        public bool CanEditUnlockTime => Unlocked && HasUnlockTime;

        public DateTime? UnlockTimeLocal
        {
            get => UnlockTime?.ToLocalTime();
            set
            {
                if (value.HasValue)
                {
                    UnlockTime = value.Value.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Local).ToUniversalTime()
                        : value.Value.ToUniversalTime();
                }
                else
                {
                    UnlockTime = null;
                }
            }
        }

        public DateTime? UnlockDate
        {
            get => UnlockTimeLocal?.Date;
            set
            {
                if (value.HasValue)
                {
                    var existingTime = UnlockTimeLocal?.TimeOfDay ?? TimeSpan.FromHours(12);
                    UnlockTimeLocal = value.Value.Date + existingTime;
                }
                else if (!Unlocked)
                {
                    UnlockTime = null;
                }
            }
        }

        public TimeSpan? UnlockTimeOfDay
        {
            get => UnlockTimeLocal?.TimeOfDay;
            set
            {
                if (value.HasValue && UnlockDate.HasValue)
                {
                    UnlockTimeLocal = UnlockDate.Value.Date + value.Value;
                }
            }
        }

        public IEnumerable<string> AvailableTimeModes => TimeModeDisplayNames;

        public string TimeText
        {
            get => _timeText;
            set
            {
                if (_timeText != value)
                {
                    _timeText = value;
                    ValidateAndApplyTimeText();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsValidTime));
                }
            }
        }

        public bool IsValidTime => _isValidTime;

        public string SelectedTimeModeText
        {
            get => _selectedTimeMode switch
            {
                TimeMode.TwentyFourHour => "24hr",
                TimeMode.PM => "PM",
                _ => "AM"
            };
            set
            {
                var newMode = value switch
                {
                    "24hr" => TimeMode.TwentyFourHour,
                    "PM" => TimeMode.PM,
                    _ => TimeMode.AM
                };

                if (_selectedTimeMode == newMode)
                {
                    return;
                }

                var previousMode = _selectedTimeMode;
                _selectedTimeMode = newMode;
                if (newMode == TimeMode.TwentyFourHour)
                {
                    _selectedHour = Convert12To24Hour(_selectedHour, previousMode);
                }
                else if (previousMode == TimeMode.TwentyFourHour)
                {
                    if (_selectedHour == 0)
                    {
                        _selectedHour = 12;
                    }
                    else if (_selectedHour > 12)
                    {
                        _selectedHour -= 12;
                    }
                }

                SetTimeTextFromSelection();
                OnPropertyChanged(nameof(SelectedTimeModeText));
                UpdateUnlockTimeFromPicker();
            }
        }

        public string DisplayIcon
        {
            get
            {
                // Hidden is tested first so the more spoiler-sensitive state wins when a row
                // is both hidden and locked-masked, matching AchievementDisplayItem.
                if (IsIconHidden)
                {
                    return AchievementIconResolver.GetHiddenFallbackIcon();
                }

                if (IsLockedIconHidden)
                {
                    return AchievementIconResolver.GetLockedFallbackIcon();
                }

                return Unlocked
                    ? AchievementIconResolver.GetUnlockedDisplayIcon(UnlockedIconPath)
                    : AchievementIconResolver.GetLockedDisplayIcon(UnlockedIconPath, LockedIconPath);
            }
        }

        public string UnlockedPreviewPath => AchievementIconResolver.GetUnlockedDisplayIcon(UnlockedIconPath);

        public string LockedPreviewPath => AchievementIconResolver.GetLockedDisplayIcon(UnlockedIconPath, LockedIconPath);

        public RarityTier RarityTier =>
            RarityTierExtensions.TryParse(Rarity, out var rarity) ? rarity : RarityTier.Common;

        public bool IsBlank =>
            string.IsNullOrWhiteSpace(Id) &&
            string.IsNullOrWhiteSpace(DisplayName) &&
            string.IsNullOrWhiteSpace(Description);

        public bool HasChanges => IsNew || !string.Equals(BuildSignature(), _baselineSignature, StringComparison.Ordinal);

        public string StateSignature => BuildSignature();

        public static AchievementEditorRow CreateNew(int index)
        {
            var row = new AchievementEditorRow
            {
                DisplayName = "New Achievement " + Math.Max(1, index).ToString(CultureInfo.InvariantCulture),
                IsNew = true
            };
            row.SyncRarityInputFromState();
            row.CaptureBaseline();
            return row;
        }

        /// <summary>
        /// Builds an editor row from a hydrated achievement, provider-supplied or custom. The
        /// merged editor lists both kinds in one grid, so they must be the same row type; the
        /// difference is carried by <see cref="IsProviderRow"/>, which gates the fields a provider
        /// achievement does not own.
        /// </summary>
        /// <remarks>
        /// The values come from hydrated data, so any existing override is already applied and the
        /// row shows the effective value rather than the provider's original.
        /// </remarks>
        public static AchievementEditorRow FromAchievementDetail(AchievementDetail achievement)
        {
            if (achievement == null)
            {
                return null;
            }

            var row = new AchievementEditorRow();
            row.SuppressNotifications = true;
            row.IsProviderRow = !achievement.IsCustom;
            row.Id = achievement.IsCustom &&
                     CustomAchievementProjectionService.TryGetCustomId(achievement.ApiName, out var customId)
                ? customId
                : null;
            row.DisplayName = achievement.DisplayName;
            row.Description = achievement.Description;
            // Written straight to the field: the public setter refuses provider rows, which is the
            // point, but loading the provider's own value must not be refused.
            row.SetUnlockedFromSource(achievement.Unlocked);
            row.UnlockTime = achievement.UnlockTimeUtc;
            row.UnlockedIconPath = achievement.UnlockedIconPath;
            row.LockedIconPath = achievement.LockedIconPath;
            row.PointsText = FormatInt(achievement.Points);
            row.TrophyType = achievement.TrophyType;
            row.Hidden = achievement.Hidden;
            row.Rarity = achievement.Rarity.ToString();
            row.GlobalPercentUnlockedText = FormatDouble(achievement.GlobalPercentUnlocked);
            row.SyncRarityInputFromState();
            row.ProgressNumText = FormatInt(achievement.ProgressNum);
            row.ProgressDenomText = FormatInt(achievement.ProgressDenom);
            row.CategoryLabel = achievement.Category;
            row.CategoryTypeValue = achievement.CategoryType;
            row.IsCapstone = achievement.IsCapstone;
            row.AchievementNote = achievement.AchievementNote;
            row.IsGoal = achievement.IsGoal;
            row.IsFiltered = achievement.IsFiltered;
            row.IsSummaryFiltered = achievement.IsFilteredFromSummaries;
            row.ValidationMessage = null;
            row.SuppressNotifications = false;

            row.OriginalApiName = achievement.ApiName;
            // The provider's own label, which Category holds only until an override replaces it.
            row.ProviderCategoryLabel = achievement.ProviderCategory ?? achievement.Category;
            row.ProviderCategoryTypeValue = achievement.CategoryType;
            row.IsNew = false;
            row.CaptureBaseline();
            return row;
        }

        public static AchievementEditorRow FromDefinition(CustomAchievementDefinition definition)
        {
            var row = new AchievementEditorRow();
            row.ApplyDefinition(definition, preserveOriginalId: false);
            row.OriginalApiName = CustomAchievementProjectionService.BuildApiName(definition?.Id);
            row.IsNew = false;
            row.CaptureBaseline();
            return row;
        }

        public void ApplyDefinition(CustomAchievementDefinition definition, bool preserveOriginalId)
        {
            if (definition == null)
            {
                return;
            }

            SuppressNotifications = true;
            Id = definition.Id;
            DisplayName = definition.DisplayName;
            Description = definition.Description;
            Unlocked = definition.Unlocked;
            UnlockTime = definition.UnlockTimeUtc;
            UnlockedIconPath = definition.UnlockedIconPath;
            LockedIconPath = definition.LockedIconPath;
            PointsText = FormatInt(definition.Points);
            TrophyType = definition.TrophyType;
            Hidden = definition.Hidden;
            Rarity = definition.Rarity;
            GlobalPercentUnlockedText = FormatDouble(definition.GlobalPercentUnlocked);
            SyncRarityInputFromState();
            ProgressNumText = FormatInt(definition.ProgressNum);
            ProgressDenomText = FormatInt(definition.ProgressDenom);
            ValidationMessage = null;
            SuppressNotifications = false;

            if (!preserveOriginalId)
            {
                OriginalApiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
            }

            OnPropertyChanged(string.Empty);
        }

        public void MarkAsNewImport()
        {
            IsNew = true;
        }

        /// <summary>
        /// Adopts what a save persisted for this row: the generated ID when it had none, the
        /// materialized icon paths, and its ApiName. Text the user is still typing is left alone.
        /// </summary>
        public void CommitSaved(CustomAchievementDefinition definition)
        {
            if (definition == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(NormalizedId))
            {
                Id = definition.Id;
            }

            UnlockedIconPath = definition.UnlockedIconPath;
            LockedIconPath = definition.LockedIconPath;
            OriginalApiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
            OnPropertyChanged(nameof(CanEditAssignments));
            CaptureBaseline();
        }

        public AchievementEditorRow CloneForDuplicate()
        {
            var definition = ToDefinition(new HashSet<string>(StringComparer.OrdinalIgnoreCase), out _);
            definition.Id = null;
            definition.DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName)
                ? "Copy"
                : definition.DisplayName + " Copy";
            var row = FromDefinition(definition);
            row.OriginalApiName = null;
            row.IsNew = true;
            row.Id = null;
            row.CaptureBaseline();
            return row;
        }

        public CustomAchievementDefinition ToDefinition(ISet<string> usedIds, out List<string> errors)
        {
            errors = new List<string>();
            usedIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var displayName = NormalizeText(DisplayName);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                errors.Add("Title is required.");
                return null;
            }

            var id = CustomAchievementProjectionService.NormalizeId(Id);
            if (string.IsNullOrWhiteSpace(id))
            {
                id = CustomAchievementProjectionService.GenerateId(displayName, usedIds);
            }

            if (!usedIds.Add(id))
            {
                errors.Add($"Duplicate ID '{id}'.");
            }

            var definition = new CustomAchievementDefinition
            {
                Id = id,
                DisplayName = displayName,
                Description = NormalizeText(Description),
                Unlocked = Unlocked,
                UnlockedIconPath = NormalizeText(UnlockedIconPath),
                LockedIconPath = NormalizeText(LockedIconPath),
                TrophyType = NormalizeText(TrophyType),
                Hidden = Hidden,
                Rarity = string.IsNullOrWhiteSpace(Rarity) ? "Common" : Rarity.Trim()
            };

            if (CanEditUnlockTime && !IsValidTime)
            {
                errors.Add("Unlock time is invalid.");
            }

            definition.UnlockTimeUtc = definition.Unlocked
                ? NormalizeUtc(UnlockTime)
                : null;
            if (definition.UnlockTimeUtc.HasValue)
            {
                definition.Unlocked = true;
            }

            definition.Points = ParseNullableInt(PointsText, errors, "Points", allowZero: true);
            definition.GlobalPercentUnlocked = ParseNullablePercent(GlobalPercentUnlockedText, errors);
            definition.ProgressNum = ParseNullableInt(ProgressNumText, errors, "Progress", allowZero: true);
            definition.ProgressDenom = ParseNullableInt(ProgressDenomText, errors, "Progress total", allowZero: false);

            if (!string.IsNullOrWhiteSpace(TrophyType) && NormalizeTrophyType(TrophyType) == null)
            {
                errors.Add("Trophy type must be bronze, silver, gold, or platinum.");
            }
            else
            {
                definition.TrophyType = NormalizeTrophyType(TrophyType);
            }

            if (_rarityInputInvalid ||
                (!string.IsNullOrWhiteSpace(Rarity) && !RarityTierExtensions.TryParse(Rarity, out _)))
            {
                errors.Add("Rarity must be a percent from 0 to 100, or Common, Uncommon, Rare, or Ultra Rare.");
            }

            if (definition.ProgressNum.HasValue &&
                definition.ProgressDenom.HasValue &&
                definition.ProgressNum.Value > definition.ProgressDenom.Value)
            {
                errors.Add("Progress cannot be greater than progress total.");
            }

            return definition;
        }

        public void CaptureBaseline()
        {
            _baselineSignature = BuildSignature();
            IsNew = false;
            OnPropertyChanged(nameof(HasChanges));
        }

        private string BuildSignature()
        {
            return JsonConvert.SerializeObject(new
            {
                Id,
                DisplayName,
                Description,
                Unlocked,
                UnlockTimeUtc = FormatDate(UnlockTime),
                UnlockedIconPath,
                LockedIconPath,
                PointsText,
                TrophyType,
                Hidden,
                Rarity,
                GlobalPercentUnlockedText,
                RarityInput,
                ProgressNumText,
                ProgressDenomText
            });
        }

        private void ValidateAndApplyTimeText()
        {
            if (_isUpdatingFromText)
            {
                return;
            }

            if (!TryParseTimeText(_timeText, _selectedTimeMode, out var parsedHour, out var parsedMinute))
            {
                _isValidTime = false;
                return;
            }

            _selectedHour = parsedHour;
            _selectedMinute = parsedMinute;
            _isValidTime = true;
            UpdateUnlockTimeFromPicker();
        }

        private static bool TryParseTimeText(string input, TimeMode mode, out int hour, out int minute)
        {
            hour = 0;
            minute = 0;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var parts = input.Trim().Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0].Trim(), out hour) ||
                !int.TryParse(parts[1].Trim(), out minute))
            {
                return false;
            }

            if (minute < 0 || minute > 59)
            {
                return false;
            }

            var minHour = mode == TimeMode.TwentyFourHour ? 0 : 1;
            var maxHour = mode == TimeMode.TwentyFourHour ? 23 : 12;
            return hour >= minHour && hour <= maxHour;
        }

        private static string FormatTimeText(int hour, int minute, TimeMode mode)
        {
            return mode == TimeMode.TwentyFourHour
                ? $"{hour:D2}:{minute:D2}"
                : $"{hour}:{minute:D2}";
        }

        private void SetTimeTextFromSelection()
        {
            _isUpdatingFromText = true;
            try
            {
                _timeText = FormatTimeText(_selectedHour, _selectedMinute, _selectedTimeMode);
                _isValidTime = true;
                OnPropertyChanged(nameof(TimeText));
                OnPropertyChanged(nameof(IsValidTime));
            }
            finally
            {
                _isUpdatingFromText = false;
            }
        }

        private void UpdateUnlockTimeFromPicker()
        {
            if (!UnlockDate.HasValue || !_isValidTime)
            {
                return;
            }

            var hour24 = _selectedTimeMode == TimeMode.TwentyFourHour
                ? _selectedHour
                : Convert12To24Hour(_selectedHour, _selectedTimeMode);

            _isApplyingPickerUpdate = true;
            try
            {
                UnlockTimeLocal = UnlockDate.Value.Date + new TimeSpan(hour24, _selectedMinute, 0);
            }
            finally
            {
                _isApplyingPickerUpdate = false;
            }
        }

        private static int Convert12To24Hour(int hour12, TimeMode mode)
        {
            if (mode == TimeMode.TwentyFourHour)
            {
                return hour12;
            }

            if (mode == TimeMode.AM)
            {
                return hour12 == 12 ? 0 : hour12;
            }

            return hour12 == 12 ? 12 : hour12 + 12;
        }

        private static void Convert24To12Hour(int hour24, out int hour12, out TimeMode mode)
        {
            if (hour24 == 0)
            {
                hour12 = 12;
                mode = TimeMode.AM;
            }
            else if (hour24 < 12)
            {
                hour12 = hour24;
                mode = TimeMode.AM;
            }
            else if (hour24 == 12)
            {
                hour12 = 12;
                mode = TimeMode.PM;
            }
            else
            {
                hour12 = hour24 - 12;
                mode = TimeMode.PM;
            }
        }

        /// <summary>
        /// Seeds the time editor from the stored timestamp. The clock mode follows the formatting
        /// culture, so a user whose language writes 18:00 is not handed an AM/PM picker; the mode
        /// dropdown still switches it per row.
        /// </summary>
        private void InitializeTimePickerFromUnlockTime()
        {
            var prefers24Hour = AchievementEditorFieldRules.PrefersTwentyFourHourClock(
                Common.FormattingCulture.Current);

            if (UnlockTimeLocal.HasValue)
            {
                var time = UnlockTimeLocal.Value.TimeOfDay;
                if (prefers24Hour)
                {
                    _selectedHour = time.Hours;
                    _selectedTimeMode = TimeMode.TwentyFourHour;
                }
                else
                {
                    Convert24To12Hour(time.Hours, out _selectedHour, out _selectedTimeMode);
                }

                _selectedMinute = time.Minutes;
            }
            else
            {
                _selectedHour = 12;
                _selectedMinute = 0;
                _selectedTimeMode = prefers24Hour ? TimeMode.TwentyFourHour : TimeMode.PM;
            }

            SetTimeTextFromSelection();
            OnPropertyChanged(nameof(SelectedTimeModeText));
        }

        private static DateTime? NormalizeUtc(DateTime? value)
        {
            if (!value.HasValue || value.Value == DateTime.MinValue)
            {
                return null;
            }

            var date = value.Value;
            return date.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                : date.ToUniversalTime();
        }

        private static int? ParseNullableInt(
            string value,
            ICollection<string> errors,
            string label,
            bool allowZero)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= (allowZero ? 0 : 1))
            {
                return parsed;
            }

            errors.Add(label + (allowZero
                ? " must be a non-negative integer."
                : " must be a positive integer."));
            return null;
        }

        private static double? ParseNullablePercent(string value, ICollection<string> errors)
        {
            var normalized = NormalizeText(value)?.TrimEnd('%');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= 0 &&
                parsed <= 100)
            {
                return parsed;
            }

            errors.Add("Percent must be between 0 and 100.");
            return null;
        }

        private static string NormalizeTrophyType(string value)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            switch (normalized.ToLowerInvariant())
            {
                case "bronze":
                case "silver":
                case "gold":
                case "platinum":
                    return normalized.ToLowerInvariant();
                default:
                    return null;
            }
        }

        private static string FormatDate(DateTime? value)
        {
            return value.HasValue ? value.Value.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) : null;
        }

        private static string FormatInt(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : null;
        }

        private static string FormatDouble(double? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : null;
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }

    public sealed class CustomAchievementSelectionOption
    {
        public CustomAchievementSelectionOption(string value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public string Value { get; }

        public string DisplayName { get; }
    }
}
